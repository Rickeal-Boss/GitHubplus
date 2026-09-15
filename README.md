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
| 不影响其他流量（性能 / 拦截层面） | WinDivert 包层按域名作用域拦截，不匹配走 `next()`，且**不改系统代理** | FastGithub 原生 |
| 信任层面：勾选 = 授权解密 | 勾选清单即**实际被本地 CA 解密**的范围；而 CA 本身的签发能力覆盖**所有域名**。详见第 7 节 | 本工具如实说明（上游既有行为） |
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
| `src-patches/FastGithub.HttpServer/Certs/CertService.cs` | 证书服务补丁：去掉强制关闭 git 全局校验（改切 `http.sslBackend schannel`）、CA 有效期 10 年 → 5 年 | **新增**（构建时覆盖进克隆源码） |
| `clean.cmd` | 卸载清理脚本：移除本机根存储中的 FastGithub CA + 恢复 git 配置 + 删除本地 CA 与私钥 | **新增**（需管理员运行） |
| `.gitattributes` | 强制 `*.cmd` / `*.bat` 以 CRLF 签出，规避 cmd.exe 对「中文 + LF」的解析 bug | **新增** |

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
2. **按已审计的固定 commit 精确拉取** FastGithub（含 `@dnscrypt-proxy` 目录，**非子模块**，无需 `--recurse-submodules`）。不再 `git clone --depth 1` 追最新代码——当前 pin 在 `f5425ec6750463f64f6b01d15d8010e0b53f94c4`（见脚本顶部 `UPSTREAM_COMMIT`），升级需显式改这一行。
3. **改写托盘「检测更新」链接**为本仓库 `Rickeal-Boss/GitHubplus`（`MainWindow.xaml.cs` 的 `RELEASES_URI`）。
4. **注入 UI 增强补丁**：把 `src-patches/FastGithub.UI/` 下的 `Program.cs`、`MainWindow.xaml`、`AcceleratorPanel.xaml(.cs)` 覆盖进 `FastGithub.UI/`，新增「加速」标签页与加速控制面板（启停开关 + 网址勾选 + HF 模式切换）。**不改 FastGithub 核心代码**。
5. **仅注入新增的 `appsettings.huggingface.json`** 到 `FastGithub/appsettings/`。GitHub 主站配置为仓库原生 `appsettings.github.json`，**不覆盖**（避免上游更新后被旧副本回退）。
6. 两步发布（先 UI 再核心单文件，`--self-contained` + `PublishTrimmed` + `PublishSingleFile`）→ 自带运行时、免安装。
7. **修正 dnscrypt-proxy 目录命名**：代码期望 `dnscrypt-proxy/`，但仓库目录是 `@dnscrypt-proxy/`，脚本把 `win-x64/dnscrypt-proxy.exe` + `dnscrypt-proxy.toml` 拷成 `dnscrypt-proxy/`，否则 DNS 防污染会静默失效（降级到 FallbackDns，仍可加速）。
8. **防御 WinDivert 原生库**：单文件下 `WinDivert64.sys`/`WinDivert.dll` 可能只在 `runtimes/win-x64/native/`，脚本将其补到 exe 同级（驱动必须挨着 `WinDivert.dll` 才能加载）。
9. 创建 `appsettings/disabled/` 目录（停用站点片段存放处，引擎不扫描该子目录）并 `Compress-Archive` 打包成 zip。
10. **默认站点收敛**：只保留 `github` 与 `huggingface`，其余片段（含高风险的 `packages` / `amazonaws`）一律移入 `appsettings/disabled/`，需用户在 UI 中显式勾选。
11. **构建期安全闸门**：移除任何预置 `cacert/` 目录；发布包中一旦检出 `*.key` 即硬失败终止构建；并断言源码中不存在 `GitConfigSslverify`。

> ⚠ **换行符**：`build-portable.cmd` / `clean.cmd` 必须以 **CRLF** 换行运行。含中文注释的 .cmd 若以 LF 换行，cmd.exe 会解析错乱（多行块被拆散、延迟变量失效）。仓库已配 `.gitattributes` 强制 CRLF 签出；如果你是手动复制文件内容或从 Raw 链接下载的，请确认换行符为 CRLF。

