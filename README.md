# GitHub + HuggingFace 加速工具（基于 FastGithub 自建）

> 自用本地加速工具。加速目标：**GitHub 主站（核心）+ HuggingFace（需加速）**，GreasyFork 已剔除。
> 底座：`creazyboyone/FastGithub`（原 dotnetcore/FastGithub，纯 .NET，AOT/Trimmed 单文件，WinDivert 包层拦截）。
> 本目录只含**自建所需的配置文件与说明**，加速内核直接复用 FastGithub，无需重写代理/MITM/证书/DNS 逻辑。

---

## 1. 需求落地对照

| 需求 | 实现 | 来源 |
|---|---|---|
| GitHub 主站加速（核心） | `TlsSni:false` SNI 伪装 + IP 优选 | `appsettings.github.json`（FastGithub 原生，保留） |
| HuggingFace 加速 | 镜像重定向 `Destination: https://hf-mirror.com` | **`appsettings.huggingface.json`（本工具新增）** |
| 不影响其他流量 | WinDivert 包层按域名作用域拦截，不匹配走 `next()` | FastGithub 原生 |
| 单文件 / 轻量 / 免安装 | .NET self-contained 发布（Trimmed 单文件） | FastGithub 原生 |
| GreasyFork | 不加入任何配置 | —— 已剔除 |
| 启动/停止加速开关 | UI「加速」页一键启停 `fastgithub.exe` 子进程 | **`src-patches/` 补丁（本工具新增）** |
| 勾选要加速的网址 | UI 复选框启用/停用 `appsettings/*.json` 片段（Watt Toolkit 风格） | **`src-patches/` 补丁（本工具新增）** |
| HuggingFace 镜像加速 | UI 单一镜像模式（`hf-mirror.com`），已去除主站直连 beta | **`src-patches/` 补丁（本工具新增）** |

---

## 2. 工作机制（已读源码确认）

```
本机 443 流量
   │  WinDivert 重定向（TcpInterceptor，不改系统代理）
   ▼
本地 HTTPS 代理（用本地 CA 为真实域名签发证书）
   │  HttpReverseProxyMiddleware.TryGetDomainConfig
   ├─ 域名不匹配 → next() 透传（其他软件零影响）★
   └─ 域名匹配   → 按 DomainConfig 处理
        ├─ Destination 非空 → 镜像重定向（HF → hf-mirror.com）
        └─ TlsSni:false      → 上游不发 SNI（GitHub 绕过 SNI 审查）
        + domainResolver 优选 IP（dnscrypt 防污染 + 测速）
```

**关键源码锚点**（在 FastGithub 仓库内）：
- `FastGithub/Startup.cs` → `ConfigureConfiguration()`：`Directory.GetFiles("appsettings", "appsettings.*.json")` **自动加载本目录所有 `appsettings.*.json`**。→ 所以我们加一个文件即可生效，无需改代码。
- `FastGithub.HttpServer/HttpMiddlewares/HttpReverseProxyMiddleware.cs` → `GetDestinationPrefix()`：`new Uri(baseUri, destination)` 实现镜像重定向。
- `FastGithub.Http/HttpClientHandler.cs` → SNI 伪装 + `domainResolver` 优选 IP。
- `FastGithub.PacketIntercept/Tcp/TcpInterceptor.cs` → WinDivert 包层重定向。

---

## 3. 本工具新增/保留的文件

| 文件 | 作用 | 处置 |
|---|---|---|
| `appsettings.github.json` | GitHub 主站域名配置（核心） | **保留**（FastGithub 原生，本目录仅作参考副本） |
| `appsettings.huggingface.json` | HuggingFace 镜像重定向配置 | **新增**（本工具核心改动） |
| `build-portable.cmd` | Windows 免安装包一键构建脚本 | **新增** |
| `src-patches/FastGithub.UI/` | UI 增强补丁：`Program.cs`（进程启停）、`MainWindow.xaml`（新增「加速」标签）、`AcceleratorPanel.xaml(.cs)`（加速控制面板） | **新增**（构建时覆盖进克隆源码，不改 FastGithub 核心） |

> 把 `appsettings.*.json` 放进 FastGithub 仓库的 `FastGithub/appsettings/` 目录即可（与 `appsettings.github.json` 同级）。

---

## 4. Windows 免安装包构建（推荐）

