using Microsoft.AspNetCore.Builder;
using System;
using System.IO;
using System.Security.Cryptography;

namespace FastGithub
{

    class Program
    {
        /// <summary>
        /// WinDivert.dll 的 SHA256 基线。
        /// [PATCH] 采集日期 2026-09-17，取自本机实际运行后 WindivertDotnet 释放的文件。
        /// </summary>
        private const string WINDIVERT_DLL_SHA256 = "C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2";

        /// <summary>
        /// WinDivert64.sys 的 SHA256 基线。
        /// [PATCH] 采集日期 2026-09-17，取自本机实际运行后 WindivertDotnet 释放的文件。
        /// </summary>
        private const string WINDIVERT64_SYS_SHA256 = "8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2";

        /// <summary>
        /// 程序入口
        /// </summary>
        /// <param name="args"></param>
        public static void Main(string[] args)
        {
            ConsoleUtil.DisableQuickEdit();
            VerifyWinDivertFiles();
            var contentRoot = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(contentRoot) == false)
            {
                Environment.CurrentDirectory = contentRoot;
            }
            var options = new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot
            };
            CreateWebApplication(options).Run(singleton: true);
        }

        /// <summary>
        /// 创建host
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static WebApplication CreateWebApplication(WebApplicationOptions options)
        {
            var builder = WebApplication.CreateBuilder(options);
            builder.ConfigureHost();
            builder.ConfigureWebHost();
            builder.ConfigureConfiguration();
            builder.ConfigureServices();

            var app = builder.Build();
            app.ConfigureApp();
            return app;
        }

        /// <summary>
        /// [PATCH] 校验 WindivertDotnet 释放到用户可写目录的驱动文件。
        /// WinDivert.dll 完全没有数字签名，且位于 %APPDATA%\WindivertDotnet\...（当前用户可写），
        /// 而它会被 requireAdministrator 的进程 LoadLibrary —— 替换该文件即可获得管理员代码执行，
        /// 不需要绕过任何签名机制。
        /// 上游释放逻辑是「目标文件已存在即跳过」，从不复查内容，因此文件被替换或损坏后永久失效、
        /// 也不会自愈。这里按哈希基线校验：不一致就删除该文件（下次运行会从内嵌资源重新释放），
        /// 顺便修好「文件损坏后永久失效」的问题。
        /// 任何异常都不阻断启动。
        /// </summary>
        private static void VerifyWinDivertFiles()
        {
            if (OperatingSystem.IsWindows() == false)
            {
                return;
            }

            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "WindivertDotnet");

                if (Directory.Exists(directory) == false)
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(directory, "WinDivert.dll", SearchOption.AllDirectories))
                {
                    VerifyFileHash(file, WINDIVERT_DLL_SHA256);
                }

                foreach (var file in Directory.GetFiles(directory, "WinDivert64.sys", SearchOption.AllDirectories))
                {
                    VerifyFileHash(file, WINDIVERT64_SYS_SHA256);
                }
            }
            catch (Exception)
            {
                // 目录不存在、无权限等情况下静默忽略，不影响启动
            }
        }

        /// <summary>
        /// [PATCH] 校验单个文件的哈希，与基线不符则删除该文件
        /// </summary>
        /// <param name="file">文件路径</param>
        /// <param name="expectedHash">期望的 SHA256（十六进制大写）</param>
        private static void VerifyFileHash(string file, string expectedHash)
        {
            string actualHash;
            try
            {
                using var stream = File.OpenRead(file);
                actualHash = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception)
            {
                // 文件被占用或不可读时静默忽略
                return;
            }

            if (actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.Delete(file);
                Console.Error.WriteLine($"[警告] WinDivert 驱动文件哈希与基线不符，已删除：{file}（实际 SHA256={actualHash}）。可能是文件损坏或被替换；下次运行将从内嵌资源重新释放。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[警告] WinDivert 驱动文件哈希与基线不符且删除失败：{file}（{ex.Message}）。实际 SHA256={actualHash}，请手动删除该文件。");
            }
        }
    }
}
