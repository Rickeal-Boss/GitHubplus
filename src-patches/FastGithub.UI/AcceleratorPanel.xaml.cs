using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace FastGithub.UI
{
    /// <summary>
    /// 加速控制面板：启停加速、勾选要加速的网址、切换 HuggingFace 镜像/主站直连（beta）。
    /// 实现不修改 FastGithub 核心代码，仅通过对 appsettings/*.json 片段的启用/停用
    /// 与 fastgithub.exe 子进程的启停来控制加速行为。
    /// </summary>
    public partial class AcceleratorPanel : UserControl
    {
        private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        private static readonly string AppSettingsDir = Path.Combine(AppDir, "appsettings");
        private static readonly string DisabledDir = Path.Combine(AppSettingsDir, "disabled");

        private bool _suppressEvents;

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

        // HuggingFace 主站直连（beta）：去掉 Destination，复用 GitHub 的「免发送 SNI + 忽略证书不匹配」机制
        private const string DirectHuggingFaceJson = @"{
  ""FastGithub"": {
    ""DomainConfigs"": {
      ""huggingface.co"": { ""TlsSni"": false, ""TlsIgnoreNameMismatch"": true },
      ""hf.co"": { ""TlsSni"": false, ""TlsIgnoreNameMismatch"": true },
      ""*.huggingface.co"": { ""TlsSni"": false, ""TlsIgnoreNameMismatch"": true }
    }
  }
}";

        // HuggingFace 镜像加速（默认）：转发到 hf-mirror.com
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
            RefreshHfMode();
        }

        #region 启停

        private void RefreshStatus()
        {
            var running = Program.IsEngineRunning;
            StatusText.Text = running ? "状态：加速运行中" : "状态：已停止";
            ToggleButton.Content = running ? "停止加速" : "启动加速";
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (Program.IsEngineRunning)
            {
                Program.StopEngine();
            }
            else
            {
                Program.StartEngine();
            }
            Thread.Sleep(300);
            RefreshStatus();
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

            UpdateHfAvailability();
        }

        private void Site_Toggled(object sender, RoutedEventArgs e)
        {
            var cb = (CheckBox)sender;
            var key = (string)cb.Tag;

            if (cb.IsChecked == true)
            {
                EnableFragment(key);
            }
            else
            {
                DisableFragment(key);
            }

            UpdateHfAvailability();
            ApplyAndRestart();
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

        #region HuggingFace 模式

        private void UpdateHfAvailability()
        {
            var hfOn = SiteEnabled("huggingface");
            HfMirrorRadio.IsEnabled = hfOn;
            HfDirectRadio.IsEnabled = hfOn;
            HfHint.Text = hfOn
                ? "启用 HuggingFace 后可选；切换后自动重启加速以生效。"
                : "先勾选 HuggingFace 才能选择加速模式。";
        }

        private void RefreshHfMode()
        {
            var frag = Path.Combine(AppSettingsDir, FragmentName("huggingface"));
            if (File.Exists(frag) == false)
            {
                return;
            }

            var isDirect = false;
            try
            {
                var txt = File.ReadAllText(frag);
                var jo = JObject.Parse(txt);
                var dc = jo["FastGithub"]?["DomainConfigs"]?["huggingface.co"] as JObject;
                isDirect = dc == null || dc["Destination"] == null;
            }
            catch
            {
                // 解析失败时保持默认
            }

            // 程序化赋值会触发 Checked 事件，用标志抑制，避免加载期误重写/重启
            _suppressEvents = true;
            HfMirrorRadio.IsChecked = !isDirect;
            HfDirectRadio.IsChecked = isDirect;
            _suppressEvents = false;
        }

        private void HfMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents)
            {
                return;
            }

            if (HfMirrorRadio.IsChecked == true)
            {
                WriteHuggingFace(false);
            }
            else if (HfDirectRadio.IsChecked == true)
            {
                WriteHuggingFace(true);
            }
            ApplyAndRestart();
        }

        private void WriteHuggingFace(bool direct)
        {
            var frag = Path.Combine(AppSettingsDir, FragmentName("huggingface"));
            if (File.Exists(frag) == false)
            {
                return;
            }
            File.WriteAllText(frag, direct ? DirectHuggingFaceJson : MirrorHuggingFaceJson);
        }

        #endregion

        private void ApplyAndRestart()
        {
            if (Program.IsEngineRunning)
            {
                Program.StopEngine();
                Thread.Sleep(800);
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
    }
}
