using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FastGithub.UI
{
    /// <summary>
    /// 加速控制面板：启停加速、勾选要加速的网址、HuggingFace 镜像加速（已去除主站直连 beta）。
    /// 实现不修改 FastGithub 核心代码，仅通过对 appsettings/*.json 片段的启用/停用
    /// 与 fastgithub.exe 子进程的启停来控制加速行为。
    /// 安全说明：每个勾选框都是一个「本地 CA 解密授权」开关，勾选即代表同意该域名
    /// 的 HTTPS 流量被本机 CA 解密后转发；所有失败都必须可见，禁止静默吞异常。
    /// </summary>
    public partial class AcceleratorPanel : UserControl
    {
        private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        private static readonly string AppSettingsDir = Path.Combine(AppDir, "appsettings");
        private static readonly string DisabledDir = Path.Combine(AppSettingsDir, "disabled");

        // 界面背景：导入的图片存于 ui-background/，路径持久化在 ui-background.txt；为空即默认背景
        private static readonly string BackgroundDir = Path.Combine(AppDir, "ui-background");
        private static readonly string BackgroundCfg = Path.Combine(AppDir, "ui-background.txt");

        // 高危站点：包管理器（NuGet / Maven）与云存储（S3）均信任系统证书存储，
        // 一旦本地 CA 私钥泄露，可被用于篡改下载的制品，形成供应链攻击。
        private static readonly HashSet<string> HighRiskSites = new HashSet<string> { "packages", "amazonaws" };

        private static readonly Dictionary<string, string> FriendlyNames = new Dictionary<string, string>
        {
            { "github", "GitHub（代码 / API / 克隆 / 图床）" },
            { "huggingface", "HuggingFace（模型 / 数据集）" },
            { "google", "Google" },
            { "microsoft", "Microsoft" },
            { "amazonaws", "AWS（S3 等 · 高风险）" },
            { "fastly", "Fastly CDN" },
            { "imgur", "Imgur" },
            { "bootcss", "BootCDN" },
            { "packages", "软件包源（Packages，含 NuGet/Maven · 高风险）" },
            { "v2ex", "V2EX" }
        };

        // HuggingFace 镜像加速（唯一模式）：转发到 hf-mirror.com
        private const string MirrorHuggingFaceJson = @"{
  ""FastGithub"": {
    ""DomainConfigs"": {
      ""huggingface.co"": { ""TlsSni"": true, ""Destination"": ""https://hf-mirror.com"" },
      ""hf.co"": { ""TlsSni"": true, ""Destination"": ""https://hf-mirror.com"" },
      ""*.huggingface.co"": { ""TlsSni"": true, ""TlsIgnoreNameMismatch"": true, ""Destination"": ""https://hf-mirror.com"" }
    }
  }
}";

        // 当前已知的站点 key 列表（启用目录 + 停用目录的并集），用于统计风险提示
        private readonly List<string> siteKeys = new List<string>();

        public AcceleratorPanel()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
            RefreshSites();
            WriteHuggingFaceMirror();
            LoadBackground();
        }

        #region 启停

        private void RefreshStatus()
        {
            var running = Program.IsEngineRunning;
            StatusText.Text = running ? "状态：加速运行中" : "状态：已停止";
            ToggleButton.Content = running ? "停止加速" : "启动加速";
        }

        private async void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleButton.IsEnabled = false;
            try
            {
                if (Program.IsEngineRunning)
                    await Task.Run(() => Program.StopEngine());   // 优雅停机（内部等待并兜底强杀）
                else
                    Program.StartEngine();
                await Task.Delay(300);
                RefreshStatus();
            }
            finally
            {
                ToggleButton.IsEnabled = true;
            }
        }

        #endregion

        #region 网址清单

        private void RefreshSites()
        {
            SitesPanel.Children.Clear();
            siteKeys.Clear();

            if (Directory.Exists(AppSettingsDir) == false)
            {
                UpdateRiskHint();
                UpdateHfHint();
                return;
            }

            var keys = new SortedSet<string>();
            try
            {
                foreach (var f in Directory.GetFiles(AppSettingsDir, "appsettings.*.json"))
                {
                    keys.Add(KeyOf(f));
                }
                if (Directory.Exists(DisabledDir))
                {
                    foreach (var f in Directory.GetFiles(DisabledDir, "appsettings.*.json"))
                    {
                        keys.Add(KeyOf(f));
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError("枚举加速站点配置片段", ex);
                UpdateRiskHint();
                UpdateHfHint();
                return;
            }

            foreach (var key in keys)
            {
                siteKeys.Add(key);

                var cb = new CheckBox
                {
                    Content = Friendly(key),
                    IsChecked = SiteEnabled(key),
                    Tag = key,
                    Margin = new Thickness(0, 4, 0, 4),
                    FontSize = 13
                };
                cb.Checked += Site_Toggled;
                cb.Unchecked += Site_Toggled;
                SitesPanel.Children.Add(cb);
            }

            UpdateRiskHint();
            UpdateHfHint();
        }

        private async void Site_Toggled(object sender, RoutedEventArgs e)
        {
            var cb = (CheckBox)sender;
            var key = (string)cb.Tag;
            var turningOn = cb.IsChecked == true;

            // 高危站点：仅在「未选中 -> 选中」时确认一次
            if (turningOn && HighRiskSites.Contains(key))
            {
                var answer = MessageBox.Show(
                    "你正在勾选高风险站点：" + Friendly(key) + Environment.NewLine + Environment.NewLine +
                    "启用后，该站点的 HTTPS 流量会被本机 CA 解密后转发；而包管理器（NuGet / Maven 等）" +
                    "与 AWS 客户端都信任 Windows 系统证书存储，因此也会信任本 CA。" + Environment.NewLine +
                    "一旦 cacert\\fastgithub.key 私钥泄露，攻击者可篡改你下载到的软件包或制品，形成供应链攻击。" + Environment.NewLine + Environment.NewLine +
                    "仅在了解并接受该风险后继续。是否启用？",
                    "高风险站点确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.Yes)
                {
                    SetCheckedSilently(cb, false);
                    UpdateRiskHint();
                    UpdateHfHint();
                    return;
                }
            }

            var success = true;
            if (turningOn)
            {
                success = EnableFragment(key);
                if (success && key == "huggingface")
                {
                    success = WriteHuggingFaceMirror();   // 确保 HF 为镜像（清理旧版直连片段）
                }
            }
            else
            {
                success = DisableFragment(key);
            }

            if (success == false)
            {
                // 文件移动/写入失败：回滚勾选状态，避免 UI 与实际不一致
                SetCheckedSilently(cb, !turningOn);
            }

            UpdateRiskHint();
            UpdateHfHint();
            await ApplyAndRestart();
        }

        // 静默设置勾选状态：临时解绑/重绑事件，避免触发递归的 Site_Toggled
        private void SetCheckedSilently(CheckBox cb, bool value)
        {
            cb.Checked -= Site_Toggled;
            cb.Unchecked -= Site_Toggled;
            cb.IsChecked = value;
            cb.Checked += Site_Toggled;
            cb.Unchecked += Site_Toggled;
        }

        /// <summary>
        /// 启用站点：把片段从 appsettings/disabled/ 移回 appsettings/
        /// </summary>
        /// <returns>是否成功（目标文件确实存在即为成功）</returns>
        private static bool EnableFragment(string key)
        {
            var src = Path.Combine(DisabledDir, FragmentName(key));
            var dst = Path.Combine(AppSettingsDir, FragmentName(key));

            // 片段本来就不在停用目录（例如上游默认启用），视为已启用
            if (File.Exists(src) == false)
            {
                return File.Exists(dst);
            }

            try
            {
                if (File.Exists(dst))
                {
                    File.Delete(dst);
                }
                File.Move(src, dst);
            }
            catch (Exception ex)
            {
                ReportError("启用站点 " + Friendly(key), ex);
                return false;
            }

            // 结果校验：文件被占用时 File.Move 可能不抛异常却未真正落盘
            if (File.Exists(dst))
            {
                return true;
            }

            ReportError("启用站点 " + Friendly(key),
                "片段移动后目标文件不存在，可能因文件被占用而未生效，请关闭占用程序（或已停止加速）后重试。");
            return false;
        }

        /// <summary>
        /// 停用站点：把片段从 appsettings/ 移到 appsettings/disabled/
        /// </summary>
        /// <returns>是否成功（片段确已不在启用目录即为成功）</returns>
        private static bool DisableFragment(string key)
        {
            var src = Path.Combine(AppSettingsDir, FragmentName(key));
            var dst = Path.Combine(DisabledDir, FragmentName(key));

            // 片段本来就不存在（例如上游未提供），无需处理
            if (File.Exists(src) == false)
            {
                return true;
            }

            try
            {
                if (Directory.Exists(DisabledDir) == false)
                {
                    Directory.CreateDirectory(DisabledDir);
                }
                if (File.Exists(dst))
                {
                    File.Delete(dst);
                }
                File.Move(src, dst);
            }
            catch (Exception ex)
            {
                ReportError("停用站点 " + Friendly(key), ex);
                return false;
            }

            // 结果校验：仍留在启用目录说明停用未生效
            if (File.Exists(src) == false)
            {
                return true;
            }

            ReportError("停用站点 " + Friendly(key),
                "片段移动后仍留在启用目录，可能因文件被占用而未生效，请关闭占用程序（或已停止加速）后重试。");
            return false;
        }

        #endregion

        #region HuggingFace 镜像

        /// <summary>
        /// 确保 HF 片段为镜像模式（清理旧版可能遗留的「主站直连」片段）。
        /// 片段不存在时不做任何事；写入失败会记录日志并弹窗提示，由调用方根据返回值处理。
        /// </summary>
        /// <returns>是否成功（片段不存在时视为成功）</returns>
        private static bool WriteHuggingFaceMirror()
        {
            var frag = Path.Combine(AppSettingsDir, FragmentName("huggingface"));
            if (File.Exists(frag) == false)
            {
                return true;
            }

            try
            {
                File.WriteAllText(frag, MirrorHuggingFaceJson);
                return true;
            }
            catch (Exception ex)
            {
                ReportError("写入 HuggingFace 镜像配置", ex);
                return false;
            }
        }

        private void UpdateHfHint()
        {
            var hfOn = SiteEnabled("huggingface");
            HfHint.Text = hfOn
                ? "HuggingFace 采用镜像加速（hf-mirror.com，稳定推荐）；修改勾选后自动重启加速以生效。注意：带 token / cookie 的请求会经过该第三方镜像，请勿在启用加速时提交不希望第三方看到的私有资源请求。"
                : "先勾选 HuggingFace 以启用镜像加速。";
        }

        #endregion

        #region 风险提示

        // 勾选即授权：把「当前有多少站点会被本机 CA 解密」如实显示给用户
        private void UpdateRiskHint()
        {
            var enabled = 0;
            var highRisk = new List<string>();

            foreach (var key in siteKeys)
            {
                if (SiteEnabled(key) == false)
                {
                    continue;
                }
                enabled++;
                if (HighRiskSites.Contains(key))
                {
                    highRisk.Add(Friendly(key));
                }
            }

            var text = "当前 " + enabled + " 个站点的 HTTPS 流量将被本机 CA 解密后转发；" +
                       "私钥明文存于 cacert\\fastgithub.key，请勿外泄。";

            if (highRisk.Count > 0)
            {
                text += Environment.NewLine +
                        "⚠ 已启用高风险站点：" + string.Join("、", highRisk.ToArray()) +
                        " —— 包管理器与云存储客户端信任系统证书存储，私钥泄露可导致供应链攻击。";
            }

            RiskHintText.Text = text;
        }

        #endregion

        private async Task ApplyAndRestart()
        {
            if (Program.IsEngineRunning)
            {
                await Task.Run(() => Program.StopEngine());   // 优雅停机（内部等待并兜底强杀）
                await Task.Delay(200);
                Program.StartEngine();
            }
            RefreshStatus();
        }

        #region 辅助

        private static string KeyOf(string filePath)
        {
            return Path.GetFileName(filePath)
                .Replace("appsettings.", "")
                .Replace(".json", "");
        }

        private static string FragmentName(string key)
        {
            return "appsettings." + key + ".json";
        }

        private static bool SiteEnabled(string key)
        {
            return File.Exists(Path.Combine(AppSettingsDir, FragmentName(key)));
        }

        private static string Friendly(string key)
        {
            return FriendlyNames.TryGetValue(key, out var v) ? v : key;
        }

        /// <summary>
        /// 记录错误日志并弹窗提示（禁止静默吞异常）
        /// </summary>
        private static void ReportError(string action, Exception ex)
        {
            ReportError(action, ex.Message);
        }

        /// <summary>
        /// 记录错误日志并弹窗提示（禁止静默吞异常）
        /// </summary>
        private static void ReportError(string action, string detail)
        {
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + action + "：" + detail;

            // 写日志失败不能影响提示本身，也不能让异常向外扩散
            try
            {
                File.AppendAllText(Path.Combine(AppDir, "ui-error.log"), line + Environment.NewLine);
            }
            catch
            {
                // 忽略：日志不可写时至少还有弹窗
            }

            try
            {
                MessageBox.Show(action + "失败：" + detail + Environment.NewLine + Environment.NewLine +
                                "详细信息已写入同目录 ui-error.log。",
                    "GitHubplus 提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // 忽略：无界面环境下不因提示失败而崩溃
            }
        }

        #endregion

        #region 界面背景

        private void ChooseBackground_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                if (Directory.Exists(BackgroundDir) == false)
                {
                    Directory.CreateDirectory(BackgroundDir);
                }
                var ext = Path.GetExtension(dlg.FileName);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                var dest = Path.Combine(BackgroundDir, "ui-background" + ext.ToLowerInvariant());
                File.Copy(dlg.FileName, dest, overwrite: true);
                File.WriteAllText(BackgroundCfg, dest);
                ApplyBackground(dest);
            }
            catch (Exception ex)
            {
                ReportError("设置界面背景", ex);
            }
        }

        private void ResetBackground_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(BackgroundCfg))
                {
                    var p = File.ReadAllText(BackgroundCfg).Trim();
                    if (File.Exists(p)) File.Delete(p);
                    File.Delete(BackgroundCfg);
                }
            }
            catch (Exception ex)
            {
                ReportError("恢复默认背景", ex);
            }
            ClearBackground();
        }

        // 加载已保存的背景（若存在则应用，否则恢复默认）
        private void LoadBackground()
        {
            try
            {
                if (File.Exists(BackgroundCfg))
                {
                    var p = File.ReadAllText(BackgroundCfg).Trim();
                    if (File.Exists(p))
                    {
                        ApplyBackground(p);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError("加载已保存的界面背景", ex);
            }
            ClearBackground();
        }

        private void ApplyBackground(string path)
        {
            var img = FindUiBackgroundImage();
            if (img == null) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                img.Source = bmp;
                img.Visibility = Visibility.Visible;
                BgPathText.Text = "当前：自定义背景（" + Path.GetFileName(path) + "）";
            }
            catch (Exception ex)
            {
                BgPathText.Text = "当前：默认背景（图片加载失败）";
                ReportError("应用界面背景", ex);
            }
        }

        private void ClearBackground()
        {
            var img = FindUiBackgroundImage();
            if (img != null)
            {
                img.Source = null;
                img.Visibility = Visibility.Collapsed;
            }
            BgPathText.Text = "当前：默认背景";
        }

        private System.Windows.Controls.Image? FindUiBackgroundImage()
        {
            var win = Window.GetWindow(this);
            return win?.FindName("UiBackgroundImage") as System.Windows.Controls.Image;
        }

        #endregion
    }
}