本目录已提供 **`build-portable.cmd`**，一键产出 `dist/FastGithub-Portable-win-x64.zip`（self-contained 单文件，解压即跑，无需安装 .NET、无需安装服务）。

```powershell
# 前置：安装 .NET 7 SDK（FastGithub 目标框架 net7.0 + RuntimeIdentifier win-x64）
#       https://dotnet.microsoft.com/download
#
# 把 build-portable.cmd 与 appsettings.huggingface.json / appsettings.github.json 放同一目录，双击运行：
build-portable.cmd
```

脚本自动完成：
1. **前置检测** `.NET 7 SDK`（`where dotnet`）；缺失则直接报错退出。
2. `git clone --depth 1` FastGithub（含 `@dnscrypt-proxy` 目录，**非子模块**，无需 `--recurse-submodules`）。
3. **改写托盘「检测更新」链接**为本仓库 `Rickeal-Boss/GitHubplus`（`MainWindow.xaml.cs` 的 `RELEASES_URI`）。
4. **注入 UI 增强补丁**：把 `src-patches/FastGithub.UI/` 下的 `Program.cs`、`MainWindow.xaml`、`AcceleratorPanel.xaml(.cs)` 覆盖进 `FastGithub.UI/`，新增「加速」标签页与加速控制面板（启停开关 + 网址勾选 + HF 模式切换）。**不改 FastGithub 核心代码**。
5. **仅注入新增的 `appsettings.huggingface.json`** 到 `FastGithub/appsettings/`。GitHub 主站配置为仓库原生 `appsettings.github.json`，**不覆盖**（避免上游更新后被旧副本回退）。
6. 两步发布（先 UI 再核心单文件，`--self-contained` + `PublishTrimmed` + `PublishSingleFile`）→ 自带运行时、免安装。
7. **修正 dnscrypt-proxy 目录命名**：代码期望 `dnscrypt-proxy/`，但仓库目录是 `@dnscrypt-proxy/`，脚本把 `win-x64/dnscrypt-proxy.exe` + `dnscrypt-proxy.toml` 拷成 `dnscrypt-proxy/`，否则 DNS 防污染会静默失效（降级到 FallbackDns，仍可加速）。
8. **防御 WinDivert 原生库**：单文件下 `WinDivert64.sys`/`WinDivert.dll` 可能只在 `runtimes/win-x64/native/`，脚本将其补到 exe 同级（驱动必须挨着 `WinDivert.dll` 才能加载）。
9. 创建 `appsettings/disabled/` 目录（停用站点片段存放处，引擎不扫描该子目录）并 `Compress-Archive` 打包成 zip。

> 产物：`dist/FastGithub-Portable-win-x64.zip`。解压后目录含 `FastGithub.UI.exe`（界面启动器）、`fastgithub.exe`（核心）、`appsettings/`、`dnscrypt-proxy/`、`WinDivert64.sys` 等。

### 手动构建（等价于脚本，便于排错）

```powershell
git clone --depth 1 https://github.com/creazyboyone/FastGithub.git src
Copy-Item appsettings.huggingface.json src\FastGithub\appsettings\   # 仅注入新增配置；github 配置为仓库原生
dotnet publish -c Release -o dist\fastgithub_win-x64 src\FastGithub.UI\FastGithub.UI.csproj
dotnet publish -c Release -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained -r win-x64 -o dist\fastgithub_win-x64 src\FastGithub\FastGithub.csproj
# 然后执行脚本里的第 4、5 步（dnscrypt 改名 + WinDivert 防御拷贝）
```

> 说明：官方 `publish.cmd` 用 `PublishTrimmed`（非 AOT）。**建议沿用 Trimmed** 而非 AOT——FastGithub 用了 `Dictionary<string,DomainConfig>` 的反射（`Startup` 里有 `[DynamicDependency]` 标注），AOT 易因裁剪导致配置反序列化失败。Trimmed 自包含已满足「免安装」。

---

## 5. 运行

1. **以管理员身份**运行解压目录里的 `FastGithub.UI.exe`（WinDivert 内核驱动需提权；首次会安装驱动，随进程卸载）。
   - UI（`FastGithub.UI.exe`）基于 **.NET Framework 4.5（WPF）**，Windows 10/11 自带、无需另行安装 .NET 7；第三方依赖 LiveCharts / Newtonsoft.Json 已作为**内嵌资源**打进 exe（运行时由 `AppDomain.AssemblyResolve` 从资源流加载），包内无需额外 dll 文件。
