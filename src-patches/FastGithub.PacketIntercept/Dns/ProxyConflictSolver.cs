using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept.Dns
{
    /// <summary>
    /// 代理冲突解决者
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class ProxyConflictSolver : IDnsConflictSolver
    {
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_PROXY_SETTINGS_CHANGED = 95;

        private const char PROXYOVERRIDE_SEPARATOR = ';';
        private const string PROXYOVERRIDE_KEY = "ProxyOverride";
        private const string INTERNET_SETTINGS = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        /// <summary>
        /// [PATCH] 本程序上次写入 ProxyOverride 的条目记录。
        /// 与 ProxyOverride 放在同一个注册表键下，仅本程序读写，Windows 会忽略该未知值。
        /// </summary>
        private const string PROXYOVERRIDE_OWNED_KEY = "FastGithubProxyOverrideOwned";

        private readonly IOptions<FastGithubOptions> options;
        private readonly ILogger<ProxyConflictSolver> logger;

        [DllImport("wininet.dll")]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);


        /// <summary>
        /// 代理冲突解决者
        /// </summary>
        /// <param name="options"></param>
        /// <param name="logger"></param>
        public ProxyConflictSolver(
            IOptions<FastGithubOptions> options,
            ILogger<ProxyConflictSolver> logger)
        {
            this.options = options;
            this.logger = logger;
        }

        /// <summary>
        /// 解决冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task SolveAsync(CancellationToken cancellationToken)
        {
            this.SetToProxyOvride();
            this.CheckProxyConflict();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 恢复冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task RestoreAsync(CancellationToken cancellationToken)
        {
            this.RemoveFromProxyOvride();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 添加到ProxyOvride
        /// [PATCH] 只按自己的记录做精确回滚，绝不碰用户手动配置的条目。
        /// 原实现只做「当前 DomainConfigs 的并集」，用户取消勾选某个站点后该域名
        /// 就不在 DomainConfigs 里了，于是留在绕过列表里永远删不掉（真机实测残留 34 条、
        /// 其中 16 条是 GitHub 条目）。
        /// </summary>
        private void SetToProxyOvride()
        {
            using var settings = Registry.CurrentUser.OpenSubKey(INTERNET_SETTINGS, writable: true);
            if (settings == null)
            {
                return;
            }

            // 先把上一次由本程序写入的条目精确摘掉，避免被取消勾选的域名残留
            var items = new HashSet<string>(GetProxyOvride(settings), StringComparer.OrdinalIgnoreCase);
            foreach (var owned in GetOwnedProxyOvride(settings))
            {
                items.Remove(owned);
            }

            var current = this.options.Value.DomainConfigs.Keys.ToArray();
            foreach (var item in current)
            {
                items.Add(item);
            }

            SetProxyOvride(settings, items);
            SetOwnedProxyOvride(settings, current);
        }

        /// <summary>
        /// 从ProxyOvride移除
        /// [PATCH] 按自己的记录精确移除，然后清空记录。
        /// 记录不存在（老版本升级上来）时退化为原有逻辑，保证兼容。
        /// </summary>
        private void RemoveFromProxyOvride()
        {
            using var settings = Registry.CurrentUser.OpenSubKey(INTERNET_SETTINGS, writable: true);
            if (settings == null)
            {
                return;
            }

            var items = new HashSet<string>(GetProxyOvride(settings), StringComparer.OrdinalIgnoreCase);
            var owned = GetOwnedProxyOvride(settings);
            if (owned.Length > 0)
            {
                foreach (var item in owned)
                {
                    items.Remove(item);
                }
            }
            else
            {
                // 没有自己的记录时，退化为「剔除当前 DomainConfigs 里的域名」，
                // 与升级前的行为保持一致，且同样不会误删用户条目。
                foreach (var item in this.options.Value.DomainConfigs.Keys)
                {
                    items.Remove(item);
                }
            }

            SetProxyOvride(settings, items);
            DeleteOwnedProxyOvride(settings);
        }

        /// <summary>
        /// 检测代理冲突
        /// </summary>
        private void CheckProxyConflict()
        {
            var systemProxy = HttpClient.DefaultProxy;
            if (systemProxy == null)
            {
                return;
            }

            foreach (var domain in this.options.Value.DomainConfigs.Keys)
            {
                var destination = new Uri($"https://{domain.Replace('*', 'a')}");
                var proxyServer = systemProxy.GetProxy(destination);
                if (proxyServer != null)
                {
                    this.logger.LogError($"由于系统设置了代理{proxyServer}，{nameof(FastGithub)}无法加速{domain}");
                }
            }
        }

        /// <summary>
        /// 获取ProxyOverride
        /// </summary>
        /// <param name="registryKey"></param>
        /// <returns></returns>
        private static string[] GetProxyOvride(RegistryKey registryKey)
        {
            var value = registryKey.GetValue(PROXYOVERRIDE_KEY, null)?.ToString();
            if (value == null)
            {
                return Array.Empty<string>();
            }

            return value
                .Split(PROXYOVERRIDE_SEPARATOR, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .ToArray();
        }

        /// <summary>
        /// 设置ProxyOverride
        /// </summary>
        /// <param name="registryKey"></param>
        /// <param name="items"></param>
        private static void SetProxyOvride(RegistryKey registryKey, IEnumerable<string> items)
        {
            var value = string.Join(PROXYOVERRIDE_SEPARATOR, items);
            registryKey.SetValue(PROXYOVERRIDE_KEY, value, RegistryValueKind.String);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PROXY_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }

        /// <summary>
        /// [PATCH] 获取本程序上次写入 ProxyOverride 的条目
        /// </summary>
        /// <param name="registryKey"></param>
        /// <returns></returns>
        private static string[] GetOwnedProxyOvride(RegistryKey registryKey)
        {
            var value = registryKey.GetValue(PROXYOVERRIDE_OWNED_KEY, null)?.ToString();
            if (string.IsNullOrEmpty(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(PROXYOVERRIDE_SEPARATOR, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .ToArray();
        }

        /// <summary>
        /// [PATCH] 记录本程序写入 ProxyOverride 的条目
        /// </summary>
        /// <param name="registryKey"></param>
        /// <param name="items"></param>
        private static void SetOwnedProxyOvride(RegistryKey registryKey, IEnumerable<string> items)
        {
            registryKey.SetValue(PROXYOVERRIDE_OWNED_KEY, string.Join(PROXYOVERRIDE_SEPARATOR, items), RegistryValueKind.String);
        }

        /// <summary>
        /// [PATCH] 删除本程序的条目记录
        /// </summary>
        /// <param name="registryKey"></param>
        private static void DeleteOwnedProxyOvride(RegistryKey registryKey)
        {
            registryKey.DeleteValue(PROXYOVERRIDE_OWNED_KEY, throwOnMissingValue: false);
        }
    }
}