> 产物：`dist/FastGithub-Portable-win-x64.zip`。解压后目录含 `FastGithub.UI.exe`（界面启动器）、`fastgithub.exe`（核心）、`appsettings/`、`dnscrypt-proxy/`、`WinDivert64.sys` 等。

### 手动构建（等价于脚本，便于排错）

```powershell
# 注意：下面这行仅为排错演示，实际请用 build-portable.cmd——它会按已审计的
# UPSTREAM_COMMIT 精确拉取，而不是 --depth 1 追最新代码。
git clone --depth 1 https://github.com/creazyboyone/FastGithub.git src
git -C src checkout f5425ec6750463f64f6b01d15d8010e0b53f94c4
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
3. **信任本地 CA（请务必读完）**：FastGithub 为每台机器生成自签 CA，存于 `cacert/` 文件夹。
   - 首次运行会把该 CA 装进 Windows **根证书存储**（受信任的根证书颁发机构），**影响范围是本机所有 HTTPS 流量的信任链**，不只是 GitHub——实际被 MITM 解密的范围由你在加速页的勾选清单决定。
   - ⚠ **私钥明文存于 `cacert/fastgithub.key`。任何拿到它的人，都能解密你已勾选站点的 HTTPS 流量。请勿分发运行过的程序目录**（国内「绿色版二次分发」很常见，构建脚本拦不住下游）。
   - **本工具不再关闭 git 全局证书校验**：改为让 git 使用 Windows 系统证书存储（`http.sslBackend schannel`）；旧版本遗留的 `http.sslVerify=false` 会在启动时被自动清理。若你此前被旧版本改过，也可手动执行 `git config --global --unset http.sslVerify`。
   - **卸载 / 清理**：不再使用时，请以管理员身份运行仓库根目录的 `clean.cmd`（移除 CA + 恢复 git 配置 + 删除本地 CA 与私钥），详见 **7.1**。
4. 验证：
   - 浏览器开 `https://github.com` 正常且快。
   - 浏览器开 `https://huggingface.co` → 实际经 `hf-mirror.com` 镜像返回，速度提升。
   - 同时开其他软件（游戏/视频/其它网站）网络照常 —— 不匹配的域名不被拦截。

### 加速控制面板（UI 新页面）

主界面新增「**加速**」标签页，提供图形化控制（无需手改配置、无需命令行）：

- **启动 / 停止加速**：一键启停底层 `fastgithub.exe` 引擎进程。停止后流量恢复直连（WinDivert 拦截随进程退出解除），UI 与托盘常驻。停止采用**优雅停机**：UI 以 `ping -t 127.0.0.1` 锚点进程伪装成 fastgithub 的「父进程」，停止时杀掉锚点触发 fastgithub 走 `host.StopAsync()` 路径清理 `dnscrypt-proxy` 子进程，避免硬杀导致的孤儿进程残留；若 5s 内未自行退出则兜底强杀。（早期曾用 `timeout.exe` 作锚点，但在「GUI 子进程 + 无控制台」环境下会读 stdin 失败立即退出，误触发停机，已改为不读 stdin 的 `ping`。）
- **加速网址清单（Watt Toolkit 风格）**：列出所有可加速站点（GitHub、HuggingFace、Google、Microsoft、AWS、Fastly、Imgur、BootCDN、Packages、V2EX），勾选即启用、取消即停用。实现方式：把对应 `appsettings.<站点>.json` 片段在 `appsettings/`（启用）与 `appsettings/disabled/`（停用）间移动，并自动重启引擎生效。
  - **勾选 = 授权**：勾一个站点，就等于同意该域名的 HTTPS 流量被本机 CA 解密后转发。面板底部会实时显示「当前 N 个站点的流量将被本机 CA 解密」。
  - **默认只启用 GitHub 与 HuggingFace**；其余站点（含高风险的 **Packages** 与 **AWS**）默认停用，需要你显式勾选。Packages 实际覆盖 `*.nuget.org` / `*.maven.org`，包管理器信任系统证书存储，私钥泄露可导致供应链攻击，勾选时会弹一次风险确认。
  - **失败不再静默**：片段移动失败（常见于文件被占用）时会弹窗提示并把勾选状态回滚，详细信息写入同目录 `ui-error.log`，不会出现「以为生效了其实没生效」。
