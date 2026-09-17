using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tommy;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// doml配置工具
    /// </summary>
    static class TomlUtil
    {
        /// <summary>
        /// 设置监听地址
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="endpoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static Task SetListensAsync(string tomlPath, IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            var value = new TomlArray
            {
                endpoint.ToString()
            };
            return SetAsync(tomlPath, "listen_addresses", value, cancellationToken);
        }

        /// <summary>
        /// 设置日志等级
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="logLevel"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static Task SetLogLevelAsync(string tomlPath, int logLevel, CancellationToken cancellationToken)
        {
            return SetAsync(tomlPath, "log_level", new TomlInteger { Value = logLevel }, cancellationToken);
        }

        /// <summary>
        /// 设置负载均衡模式
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="value"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static Task SetLBStrategyAsync(string tomlPath, string value, CancellationToken cancellationToken)
        {
            return SetAsync(tomlPath, "lb_strategy", new TomlString { Value = value }, cancellationToken);
        }

        /// <summary>
        /// 设置TTL
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="minTTL"></param>
        /// <param name="maxTTL"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static Task SetMinMaxTTLAsync(string tomlPath, TimeSpan minTTL, TimeSpan maxTTL, CancellationToken cancellationToken)
        {
            var minValue = new TomlInteger { Value = (int)minTTL.TotalSeconds };
            var maxValue = new TomlInteger { Value = (int)maxTTL.TotalSeconds };

            // [PATCH] 合并为一轮「读-改-写」。上游连做 4 次 SetAsync，每次都要完整地
            // 读文件、解析、序列化、再写文件（dnscrypt-proxy.toml 有数百行），
            // 而这几项 TTL 本就在同一张表里，一轮即可全部写入，减少 I/O 与中间态窗口。
            return SetManyAsync(tomlPath, new (string Key, TomlNode Value)[]
            {
                ("cache_min_ttl", minValue),
                ("cache_neg_min_ttl", minValue),
                ("cache_max_ttl", maxValue),
                ("cache_neg_max_ttl", maxValue)
            }, cancellationToken);
        }

        /// <summary>
        /// 设置指定键的值
        /// </summary>
        /// <param name="tomlPath"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static Task SetAsync(string tomlPath, string key, TomlNode value, CancellationToken cancellationToken)
        {
            return SetManyAsync(tomlPath, new[] { (key, value) }, cancellationToken);
        }

        /// <summary>
        /// [PATCH] 一次性写入多个键，并保证写入的原子性。
        /// 上游直接 File.WriteAllTextAsync 覆盖原文件：若进程在写入过程中被终止
        /// （用户关加速、进程崩溃、系统关机），dnscrypt-proxy.toml 会残留半截内容，
        /// 下次启动 dnscrypt-proxy 会因配置损坏而直接失败，导致 DNS 防污染整体失效。
        /// 改为「先写同目录临时文件，再 File.Move(overwrite:true) 原子替换」：
        /// 同一分区内 Move 是原子操作，目标文件要么是旧内容、要么是完整的新内容，绝不会半截。
        /// 临时文件与目标同目录，保证落在同一分区，Move 才能是原子替换而非跨盘复制。
        /// </summary>
        /// <param name="tomlPath">toml 文件路径</param>
        /// <param name="items">要写入的键值对集合</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task SetManyAsync(string tomlPath, (string Key, TomlNode Value)[] items, CancellationToken cancellationToken)
        {
            var toml = await File.ReadAllTextAsync(tomlPath, cancellationToken);
            var reader = new StringReader(toml);
            var tomlTable = TOML.Parse(reader);
            foreach (var item in items)
            {
                tomlTable[item.Key] = item.Value;
            }

            var builder = new StringBuilder();
            var writer = new StringWriter(builder);
            tomlTable.WriteTo(writer);
            var newToml = builder.ToString();

            var tmpPath = tomlPath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, newToml, cancellationToken);
            File.Move(tmpPath, tomlPath, overwrite: true);
        }
    }
}
