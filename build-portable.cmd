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
set "SRC=%SCRIPT_DIR%src"
set "DIST=%SCRIPT_DIR%dist"
set "PKG=%DIST%\fastgithub_win-x64"
set "PATCHES=%SCRIPT_DIR%src-patches"

echo [前置] 检测 .NET 7 SDK
where dotnet >nul 2>&1 || (echo [错误] 未检测到 dotnet，请先安装 .NET 7 SDK（https://dotnet.microsoft.com/download） & goto :fail)
for /f "tokens=*" %%v in ('dotnet --version') do set "DOTNET_VER=%%v"
echo   检测到的 dotnet 版本：%DOTNET_VER%

echo [1/6] 清理并准备目录
if exist "%DIST%" rd /S /Q "%DIST%"
mkdir "%PKG%"

echo [2/6] 克隆仓库（@dnscrypt-proxy 是普通目录，非子模块；失败自动重试）
if exist "%SRC%" rd /S /Q "%SRC%"
set "CLONE_TRY=0"
:clone_retry
git -c http.version=HTTP/1.1 -c http.postBuffer=524288000 clone --depth 1 --filter=blob:none "%REPO%" "%SRC%"
if errorlevel 1 (
    set /a CLONE_TRY+=1
    if exist "%SRC%" rd /S /Q "%SRC%"
    if !CLONE_TRY! LSS 5 (
        echo   [警告] 克隆失败，5 秒后重试（!CLONE_TRY!/5）…
        ping -n 6 127.0.0.1 >nul
        goto clone_retry
    )
    echo [错误] 克隆仓库失败，请检查网络后重试。
    goto :fail
)

echo [2b/6] 改写"检测更新"跳转链接 -> 我们的仓库（Rickeal-Boss/GitHubplus）
powershell -NoProfile -Command "$p='%SRC%\FastGithub.UI\MainWindow.xaml.cs'; $c=Get-Content -Raw $p; $c=$c.Replace('https://github.com/creazyboyone/FastGithub','https://github.com/Rickeal-Boss/GitHubplus'); $c | Set-Content $p -Encoding utf8; if ($c -notmatch 'Rickeal-Boss/GitHubplus') { Write-Error '检测更新链接未替换成功'; exit 1 }"
if errorlevel 1 goto :fail

echo [2c/6] 注入 UI 增强（加速控制页 + 进程启停控制 + 主窗体新增“加速”标签）
copy /Y "%PATCHES%\Program.cs" "%SRC%\FastGithub.UI\Program.cs" || goto :fail
copy /Y "%PATCHES%\MainWindow.xaml" "%SRC%\FastGithub.UI\MainWindow.xaml" || goto :fail
copy /Y "%PATCHES%\AcceleratorPanel.xaml" "%SRC%\FastGithub.UI\AcceleratorPanel.xaml" || goto :fail
copy /Y "%PATCHES%\AcceleratorPanel.xaml.cs" "%SRC%\FastGithub.UI\AcceleratorPanel.xaml.cs" || goto :fail

echo [3/6] 注入加速配置（仅新增 HuggingFace 镜像；GitHub 主站配置为仓库原生，不覆盖）
copy /Y "%SCRIPT_DIR%appsettings.huggingface.json" "%SRC%\FastGithub\appsettings\" || goto :fail

echo [4/6] 发布（官方两步法：先 UI，再核心单文件，输出到同一目录）
dotnet publish -c Release -o "%PKG%" "%SRC%\FastGithub.UI\FastGithub.UI.csproj"
if errorlevel 1 goto :fail
dotnet publish -c Release -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained -r win-x64 -o "%PKG%" "%SRC%\FastGithub\FastGithub.csproj"
if errorlevel 1 goto :fail

echo [5/6] 修正 dnscrypt-proxy 目录命名（代码期望 dnscrypt-proxy/，仓库是 @dnscrypt-proxy/）
if not exist "%PKG%\dnscrypt-proxy" mkdir "%PKG%\dnscrypt-proxy"
copy /Y "%SRC%\@dnscrypt-proxy\win-x64\dnscrypt-proxy.exe" "%PKG%\dnscrypt-proxy\" || echo [警告] dnscrypt-proxy.exe 未复制，DNS 防污染将降级（仍可加速）
copy /Y "%SRC%\@dnscrypt-proxy\dnscrypt-proxy.toml"        "%PKG%\dnscrypt-proxy\" || echo [警告] dnscrypt-proxy.toml 未复制

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

echo [6/6] 打包为 zip（免安装包）
powershell -NoProfile -Command "Compress-Archive -Path '%PKG%\*' -DestinationPath '%DIST%\FastGithub-Portable-win-x64.zip' -Force"
if errorlevel 1 goto :fail

echo.
echo [校验] 产物 SHA256：
powershell -NoProfile -Command "(Get-FileHash '%DIST%\FastGithub-Portable-win-x64.zip' -Algorithm SHA256).Hash"
echo.
echo [完成] 免安装包：%DIST%\FastGithub-Portable-win-x64.zip
echo   使用：解压后右键“以管理员身份运行” FastGithub.UI.exe（WinDivert 需管理员）
goto :eof

:fail
echo [失败] 构建出错，请检查上面的错误信息。
exit /b 1
