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

            var startInfo = new ProcessStartInfo
            {
                FileName = FASTGITHUB_PATH,
                Arguments = $"ParentProcessId={Process.GetCurrentProcess().Id} UdpLoggerPort={UdpLogger.Port}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            EngineProcess = Process.Start(startInfo);
        }

        /// <summary>
        /// 停止加速引擎
        /// </summary>
        internal static void StopEngine()
        {
            if (EngineProcess == null)
            {
                return;
            }

            try
            {
                if (EngineProcess.HasExited == false)
                {
                    EngineProcess.Kill();
                }
            }
            catch
            {
                // 进程可能已退出，忽略
            }

            try
            {
                EngineProcess.Dispose();
            }
            catch
            {
                // 忽略
            }

            EngineProcess = null;
        }
    }
}
