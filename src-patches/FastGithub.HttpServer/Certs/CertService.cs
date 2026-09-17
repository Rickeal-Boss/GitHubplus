using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace FastGithub.HttpServer.Certs
{
    /// <summary>
    /// 证书服务
    /// </summary>
    sealed class CertService
    {
        // [PATCH] 原为相对路径 "cacert"，解析依赖进程的当前工作目录。
        // 上游 FastGithub/Program.cs 确实会把 Environment.CurrentDirectory 设为 exe 所在目录，
        // 但那行被包在 if (string.IsNullOrEmpty(contentRoot) == false) 里——
        // Environment.ProcessPath 为空时会静默跳过，此时本路径会落到宿主的默认工作目录
        // （Windows 服务模式下通常是 C:\Windows\System32）。
        // 显式基于程序所在目录，彻底消除对宿主 CWD 的隐式依赖。
        private static readonly string CACERT_PATH = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "cacert");
        private readonly IMemoryCache serverCertCache;
        private readonly IEnumerable<ICaCertInstaller> certInstallers;
        private readonly ILogger<CertService> logger;
        private X509Certificate2? caCert;

        // [PATCH] 保护 caCert 的首次初始化（单例 + 并发 TLS 握手）
        private readonly object caCertLock = new object();

        // [PATCH] 按域名的证书签发闸门：保证同一域名同一时刻只有一个线程在签发证书。
        // 与 serverCertCache 的生命周期协同：证书被淘汰时同步移除对应闸门，避免无界增长。
        private readonly ConcurrentDictionary<string, SemaphoreSlim> certGates = new();


        /// <summary>
        /// 获取证书文件路径
        /// </summary>
        public string CaCerFilePath { get; } = OperatingSystem.IsLinux() ? $"{CACERT_PATH}/fastgithub.crt" : $"{CACERT_PATH}/fastgithub.cer";

        /// <summary>
        /// 获取私钥文件路径
        /// </summary>
        public string CaKeyFilePath { get; } = $"{CACERT_PATH}/fastgithub.key";

        /// <summary>
        /// 证书服务
        /// </summary>
        /// <param name="serverCertCache"></param>
        /// <param name="certInstallers"></param>
        /// <param name="logger"></param>
        public CertService(
            IMemoryCache serverCertCache,
            IEnumerable<ICaCertInstaller> certInstallers,
            ILogger<CertService> logger)
        {
            this.serverCertCache = serverCertCache;
            this.certInstallers = certInstallers;
            this.logger = logger;
            Directory.CreateDirectory(CACERT_PATH);
        }

        /// <summary>
        /// 生成CA证书
        /// </summary> 
        public bool CreateCaCertIfNotExists()
        {
            if (File.Exists(this.CaCerFilePath) && File.Exists(this.CaKeyFilePath))
            {
                // [PATCH] 到期轮换：上游只判「两个文件是否都在」，完全不看有效期。
                // 而 CertGenerator.CreateEndCertificate 会把终端证书的 notAfter 夹到签发者 CA 的
                // NotAfter —— CA 一过期，签出的证书立即不受信（硬失败，UI 无任何提示）。
                // 剩余有效期不足 30 天时重新签发，避免撞上硬失败。
                if (IsCaCertStillValid())
                {
                    return false;
                }
                this.logger.LogWarning("本地 CA 证书已过期或即将过期（剩余有效期不足 30 天），将重新生成；请重新启动加速以信任新的 CA 证书。");
            }
            else
            {
                // [PATCH] 原实现的「文件缺失」分支完全静默：任何误删 / 清理 / 杀软隔离 cacert 的场景
                // 都会在零日志线索下把 CA 换成新的（并写入系统信任库）。不使用 Windows 信任库的客户端
                //（如 http.sslBackend=openssl 的 git、手工 pin 过本机 CA 的工具）会突然全线 TLS 失败却无从排查。
                // 这里补一条 Warning，让「CA 被换新」这件事在日志里可见。
                this.logger.LogWarning("未找到本地 CA 证书文件（可能被清理、移动或杀软隔离），将重新生成；请重新启动加速以信任新的 CA 证书。");
            }

            File.Delete(this.CaCerFilePath);
            File.Delete(this.CaKeyFilePath);

            var notBefore = DateTimeOffset.Now.AddDays(-1);
            // [PATCH] 缩短 CA 有效期至 5 年（原为 10 年）：
            // 私钥明文存于 cacert/fastgithub.key 且无名称约束，有效期越长风险窗口越大。
            var notAfter = DateTimeOffset.Now.AddYears(5);

            var subjectName = new X500DistinguishedName($"CN={nameof(FastGithub)}");
            this.caCert = CertGenerator.CreateCACertificate(subjectName, notBefore, notAfter);

            var privateKeyPem = this.caCert.GetRSAPrivateKey()?.ExportRSAPrivateKeyPem();
            File.WriteAllText(this.CaKeyFilePath, new string(privateKeyPem), Encoding.ASCII);

            var certPem = this.caCert.ExportCertificatePem();
            File.WriteAllText(this.CaCerFilePath, new string(certPem), Encoding.ASCII);

            // [PATCH] 记录新 CA 的指纹与有效期：便于把「系统信任库里的证书」与「磁盘上的证书」对齐核对，
            // 用于定位「CA 被静默换新后，旧证书客户端全线失败」这类问题。
            this.logger.LogWarning(
                "已生成本地 CA 证书（Subject={Subject}，指纹={Thumbprint}，有效期 {NotBefore:O} ~ {NotAfter:O}）：证书 {Cer}，私钥 {Key}。",
                subjectName.Name, this.caCert.Thumbprint, this.caCert.NotBefore, this.caCert.NotAfter,
                this.CaCerFilePath, this.CaKeyFilePath);

            return true;
        }

        /// <summary>
        /// [PATCH] 判断现有 CA 证书是否仍可继续使用（剩余有效期 &gt; 30 天）
        /// </summary>
        /// <returns>仍可使用时返回 true；过期、临近过期或文件损坏时返回 false</returns>
        private bool IsCaCertStillValid()
        {
            try
            {
                using var cert = new X509Certificate2(this.CaCerFilePath);

                // 有效期：剩余不足 30 天即重新签发
                if (cert.NotAfter <= DateTime.Now.AddDays(30))
                {
                    return false;
                }

                // 配对检查：证书与私钥必须是同一对。
                // 若用户只替换了其中一个（手动导入公司 CA、旧包升级残留等），
                // X509Certificate2.CopyWithPrivateKey 不会报错，真正的失败会延迟到
                // 每次 TLS 握手签名时才抛 CryptographicException —— 表现为 HTTPS 全线不可用。
                using var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(this.CaKeyFilePath));
                using var certPublicKey = cert.GetRSAPublicKey();
                if (certPublicKey == null)
                {
                    return false;
                }

                return certPublicKey.ExportParameters(false).Modulus
                    .SequenceEqual(rsa.ExportParameters(false).Modulus);
            }
            catch (Exception)
            {
                // 文件损坏或无法解析时视为不可用，走重新生成流程
                return false;
            }
        }

        /// <summary>
        /// 安装和信任CA证书
        /// </summary> 
        public void InstallAndTrustCaCert()
        {
            var installer = this.certInstallers.FirstOrDefault(item => item.IsSupported());
            if (installer != null)
            {
                installer.Install(this.CaCerFilePath);
            }
            else
            {
                this.logger.LogWarning($"请根据你的系统平台手动安装和信任CA证书{this.CaCerFilePath}");
            }

            // [PATCH] 不再关闭 git 全局证书校验
            // 原实现会在每次启动时执行「git config --global http.sslverify false」，
            // 该配置全局、持久、程序退出也不恢复；对应方法已从本文件删除。
            // 新实现：CA 已在系统信任库，改让 git 读取系统证书存储即可正常校验。
            ConfigureGitToUseSystemCertStore();
        }

        /// <summary>
        /// [PATCH] 让 git 能正常校验证书，而不是关闭校验。
        /// 首选切到 Schannel 读 Windows 证书存储；若用户已显式改用其它后端（如 openssl），
        /// 则退一步把本机 CA 文件路径交给 git。无论如何都不再写 http.sslVerify=false。
        /// </summary>
        private void ConfigureGitToUseSystemCertStore()
        {
            // 仅 Windows 有 Schannel 后端可切；其它平台沿用系统默认校验行为
            if (OperatingSystem.IsWindows() == false)
            {
                return;
            }

            // 清理旧版本遗留的全局关闭配置（幂等：未设置时 git 返回非 0，忽略退出码即可）
            RunGitConfig("--unset http.sslVerify");

            var sslBackend = RunGitConfigGet("http.sslBackend");
            if (string.IsNullOrEmpty(sslBackend))
            {
                // 仅当用户从未显式配置过 sslBackend 时才切换，
                // 避免每次勾选站点重启引擎都重刷用户的既有选择。
                // 注：Git for Windows 的 http.schannelCheckRevoke 默认值已是 best-effort，
                //     自签 CA 缺少 CRL 分发点也不会报错，因此无需额外设置。
                if (RunGitConfig("http.sslBackend schannel") == false)
                {
                    this.logger.LogWarning(
                        "写入 git 全局配置 http.sslBackend=schannel 失败，git 可能不信任本机 CA，https 操作会失败。" +
                        "可手动执行 git config --global http.sslBackend schannel 后重试。");
                }
                return;
            }

            if (string.Equals(sslBackend, "schannel", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // [PATCH] 回归防线：openssl 等后端不读 Windows 证书存储，而旧版依赖的
            // http.sslVerify=false 已被移除 —— 若不处理，这类用户会直接 clone 失败。
            // 用户未指定自己的 CA 包时才指向本机 CA；已指定则只告警，绝不覆盖其配置。
            var sslCAInfo = RunGitConfigGet("http.sslCAInfo");
            if (string.IsNullOrEmpty(sslCAInfo))
            {
                // git config 值里的反斜杠会被当作转义，统一换成正斜杠
                var caPath = Path.GetFullPath(this.CaCerFilePath).Replace('\\', '/');
                RunGitConfig($"http.sslCAInfo \"{caPath}\"");
                return;
            }

            this.logger.LogWarning(
                $"检测到 git 使用了 http.sslBackend={sslBackend} 且已配置 http.sslCAInfo，" +
                "该组合不会读取 Windows 证书存储，git 可能不信任本机 CA 导致操作失败。" +
                "可自行执行 git config --global --unset http.sslBackend 切换为 schannel，" +
                $"或把 {this.CaCerFilePath} 追加到你现有的 CA 包中。");
        }

        /// <summary>
        /// [PATCH] 执行 git config --global（修复原实现的两个 bug：
        /// UseShellExecute=true 拿不到退出码、且未等待进程结束）
        /// </summary>
        /// <param name="arguments">传给 git config --global 的参数</param>
        /// <returns>是否执行成功</returns>
        private static bool RunGitConfig(string arguments)
        {
            // [PATCH] 按名字启动 git 存在程序目录劫持风险：UseShellExecute=false 时，
            // CreateProcess 对无路径命令的搜索顺序是「应用程序目录 → 当前目录 → System32 → PATH」。
            // 引擎 Program.Main 会把 CWD 设为 exe 所在目录（通常解压在用户可写位置，而本程序以管理员运行），
            // 若按名字启动 git，会在「当前目录」这一步命中程序目录里被投放的 git.exe。
            // 这里双保险：cmd.exe 用绝对路径（消掉「应用程序目录」= exe 目录这一项），
            // 再把 WorkingDirectory 设为 SystemDirectory（消掉「当前目录」= exe 目录这一项），
            // 使 git 只可能从 System32 / Windows 系统目录 / PATH 中被找到，不再查用户可写目录。
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = $"/c git config --global {arguments}",
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                // [PATCH] 原实现只等退出、不看 ExitCode：git 因参数非法等失败时会被当成成功，
                // 调用方据此掩盖了错误（同文件 RunGitConfigGet 已正确判断退出码）。
                if (process == null || process.WaitForExit(5000) == false)
                {
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// [PATCH] 读取 git 全局配置的指定项
        /// </summary>
        /// <param name="key">配置项名称，如 http.sslBackend</param>
        /// <returns>配置值；未设置或执行失败时返回 null</returns>
        private static string? RunGitConfigGet(string key)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    Arguments = $"/c git config --global --get {key}",
                    WorkingDirectory = Environment.SystemDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });

                if (process == null)
                {
                    return null;
                }

                var value = process.StandardOutput.ReadToEnd();
                if (process.WaitForExit(5000) == false || process.ExitCode != 0)
                {
                    return null;
                }

                return value.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 获取颁发给指定域名的证书
        /// </summary>
        /// <param name="domain"></param> 
        /// <returns></returns>
        public X509Certificate2 GetOrCreateServerCert(string? domain)
        {
            if (this.caCert == null)
            {
                // [PATCH] CertService 是单例，而 TLS 握手是多线程并发的。
                // 原实现无同步：并发首次握手会各建一份 X509Certificate2，多余的那份
                // 被直接丢弃且未 Dispose（非托管证书句柄泄漏）。双检锁解决。
                lock (this.caCertLock)
                {
                    if (this.caCert == null)
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportFromPem(File.ReadAllText(this.CaKeyFilePath));
                        this.caCert = new X509Certificate2(this.CaCerFilePath).CopyWithPrivateKey(rsa);
                    }
                }
            }

            var key = $"{nameof(CertService)}:{domain}";

            // [PATCH] 无锁快路径：命中缓存直接返回，避免每次 TLS 握手都去取闸门。
            if (this.serverCertCache.TryGetValue(key, out var cached) && cached is X509Certificate2 cachedCert)
            {
                return cachedCert;
            }

            // [PATCH] 按域名串行签发。IMemoryCache.GetOrCreate 不是原子的：
            // 并发首次握手会各自进入工厂方法，同一域名被签发多份证书，
            // 多余的那份被丢弃且从未 Dispose（非托管句柄泄漏），同时白白浪费 CPU。
            // 注意：Kestrel 在同步回调里调用本方法，取闸门必须用同步阻塞（Wait），
            // 不能把本方法改成 async（否则会改变返回类型、破坏调用方）。
            var gate = this.certGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                // 闸门内二次检查：可能已有并发线程完成签发并写入缓存，此处会直接命中
                var endCert = this.serverCertCache.GetOrCreate(key, GetOrCreateCert);
                return endCert!;
            }
            finally
            {
                gate.Release();
            }

            // 生成域名的1年证书
            X509Certificate2 GetOrCreateCert(ICacheEntry entry)
            {
                var notBefore = DateTimeOffset.Now.AddDays(-1);
                var notAfter = DateTimeOffset.Now.AddYears(1);
                entry.SetAbsoluteExpiration(notAfter);

                // [PATCH] 证书缓存必须有容量计费与淘汰回调：
                // 1、SetSize 传入 1，让 AddMemoryCache 的 SizeLimit 能据此做 LRU 淘汰；
                // 2、X509Certificate2 是非托管句柄，被淘汰时必须 Dispose，否则句柄泄漏。
                //    原实现两项都缺，访问大量不同子域（如 *.cloudfront.net）会无界增长直至 OOM。
                entry.SetSize(1);
                entry.RegisterPostEvictionCallback((cacheKey, value, reason, state) =>
                {
                    (value as X509Certificate2)?.Dispose();
                    // [PATCH] 证书被淘汰时同步清理其签发闸门，避免 certGates 随访问过的域名无界增长。
                    // 回调形参 cacheKey 的类型被推断为 object（RegisterPostEvictionCallback 签名如此），
                    // 故按 string 模式匹配后再用；命名 cacheKey 而非 key，避免遮蔽外层那个 string key。
                    if (cacheKey is string domainKey)
                    {
                        this.certGates.TryRemove(domainKey, out _);
                    }
                });

                var extraDomains = GetExtraDomains();

                var subjectName = new X500DistinguishedName($"CN={domain}");
                var endCert = CertGenerator.CreateEndCertificate(this.caCert, subjectName, extraDomains, notBefore, notAfter);

                // 重新初始化证书，以兼容win平台不能使用内存证书
                return new X509Certificate2(endCert.Export(X509ContentType.Pfx));
            }
        }

        /// <summary>
        /// 获取域名
        /// </summary>
        /// <param name="domain"></param>
        /// <returns></returns>
        private static IEnumerable<string> GetExtraDomains()
        {
            yield return Environment.MachineName;
            yield return IPAddress.Loopback.ToString();
            yield return IPAddress.IPv6Loopback.ToString();
        }
    }
}
