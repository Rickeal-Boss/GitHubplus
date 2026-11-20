using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP服务
    /// 域名IP关系缓存10分钟
    /// IPEndPoint时延缓存5分钟
    /// IPEndPoint连接超时5秒
    /// </summary>
    sealed class IPAddressService
    {
        /// <summary>
        /// 单个域名一轮拨测的最大并发Socket数
        /// 原实现用 Task.WhenAll 一次性并发拨测全部候选IP且无上限，
        /// 不可达域名会在每个周期重建一批并发连接。
        /// </summary>
        private const int MAX_CONCURRENT_TEST_COUNT = 8;

        private record DomainAddress(string Domain, IPAddress Address);
        private readonly TimeSpan domainAddressExpiration = TimeSpan.FromMinutes(10d);
        private readonly IMemoryCache domainAddressCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private record AddressElapsed(IPAddress Address, TimeSpan Elapsed);
        // [PATCH 0006b] 失败耗时一律短过期。原实现只对 NetworkDown / NetworkUnreachable
        // 用 1 分钟，其余失败（远端超时、连接被拒、TLS 失败等更常见的波动）用
        // normalElapsedExpiration=5 分钟，于是「波动已过但 IP 仍被记为 TimeSpan.MaxValue」
        // 最长残留 5 分钟，是空闲/波动后首请求变慢的直接来源之一。
        // 失败记录本就该快速重试；正常测速结果仍保留 5 分钟。
        private readonly TimeSpan problemElapsedExpiration = TimeSpan.FromSeconds(30d);
        private readonly TimeSpan failedElapsedExpiration = TimeSpan.FromMinutes(1d);
        private readonly TimeSpan normalElapsedExpiration = TimeSpan.FromMinutes(5d);
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(5d);
        // [PATCH 0006b] 改为具体 MemoryCache：恢复路径要枚举并只剔除失败条目，
        // IMemoryCache 接口没有枚举能力（与 DnsClient.ClearCaches 同样的处理）。
        private readonly MemoryCache addressElapsedCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        /// <summary>
        /// 并发拨测闸门
        /// </summary>
        private readonly SemaphoreSlim concurrentTestGate = new SemaphoreSlim(MAX_CONCURRENT_TEST_COUNT, MAX_CONCURRENT_TEST_COUNT);

        /// <summary>
        /// 整轮拨测闸门：上一轮没跑完就不叠加下一轮
        /// </summary>
        private readonly SemaphoreSlim roundTestGate = new SemaphoreSlim(1, 1);

        private readonly DnsClient dnsClient;

        /// <summary>
        /// IP服务
        /// </summary>
        /// <param name="dnsClient"></param>
        public IPAddressService(DnsClient dnsClient)
        {
            this.dnsClient = dnsClient;
        }

        /// <summary>
        /// 并行获取可连接的IP
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="oldAddresses"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IPAddress[]> GetAddressesAsync(DnsEndPoint dnsEndPoint, IEnumerable<IPAddress> oldAddresses, CancellationToken cancellationToken)
        {
            await this.roundTestGate.WaitAsync(cancellationToken);
            try
            {
                return await this.GetAddressesCoreAsync(dnsEndPoint, oldAddresses, cancellationToken);
            }
            finally
            {
                this.roundTestGate.Release();
            }
        }

        /// <summary>
        /// 并行获取可连接的IP的实现
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="oldAddresses"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IPAddress[]> GetAddressesCoreAsync(DnsEndPoint dnsEndPoint, IEnumerable<IPAddress> oldAddresses, CancellationToken cancellationToken)
        {
            var ipEndPoints = new HashSet<IPEndPoint>();

            // 历史未过期的IP节点
            foreach (var address in oldAddresses)
            {
                var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                if (this.domainAddressCache.TryGetValue(domainAddress, out _))
                {
                    ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                }
            }

            // 新解析出的IP节点
            await foreach (var address in this.dnsClient.ResolveAsync(dnsEndPoint, fastSort: false, cancellationToken))
            {
                ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                this.domainAddressCache.Set(domainAddress, default(object), this.domainAddressExpiration);
            }

            if (ipEndPoints.Count == 0)
            {
                return Array.Empty<IPAddress>();
            }

            var addressElapsedTasks = ipEndPoints.Select(item => this.GetAddressElapsedWithGateAsync(item, cancellationToken));
            var addressElapseds = await Task.WhenAll(addressElapsedTasks);

            return addressElapseds
                .Where(item => item.Elapsed < TimeSpan.MaxValue)
                .OrderBy(item => item.Elapsed)
                .Select(item => item.Address)
                .ToArray();
        }


        /// <summary>
        /// 获取IP节点的时延
        /// </summary> 
        /// <param name="endPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<AddressElapsed> GetAddressElapsedWithGateAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
        {
            await this.concurrentTestGate.WaitAsync(cancellationToken);
            try
            {
                return await this.GetAddressElapsedAsync(endPoint, cancellationToken);
            }
            finally
            {
                this.concurrentTestGate.Release();
            }
        }

        /// <summary>
        /// 获取IP节点的时延
        /// </summary> 
        /// <param name="endPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<AddressElapsed> GetAddressElapsedAsync(IPEndPoint endPoint, CancellationToken cancellationToken)
        {
            if (this.addressElapsedCache.TryGetValue<AddressElapsed>(endPoint, out var addressElapsed))
            {
                return addressElapsed;
            }

            var stopWatch = Stopwatch.StartNew();
            try
            {
                using var timeoutTokenSource = new CancellationTokenSource(this.connectTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(endPoint, linkedTokenSource.Token);

                addressElapsed = new AddressElapsed(endPoint.Address, stopWatch.Elapsed);
                return this.addressElapsedCache.Set(endPoint, addressElapsed, this.normalElapsedExpiration);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                addressElapsed = new AddressElapsed(endPoint.Address, TimeSpan.MaxValue);
                var expiration = IsLocalNetworkProblem(ex) ? this.problemElapsedExpiration : this.failedElapsedExpiration;
                return this.addressElapsedCache.Set(endPoint, addressElapsed, expiration);
            }
            finally
            {
                stopWatch.Stop();
            }
        }

        /// <summary>
        /// [PATCH 0006b] 网络恢复后清掉失败耗时记录（D-2），保留正常测速结果。
        /// 只剔除 Elapsed == TimeSpan.MaxValue 的条目：全清会让所有 IP 重新竞速，
        /// 反而把恢复首请求拖慢。由 NetworkWatchService 在 ResumeAsync 成功后调用。
        /// 纯空闲退化不走这条路径，由短过期 + DomainResolver 的 stale-while-error 处理。
        /// </summary>
        public void ClearFailureCaches()
        {
            // 先收集再删，避免在任何 MemoryCache 实现上出现「枚举中修改」的歧义
            var staleKeys = new List<object>();
            // [0007-fix] MemoryCache 只显式实现非泛型 IEnumerable（GetEnumerator 为显式接口实现），
            // 直接 foreach MemoryCache 类型变量编译失败（CI 实测：CS01579 does not contain public GetEnumerator）。
            // cast 到 IEnumerable 并用 KeyValuePair<object, object> 显式类型枚举：MemoryCache.GetEnumerator()
            // 的 Current 运行时即 KeyValuePair<object, object>（CacheEntry 转换而来），类型安全。
            foreach (KeyValuePair<object, object> item in (System.Collections.IEnumerable)this.addressElapsedCache)
            {
                if (item.Value is AddressElapsed elapsed && elapsed.Elapsed == TimeSpan.MaxValue)
                {
                    staleKeys.Add(item.Key);
                }
            }

            foreach (var key in staleKeys)
            {
                this.addressElapsedCache.Remove(key);
            }
        }

        /// <summary>
        /// 是否为本机网络问题
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static bool IsLocalNetworkProblem(Exception ex)
        {
            if (ex is not SocketException socketException)
            {
                return false;
            }

            var code = socketException.SocketErrorCode;
            return code == SocketError.NetworkDown || code == SocketError.NetworkUnreachable;
        }
    }
}
