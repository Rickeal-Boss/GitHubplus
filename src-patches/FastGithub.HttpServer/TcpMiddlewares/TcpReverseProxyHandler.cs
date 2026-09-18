using FastGithub.DomainResolve;
using Microsoft.AspNetCore.Connections;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.HttpServer.TcpMiddlewares
{
    /// <summary>
    /// tcp协议代理处理者
    /// </summary>
    ///
    /// [PATCH] 相对上游的唯一改动：CreateConnectionAsync 里成功路径的 NetworkStream
    /// 由 ownsSocket: false 改为 true（详见方法内注释）。用于消除 ssh/git 反向代理的
    /// Socket 句柄泄漏。
    abstract class TcpReverseProxyHandler : ConnectionHandler
    {
        private readonly IDomainResolver domainResolver;
        private readonly DnsEndPoint endPoint;
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(10d);

        /// <summary>
        /// tcp协议代理处理者
        /// </summary>
        /// <param name="domainResolver"></param>
        /// <param name="endPoint"></param>
        public TcpReverseProxyHandler(IDomainResolver domainResolver, DnsEndPoint endPoint)
        {
            this.domainResolver = domainResolver;
            this.endPoint = endPoint;
        }

        /// <summary>
        /// tcp连接后
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override async Task OnConnectedAsync(ConnectionContext context)
        {
            var cancellationToken = context.ConnectionClosed;
            using var connection = await CreateConnectionAsync(cancellationToken);
            var task1 = connection.CopyToAsync(context.Transport.Output, cancellationToken);
            var task2 = context.Transport.Input.CopyToAsync(connection, cancellationToken);
            await Task.WhenAny(task1, task2);
        }

        /// <summary>
        /// 创建连接
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="AggregateException"></exception>
        private async Task<Stream> CreateConnectionAsync(CancellationToken cancellationToken)
        {
            var innerExceptions = new List<Exception>();
            await foreach (var address in domainResolver.ResolveAsync(endPoint, cancellationToken))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    using var timeoutTokenSource = new CancellationTokenSource(connectTimeout);
                    using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                    await socket.ConnectAsync(address, endPoint.Port, linkedTokenSource.Token);

                    // [PATCH] 原为 ownsSocket: false。
                    // 该返回值被 OnConnectedAsync 用 `using var connection` 持有，流被释放时
                    // 会因为 ownsSocket=false 而**不释放底层 Socket**；而这里成功分支直接 return，
                    // 也不会走到 catch 里的 socket.Dispose()。
                    // 结果是：每建立一条 ssh(:22) / git(:9418) 反向代理连接就泄漏一个 Socket 句柄，
                    // 只能等终结器兜底；长时间高频使用 git over ssh 会撑高句柄数直至耗尽端口。
                    // 同目录的 TunnelMiddleware 用的就是 ownsSocket: true，这里保持一致。
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    innerExceptions.Add(ex);
                }
            }
            throw new AggregateException($"无法连接到{endPoint.Host}:{endPoint.Port}", innerExceptions);
        }
    }
}
