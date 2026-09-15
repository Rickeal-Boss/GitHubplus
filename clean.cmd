@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul

:: ============================================================
:: GitHubplus 卸载清理脚本
:: 作用：0) 结束运行中的加速进程（否则私钥被占用，删不掉却报成功）
::       1) 移除本机根证书存储中的 FastGithub CA
::       2) 恢复被旧版本改动的 git 全局配置
::       3) 从系统代理绕过列表（ProxyOverride）移除被追加的加速域名
::       4) 删除本地 CA 与私钥（cacert/）
::       5) 删除运行期日志与界面残留（logs/ ui-error.log ui-background）
::       6) 删除 WinDivert 驱动本地副本（%APPDATA%\WindivertDotnet）
::       7) 删除可能残留的 Windows 服务 + 回显残留
:: 用法：把本文件放到程序运行目录（cacert/ 所在目录），
::       右键“以管理员身份运行”（清理根证书存储与 Windows 服务必须提权）
:: ============================================================

:: 关键：右键「以管理员身份运行」时 Windows 常把当前目录重置为系统目录，
:: 会导致下面基于相对路径的 cacert / logs / appsettings 判断全部落空。强制切到脚本所在目录。
cd /d "%~dp0"

echo ==== GitHubplus 卸载清理 ====
echo.
echo   工作目录：%CD%

echo [权限] 检测管理员权限
net session >nul 2>&1
if errorlevel 1 (
    echo   [错误] 当前不是管理员权限。
    echo   清理根证书存储与删除 Windows 服务需要管理员权限；
    echo   请右键本文件，选择“以管理员身份运行”后重试。
    echo.
    pause
    exit /b 1
)
echo   [OK] 已获取管理员权限
echo.

echo [0/7] 结束正在运行的加速进程
:: 不用 `tasklist | find` 判断进程是否存在：Git Bash / Cygwin 等终端的 PATH 里，
:: GNU find 会抢在 Windows 的 find.exe 前面，导致管道判断静默失效（引擎其实没被停掉）。
:: 直接 taskkill：命中返回 0，未找到进程返回非 0，忽略即可。
taskkill /IM fastgithub.exe /F >nul 2>&1
if not errorlevel 1 echo   已结束 fastgithub.exe
taskkill /IM FastGithub.UI.exe /F >nul 2>&1
if not errorlevel 1 echo   已结束 FastGithub.UI.exe
taskkill /IM dnscrypt-proxy.exe /F >nul 2>&1
if not errorlevel 1 echo   已结束 dnscrypt-proxy.exe
:: 给进程退出与文件句柄释放留出时间
ping -n 3 127.0.0.1 >nul
echo.

echo [1/7] 移除根证书存储中的 FastGithub CA
powershell -NoProfile -Command "$c = @(Get-ChildItem 'Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root' -EA SilentlyContinue | Where-Object { $_.Subject -like '*FastGithub*' }); if ($c.Count -gt 0) { $c | ForEach-Object { Remove-Item $_.PSPath -Force -EA SilentlyContinue }; Write-Host ('  已移除 ' + $c.Count + ' 张证书') } else { Write-Host '  未找到 FastGithub 证书' }"
echo.

echo [2/7] 恢复 git 配置
git config --global --unset http.sslVerify 2>nul
git config --global --unset http.sslBackend 2>nul
git config --global --unset http.sslCAInfo 2>nul
echo   已清理 http.sslVerify / http.sslBackend / http.sslCAInfo
echo   注：若你希望保留 Schannel 后端（让 git 读取 Windows 证书存储），
echo       可自行执行：git config --global http.sslBackend schannel
echo.

echo [3/7] 从系统代理绕过列表移除被追加的加速域名
:: 上游 ProxyConflictSolver 会往 HKCU\...\Internet Settings\ProxyOverride 追加加速域名，
:: 且只在优雅停机时移除；进程被强杀/崩溃时这些条目会永久留在绕过列表里。
:: 这里只删除「能在大写小写无关的精确匹配本程序 appsettings 片段里出现过的域名」的条目，
:: 不碰用户自己配置的任何条目（含 <local>）。
powershell -NoProfile -Command "$key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'; $cur=(Get-ItemProperty -Path $key -Name ProxyOverride -EA SilentlyContinue).ProxyOverride; if ([string]::IsNullOrEmpty($cur)) { Write-Host '  未设置 ProxyOverride，无需处理' } else { $files=@(); if (Test-Path 'appsettings') { $files += @(Get-ChildItem 'appsettings' -Filter 'appsettings.*.json' -EA SilentlyContinue) }; if (Test-Path 'appsettings\disabled') { $files += @(Get-ChildItem 'appsettings\disabled' -Filter 'appsettings.*.json' -EA SilentlyContinue) }; if (Test-Path 'appsettings.json') { $files += @(Get-Item 'appsettings.json') }; $pats=@(); foreach($f in $files) { try { $j=Get-Content -Raw $f.FullName | ConvertFrom-Json; if ($j.FastGithub.DomainConfigs) { foreach($n in $j.FastGithub.DomainConfigs.PSObject.Properties.Name) { $pats += $n } } } catch { } }; if ($pats.Count -eq 0) { Write-Host '  未解析到任何域名模式（配置片段可能已被删除），为安全起见不改动注册表'; Write-Host ('  当前 ProxyOverride：' + $cur) } else { $parts=@($cur -split ';' | Where-Object { $_ -ne '' }); $kept=@($parts | Where-Object { $pats -notcontains $_ }); if ($kept.Count -lt $parts.Count) { $new=($kept -join ';'); Set-ItemProperty -Path $key -Name ProxyOverride -Value $new; Write-Host ('  已移除 ' + ($parts.Count - $kept.Count) + ' 条加速域名绕过项（共 ' + $pats.Count + ' 个模式参与匹配）'); Write-Host ('  现值：' + $new); Write-Host '  注：其余条目（含其它工具写入的）保持原样，未做任何改动' } else { Write-Host '  ProxyOverride 中未发现本工具追加的条目（保持原样）' } } }"
echo.

