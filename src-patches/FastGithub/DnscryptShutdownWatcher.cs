using FastGithub.DomainResolve;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub
{
    /// <summary>
    /// [PATCH] 注销 / 关机时的 dnscrypt-proxy 优雅停机钩子（Windows）。
    ///
    /// 为什么必须单独做这件事：
    /// 1) .NET 的 ConsoleLifetime 只把 Ctrl+C / Ctrl+Break 映射为「停止主机」。
    ///    而控制台关闭、用户注销、系统关机走的是 CTRL_CLOSE_EVENT / CTRL_LOGOFF_EVENT /
    ///    CTRL_SHUTDOWN_EVENT，不经过 Console.CancelKeyPress —— 也就是说这三种场景下
    ///    后台服务的 StopAsync 根本不会被调用，dnscrypt-proxy、hosts 还原、代理绕过表清理全部落空。
    /// 2) 系统不会等异步任务：处理器一旦返回（或超时），进程即被终止。
    ///    所以这里必须在处理器里「同步」把 dnscrypt-proxy 送走，预算取 DnscryptLifecycle:StopBudgetMs
    ///    （该值被夹到 4 秒以内，正是为了对上注销/关机的系统时限）。
    ///
    /// 适用边界（务必看清，避免误以为双路注销都可靠）：
    /// - MSDN 明确 CTRL_LOGOFF_EVENT / CTRL_SHUTDOWN_EVENT 只发给服务进程；交互进程在系统发出
    ///   这两个信号时已被终止；加载 user32/gdi32 的控制台应用同样收不到。
    /// - 另外 AttachConsole / FreeConsole 会重置本进程的控制台处理器表，软信号投递过一次后
    ///   本钩子即失效。
    /// - 因此桌面形态下真正可靠的是 UI 的 Application.SessionEnding → 杀锚点 → host.StopAsync；
    ///   本钩子仅作「直接运行 fastgithub.exe 且控制台未被让出」时的尽力而为兜底，
    ///   CTRL_CLOSE_EVENT（关控制台窗口）是它在控制台形态下唯一稳定命中的事件。
    /// - 服务模式由 WindowsServiceLifetime / SCM 负责停机，不需要本钩子抢占信号语义。
    /// </summary>
    internal sealed class DnscryptShutdownWatcher : IHostedService
    {
        private const uint CTRL_C_EVENT = 0x00000000;
        private const uint CTRL_BREAK_EVENT = 0x00000001;
        private const uint CTRL_CLOSE_EVENT = 0x00000002;
        private const uint CTRL_LOGOFF_EVENT = 0x00000005;
        private const uint CTRL_SHUTDOWN_EVENT = 0x00000006;

        /// <summary>
        /// 非托管回调持有的委托指针，必须保活，被 GC 后回调会踩空
        /// </summary>
        private static ConsoleCtrlDelegate? installedHandler;
        private static readonly object installLock = new();

        private delegate bool ConsoleCtrlDelegate(uint ctrlType);

        private readonly IDnscryptProxyController controller;
        private readonly IHostApplicationLifetime lifetime;
        private readonly ILogger<DnscryptShutdownWatcher> logger;
        private readonly int stopBudgetMs;

        /// <summary>
        /// 注销 / 关机钩子
        /// </summary>
        public DnscryptShutdownWatcher(
            IDnscryptProxyController controller,
            IHostApplicationLifetime lifetime,
            IOptionsMonitor<DnscryptLifecycleOptions> options,
            ILogger<DnscryptShutdownWatcher> logger)
        {
            this.controller = controller;
            this.lifetime = lifetime;
            this.logger = logger;
            this.stopBudgetMs = options.CurrentValue.StopBudgetMs;
        }

        /// <summary>
        /// 注册控制台事件处理器
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows() == false)
            {
                return Task.CompletedTask;
            }

            lock (installLock)
            {
                if (installedHandler != null)
                {
                    return Task.CompletedTask;
                }

                installedHandler = this.OnConsoleCtrlEvent;
                if (SetConsoleCtrlHandler(installedHandler, true) == false)
                {
                    // 注册失败不致命：仍有 UI 的 SessionEnding 与 Job Object 两层兜底
                    installedHandler = null;
                    this.logger.LogWarning("注册控制台事件处理器失败，注销/关机时将只能依赖 UI 通知与 Job Object 兜底（Win32Error={Error}）",
                        Marshal.GetLastWin32Error());
                    return Task.CompletedTask;
                }

                this.logger.LogDebug("已注册控制台事件处理器：注销/关机时将优雅停止 {Name}", "dnscrypt-proxy");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 摘除控制台事件处理器
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows() == false)
            {
                return Task.CompletedTask;
            }

            lock (installLock)
            {
                var handler = installedHandler;
                if (handler == null)
                {
                    return Task.CompletedTask;
                }

                SetConsoleCtrlHandler(handler, false);
                installedHandler = null;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 控制台事件回调
        /// </summary>
        private bool OnConsoleCtrlEvent(uint ctrlType)
        {
            switch (ctrlType)
            {
                case CTRL_C_EVENT:
                case CTRL_BREAK_EVENT:
                    // 交回默认链路：.NET 的 ConsoleLifetime 会把它转成 host 停止，
                    // 这里返回 false 表示「我不管，继续传」，避免抢掉 Ctrl+C 语义
                    return false;

                case CTRL_CLOSE_EVENT:
                case CTRL_LOGOFF_EVENT:
                case CTRL_SHUTDOWN_EVENT:
                    this.StopBeforeSessionDies(Describe(ctrlType));
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 在系统强杀之前同步完成 dnscrypt-proxy 的优雅停机。
        /// </summary>
        /// <param name="reason"></param>
        private void StopBeforeSessionDies(string reason)
        {
            this.logger.LogWarning("收到 {Reason}，正在优雅停止 dnscrypt-proxy（预算 {Ms}ms）", reason, this.stopBudgetMs);

            // 先触发整机停止：其它后台服务（hosts 还原、代理绕过表清理、Kestrel）在线程池上并行收尾；
            // 本线程则死等 dnscrypt-proxy 这一步，因为它最需要「按时结束」。
            try
            {
                this.lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "触发主机停止异常（忽略）");
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(this.stopBudgetMs + 500));
                var graceful = this.controller.StopAsync($"系统事件：{reason}", cts.Token)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                this.logger.LogInformation("dnscrypt-proxy 停机收尾完成（优雅={Graceful}）", graceful);
            }
            catch (OperationCanceledException)
            {
                this.logger.LogWarning("dnscrypt-proxy 停机超出预算，剩余清理由 Job Object 兜底");
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "dnscrypt-proxy 停机收尾异常，剩余清理由 Job Object 兜底");
            }
        }

        /// <summary>
        /// 事件名转可读文本
        /// </summary>
        private static string Describe(uint ctrlType)
        {
            return ctrlType switch
            {
                CTRL_CLOSE_EVENT => "控制台窗口关闭",
                CTRL_LOGOFF_EVENT => "用户注销",
                CTRL_SHUTDOWN_EVENT => "系统关机",
                _ => $"控制台事件 {ctrlType}"
            };
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handlerRoutine, bool add);
    }
}
