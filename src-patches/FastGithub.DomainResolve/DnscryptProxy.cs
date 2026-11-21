using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static PInvoke.AdvApi32;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// DnscryptProxy服务
    /// </summary>
    sealed class DnscryptProxy : IDnscryptProxyController, IDisposable
    {
        /// <summary>
        /// dnscrypt-proxy 首选监听端口
        /// </summary>
        private const int PREFERRED_PORT = 5533;

        /// <summary>
        /// 端口实探上限：从首选端口起连续尝试的端口个数，超出则退回 GlobalListener 的快照口径
        /// </summary>
        private const int PORT_PROBE_SPAN = 64;

        private readonly ILogger<DnscryptProxy> logger;
        private readonly IOptionsMonitor<DnscryptLifecycleOptions> optionsAccessor;
        private readonly string processName;
        private readonly string serviceName;
        private readonly string exeFilePath;
        private readonly string tomlFilePath;

        /// <summary>
        /// [PATCH] 状态迁移串行化闸门：首启、看护重启、优雅停机三类入口共用同一把锁，
        /// 保证任意时刻只有一个 Start/Stop 在跑（否则 TOML 与端口会互相踩）
        /// </summary>
        private readonly SemaphoreSlim gate = new(1, 1);

        /// <summary>
        /// [PATCH] 停机请求标记：Stop 在 Start 还没跑完时排队等待，若等到超时则置位本标记，
        /// Start 收尾时见到它就立刻自我收尾，避免出现「宿主已走，dnscrypt 刚被拉起来」的孤儿。
        /// [PATCH 0007 / S-1] 同时是「用户/宿主已经要求停止」的粘性意图：
        /// 只由 StartAsync（一次显式启动）解除，绝不在 Stop 自己的 finally 里清掉。
        /// 网络看护的 SuspendAsync（pause=true）不得置位，否则 ResumeAsync 的守卫会永久拦住恢复。
        /// </summary>
        private volatile bool stopRequested;

        /// <summary>
        /// [PATCH] 本次启动是否走的服务模式（Windows 服务托管），决定停机走 SCM 还是走进程
        /// </summary>
        private bool isServiceMode;

        /// <summary>
        /// [PATCH] 承载 dnscrypt-proxy 的 Job Object 句柄。
        /// 本进程持有它，JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 保证：无论引擎如何结束
        /// （优雅停机 / 被强杀 / 崩溃），内核都会终止 Job 内的 dnscrypt-proxy，杜绝孤儿进程。
        /// </summary>
        private IntPtr jobHandle = IntPtr.Zero;

        /// <summary>
        /// 相关进程
        /// </summary>
        private Process? process;

        /// <summary>
        /// 获取监听的节点
        /// </summary>
        public IPEndPoint? LocalEndPoint { get; private set; }

        /// <summary>
        /// [PATCH] 当前生命周期快照
        /// </summary>
        public DnscryptSnapshot Current { get; private set; } = DnscryptSnapshot.Initial;

        /// <summary>
        /// DnscryptProxy服务
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="optionsAccessor">[PATCH] 优雅停机与看护配置</param>
        public DnscryptProxy(ILogger<DnscryptProxy> logger, IOptionsMonitor<DnscryptLifecycleOptions> optionsAccessor)
        {
            const string PATH = "dnscrypt-proxy";
            const string NAME = "dnscrypt-proxy";

            this.logger = logger;
            this.optionsAccessor = optionsAccessor;
            this.processName = NAME;
            this.serviceName = $"{nameof(FastGithub)}.{NAME}";

            // [PATCH] 上游用相对路径（仅有赖 Program.Main 把 CWD 设为 exe 目录才正常）。
            // CWD 一旦不是程序目录，dnscrypt-proxy.exe / toml 就会被按 CWD 解析，
            // 攻击者可在该目录投放同名文件被 requireAdministrator 引擎加载（替换 toml 即掌握 DNS 解析结果）。
            // 这里直接基于程序所在目录拼出绝对路径，彻底消除对 CWD 的依赖：
            // 变量绝对化后，下面交给 ServiceInstallUtil 的服务镜像路径、StartDnscryptProxy 的
            // FileName 与 WorkingDirectory 全部一并变为绝对路径。
            var programDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            this.exeFilePath = Path.Combine(programDirectory, PATH, OperatingSystem.IsWindows() ? $"{NAME}.exe" : NAME);
            this.tomlFilePath = Path.Combine(programDirectory, PATH, $"{NAME}.toml");
        }

        /// <summary>
        /// 启动dnscrypt-proxy
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // [PATCH 0007 / S-1] 显式启动是唯一解除粘性停机意图的地方。
            // 原实现在 StopCoreEntryAsync 的 finally 里无条件清回 false：
            // Stop 排队在 ResumeAsync 持锁启动后面时，TransitionAsync 等闸门超时置位，
            // 又被同一个调用栈立刻清掉，持锁方看到 false，dnscrypt 照常 Running ——
            // 那次停止被静默抹掉。首启时该位本就是 false，清位无副作用。
            this.stopRequested = false;
            return this.TransitionAsync(DnscryptState.Running, "启动", cancellationToken);
        }

        /// <summary>
        /// [PATCH] 优雅停止（用户/系统语义的「停」，网络恢复不会自动复活）
        /// </summary>
        /// <param name="reason"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<bool> StopAsync(string reason, CancellationToken cancellationToken)
        {
            return this.StopCoreEntryAsync(reason, false, cancellationToken);
        }

        /// <summary>
        /// [PATCH] 因网络不可用暂停
        /// </summary>
        /// <param name="reason"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<bool> SuspendAsync(string reason, CancellationToken cancellationToken)
        {
            return this.StopCoreEntryAsync(reason, true, cancellationToken);
        }

        /// <summary>
        /// [PATCH] 网络恢复后重新启动
        /// </summary>
        /// <param name="reason"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<bool> ResumeAsync(string reason, CancellationToken cancellationToken)
        {
            if (this.Current.State != DnscryptState.PausedByNetwork || this.stopRequested)
            {
                return false;
            }

            var state = await this.TransitionAsync(DnscryptState.Running, $"网络恢复重启：{reason}", cancellationToken);
            return state == DnscryptState.Running;
        }

        /// <summary>
        /// [PATCH] 停止服务（保留旧签名，供不带异步上下文的调用方使用）。
        /// 内部走完整优雅停机，超时预算由 DnscryptLifecycle:StopBudgetMs 控制。
        /// </summary>
        public void Stop()
        {
            try
            {
                this.StopCoreEntryAsync("宿主停止", false, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{this.processName}停止失败：{ex.Message}");
            }
        }

        /// <summary>
        /// [PATCH] 状态迁移统一入口：串行 + 幂等 + 状态落快照
        /// </summary>
        /// <param name="target">期望达到的状态（Running 表示启动）</param>
        /// <param name="reason"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>迁移后的状态</returns>
        private async Task<DnscryptState> TransitionAsync(DnscryptState target, string reason, CancellationToken cancellationToken)
        {
            var entered = false;
            try
            {
                try
                {
                    entered = await this.gate.WaitAsync(TimeSpan.FromSeconds(5d), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    entered = false;
                }

                // 拿不到闸门说明有另一个迁移在跑，绝不并发插队；置位停机请求由持锁方收尾
                if (entered == false)
                {
                    this.logger.LogDebug("dnscrypt-proxy 状态迁移等待闸门超时或被打断，忽略本次「{Reason}」", reason);
                    if (target == DnscryptState.Stopped)
                    {
                        this.stopRequested = true;
                    }
                    return this.Current.State;
                }

                if (target == DnscryptState.Running)
                {
                    if (this.Current.State is DnscryptState.Running or DnscryptState.Starting)
                    {
                        this.logger.LogDebug("dnscrypt-proxy 已处于 {State}，跳过重复启动", this.Current.State);
                        return this.Current.State;
                    }
                    if (this.stopRequested)
                    {
                        this.logger.LogInformation("宿主正在停机，取消 dnscrypt-proxy 启动");
                        this.SetState(DnscryptState.Stopped, "宿主停机", this.Current.LastStopWasGraceful);
                        return DnscryptState.Stopped;
                    }

                    // [PATCH 0007 / S-2] 启动前统一回收上一次的 Job / Process / 端点。
                    // 正常断网恢复已经过 StopCoreAsync.finally 清理，这里是幂等的；
                    // 但「进程意外退出」路径只由 Exited 回调把状态落到 PausedByNetwork，
                    // 从不走 StopCoreAsync，残留的 Job 句柄与 Process 组件若不回收，
                    // 每次崩溃复活都会泄漏一对句柄。三个清理本身都幂等，且把
                    // this.process 置 null 后，旧进程迟到的 Exited 会被 ReferenceEquals 守卫挡掉。
                    this.ReleaseJob();
                    this.DisposeProcess();
                    this.LocalEndPoint = null;

                    this.SetState(DnscryptState.Starting, reason, this.Current.LastStopWasGraceful);
                    try
                    {
                        await this.StartCoreAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // [PATCH] 宿主停机触发的取消应交回上层，不记成「启动失败」误导排查
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // [PATCH] 启动失败只降级，不得上抛：上游与 v3.0.x 的 StartAsync 内部有 try/catch，
                        // 异常若冒到 DomainResolveHostedService.ExecuteAsync，会直接终结测速 while 循环，
                        // 进程存活期内再无测速/缓存刷新，新域名永不解析（toml 被锁、目录缺失时高发）。
                        this.logger.LogWarning(ex, $"{this.processName}启动失败：{ex.Message}");
                        // [PATCH] StartCoreAsync 可能在「已创建子进程 / 已绑定 Job」之后才抛异常。
                        // 必须就地回收：否则会留下一个状态为 Stopped、看护永远不会回收的 dnscrypt
                        // 子进程与 Job 句柄（看护只从 Running/Starting 暂停、只从 PausedByNetwork 恢复）。
                        this.LocalEndPoint = null;
                        this.ReleaseJob();
                        this.DisposeProcess();
                        this.SetState(DnscryptState.Stopped, $"启动失败：{ex.Message}", false);
                        return DnscryptState.Stopped;
                    }

                    // [PATCH 0007 / S-2b] Start 收尾复查（R2 沙箱实测竞态的根因修复）：
                    // StartCoreAsync 期间若有 Stop 因 gate 超时置位 stopRequested（S-1 改为粘性意图后，
                    // 该位会一直留着），必须回滚刚拉起的进程，否则宿主已请求停机、dnscrypt 却留在 Running。
                    // 0007 原始补丁只改了 stopRequested 的清位时机（S-1），漏了 Start 收尾复查这一环；
                    // R2 T2（20/20 孤儿）指出缺口，T2b 实证补此段后孤儿归零。
                    // [0008 订正] stopRequested 的置位点只有两处：StopCoreEntryAsync(pause=false)
                    // 与 TransitionAsync 的 gate 超时分支（且仅 target==Stopped），Suspend/pause 方向
                    // 不置位，故本复查实际只覆盖 Stop 方向；R3 A4 若测的是 pause 方向，其机理需另行确认
                    // （gate 超时后 Suspend 直接 return，需靠下一轮 15s 判定再暂停，并非永久孤儿）。
                    // 回滚操作幂等（与启动失败分支一致），Exited 回调的守卫（ReferenceEquals + State==Running）
                    // 会挡掉旧进程迟到事件；复活失败时 TransitionAsync 落 Stopped，本守卫不再满足，无风暴。
                    // 已知边界：本回滚只 ReleaseJob/DisposeProcess，不走 StopCoreAsync，
                    // 服务模式（jobHandle==0）下 SCM 服务与服务定义不会被停止/删除，属极端工况，
                    // 见 README 已知限制；LastStopWasGraceful 沿用旧值也会与实际（强杀兜底）不符。
                    if (this.stopRequested)
                    {
                        this.LocalEndPoint = null;
                        this.ReleaseJob();
                        this.DisposeProcess();
                        this.SetState(DnscryptState.Stopped, "宿主停机（启动收尾回滚）", this.Current.LastStopWasGraceful);
                        this.logger.LogInformation("dnscrypt-proxy 启动期间收到停机请求，已回滚启动并保持 Stopped");
                        return DnscryptState.Stopped;
                    }

                    var running = this.LocalEndPoint != null;
                    this.SetState(running ? DnscryptState.Running : DnscryptState.Stopped,
                        running ? reason : $"{reason}（未成功：进程或监听端点缺失）",
                        this.Current.LastStopWasGraceful);
                    return this.Current.State;
                }

                // 停机方向
                if ((this.Current.State is DnscryptState.Stopped or DnscryptState.PausedByNetwork)
                    && this.process == null && this.jobHandle == IntPtr.Zero)
                {
                    this.SetState(target, reason, this.Current.LastStopWasGraceful);
                    return this.Current.State;
                }

                var graceful = await this.StopCoreAsync(reason, target == DnscryptState.PausedByNetwork);
                this.SetState(target, reason, graceful);
                return this.Current.State;
            }
            finally
            {
                if (entered)
                {
                    this.gate.Release();
                }
            }
        }

        /// <summary>
        /// [PATCH] 停机入口。pause=false 是用户/宿主语义的「停止」，置位粘性意图；
        /// pause=true 是网络看护的「暂停」，绝不能置位（否则 ResumeAsync 的
        /// stopRequested 守卫会永久拦住恢复）。清除点只有 StartAsync。
        /// </summary>
        private async Task<bool> StopCoreEntryAsync(string reason, bool pause, CancellationToken cancellationToken)
        {
            var target = pause ? DnscryptState.PausedByNetwork : DnscryptState.Stopped;
            if (pause == false)
            {
                this.stopRequested = true;
            }
            var state = await this.TransitionAsync(target, reason, cancellationToken);
            return state == target;
        }

        /// <summary>
        /// 启动dnscrypt-proxy
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            // [PATCH] 端口实探取代启动期快照：GlobalListener 的监听集合是 static readonly 的
            // 进程启动快照，运行期重启（断网恢复、注销后重启）时它会认为「5533 仍可用」，
            // 而真实占用者可能刚好还没退干净 —— 结果 dnscrypt 绑不上端口，
            // 但 LocalEndPoint 已被赋成幽灵值，DnsClient 会一直把查询投给一个没人听的端口。
            var port = this.GetLiveAvailablePort(PREFERRED_PORT);
            var localEndPoint = new IPEndPoint(IPAddress.Loopback, port);

            await TomlUtil.SetListensAsync(this.tomlFilePath, localEndPoint, cancellationToken);
            await TomlUtil.SetLogLevelAsync(this.tomlFilePath, 6, cancellationToken);
            await TomlUtil.SetLBStrategyAsync(this.tomlFilePath, "ph", cancellationToken);
            await TomlUtil.SetMinMaxTTLAsync(this.tomlFilePath, TimeSpan.FromMinutes(1d), TimeSpan.FromMinutes(2d), cancellationToken);

            this.isServiceMode = OperatingSystem.IsWindows() && Environment.UserInteractive == false;
            if (this.isServiceMode && OperatingSystem.IsWindows())
            {
                // [PATCH] 看护恢复路径：服务定义若还在（暂停语义下刻意保留），直接 StartService，
                // 避免每次断网恢复都走一遍「删除 + 重建」的 SCM 抖动。
                if (ServiceInstallUtil.TryStartExistingService(this.serviceName) == false)
                {
                    ServiceInstallUtil.StopAndDeleteService(this.serviceName);
                    ServiceInstallUtil.InstallAndStartService(this.serviceName, this.exeFilePath, ServiceStartType.SERVICE_DEMAND_START);
                }
                this.process = this.FindServiceProcess();
            }
            else
            {
                this.process = StartDnscryptProxy();
            }

            if (this.process != null)
            {
                this.LocalEndPoint = localEndPoint;
                this.process.EnableRaisingEvents = true;
                // [PATCH] 进程自行退出（崩溃 / 被外部结束）时同样清空端点，
                // 让 DnsClient 立刻退回 FallbackDns，而不是继续投给已死的端口。
                // [PATCH] ReferenceEquals 守卫（wbs）：旧进程迟到的 Exited 不得清掉新一轮
                // 启动写入的 LocalEndPoint，否则看护刚拉起的端点会被误清。
                var started = this.process;
                started.Exited += (s, e) =>
                {
                    if (ReferenceEquals(this.process, started) == false)
                    {
                        return;
                    }
                    this.LocalEndPoint = null;

                    // [PATCH 0007 / S-2] 进程意外退出（被强杀 / 自身崩溃）时把状态落到
                    // PausedByNetwork，复用 NetworkWatchService 已真机验证过的恢复链：
                    //   下一轮在线判定 -> ResumeAsync -> TransitionAsync(Running) -> StartCoreAsync。
                    // 绝不在事件回调里做异步启动，避免与 gate / 停机链互相踩。
                    // 守卫排除「我们自己发起的停机」：stopRequested 由 StopCoreEntryAsync、
                    // 宿主停机、DnscryptShutdownWatcher 先置位；State==Running 排除启动失败、
                    // 停机中与已暂停；ReferenceEquals 排除旧进程迟到的 Exited（原有守卫）。
                    // 复活失败时 TransitionAsync 会把状态落成 Stopped，本守卫不再满足，
                    // 因此最多尝试一次，天然避免崩溃复活风暴；看护周期 15s + RestartDelay 3s
                    // 即为冷却窗口。
                    if (this.stopRequested == false && this.Current.State == DnscryptState.Running)
                    {
                        this.SetState(DnscryptState.PausedByNetwork,
                            "dnscrypt-proxy 进程意外退出，等待网络看护恢复",
                            this.Current.LastStopWasGraceful);
                        this.logger.LogWarning("{Name} 进程意外退出，已置为待恢复状态（等待看护下一轮拉起）", this.processName);
                    }
                };

                // [PATCH] 只有进程模式才纳入 Job Object 兜底。
                // 服务模式下的进程归 SCM 管：绑进引擎的 KILL_ON_JOB_CLOSE 作业，
                // 引擎一崩，服务进程就被内核强杀，SCM 视角里变成「服务异常终止」，
                // 与它自己的服务生命周期管理互相打架。
                if (this.isServiceMode == false)
                {
                    this.TryBindJobObject(this.process);
                }
            }
        }

        /// <summary>
        /// [PATCH] 定位服务模式下的 dnscrypt-proxy 进程。
        /// 上游只按「进程名 + SessionId==0」取第一个，机器上若已存在其它来源的 dnscrypt-proxy
        /// （用户自装、或另一个 FastGithub 实例），会误认成自己的进程：停机时杀别人的进程，
        /// 或把自己真正的进程漏掉。这里补一层镜像路径比对，比对不了的（无权限取 MainModule）退回原口径。
        /// </summary>
        private Process? FindServiceProcess()
        {
            var candidates = Process.GetProcessesByName(this.processName).Where(item => item.SessionId == 0).ToArray();
            foreach (var candidate in candidates)
            {
                try
                {
                    if (string.Equals(candidate.MainModule?.FileName, this.exeFilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // 32/64 位不匹配或权限不足时取不到 MainModule，继续按名字认定
                    return candidate;
                }
            }

            if (candidates.Length > 0)
            {
                this.logger.LogWarning("未发现镜像路径与 {Exe} 匹配的 {Name} 服务进程，视为服务未成功启动", this.exeFilePath, this.processName);
            }
            return candidates.FirstOrDefault();
        }

        /// <summary>
        /// [PATCH] 优雅停机：服务模式走 SCM 的 SERVICE_CONTROL_STOP，进程模式尽力投递软信号，
        /// 两者都在预算内等待目标自行退出，超时才强杀兜底。
        /// </summary>
        /// <param name="reason">停机原因，仅用于日志</param>
        /// <param name="keepServiceDefinition">true 为暂停语义（保留服务定义，供网络恢复时直接启动）</param>
        /// <returns>true 表示目标自行退出（真正优雅）；false 表示走了强杀兜底</returns>
        private async Task<bool> StopCoreAsync(string reason, bool keepServiceDefinition)
        {
            var budget = this.optionsAccessor.CurrentValue.StopBudgetMs;
            var graceful = true;
            try
            {
                // [PATCH] isServiceMode 本身已蕴含 Windows，再写一层判定只为让 CA1416 认可平台边界。
                if (this.isServiceMode && OperatingSystem.IsWindows())
                {
                    // 服务模式的优雅停机由 SCM 完成（SERVICE_CONTROL_STOP 会让 dnscrypt-proxy 走
                    // 它自己的优雅退出），这里绝不能再 Kill 进程：
                    // 上游在 ControlService(STOP) 之后紧接 process.Kill()，
                    // 等于把刚被礼貌请退出的进程当场打死，dnscrypt 的日志与缓存收尾全部作废。
                    graceful = ServiceInstallUtil.StopService(this.serviceName, budget);
                    if (graceful == false)
                    {
                        this.logger.LogWarning("{Name} 服务未在 {Ms}ms 内优雅停止，改用进程级兜底清理", this.processName, budget);
                        this.KillServiceProcess();
                    }

                    // 暂停语义（断网）保留服务定义，网络恢复时 StartService 即可，避免反复删建服务；
                    // 真停语义连定义一起删除，与上游行为一致（不给系统留残余服务）。
                    if (keepServiceDefinition == false)
                    {
                        ServiceInstallUtil.DeleteService(this.serviceName);
                    }

                    // 服务形态下只等、不信号、不 Kill：进程句柄可能还没收到退出事件，
                    // 此时再 Kill 就是把 SCM 正在礼貌收尾的进程当场打死。
                    if (this.process != null)
                    {
                        var waited = await WaitForExitAsync(this.process, TimeSpan.FromMilliseconds(Math.Min(budget, 800)));
                        if (waited == false)
                        {
                            this.logger.LogDebug("{Name} 服务进程句柄尚未确认退出（SCM 已接手），不再干预", this.processName);
                        }
                    }
                }
                else if (this.process != null)
                {
                    var processGraceful = await this.TerminateProcessAsync(this.process, budget);
                    graceful &= processGraceful;
                }

                if (this.isServiceMode == false)
                {
                    this.KillStrays();
                }
                return graceful;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{this.processName}停止失败：{ex.Message}");
                return false;
            }
            finally
            {
                this.LocalEndPoint = null;
                // [PATCH] 释放 Job：句柄归零时内核会扫掉 Job 里任何残留进程，
                // 这是「绝不遗留孤儿」的最后一道兜底，也修掉了原实现从不 CloseHandle 的句柄泄漏。
                this.ReleaseJob();
                this.DisposeProcess();
            }
        }

        /// <summary>
        /// [PATCH] 服务模式下的进程级兜底清理：只杀镜像路径与自己 exe 一致的同名进程。
        /// 仅在 SCM 优雅停止超时后调用，绝不碰别人安装的 dnscrypt-proxy。
        /// </summary>
        private void KillServiceProcess()
        {
            if (OperatingSystem.IsWindows() == false)
            {
                return;
            }

            try
            {
                foreach (var item in Process.GetProcessesByName(this.processName))
                {
                    using (item)
                    {
                        try
                        {
                            if (item.HasExited || string.Equals(item.MainModule?.FileName, this.exeFilePath, StringComparison.OrdinalIgnoreCase) == false)
                            {
                                continue;
                            }
                            item.Kill();
                            item.WaitForExit(1000);
                        }
                        catch (Exception)
                        {
                            // 已退出 / 无权访问，忽略
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "清理 {Name} 服务进程时异常", this.processName);
            }
        }

        /// <summary>
        /// [PATCH] 进程模式停机：软信号 → 等待 → 强杀兜底。
        /// 返回 true 仅代表目标在收到软信号后自行退出。
        /// </summary>
        private async Task<bool> TerminateProcessAsync(Process target, int budgetMs)
        {
            try
            {
                if (target.HasExited)
                {
                    return true;
                }

                var sw = Stopwatch.StartNew();
                if (this.CanUseSoftSignal())
                {
                    // 软信号预算取整体的一半，剩下一半留给强杀与等待，保证总耗时可控
                    var signalSlice = TimeSpan.FromMilliseconds(Math.Max(300, budgetMs / 2));
                    if (ProcessGracefulStop.TrySignal(target.Id, this.logger))
                    {
                        if (await WaitForExitAsync(target, signalSlice))
                        {
                            this.logger.LogInformation("{Name} 已优雅退出（{Ms}ms，PID={Pid}）", this.processName, sw.ElapsedMilliseconds, target.Id);
                            return true;
                        }
                        this.logger.LogWarning("{Name} 收到软信号后 {Ms}ms 内未退出，改用强杀兜底（PID={Pid}）", this.processName, signalSlice.TotalMilliseconds, target.Id);
                    }
                }

                try
                {
                    if (target.HasExited == false)
                    {
                        target.Kill();
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning("{Name} 强杀失败：{Message}", this.processName, ex.Message);
                }

                var remainSlice = TimeSpan.FromMilliseconds(Math.Max(300, budgetMs - (int)sw.ElapsedMilliseconds));
                if (await WaitForExitAsync(target, remainSlice) == false)
                {
                    // 进程仍在：Job Object 释放时会由内核兜底清理
                    this.logger.LogWarning("{Name} 强杀后仍未在 {Ms}ms 内退出，交由 Job Object 兜底清理", this.processName, remainSlice.TotalMilliseconds);
                }
                return false;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning("{Name} 停机异常：{Message}", this.processName, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// [PATCH] 是否允许投递软信号（CTRL_BREAK）。
        /// auto 口径下要求引擎的控制台是独占的 —— 用户在 cmd/PowerShell 里手动跑 fastgithub.exe 时不动他的控制台。
        /// </summary>
        private bool CanUseSoftSignal()
        {
            // 服务模式归 SCM 管，它的优雅停机走 SERVICE_CONTROL_STOP；
            // 服务进程在 Session 0，AttachConsole 也不可能成功。
            if (this.isServiceMode)
            {
                return false;
            }

            // 非 Windows（systemd / 终端前台）走 SIGTERM，同样是官方支持的优雅退出通道
            if (OperatingSystem.IsWindows() == false)
            {
                return true;
            }

            var softSignal = this.optionsAccessor.CurrentValue.SoftSignal;
            if (string.Equals(softSignal, DnscryptLifecycleOptions.SOFT_SIGNAL_OFF, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (string.Equals(softSignal, DnscryptLifecycleOptions.SOFT_SIGNAL_ON, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return ProcessGracefulStop.IsConsoleExclusive();
        }

        /// <summary>
        /// [PATCH] 兜底清理：杀掉同名但未被本实例跟踪的 dnscrypt-proxy。
        /// 覆盖「本实例 process 为空（取句柄失败）但进程确实起来了」这类漏网场景。
        /// 只匹配自己目录下的镜像，绝不碰别人安装的 dnscrypt-proxy。
        /// </summary>
        private void KillStrays()
        {
            if (OperatingSystem.IsWindows() == false)
            {
                return;
            }

            try
            {
                foreach (var item in Process.GetProcessesByName(this.processName))
                {
                    using (item)
                    {
                        if (this.process != null && item.Id == this.process.Id)
                        {
                            continue;
                        }
                        try
                        {
                            if (string.Equals(item.MainModule?.FileName, this.exeFilePath, StringComparison.OrdinalIgnoreCase) == false)
                            {
                                continue;
                            }
                            if (item.HasExited == false)
                            {
                                this.logger.LogWarning("清理残留的 {Name}（PID={Pid}）", this.processName, item.Id);
                                item.Kill();
                                item.WaitForExit(1000);
                            }
                        }
                        catch (Exception)
                        {
                            // 已退出 / 无权限读取模块信息，忽略
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "清理残留 {Name} 时异常", this.processName);
            }
        }

        /// <summary>
        /// [PATCH] 等待进程退出
        /// </summary>
        private static async Task<bool> WaitForExitAsync(Process target, TimeSpan timeout)
        {
            if (target.HasExited)
            {
                return true;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void onExited(object? sender, EventArgs e) => tcs.TrySetResult(true);
            target.Exited += onExited;
            try
            {
                if (target.HasExited)
                {
                    return true;
                }
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, CancellationToken.None));
                return completed == tcs.Task && target.HasExited;
            }
            catch (Exception)
            {
                return target.HasExited;
            }
            finally
            {
                target.Exited -= onExited;
            }
        }

        /// <summary>
        /// [PATCH] 端口实探：优先端口被占时逐个向后试绑，避免运行期重启时
        /// 依赖启动期快照拿到一个已被占用的端口
        /// </summary>
        private int GetLiveAvailablePort(int preferredPort)
        {
            try
            {
                for (var port = preferredPort; port < preferredPort + PORT_PROBE_SPAN; port++)
                {
                    if (IsPortBindable(port))
                    {
                        return port;
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "端口实探异常，退回快照口径");
            }
            return GlobalListener.GetAvailablePort(preferredPort);
        }

        /// <summary>
        /// [PATCH] 回环上 TCP + UDP 都能绑才算可用（dnscrypt-proxy 两者都监听）
        /// </summary>
        private static bool IsPortBindable(int port)
        {
            try
            {
                using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException)
            {
                return false;
            }

            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// [PATCH] 写入状态快照
        /// </summary>
        private void SetState(DnscryptState state, string reason, bool lastStopWasGraceful)
        {
            this.Current = DnscryptSnapshot.Create(state, reason, lastStopWasGraceful);
        }

        /// <summary>
        /// 启动DnscryptProxy进程
        /// </summary> 
        /// <returns></returns>
        private Process? StartDnscryptProxy()
        {
            // [PATCH] 必须以 CREATE_NEW_PROCESS_GROUP 启动：ProcessGracefulStop.TrySignal 的
            // GenerateConsoleCtrlEvent(CTRL_BREAK, pid) 第二个参数是「进程组 ID」，
            // 只有带该标志创建的进程才自成一组（组 ID == 自身 PID），软信号才送得达。
            // 实测（2026-11-20 Win11）：原 Process.Start（无此标志）下发送 API 返回成功
            // 但子进程永远收不到 CTRL_BREAK，软停机静默失败、每次落到强杀兜底。
            // 失败退回 Process.Start：软信号自动降级为强杀，行为不劣于改前。
            if (OperatingSystem.IsWindows())
            {
                var started = StartAsProcessGroup(this.exeFilePath, Path.GetDirectoryName(this.exeFilePath));
                if (started != null)
                {
                    return started;
                }
            }

            return Process.Start(new ProcessStartInfo
            {
                FileName = this.exeFilePath,
                WorkingDirectory = Path.GetDirectoryName(this.exeFilePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        /// <summary>
        /// [PATCH] CreateProcessW 启动（CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW）。
        /// 成功返回已包装的 Process；失败返回 null（调用方退回 Process.Start）。
        /// </summary>
        private Process? StartAsProcessGroup(string exeFilePath, string? workingDirectory)
        {
            var commandLine = new StringBuilder($"\"{exeFilePath}\"");
            var startupInfo = new STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf<STARTUPINFO>();
            var processInformation = new PROCESS_INFORMATION();
            var created = false;

            try
            {
                if (CreateProcessW(
                        exeFilePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out processInformation) == false)
                {
                    this.logger.LogWarning("CreateProcessW 启动 {Name} 失败（Win32Error={Error}），退回 Process.Start", this.processName, Marshal.GetLastWin32Error());
                    return null;
                }

                created = true;
                return Process.GetProcessById(processInformation.dwProcessId);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning("CreateProcessW 启动 {Name} 异常，退回 Process.Start：{Message}", this.processName, ex.Message);
                // 已创建但包装失败（如进程秒退致 GetProcessById 抛异常）：必须清掉已建进程，
                // 否则调用方退回 Process.Start 会双开。仅在「创建失败」时才允许退回。
                if (created && processInformation.dwProcessId != 0)
                {
                    try
                    {
                        using var stray = Process.GetProcessById(processInformation.dwProcessId);
                        stray.Kill();
                    }
                    catch
                    {
                        // 进程已自行退出，无需清理
                    }
                    throw;
                }
                return null;
            }
            finally
            {
                if (processInformation.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hThread);
                }
                if (processInformation.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hProcess);
                }
            }
        }

        /// <summary>
        /// [PATCH] 把目标进程绑入一个 KILL_ON_JOB_CLOSE 的 Job Object。
        /// 任何一步失败都只记 Warning、不抛出：这只是兜底手段，不应影响 dnscrypt 启动。
        /// </summary>
        private void TryBindJobObject(Process target)
        {
            // Job Object 是 Windows 内核对象；非 Windows 平台直接跳过，避免多余告警
            if (OperatingSystem.IsWindows() == false)
            {
                return;
            }

            try
            {
                // 先释放可能存在的旧 Job（看护重启会二次进入这里）
                this.ReleaseJob();

                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    this.logger.LogWarning("创建 Job Object 失败，无法保证 dnscrypt-proxy 随引擎退出（将成为孤儿进程）。");
                    return;
                }

                if (SetKillOnJobClose(job) == false)
                {
                    CloseHandle(job);
                    this.logger.LogWarning("设置 Job Object 的 KILL_ON_JOB_CLOSE 失败，无法保证 dnscrypt-proxy 随引擎退出。");
                    return;
                }

                if (AssignProcessToJobObject(job, target.Handle) == false)
                {
                    CloseHandle(job);
                    this.logger.LogWarning("将 dnscrypt-proxy 加入 Job Object 失败，无法保证其随引擎退出。");
                    return;
                }

                this.jobHandle = job;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"绑定 dnscrypt-proxy 到 Job Object 失败：{ex.Message}");
            }
        }

        /// <summary>
        /// [PATCH] 释放 Job Object 句柄。
        /// 因为带 KILL_ON_JOB_CLOSE，关闭最后一个句柄即由内核终止 Job 内残余进程，
        /// 所以这一步同时承担「停机最后兜底」的职责；原实现从不调用 CloseHandle，
        /// 且重复启动会覆盖句柄造成泄漏，这里一并修掉。
        /// </summary>
        private void ReleaseJob()
        {
            var job = Interlocked.Exchange(ref this.jobHandle, IntPtr.Zero);
            if (job == IntPtr.Zero)
            {
                return;
            }

            try
            {
                CloseHandle(job);
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "释放 Job Object 句柄异常");
            }
        }

        /// <summary>
        /// [PATCH] 释放 Process 组件（内部持有进程/线程句柄）。
        /// 看护每轮断网恢复都会新建一个 Process 实例，不释放会稳定泄漏。
        /// </summary>
        private void DisposeProcess()
        {
            var target = this.process;
            this.process = null;
            if (target == null)
            {
                return;
            }

            try
            {
                target.EnableRaisingEvents = false;
                target.Dispose();
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "释放 {Name} 进程句柄异常", this.processName);
            }
        }

        /// <summary>
        /// [PATCH] 为 Job Object 设置 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        /// </summary>
        private static bool SetKillOnJobClose(IntPtr job)
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                return SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// [PATCH] 宿主释放单例时兜底
        /// </summary>
        public void Dispose()
        {
            try
            {
                this.ReleaseJob();
                this.DisposeProcess();
                this.gate.Dispose();
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "释放 {Name} 资源异常", this.processName);
            }
        }

        /// <summary>
        /// 转换为字符串
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return this.processName;
        }

        #region [PATCH] Job Object 的 kernel32 声明（仓库仅引用 PInvoke.AdvApi32，无 PInvoke.Kernel32）

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        // [PATCH] CreateProcessW 启动标志：进程组（软信号定向投递的前提）+ 隐藏控制台
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        private const uint CREATE_NO_WINDOW = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        #endregion
    }
}
