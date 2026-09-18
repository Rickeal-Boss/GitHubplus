@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul

:: 脚本基准目录（无论从哪里调用，路径都基于脚本自身位置，避免依赖当前工作目录）
set "SCRIPT_DIR=%~dp0"

:: ============================================================
:: 构建 Windows 免安装包（self-contained 单文件，解压即跑）
:: 前置：安装 .NET 10 SDK  https://dotnet.microsoft.com/download
:: 用法：把本文件与 appsettings.*.json、src-patches/ 放在同一目录，双击运行
:: 产物：dist\FastGithub-Portable-win-x64.zip
:: ============================================================

set "REPO=https://github.com/creazyboyone/FastGithub.git"
:: [供应链防篡改] 已人工审计的上游 commit（2025-08-01「优化下版本字符串解析」）。
:: 不再 `git clone --depth 1` 追最新代码——上游一旦被改，恶意二进制会经本仓库
:: Release 直接分发给所有用户。升级上游版本时必须显式修改下面这一行。
set "UPSTREAM_COMMIT=f5425ec6750463f64f6b01d15d8010e0b53f94c4"
:: 默认启用的站点片段（改这一行即可调整默认集）：
::   github      —— 本工具的核心目标，必须默认开
::   huggingface —— 本工具的第二目标，必须默认开
:: 其余站点（含高风险的 packages / amazonaws）默认停用，需用户在 UI 中显式勾选。
set "DEFAULT_SITES=github huggingface"
set "SRC=%SCRIPT_DIR%src"
set "DIST=%SCRIPT_DIR%dist"
set "PKG=%DIST%\fastgithub_win-x64"
set "PATCHES=%SCRIPT_DIR%src-patches\FastGithub.UI"