2. 程序自动把本机 443 流量经 WinDivert 引入本地代理；**不修改系统代理设置**。
3. **信任本地 CA**：FastGithub 为每台机器生成自签 CA，存于 `cacert/` 文件夹。浏览器/系统需信任该根证书才能访问 https（按程序提示或手动导入「受信任根证书颁发机构」）。
   - ?? 私钥仅在本地，请勿外泄。
4. 验证：
   - 浏览器开 `https://github.com` 正常且快。
   - 浏览器开 `https://huggingface.co` → 实际经 `hf-mirror.com` 镜像返回，速度提升。
   - 同时开其他软件（游戏/视频/其它网站）网络照常 —— 不匹配的域名不被拦截。

### 加速控制面板（UI 新页面）

主界面新增「**加速**」标签页，提供图形化控制（无需手改配置、无需命令行）：

- **启动 / 停止加速**：一键启停底层 `fastgithub.exe` 引擎进程。停止后流量恢复直连（WinDivert 拦截随进程退出解除），UI 与托盘常驻。停止采用**优雅停机**：UI 以 `ping -t 127.0.0.1` 锚点进程伪装成 fastgithub 的「父进程」，停止时杀掉锚点触发 fastgithub 走 `host.StopAsync()` 路径清理 `dnscrypt-proxy` 子进程，避免硬杀导致的孤儿进程残留；若 5s 内未自行退出则兜底强杀。（早期曾用 `timeout.exe` 作锚点，但在「GUI 子进程 + 无控制台」环境下会读 stdin 失败立即退出，误触发停机，已改为不读 stdin 的 `ping`。）
- **加速网址清单（Watt Toolkit 风格）**：列出所有可加速站点（GitHub、HuggingFace、Google、Microsoft、AWS、Fastly、Imgur、BootCDN、Packages、V2EX），勾选即启用、取消即停用。实现方式：把对应 `appsettings.<站点>.json` 片段在 `appsettings/`（启用）与 `appsettings/disabled/`（停用）间移动，并自动重启引擎生效。
- **HuggingFace 加速（镜像模式）**：`huggingface.co` 重定向到 `hf-mirror.com`，速度最优、最稳。无需切换，勾选即启用镜像（**已去除不稳定的主站直连 beta**）。

> 所有改动即时写入 `appsettings/` 目录并重启引擎；关闭软件时引擎随之退出。下次启动按文件现状恢复（勾选状态持久化在片段所在位置中）。

---

## 6. 如何增删站点（基于 DomainPattern）

`DomainPattern` 语法：`*` 表示除 `.` 之外的任意 0~多个字符。新增一个站点 = 新建 `appsettings.xxx.json`：

```json
{
  "FastGithub": {
    "DomainConfigs": {
      "example.com": { "TlsSni": false },
      "*.example.com": { "TlsSni": false, "TlsIgnoreNameMismatch": true }
    }
  }
}
```

`DomainConfig` 可用字段：
| 字段 | 含义 |
|---|---|
| `TlsSni` | 上游 TLS 是否发送 SNI。`false` = 不发（绕过 SNI 审查，仿 GitHub） |
| `TlsSniPattern` | 自定义 SNI 表达式：`@domain` / `@ipaddress` / `@random` |
| `TlsIgnoreNameMismatch` | 忽略服务器证书域名不匹配（CDN 默认证书场景） |
| `IPAddress` | 强制使用指定 IP |
| `Destination` | **镜像重定向目标**（绝对/相对 Uri），如 `https://hf-mirror.com` |
| `Timeout` | 请求超时，如 `"00:02:00"` |
| `Response` | 自定义拦截响应（设了它其它字段失效） |

两种加速策略：
- **SNI 伪装直连**（仿 GitHub）：`"TlsSni": false` —— 适合被 SNI 审查的站点。
- **镜像重定向**（仿 HF）：`"Destination": "https://镜像站"` —— 适合有国内镜像的站点，速度最优。

---

## 7. 安全与系统影响（已核实）

