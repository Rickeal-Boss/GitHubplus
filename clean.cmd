@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul

:: ============================================================
:: GitHubplus 卸载清理脚本
:: 作用：1) 移除本机根证书存储中的 FastGithub CA
::       2) 恢复被旧版本改动的 git 全局配置
::       3) 删除本地 CA 与私钥（cacert/）
::       4) 回显残留以便确认
:: 用法：把本文件放到程序运行目录（cacert/ 所在目录），
::       右键“以管理员身份运行”（清理 LocalMachine 根存储必须提权）
:: ============================================================

:: 关键：右键「以管理员身份运行」时 Windows 常把当前目录重置为 C:\Windows\System32，
:: 会导致下面基于相对路径的 cacert / logs 判断全部落空。强制切到脚本所在目录。
cd /d "%~dp0"

echo ==== GitHubplus 卸载清理 ====
echo.
echo   工作目录：%CD%

echo [权限] 检测管理员权限
net session >nul 2>&1
if errorlevel 1 (
    echo   [错误] 当前不是管理员权限。
    echo   清理 LocalMachine 根证书存储需要管理员权限；
    echo   请右键本文件，选择“以管理员身份运行”后重试。
    echo.
    pause
    exit /b 1
)
echo   [OK] 已获取管理员权限
echo.

echo [1/5] 移除根证书存储中的 FastGithub CA
powershell -NoProfile -Command "$c = @(Get-ChildItem 'Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root' -EA SilentlyContinue | Where-Object { $_.Subject -like '*FastGithub*' }); if ($c.Count -gt 0) { $c | ForEach-Object { Remove-Item $_.PSPath -Force -EA SilentlyContinue }; Write-Host ('  已移除 ' + $c.Count + ' 张证书') } else { Write-Host '  未找到 FastGithub 证书' }"
echo.

echo [2/5] 恢复 git 配置
git config --global --unset http.sslVerify 2>nul
git config --global --unset http.sslBackend 2>nul
echo   已清理 http.sslVerify / http.sslBackend
echo   注：若你希望保留 Schannel 后端（让 git 读取 Windows 证书存储），
echo       可自行执行：git config --global http.sslBackend schannel
echo.

echo [3/5] 删除本地 CA 与私钥
set "FOUND_CACERT=0"
if exist "cacert" (
    rd /S /Q "cacert"
    set "FOUND_CACERT=1"
    echo   已删除 cacert 目录（含私钥 fastgithub.key）
) else (
    echo   未在当前目录找到 cacert
)
echo.

echo [4/5] 删除运行期日志
:: 上游 logs/log.txt 按天滚动、无容量上限，且记录了访问过的域名与路径
set "FOUND_LOGS=0"
if exist "logs" (
    rd /S /Q "logs"
    set "FOUND_LOGS=1"
    echo   已删除 logs 目录（含按天滚动的访问日志）
) else (
    echo   未找到 logs 目录
)
echo.

echo [5/5] 确认残留（下面两行有输出即表示仍有遗留）
git config --global --get http.sslVerify
git config --global --get http.sslBackend
echo   以上两行均无输出 = git 全局配置已恢复默认。
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
echo.
pause
