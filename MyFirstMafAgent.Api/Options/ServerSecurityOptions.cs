namespace MyFirstMafAgent.Api.Options;

/// <summary>
/// 服务端（云主机 / 容器 / 内网）部署的安全设置。
///
/// <para>
/// 桌面模式靠 <c>DESKTOP_API_TOKEN</c> 守 <c>/api</c>，令牌每次启动重新生成、只在主进程里活着；
/// 但把同一份程序丢到云主机上时，没有任何人会给它发这个环境变量——
/// 于是 <c>/api</c> 会在公网上<b>完全不鉴权</b>。这个节就是为那种场景准备的。
/// </para>
///
/// <para>
/// 两条互不冲突的用法：
/// ① 直接配 <see cref="ApiToken"/>（或环境变量 <c>MAF_SERVER_TOKEN</c>），客户端自己带 <c>X-Api-Token</c>；
/// ② 反向代理做完自己的认证（Basic / OAuth / mTLS）后，把 <c>X-Api-Token</c> 注入给后端，
///    浏览器端就完全不用改代码。
/// </para>
/// </summary>
public sealed class ServerSecurityOptions
{
    public const string SectionName = "Server";

    /// <summary>服务端长期 API 令牌的环境变量名。配置文件的同名项优先级更低。</summary>
    public const string TokenEnvironmentVariable = "MAF_SERVER_TOKEN";

    /// <summary>
    /// 服务端 API 令牌。留空表示不启用（桌面模式、纯本机控制台调试）。
    /// 生产环境请用环境变量注入，不要写进 appsettings.json。
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// 是否放行未鉴权的 <c>/api/health</c>。
    /// 云上通常需要开着：负载均衡、容器 HEALTHCHECK、uptime 监控都拿不到令牌。
    /// 该端点只返回 <c>{"status":"ok","database":"connected"}</c>，不泄露业务数据。
    /// </summary>
    public bool AllowAnonymousHealth { get; set; }

    /// <summary>
    /// 是否信任 <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>。
    ///
    /// <para>
    /// 放在反向代理后面时<b>必须开启</b>，否则 <c>RemoteIpAddress</c> 永远是代理的地址：
    /// 审计日志里的来源 IP 全变成 127.0.0.1，而且 <c>/api/desktop/*</c> 的回环校验会把
    /// 所有经代理来的请求都当成「本机」，等于把优雅关机接口暴露给公网。
    /// </para>
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// 可信代理地址列表（IP）。留空表示只信任回环地址，适用于「反向代理与 API 同机」的常见部署。
    /// 代理在另一台机器时，务必把它填进来——否则伪造的 X-Forwarded-For 会被当真。
    /// </summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>
    /// 每个来源 IP 每分钟允许的鉴权失败次数。<c>0</c> 表示不限（不建议）。
    ///
    /// <para>
    /// 客户端密钥是 256 位随机值，猜中概率可以忽略；这个闸门防的是另外两件事：
    /// 失败请求打满日志/磁盘，以及无上限的重试流量。
    /// </para>
    /// </summary>
    public int FailedAuthLimitPerMinute { get; set; } = 30;

    /// <summary>失败次数超限后的封禁时长（秒）。</summary>
    public int FailedAuthBlockSeconds { get; set; } = 300;

    /// <summary>是否移除 Kestrel 的 <c>Server: Kestrel</c> 响应头（减少指纹信息）。</summary>
    public bool HideServerHeader { get; set; } = true;
}