- **不影响其他流量**：WinDivert 按域名作用域拦截，不匹配包直接放行，且不改系统代理；代理进程崩溃只丢加速站点，不影响其它软件。
- **本地 CA 根证书**：程序为每台机器生成自签 CA 并需被信任，意味着本机具备对该 CA 覆盖域名的 MITM 能力。仅本地使用、私钥不外泄则安全。不要使用来路不明的远程配置。
- **内核驱动**：WinDivert 为内核态驱动，需管理员提权；仅在运行时加载，停止即卸载。（驱动二进制随 WindivertDotnet **内嵌于发布包**，zip 内看不到 `.sys` 属正常，首次运行由库自动解压安装——与官方 `fastgithub-win-x64.zip` 结构一致）
- **无系统代理冲突**：因不改系统代理，可与 Steam++/Watt Toolkit 等并存（但没必要同时开同类工具）。

---

## 8. 品牌化（可选）

- **程序名/图标**：改 `FastGithub.csproj` 的 `<AssemblyName>`、UI 项目 `FastGithub.UI` 的标题与图标。
- **默认端口**：`appsettings.json` 的 `HttpProxyPort`（默认 38457，Linux/macOS 用）。
- **日志/统计**：`/flowStatistics` 端点（见 `Startup.ConfigureApp`）。

---

## 9. 如何确认加速正常运行（验证清单）

构建并运行后，按以下顺序确认：

**GitHub 主站（核心）**
- 浏览器开 `https://github.com`：能正常打开、图片/API 加载正常（不再卡顿或超时）。
- 命令行测速：`git clone` 任意仓库，对比开启加速前后的耗时。
- 实时统计：浏览器开 `http://localhost:38457/flowStatistics`，查看 GitHub 相关域名的命中与流量。
- 若 `github.com` 仍超时：多为本地 CA 未信任（见第 5 节第 3 步）或管理员权限不足（WinDivert 驱动未加载）。

**HuggingFace（镜像重定向）**
- 浏览器开 `https://huggingface.co`：地址栏仍是 huggingface.co，但内容由 `hf-mirror.com` 提供（DevTools → Network 可见请求落到镜像域名）。
- 模型/数据集下载速度显著提升即说明重定向生效。
- 注意：`hf-mirror.com` 是**上游镜像、本身不被拦截加速**；我们加速的是 `huggingface.co`，靠重定向借道镜像提速。

**不影响其他流量（关键）**
- 同时开着游戏 / 视频 / 其它网站，网络照常——不匹配的域名不被 WinDivert 拦截。
- 若其它软件也变慢/断流：说明 WinDivert 过滤表达式或驱动异常，需排查（正常情况不应发生）。

---

## 10. 许可证

FastGithub 原仓库含 LICENSE（MIT 系）。**复用前请核对 `creazyboyone/FastGithub` 仓库当前 LICENSE 条款**；本目录配置文件按相同许可随附。

---

## 11. GitHub Actions 自动构建（CI）

仓库已配置 `.github/workflows/build.yml`：推送 `main`（或 tag、或手动）即在 GitHub 托管的 `windows-latest` runner 上自动执行 `build-portable.cmd`，产出 `dist/FastGithub-Portable-win-x64.zip`。

- **每次推送 `main` 构建成功都会同步发布 Release**：以 `ci-<运行号>` 为标签自动建版并附上 zip（Releases 页始终有最新构建）。
- **打 `v*` tag**（如 `v1.0.0`）则发布对应版本号 Release。
- 构建时自动把托盘右键"检测更新"跳转链接改写为本仓库 `Rickeal-Boss/GitHubplus`（`build-portable.cmd` 的 `[2b]` 步 patch `FastGithub.UI/MainWindow.xaml.cs` 的 `RELEASES_URI`）；并注入 UI 增强补丁（`[2c]` 步把 `src-patches/FastGithub.UI/` 覆盖进克隆源码，新增「加速」标签页与加速控制面板）。
- **取构件 / Release**：仓库 **Actions** 页 → 对应运行 → **Artifacts**；或 **Releases** 页直接下载。
- **手动触发**：Actions 页 → 选工作流 → **Run workflow**。
- **环境**：runner 通过 `actions/setup-dotnet` 装好 .NET 7 SDK；CI 只构建、不加载 WinDivert 驱动（无需管理员）。

## 12. 一句话总结

> 自建 = `git clone` FastGithub + 把 `appsettings.huggingface.json`（及原生 `appsettings.github.json`）放进 `FastGithub/appsettings/` + 运行 `build-portable.cmd` 出 Trimmed 自包含免安装包。
> 加速内核、MITM、本地 CA、DNS 优选、WinDivert 拦截**全部复用**，真正需要写的"代码"就那一个 JSON 文件。
