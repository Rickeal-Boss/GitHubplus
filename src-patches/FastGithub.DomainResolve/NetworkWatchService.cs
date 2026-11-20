using FastGithub.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// [PATCH] 网络看护：断网时优雅暂停 dnscrypt-proxy，网络恢复后再拉起。
    ///
    /// 为什么值得做：dnscrypt-proxy 启动时要解析并握手 DoH/DoQ 递归、可能拉取 relays 列表，
    /// 断网状态下它会持续重试并空占 5533；引擎侧的测速循环也会对着一个不会应答的解析器
    /// 每 15 秒空转一轮（日志噪声 + Socket 抖动）。网络恢复后 dnscrypt 是否自愈取决于
    /// 它自己的 resolver 健康检查，并不及时，因此这里显式重拉一次。
    ///
    /// 三条纪律：
    /// 1) 判定宁松勿严：默认只看「有没有 Up 且配了网关/可路由地址的接口」这一零误伤口径；
    ///    要求连续 OfflineConfirmThreshold 次成立才动手，避免拔网线、切 Wi-Fi、VPN 重连引发停机风暴。
    /// 2) 只碰自己启起来的东西：仅在 Running / Starting 时暂停，仅暂停在 PausedByNetwork 时恢复；
    ///    用户主动停止（Stopped）永远不自动复活。
    /// 3) 串行化：起停统一经 IDnscryptProxyController，内部由信号量保证同一时刻只有一个迁移。
    /// </summary>
    sealed class NetworkWatchService : BackgroundService
    {
        private readonly IDnscryptProxyController controller;
        private readonly DnsClient dnsClient;
        private readonly IPAddressService addressService;
        private readonly IOptionsMonitor<DnscryptLifecycleOptions> lifecycleOptions;
        private readonly IOptions<FastGithubOptions> fastGithubOptions;
        private readonly IHostApplicationLifetime lifetime;
        private readonly ILogger<NetworkWatchService> logger;

        /// <summary>
        /// 网络状态变化事件驱动的「立刻复检」信号（maxCount=1，多余事件直接丢弃）
        /// </summary>
        private readonly SemaphoreSlim probeSignal = new(0, 1);

        /// <summary>
        /// 连续不可用 / 可用计数
        /// </summary>
        private int offlineStreak;
        private int onlineStreak;

        /// <summary>
        /// 是否已经产出过第一次判定（首轮只观测，不动作）
        /// </summary>
        private bool primed;

        /// <summary>
        /// 上一次实际探测的时戳（Environment.TickCount64）。
        /// 用途：系统网络事件是「成束」的（拔网卡一次会同时甩出 NetworkAvailabilityChanged
        /// 与多个 NetworkAddressChanged），若每次都立即复检，几毫秒内的连续两次探测
        /// 就会把 OfflineConfirmThreshold 坐满，抖动抑制形同虚设。
        /// 这里给事件驱动的复检加一个最小间隔，而不是在事件线程里 Sleep（那会白白占着线程池）。
        /// </summary>
        private long lastProbeTick;

        /// <summary>
        /// 事件驱动复检的最小间隔
        /// </summary>
        private static readonly TimeSpan minProbeGap = TimeSpan.FromSeconds(3d);

        /// <summary>
        /// 网络看护
        /// </summary>
        public NetworkWatchService(
            IDnscryptProxyController controller,
            DnsClient dnsClient,
            IPAddressService addressService,
            IOptionsMonitor<DnscryptLifecycleOptions> lifecycleOptions,
            IOptions<FastGithubOptions> fastGithubOptions,
            IHostApplicationLifetime lifetime,
            ILogger<NetworkWatchService> logger)
        {
            this.controller = controller;
            this.dnsClient = dnsClient;
            this.addressService = addressService;
            this.lifecycleOptions = lifecycleOptions;
            this.fastGithubOptions = fastGithubOptions;
            this.lifetime = lifetime;
            this.logger = logger;
        }

        /// <summary>
        /// 后台任务
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var options = this.lifecycleOptions.CurrentValue;
            if (options.WatchNetwork == false)
            {
                this.logger.LogDebug("网络看护已配置为关闭（DnscryptLifecycle:WatchNetwork=false）");
                return;
            }

            this.SubscribeNetworkChange();
            var interval = TimeSpan.FromSeconds(options.NetworkProbeIntervalSeconds);
            this.logger.LogInformation("网络看护已启动：口径 {Check}，周期 {Interval}s，连续 {Off} 次不可用才暂停，连续 {On} 次可用才恢复",
                options.NetworkCheck, (int)interval.TotalSeconds, options.OfflineConfirmThreshold, options.OnlineConfirmThreshold);

            try
            {
                while (stoppingToken.IsCancellationRequested == false)
                {
                    try
                    {
                        await this.WaitNextRoundAsync(interval, stoppingToken);
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var online = await this.IsOnlineAsync(stoppingToken);
                        this.lastProbeTick = Environment.TickCount64;
                        if (online)
                        {
                            this.onlineStreak++;
                            this.offlineStreak = 0;
                        }
                        else
                        {
                            this.offlineStreak++;
                            this.onlineStreak = 0;
                        }

                        // 首轮只建立基线：引擎启动瞬间网络栈可能还没就绪，此时就判死刑没有意义
                        if (this.primed == false)
                        {
                            this.primed = true;
                            this.logger.LogDebug("网络看护首次探测结果：{Online}", online ? "可用" : "不可用");
                            continue;
                        }

                        await this.ApplyAsync(online, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // [PATCH 0007 / S-3] 单轮异常不得终结整个看护循环。
                        // 原实现把 catch 放在 while 外层：一次 IsOnlineAsync / ApplyAsync 的意外异常
                        // 就退出循环，finally 顺手 Dispose 掉 probeSignal，之后网络恢复永久失效
                        // （且后续任何唤醒都会抛 ObjectDisposedException）。现在只跳过本轮、
                        // 清零连续判定计数，下一轮照常判定；probeSignal 只在宿主真正停机时释放。
                        this.logger.LogWarning(ex, "网络看护本轮异常，已跳过并继续下一轮");
                        this.offlineStreak = 0;
                        this.onlineStreak = 0;
                        this.primed = false;
                        await Task.Delay(TimeSpan.FromSeconds(2d), stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // 看护绝不能把宿主带崩（仅覆盖订阅/退订等循环外异常）
                this.logger.LogWarning(ex, "网络看护异常退出，dnscrypt-proxy 保持当前状态");
            }
            finally
            {
                this.UnsubscribeNetworkChange();
                this.probeSignal.Dispose();
            }
        }

        /// <summary>
        /// 按判定结果迁移 dnscrypt-proxy 状态
        /// </summary>
        private async Task ApplyAsync(bool online, CancellationToken stoppingToken)
        {
            var options = this.lifecycleOptions.CurrentValue;
            var snapshot = this.controller.Current;

            if (online == false)
            {
                if (this.offlineStreak < options.OfflineConfirmThreshold)
                {
                    return;
                }
                if (snapshot.State is DnscryptState.Running or DnscryptState.Starting)
                {
                    var reason = $"网络不可用（连续 {this.offlineStreak} 次判定，口径 {options.NetworkCheck}）";
                    this.logger.LogInformation("暂停 dnscrypt-proxy：{Reason}", reason);
                    await this.controller.SuspendAsync(reason, stoppingToken);
                    // [PATCH 0007] ClearDnsCacheWhenOffline 原本是死配置：属性、绑定、
                    // appsettings 三处齐全但运行期零读取点，注释说「默认不清」、
                    // 实现却从不读它。现在接上：默认 false 时行为完全不变（断网期保留
                    // 缓存里的 IP 供离线使用），true 时额外清空。恢复分支的 ClearCaches
                    // 保持不变，用于剔除断网期间写入的空解析（最长缓存 10 分钟）。
                    if (options.ClearDnsCacheWhenOffline)
                    {
                        this.dnsClient.ClearCaches();
                    }
                }
                return;
            }

            if (this.onlineStreak < options.OnlineConfirmThreshold)
            {
                return;
            }
            if (snapshot.State != DnscryptState.PausedByNetwork)
            {
                return;
            }
            if (stoppingToken.IsCancellationRequested || this.lifetime.ApplicationStopping.IsCancellationRequested)
            {
                // 宿主正在关闭，不要在此刻复活子进程
                return;
            }

            // 接口 Up 不等于链路可用：DHCP/DNS/路由可能还在收敛，给一个缓冲再拉起
            if (options.RestartDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.RestartDelaySeconds), stoppingToken);
            }

            this.logger.LogInformation("网络已恢复（连续 {Count} 次判定可用），重新启动 dnscrypt-proxy", this.onlineStreak);
            if (await this.controller.ResumeAsync("网络恢复", stoppingToken))
            {
                // [PATCH] 清掉断网期间写入的空解析/不可用缓存（空解析最长缓存 10 分钟），
                // 否则恢复后命中缓存的域名最长 10 分钟拿到空解析，dnscrypt 已重启也轮不到。
                this.dnsClient.ClearCaches();
                // [PATCH 0006b] D-2：断网期写入的 TimeSpan.MaxValue 会让恢复后的 IP 优选
                // 继续跳过这些 IP 最长 1 分钟。只清失败条目，保留正常测速结果。
                // IPAddressService 由 AddDomainResolve() 注册为 Singleton，且该方法在
                // AddDnscryptLifecycle() 之前调用，此处可直接注入。
                // 注意：这条只覆盖「断网 -> 恢复」；纯空闲退化由 IPAddressService 的
                // 短过期 + DomainResolver 的 stale-while-error 处理。
                this.addressService.ClearFailureCaches();
            }
        }

        /// <summary>
        /// 等到下一轮：固定周期，但网络状态变化事件可以提前唤醒
        /// </summary>
        private async Task WaitNextRoundAsync(TimeSpan interval, CancellationToken stoppingToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = Task.Delay(interval, cts.Token);
                var signal = this.probeSignal.WaitAsync(cts.Token);
                await Task.WhenAny(delay, signal);
                cts.Cancel();
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// 连通性判定
        /// </summary>
        private async Task<bool> IsOnlineAsync(CancellationToken stoppingToken)
        {
            if (HasUsableInterface() == false)
            {
                return false;
            }

            var options = this.lifecycleOptions.CurrentValue;
            if (options.NetworkCheck != DnscryptLifecycleOptions.NETWORK_CHECK_PROBE)
            {
                return true;
            }

            var targets = this.fastGithubOptions.Value.FallbackDns;
            if (targets == null || targets.Length == 0)
            {
                // 没有可探测目标时退回接口口径，绝不因为「探测不了」就判定断网
                return true;
            }

            foreach (var target in targets)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (await TryTcpConnectAsync(target, options.NetworkProbeTimeoutMs, stoppingToken))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 是否存在处于 Up 状态、且配有网关或可路由单播地址的非回环接口
        /// </summary>
        private static bool HasUsableInterface()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    try
                    {
                        var properties = nic.GetIPProperties();
                        if (properties.GatewayAddresses.Any(item => IsRoutable(item.Address)))
                        {
                            return true;
                        }
                        // 无网关的场景：点对点/蜂窝/部分 VPN，只要有可路由单播地址也算在线
                        if (properties.UnicastAddresses.Any(item => IsRoutable(item.Address)))
                        {
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        // 单块网卡取不到属性不影响整体判定
                    }
                }
            }
            catch (Exception)
            {
                // 取不到网卡信息时按「在线」处理：看护的铁律是判定不确定时绝不停机
                return true;
            }
            return false;
        }

        /// <summary>
        /// 是否为可路由地址（排除回环、任意地址、链路本地/APIPA、组播）
        /// </summary>
        private static bool IsRoutable(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                return false;
            }
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Teredo)
            {
                return false;
            }
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
            {
                // 169.254.0.0/16 —— 拿不到 DHCP 时的 APIPA 地址，说明没通
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    return false;
                }
                return true;
            }
            return true;
        }

        /// <summary>
        /// TCP 连通性探测（不用 ICMP：出站 ICMP 常被过滤，会把在线判成离线）
        /// </summary>
        private static async Task<bool> TryTcpConnectAsync(IPEndPoint target, int timeoutMs, CancellationToken stoppingToken)
        {
            try
            {
                using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
                await socket.ConnectAsync(target, cts.Token);
                return socket.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 订阅系统网络事件，用于把最长一个周期的等待压缩到秒级
        /// </summary>
        private void SubscribeNetworkChange()
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }

        /// <summary>
        /// 退订系统网络事件
        /// </summary>
        private void UnsubscribeNetworkChange()
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        }

        /// <summary>
        /// 网络事件回调：只负责把下一轮提前，判定仍在循环里做，避免在事件线程里起停进程。
        /// 不在事件线程里做任何阻塞等待（网线插拔时事件密集，Sleep 会把线程池占满）；
        /// 抖动抑制交给循环内的连续计数 + minProbeGap。
        /// </summary>
        private void OnNetworkChanged(object? sender, EventArgs e)
        {
            // 刚探测过就不重复唤醒：事件风暴期间保持探测节奏稳定
            if (Environment.TickCount64 - this.lastProbeTick < (long)minProbeGap.TotalMilliseconds)
            {
                return;
            }

            try
            {
                this.probeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已有待处理的复检，忽略
            }
            catch (ObjectDisposedException)
            {
                // 看护正在退出
            }
        }
    }

    /// <summary>
    /// [PATCH] dnscrypt-proxy 生命周期服务注册扩展
    /// </summary>
    public static class DnscryptLifecycleServiceCollectionExtensions
    {
        /// <summary>
        /// 注册优雅停机与网络看护
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration">DnscryptLifecycle 配置节</param>
        /// <returns></returns>
        public static IServiceCollection AddDnscryptLifecycle(this IServiceCollection services, IConfiguration configuration)
        {
            // 手动绑定（详见 DnscryptLifecycleOptions.Bind）：避开 ConfigurationBinder 的反射路径，
            // 既消掉 PublishTrimmed 下的 IL2026，也堵住「setter 被裁 -> 配置静默失效」这一类坑。
            var section = configuration.GetSection(DnscryptLifecycleOptions.SECTION_NAME);
            services.Configure<DnscryptLifecycleOptions>(options => DnscryptLifecycleOptions.Bind(section, options));
            services.TryAddSingleton<IDnscryptProxyController>(provider => (IDnscryptProxyController)provider.GetRequiredService<DnscryptProxy>());
            services.AddHostedService<NetworkWatchService>();
            return services;
        }
    }
}