echo [前置] 检测 .NET 10 SDK
where dotnet >nul 2>&1 || (echo [错误] 未检测到 dotnet，请先安装 .NET 10 SDK（https://dotnet.microsoft.com/download） & goto :fail)
for /f "tokens=*" %%v in ('dotnet --version') do set "DOTNET_VER=%%v"
echo   检测到的 dotnet 版本：%DOTNET_VER%
where git >nul 2>&1 || (echo [错误] 未检测到 git，请先安装 Git for Windows（https://git-scm.com/download/win） & goto :fail)

echo [1/6] 清理并准备目录
if exist "%DIST%" rd /S /Q "%DIST%"
mkdir "%PKG%"

echo [2/6] 拉取上游源码（按已审计 commit 精确 pin：%UPSTREAM_COMMIT%；失败自动重试）
set "CLONE_TRY=0"
:clone_retry
if exist "%SRC%" rd /S /Q "%SRC%"
mkdir "%SRC%" 2>nul
git -C "%SRC%" init -q 2>nul
git -C "%SRC%" remote add origin "%REPO%" 2>nul
:: git-for-Windows 在 checkout 完成后偶发“The system cannot find the path specified”并以 exit 1 退出，
:: 但文件实际已就位；故不以 exit code 判生死，而以关键工程文件是否存在 + 版本锚点是否一致判成功。
git -C "%SRC%" -c http.version=HTTP/1.1 -c http.postBuffer=524288000 fetch --depth 1 --filter=blob:none origin %UPSTREAM_COMMIT% 2>nul
git -C "%SRC%" checkout -q FETCH_HEAD 2>nul
set "HEAD_SHA="
for /f "usebackq tokens=*" %%h in (`git -C "%SRC%" rev-parse HEAD 2^>nul`) do set "HEAD_SHA=%%h"
if exist "%SRC%\FastGithub.UI\FastGithub.UI.csproj" if exist "%SRC%\FastGithub\FastGithub.csproj" if /I "!HEAD_SHA!"=="%UPSTREAM_COMMIT%" goto clone_done
set /a CLONE_TRY+=1
if !CLONE_TRY! LSS 5 (
    echo   [警告] 拉取失败或版本锚点不匹配（期望 %UPSTREAM_COMMIT%，实际 !HEAD_SHA!），5 秒后重试（!CLONE_TRY!/5）…
    ping -n 6 127.0.0.1 >nul
    goto clone_retry
)
echo [错误] 拉取上游源码失败，或版本锚点不等于已审计 commit，请检查网络后重试。
goto :fail
:clone_done
echo   [OK] 上游源码已就绪（%SRC%）@ %UPSTREAM_COMMIT%

echo [2b/6] 注入主窗体补丁（检测更新链接指向本仓库 + 拆开最小化/关闭语义）
:: 原实现用 PowerShell 对上游 MainWindow.xaml.cs 做字符串替换改链接，上游改格式就会失效；
:: 改为整文件覆盖（与 AcceleratorPanel 同样的做法），并额外修掉「最小化等同关闭」的问题。
if not exist "%PATCHES%\MainWindow.xaml.cs" (echo [错误] 缺少 src-patches\FastGithub.UI\MainWindow.xaml.cs & goto :fail)
copy /Y "%PATCHES%\MainWindow.xaml.cs" "%SRC%\FastGithub.UI\MainWindow.xaml.cs" || goto :fail
powershell -NoProfile -Command "$p='%SRC%\FastGithub.UI\MainWindow.xaml.cs'; if (-not (Select-String -Path $p -Pattern 'Rickeal-Boss/GitHubplus' -Quiet)) { Write-Error '主窗体补丁未生效：检测更新链接未指向本仓库'; exit 1 }"
if errorlevel 1 goto :fail

echo [2c/6] 注入 UI 增强（加速控制页 + 进程启停控制 + 主窗体新增“加速”标签）
copy /Y "%PATCHES%\Program.cs" "%SRC%\FastGithub.UI\Program.cs" || goto :fail
copy /Y "%PATCHES%\MainWindow.xaml" "%SRC%\FastGithub.UI\MainWindow.xaml" || goto :fail
copy /Y "%PATCHES%\AcceleratorPanel.xaml" "%SRC%\FastGithub.UI\AcceleratorPanel.xaml" || goto :fail
copy /Y "%PATCHES%\AcceleratorPanel.xaml.cs" "%SRC%\FastGithub.UI\AcceleratorPanel.xaml.cs" || goto :fail

echo [2d] 注入证书服务补丁（移除强制关闭 git 全局校验 + CA 有效期缩短为 5 年）
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\Certs\CertService.cs" "%SRC%\FastGithub.HttpServer\Certs\CertService.cs" || goto :fail

echo [2e] 安全断言：源码中不得再出现 GitConfigSslverify
findstr /S /I /M /C:"GitConfigSslverify" "%SRC%\FastGithub.HttpServer\*.cs" >nul 2>nul
if not errorlevel 1 (
    echo [错误] 检出 GitConfigSslverify（强制关闭 git 全局证书校验），构建终止
    goto :fail
)
echo   [OK] 未检出 GitConfigSslverify

echo [2f] 固定 NuGet 版本 + 迁移到 .NET 10 LTS
:: 上游 FastGithub.csproj 第 12-13 行把 Microsoft.Extensions.Hosting.Systemd / WindowsServices
:: 写成 Version="7.0.0-rc*" —— 浮动到预发布通道。.NET 7 已于 2024-05-14 EOL，
:: 且 self-contained 发布会把无补丁运行时打进产物。现固定为 10.0.12（LTS，EOL 2028-11，
:: 当前 10.x 最新稳定版；10.0.0 不存在于 NuGet，10.x 从 10.0.8 起）。
:: 注意：UPSTREAM_COMMIT 只锁住「上游源码」这一个输入，NuGet 依赖是第二个未锁定的输入。
:: 彻底做法是生成 packages.lock.json 并用 dotnet restore --locked-mode，列为后续项。
powershell -NoProfile -Command "$p='%SRC%\FastGithub\FastGithub.csproj'; $c=Get-Content -Raw $p; $c=$c.Replace('7.0.0-rc*','10.0.12'); $c | Set-Content $p -Encoding utf8; if ($c -match 'rc\*') { Write-Error '浮动版本未消除'; exit 1 }"
if errorlevel 1 goto :fail
echo   [OK] 已将 7.0.0-rc* 固定为 10.0.12

echo [2ac] 修复 Yarp.ReverseProxy 高危依赖（1.1.1 -> 1.1.2；GHSA-jrjw-qgr2-wfcg / CVE-2023-33141）
:: 上游 FastGithub.HttpServer.csproj 直接引用 Yarp.ReverseProxy 1.1.1（受影响范围 <=1.1.1），
:: 存在 DoS（CWE-400 不受控资源消耗，CVSS 7.5）。1.x 线已有修复版 1.1.2，故保持在 1.x 内升级，
:: 不跨到 2.x（1.x -> 2.x 是破坏性变更，上游 HttpReverseProxyMiddleware 与本仓库补丁的
:: RequestLoggingMilldeware 都依赖 1.x 的 HttpForwarder / IHttpForwarder / ForwarderRequestConfig）。
:: 注：PowerShell 里用 [char]34 拼出双引号，避免在 cmd 的 "..." 参数里再嵌 " 导致解析错乱。
powershell -NoProfile -Command "$q=[char]34; $old='Yarp.ReverseProxy'+$q+' Version='+$q+'1.1.1'; $new='Yarp.ReverseProxy'+$q+' Version='+$q+'1.1.2'; $p='%SRC%\FastGithub.HttpServer\FastGithub.HttpServer.csproj'; $c=Get-Content -Raw $p; $c=$c.Replace($old,$new); $c | Set-Content $p -Encoding utf8; $r=Get-Content -Raw $p; if ($r.Contains($old) -or ($r.Contains($new) -eq $false)) { Write-Error 'Yarp.ReverseProxy 未成功升到 1.1.2'; exit 1 }"
if errorlevel 1 goto :fail
echo   [OK] 已将 Yarp.ReverseProxy 1.1.1 升到 1.1.2

echo [2ad] 修复 Microsoft.Extensions.Caching.Memory 高危依赖（6.0.1 -> 10.0.12；GHSA-qj66-m88j-hmgj / CVE-2024-43483）
:: 上游 FastGithub.DomainResolve.csproj 直接引用 Microsoft.Extensions.Caching.Memory 6.0.1
:: （受影响范围 <=6.0.1），存在哈希洪泛导致的 DoS。项目已统一为 net10.0，故升到 10.0.12，
:: 与 FastGithub.csproj 已 pin 的 Microsoft.Extensions.Hosting 10.0.12 对齐，避免 6.x/10.x 混装；
:: 本仓库补丁用的 AddMemoryCache(o => o.SizeLimit = ...) 在 6.x/8.x/10.x 均兼容。
powershell -NoProfile -Command "$q=[char]34; $old='Microsoft.Extensions.Caching.Memory'+$q+' Version='+$q+'6.0.1'; $new='Microsoft.Extensions.Caching.Memory'+$q+' Version='+$q+'10.0.12'; $p='%SRC%\FastGithub.DomainResolve\FastGithub.DomainResolve.csproj'; $c=Get-Content -Raw $p; $c=$c.Replace($old,$new); $c | Set-Content $p -Encoding utf8; $r=Get-Content -Raw $p; if ($r.Contains($old) -or ($r.Contains($new) -eq $false)) { Write-Error 'Caching.Memory 未成功升到 10.0.12'; exit 1 }"
if errorlevel 1 goto :fail
echo   [OK] 已将 Microsoft.Extensions.Caching.Memory 6.0.1 升到 10.0.12

echo [2g] 注入服务注册补丁（证书缓存加容量上限 + 淘汰时 Dispose）
:: 上游 ServiceCollectionExtensions.cs 的 AddReverseProxy() 用 .AddMemoryCache() 无 SizeLimit，
:: 而 CertService 以域名为 key 缓存 X509Certificate2（非托管句柄），访问大量不同子域会无界增长。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\ServiceCollectionExtensions.cs" "%SRC%\FastGithub.HttpServer\ServiceCollectionExtensions.cs" || goto :fail

echo [2h] 注入构建属性补丁（TargetFramework net7.0 -> net10.0 LTS）
:: 上游 Directory.Build.props 全局 net7.0（EOL），self-contained 发布会内嵌无补丁运行时。
:: FastGithub.UI.csproj 显式 net45，不受 Directory.Build.props 覆盖。
copy /Y "%SCRIPT_DIR%src-patches\Directory.Build.props" "%SRC%\Directory.Build.props" || goto :fail

echo [2i] 收窄 CA 与叶子证书的 EKU（移除 tlsClientOid）
:: 上游 CertGenerator.cs 在 CA 与叶子证书的 EKU 里同时含 tlsServerOid + tlsClientOid。
:: MITM 代理只需 tlsServer，含 tlsClient 意味着私钥泄露后攻击者可伪造客户端 mTLS 证书，
:: 扩大攻击面（某些企业环境用客户端证书做身份认证）。
powershell -NoProfile -Command "$p='%SRC%\FastGithub.HttpServer\Certs\CertGenerator.cs'; $c=Get-Content -Raw $p; $c=$c.Replace('new OidCollection { tlsServerOid, tlsClientOid }','new OidCollection { tlsServerOid }'); $c | Set-Content $p -Encoding utf8; if ($c -match 'tlsServerOid, tlsClientOid') { Write-Error 'EKU 收窄失败：仍含 tlsClientOid'; exit 1 }"
if errorlevel 1 goto :fail
echo   [OK] 已移除 CA 与叶子证书的 tlsClientOid


echo [2j] 注入域名解析补丁（解析全失败时负缓存 30 秒 + 下发 IP 截断为 MAX_IP_COUNT）
:: 缓存命中条件是 addresses.Length > 0，而全失败时写入的是空数组 -> 空数组永不命中，
:: 于是每个请求都重走「DNS 解析 + 竞速 + 串行连接全部候选 IP」（单 IP 超时 10 秒），
:: 直接放大成重试风暴。故加 30 秒负缓存，并把下发列表截断到 MAX_IP_COUNT 个。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.DomainResolve\DomainResolver.cs" "%SRC%\FastGithub.DomainResolve\DomainResolver.cs"
if errorlevel 1 goto :fail

echo [2k] 注入测速周期补丁（后台测速周期 1s -> 15s）
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.DomainResolve\DomainResolveHostedService.cs" "%SRC%\FastGithub.DomainResolve\DomainResolveHostedService.cs"
if errorlevel 1 goto :fail

echo [2l] 注入拨测并发补丁（并发拨测上限 8 + 整轮拨测串行化）
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.DomainResolve\IPAddressService.cs" "%SRC%\FastGithub.DomainResolve\IPAddressService.cs"
if errorlevel 1 goto :fail

echo [2m] 注入日志容量补丁（单文件 20MB 滚动 + 最多保留 7 个）
:: 上游 WriteTo.File 只配了 rollingInterval: Day，没有大小与数量上限
copy /Y "%SCRIPT_DIR%src-patches\FastGithub\Startup.cs" "%SRC%\FastGithub\Startup.cs"
if errorlevel 1 goto :fail

echo [2n] 注入 FallbackDns 补丁（去掉国内不可达的 8.8.8.8，补 223.5.5.5）
:: 8.8.8.8 在国内基本不可达，且数组按序优先 -> 每次 fallback 先吃一轮超时
copy /Y "%SCRIPT_DIR%src-patches\FastGithub\appsettings.json" "%SRC%\FastGithub\appsettings.json"
if errorlevel 1 goto :fail

echo [2o] 注入端口占用检测补丁（保留监听的 Address，避免回环 443 被其它网卡 IP 误判为占用）
:: 原实现只取 endpoint.Port 丢掉 Address：有任何进程监听 0.0.0.0:443 或某网卡IP:443 时，
:: 回环 443 也被判为占用 -> HttpsPort 漂到 444 -> WinDivert 的 TCP 重定向不区分目的IP，
:: 会把回环上所有目标 443 的包无差别改写到 444。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.Configuration\GlobalListener.cs" "%SRC%\FastGithub.Configuration\GlobalListener.cs"
if errorlevel 1 goto :fail

echo [2p] 注入CA删除补丁（FindBySubjectName 子串匹配改为精确匹配+自签名判定）
:: 子串匹配会对 LocalMachine\Root 里任何主题含 "FastGithub" 的证书执行 store.Remove，
:: 误删第三方根证书会导致相关站点证书校验全部失败。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\Certs\CaCertInstallers\CaCertInstallerOfWindows.cs" "%SRC%\FastGithub.HttpServer\Certs\CaCertInstallers\CaCertInstallerOfWindows.cs"
if errorlevel 1 goto :fail

echo [2q] 注入服务镜像路径校验补丁（拒绝在普通用户可写目录安装 SYSTEM 权限服务）
:: 程序解压在 Downloads/用户目录时，服务镜像指向普通用户可替换的 exe，
:: 下次开机即以 SYSTEM 权限执行被替换的二进制（本地提权）。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub\ServiceExtensions.cs" "%SRC%\FastGithub\ServiceExtensions.cs"
if errorlevel 1 goto :fail

echo [2r] 注入TLS白名单补丁（发送了SNI但不在加速白名单内的域名一律中止握手）
:: 原实现对任意 SNI 都签发解密证书，安全完全依赖 DNS 投毒白名单与 TCP 过滤器两道前置闸门；
:: 流量一旦绕过闸门进入监听器就是任意域名解密。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\KestrelServerExtensions.cs" "%SRC%\FastGithub.HttpServer\KestrelServerExtensions.cs"
if errorlevel 1 goto :fail

echo [2s] 注入WinDivert文件自愈补丁（哈希与基线不符即删除，下次运行重新从内嵌资源释放）
:: WinDivert.dll 无数字签名且位于 %APPDATA%\WindivertDotnet（用户可写），
:: 被替换后在提权进程内 LoadLibrary 即管理员代码执行；上游释放逻辑是「存在即跳过」，永不复查。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub\Program.cs" "%SRC%\FastGithub\Program.cs"
if errorlevel 1 goto :fail

echo [2t] 注入ProxyOverride回滚补丁（按自己的记录精确回滚，取消勾选不再残留）
:: 原实现用 Except(DomainConfigs.Keys) 做移除：用户取消勾选某站点后该域名就不在
:: DomainConfigs 里了，于是永久留在系统代理绕过列表（真机实测残留 34 条，其中 16 条是 GitHub 条目）。
:: 新实现用注册表值 FastGithubProxyOverrideOwned 记录自己写入过的条目并按记录精确回滚。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.PacketIntercept\Dns\ProxyConflictSolver.cs" "%SRC%\FastGithub.PacketIntercept\Dns\ProxyConflictSolver.cs"
if errorlevel 1 goto :fail

echo [2u] 注入流量图补丁（控件不可见时不轮询 /flowStatistics）
:: MainWindow.xaml 内联构造了 FlowChart 控件，即使从不打开流量页，它也会每秒请求一次
:: http://localhost/flowStatistics（穿 Kestrel + 各中间件 + JSON 序列化 + 图表重绘，86400 次/天）。
:: 改为控件不可见时只等待、不发请求，用户切到流量页后立刻恢复刷新。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.UI\FlowChart.xaml.cs" "%SRC%\FastGithub.UI\FlowChart.xaml.cs"
if errorlevel 1 goto :fail

echo [2v] 注入请求日志补丁（错误路径只记一行，完整堆栈降级到 Debug）
:: 原实现把整个 AggregateException 展开写进 Error 日志，实测占日志总字节的 59.8%（平均 3639 B/条）。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\HttpMiddlewares\RequestLoggingMilldeware.cs" "%SRC%\FastGithub.HttpServer\HttpMiddlewares\RequestLoggingMilldeware.cs"
if errorlevel 1 goto :fail

echo [2w] 注入 GitHub collector 片段（collector.github.com 直接返回 204）
:: AppCenter / VS 遥测会周期性向 collector.github.com 发 POST；改为本地直接 204 空响应，
:: 既省去解密与转发，也消除无用外联。该片段整体覆盖仓库原生的 appsettings.github.json。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub\appsettings\appsettings.github.json" "%SRC%\FastGithub\appsettings\appsettings.github.json"
if errorlevel 1 goto :fail

echo [2x] 注入TomlUtil原子写补丁（先写临时文件，再原子替换）
:: 原实现直接 File.WriteAllTextAsync 覆盖 dnscrypt-proxy.toml：进程在写入中途被终止
:: （关加速、崩溃、关机）会残留半截配置，下次启动 dnscrypt-proxy 因配置损坏直接失败。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.DomainResolve\TomlUtil.cs" "%SRC%\FastGithub.DomainResolve\TomlUtil.cs"
if errorlevel 1 goto :fail

echo [2y] 注入 hosts 编码保真 + 原子写补丁（HostsConflictSolver）
:: 上游用 StreamReader 默认解码（UTF-8）读 hosts，中国区 ANSI(GBK) 的 hosts 会被读成乱码再写回 -> 损坏文件；
:: 且直接 File.WriteAllTextAsync 覆盖，进程中断即残留半截 hosts。现按原始字节判编码（BOM 决定 UTF-8 / ANSI），
:: 同目录 .tmp + File.Move 原子替换，改前先把回滚记录写进 HKLM\SOFTWARE\FastGithub 供崩溃自愈恢复。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.PacketIntercept\Dns\HostsConflictSolver.cs" "%SRC%\FastGithub.PacketIntercept\Dns\HostsConflictSolver.cs"
if errorlevel 1 goto :fail

echo [2z] 注入 dnscrypt 路径绝对化 + Job Object 兜底补丁（DnscryptProxy）
:: 上游用相对路径（依赖 Program.Main 把 CWD 设为 exe 目录），CWD 一变即可被投放同名文件劫持；
:: 现基于 Environment.ProcessPath 拼绝对路径；并把 dnscrypt-proxy 纳入 KILL_ON_JOB_CLOSE 的 Job Object，
:: 引擎无论优雅停机/强杀/崩溃都不会遗留孤儿进程；Stop() 补 WaitForExit(3000) 防止端口未释放。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.DomainResolve\DnscryptProxy.cs" "%SRC%\FastGithub.DomainResolve\DnscryptProxy.cs"
if errorlevel 1 goto :fail

echo [2aa] 注入服务安装目录白名单补丁（ServiceInstallUtil 下沉校验）
:: 上游 ServiceExtensions 用黑名单（漏 C:\temp / ProgramData 等用户可写目录），且 dnscrypt 直接调
:: InstallAndStartService 绕过了该调用侧校验。现改为白名单（仅 Program Files / Program Files (x86)，
:: 用 SpecialFolder 枚举规避环境变量重定向陷阱），并把判定下沉进 InstallAndStartService 内部。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.DomainResolve\ServiceInstallUtil.cs" "%SRC%\FastGithub.DomainResolve\ServiceInstallUtil.cs"
if errorlevel 1 goto :fail

echo [2ab] 注入 TLS 入侵中间件补丁（裸 catch{} 改为记 Debug）
:: 上游 IsTlsConnectionAsync 的裸 catch 会静默吞掉一切异常，真出问题无从排查；
:: 改为 catch 后记一条 Debug（返回语义不变，仍按“非 tls”处理）。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\TlsMiddlewares\TlsInvadeMiddleware.cs" "%SRC%\FastGithub.HttpServer\TlsMiddlewares\TlsInvadeMiddleware.cs"
if errorlevel 1 goto :fail

echo [2az] 注入父进程监控补丁（锚点已死是预期分支，降 Information 而非 Error）
:: 上游 AppHostedService.WaitForParentProcessExitAsync 把 GetProcessById 的一切异常记 Error；
:: 其中 ArgumentException（进程不存在）在 UI 快速重启场景是预期路径（锚点 ping 已随上一个
:: UI 实例退出而死亡），真机 pre17 日志 10 秒内连续 4 条 ERR 即来源于此；且上游不释放
:: GetProcessById 返回的 Process 句柄。补丁：ArgumentException 单独降为 Information + using 释放。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub\AppHostedService.cs" "%SRC%\FastGithub\AppHostedService.cs" || goto :fail

echo [2ae] 注入配置绑定防裁剪补丁（FastGithubOptions.DomainConfigs 标注 DynamicallyAccessedMembers）
:: PublishTrimmed 会裁掉「仅被反射访问」的成员，而 ConfigurationBinder 是纯反射绑定：DomainConfig/
:: ResponseConfig 为 record + init-only 属性，setter 被裁后绑定器静默保留默认值（键在、值全丢，
:: Response/Destination/TlsIgnoreNameMismatch/TlsSni 失效且不抛异常）。本步在 DomainConfigs 属性上
:: 显式要求保留其泛型实参 DomainConfig 的所有成员，与 [2m] Startup.cs 的 [DynamicDependency]、
:: [2h] Directory.Build.props 的 TrimmerRootAssembly(FastGithub.Configuration) 互为多保险。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.Configuration\FastGithubOptions.cs" "%SRC%\FastGithub.Configuration\FastGithubOptions.cs"
if errorlevel 1 goto :fail

echo [2af] 注入出站 TLS 链校验补丁（修复 TlsIgnoreNameMismatch 连带放弃链校验 → 自签证书即可 MITM）
:: 上游 ValidateServerCertificate 先判 HasFlag(RemoteCertificateNameMismatch) 再直接 return true。
:: 而自签/伪造证书会同时置上 NameMismatch|ChainErrors 两个标志，HasFlag(NameMismatch) 成立即放行，
:: 等于对配置了 TlsIgnoreNameMismatch 的域名完全放弃证书链校验。该配置在默认启用的 github 片段里
:: 覆盖 *.github.com / *.githubusercontent.com / *.githubassets.com / *.github.io / *.githubapp.com /
:: gist.github.com，即 raw 下载、Release 附件与头像资源全部可被一张自签证书投毒。
:: 这与设计意图相悖（本意是「只放行链有效但域名不匹配」），补丁改为链错误最先拒绝。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.Http\HttpClientHandler.cs" "%SRC%\FastGithub.Http\HttpClientHandler.cs"
if errorlevel 1 goto :fail

echo [2ag] 注入 ssh/git 反向代理 Socket 句柄泄漏补丁（ownsSocket: false → true）
:: 上游成功分支直接 return new NetworkStream(socket, ownsSocket: false)，既不会被 catch 里的
:: socket.Dispose() 覆盖，也不会在 using var connection 释放流时连带释放 Socket。
:: 结果每建立一条 ssh(:22)/git(:9418) 代理连接就泄漏一个句柄，只能等终结器兜底。
:: 同目录 TunnelMiddleware 用的就是 ownsSocket: true，这里对齐。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\TcpMiddlewares\TcpReverseProxyHandler.cs" "%SRC%\FastGithub.HttpServer\TcpMiddlewares\TcpReverseProxyHandler.cs"
if errorlevel 1 goto :fail

echo [3/6] 注入加速配置（HuggingFace 镜像；GitHub collector 片段已在 [2w] 覆盖）
copy /Y "%SCRIPT_DIR%appsettings.huggingface.json" "%SRC%\FastGithub\appsettings\" || goto :fail

echo [4/6] 发布（官方两步法：先 UI，再核心单文件，输出到同一目录）
dotnet publish -c Release -o "%PKG%" "%SRC%\FastGithub.UI\FastGithub.UI.csproj"
if errorlevel 1 goto :fail
dotnet publish -c Release -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained -r win-x64 -o "%PKG%" "%SRC%\FastGithub\FastGithub.csproj"
if errorlevel 1 goto :fail

echo [5/6] 修正 dnscrypt-proxy 目录命名（代码期望 dnscrypt-proxy/，仓库是 @dnscrypt-proxy/）
if not exist "%PKG%\dnscrypt-proxy" mkdir "%PKG%\dnscrypt-proxy"
:: [PATCH] 原实现复制失败只打警告继续（fail-open）。DNS 防污染是安全功能，缺了应硬失败。
copy /Y "%SRC%\@dnscrypt-proxy\win-x64\dnscrypt-proxy.exe" "%PKG%\dnscrypt-proxy\"
if errorlevel 1 (echo [错误] dnscrypt-proxy.exe 未复制，DNS 防污染将失效，终止构建 & goto :fail)
copy /Y "%SRC%\@dnscrypt-proxy\dnscrypt-proxy.toml"        "%PKG%\dnscrypt-proxy\"
if errorlevel 1 (echo [错误] dnscrypt-proxy.toml 未复制，终止构建 & goto :fail)

echo [5i] dnscrypt 源去自指：摘掉自引用源，删除从未使用的 relays 源段
:: [PATCH] dnscrypt-proxy 启动时会拉源列表（urls），其中第一项是 raw.githubusercontent.com；
:: 该域名解析走系统 DNS(udp 53)，而它命中 *.githubusercontent.com 加速配置，会被我们自己的
:: WinDivert 投毒到 127.0.0.1 —— 于是形成「dnscrypt 拉源 -> 本机 443 反代 -> 再解析该域名 ->
:: 卡在 IPv6 黑洞」的自指死锁（真机 pre14：relays.md 请求 29.8 秒后返回 400）。
:: 这里在 toml 已落到发布目录之后做后处理（不是改仓库里的模板）：
::   a) 从 urls 数组中摘掉自引用项，保留 download.dnscrypt.info / .net 等镜像；
::   b) 整段删除 sources.relays —— [anonymized_dns] 的 routes 全部被注释，relays 从未被使用，
::      本次 400 正是由它触发，删掉即省掉一次注定失败的请求。
:: 段名必须两种写法都认：上游原版 toml 的段名带单引号（[sources.'public-resolvers']），
:: 而运行时经 Tommy 规范化后会写成不带引号（[sources.public-resolvers]）——只认一种就会漏删
:: （pre15 首个 CI 即因此漏删 relays，并把 public-resolvers 误判为丢失而 exit 1）。
:: 故每行先 Trim、再剥掉单/双引号做归一化比较；删除 relays 时一直丢弃到下一个任意段头为止
:: （终止行保留，避免吞掉后续段）；四条校验同样基于归一化后的内容，任一不符即 exit 1。
:: 逐行过滤（比整串正则更稳），回写时统一为 LF 行尾且无 BOM，避免 dnscrypt-proxy 解析异常。
powershell -NoProfile -Command "$p='%PKG%\dnscrypt-proxy\dnscrypt-proxy.toml'; if (-not (Test-Path $p)) { Write-Error 'dnscrypt-proxy.toml 不存在'; exit 1 }; $qs=[string][char]39; $qd=[string][char]34; $re1=$qs+'https://raw\.githubusercontent\.com/[^'+$qs+']*'+$qs+'\s*,\s*'; $re2=',\s*'+$qs+'https://raw\.githubusercontent\.com/[^'+$qs+']*'+$qs; $raw=[System.IO.File]::ReadAllText($p); $sep=[string][char]13+'?'+[string][char]10; $lines=$raw -split $sep; $out=New-Object System.Collections.Generic.List[string]; $inRelays=$false; foreach($l in $lines){ $t=$l.Trim(); $n=$t.Replace($qs,'').Replace($qd,''); if($inRelays){ if($t.StartsWith('[') -or $t.StartsWith('#')){ $inRelays=$false; $out.Add($l) }; continue }; if($n -eq '[sources.relays]'){ $inRelays=$true; continue }; if($l.Contains('raw.githubusercontent.com')){ $l=$l -replace $re1,''; $l=$l -replace $re2,'' }; $out.Add($l) }; if($out.Count -gt 0 -and $out[$out.Count-1].Length -eq 0){ $out.RemoveAt($out.Count-1) }; $new=[string]::Join([string][char]10,$out)+[string][char]10; [System.IO.File]::WriteAllText($p,$new,(New-Object System.Text.UTF8Encoding($false))); $r=[System.IO.File]::ReadAllText($p); $rn=$r.Replace($qs,'').Replace($qd,''); if($r.Contains('raw.githubusercontent.com')){ Write-Error '自引用源未摘除'; exit 1 }; if($rn.Contains('[sources.relays]')){ Write-Error 'relays 源段未删除'; exit 1 }; if($rn.Contains('[sources.public-resolvers]') -eq $false){ Write-Error 'public-resolvers 段丢失'; exit 1 }; if($r.Contains('download.dnscrypt.info') -eq $false){ Write-Error '可用镜像源丢失'; exit 1 }"
if errorlevel 1 goto :fail
echo   [OK] dnscrypt 源已去自指（镜像源保留），无用 relays 源段已删除

