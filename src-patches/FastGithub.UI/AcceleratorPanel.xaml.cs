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
    /// </summary>
    public partial class AcceleratorPanel : UserControl
    {
        private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        private static readonly string AppSettingsDir = Path.Combine(AppDir, "appsettings");
        private static readonly string DisabledDir = Path.Combine(AppSettingsDir, "disabled");

        // 界面背景：导入的图片存于 ui-background/，路径持久化在 ui-background.txt；为空即默认背景
        private static readonly string BackgroundDir = Path.Combine(AppDir, "ui-background");
        private static readonly string BackgroundCfg = Path.Combine(AppDir, "ui-background.txt");

        private static readonly Dictionary<string, string> FriendlyNames = new Dictionary<string, string>
        {
            { "github", "GitHub（代码 / API / 克隆 / 图床）" },
            { "huggingface", "HuggingFace（模型 / 数据集）" },
            { "google", "Google" },
            { "microsoft", "Microsoft" },
            { "amazonaws", "AWS（S3 等）" },
            { "fastly", "Fastly CDN" },
            { "imgur", "Imgur" },
            { "bootcss", "BootCDN" },
            { "packages", "软件包源（Packages）" },
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

        public AcceleratorPanel()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
            RefreshSites();
            EnsureHfMirror();
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
            if (Directory.Exists(AppSettingsDir) == false)
            {
                return;
            }

            var keys = new SortedSet<string>();
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

            foreach (var key in keys)
            {
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

            UpdateHfHint();
        }

        private async void Site_Toggled(object sender, RoutedEventArgs e)
        {
            var cb = (CheckBox)sender;
            var key = (string)cb.Tag;

            if (cb.IsChecked == true)
            {
                EnableFragment(key);
                if (key == "huggingface") WriteHuggingFace();   // 确保 HF 为镜像（清理旧版直连片段）
            }
            else
            {
                DisableFragment(key);
            }

            UpdateHfHint();
            await ApplyAndRestart();
        }

        private void EnableFragment(string key)
        {
            var src = Path.Combine(DisabledDir, FragmentName(key));
            var dst = Path.Combine(AppSettingsDir, FragmentName(key));
            if (File.Exists(src))
            {
                if (File.Exists(dst))
                {
                    File.Delete(dst);
                }
                File.Move(src, dst);
            }
        }

        private void DisableFragment(string key)
        {
            var src = Path.Combine(AppSettingsDir, FragmentName(key));
            var dst = Path.Combine(DisabledDir, FragmentName(key));
            if (File.Exists(src))
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
        }

        #endregion

        #region HuggingFace 镜像

        // 确保 HF 片段为镜像模式（清理旧版可能遗留的「主站直连」片段）
        private void EnsureHfMirror()
        {
            var frag = Path.Combine(AppSettingsDir, FragmentName("huggingface"));
            if (File.Exists(frag) == false) return;
            try { File.WriteAllText(frag, MirrorHuggingFaceJson); } catch { }
        }

        private void WriteHuggingFace()
        {
            var frag = Path.Combine(AppSettingsDir, FragmentName("huggingface"));
            if (File.Exists(frag) == false) return;
            try { File.WriteAllText(frag, MirrorHuggingFaceJson); } catch { }
        }

        private void UpdateHfHint()
        {
            var hfOn = SiteEnabled("huggingface");
            HfHint.Text = hfOn
                ? "HuggingFace 采用镜像加速（hf-mirror.com，稳定推荐）；修改勾选后自动重启加速以生效。"
                : "先勾选 HuggingFace 以启用镜像加速。";
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
                MessageBox.Show("设置背景失败：" + ex.Message, "界面背景", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            catch { }
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
            catch { }
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
            catch
            {
                BgPathText.Text = "当前：默认背景（图片加载失败）";
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
