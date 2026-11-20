@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul

:: ============================================================
:: GitHubplus 卸载清理脚本
:: 作用：0) 结束运行中的加速进程（否则私钥被占用，删不掉却报成功）
::       1) 移除本机根证书存储中的 FastGithub CA
::       2) 恢复被旧版本改动的 git 全局配置
::       3) 从系统代理绕过列表（ProxyOverride）移除被追加的加速域名
::       3b) 删除本工具写入的浏览器模拟值（HKCU FeatureControl）与 hosts 回滚记录（HKLM\SOFTWARE\FastGithub）
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
:: [N-1] 先精确结束本工具的 ping 锚点（UI 用 ping -t 127.0.0.1 作为引擎的名义父进程）。
:: fastgithub.exe 的 AppHostedService.WaitForParentProcessExitAsync 检测到父进程退出后会走
:: host.StopAsync() 优雅链：hosts 按字节还原并自删 HKLM 回滚记录、ProxyOverride 同步回滚。
:: 直接 /F 强杀会绕过这条链，留下未消费记录，令 [3b/7] 保留分支与「启动引擎自愈」陷入死循环。
:: 按命令行精确匹配，不用 /IM ping.exe，避免误杀机器上其它 ping。
powershell -NoProfile -Command "$anchors=@(Get-CimInstance Win32_Process -EA SilentlyContinue | Where-Object { $_.Name -eq 'ping.exe' -and $_.CommandLine -like '*-t 127.0.0.1*' }); foreach($p in $anchors) { Invoke-CimMethod -InputObject $p -MethodName Terminate -EA SilentlyContinue | Out-Null }; Write-Host ('  已结束 ' + $anchors.Count + ' 个本工具锚点进程（ping -t 127.0.0.1）')"
:: 服务模式没有锚点，先请求 SCM 优雅停止（未安装服务时报错忽略）
sc query fastgithub >nul 2>&1 && sc stop fastgithub >nul 2>&1
:: 等优雅停机跑完：UseShutdownTimeout(6s) 容纳 StopBudgetMs(2500/4000)，再留 2s 余量
ping -n 9 127.0.0.1 >nul
:: 不用 `tasklist | find` 判断进程是否存在：Git Bash / Cygwin 等终端的 PATH 里，
:: GNU find 会抢在 Windows 的 find.exe 前面，导致管道判断静默失效（引擎其实没被停掉）。
:: 残留进程直接 taskkill：命中返回 0，未找到进程返回非 0，忽略即可。
taskkill /IM fastgithub.exe /F >nul 2>&1
if not errorlevel 1 echo   已强杀残留 fastgithub.exe
taskkill /IM FastGithub.UI.exe /F >nul 2>&1
if not errorlevel 1 echo   已结束 FastGithub.UI.exe
taskkill /IM dnscrypt-proxy.exe /F >nul 2>&1
if not errorlevel 1 echo   已强杀残留 dnscrypt-proxy.exe
:: 给进程退出与文件句柄释放留出时间
ping -n 3 127.0.0.1 >nul
echo.

echo [1/7] 移除根证书存储中的 FastGithub CA（精确匹配 Subject=CN=FastGithub 且自签名，见 CertService.cs 的 new X500DistinguishedName($"CN={nameof(FastGithub)}")）
powershell -NoProfile -Command "$actualSubject = 'CN=FastGithub'; $filter = { $_.Subject -eq $actualSubject -and $_.Issuer -eq $_.Subject }; $c = @(Get-ChildItem 'Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root' -EA SilentlyContinue | Where-Object $filter); if ($c.Count -gt 0) { $c | ForEach-Object { Remove-Item $_.PSPath -Force -EA SilentlyContinue }; $left = @(Get-ChildItem 'Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root' -EA SilentlyContinue | Where-Object $filter); if ($left.Count -gt 0) { Write-Host ('  [警告] 仍有 ' + $left.Count + ' 张 FastGithub CA 未能删除，请手动检查证书存储') } else { Write-Host ('  已移除 ' + $c.Count + ' 张证书（删除后已复核）') } } else { Write-Host '  未找到 FastGithub 证书' }"
echo.

echo [2/7] 恢复 git 配置
:: [L-2] 先判 git 是否存在：原实现三条 --unset 被 2>nul 吞掉失败后仍无条件宣告「已清理」，
:: 真机日志里 git 未安装时 [2/7] 依旧显示「已清理」、末尾 --get 报错却被当成「无残留」。
where git >nul 2>&1
if errorlevel 1 (
    echo   未检测到 git，跳过全局配置清理
) else (
    git config --global --unset http.sslVerify 2>nul
    git config --global --unset http.sslBackend 2>nul
    git config --global --unset http.sslCAInfo 2>nul
    echo   已清理 http.sslVerify / http.sslBackend / http.sslCAInfo
)
echo   注：若你希望保留 Schannel 后端（让 git 读取 Windows 证书存储），
echo       可自行执行：git config --global http.sslBackend schannel
echo.

