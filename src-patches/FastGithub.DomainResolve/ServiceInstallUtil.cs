using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using static PInvoke.AdvApi32;

namespace FastGithub.DomainResolve
{
    public static class ServiceInstallUtil
    {
        /// <summary>
        /// [PATCH] 判断服务镜像路径是否位于允许安装系统服务的目录（白名单）。
        /// 上游用黑名单，实测漏掉了 C:\temp、C:\ProgramData、C:\Users\Public 等标准用户同样可写的目录，
        /// 解压到这些目录再执行 start 就能装出 LocalSystem 服务，普通用户替换 exe 即可本地提权。
        /// 这里改成白名单：只放行 Program Files 与 Program Files (x86)。
        /// 判定只有这一处，ServiceExtensions 的调用侧校验与 InstallAndStartService 的下沉校验共用它，
        /// 避免出现两套不一致的判定。
        /// </summary>
        /// <param name="binaryPath">服务镜像路径</param>
        /// <returns>允许安装时返回 true</returns>
        public static bool IsAllowedServiceBinaryPath(string binaryPath)
        {
            // [PATCH] 判定前必须规范化：GetDirectoryName 不做规范化，
            // C:\Program Files\..\Users\Public\evil\x.exe 的字面目录以 C:\Program Files\ 开头会通过白名单，
            // 而 InstallAndStartService 随后用 Path.GetFullPath(binaryPath) 建服务，实际镜像落在用户可写目录，
            // 白名单失效（本地提权语义重现）。这里先规范化再取目录。
            var directory = Path.GetDirectoryName(Path.GetFullPath(binaryPath));
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            var separator = Path.DirectorySeparatorChar;
            var directoryWithSeparator = directory.EndsWith(separator) ? directory : $"{directory}{separator}";

            // [PATCH] 只用 SpecialFolder 枚举，不用 %ProgramFiles% / %ProgramW6432% 环境变量字符串：
            // 前者在 WOW64 的 32 位进程里会被重定向，后者在 32 位进程下根本不存在，都会被误导。
            // 注意：不要在这里加 SystemRoot（C:\Windows\Temp 等子目录对普通用户可写）。
            var allowedRoots = new string?[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (var root in allowedRoots)
            {
                // ⚠️ 空串陷阱：白名单语义是「任一命中即放行」，若候选取到空串，
                // StartsWith("") 恒为 true —— 会对任意目录全部放行，比黑名单还糟。
                // 因此每个候选必须防空并跳过。
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                // 前缀必须以目录分隔符结尾再比较，避免 C:\Program FilesEvil 被误判为 C:\Program Files
                var prefix = root.EndsWith(separator) ? root : $"{root}{separator}";
                if (directoryWithSeparator.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // [PATCH] 白名单为空（两个 SpecialFolder 都取不到）时失败关闭；绝不失败开放
            return false;
        }

        /// <summary>
        /// 安装并启动服务
        /// </summary>
        /// <param name="serviceName"></param>
        /// <param name="binaryPath"></param>
        /// <param name="startType"></param>
        /// <returns></returns>
        [SupportedOSPlatform("windows")]
        public static bool InstallAndStartService(string serviceName, string binaryPath, ServiceStartType startType = ServiceStartType.SERVICE_AUTO_START)
        {
            // [PATCH] 下沉的安装目录白名单校验。dnscrypt-proxy 也会经此安装 LocalSystem 服务
            // （DnscryptProxy 直接调用本方法），上游此处无任何校验，等于绕过了 ServiceExtensions 的调用侧校验。
            // 校验不通过即拒绝安装（失败关闭）。
            if (IsAllowedServiceBinaryPath(binaryPath) == false)
            {
                return false;
            }

            using var hSCManager = OpenSCManager(null, null, ServiceManagerAccess.SC_MANAGER_ALL_ACCESS);
            if (hSCManager.IsInvalid == true)
            {
                return false;
            }

            var hService = OpenService(hSCManager, serviceName, ServiceAccess.SERVICE_ALL_ACCESS);
            if (hService.IsInvalid == true)
            {
                hService = CreateService(
                    hSCManager,
                    serviceName,
                    serviceName,
                    ServiceAccess.SERVICE_ALL_ACCESS,
                    ServiceType.SERVICE_WIN32_OWN_PROCESS,
                    startType,
                    ServiceErrorControl.SERVICE_ERROR_NORMAL,
                    Path.GetFullPath(binaryPath),
                    lpLoadOrderGroup: null,
                    lpdwTagId: 0,
                    lpDependencies: null,
                    lpServiceStartName: null,
                    lpPassword: null);
            }

            if (hService.IsInvalid == true)
            {
                return false;
            }

            using (hService)
            {
                return StartService(hService, 0, null);
            }
        }

        /// <summary>
        /// 停止并删除服务
        /// </summary>
        /// <param name="serviceName"></param>
        /// <returns></returns>
        [SupportedOSPlatform("windows")]
        public static bool StopAndDeleteService(string serviceName)
        {
            // [PATCH] 拆为「先优雅停、再删除」：原实现 ControlService(STOP) 后立刻 DeleteService，
            // 不等 SERVICE_STOPPED —— 删除与停止竞态，服务记录先没了，进程归宿交给运气。
            StopService(serviceName, DEFAULT_STOP_TIMEOUT_MS);
            return DeleteService(serviceName);
        }

        /// <summary>
        /// 默认停机预算（毫秒）
        /// </summary>
        private const int DEFAULT_STOP_TIMEOUT_MS = 3000;

        /// <summary>
        /// [PATCH] 发送 SERVICE_CONTROL_STOP 并等待服务真正进入 SERVICE_STOPPED。
        /// 这是 dnscrypt-proxy 唯一真正的优雅停机通道（它内部把 SCM 的 STOP 映射到自己的
        /// 退出流程，会先把监听 socket 收尾干净）。
        /// </summary>
        /// <param name="serviceName">服务名</param>
        /// <param name="timeoutMs">等待预算，超时即返回 false 交由调用方兜底</param>
        /// <returns>已停止（或本就不存在）返回 true</returns>
        [SupportedOSPlatform("windows")]
        public static bool StopService(string serviceName, int timeoutMs = DEFAULT_STOP_TIMEOUT_MS)
        {
            using var hSCManager = OpenSCManager(null, null, ServiceManagerAccess.SC_MANAGER_ALL_ACCESS);
            if (hSCManager.IsInvalid == true)
            {
                return false;
            }

            using var hService = OpenService(hSCManager, serviceName, ServiceAccess.SERVICE_ALL_ACCESS);
            if (hService.IsInvalid == true)
            {
                // 服务不存在，无需停止
                return true;
            }

            var status = new SERVICE_STATUS();
            if (QueryServiceStatus(hService, ref status) == false)
            {
                return false;
            }
            if (status.dwCurrentState == ServiceState.SERVICE_STOPPED)
            {
                return true;
            }

            var stopPending = status.dwCurrentState == ServiceState.SERVICE_STOP_PENDING;
            if (stopPending == false &&
                ControlService(hService, ServiceControl.SERVICE_CONTROL_STOP, ref status) == false)
            {
                return false;
            }

            // 尊重服务自己声明的 dwWaitHint：在它声称需要的时间里先不打扰，避免无谓轮询
            var waitHintMs = status.dwWaitHint > 0
                ? (int)Math.Min(status.dwWaitHint, (uint)Math.Max(200, timeoutMs))
                : 150;
            Thread.Sleep(Math.Max(50, waitHintMs));

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (QueryServiceStatus(hService, ref status) == false)
                {
                    return false;
                }
                if (status.dwCurrentState == ServiceState.SERVICE_STOPPED)
                {
                    return true;
                }
                Thread.Sleep(100);
            }

            // 预算内仍未 STOPPED：最后读一次状态，给刚好卡在边界的情况一个机会
            return QueryServiceStatus(hService, ref status) && status.dwCurrentState == ServiceState.SERVICE_STOPPED;
        }

        /// <summary>
        /// [PATCH] 删除服务定义（不先停止，调用方负责停机）
        /// </summary>
        /// <param name="serviceName"></param>
        /// <returns></returns>
        [SupportedOSPlatform("windows")]
        public static bool DeleteService(string serviceName)
        {
            using var hSCManager = OpenSCManager(null, null, ServiceManagerAccess.SC_MANAGER_ALL_ACCESS);
            if (hSCManager.IsInvalid == true)
            {
                return false;
            }

            using var hService = OpenService(hSCManager, serviceName, ServiceAccess.SERVICE_ALL_ACCESS);
            if (hService.IsInvalid == true)
            {
                return true;
            }

            return PInvoke.AdvApi32.DeleteService(hService);
        }

        /// <summary>
        /// [PATCH] 启动已存在的服务（断网恢复时避开「删服务 + 重建」的 SCM 抖动）。
        /// </summary>
        /// <param name="serviceName"></param>
        /// <returns>服务存在且已启动 / 本就在跑返回 true；不存在或启动失败返回 false</returns>
        [SupportedOSPlatform("windows")]
        public static bool TryStartExistingService(string serviceName)
        {
            using var hSCManager = OpenSCManager(null, null, ServiceManagerAccess.SC_MANAGER_ALL_ACCESS);
            if (hSCManager.IsInvalid == true)
            {
                return false;
            }

            using var hService = OpenService(hSCManager, serviceName, ServiceAccess.SERVICE_ALL_ACCESS);
            if (hService.IsInvalid == true)
            {
                return false;
            }

            var status = new SERVICE_STATUS();
            if (QueryServiceStatus(hService, ref status) &&
                (status.dwCurrentState == ServiceState.SERVICE_RUNNING || status.dwCurrentState == ServiceState.SERVICE_START_PENDING))
            {
                return true;
            }

            return StartService(hService, 0, null);
        }
    }
}
