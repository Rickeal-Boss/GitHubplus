using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace FastGithub.UI
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    ///
    /// 本文件为补丁版本，相对上游（creazyboyone/FastGithub）的改动：
    /// 1. 托盘「检测更新」跳转链接指向本仓库
    /// 2. 拆开「最小化」与「关闭」的语义（上游把两者都处理成 Hide，见下方 WndProc）
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly System.Windows.Forms.NotifyIcon notifyIcon;
        private const string FASTGITHUB_UI = "FastGithub.UI";
        private const string RELEASES_URI = "https://github.com/Rickeal-Boss/GitHubplus/releases";

        public MainWindow()
        {
            InitializeComponent();

            var upgrade = new System.Windows.Forms.MenuItem("检测更新(&U)");
            upgrade.Click += (s, e) => Process.Start(RELEASES_URI);

            var exit = new System.Windows.Forms.MenuItem("退出应用(&C)");
            exit.Click += (s, e) => this.Close();

            var version = this.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            this.Title = $"{FASTGITHUB_UI} v{version}";
            this.notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Visible = true,
                Text = FASTGITHUB_UI,
                ContextMenu = new System.Windows.Forms.ContextMenu(new[] { upgrade, exit }),
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath)
            };

            // 左键单击托盘图标：还原并激活窗口
            this.notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    this.Show();
                    this.Activate();
                    this.WindowState = WindowState.Normal;
                }
            };
        }


        /// <summary>
        /// 拦截关闭事件：收起窗口到托盘，而不是退出进程。
        /// </summary>
        /// <param name="e"></param>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
            hwndSource.AddHook(WndProc);

            IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                const int WM_SYSCOMMAND = 0x112;
                const int SC_CLOSE = 0xf060;

                // [PATCH] 上游原实现是：
                //     if (wParam.ToInt32() == SC_MINIMIZE || wParam.ToInt32() == SC_CLOSE) { this.Hide(); handled = true; }
                // 即「点击最小化」与「点击关闭」表现完全一致：窗口凭空消失到托盘，
                // 用户会以为程序已退出，而加速引擎其实还在后台运行。
                // 现在只拦截关闭（收起托盘），最小化交还系统默认行为（缩到任务栏）。
                if (msg == WM_SYSCOMMAND && wParam.ToInt32() == SC_CLOSE)
                {
                    this.Hide();
                    handled = true;
                }
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 关闭时
        /// </summary>
        /// <param name="e"></param>
        protected override void OnClosed(EventArgs e)
        {
            this.notifyIcon.Icon = null;
            this.notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}
