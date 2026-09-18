using System;
using System.IO;
using System.Runtime.Versioning;
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

            var status = new SERVICE_STATUS();
            if (QueryServiceStatus(hService, ref status) == true)
            {
                if (status.dwCurrentState != ServiceState.SERVICE_STOP_PENDING &&
                    status.dwCurrentState != ServiceState.SERVICE_STOPPED)
                {
                    ControlService(hService, ServiceControl.SERVICE_CONTROL_STOP, ref status);
                }
            }

            return DeleteService(hService);
        }
    }
}
