using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static PInvoke.AdvApi32;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// DnscryptProxy服务
    /// </summary>
    sealed class DnscryptProxy
    {
        private readonly ILogger<DnscryptProxy> logger;
        private readonly string processName;
        private readonly string serviceName;
        private readonly string exeFilePath;
        private readonly string tomlFilePath;

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
        /// DnscryptProxy服务
        /// </summary>
        /// <param name="logger"></param>
        public DnscryptProxy(ILogger<DnscryptProxy> logger)
        {
            const string PATH = "dnscrypt-proxy";
            const string NAME = "dnscrypt-proxy";

            this.logger = logger;
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
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await this.StartCoreAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{this.processName}启动失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 启动dnscrypt-proxy
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            var port = GlobalListener.GetAvailablePort(5533);
            var localEndPoint = new IPEndPoint(IPAddress.Loopback, port);

            await TomlUtil.SetListensAsync(this.tomlFilePath, localEndPoint, cancellationToken);
            await TomlUtil.SetLogLevelAsync(this.tomlFilePath, 6, cancellationToken);
            await TomlUtil.SetLBStrategyAsync(this.tomlFilePath, "ph", cancellationToken);
            await TomlUtil.SetMinMaxTTLAsync(this.tomlFilePath, TimeSpan.FromMinutes(1d), TimeSpan.FromMinutes(2d), cancellationToken);

            if (OperatingSystem.IsWindows() && Environment.UserInteractive == false)
            {
                ServiceInstallUtil.StopAndDeleteService(this.serviceName);
                ServiceInstallUtil.InstallAndStartService(this.serviceName, this.exeFilePath, ServiceStartType.SERVICE_DEMAND_START);
                this.process = Process.GetProcessesByName(this.processName).FirstOrDefault(item => item.SessionId == 0);
            }
            else
            {
                this.process = StartDnscryptProxy();
            }

            if (this.process != null)
            {
                this.LocalEndPoint = localEndPoint;
                this.process.EnableRaisingEvents = true;
                this.process.Exited += (s, e) => this.LocalEndPoint = null;

                // [PATCH] 把 dnscrypt-proxy 纳入 Job Object 兜底（失败只告警，不影响启动）
                this.TryBindJobObject(this.process);
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        public void Stop()
        {
            try
            {
                if (OperatingSystem.IsWindows() && Environment.UserInteractive == false)
                {
                    ServiceInstallUtil.StopAndDeleteService(this.serviceName);
                }

                if (this.process != null && this.process.HasExited == false)
                {
                    this.process.Kill();
                    // [PATCH] 等待句柄真正释放，避免下次启动时 5533 端口仍被占用而静默改绑其它端口
                    this.process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{this.processName}停止失败：{ex.Message }");
            }
            finally
            {
                this.LocalEndPoint = null;
            }
        }

        /// <summary>
        /// 启动DnscryptProxy进程
        /// </summary> 
        /// <returns></returns>
        private Process? StartDnscryptProxy()
        {
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