echo [5g] dnscrypt-proxy 完整性校验（基线固定，非"已审计"：无法逆向 7MB 二进制）
:: 该 exe 是 git blob，UPSTREAM_COMMIT 已从内容上绑定它；本校验的作用是
:: 「审计结论以哈希形式沉淀下来」，使后续任何变更都能被检出，而不是静默换包。
powershell -NoProfile -Command "$f='%PKG%\dnscrypt-proxy\dnscrypt-proxy.exe'; if (-not (Test-Path $f)) { Write-Error 'dnscrypt-proxy.exe 缺失'; exit 1 }; $h=(Get-FileHash $f -Algorithm SHA256).Hash; if ($h -ne 'D91AEB9462B91F5B503BA6FDC5CE118E6C475AFC0030059BB706BC3DB6BB0469') { Write-Error ('dnscrypt-proxy 哈希不匹配，基线 D91AEB94...，实际 '+$h); exit 1 }"
if errorlevel 1 goto :fail
echo   [OK] dnscrypt-proxy 哈希匹配基线（7,517,696 字节）

echo [5b]  WinDivert 驱动：单文件发布时 WinDivert64.sys/WinDivert.dll 已由 WindivertDotnet 内嵌进 fastgithub.exe，
echo       运行时自动解压安装内核驱动；zip 内看不到 .sys 属正常。以下仅作非单文件场景的兼容兜底。
if not exist "%PKG%\WinDivert64.sys" (
  if exist "%PKG%\runtimes\win-x64\native\WinDivert64.sys" (
    copy /Y "%PKG%\runtimes\win-x64\native\WinDivert64.sys" "%PKG%\"
    copy /Y "%PKG%\runtimes\win-x64\native\WinDivert.dll"  "%PKG%\"
  ) else (
    echo [信息] 单文件发布：WinDivert 驱动已内嵌于 fastgithub.exe，首次运行由 WinDivertDotnet 自动安装（无需单独文件）
  )
)

