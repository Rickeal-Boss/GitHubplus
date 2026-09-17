using FastGithub.DomainResolve;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PInvoke;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace FastGithub
{
    /// <summary>
    /// IHostBuilder扩展
    /// </summary>
    static class ServiceExtensions
    {
        /// <summary>
        /// 控制命令
        /// </summary>
        private enum Command
        {
            Start,
            Stop,
        }

        [SupportedOSPlatform("linux")]
        [DllImport("libc", SetLastError = true)]
        private static extern uint geteuid();

        /// <summary>
        /// 使用windows服务
        /// </summary>
        /// <param name="hostBuilder"></param> 
        /// <returns></returns>
        public static IHostBuilder UseWindowsService(this IHostBuilder hostBuilder)
        {
            return WindowsServiceLifetimeHostBuilderExtensions.UseWindowsService(hostBuilder);
        }

        /// <summary>
        /// 运行主机
        /// </summary>
        /// <param name="app"></param>
        /// <param name="singleton"></param>
        public static void Run(this WebApplication app, bool singleton)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(FastGithub));
            if (UseCommand(logger) == false)
            {
                using var mutex = new Mutex(true, "Global\\FastGithub", out var firstInstance);
                if (singleton == false || firstInstance)
                {
                    app.Run();
                }
                else
                {
                    logger.LogWarning($"程序将自动关闭：系统已运行其它实例");
                }
            }
        }

        /// <summary>
        /// 使用命令
        /// </summary>
        /// <param name="logger"></param>
        /// <returns></returns>
        private static bool UseCommand(ILogger logger)
        {
            var args = Environment.GetCommandLineArgs();
            if (Enum.TryParse<Command>(args.Skip(1).FirstOrDefault(), true, out var cmd) == false)
            {
                return false;
            }

            var action = cmd == Command.Start ? "启动" : "停止";
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    UseCommandAtWindows(cmd);
                }
                else if (OperatingSystem.IsLinux())
                {
                    UseCommandAtLinux(cmd);
                }
                else
                {
                    return false;
                }
                logger.LogInformation($"服务{action}成功");
            }
            catch (Exception ex)
            {
                logger.LogError(ex.Message, $"服务{action}异常");
            }
            return true;
        }

        /// <summary>
        /// 应用控制指令
        /// </summary> 
        /// <param name="cmd"></param>
        [SupportedOSPlatform("windows")]
        private static void UseCommandAtWindows(Command cmd)
        {
            var binaryPath = Path.GetFullPath(Environment.GetCommandLineArgs().First());
            var serviceName = Path.GetFileNameWithoutExtension(binaryPath);
            var state = true;
            if (cmd == Command.Start)
            {
                // 服务镜像路径指向的 exe 若位于普通用户可写目录，用户可自行替换该 exe，
                // 下次开机即以 SYSTEM 权限加载被替换的二进制，属于本地提权。
                EnsureNotInUserWritableDirectory(binaryPath);
                state = ServiceInstallUtil.InstallAndStartService(serviceName, binaryPath);
            }
            else if (cmd == Command.Stop)
            {
                state = ServiceInstallUtil.StopAndDeleteService(serviceName);
            }

            if (state == false)
            {
                throw new Win32Exception();
            }
        }

        /// <summary>
        /// 确保服务的镜像路径位于允许安装系统服务的目录（白名单）
        /// </summary>
        /// <param name="binaryPath">服务镜像路径</param>
        /// <exception cref="InvalidOperationException"></exception>
        [SupportedOSPlatform("windows")]
        private static void EnsureNotInUserWritableDirectory(string binaryPath)
        {
            // [PATCH] 判定逻辑单一来源：与 InstallAndStartService 内部的下沉校验共用
            // ServiceInstallUtil.IsAllowedServiceBinaryPath，这里只做调用侧校验，负责给出可见的错误信息。
            if (ServiceInstallUtil.IsAllowedServiceBinaryPath(binaryPath) == false)
            {
                var directory = Path.GetDirectoryName(binaryPath);
                throw new InvalidOperationException($"拒绝安装Windows服务：程序目录「{directory}」不在允许的安装位置（仅允许 Program Files 与 Program Files (x86)）。该目录可能被普通用户写入，服务会以SYSTEM权限加载其中的 {Path.GetFileName(binaryPath)}，普通用户替换该文件即可实现本地提权。请先将整个程序目录移动到 C:\\Program Files\\ 下，再执行 start 命令。");
            }
        }

        /// <summary>
        /// 应用控制指令
        /// </summary> 
        /// <param name="cmd"></param>
        [SupportedOSPlatform("linux")]
        private static void UseCommandAtLinux(Command cmd)
        {
            if (geteuid() != 0)
            {
                throw new UnauthorizedAccessException("无法操作服务：没有root权限");
            }

            var binaryPath = Path.GetFullPath(Environment.GetCommandLineArgs().First());
            var serviceName = Path.GetFileNameWithoutExtension(binaryPath);
            var serviceFilePath = $"/etc/systemd/system/{serviceName}.service";

            if (cmd == Command.Start)
            {
                var serviceBuilder = new StringBuilder()
                    .AppendLine("[Unit]")
                    .AppendLine($"Description={serviceName}")
                    .AppendLine()
                    .AppendLine("[Service]")
                    .AppendLine("Type=notify")
                    .AppendLine($"User={Environment.UserName}")
                    .AppendLine($"ExecStart={binaryPath}")
                    .AppendLine($"WorkingDirectory={Path.GetDirectoryName(binaryPath)}")
                    .AppendLine()
                    .AppendLine("[Install]")
                    .AppendLine("WantedBy=multi-user.target");
                File.WriteAllText(serviceFilePath, serviceBuilder.ToString());

                Process.Start("chcon", $"--type=bin_t {binaryPath}").WaitForExit(); // SELinux
                Process.Start("systemctl", "daemon-reload").WaitForExit();
                Process.Start("systemctl", $"start {serviceName}.service").WaitForExit();
                Process.Start("systemctl", $"enable {serviceName}.service").WaitForExit();
            }
            else if (cmd == Command.Stop)
            {
                Process.Start("systemctl", $"stop {serviceName}.service").WaitForExit();
                Process.Start("systemctl", $"disable {serviceName}.service").WaitForExit();

                if (File.Exists(serviceFilePath))
                {
                    File.Delete(serviceFilePath);
                }
                Process.Start("systemctl", "daemon-reload").WaitForExit();
            }
        }
    }
}
