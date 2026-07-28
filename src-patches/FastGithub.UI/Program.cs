using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace FastGithub.UI
{
    class Program
    {
        private const string MUTEX_NAME = "Global\\FastGithub.UI";
        private const string MAIN_WINDOWS = "MainWindow.xaml";
        private const string FASTGITHUB_PATH = "fastgithub.exe";

        /// <summary>
        /// 加速引擎子进程（fastgithub.exe）。由本 UI 拉起并受控启停。
        /// </summary>
        internal static Process? EngineProcess { get; private set; }

        /// <summary>
        /// 锚点进程：fastgithub 把它当成“父进程”监听其退出以走优雅停机路径。
        /// UI 自身不退出时，我们杀掉此锚点即可触发 fastgithub 的
        /// WaitForParentProcessExitAsync -> host.StopAsync()，从而清理 dnscrypt-proxy
        /// 等子进程（避免硬杀 fastgithub 直接遗弃孤儿进程）。
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

            StartEngine();
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
            var registryKey = Registry.CurrentUser.OpenSubKey(subKey, true);
            if (registryKey == null)
            {
                registryKey = Registry.CurrentUser.CreateSubKey(subKey);
            }
            var name = $"{Process.GetCurrentProcess().ProcessName}.exe";
            using var webBrowser = new System.Windows.Forms.WebBrowser();
            var value = int.Parse($"{webBrowser.Version.Major}000");
            registryKey.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>
        /// 设置浏览器DPI
        /// </summary>
        private static void SetWebBrowserDPI()
        {
            const string subKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_96DPI_PIXEL";
            var registryKey = Registry.CurrentUser.OpenSubKey(subKey, true);
            if (registryKey == null)
            {
                registryKey = Registry.CurrentUser.CreateSubKey(subKey);
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
        internal static void StartEngine()
        {
            if (IsEngineRunning)
            {
                return;
            }

            if (File.Exists(FASTGITHUB_PATH) == false)
            {
                return;
            }

            // 拉起锚点进程，作为 fastgithub 名义上的“父进程”
            EnsureAnchor();
            var parentPid = (_anchorProcess != null && _anchorProcess.HasExited == false)
                ? _anchorProcess.Id
                : Process.GetCurrentProcess().Id;   // 锚点不可用时回退为 UI 自身（行为等价旧版）

            var startInfo = new ProcessStartInfo
            {
                FileName = FASTGITHUB_PATH,
                Arguments = $"ParentProcessId={parentPid} UdpLoggerPort={UdpLogger.Port}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            EngineProcess = Process.Start(startInfo);
        }

        /// <summary>
        /// 停止加速引擎。
        /// 默认走优雅停机：杀掉锚点进程，使 fastgithub 检测到“父进程退出”后自行
        /// host.StopAsync()（清理 dnscrypt-proxy 等子进程）；超时未退则兜底强杀。
        /// </summary>
        internal static void StopEngine(bool graceful = true)
        {
            if (EngineProcess == null)
            {
                TryDisposeAnchor();
                return;
            }

            if (graceful)
            {
                // 触发 fastgithub 的 WaitForParentProcessExitAsync -> host.StopAsync()
                TryKillAnchor();
                try
                {
                    if (EngineProcess.HasExited == false && EngineProcess.WaitForExit(5000) == false)
                    {
                        EngineProcess.Kill();   // 超时兜底：强杀
                    }
                }
                catch
                {
                    try { EngineProcess.Kill(); } catch { }
                }
            }
            else
            {
                try { if (EngineProcess.HasExited == false) EngineProcess.Kill(); } catch { }
            }

            try { EngineProcess.Dispose(); } catch { }
            EngineProcess = null;
            TryDisposeAnchor();
        }

        /// <summary>
        /// UI 退出时调用：杀掉锚点即可让 fastgithub 自行优雅停机（清理 dnscrypt），
        /// 不等待、不强杀，让 fastgithub 在后台完成收尾后退出。
        /// </summary>
        internal static void DetachEngineOnExit()
        {
            TryKillAnchor();
            TryDisposeAnchor();
            try { EngineProcess?.Dispose(); } catch { }
            EngineProcess = null;
        }

        /// <summary>
        /// 拉起锚点进程（timeout.exe 长期挂起，被杀即视为“父进程退出”）。
        /// 失败时留空，由调用方回退为 UI 自身 PID。
        /// </summary>
        private static void EnsureAnchor()
        {
            if (_anchorProcess != null && _anchorProcess.HasExited == false)
            {
                return;
            }
            try
            {
                _anchorProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "timeout.exe",
                    Arguments = "/t 999999 /nobreak",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch
            {
                _anchorProcess = null;
            }
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