echo [3/7] 从系统代理绕过列表移除被追加的加速域名
:: 上游 ProxyConflictSolver 会往 HKCU\...\Internet Settings\ProxyOverride 追加加速域名，
:: 且只在优雅停机时移除；进程被强杀/崩溃时这些条目会永久留在绕过列表里。
:: 这里只删除「能在大写小写无关的精确匹配本程序 appsettings 片段里出现过的域名」的条目，
:: 不碰用户自己配置的任何条目（含 <local>）。
powershell -NoProfile -Command "$key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'; $cur=(Get-ItemProperty -Path $key -Name ProxyOverride -EA SilentlyContinue).ProxyOverride; if ([string]::IsNullOrEmpty($cur)) { Write-Host '  未设置 ProxyOverride，无需处理' } else { $files=@(); if (Test-Path 'appsettings') { $files += @(Get-ChildItem 'appsettings' -Filter 'appsettings.*.json' -EA SilentlyContinue) }; if (Test-Path 'appsettings\disabled') { $files += @(Get-ChildItem 'appsettings\disabled' -Filter 'appsettings.*.json' -EA SilentlyContinue) }; if (Test-Path 'appsettings.json') { $files += @(Get-Item 'appsettings.json') }; $pats=@(); $owned=(Get-ItemProperty -Path $key -Name 'FastGithubProxyOverrideOwned' -EA SilentlyContinue).FastGithubProxyOverrideOwned; if(-not [string]::IsNullOrEmpty($owned)){ $pats += @($owned -split ';' | Where-Object { $_ -ne '' }) }; $pats += '*.fastgithub.com'; foreach($f in $files) { try { $j=(Get-Content -Raw $f.FullName) -replace '(?m)^\s*//.*$','' | ConvertFrom-Json; if ($j.FastGithub.DomainConfigs) { foreach($n in $j.FastGithub.DomainConfigs.PSObject.Properties.Name) { $pats += $n } } } catch { } }; if ($pats.Count -eq 0) { Write-Host '  未解析到任何域名模式（配置片段可能已被删除），为安全起见不改动注册表'; Write-Host ('  当前 ProxyOverride：' + $cur) } else { $parts=@($cur -split ';' | Where-Object { $_ -ne '' }); $kept=@($parts | Where-Object { $pats -notcontains $_ }); if ($kept.Count -lt $parts.Count) { $new=($kept -join ';'); Set-ItemProperty -Path $key -Name ProxyOverride -Value $new; Write-Host ('  已移除 ' + ($parts.Count - $kept.Count) + ' 条加速域名绕过项（共 ' + $pats.Count + ' 个模式参与匹配）'); Write-Host ('  现值：' + $new); Write-Host '  注：其余条目（含其它工具写入的）保持原样，未做任何改动' } else { Write-Host '  ProxyOverride 中未发现本工具追加的条目（保持原样）' } } }"
:: 本工具新版会用同一个键下的 FastGithubProxyOverrideOwned 记录自己写入过哪些条目
:: （用于取消勾选时精确回滚）。卸载时一并删掉，避免留下无意义的残留值。
powershell -NoProfile -Command "$k='HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'; if (Get-ItemProperty -Path $k -Name 'FastGithubProxyOverrideOwned' -EA SilentlyContinue) { Remove-ItemProperty -Path $k -Name 'FastGithubProxyOverrideOwned' -EA SilentlyContinue; Write-Host '  已删除 ProxyOverride 条目记录 FastGithubProxyOverrideOwned' } else { Write-Host '  未找到 ProxyOverride 条目记录（无需处理）' }"
echo.

echo [3b/7] 删除本工具写入的浏览器模拟值与 hosts 回滚记录
:: UI 每次运行都会往 HKCU 的 FeatureControl 下写名为 FastGithub.UI.exe 的两个值
:: （FEATURE_BROWSER_EMULATION 让内置 WebBrowser 用现代 IE 内核、FEATURE_96DPI_PIXEL 关宿主机 DPI 缩放）。
:: 卸载时删除，避免在用户注册表留残留。只删本程序自己的值（name 精确为 FastGithub.UI.exe），
:: 不动同键下其它进程/用户写入的条目。
powershell -NoProfile -Command "$name='FastGithub.UI.exe'; $paths=@('HKCU:\Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION','HKCU:\Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_96DPI_PIXEL'); $n=0; foreach ($p in $paths) { if (Get-ItemProperty -Path $p -Name $name -EA SilentlyContinue) { Remove-ItemProperty -Path $p -Name $name -EA SilentlyContinue; $n++; Write-Host ('  已删除 ' + $name + ' @ ' + $p) } }; if ($n -eq 0) { Write-Host '  未找到本工具写入的浏览器模拟值（无需处理）' }"
:: 新版 HostsConflictSolver 会在 HKLM\SOFTWARE\FastGithub 记录 hosts 被改写前的原始行段字节（base64），
:: 供崩溃后自愈恢复。正常退出会自行删除；进程被强杀/崩溃时残留。
:: 卸载时必须保留未消费的记录（否则 hosts 永久无法自愈），待下次启动引擎消费后再清理。
powershell -NoProfile -Command "$k='HKLM:\SOFTWARE\FastGithub'; if (Test-Path $k) { $rec=(Get-ItemProperty -Path $k -Name 'HostsPatchedSegments' -EA SilentlyContinue).HostsPatchedSegments; if ([string]::IsNullOrEmpty($rec)) { Remove-Item -Path $k -Recurse -Force -EA SilentlyContinue; if (Test-Path $k) { Write-Host '  [警告] HKLM\SOFTWARE\FastGithub 删除失败（可能被占用）' } else { Write-Host '  已删除空的 hosts 回滚记录键（无未消费记录）' } } else { Write-Host '  [重要] 检测到未消费的 hosts 回滚记录 HostsPatchedSegments：上次引擎可能非正常退出，hosts 仍被本工具改写。'; Write-Host '  该记录是 [2y] 补丁按字节还原 hosts 的唯一依据，本次卸载予以保留。'; Write-Host '  处理（两步，缺一不可）：启动一次引擎 → 在 UI 点「停止加速」让它优雅退出 → 再重跑本脚本。'; Write-Host '  仅启动引擎再强杀无效：SolveAsync 自愈后会重新注释原本冲突的 hosts 行并再次写入记录。'; Write-Host '  此过程会临时重装 CA（前面步骤已删除），最后重跑本脚本会一并清掉。' } } else { Write-Host '  未找到 hosts 回滚记录（无需处理）' }"
echo.

