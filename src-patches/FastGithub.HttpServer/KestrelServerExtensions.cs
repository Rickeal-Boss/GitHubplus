using FastGithub.Configuration;
using FastGithub.HttpServer.Certs;
using FastGithub.HttpServer.TcpMiddlewares;
using FastGithub.HttpServer.TlsMiddlewares;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace FastGithub
{
    /// <summary>
    /// Kestrel扩展
    /// </summary>
    public static class KestrelServerExtensions
    {
        /// <summary>
        /// 无限制
        /// </summary>
        /// <param name="kestrel"></param>
        public static void NoLimit(this KestrelServerOptions kestrel)
        {
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Limits.MinRequestBodyDataRate = null;
        }

        /// <summary>
        /// 监听http代理(Linux)
        /// </summary>
        /// <param name="kestrel"></param>
        public static void ListenHttpProxy(this KestrelServerOptions kestrel)
        {
            var options = kestrel.ApplicationServices.GetRequiredService<IOptions<FastGithubOptions>>().Value;
            var httpProxyPort = options.HttpProxyPort;

            if (GlobalListener.CanListenTcp(httpProxyPort) == false)
            {
                throw new FastGithubException($"tcp端口{httpProxyPort}已经被其它进程占用，请在配置文件更换{nameof(FastGithubOptions.HttpProxyPort)}为其它端口");
            }

            kestrel.ListenLocalhost(httpProxyPort, listen =>
            {
                var proxyMiddleware = kestrel.ApplicationServices.GetRequiredService<HttpProxyMiddleware>();
                var tunnelMiddleware = kestrel.ApplicationServices.GetRequiredService<TunnelMiddleware>();

                listen.Use(next => context => proxyMiddleware.InvokeAsync(next, context));
                listen.UseTls();
                listen.Use(next => context => tunnelMiddleware.InvokeAsync(next, context));
            });

            kestrel.GetLogger().LogInformation($"已监听 http://localhost:{httpProxyPort}，http代理服务启动完成");
        }

        /// <summary>
        /// 监听ssh协议代理
        /// </summary>
        /// <param name="kestrel"></param>
        public static void ListenSshReverseProxy(this KestrelServerOptions kestrel)
        {
            var sshPort = GlobalListener.SshPort;
            kestrel.ListenLocalhost(sshPort, listen =>
            {
                listen.UseFlowAnalyze();
                listen.UseConnectionHandler<GithubSshReverseProxyHandler>();
            });

            kestrel.GetLogger().LogInformation($"已监听 ssh://localhost:{sshPort}，github的ssh反向代理服务启动完成");
        }

        /// <summary>
        /// 监听git协议代理代理
        /// </summary>
        /// <param name="kestrel"></param>
        public static void ListenGitReverseProxy(this KestrelServerOptions kestrel)
        {
            var gitPort = GlobalListener.GitPort;
            kestrel.ListenLocalhost(gitPort, listen =>
            {
                listen.UseFlowAnalyze();
                listen.UseConnectionHandler<GithubGitReverseProxyHandler>();
            });

            kestrel.GetLogger().LogInformation($"已监听 git://localhost:{gitPort}，github的git反向代理服务启动完成");
        }

        /// <summary>
        /// 监听http反向代理
        /// </summary>
        /// <param name="kestrel"></param>
        public static void ListenHttpReverseProxy(this KestrelServerOptions kestrel)
        {
            var httpPort = GlobalListener.HttpPort;
            kestrel.ListenLocalhost(httpPort);

            if (OperatingSystem.IsWindows())
            {
                kestrel.GetLogger().LogInformation($"已监听 http://localhost:{httpPort}，http反向代理服务启动完成");
            }
        }

        /// <summary>
        /// 监听https反向代理
        /// </summary>
        /// <param name="kestrel"></param>
        /// <exception cref="FastGithubException"></exception>
        public static void ListenHttpsReverseProxy(this KestrelServerOptions kestrel)
        {
            var httpsPort = GlobalListener.HttpsPort;
            kestrel.ListenLocalhost(httpsPort, listen =>
            {
                if (OperatingSystem.IsWindows())
                {
                    listen.UseFlowAnalyze();
                }
                listen.UseTls();
            });

            if (OperatingSystem.IsWindows())
            {
                var logger = kestrel.GetLogger();
                logger.LogInformation($" 已监听 https://localhost:{httpsPort}，https反向代理服务启动完成");
            }
        }

        /// <summary>
        /// 获取日志
        /// </summary>
        /// <param name="kestrel"></param>
        /// <returns></returns>
        private static ILogger GetLogger(this KestrelServerOptions kestrel)
        {
            var loggerFactory = kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>();
            return loggerFactory.CreateLogger($"{nameof(FastGithub)}.{nameof(HttpServer)}");
        }

        /// <summary>
        /// 使用Tls中间件
        /// </summary>
        /// <param name="listen"></param>
        /// <param name="configureOptions">https配置</param>
        /// <returns></returns>
        public static ListenOptions UseTls(this ListenOptions listen)
        {
            var certService = listen.ApplicationServices.GetRequiredService<CertService>();
            certService.CreateCaCertIfNotExists();
            certService.InstallAndTrustCaCert();
            return listen.UseTls(domain => certService.GetOrCreateServerCert(domain));
        }

        /// <summary>
        /// 使用Tls中间件
        /// </summary>
        /// <param name="listen"></param>
        /// <param name="configureOptions">https配置</param>
        /// <returns></returns>
        private static ListenOptions UseTls(this ListenOptions listen, Func<string, X509Certificate2> certFactory)
        {
            var invadeMiddleware = listen.ApplicationServices.GetRequiredService<TlsInvadeMiddleware>();
            var restoreMiddleware = listen.ApplicationServices.GetRequiredService<TlsRestoreMiddleware>();
            var fastGithubConfig = listen.ApplicationServices.GetRequiredService<FastGithubConfig>();
            var logger = listen.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger($"{nameof(FastGithub)}.{nameof(HttpServer)}");

            listen.Use(next => context => invadeMiddleware.InvokeAsync(next, context));
            listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                OnConnection = context =>
                {
                    var serverName = context.ClientHelloInfo.ServerName;
                    // 只对发送了SNI且不在加速白名单内的域名做拦截；
                    // 未发送SNI的老客户端(null/空串)保持原有行为，否则会打断其连接。
                    // 注意：不能把「空 SNI 也一律拒绝」——IP 字面量直连（如 https://127.0.0.1）
                    // 本身就不发 SNI，拒绝会直接打断本机直连；而 IP/localhost/机器名都不是
                    // 有价值的 MITM 目标，放行它们不扩大攻击面（详见 IsLocalHostName 的说明）。
                    // 白名单来源与DNS投毒用的是同一个 FastGithubConfig，正常加速域名必然匹配。
                    if (string.IsNullOrEmpty(serverName) == false &&
                        IsLocalHostName(serverName) == false &&
                        fastGithubConfig.IsMatch(serverName) == false)
                    {
                        logger.LogWarning($"拒绝为不在加速白名单内的域名{serverName}签发证书，已中止TLS握手");
                        throw new AuthenticationException($"域名{serverName}不在加速白名单内，已中止TLS握手");
                    }

                    var options = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certFactory(context.ClientHelloInfo.ServerName)
                    };
                    return ValueTask.FromResult(options);
                },
            });
            listen.Use(next => context => restoreMiddleware.InvokeAsync(next, context));
            return listen;
        }

        /// <summary>
        /// [PATCH] 是否为本机地址直连：IP 字面量或无点主机名（localhost、机器名）。
        /// 上游本来就期望这类访问可用 —— CertService.GetExtraDomains() 会把机器名、
        /// 127.0.0.1、::1 写进每一张叶子证书的 SAN，若一并拒掉会打断本机直连。
        /// 放行它们不影响安全性：伪造 SNI 去 MITM 一个非白名单域名，用的必然是带点的
        /// 真实域名，仍会被白名单拦下；而 IP 字面量 / localhost / 机器名都不是有意义的
        /// MITM 目标。
        /// </summary>
        /// <param name="serverName">客户端发送的SNI</param>
        /// <returns></returns>
        private static bool IsLocalHostName(string serverName)
        {
            if (IPAddress.TryParse(serverName, out _))
            {
                return true;
            }

            return serverName.IndexOf('.') < 0;
        }
    }
}
