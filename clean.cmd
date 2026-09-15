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

echo ==== GitHubplus 卸载清理 ====
echo.

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

echo [1/4] 移除根证书存储中的 FastGithub CA
powershell -NoProfile -Command "$c = @(Get-ChildItem 'Cert:\LocalMachine\Root', 'Cert:\CurrentUser\Root' -EA SilentlyContinue | Where-Object { $_.Subject -like '*FastGithub*' }); if ($c.Count -gt 0) { $c | ForEach-Object { Remove-Item $_.PSPath -Force -EA SilentlyContinue }; Write-Host ('  已移除 ' + $c.Count + ' 张证书') } else { Write-Host '  未找到 FastGithub 证书' }"
echo.

echo [2/4] 恢复 git 配置
git config --global --unset http.sslVerify 2>nul
git config --global --unset http.sslBackend 2>nul
echo   已清理 http.sslVerify / http.sslBackend
echo   注：若你希望保留 Schannel 后端（让 git 读取 Windows 证书存储），
echo       可自行执行：git config --global http.sslBackend schannel
echo.

echo [3/4] 删除本地 CA 与私钥
if exist "cacert" (
    rd /S /Q "cacert"
    echo   已删除 cacert 目录（含私钥 fastgithub.key）
) else (
    echo   未在当前目录找到 cacert（请把本脚本放到程序运行目录再执行）
)
echo.

echo [4/4] 确认残留（下面两行有输出即表示仍有遗留）
git config --global --get http.sslVerify
git config --global --get http.sslBackend
echo   以上两行均无输出 = git 全局配置已恢复默认。
echo.
echo ==== 清理完成 ====
echo.
echo 重要提示：
echo   1. 请勿分发运行过的程序目录——其中的 cacert\fastgithub.key 是本机生成的
echo      CA 私钥（明文），任何拿到它的人都能解密你已勾选站点的 HTTPS 流量。
echo   2. 若需在其它机器使用，请用官方 Release 的干净压缩包重新解压运行。
echo   3. 浏览器 / 系统可能缓存了旧证书，建议重启终端与浏览器后再验证。
echo.
pause
