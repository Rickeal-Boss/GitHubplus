using FastGithub.HttpServer.Certs;
using FastGithub.HttpServer.Certs.CaCertInstallers;
using FastGithub.HttpServer.HttpMiddlewares;
using FastGithub.HttpServer.TcpMiddlewares;
using FastGithub.HttpServer.TlsMiddlewares;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FastGithub
{
    /// <summary>
    /// http反向代理的服务注册扩展
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 添加http反向代理
        /// </summary>
        /// <param name="services"></param> 
        /// <returns></returns>
        public static IServiceCollection AddReverseProxy(this IServiceCollection services)
        {
            // [PATCH] 原为 .AddMemoryCache()，未配置 SizeLimit。
            // CertService.GetOrCreateServerCert 以域名为 key 缓存 X509Certificate2（非托管句柄），
            // 访问大量不同子域（如 *.cloudfront.net 的随机 hash 子域）会无界增长直至 OOM。
            // 现配置 SizeLimit=4096，配合 CertService 里 entry.SetSize(1) + RegisterPostEvictionCallback
            // 实现 LRU 淘汰 + 被淘汰证书的 Dispose。
            return services
                .AddMemoryCache(options =>
                {
                    options.SizeLimit = 4096;
                })
                .AddHttpForwarder()
                .AddSingleton<CertService>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfMacOS>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfWindows>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfLinuxRedHat>()
                .AddSingleton<ICaCertInstaller, CaCertInstallerOfLinuxDebian>()

                // tcp
                .AddSingleton<HttpProxyMiddleware>()
                .AddSingleton<TunnelMiddleware>()

                // tls
                .AddSingleton<TlsInvadeMiddleware>()
                .AddSingleton<TlsRestoreMiddleware>()

                // http
                .AddSingleton<HttpProxyPacMiddleware>()
                .AddSingleton<RequestLoggingMiddleware>()
                .AddSingleton<HttpReverseProxyMiddleware>();
        }
    }
}
