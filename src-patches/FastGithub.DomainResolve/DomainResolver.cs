using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary> 
    sealed class DomainResolver : IDomainResolver
    {
        private const int MAX_IP_COUNT = 3;

        /// <summary>
        /// 解析出0个可用IP时的负缓存时长（毫秒）
        /// 缓存命中条件是 addresses.Length > 0，而全失败时写入的是空数组，
        /// 空数组永不命中会导致每个请求都重走「DNS解析+竞速+串行连接全部候选IP」，
        /// 单IP连接超时为10秒，最终放大为重试风暴。故对失败结果做短时负缓存。
        /// </summary>
        private const int NEGATIVE_CACHE_MILLISECONDS = 30 * 1000;

        private readonly DnsClient dnsClient;
        private readonly PersistenceService persistence;
        private readonly IPAddressService addressService;
        private readonly ILogger<DomainResolver> logger;
        private readonly ConcurrentDictionary<DnsEndPoint, IPAddress[]> dnsEndPointAddress = new();

        /// <summary>
        /// 解析失败的负缓存：key为"host:port"，value为失败时刻(Environment.TickCount64)
        /// </summary>
        private readonly ConcurrentDictionary<string, long> negativeCache = new();

        /// <summary>
        /// 域名解析器
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="persistence"></param>
        /// <param name="addressService"></param>
        /// <param name="logger"></param>
        public DomainResolver(
            DnsClient dnsClient,
            PersistenceService persistence,
            IPAddressService addressService,
            ILogger<DomainResolver> logger)
        {
            this.dnsClient = dnsClient;
            this.persistence = persistence;
            this.addressService = addressService;
            this.logger = logger;

            foreach (var endPoint in persistence.ReadDnsEndPoints())
            {
                this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>());
            }
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 近期解析失败的域名直接返回空结果，不再触发完整的DNS解析与竞速
            if (this.IsNegativeCached(endPoint) == true)
            {
                yield break;
            }

            if (this.dnsEndPointAddress.TryGetValue(endPoint, out var addresses) && addresses.Length > 0)
            {
                foreach (var address in addresses)
                {
                    // [PATCH] 缓存里可能残留链路本地地址（来自持久化文件），下发前同样过滤
                    if (IsConnectableAddress(address))
                    {
                        yield return address;
                    }
                }
            }
            else
            {
                if (this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>()))
                {
                    await this.persistence.WriteDnsEndPointsAsync(this.dnsEndPointAddress.Keys, cancellationToken);
                }

                // DNS可能返回远超MAX_IP_COUNT个记录，而下层对每个IP串行尝试连接(单IP超时10秒)，
                // 不截断会让单次请求最坏耗时达到 记录数 × 10秒
                var addressCount = 0;
                await foreach (var adddress in this.dnsClient.ResolveAsync(endPoint, fastSort: true, cancellationToken))
                {
                    // [PATCH] 丢弃链路本地地址（IPv4 169.254.0.0/16、IPv6 fe80::/10）：
                    // 它们只在本地链路有效，绝不可能是目标服务器地址；下发后下层会对它发起
                    // 必然超时的连接（单 IP 10 秒），放大成重试风暴。回环地址仍放行。
                    if (IsConnectableAddress(adddress) == false)
                    {
                        continue;
                    }

                    yield return adddress;
                    addressCount = addressCount + 1;
                    if (addressCount >= MAX_IP_COUNT)
                    {
                        break;
                    }
                }

                if (addressCount == 0)
                {
                    this.SetNegativeCache(endPoint);
                }
            }
        }

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task TestSpeedAsync(CancellationToken cancellationToken)
        {
            foreach (var keyValue in this.dnsEndPointAddress.OrderBy(item => item.Value.Length))
            {
                var dnsEndPoint = keyValue.Key;
                var oldAddresses = keyValue.Value;

                var newAddresses = await this.addressService.GetAddressesAsync(dnsEndPoint, oldAddresses, cancellationToken);
                // [PATCH] 先过滤链路本地地址再截断，避免无效 IP 占满 MAX_IP_COUNT 配额
                newAddresses = newAddresses.Where(IsConnectableAddress).Take(MAX_IP_COUNT).ToArray();
                this.dnsEndPointAddress[dnsEndPoint] = newAddresses;

                if (newAddresses.Length == 0)
                {
                    this.SetNegativeCache(dnsEndPoint);
                }
                else
                {
                    this.RemoveNegativeCache(dnsEndPoint);
                }

                var oldSegmentums = oldAddresses.Take(MAX_IP_COUNT);
                var newSegmentums = newAddresses.Take(MAX_IP_COUNT);
                // [PATCH] 改为顺序无关比较：DNS 常对同一组 IP 做轮转（集合不变、顺序变），
                // 而 SequenceEqual 是顺序敏感的，会把「仅顺序不同」误判为「地址已变更」，
                // 于是白白触发一轮地址更新并多刷一条日志（真机日志里的成片噪声即属此类）。
                // 排序后再比，保持原有「集合/数量有变化才算变更、变更才记日志」的语义不变。
                if (IsSameAddressSet(oldSegmentums, newSegmentums) == false)
                {
                    var addressArray = string.Join(", ", newSegmentums.Select(item => item.ToString()));
                    this.logger.LogInformation($"{dnsEndPoint.Host}:{dnsEndPoint.Port}->[{addressArray}]");
                }
            }
        }

        /// <summary>
        /// [PATCH] 判断地址是否可用于对外连接。
        /// 过滤 IPv4 链路本地（169.254.0.0/16，即 APIPA）与 IPv6 链路本地（fe80::/10）：
        /// 这些地址只在本地链路有效，绝不可能是目标服务器的公网地址；一旦被解析出来，
        /// 下层会对它发起连接并吃满 10 秒超时，放大成重试风暴。
        /// 注意保留回环地址（127.0.0.1 / ::1）——用户把域名在 hosts 里指向本机是合法用法。
        /// </summary>
        /// <param name="address">待判断的地址</param>
        /// <returns>可用于连接时返回 true</returns>
        private static bool IsConnectableAddress(IPAddress address)
        {
            // 回环地址始终放行（127.0.0.0/8 与 ::1）
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                // 169.254.0.0/16：IPv4 链路本地（APIPA）
                return bytes[0] != 169 || bytes[1] != 254;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // fe80::/10：IPv6 链路本地。要求前 10 位为 1111111010，
                // 即首字节为 0xFE、第二字节高 2 位为 10（0x80~0xBF）。
                return bytes[0] != 0xFE || (bytes[1] & 0xC0) != 0x80;
            }

            return true;
        }

        /// <summary>
        /// [PATCH] 判断两个地址序列是否表示同一组地址（顺序无关、数量/重复敏感）。
        /// IPAddress 未实现 IComparable，故按其规范字符串（ToString）做 Ordinal 排序后再逐项比较，
        /// 从而消除 DNS 轮转导致的「仅顺序不同」误判。
        /// </summary>
        /// <param name="x">地址序列</param>
        /// <param name="y">地址序列</param>
        /// <returns>表示同一组地址时返回 true</returns>
        private static bool IsSameAddressSet(IEnumerable<IPAddress> x, IEnumerable<IPAddress> y)
        {
            var sortedX = x.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToArray();
            var sortedY = y.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToArray();
            return sortedX.SequenceEqual(sortedY);
        }

        /// <summary>
        /// 获取负缓存的键
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <returns></returns>
        private static string GetNegativeCacheKey(DnsEndPoint endPoint)
        {
            return $"{endPoint.Host}:{endPoint.Port}";
        }

        /// <summary>
        /// 是否处于解析失败的负缓存期内
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <returns></returns>
        private bool IsNegativeCached(DnsEndPoint endPoint)
        {
            var key = GetNegativeCacheKey(endPoint);
            if (this.negativeCache.TryGetValue(key, out var tickCount) == false)
            {
                return false;
            }

            if (Environment.TickCount64 - tickCount >= NEGATIVE_CACHE_MILLISECONDS)
            {
                this.negativeCache.TryRemove(key, out _);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 记录一次解析失败
        /// </summary>
        /// <param name="endPoint">节点</param>
        private void SetNegativeCache(DnsEndPoint endPoint)
        {
            this.negativeCache[GetNegativeCacheKey(endPoint)] = Environment.TickCount64;
        }

        /// <summary>
        /// 清除解析失败的负缓存
        /// </summary>
        /// <param name="endPoint">节点</param>
        private void RemoveNegativeCache(DnsEndPoint endPoint)
        {
            this.negativeCache.TryRemove(GetNegativeCacheKey(endPoint), out _);
        }
    }
}
