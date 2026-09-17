using FastGithub.Configuration;
using FastGithub.FlowAnalyze;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Sinks.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Text.Json;

namespace FastGithub
{
    /// <summary>
    /// 启动项
    /// </summary>
    static class Startup
    {
        /// <summary>
        /// 配置通用主机
        /// </summary>
        /// <param name="builder"></param>
        public static void ConfigureHost(this WebApplicationBuilder builder)
        {
            builder.Host.UseSystemd().UseWindowsService();
            builder.Host.UseSerilog((hosting, logger) =>
            {
                var template = "{Timestamp:O} [{Level:u3}]{NewLine}{SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}";
                logger
                    .ReadFrom.Configuration(hosting.Configuration)
                    .Enrich.FromLogContext()
                    .WriteTo.Console(outputTemplate: template)
                    // 日志容量上限：单文件20MB后滚动，最多保留7个文件。
                    // 上游只配了 rollingInterval: Day，没有大小与数量限制，
                    // 不可达域名被高频重试时日志会无界增长（实测5分钟 0.7KB -> 45KB）。
                    .WriteTo.File(
                        Path.Combine("logs", @"log.txt"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: template,
                        fileSizeLimitBytes: 20 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 7);

                var udpLoggerPort = hosting.Configuration.GetValue(nameof(AppOptions.UdpLoggerPort), 38457);
                logger.WriteTo.UDPSink(IPAddress.Loopback, udpLoggerPort);
            });
        }

        /// <summary>
        /// 配置web主机
        /// </summary>
        /// <param name="builder"></param>
        public static void ConfigureWebHost(this WebApplicationBuilder builder)
        {
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(1d));
            builder.WebHost.UseKestrel(kestrel =>
            {
                kestrel.NoLimit();
                if (OperatingSystem.IsWindows())
                {
                    kestrel.ListenHttpsReverseProxy();
                    kestrel.ListenHttpReverseProxy();
                    kestrel.ListenSshReverseProxy();
                    kestrel.ListenGitReverseProxy();
                }
                else
                {
                    kestrel.ListenHttpProxy();
                }
            });
        }


        /// <summary>
        /// 配置配置
        /// </summary>
        /// <param name="builder"></param>
        public static void ConfigureConfiguration(this WebApplicationBuilder builder)
        {
            const string APPSETTINGS = "appsettings";
            if (Directory.Exists(APPSETTINGS) == true)
            {
                foreach (var file in Directory.GetFiles(APPSETTINGS, "appsettings.*.json"))
                {
                    var jsonFile = Path.Combine(APPSETTINGS, Path.GetFileName(file));
                    // 关闭热重载：程序可能被解压在用户可写目录并以管理员运行，热重载等价于
                    // 把「域名白名单」的控制权交给任何能写该目录的主体——丢一个
                    // appsettings.*.json 进去就能让任意域名进入 DNS 投毒 + TLS 解密，无需重启、
                    // 也无需经过 UI 勾选。改配置后请重启程序生效。
                    builder.Configuration.AddJsonFile(jsonFile, true, false);
                }
            }
        }


        /// <summary>
        /// 配置服务
        /// </summary>
        /// <param name="builder"></param>
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Dictionary<string, DomainConfig>))]
        public static void ConfigureServices(this WebApplicationBuilder builder)
        {
            var services = builder.Services;
            var configuration = builder.Configuration;

            services.Configure<AppOptions>(configuration);
            services.Configure<FastGithubOptions>(configuration.GetSection(nameof(FastGithub)));

            services.AddConfiguration();
            services.AddDomainResolve();
            services.AddHttpClient();
            services.AddReverseProxy();
            services.AddFlowAnalyze();
            services.AddHostedService<AppHostedService>();

            if (OperatingSystem.IsWindows())
            {
                services.AddPacketIntercept();
            }
        }

        /// <summary>
        /// 配置应用
        /// </summary>
        /// <param name="app"></param>
        public static void ConfigureApp(this WebApplication app)
        {
            app.UseHttpProxyPac();
            app.UseRequestLogging();
            app.UseHttpReverseProxy();

            app.UseRouting();
            app.DisableRequestLogging();

            app.MapGet("/flowStatistics", context =>
            {
                var flowStatistics = context.RequestServices.GetRequiredService<IFlowAnalyzer>().GetFlowStatistics();
                var json = JsonSerializer.Serialize(flowStatistics, FlowStatisticsContext.Default.FlowStatistics);
                return context.Response.WriteAsync(json);
            });
        }
    }
}