- **HuggingFace 加速（镜像模式）**：`huggingface.co` 重定向到 `hf-mirror.com`，速度最优、最稳。无需切换，勾选即启用镜像（**已去除不稳定的主站直连 beta**）。
- **界面背景（自定义 UI 背景）**：加速选项卡底部可「选择图片…」导入本地图片作为**全局 UI 背景**（四个选项卡内容改为透明以透出背景），「恢复默认」即回到原始背景。所选图片拷入 `ui-background/` 目录，路径持久化于 `ui-background.txt`，下次启动自动还原；图片加载失败或清空时自动回退默认背景。

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

- **「不影响其他流量」仅在性能 / 拦截层面成立**：WinDivert 按域名作用域拦截，不匹配包直接放行，且不改系统代理；代理进程崩溃只丢加速站点，不影响其它软件。**这一点在信任层面不成立**，见下一条。
- **本地 CA 与信任链**：程序为每台机器生成自签 CA，并装进 Windows **根证书存储**。装进去之后，本机就具备了对「该 CA 能覆盖的域名」的 MITM 能力——CA 本身的**签发能力覆盖所有域名**，勾选清单只决定**实际被解密**的范围。因此：

  > **勾选清单 = 你实际授权被本地 CA 解密的范围。**

- **私钥是明文文件**：`cacert/fastgithub.key` 未加密（`.NET` 的 `ImportFromPem` 读不了加密 PEM）。任何拿到它的人，都能解密你已勾选站点的 HTTPS 流量。
  - `packages`（NuGet / Maven）尤其危险：包管理器信任系统证书存储，也就信任了本 CA，私钥泄露可被用来植入可执行代码，形成**供应链攻击**。故默认停用，勾选时会二次确认。
  - **请勿分发运行过的程序目录**——目录里就躺着这把私钥。
- **git 全局证书校验不再被关闭**：本工具已把上游的 `git config --global http.sslVerify false` 改为让 git 使用 Windows 系统证书存储（`http.sslBackend schannel`），且**仅在你从未显式配置过 `sslBackend` 时才写**，不会每次重启都覆盖你的选择。旧版本遗留的 `http.sslVerify=false` 会被自动清理；若你此前被旧版本改过，可手动执行：

  ```bash
  git config --global --unset http.sslVerify
  git config --global --get  http.sslBackend   # 期望为空或 schannel
  ```

- **HuggingFace 走第三方镜像**：`huggingface.co` 的请求会被重定向到 `hf-mirror.com`，**带 token / cookie 的请求同样会经过该镜像**。不要在启用加速时提交你不希望第三方看到的私有资源请求（详见第 9 节）。
- **内核驱动**：WinDivert 为内核态驱动，需管理员提权；仅在运行时加载，停止即卸载。（驱动二进制随 WindivertDotnet **内嵌于发布包**，zip 内看不到 `.sys` 属正常，首次运行由库自动解压安装——与官方 `fastgithub-win-x64.zip` 结构一致）
- **无系统代理冲突**：因不改系统代理，可与 Steam++/Watt Toolkit 等并存（但没必要同时开同类工具）。
- **卸载与残留**：不再使用时请运行 `clean.cmd`（见 7.1）。不要使用来路不明的远程配置或二次分发的程序目录。

### 7.1 卸载与清理

不再使用时，请到**程序运行目录**以**管理员身份**运行仓库根目录的 `clean.cmd`（清理 `LocalMachine` 根证书存储必须提权，脚本会自检并在未提权时退出）。它依次做四件事：

