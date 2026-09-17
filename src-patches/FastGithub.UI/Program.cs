using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FastGithub.UI
{
    class Program
    {
        private const string MUTEX_NAME = "Global\\FastGithub.UI";
        private const string MAIN_WINDOWS = "MainWindow.xaml";
        // [PATCH] 本 UI 以管理员运行，所有外部可执行文件都必须用绝对路径启动。
        // 原因：UseShellExecute=false 时 CreateProcess 的搜索顺序是
        // 「应用程序目录 → 当前目录 → System32 → PATH」，前两项都指向程序目录，
        // 而程序目录通常解压在 Downloads 等用户可写位置、ACL 只保护到「同一用户」——
        // 使用者本身又是管理员，因此投放同名 exe 即可无 UAC 提示静默提权，且每次启动触发。
        private const string FASTGITHUB_PATH = "fastgithub.exe";

        /// <summary>
        /// 加速引擎子进程（fastgithub.exe）。由本 UI 拉起并受控启停。
        /// </summary>
        internal static Process? EngineProcess { get; private set; }

        /// <summary>
        /// [PATCH] 引擎启停的串行化锁。
        /// StartEngine / StopEngine 都是可重入的（勾选站点与启停按钮都可能在 UI 线程并发触发），
        /// 无锁时会出现「两个线程都判定引擎未运行 → 起两个 fastgithub.exe」，
        /// 后起的抢不到 38457 端口立即退出，而 EngineProcess 被它覆盖 → UI 显示已停止但引擎仍在跑。
        /// </summary>
        private static readonly object engineLock = new object();

        /// <summary>
        /// 锚点进程：fastgithub 把它当成“父进程”监听其退出以走优雅停机路径。
        /// UI 自身不退出时，我们杀掉此锚点即可触发 fastgithub 的
        /// WaitForParentProcessExitAsync -> host.StopAsync()，从而清理 dnscrypt-proxy
        /// 等子进程（避免硬杀 fastgithub 直接遗弃孤儿进程）。
        /// 锚点选用 ping -t 127.0.0.1：它不读 stdin，在「GUI 子进程 + 无控制台」
        /// 环境下不会像 timeout.exe 那样立即退出，可长期存活直到被我们杀掉。
        /// </summary>
        private static Process? _anchorProcess;

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            using var mutex = new Mutex(true, MUTEX_NAME, out var isFirstInstance);
            if (isFirstInstance == false)
            {
                return;
            }

            StartEngine(verify: false);   // 启动时不等校验，避免窗口延后显示
            SetWebBrowserDPI();
            SetWebBrowserVersion();

            var app = new Application();
            app.StartupUri = new Uri(MAIN_WINDOWS, UriKind.Relative);
            app.Exit += (s, e) => DetachEngineOnExit();   // UI 退出时让 fastgithub 自行优雅停机
            app.Run();
        }

        /// <summary>
        /// 程序集加载失败时
        /// </summary>
        private static Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name.EndsWith(".resources"))
            {
                return default;
            }

            var stream = Application.GetResourceStream(new Uri($"Resource/{name}.dll", UriKind.Relative)).Stream;
            var buffer = new byte[stream.Length];
            stream.Read(buffer, 0, buffer.Length);
            return Assembly.Load(buffer);
        }

        /// <summary>
        /// 设置浏览器版本
        /// </summary>
        private static void SetWebBrowserVersion()
        {
            const string subKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";

            // [PATCH] OpenSubKey / CreateSubKey 的返回值都可能为 null（权限不足、被组策略锁定），
            // 原实现未判空即 SetValue，会抛 NullReferenceException 中断 UI 启动；
            // 且两者都返回需要释放的 RegistryKey，原实现未放进 using。
            // 这里统一放进 using 管理生命周期，取不到时跳过并记 Warning，不影响主流程。
            using var registryKey = OpenOrCreateSubKey(subKey);
            if (registryKey == null)
            {
                Trace.TraceWarning($"无法打开或创建注册表项 {subKey}，跳过浏览器版本模拟值写入。");
                return;
            }

            var name = $"{Process.GetCurrentProcess().ProcessName}.exe";

            // [PATCH] 原实现 new 一个 System.Windows.Forms.WebBrowser 只为读它的 Version.Major，
            // 会实例化一个真正的 IE 内核控件：开销大、强依赖 IE 组件，在无 IE 的精简系统上
            // 可能挂起甚至抛异常。改为直接读注册表里的 IE 版本号（svcVersion 优先，其次 Version；
            // 64 位视图与 WOW6432Node 视图都查），读不到时回退到 IE11 的默认值 11000，
            // 绝不再实例化 WebBrowser 控件。
            var value = GetIEBrowserEmulationValue();
            registryKey.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>
        /// [PATCH] 以可写方式打开注册表子键；不存在则创建。
        /// 返回 null 表示既打不开也建不出（权限不足 / 被组策略锁定），由调用方判定是否跳过。
        /// 返回的 RegistryKey 需由调用方用 using 管理生命周期。
        /// </summary>
        private static RegistryKey? OpenOrCreateSubKey(string subKey)
        {
            var registryKey = Registry.CurrentUser.OpenSubKey(subKey, true);
            if (registryKey == null)
            {
                registryKey = Registry.CurrentUser.CreateSubKey(subKey);
            }
            return registryKey;
        }

        /// <summary>
        /// [PATCH] 计算 FEATURE_BROWSER_EMULATION 需要的值（IE 主版本号 × 1000）。
        /// 取不到版本号时回退到 IE11 对应的 11000，保证主流程不因读取失败而中断。
        /// </summary>
        private static int GetIEBrowserEmulationValue()
        {
            var major = GetIEVersionMajorFromRegistry();
            return major > 0 ? major * 1000 : 11000;
        }

        /// <summary>
        /// [PATCH] 从注册表读取 IE 主版本号。HKLM 与 HKCU、64 位视图与 WOW6432Node 视图依次尝试；
        /// 读不到返回 0。
        /// </summary>
        private static int GetIEVersionMajorFromRegistry()
        {
            const string ieSubKey = @"SOFTWARE\Microsoft\Internet Explorer";
            const string ieSubKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Internet Explorer";

            var hives = new[] { Registry.LocalMachine, Registry.CurrentUser };
            var subKeys = new[] { ieSubKey, ieSubKeyWow };
            foreach (var hive in hives)
            {
                foreach (var subKey in subKeys)
                {
                    var major = TryReadIEVersionMajor(hive, subKey);
                    if (major > 0)
                    {
                        return major;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// [PATCH] 在指定注册表位置读取 IE 主版本号：svcVersion 优先，其次 Version。
        /// </summary>
        private static int TryReadIEVersionMajor(RegistryKey hive, string subKey)
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key == null)
                {
                    return 0;
                }
                foreach (var valueName in new[] { "svcVersion", "Version" })
                {
                    var version = key.GetValue(valueName) as string;
                    if (string.IsNullOrEmpty(version))
                    {
                        continue;
                    }
                    if (int.TryParse(version.Split('.')[0], out var major) && major > 0)
                    {
                        return major;
                    }
                }
            }
            catch
            {
                // 忽略单点读取失败，交由上层继续尝试其它位置
            }
            return 0;
        }

        /// <summary>
        /// 设置浏览器DPI
        /// </summary>
        private static void SetWebBrowserDPI()
        {
            const string subKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_96DPI_PIXEL";

            // [PATCH] 同 SetWebBrowserVersion：OpenSubKey / CreateSubKey 可能返回 null，
            // 统一用 using 管理并判空，取不到时跳过并记 Warning，避免 NullReferenceException 中断启动。
            using var registryKey = OpenOrCreateSubKey(subKey);
            if (registryKey == null)
            {
                Trace.TraceWarning($"无法打开或创建注册表项 {subKey}，跳过浏览器 DPI 模拟值写入。");
                return;
            }

            var name = $"{Process.GetCurrentProcess().ProcessName}.exe";
            registryKey.SetValue(name, 1, RegistryValueKind.DWord);
        }

        /// <summary>
        /// 加速引擎是否正在运行
        /// </summary>
        internal static bool IsEngineRunning => EngineProcess != null && EngineProcess.HasExited == false;

        /// <summary>
        /// 启动加速引擎（若已在运行则忽略）
        /// </summary>
        /// <param name="verify">
        /// 是否等待并校验引擎确实存活。用户在 UI 上主动启动时用 true（失败要能报出来）；
        /// 程序启动时用 false —— 否则窗口会因这 1.5 秒延后显示，而启动状态在
        /// 加速面板加载时会立刻回显给用户。
        /// </param>
        internal static bool StartEngine(bool verify = true)
        {
            lock (engineLock)
            {
                if (IsEngineRunning)
                {
                    return true;
                }

                // [PATCH] 绝对路径化（见类顶部说明）：不再依赖 CreateProcess 的目录搜索顺序
                var enginePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FASTGITHUB_PATH);
                if (File.Exists(enginePath) == false)
                {
                    return false;
                }

                // 拉起锚点进程，作为 fastgithub 名义上的“父进程”
                EnsureAnchor();
                var parentPid = (_anchorProcess != null && _anchorProcess.HasExited == false)
                    ? _anchorProcess.Id
                    : Process.GetCurrentProcess().Id;   // 锚点不可用时回退为 UI 自身（行为等价旧版）

                var startInfo = new ProcessStartInfo
                {
                    FileName = enginePath,
                    Arguments = $"ParentProcessId={parentPid} UdpLoggerPort={UdpLogger.Port}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process? process;
                try
                {
                    process = Process.Start(startInfo);
                }
                catch
                {
                    return false;
                }
                if (process == null)
                {
                    return false;
                }
                EngineProcess = process;

                // [PATCH] 原实现启动后不做任何校验：exe 缺失、被杀软拦截、38457 端口被占用、
                // WinDivert 驱动加载失败等情况都会静默表现为「没在加速」，而 UI 仍显示运行中。
                // 给引擎 1.5 秒：若在此期间自行退出，判定为启动失败。
                if (verify && process.WaitForExit(1500) && process.HasExited)
                {
                    try { process.Dispose(); } catch { }
                    EngineProcess = null;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// 停止加速引擎。
        /// 默认走优雅停机：杀掉锚点进程，使 fastgithub 检测到“父进程退出”后自行
        /// host.StopAsync()（清理 dnscrypt-proxy 等子进程）；超时未退则兜底强杀。
        /// </summary>
        internal static void StopEngine(bool graceful = true)
        {
            lock (engineLock)
            {
                // [PATCH] 先把引用取到局部变量再置空：原实现全程读静态属性 EngineProcess，
                // 而并发调用会把它置空 / 换掉，导致杀错进程或漏杀（TOCTOU）。
                var process = EngineProcess;
                EngineProcess = null;

                if (process == null)
                {
                    TryDisposeAnchor();
                    return;
                }

                if (graceful)
                {
                    // 触发 fastgithub 的 WaitForParentProcessExitAsync -> host.StopAsync()
                    TryKillAnchor();
                    // 确认锚点已真正退出（fastgithub 已收到“父进程退出”信号）再等引擎收尾
                    try { WaitForAnchorExit().Wait(2000); } catch { }
                    try
                    {
                        if (process.HasExited == false && process.WaitForExit(5000) == false)
                        {
                            KillProcessTree(process);   // 超时兜底：强杀整棵进程树
                        }
                    }
                    catch
                    {
                        KillProcessTree(process);
                    }
                }
                else
                {
                    KillProcessTree(process);
                }

                try { process.Dispose(); } catch { }
                TryDisposeAnchor();
            }
        }

        /// <summary>
        /// [PATCH] 强杀引擎进程及其整棵子进程树（fastgithub.exe 派生的 dnscrypt-proxy 等）。
        /// 本 UI 目标框架为 net45，Process.Kill(bool entireProcessTree) 不可用；
        /// 故用系统目录（Environment.SystemDirectory）拼 taskkill.exe 绝对路径执行 /F /T /PID，
        /// 与本文件 ping.exe 的绝对路径化保持一致，避免被程序目录投放的同名文件劫持。
        /// 先 taskkill：返回码非 0 或抛异常时，退回原 process.Kill()，保证兜底仍然有效。
        /// </summary>
        private static void KillProcessTree(Process process)
        {
            if (process == null)
            {
                return;
            }

            var killedByTaskkill = false;
            try
            {
                if (process.HasExited)
                {
                    return;   // 已退出，无需再杀
                }

                var taskkillPath = Path.Combine(Environment.SystemDirectory, "taskkill.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = taskkillPath,
                    Arguments = $"/F /T /PID {process.Id}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var taskkill = Process.Start(psi);
                if (taskkill != null)
                {
                    // 先读空输出再等待退出，避免管道缓冲写满使 taskkill 阻塞
                    try { taskkill.StandardOutput.ReadToEnd(); } catch { }
                    try { taskkill.StandardError.ReadToEnd(); } catch { }
                    taskkill.WaitForExit(5000);
                    killedByTaskkill = taskkill.HasExited && taskkill.ExitCode == 0;
                }
            }
            catch
            {
                killedByTaskkill = false;
            }

            if (killedByTaskkill == false)
            {
                // 退回原兜底：直接强杀进程本身（不含子进程）
                try { process.Kill(); } catch { }
            }
        }

        /// <summary>
        /// UI 退出时调用：确保引擎与 dnscrypt 子进程被停止。
        /// 原实现只杀锚点等 fastgithub 自行退出，但若 fastgithub 的
        /// WaitForParentProcessExitAsync 未响应或 dnscrypt 卡住，引擎会残留，
        /// 占用 38457 端口导致下次启动失败（ui-error.log 报"端口被占用"）。
        /// 现改为直接 StopEngine（含 5 秒优雅停机 + 强杀兜底）。
        /// </summary>
        internal static void DetachEngineOnExit()
        {
            try { StopEngine(graceful: true); } catch { }
            TryDisposeAnchor();
        }

        /// <summary>
        /// 拉起锚点进程（ping -t 127.0.0.1 长期存活，被杀即视为“父进程退出”）。
        /// 先用「重定向输出」方式启动；若该方式失败，再退回「无重定向」方式兜底。
        /// 两者都失败时留空，由调用方回退为 UI 自身 PID。
        /// </summary>
        private static void EnsureAnchor()
        {
            if (_anchorProcess != null && _anchorProcess.HasExited == false)
            {
                return;
            }
            try
            {
                _anchorProcess = StartAnchor(redirect: true) ?? StartAnchor(redirect: false);
            }
            catch
            {
                _anchorProcess = null;
            }
        }

        /// <summary>
        /// 启动 ping 锚点进程。
        /// ping -t 不读 stdin，在「GUI 子进程 + CreateNoWindow（无控制台）」环境下
        /// 不会像 timeout.exe 那样读 stdin 失败后立刻退出，可长期存活。
        /// redirect=true 时重定向标准输出/错误并后台读取丢弃，避免管道缓冲写满
        /// 导致 ping 在 WriteFile 上阻塞（仍存活，但更干净）。
        /// </summary>
        private static Process? StartAnchor(bool redirect)
        {
            // [PATCH] 锚点进程同样绝对路径化：优先取系统目录下的 ping.exe，
            // 取不到时才回退为按名字查找（此时仍存在被程序目录劫持的风险，但概率极低）
            var systemPing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            var pingPath = File.Exists(systemPing) ? systemPing : "ping";

            var psi = new ProcessStartInfo
            {
                FileName = pingPath,
                Arguments = "-t 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (redirect)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }

            var p = Process.Start(psi);
            if (p != null && redirect)
            {
                // 后台读取并丢弃输出，防止管道缓冲写满使 ping 卡死在写操作
                _ = Task.Run(() => { try { p.StandardOutput.ReadToEnd(); } catch { } });
                _ = Task.Run(() => { try { p.StandardError.ReadToEnd(); } catch { } });
            }
            return p;
        }

        /// <summary>
        /// 返回在锚点进程退出时完成的 Task（基于 Process.Exited + TaskCompletionSource）。
        /// 锚点已被杀或不存在时立即完成，可安全 await / Wait。
        /// </summary>
        private static Task WaitForAnchorExit()
        {
            var anchor = _anchorProcess;
            if (anchor == null || anchor.HasExited)
            {
                return Task.FromResult(true);
            }

            var tcs = new TaskCompletionSource<bool>();
            void OnExited(object? sender, EventArgs e)
            {
                anchor.Exited -= OnExited;
                tcs.TrySetResult(true);
            }

            anchor.EnableRaisingEvents = true;
            anchor.Exited += OnExited;
            return anchor.HasExited ? Task.FromResult(true) : tcs.Task;
        }

        private static void TryKillAnchor()
        {
            if (_anchorProcess == null) return;
            try { if (_anchorProcess.HasExited == false) _anchorProcess.Kill(); } catch { }
        }

        private static void TryDisposeAnchor()
        {
            if (_anchorProcess == null) return;
            try { if (_anchorProcess.HasExited == false) _anchorProcess.Kill(); } catch { }
            try { _anchorProcess.Dispose(); } catch { }
            _anchorProcess = null;
        }
    }
}