echo [4/7] 删除本地 CA 与私钥
set "FOUND_CACERT=0"
if exist "cacert" (
    rd /S /Q "cacert"
    set "FOUND_CACERT=1"
    if exist "cacert" (
        echo   [警告] cacert 目录未完全删除（私钥可能仍被占用），请关闭相关程序后重跑本脚本
    ) else (
        echo   已删除 cacert 目录（含私钥 fastgithub.key）
    )
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
:: [L-1] 必须先停并删除 WinDivert 内核驱动服务，再删 .sys 副本。原实现把服务删除放在 [7/7]，
:: 驱动仍加载时 rd /S /Q 对 WinDivert64.sys 必然 Access is denied；且 rd 后无条件 echo「已删除」、
:: 不查结果，失败被静默包装成成功（真机日志已复现：报错与「已删除」同时出现）。
sc query WinDivert >nul 2>&1 && (sc stop WinDivert >nul 2>&1 & sc delete WinDivert >nul 2>&1)
if exist "%APPDATA%\WindivertDotnet" (
    rd /S /Q "%APPDATA%\WindivertDotnet"
    if exist "%APPDATA%\WindivertDotnet" (
        echo   [警告] WindivertDotnet 未完全删除（WinDivert64.sys 可能仍被占用）
        echo          请重启电脑后重跑本脚本，或手动删除：%APPDATA%\WindivertDotnet
    ) else (
        echo   已删除 %APPDATA%\WindivertDotnet（WinDivert 驱动本地副本）
    )
) else (
    echo   未找到 %APPDATA%\WindivertDotnet
)
echo.

echo [7/7] 删除可能残留的 Windows 服务并回显残留
:: 只有执行过 fastgithub.exe start 才会装服务（AUTO_START + LocalSystem，会开机自启）。
:: 服务不存在时 sc delete 会报错，用 >nul 2>&1 忽略。另外本工具运行时会由 WindivertDotnet 在 SCM 注册名为 WinDivert 的内核驱动服务，卸载时需一并删除。
sc query fastgithub >nul 2>&1 && (sc stop fastgithub >nul 2>&1 & sc delete fastgithub >nul 2>&1 & echo   已删除服务 fastgithub)
sc query FastGithub.dnscrypt-proxy >nul 2>&1 && (sc stop FastGithub.dnscrypt-proxy >nul 2>&1 & sc delete FastGithub.dnscrypt-proxy >nul 2>&1 & echo   已删除服务 FastGithub.dnscrypt-proxy)
:: WinDivert 服务已在 [6/7] 提前停止并删除（否则 .sys 删不掉）；这里仅作幂等兜底。
sc query WinDivert >nul 2>&1 && (sc stop WinDivert >nul 2>&1 & sc delete WinDivert >nul 2>&1 & echo   已删除内核驱动服务 WinDivert)
echo.
echo   --- git 全局配置残留检查 ---
where git >nul 2>&1
if errorlevel 1 (
    echo   未检测到 git，跳过残留检查
) else (
    git config --global --get http.sslVerify
    git config --global --get http.sslBackend
    echo   以上两行无输出 = 对应配置已恢复默认；若有输出，请按上面的取值手动清理。
)
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
echo   4. hosts 文件：本仓库 [2y] 已实现按字节精确还原（正常停机自动恢复；非正常退出
echo      后由下次启动自愈，依据 HKLM\SOFTWARE\FastGithub 的 HostsPatchedSegments 记录）。
echo      若上面提示「检测到未消费的回滚记录」，请在启动引擎后在 UI 点「停止加速」优雅退出
echo      （此过程会临时重装 CA，属预期现象，重跑本脚本会清理），再重跑本脚本；仅启动再强杀无效。
echo      如你原本自定义过 hosts，仍建议检查一次 C:\Windows\System32\drivers\etc\hosts。
echo.
pause