echo [5c] 创建 appsettings/disabled 目录（停用站点片段存放处，引擎不扫描该子目录）
if not exist "%PKG%\appsettings\disabled" mkdir "%PKG%\appsettings\disabled"

echo [5h] 随包分发卸载脚本与安全说明
:: 少了这一步，用户解压后拿不到 clean.cmd，也看不到任何安全提示——
:: 「卸载干净」这条会在分发环节断掉（README 同样不在 publish 产物里）。
copy /Y "%SCRIPT_DIR%clean.cmd" "%PKG%\"
if errorlevel 1 (echo [错误] clean.cmd 未随包分发 & goto :fail)
copy /Y "%SCRIPT_DIR%README.md" "%PKG%\README.md"
if errorlevel 1 (echo [错误] README.md 未随包分发 & goto :fail)
:: 包内还带有上游自带的 README.html，其中「本工具未在 github 之外渠道发布」等表述
:: 与本仓库的分发事实冲突，保留会让用户读到互相矛盾的说明，直接删除。
if exist "%PKG%\README.html" (
    del /F /Q "%PKG%\README.html"
    echo   [OK] 已移除包内上游 README.html（避免与本仓库说明冲突）
)
echo   [OK] clean.cmd + README.md 已随包

echo [5d] 默认站点收敛（默认启用：%DEFAULT_SITES%；其余移入 appsettings\disabled\）
:: 注意：这一步是安全关键。收敛失败意味着 packages / amazonaws 等高风险站点
:: 会随包默认启用，因此失败必须硬失败，不能只打警告放行（原实现是 fail-open）。
set "CONVERGE_FAIL=0"
for %%f in ("%PKG%\appsettings\appsettings.*.json") do (
    set "FILE_NAME=%%~nxf"
    set "KEY=!FILE_NAME:appsettings.=!"
    set "KEY=!KEY:.json=!"
    set "KEEP=0"
    for %%s in (%DEFAULT_SITES%) do if /I "%%s"=="!KEY!" set "KEEP=1"
    if "!KEEP!"=="0" (
        echo   [5d] 默认停用：!KEY!
        move /Y "%%f" "%PKG%\appsettings\disabled\" >nul
        if errorlevel 1 (
            echo   [错误] 站点 !KEY! 停用失败（文件可能被占用）
            set "CONVERGE_FAIL=1"
        )
    )
)
if "!CONVERGE_FAIL!"=="1" (echo [错误] 默认站点收敛失败，高风险站点可能仍处于启用状态，终止构建 & goto :fail)

