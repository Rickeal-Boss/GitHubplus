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
        private readonly TimeSpan problemElapsedExpiration = TimeSpan.FromMinutes(1d);
        private readonly TimeSpan normalElapsedExpiration = TimeSpan.FromMinutes(5d);
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(5d);
        private readonly IMemoryCache addressElapsedCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

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
                var expiration = IsLocalNetworkProblem(ex) ? this.problemElapsedExpiration : this.normalElapsedExpiration;
                return this.addressElapsedCache.Set(endPoint, addressElapsed, expiration);
            }
            finally
            {
                stopWatch.Stop();
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
