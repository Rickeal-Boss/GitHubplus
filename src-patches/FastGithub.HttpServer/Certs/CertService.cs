using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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
                return cert.NotAfter > DateTime.Now.AddDays(30);
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
        /// [PATCH] 让 git 使用 Windows 系统证书存储，而不是关闭证书校验
        /// </summary>
        private static void ConfigureGitToUseSystemCertStore()
        {
            // 仅 Windows 有 Schannel 后端可切；其它平台沿用系统默认校验行为
            if (OperatingSystem.IsWindows() == false)
            {
                return;
            }

            // 清理旧版本遗留的全局关闭配置（幂等：未设置时 git 返回非 0，忽略退出码即可）
            RunGitConfig("--unset http.sslVerify");

            // 仅当用户从未显式配置过 sslBackend 时才切换，
            // 避免每次勾选站点重启引擎都重刷用户的既有选择
            var sslBackend = RunGitConfigGet("http.sslBackend");
            if (string.IsNullOrEmpty(sslBackend))
            {
                RunGitConfig("http.sslBackend schannel");
            }

            // 注：Git for Windows 的 http.schannelCheckRevoke 默认值已是 best-effort，
            //     自签 CA 缺少 CRL 分发点也不会报错，因此无需额外设置。
        }

        /// <summary>
        /// [PATCH] 执行 git config --global（修复原实现的两个 bug：
        /// UseShellExecute=true 拿不到退出码、且未等待进程结束）
        /// </summary>
        /// <param name="arguments">传给 git config --global 的参数</param>
        /// <returns>是否执行成功</returns>
        private static bool RunGitConfig(string arguments)
        {
            // [PATCH] 按名字启动 git 存在程序目录劫持风险：UseShellExecute=false 时
            // CreateProcess 的搜索顺序是「应用程序目录 → 当前目录 → System32 → PATH」，
            // 前两项都指向程序目录（通常解压在用户可写位置，而本程序以管理员运行）。
            // 把 WorkingDirectory 设为系统目录，至少消除第二项。
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"config --global {arguments}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.SystemDirectory
                });

                return process != null && process.WaitForExit(5000);
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
                    FileName = "git",
                    Arguments = $"config --global --get {key}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.SystemDirectory
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
                using var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(this.CaKeyFilePath));
                this.caCert = new X509Certificate2(this.CaCerFilePath).CopyWithPrivateKey(rsa);
            }

            var key = $"{nameof(CertService)}:{domain}";
            var endCert = this.serverCertCache.GetOrCreate(key, GetOrCreateCert);
            return endCert!;

            // 生成域名的1年证书
            X509Certificate2 GetOrCreateCert(ICacheEntry entry)
            {
                var notBefore = DateTimeOffset.Now.AddDays(-1);
                var notAfter = DateTimeOffset.Now.AddYears(1);
                entry.SetAbsoluteExpiration(notAfter);

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