echo [5d-verify] 复核 appsettings\ 顶层只应剩默认启用站点
set "VERIFY_FAIL=0"
rem [PATCH] 计数字段：for %%f 在**零匹配**时根本不执行循环体，
rem VERIFY_FAIL 会保持 0 直接通过 —— 即「一个站点片段都没发布出去」也会被判为校验通过。
rem 这是典型的“真空通过”：包内没有任何加速配置却放行，装上等于完全不可用。
rem 这里统计实际遍历到的片段数，为零即硬失败。
set "VCOUNT=0"
for %%f in ("%PKG%\appsettings\appsettings.*.json") do (
    set /A "VCOUNT+=1"
    set "VN=%%~nxf"
    set "VK=!VN:appsettings.=!"
    set "VK=!VK:.json=!"
    set "VOK=0"
    for %%s in (%DEFAULT_SITES%) do if /I "%%s"=="!VK!" set "VOK=1"
    if "!VOK!"=="0" (
        echo   [错误] 站点 !VK! 仍处于启用状态
        set "VERIFY_FAIL=1"
    )
)
if "!VCOUNT!"=="0" (echo [错误] 未产出任何 appsettings 站点片段（顶层为空），加速将完全不可用，终止构建 & goto :fail)
if not exist "%PKG%\appsettings\appsettings.github.json" (echo [错误] 缺少核心片段 appsettings.github.json，终止构建 & goto :fail)
if "!VERIFY_FAIL!"=="1" (echo [错误] 默认站点集合校验未通过，终止构建 & goto :fail)
echo   [OK] 顶层启用站点与默认集合一致（共 !VCOUNT! 个片段，已确认含 github）

