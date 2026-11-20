using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析后台服务
    /// </summary>
    sealed class DomainResolveHostedService : BackgroundService
    {
        private readonly DnscryptProxy dnscryptProxy;
        private readonly IDomainResolver domainResolver;
        private readonly ILogger<DomainResolveHostedService> logger;
        private readonly TimeSpan dnscryptProxyInitDelay = TimeSpan.FromSeconds(5d);
        /// <summary>
        /// 测速周期
        /// 原值为1秒：每秒遍历全部已记录域名并对每个未缓存IP并发拨测(单Socket超时5秒)，
        /// 对持续不可达的域名会长期维持十几个并发Socket且每轮重建。放宽到15秒。
        /// </summary>
        private readonly TimeSpan testPeriodTimeSpan = TimeSpan.FromSeconds(15d);

        /// <summary>
        /// 域名解析后台服务
        /// </summary>
        /// <param name="dnscryptProxy"></param>
        /// <param name="domainResolver"></param>
        public DomainResolveHostedService(
            DnscryptProxy dnscryptProxy,
            IDomainResolver domainResolver,
            ILogger<DomainResolveHostedService> logger)
        {
            this.dnscryptProxy = dnscryptProxy;
            this.domainResolver = domainResolver;
            this.logger = logger;
        }

        /// <summary>
        /// 后台任务
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await this.dnscryptProxy.StartAsync(stoppingToken);
                await Task.Delay(dnscryptProxyInitDelay, stoppingToken);

                while (stoppingToken.IsCancellationRequested == false)
                {
                    // [PATCH] dnscrypt-proxy 不在运行时（断网被看护暂停 / 启动失败 / 已被优雅停机）
                    // 跳过本轮测速：此时只会得到成批超时与无意义日志，还会白占 Socket 并发。
                    if (this.ShouldSkipTestSpeed())
                    {
                        await Task.Delay(this.testPeriodTimeSpan, stoppingToken);
                        continue;
                    }

                    await this.domainResolver.TestSpeedAsync(stoppingToken);
                    await Task.Delay(this.testPeriodTimeSpan, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "域名解析异常");
            }
        }

        /// <summary>
        /// [PATCH] 是否应跳过本轮测速
        /// </summary>
        private bool ShouldSkipTestSpeed()
        {
            var snapshot = this.dnscryptProxy.Current;
            if (snapshot.IsSuspended || snapshot.State == DnscryptState.Stopping)
            {
                this.logger.LogDebug("dnscrypt-proxy 当前为 {State}，跳过本轮测速", snapshot.State);
                return true;
            }

            // Starting / Running 但端点尚未就绪（启动未完成）也跳过
            return this.dnscryptProxy.LocalEndPoint == null;
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // [PATCH] 原实现是同步 Stop()（内部 process.Kill() 硬杀）。
            // 改走异步优雅停机：服务形态发 SERVICE_CONTROL_STOP 并等 SERVICE_STOPPED，
            // 进程形态先投软信号、预算内等它自行退出，超时才强杀。
            // 这一步必须在宿主关闸前完成，主机会在后台超时后直接把进程抹掉，
            // 所以预算上限在 DnscryptLifecycle:StopBudgetMs 里卡到系统时限以内。
            try
            {
                await this.dnscryptProxy.StopAsync("宿主停止", cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "dnscrypt-proxy 优雅停机异常");
            }

            await base.StopAsync(cancellationToken);
        }
    }
}
