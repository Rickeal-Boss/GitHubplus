using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace FastGithub.Configuration
{
    /// <summary>
    /// FastGithub的配置
    /// </summary>
    public class FastGithubOptions
    {
        /// <summary>
        /// http代理端口
        /// </summary>
        public int HttpProxyPort { get; set; } = 38457;

        /// <summary>
        /// 回退的dns
        /// </summary>
        public IPEndPoint[] FallbackDns { get; set; } = Array.Empty<IPEndPoint>();

        /// <summary>
        /// 代理的域名配置
        /// </summary>
        // [PATCH] 防 trimming 静默丢值：PublishTrimmed 会裁掉「仅被反射访问」的成员，而 ConfigurationBinder
        // 是纯反射绑定（目标类型无法被静态分析推断）。这里显式声明：本属性的泛型实参 DomainConfig
        // 的所有成员都必须保留。与 Startup.ConfigureServices 的 [DynamicDependency]、以及
        // Directory.Build.props 的 TrimmerRootAssembly(FastGithub.Configuration) 互为多保险。
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
        public Dictionary<string, DomainConfig> DomainConfigs { get; set; } = new();
    }
}
