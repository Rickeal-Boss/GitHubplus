using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace FastGithub.Configuration
{
    /// <summary>
    /// 监听器
    /// </summary>
    public static class GlobalListener
    {
        private static readonly IPGlobalProperties global = IPGlobalProperties.GetIPGlobalProperties();
        private static readonly HashSet<int> tcpListenPorts = GetListenPorts(global.GetActiveTcpListeners);
        private static readonly HashSet<int> udpListenPorts = GetListenPorts(global.GetActiveUdpListeners);

        /// <summary>
        /// ssh端口
        /// </summary>
        public static int SshPort { get; } = GetAvailableTcpPort(22);

        /// <summary>
        /// git端口
        /// </summary>
        public static int GitPort { get; } = GetAvailableTcpPort(9418);

        /// <summary>
        /// http端口
        /// </summary>
        public static int HttpPort { get; } = OperatingSystem.IsWindows() ? GetAvailableTcpPort(80) : GetAvailableTcpPort(3880);

        /// <summary>
        /// https端口
        /// </summary>
        public static int HttpsPort { get; } = OperatingSystem.IsWindows() ? GetAvailableTcpPort(443) : GetAvailableTcpPort(38443);

        /// <summary>
        /// 获取已监听的端口
        /// </summary>
        /// <param name="func"></param>
        /// <returns></returns>
        private static HashSet<int> GetListenPorts(Func<IPEndPoint[]> func)
        {
            var hashSet = new HashSet<int>();
            try
            {
                foreach (var endpoint in func())
                {
                    // 只统计会影响本机回环监听的地址：0.0.0.0 / [::] / 127.0.0.1 / [::1]。
                    // 原实现只取 endpoint.Port 丢掉 Address，某块网卡IP上的 443 被占用
                    // 就会把回环 443 也误判为占用，导致 HttpsPort 漂移到 444，
                    // 而TCP重定向过滤器不区分目的IP，会把回环上所有443的包一并改写到444。
                    if (IsLoopbackOrAnyAddress(endpoint.Address) == false)
                    {
                        continue;
                    }
                    hashSet.Add(endpoint.Port);
                }
            }
            catch (Exception)
            {
            }
            return hashSet;
        }

        /// <summary>
        /// 是否为会覆盖本机回环的地址
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        private static bool IsLoopbackOrAnyAddress(IPAddress address)
        {
            return IPAddress.Any.Equals(address) ||
                IPAddress.IPv6Any.Equals(address) ||
                IPAddress.Loopback.Equals(address) ||
                IPAddress.IPv6Loopback.Equals(address);
        }

        /// <summary>
        /// 是可以监听TCP
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool CanListenTcp(int port)
        {
            return tcpListenPorts.Contains(port) == false;
        }

        /// <summary>
        /// 是可以监听UDP
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool CanListenUdp(int port)
        {
            return udpListenPorts.Contains(port) == false;
        }

        /// <summary>
        /// 是可以监听TCP和Udp
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool CanListen(int port)
        {
            return CanListenTcp(port) && CanListenUdp(port);
        }

        /// <summary>
        /// 获取可用的随机Tcp端口
        /// </summary>
        /// <param name="minPort"></param> 
        /// <returns></returns>
        public static int GetAvailableTcpPort(int minPort)
        {
            return GetAvailablePort(CanListenTcp, minPort);
        }

        /// <summary>
        /// 获取可用的随机Udp端口
        /// </summary>
        /// <param name="minPort"></param> 
        /// <returns></returns>
        public static int GetAvailableUdpPort(int minPort)
        {
            return GetAvailablePort(CanListenUdp, minPort);
        }

        /// <summary>
        /// 获取可用的随机端口
        /// </summary>
        /// <param name="minPort"></param> 
        /// <returns></returns>
        public static int GetAvailablePort(int minPort)
        {
            return GetAvailablePort(CanListen, minPort);
        }

        /// <summary>
        /// 获取可用端口
        /// </summary>
        /// <param name="canFunc"></param>
        /// <param name="minPort"></param>
        /// <returns></returns>
        /// <exception cref="FastGithubException"></exception>
        private static int GetAvailablePort(Func<int, bool> canFunc, int minPort)
        {
            for (var port = minPort; port < IPEndPoint.MaxPort; port++)
            {
                if (canFunc(port) == true)
                {
                    return port;
                }
            }
            throw new FastGithubException("当前无可用的端口");
        }
    }
}
