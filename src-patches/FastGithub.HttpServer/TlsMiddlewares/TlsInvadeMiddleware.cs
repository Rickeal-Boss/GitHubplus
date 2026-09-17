using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace FastGithub.HttpServer.TlsMiddlewares
{
    /// <summary>
    /// https入侵中间件
    /// </summary>
    sealed class TlsInvadeMiddleware
    {
        private readonly ILogger<TlsInvadeMiddleware> logger;

        /// <summary>
        /// https入侵中间件
        /// </summary>
        /// <param name="logger"></param>
        public TlsInvadeMiddleware(ILogger<TlsInvadeMiddleware> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// 执行中间件
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task InvokeAsync(ConnectionDelegate next, ConnectionContext context)
        {
            // 连接不是tls
            if (await IsTlsConnectionAsync(context) == false)
            {
                // 没有任何tls中间件执行过
                if (context.Features.Get<ITlsConnectionFeature>() == null)
                {
                    // 设置假的ITlsConnectionFeature，迫使https中间件跳过自身的工作
                    context.Features.Set<ITlsConnectionFeature>(FakeTlsConnectionFeature.Instance);
                }
            }
            await next(context);
        }


        /// <summary>
        /// 是否为tls协议
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task<bool> IsTlsConnectionAsync(ConnectionContext context)
        {
            try
            {
                var result = await context.Transport.Input.ReadAtLeastAsync(2, context.ConnectionClosed);
                var state = IsTlsProtocol(result);
                context.Transport.Input.AdvanceTo(result.Buffer.Start);
                return state;
            }
            catch (Exception ex)
            {
                // [PATCH] 原为裸 `catch { return false; }`，会静默吞掉一切异常。
                // 此处的异常主要来自连接中断/取消（context.ConnectionClosed 触发、
                // 对端在发出 2 字节前断开），属预期路径，仍返回 false 让上层按“非 tls”处理；
                // 但为避免真正的问题被完全吞没，记一条 Debug（当前 MinimumLevel=Information，
                // Debug 默认不落盘，需要排查时调低级别即可取回完整信息）。
                this.logger.LogDebug(ex, "读取连接前两个字节判断是否为 tls 协议时失败，按非 tls 处理");
                return false;
            }

            static bool IsTlsProtocol(ReadResult result)
            {
                var reader = new SequenceReader<byte>(result.Buffer);
                return reader.TryRead(out var firstByte) &&
                    reader.TryRead(out var nextByte) &&
                    firstByte == 0x16 &&
                    nextByte == 0x3;
            }
        }
    }
}