1. **移除证书**：从 `Cert:\LocalMachine\Root` 与 `Cert:\CurrentUser\Root` 两处删除 Subject 含 `FastGithub` 的证书，并打印移除数量。
2. **恢复 git 配置**：`git config --global --unset http.sslVerify` 与 `--unset http.sslBackend`。（若你希望保留 Schannel 后端，可自行 `git config --global http.sslBackend schannel`。）
3. **删除本地 CA 与私钥**：删除 `cacert/` 目录（含 `fastgithub.key`）。
4. **回显残留**：打印 `http.sslVerify` / `http.sslBackend` 当前值，两行均无输出即表示已恢复默认。

> ⚠ 清理完也**不要分发运行过的程序目录**：`cacert\fastgithub.key` 是本机生成的 CA 私钥（明文）。换机器请用官方 Release 的干净压缩包重新解压。

### 7.2 已知残留与限制（上游行为，本工具未改动）

以下几条属于「上游既有行为 + 本工具暂未修」，但会影响你对本机关联影响的判断，如实列出：

| 项 | 说明 | 应对 |
|---|---|---|
| **hosts 文件被改写且不会自动恢复** | 引擎启动时会把命中加速域名的 hosts 行注释掉；上游的恢复逻辑是空实现，**停止加速后不会还原** | 若你原本自定义过 hosts，停止加速后请自行检查 `C:\Windows\System32\drivers\etc\hosts` |
| **引擎可能残留后台运行** | 若 UI 被强杀或崩溃，`fastgithub.exe` 可能继续在后台拦截 443 并写日志，而托盘图标已消失、用户无感知 | 在任务管理器中结束 `fastgithub.exe`；或重新打开 UI 点「停止加速」 |
| **覆盖式升级不收敛默认集** | 「默认只启用 GitHub 与 HuggingFace」只在**全新解压**时生效。若你在旧版本目录上直接覆盖解压，旧版已启用的站点片段仍留在 `appsettings/` 顶层继续生效 | 升级请用全新目录解压；或手动把不需要的 `appsettings.*.json` 移入 `appsettings/disabled/` |
| **日志无容量上限** | `logs/log.txt` 按天滚动、不清理，记录访问过的**域名与路径**（不含 query） | `clean.cmd` 会一并删除 `logs/` |

**私钥 ACL（可选自行加固）**：`cacert\fastgithub.key` 为明文，目录权限继承程序目录。若只想让当前用户与 SYSTEM 可读，可在程序目录执行：

```bat
icacls "cacert" /inheritance:r /grant:r "%USERNAME%:(OI)(CI)F" "SYSTEM:(OI)(CI)F"
```

> 不打算把私钥迁到 `%ProgramData%`：该目录默认继承的 ACL 反而更松（Everyone 可读），且写入需管理员权限，与免安装定位冲突。

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

**HuggingFace（镜像重定向）** — ⚠ 隐私提示
- 浏览器开 `https://huggingface.co`：地址栏仍是 huggingface.co，但内容由 `hf-mirror.com` 提供（DevTools → Network 可见请求落到镜像域名）。
- 模型/数据集下载速度显著提升即说明重定向生效。
- **注意：`hf-mirror.com` 是第三方镜像站，`huggingface.co` 的请求（含 header 里的 token / cookie）会经过它。** 启用 HF 加速时，请勿提交你不希望第三方看到的私有模型 / 数据集请求；涉及私有资源的，请先取消勾选 HuggingFace 或改用其它方式。
- 注意：`hf-mirror.com` 是**上游镜像、本身不被拦截加速**；我们加速的是 `huggingface.co`，靠重定向借道镜像提速。
- 另需注意：`*.huggingface.co` 配了 `TlsIgnoreNameMismatch: true`（重定向到 `hf-mirror.com` 所必需），该项的**域名校验实际是关闭的**。这是沿用上游 `google.json` 等片段的既有模式，并非本工具新增的风险类型，但值得知情。

**不影响其他流量（关键，仅性能 / 拦截层面）**
- 同时开着游戏 / 视频 / 其它网站，网络照常——不匹配的域名不被 WinDivert 拦截。
- 若其它软件也变慢/断流：说明 WinDivert 过滤表达式或驱动异常，需排查（正常情况不应发生）。

