using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// [PATCH] dnscrypt-proxy 的托管状态
    /// </summary>
    public enum DnscryptState
    {
        /// <summary>
        /// 未运行（从未启动，或被主动停止且不会被网络看护自动恢复）
        /// </summary>
        Stopped,

        /// <summary>
        /// 启动中
        /// </summary>
        Starting,

        /// <summary>
        /// 运行中
        /// </summary>
        Running,

        /// <summary>
        /// 停止中（优雅停机窗口内）
        /// </summary>
        Stopping,

        /// <summary>
        /// 因网络不可用被看护服务暂停，网络恢复后会自动重新启动
        /// </summary>
        PausedByNetwork
    }

    /// <summary>
    /// [PATCH] dnscrypt-proxy 生命周期快照，供日志、UI、健康检查读取
    /// </summary>
    public sealed class DnscryptSnapshot
    {
        /// <summary>
        /// 当前状态
        /// </summary>
        public DnscryptState State { get; init; }

        /// <summary>
        /// 状态原因（人类可读，例如「网络不可用：无可用网关」）
        /// </summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>
        /// 最近一次停机是否为优雅停机（目标自行退出）；false 表示走了强杀兜底
        /// </summary>
        public bool LastStopWasGraceful { get; init; } = true;

        /// <summary>
        /// 是否被网络看护暂停（暂停期间域名测速应跳过）
        /// </summary>
        public bool IsSuspended => State == DnscryptState.PausedByNetwork;

        /// <summary>
        /// 状态变更时间
        /// </summary>
        public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.Now;

        /// <summary>
        /// 初始快照
        /// </summary>
        public static DnscryptSnapshot Initial { get; } = new() { State = DnscryptState.Stopped, Reason = "尚未启动" };

        /// <summary>
        /// 生成新快照（不可变语义，避免并发读到半更新状态）
        /// </summary>
        /// <param name="state"></param>
        /// <param name="reason"></param>
        /// <param name="lastStopWasGraceful"></param>
        /// <returns></returns>
        public static DnscryptSnapshot Create(DnscryptState state, string reason, bool lastStopWasGraceful)
        {
            return new DnscryptSnapshot
            {
                State = state,
                Reason = reason,
                LastStopWasGraceful = lastStopWasGraceful,
                ChangedAt = DateTimeOffset.Now
            };
        }

        /// <summary>
        /// 转换为字符串
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return string.IsNullOrEmpty(this.Reason) ? $"{this.State}" : $"{this.State}（{this.Reason}）";
        }
    }

    /// <summary>
    /// [PATCH] dnscrypt-proxy 生命周期状态源
    /// </summary>
    public interface IDnscryptStateSource
    {
        /// <summary>
        /// 当前快照
        /// </summary>
        DnscryptSnapshot Current { get; }
    }

    /// <summary>
    /// [PATCH] dnscrypt-proxy 生命周期控制器。
    /// 所有方法均幂等且串行：同一时刻只允许一个状态迁移，重复调用直接返回当前结论。
    /// 网络看护、系统注销/关机钩子、（可选的）UI 都走这一个入口，避免多处 Start/Stop 互相踩。
    /// </summary>
    public interface IDnscryptProxyController : IDnscryptStateSource
    {
        /// <summary>
        /// 优雅停止（用户/系统语义的「停」，网络恢复后不会自动复活）
        /// </summary>
        /// <param name="reason">停机原因，写入快照与日志</param>
        /// <param name="cancellationToken"></param>
        /// <returns>true 表示目标自行退出（优雅），false 表示走了强杀兜底或目标本就不在</returns>
        Task<bool> StopAsync(string reason, CancellationToken cancellationToken);

        /// <summary>
        /// 因网络不可用而暂停（快照置为 PausedByNetwork，看护会在新网络可用时自动恢复）
        /// </summary>
        /// <param name="reason"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<bool> SuspendAsync(string reason, CancellationToken cancellationToken);

        /// <summary>
        /// 恢复启动（仅当前状态为 PausedByNetwork 时生效）
        /// </summary>
        /// <param name="reason"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<bool> ResumeAsync(string reason, CancellationToken cancellationToken);
    }

    /// <summary>
    /// [PATCH] 优雅停机与网络看护配置。
    /// 全部取值的默认行为都刻意偏保守：判定不确定时「不动 dnscrypt-proxy」，
    /// 避免因为一次误判把 DNS 防污染整体打掉。
    /// </summary>
    [SuppressMessage("Performance", "CA1819")]
    public sealed class DnscryptLifecycleOptions
    {
        /// <summary>
        /// 配置节名称
        /// </summary>
        public const string SECTION_NAME = "DnscryptLifecycle";

        /// <summary>
        /// 单次优雅停机预算（毫秒）。
        /// 必须显著小于注销/关机的系统时限（控制台进程约 2~5 秒，服务为
        /// WaitToKillServiceTimeout，默认 5 秒），否则系统会直接强杀，优雅无意义。
        /// </summary>
        public int StopBudgetMs { get; set; } = 2500;

        /// <summary>
        /// 优雅停机预算上限（毫秒）。用户填得再大也会被夹到该值以内，
        /// 防止把停机窗口拖成「注销时进程被系统强杀」。
        /// </summary>
        public const int StopBudgetMaxMs = 4000;

        /// <summary>
        /// 交互（进程）模式下是否尝试向 dnscrypt-proxy 投递 CTRL_BREAK 软信号。
        /// auto（默认）：仅在引擎独占（或没有）控制台时启用，避免打扰用户终端；
        /// on：总是尝试；off：禁用，直接走 Kill 兜底。
        /// </summary>
        public string SoftSignal { get; set; } = SOFT_SIGNAL_AUTO;

        /// <summary>软信号：按控制台归属自动决定</summary>
        public const string SOFT_SIGNAL_AUTO = "auto";
        /// <summary>软信号：总是尝试</summary>
        public const string SOFT_SIGNAL_ON = "on";
        /// <summary>软信号：禁用</summary>
        public const string SOFT_SIGNAL_OFF = "off";

        /// <summary>
        /// 是否启用「断网停机 / 恢复启停」看护。
        /// </summary>
        public bool WatchNetwork { get; set; } = true;

        /// <summary>
        /// 看护探测周期（秒）
        /// </summary>
        public int NetworkProbeIntervalSeconds { get; set; } = 15;

        /// <summary>
        /// 连续判定「不可用」达到该次数才停机（抑制拔网线、切 Wi-Fi、VPN 重连等抖动）
        /// </summary>
        public int OfflineConfirmThreshold { get; set; } = 2;

        /// <summary>
        /// 连续判定「可用」达到该次数才重新启动
        /// </summary>
        public int OnlineConfirmThreshold { get; set; } = 1;

        /// <summary>
        /// 单次连通性探测超时（毫秒）
        /// </summary>
        public int NetworkProbeTimeoutMs { get; set; } = 1500;

        /// <summary>
        /// 网络恢复后重新启动前的延迟（秒）。
        /// 接口 Up 不等于链路可用（DHCP/路由/防火墙可能还没就绪），留一个缓冲。
        /// </summary>
        public int RestartDelaySeconds { get; set; } = 3;

        /// <summary>
        /// 判定「不可用」所允许的最严口径：
        /// interfaces（默认）：只看是否存在处于 Up 且配置了网关的非回环接口 —— 零误伤口径；
        /// probe：接口口径之外，再对 FallbackDns 做 TCP 探测 —— 更贴近「真的上不了网」，
        /// 但 captive portal / 只放行 443 的内网可能误判，故不作为默认。
        /// </summary>
        public string NetworkCheck { get; set; } = NETWORK_CHECK_INTERFACES;

        /// <summary>网络口径：仅看接口与网关</summary>
        public const string NETWORK_CHECK_INTERFACES = "interfaces";
        /// <summary>网络口径：接口 + TCP 探测</summary>
        public const string NETWORK_CHECK_PROBE = "probe";

        /// <summary>
        /// 停机期间是否顺带清空 DNS 解析缓存。
        /// 断网时缓存里的 IP 仍然是唯一可用的信息，默认不清空。
        /// </summary>
        public bool ClearDnsCacheWhenOffline { get; set; } = false;

        /// <summary>
        /// 把用户配置夹到安全区间
        /// </summary>
        public DnscryptLifecycleOptions Normalize()
        {
            this.StopBudgetMs = Math.Clamp(this.StopBudgetMs, 300, StopBudgetMaxMs);
            this.NetworkProbeIntervalSeconds = Math.Clamp(this.NetworkProbeIntervalSeconds, 5, 600);
            this.OfflineConfirmThreshold = Math.Clamp(this.OfflineConfirmThreshold, 1, 10);
            this.OnlineConfirmThreshold = Math.Clamp(this.OnlineConfirmThreshold, 1, 10);
            this.NetworkProbeTimeoutMs = Math.Clamp(this.NetworkProbeTimeoutMs, 200, 5000);
            this.RestartDelaySeconds = Math.Clamp(this.RestartDelaySeconds, 0, 120);

            if (string.Equals(SOFT_SIGNAL_ON, this.SoftSignal, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SOFT_SIGNAL_OFF, this.SoftSignal, StringComparison.OrdinalIgnoreCase))
            {
                this.SoftSignal = this.SoftSignal.ToLowerInvariant();
            }
            else
            {
                this.SoftSignal = SOFT_SIGNAL_AUTO;
            }

            if (string.Equals(NETWORK_CHECK_PROBE, this.NetworkCheck, StringComparison.OrdinalIgnoreCase))
            {
                this.NetworkCheck = NETWORK_CHECK_PROBE;
            }
            else
            {
                this.NetworkCheck = NETWORK_CHECK_INTERFACES;
            }
            return this;
        }

        /// <summary>
        /// [PATCH] 从配置节手动绑定。
        /// 不用 services.Configure&lt;T&gt;(IConfiguration) ：那条路径是纯反射（ConfigurationBinder），
        /// PublishTrimmed 下会报 IL2026，而且一旦 setter 被裁，配置会「键在、值全丢」地静默回退。
        /// 这里只走字符串索引器 + TryParse，零反射，顺便在绑完当场 Normalize，
        /// 使下游拿到的永远是夹过安全区间的值。
        /// </summary>
        /// <param name="section">DnscryptLifecycle 配置节</param>
        /// <param name="options">待填充实例</param>
        public static void Bind(IConfigurationSection section, DnscryptLifecycleOptions options)
        {
            options.StopBudgetMs = GetInt32(section, nameof(StopBudgetMs), options.StopBudgetMs);
            options.SoftSignal = GetString(section, nameof(SoftSignal), options.SoftSignal);
            options.WatchNetwork = GetBoolean(section, nameof(WatchNetwork), options.WatchNetwork);
            options.NetworkProbeIntervalSeconds = GetInt32(section, nameof(NetworkProbeIntervalSeconds), options.NetworkProbeIntervalSeconds);
            options.OfflineConfirmThreshold = GetInt32(section, nameof(OfflineConfirmThreshold), options.OfflineConfirmThreshold);
            options.OnlineConfirmThreshold = GetInt32(section, nameof(OnlineConfirmThreshold), options.OnlineConfirmThreshold);
            options.NetworkProbeTimeoutMs = GetInt32(section, nameof(NetworkProbeTimeoutMs), options.NetworkProbeTimeoutMs);
            options.RestartDelaySeconds = GetInt32(section, nameof(RestartDelaySeconds), options.RestartDelaySeconds);
            options.NetworkCheck = GetString(section, nameof(NetworkCheck), options.NetworkCheck);
            options.ClearDnsCacheWhenOffline = GetBoolean(section, nameof(ClearDnsCacheWhenOffline), options.ClearDnsCacheWhenOffline);
            options.Normalize();
        }

        private static int GetInt32(IConfigurationSection section, string key, int fallback)
        {
            var value = section[key];
            if (string.IsNullOrEmpty(value) || int.TryParse(value, out var result) == false)
            {
                return fallback;
            }
            return result;
        }

        private static bool GetBoolean(IConfigurationSection section, string key, bool fallback)
        {
            var value = section[key];
            if (string.IsNullOrEmpty(value) || bool.TryParse(value, out var result) == false)
            {
                return fallback;
            }
            return result;
        }

        private static string GetString(IConfigurationSection section, string key, string fallback)
        {
            var value = section[key];
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
