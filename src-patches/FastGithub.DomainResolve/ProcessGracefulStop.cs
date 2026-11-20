using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// [PATCH] 进程模式下向 dnscrypt-proxy 投递软停机信号（CTRL_BREAK_EVENT）。
    ///
    /// 为什么要走这条路：dnscrypt-proxy 是 Go 程序，Windows 下它只认两种优雅退出通道 ——
    /// ① 服务控制管理器的 SERVICE_CONTROL_STOP（服务模式，见 ServiceInstallUtil）；
    /// ② 控制台信号（os.Interrupt，由 CTRL_C_EVENT / CTRL_BREAK_EVENT 投递）。
    /// 引擎用 Process.Start 拉起的裸进程只能走 ②。
    ///
    /// 为什么用 CTRL_BREAK 而不是 CTRL_C：
    /// GenerateConsoleCtrlEvent 对 CTRL_C_EVENT 要求 dwProcessId 为 0，即广播给「附加到该控制台的
    /// 全部进程」—— 而投递方自己此刻也必须附加在目标控制台上，等于给自己来一发；
    /// CTRL_BREAK_EVENT 可以按进程号定向投递，只命中目标进程组，不会波及引擎。
    ///
    /// 代价（必须如实说明）：控制台归属是「进程级」而非线程级 ——
    /// AttachConsole(目标) 之前必须先 FreeConsole，而控制台在最后一个附加进程离开时即被销毁。
    /// 因此本类只在「引擎的控制台没人用」时才启用软信号：
    ///   · 引擎没有控制台（被 GUI 以 CREATE_NO_WINDOW 拉起，dnscrypt 自带隐藏控制台）→ 可直接附加；
    ///   · 引擎有控制台且全系统只有引擎自己附加（GetConsoleProcessList == 1）→ 可以让出，
    ///     让出前把 Console.Out/Error 重定向到 Null，避免日志的控制台 sink 往已销毁的控制台写；
    ///   · 引擎有控制台且还有别人（用户在 cmd / PowerShell 里手动跑 fastgithub.exe）→ 放弃软信号，
    ///     直接走 Kill 兜底。不去动用户正在看的终端，这是产品判断而非技术限制。
    /// 任一环节失败都只记日志并返回 false，由调用方走强杀兜底。
    /// </summary>
    static class ProcessGracefulStop
    {
        private const uint CTRL_BREAK_EVENT = 0x00000001;
        private const uint GENERIC_READ_WRITE = 0xC0000000;
        private const uint FILE_SHARE_READ_WRITE = 0x00000003;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        private static readonly object syncRoot = new();
        private static bool consoleSurrendered;

        /// <summary>
        /// 引擎是否可以让出自己的控制台（没有控制台，或控制台只有自己在使用）
        /// </summary>
        /// <returns>可以返回 true</returns>
        public static bool IsConsoleExclusive()
        {
            if (OperatingSystem.IsWindows() == false)
            {
                return true;
            }

            try
            {
                if (HasConsole() == false)
                {
                    return true;
                }
                return GetConsoleProcessListCount() <= 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 向目标进程投递 CTRL_BREAK，请求其优雅退出。
        /// </summary>
        /// <param name="targetPid">目标进程号</param>
        /// <param name="logger">日志</param>
        /// <returns>信号是否投递成功（不代表目标已退出，退出与否由调用方等待判定）</returns>
        public static bool TrySignal(int targetPid, ILogger logger)
        {
            if (targetPid <= 0)
            {
                return false;
            }

            if (OperatingSystem.IsWindows() == false)
            {
                return TrySignalUnix(targetPid, logger);
            }

            lock (syncRoot)
            {
                try
                {
                    // 让出引擎自身控制台：控制台不能被两个进程同时「拥有」，
                    // 且附加到目标控制台后收到的广播也会落到自己头上。
                    if (HasConsole())
                    {
                        if (consoleSurrendered == false)
                        {
                            consoleSurrendered = true;
                            // 引擎自带的隐藏控制台在此让出后即被销毁；先把标准输出/错误重定向到 Null，
                            // 使 Serilog 的控制台 sink 继续可写（文件与 UDP sink 不受影响）。
                            try
                            {
                                Console.SetOut(TextWriter.Null);
                                Console.SetError(TextWriter.Null);
                            }
                            catch
                            {
                                // 重定向失败也不致命，最坏是控制台 sink 的写入被吞
                            }
                            logger.LogDebug("为投递 dnscrypt-proxy 软信号，引擎将让出自身控制台。");
                        }

                        if (FreeConsole() == false)
                        {
                            logger.LogDebug("FreeConsole 失败，放弃 dnscrypt-proxy 软信号（将由强杀兜底）。Win32Error={Error}", Marshal.GetLastWin32Error());
                            return false;
                        }
                    }

                    try
                    {
                        if (AttachConsole((uint)targetPid) == false)
                        {
                            // 常见原因：目标没有控制台（非 CREATE_NO_WINDOW/NEW_CONSOLE 启动）、
                            // 目标已退出、目标是另一会话的服务进程
                            logger.LogDebug("AttachConsole({Pid}) 失败，放弃 dnscrypt-proxy 软信号。Win32Error={Error}", targetPid, Marshal.GetLastWin32Error());
                            return false;
                        }

                        if (GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)targetPid) == false)
                        {
                            logger.LogDebug("向 {Pid} 投递 CTRL_BREAK 失败。Win32Error={Error}", targetPid, Marshal.GetLastWin32Error());
                            return false;
                        }

                        logger.LogDebug("已向 dnscrypt-proxy（PID={Pid}）投递 CTRL_BREAK 软信号。", targetPid);
                        return true;
                    }
                    finally
                    {
                        // 立刻脱离目标控制台，别把自己留在别人的控制台里
                        FreeConsole();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "投递 dnscrypt-proxy 软信号异常，将由强杀兜底。");
                    return false;
                }
            }
        }

        /// <summary>
        /// 当前进程是否附加了控制台。
        /// 不能用 GetConsoleWindow 判定：CREATE_NO_WINDOW 创建的控制台没有窗口，
        /// 该 API 会返回 NULL，但控制台本身是存在的。
        /// </summary>
        private static bool HasConsole()
        {
            var handle = CreateFileW("CONOUT$", GENERIC_READ_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == INVALID_HANDLE_VALUE)
            {
                return false;
            }
            CloseHandle(handle);
            return true;
        }

        /// <summary>
        /// 取附加到当前控制台的进程数（传空缓冲区即返回所需数量）
        /// </summary>
        private static int GetConsoleProcessListCount()
        {
            var count = GetConsoleProcessList(null, 0);
            if (count > 0)
            {
                return (int)count;
            }

            // 少数实现下空缓冲区返回 0，这里再按 16 个槽位实测一次
            var buffer = new uint[16];
            count = GetConsoleProcessList(buffer, (uint)buffer.Length);
            return (int)count;
        }

        /// <summary>
        /// [PATCH] 非 Windows：直接 kill(pid, SIGTERM)。dnscrypt-proxy 的 Go 主循环监听 SIGTERM，
        /// 这是它的官方优雅退出通道。原实现一律 Process.Kill()（等价 SIGKILL）。
        /// </summary>
        private static bool TrySignalUnix(int targetPid, ILogger logger)
        {
            try
            {
                if (kill(targetPid, SIGTERM) != 0)
                {
                    logger.LogDebug("向 {Pid} 发送 SIGTERM 失败，将由强杀兜底（GetLastPInvokeError={Error}，即 libc errno）", targetPid, Marshal.GetLastPInvokeError());
                    return false;
                }
                logger.LogDebug("已向 dnscrypt-proxy（PID={Pid}）投递 SIGTERM 软信号。", targetPid);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "投递 SIGTERM 异常，将由强杀兜底");
                return false;
            }
        }

        #region [PATCH] kernel32 声明（仓库只引用 PInvoke.AdvApi32，无 PInvoke.Kernel32）

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList([Out] uint[]? lpdwProcessList, uint dwProcessListSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int SIGTERM = 15;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        #endregion
    }
}