---

## 10. 许可证

FastGithub 原仓库含 LICENSE（MIT 系）。**复用前请核对 `creazyboyone/FastGithub` 仓库当前 LICENSE 条款**；本目录的**配置文件与 `src-patches/` 补丁沿用上游 MIT 许可（`Copyright (c) 2021 老九`）**，与上游保持一致。

---

## 11. GitHub Actions 自动构建（CI）

仓库已配置 `.github/workflows/build.yml`：推送 `main`（或 tag、或手动）即在 GitHub 托管的 `windows-latest` runner 上自动执行 `build-portable.cmd`，产出 `dist/FastGithub-Portable-win-x64.zip`。

- **每次推送 `main` 构建成功都会同步发布 Release**：以 `ci-<运行号>` 为标签自动建版并附上 zip（Releases 页始终有最新构建）。
- **打 `v*` tag**（如 `v1.0.0`）则发布对应版本号 Release。
- 构建时自动把托盘右键"检测更新"跳转链接改写为本仓库 `Rickeal-Boss/GitHubplus`（`build-portable.cmd` 的 `[2b]` 步 patch `FastGithub.UI/MainWindow.xaml.cs` 的 `RELEASES_URI`）；并注入 UI 增强补丁（`[2c]` 步把 `src-patches/FastGithub.UI/` 覆盖进克隆源码，新增「加速」标签页与加速控制面板）。
- **上游供应链已 pin**：上游 `creazyboyone/FastGithub` 已固定到已审计的 commit `f5425ec6750463f64f6b01d15d8010e0b53f94c4`（2025-08-01）。构建脚本 `build-portable.cmd` 只按这个 SHA 精确拉取，并在拉取后校验 `git rev-parse HEAD` 与之相等，不等即失败。**要升级上游版本，必须显式修改 `build-portable.cmd` 顶部的 `UPSTREAM_COMMIT`**——CI 里也有一道断言，改动该行会被校验。原 `dotnetcore/FastGithub` 已删库、现 fork 不再维护，切勿回到「追最新代码」的做法。
- **构建期安全断言**：构建前校验 `build-portable.cmd` 是否 pin 了上述 commit；构建后校验 `src/FastGithub.HttpServer/Certs/CertService.cs` 不含 `GitConfigSslverify`、`dist` 下无 `*.key`、无预置 `cacert/` 目录，任一不符即构建失败。
- **取构件 / Release**：仓库 **Actions** 页 → 对应运行 → **Artifacts**；或 **Releases** 页直接下载。
- **手动触发**：Actions 页 → 选工作流 → **Run workflow**。
- **环境**：runner 通过 `actions/setup-dotnet` 装好 .NET 7 SDK；CI 只构建、不加载 WinDivert 驱动（无需管理员）。

## 12. 一句话总结

> 自建 = `git clone` FastGithub + 把 `appsettings.huggingface.json`（及原生 `appsettings.github.json`）放进 `FastGithub/appsettings/` + 运行 `build-portable.cmd` 出 Trimmed 自包含免安装包。
> 加速内核、MITM、本地 CA、DNS 优选、WinDivert 拦截**全部复用**，真正需要写的"代码"就那一个 JSON 文件。

---

## 13. 使用声明

> 本节不再引用上游原仓库的「合法性说明」。该论证所依据的法规条文已被指出失效（见上游 issue #202），照抄等于把一份失效论证挂在本仓库上，故删除，仅保留中性声明。

- **用途**：本工具为**本地网络加速与开发提效**用途，用于改善 GitHub / HuggingFace 等开发相关站点的访问体验。
- **自负风险**：使用本工具会在本机安装一张自签 CA 并解密你所勾选站点的 HTTPS 流量（详见第 7 节）。请仅在你拥有管理权限、且了解该行为的机器上使用。
- **合规责任**：用户需**自行了解并遵守所在地区的法律法规与所在单位的网络使用规定**；因使用本工具产生的一切后果由使用者自行承担。
- **非法律意见**：本节仅为用途声明与风险提示，**不构成任何法律意见或合规承诺**。
