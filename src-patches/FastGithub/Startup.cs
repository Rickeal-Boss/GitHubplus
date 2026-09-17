using FastGithub.Configuration;
using FastGithub.FlowAnalyze;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        // [PATCH] 防 trimming 静默丢值：PublishTrimmed（build-portable.cmd）会裁掉「仅被反射访问」的成员，
        // 而 ConfigurationBinder 恰恰是纯反射绑定。DomainConfig / ResponseConfig 都是 record + init-only 属性，
        // 其属性 setter 只被绑定器引用，会被裁剪；绑定器随后找不到可写属性，「静默」保留 C# 默认值——
        // 字典键仍能建立（DNS 劫持照常），但 Response / Destination / TlsIgnoreNameMismatch / TlsSni
        // 全部失效且不抛任何异常（真机 pre12 已复现：collector.github.com 的 Response=204 与 avatars 全线 502）。
        // 原实现只标注了【字典类型本身】，未覆盖【值类型 DomainConfig】及其【嵌套值类型 ResponseConfig】。
        // 这里把值类型与选项类型一并 root；同时 Directory.Build.props 用 TrimmerRootAssembly 把整个
        // FastGithub.Configuration 程序集 root（互为双保险），启动期再用 CheckConfigurationBinding 自检。
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Dictionary<string, DomainConfig>))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(DomainConfig))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ResponseConfig))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(FastGithubOptions))]
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

        /// <summary>
        /// [PATCH] 配置绑定自检：把「静默失效」变成启动期 LogError。
        /// PublishTrimmed 裁掉配置类型的属性 setter 时，ConfigurationBinder 不会抛异常，只会安静地保留
        /// C# 默认值，导致 Response / Destination / TlsIgnoreNameMismatch / TlsSni 等核心功能失效却
        /// 无任何日志线索（真机 pre12 的 Response=204 失效与 avatars 全线 502 即由此而来）。
        /// 这里在启动时把「JSON 中已声明」与「运行时绑定值」逐一比对，任何一项未生效即 LogError。
        /// 自检本身失败绝不阻断启动。
        /// </summary>
        /// <param name="app">已构建的 web 应用（其 Services 已可解析 IOptions/IConfiguration）</param>
        public static void CheckConfigurationBinding(this WebApplication app)
        {
            try
            {
                var configuration = app.Services.GetRequiredService<IConfiguration>();
                var fastGithubOptions = app.Services.GetRequiredService<IOptions<FastGithubOptions>>().Value;

                var domainConfigsSection = configuration.GetSection(
                    $"{nameof(FastGithub)}:{nameof(FastGithubOptions.DomainConfigs)}");
                if (domainConfigsSection.Exists() == false)
                {
                    return;
                }

                var dropped = new List<string>();
                foreach (var item in domainConfigsSection.GetChildren())
                {
                    var domain = item.Key;
                    if (fastGithubOptions.DomainConfigs.TryGetValue(domain, out var config) == false)
                    {
                        // 连条目本身都没绑上：说明 Dictionary 的 Add 都没发生（远不止 setter 被裁）
                        dropped.Add($"{domain}（整条目未绑定）");
                        continue;
                    }

                    // bool 属性：仅当 JSON 声明 true 而绑定值仍为默认 false 时判定为丢失
                    var tlsSniText = item.GetSection(nameof(DomainConfig.TlsSni)).Value;
                    if (bool.TryParse(tlsSniText, out var tlsSni) && config.TlsSni != tlsSni)
                    {
                        dropped.Add($"{domain}.TlsSni（声明 {tlsSni}，实际 {config.TlsSni}）");
                    }

                    var ignoreMismatchText = item.GetSection(nameof(DomainConfig.TlsIgnoreNameMismatch)).Value;
                    if (bool.TryParse(ignoreMismatchText, out var ignoreMismatch)
                        && config.TlsIgnoreNameMismatch != ignoreMismatch)
                    {
                        dropped.Add($"{domain}.TlsIgnoreNameMismatch（声明 {ignoreMismatch}，实际 {config.TlsIgnoreNameMismatch}）");
                    }

                    // 引用类型 / 复杂类型：JSON 里有该子节点但绑定值为 null 即判定为丢失
                    if (item.GetSection(nameof(DomainConfig.TlsSniPattern)).Exists() && config.TlsSniPattern == null)
                    {
                        dropped.Add($"{domain}.TlsSniPattern");
                    }
                    if (item.GetSection(nameof(DomainConfig.Destination)).Exists() && config.Destination == null)
                    {
                        dropped.Add($"{domain}.Destination");
                    }
                    if (item.GetSection(nameof(DomainConfig.IPAddress)).Exists() && config.IPAddress == null)
                    {
                        dropped.Add($"{domain}.IPAddress");
                    }

                    // 嵌套的 ResponseConfig：整体为 null，或整体在但 StatusCode 未绑上，二者都算丢失
                    var responseSection = item.GetSection(nameof(DomainConfig.Response));
                    if (responseSection.Exists())
                    {
                        if (config.Response == null)
                        {
                            dropped.Add($"{domain}.Response");
                        }
                        else
                        {
                            var statusCodeText = responseSection.GetSection(nameof(ResponseConfig.StatusCode)).Value;
                            if (int.TryParse(statusCodeText, out var statusCode) && config.Response.StatusCode != statusCode)
                            {
                                dropped.Add($"{domain}.Response.StatusCode（声明 {statusCode}，实际 {config.Response.StatusCode}）");
                            }
                        }
                    }
                }

                if (dropped.Count == 0)
                {
                    return;
                }

                var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Startup));
                logger.LogError(
                    "配置绑定自检失败：以下域名配置在 JSON 中已声明，但未绑定到运行时对象，相关功能会静默失效。\n" +
                    "最可能原因：PublishTrimmed 裁剪掉了配置类型的属性 setter（DomainConfig / ResponseConfig）。\n" +
                    "请确认 src-patches/Directory.Build.props 中的 TrimmerRootAssembly（FastGithub.Configuration）" +
                    "与 Startup.ConfigureServices 上的 [DynamicDependency] 标注仍存在。\n失败项：{Dropped}",
                    string.Join("；", dropped));
            }
            catch (Exception ex)
            {
                // 自检不得影响启动：解析/绑定任何环节抛错都只降级为一条 Warning
                try
                {
                    app.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(nameof(Startup))
                        .LogWarning(ex, "配置绑定自检执行失败（不影响启动）");
                }
                catch (Exception)
                {
                    // 连日志设施都不可用时静默忽略
                }
            }
        }
    }
}
