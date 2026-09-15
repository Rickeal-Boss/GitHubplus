@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul

:: 脚本基准目录（无论从哪里调用，路径都基于脚本自身位置，避免依赖当前工作目录）
set "SCRIPT_DIR=%~dp0"

:: ============================================================
:: 构建 Windows 免安装包（self-contained 单文件，解压即跑）
:: 前置：安装 .NET 7 SDK  https://dotnet.microsoft.com/download
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

echo [前置] 检测 .NET 7 SDK
where dotnet >nul 2>&1 || (echo [错误] 未检测到 dotnet，请先安装 .NET 7 SDK（https://dotnet.microsoft.com/download） & goto :fail)
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

echo [2g] 注入服务注册补丁（证书缓存加容量上限 + 淘汰时 Dispose）
:: 上游 ServiceCollectionExtensions.cs 的 AddReverseProxy() 用 .AddMemoryCache() 无 SizeLimit，
:: 而 CertService 以域名为 key 缓存 X509Certificate2（非托管句柄），访问大量不同子域会无界增长。
copy /Y "%SCRIPT_DIR%src-patches\FastGithub.HttpServer\ServiceCollectionExtensions.cs" "%SRC%\FastGithub.HttpServer\ServiceCollectionExtensions.cs" || goto :fail

echo [2h] 注入构建属性补丁（TargetFramework net7.0 -> net10.0 LTS）
:: 上游 Directory.Build.props 全局 net7.0（EOL），self-contained 发布会内嵌无补丁运行时。
:: FastGithub.UI.csproj 显式 net45，不受 Directory.Build.props 覆盖。
copy /Y "%SCRIPT_DIR%src-patches\Directory.Build.props" "%SRC%\Directory.Build.props" || goto :fail


echo [3/6] 注入加速配置（仅新增 HuggingFace 镜像；GitHub 主站配置为仓库原生，不覆盖）
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
for %%f in ("%PKG%\appsettings\appsettings.*.json") do (
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
if "!VERIFY_FAIL!"=="1" (echo [错误] 默认站点集合校验未通过，终止构建 & goto :fail)
echo   [OK] 顶层启用站点与默认集合一致

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
