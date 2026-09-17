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

        // 站点详细说明（鼠标悬停勾选框时显示）：加速范围 / 实现方式 / 风险 / 默认状态。
        // 内容依据上游对应 appsettings.*.json 的实际 DomainConfigs 整理。
        private static readonly Dictionary<string, string> SiteDescriptions = new Dictionary<string, string>
        {
            { "github",
              "【加速范围】\n" +
              "  github.com / api.github.com / gist.github.com / githubstatus.com\n" +
              "  *.github.com / *.github.io / *.githubusercontent.com（raw 与 Release 下载）\n" +
              "  *.githubassets.com / *.githubapp.com / vscode-auth.github.com\n" +
              "【覆盖场景】网页浏览、API 调用、git clone / pull、Release 与 raw 文件、头像图床。\n" +
              "【实现】直连转发。GitHub 经 CDN 分发，证书 SAN 常与实际域名不一致，故配置了放宽域名匹配校验。\n" +
              "【默认】启用（本工具的主要用途）。" },

            { "huggingface",
              "【加速范围】\n" +
              "  huggingface.co / hf.co / *.huggingface.co（模型与数据集下载）。\n" +
              "【实现】镜像转发——请求被改发到第三方镜像 hf-mirror.com，并非加速官方站点。\n" +
              "【⚠ 隐私】带 token / cookie 的请求会完整经过该第三方镜像，\n" +
              "  请勿在启用加速时提交不希望第三方看到的私有仓库请求。\n" +
              "【默认】启用。" },

            { "google",
              "【加速范围】\n" +
              "  ajax.googleapis.com / fonts.googleapis.com / fonts.gstatic.com /\n" +
              "  themes.googleusercontent.com —— 以上四项均被重定向到第三方反代\n" +
              "  gapis.geekzu.org、fonts.geekzu.org（非 Google 官方）。\n" +
              "  *.gravatar.com —— Gravatar 头像服务，与 Google 无关，且放宽了域名匹配校验。\n" +
              "【默认】停用。" },

            { "microsoft",
              "【加速范围】共 26 个域名，主要包括：\n" +
              "  azure.com 系列 / *.visualstudio.com / *.windows.net / *.azurewebsites.net /\n" +
              "  *.vscode.cdn.azure.cn / *.aspnetcdn.com / *.vsassets.io / static2.sharepointonline.com。\n" +
              "【⚠ 隐私风险】其中包含 live.com、onedrive.live.com、microsoftonline.com、\n" +
              "  aadcdn.msauth.net —— 即 Microsoft 账户登录与 OneDrive 云盘。\n" +
              "  启用后这些流量同样会被本机 CA 解密，请确认你接受该范围。\n" +
              "【默认】停用。" },

            { "amazonaws",
              "【加速范围】\n" +
              "  s3.amazonaws.com / *.s3.amazonaws.com（S3 对象存储）。\n" +
              "【⚠ 供应链风险】AWS CLI / SDK 及各类工具信任 Windows 系统证书存储，\n" +
              "  私钥泄露后可被用于篡改你下载的制品，或窃取经本机转发的临时凭据。\n" +
              "【默认】停用。" },

            { "fastly",
              "【加速范围】\n" +
              "  *.fastly.net 及其多级子域（*.*.fastly.net / *.*.*.fastly.net）。\n" +
              "【说明】Fastly 是被广泛使用的 CDN，npm、PyPI、GitHub 等大量静态资源由它承载。\n" +
              "【默认】停用。" },

            { "imgur",
              "【加速范围】\n" +
              "  imgur.com / *.imgur.com / *.*.imgur.com（图片托管站）。\n" +
              "【默认】停用。" },

            { "bootcss",
              "【加速范围】\n" +
              "  cdn.bootcss.com —— 被重定向到 cdnjs.cloudflare.com/ajax/libs/。\n" +
              "  *.cloudflare.com\n" +
              "【⚠ 范围过宽】Cloudflare 为全球约 20% 的网站提供 CDN 与防护服务，\n" +
              "  启用后会授权解密所有命中 *.cloudflare.com 的流量（远不止 BootCDN）。\n" +
              "【默认】停用，且不建议启用。" },

            { "packages",
              "【加速范围】\n" +
              "  *.nuget.org（.NET / NuGet）\n" +
              "  *.maven.org（Java / Maven）\n" +
              "【⚠ 供应链风险】包管理器信任 Windows 系统证书存储，且下载内容多为\n" +
              "  可执行代码与构建产物；私钥一旦泄露可导致依赖投毒。\n" +
              "【默认】停用。" },

            { "v2ex",
              "【加速范围】\n" +
              "  v2ex.com / *.v2ex.com（社区论坛）。\n" +
              "【默认】停用。" }
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

        // 引擎启动失败的常见原因（用于把「启动后零校验」的静默失败变成可见提示）
        private const string EngineStartFailureDetail =
            "引擎未能启动，当前实际未处于加速状态。常见原因：" +
            "fastgithub.exe 缺失或被安全软件拦截、38457 端口被占用、" +
            "WinDivert 驱动未加载（需以管理员身份运行）。请排查后重试。";

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
                else if (Program.StartEngine() == false)
                    ReportError("启动加速引擎", EngineStartFailureDetail);
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
                    FontSize = 13,
                    ToolTip = BuildSiteToolTip(key)
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
                // 文件移动/写入失败：回滚勾选状态，避免 UI 与实际不一致。
                // 此时不必重启引擎——配置没变，重启只会造成无谓的断网。
                SetCheckedSilently(cb, !turningOn);
                UpdateRiskHint();
                UpdateHfHint();
                return;
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
                if (File.Exists(dst))
                {
                    return true;
                }
                // 两个目录都没有该片段：直接返回 false 会让勾选被静默回滚，用户不知道为什么
                ReportError("启用站点 " + Friendly(key),
                    "未找到该站点的配置片段（appsettings\\ 与 appsettings\\disabled\\ 下都没有），无法启用。");
                return false;
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
                if (Program.StartEngine() == false)
                {
                    // [PATCH] 原实现启动后零校验：引擎起不来时 UI 只是静默从「运行中」变「已停止」，
                    // 用户会误以为仍在加速。这里必须显式告知。
                    ReportError("重启加速引擎", EngineStartFailureDetail);
                }
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
        /// 构造站点详细说明的悬停提示（宽度受限并自动换行，避免长文本撑满屏幕）
        /// </summary>
        private static ToolTip BuildSiteToolTip(string key)
        {
            return new ToolTip
            {
                Padding = new Thickness(10),
                FontSize = 12,
                Content = new TextBlock
                {
                    Text = Description(key),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460
                }
            };
        }

        /// <summary>
        /// 取站点详细说明；未登记的内置站点给出通用提示而不是空白
        /// </summary>
        private static string Description(string key)
        {
            return SiteDescriptions.TryGetValue(key, out var v)
                ? v
                : "该站点暂无内置说明。\n勾选后，其对应域名的 HTTPS 流量会被本机 CA 解密后转发；\n具体域名范围见程序目录 appsettings\\appsettings." + key + ".json。";
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
                    // [PATCH] 只加载 ui-background/ 目录内的图片：ui-background.txt 与程序同目录、
                    // 而程序目录可能被普通用户写入；若直接信任其中的任意路径，攻击者可让管理员进程
                    // 加载任意文件。这里把配置里的路径解析为绝对路径，并校验其所在目录必须等于
                    // ui-background/ 目录本身（ChooseBackground_Click 只把图片写到该目录的直接子级）。
                    var full = Path.GetFullPath(p);
                    var dir = Path.GetDirectoryName(full);
                    if (File.Exists(full) &&
                        string.Equals(dir, Path.GetFullPath(BackgroundDir), StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyBackground(full);
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

                // [PATCH] 按显示尺寸解码背景图。原实现不设 DecodePixelWidth，WPF 会按图片原始
                // 像素解码——用户选一张 4K / 手机直出大图做背景，就要常驻几十 MB 位图内存，
                // 而实际只显示到窗口大小。这里按窗口宽度（×当前 DPI 缩放）下采样；
                // 只设 DecodePixelWidth、不设 DecodePixelHeight，WPF 会保持原始宽高比。
                var decodeWidth = GetBackgroundDecodeWidth();
                if (decodeWidth > 0)
                {
                    bmp.DecodePixelWidth = decodeWidth;
                }

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

        /// <summary>
        /// [PATCH] 计算背景图解码宽度（物理像素）：窗口宽度 × 当前 DPI 缩放。
        /// 取不到窗口或缩放时回退到一个保守的默认值，避免退回按原图全分辨率解码。
        /// </summary>
        private int GetBackgroundDecodeWidth()
        {
            try
            {
                var win = Window.GetWindow(this);
                var width = (win != null && win.ActualWidth > 0) ? win.ActualWidth : this.ActualWidth;
                if (width <= 0)
                {
                    return 1920;
                }

                // 96 DPI 基准下的设备无关单位要换算成物理像素，否则高 DPI 屏会解码得偏小
                var dpiScale = 1.0;
                var source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null)
                {
                    dpiScale = source.CompositionTarget.TransformToDevice.M11;
                }
                if (dpiScale <= 0)
                {
                    dpiScale = 1.0;
                }

                var pixelWidth = (int)Math.Ceiling(width * dpiScale);
                // 上限保护：避免异常窗口尺寸 / DPI 造成过大的解码缓冲
                return Math.Min(Math.Max(pixelWidth, 1), 8192);
            }
            catch
            {
                return 1920;
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