echo [4/7] 删除本地 CA 与私钥
set "FOUND_CACERT=0"
if exist "cacert" (
    rd /S /Q "cacert"
    set "FOUND_CACERT=1"
    echo   已删除 cacert 目录（含私钥 fastgithub.key）
) else (
    echo   未在当前目录找到 cacert
)
echo.

echo [5/7] 删除运行期日志与界面残留
:: 上游 logs/log.txt 按天滚动、无容量上限，且记录了访问过的域名与路径
set "FOUND_LOGS=0"
if exist "logs" (
    rd /S /Q "logs"
    set "FOUND_LOGS=1"
    echo   已删除 logs 目录（含按天滚动的访问日志）
) else (
    echo   未找到 logs 目录
)
:: 界面补丁（AcceleratorPanel）写出的残留：错误日志 + 自定义背景图片与路径记录
if exist "ui-error.log" (
    del /F /Q "ui-error.log"
    echo   已删除 ui-error.log
)
if exist "ui-background.txt" del /F /Q "ui-background.txt"
if exist "ui-background" (
    rd /S /Q "ui-background"
    echo   已删除 ui-background 目录（自定义界面背景）
)
echo.

echo [6/7] 删除 WinDivert 驱动本地副本
:: WindivertDotnet 会把 WinDivert64.sys / WinDivert.dll 解压到 %APPDATA%\WindivertDotnet\...
:: 且「文件存在即跳过」，永不复查、卸载也不清理。该目录位于用户可写位置，
:: 被替换后会在下次提权运行时加载为内核驱动 —— 必须清掉。
if exist "%APPDATA%\WindivertDotnet" (
    rd /S /Q "%APPDATA%\WindivertDotnet"
    echo   已删除 %APPDATA%\WindivertDotnet（WinDivert 驱动本地副本）
) else (
    echo   未找到 %APPDATA%\WindivertDotnet
)
echo.

echo [7/7] 删除可能残留的 Windows 服务并回显残留
:: 只有执行过 fastgithub.exe start 才会装服务（AUTO_START + LocalSystem，会开机自启）。
:: 服务不存在时 sc delete 会报错，用 >nul 2>&1 忽略。
sc query fastgithub >nul 2>&1 && (sc stop fastgithub >nul 2>&1 & sc delete fastgithub >nul 2>&1 & echo   已删除服务 fastgithub)
sc query FastGithub.dnscrypt-proxy >nul 2>&1 && (sc stop FastGithub.dnscrypt-proxy >nul 2>&1 & sc delete FastGithub.dnscrypt-proxy >nul 2>&1 & echo   已删除服务 FastGithub.dnscrypt-proxy)
echo.
echo   --- git 全局配置残留（下面两行有输出即表示仍有遗留）---
git config --global --get http.sslVerify
git config --global --get http.sslBackend
echo   以上均无输出 = git 全局配置已恢复默认。
echo.

if "!FOUND_CACERT!"=="0" (
    echo ==== 清理未完成（重要）====
    echo   未在本目录找到 cacert，说明这里可能不是程序运行目录。
    echo   私钥很可能仍留在真实程序目录里，本次清理并未删除它。
    echo   请把本脚本复制到程序运行目录（cacert 所在目录）后重新运行。
    echo.
) else (
    echo ==== 清理完成 ====
    echo.
)
echo 重要提示：
echo   1. 请勿分发运行过的程序目录——其中的 cacert\fastgithub.key 是本机生成的
echo      CA 私钥（明文），任何拿到它的人都能解密你已勾选站点的 HTTPS 流量。
echo   2. 若需在其它机器使用，请用官方 Release 的干净压缩包重新解压运行。
echo   3. 浏览器 / 系统可能缓存了旧证书，建议重启终端与浏览器后再验证。
echo   4. hosts 文件中被注释掉的加速域名条目不会被本脚本恢复（上游未实现恢复逻辑），
echo      如你原本自定义过 hosts，请自行检查系统 hosts 文件。
echo.
pause