echo [5e] 防御：移除任何预置 cacert（CA 必须由用户首次运行时在本机生成）
if exist "%PKG%\cacert" (
    rd /S /Q "%PKG%\cacert"
    echo   [警告] 已移除预置 cacert 目录（私钥必须本机生成，禁止随包分发）
)

echo [5f] 硬失败：发布包中检出私钥则终止构建
dir /S /B "%PKG%\*.key" >nul 2>nul
if not errorlevel 1 (
    echo [错误] 发布包中检出私钥文件（*.key），构建终止
    goto :fail
)
echo   [OK] 发布包中无私钥文件

echo [6/6] 打包为 zip（免安装包）
powershell -NoProfile -Command "Compress-Archive -Path '%PKG%\*' -DestinationPath '%DIST%\FastGithub-Portable-win-x64.zip' -Force"
if errorlevel 1 goto :fail

echo.
echo [校验] 产物 SHA256：
:: 同时落盘 dist\SHA256.txt，供 CI 写进 Release notes（用户下载后可自行校验）
powershell -NoProfile -Command "$h=(Get-FileHash '%DIST%\FastGithub-Portable-win-x64.zip' -Algorithm SHA256).Hash; $h | Set-Content '%DIST%\SHA256.txt' -Encoding ascii; $h"
echo.
echo [完成] 免安装包：%DIST%\FastGithub-Portable-win-x64.zip
echo   使用：解压后右键“以管理员身份运行” FastGithub.UI.exe（WinDivert 需管理员）
echo   卸载：不再使用时，请以管理员身份运行仓库根目录的 clean.cmd（移除本地 CA、恢复 git 配置、删除私钥）
goto :eof

:fail
echo [失败] 构建出错，请检查上面的错误信息。
exit /b 1
