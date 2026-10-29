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

    /// <summary>
    /// 是否允许局域网发现（mDNS/DNS-SD 广播 <c>_mcp._tcp</c>）。
    ///
    /// <para>
    /// <b>默认关闭</b>，而且必须保持关闭：mDNS 是「发现」协议，不是「授权」协议——
    /// 它不能证明客户端可信，也不能替代 <c>maf_</c> 密钥或 OAuth。
    /// 打开它等于告诉整个局域网「这里有个知识库端点」。
    /// 只有用户显式开启「局域网共享」时才应启用，并确保认证配置已经就位。
    /// </para>
    /// <para>
    /// <b>本版本尚未实现真正的广播器</b>：置为 <c>true</c> 只会在启动时打一条
    /// <c>LogWarning</c> 说明「不生效」（见 <c>ServerSecurity.WarnUnimplementedMdns</c>），
    /// 除此之外没有任何行为。保留这个键是为了让「局域网共享」这个产品功能有落点，
    /// 不是因为现在能用。客户端配置请走 <c>/api/mcp/local-config</c>。
    /// </para>
    /// </summary>
    public bool EnableMdns { get; set; }

    /// <summary>mDNS 上展示的服务名（仅在 <see cref="EnableMdns"/> 为 true 且广播器已实现时才有意义；当前仅用于告警文案）。</summary>
    public string MdnsServiceName { get; set; } = "知间知识库";

    /// <summary>
    /// 对外可达的基地址，例如 <c>https://knowspace.example.com</c>。留空表示不声明。
    ///
    /// <para>
    /// 只用于<b>生成给人看的配置片段</b>（<c>/api/mcp/local-config</c>、Server Card）。
    /// 它<b>不</b>参与任何鉴权判定，也<b>不</b>影响令牌的 <c>iss</c>/<c>aud</c>——
    /// 后者只认 <c>Mcp:OAuth:Issuer</c>，且绝不从请求头推导（防 Host 伪造）。
    /// </para>
    ///
    /// <para>
    /// 存在的理由：服务端部署时 Kestrel 监听的是 <c>127.0.0.1:5056</c>，
    /// 若没开 OAuth 又没有本项，「一键复制」会生成 <c>http://127.0.0.1:5056/mcp</c>，
    /// 那个地址在用户本机上是连不上云端的。填上公网地址即可修正。
    /// </para>
    ///
    /// <para>解析优先级：<c>Mcp:OAuth:Issuer</c> &gt; 本项 &gt; Kestrel 监听地址 &gt; 请求的 scheme/host。</para>
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 桌面版固定回环端口。默认 <c>5056</c>（本产品的「专属端口」），设为 <c>0</c> 则每次启动
    /// 由操作系统分配随机端口。
    ///
    /// <para>
    /// 为什么默认固定：端口随机变化会让用户手写的 MCP 配置、书签、脚本里的地址失效。
    /// 固定端口便于「一次配置长期可用」；<b>只绑回环地址</b>这一安全前提不变，
    /// 代价仅是降低了别的本机进程扫到端口的成本。
    /// </para>
    ///
    /// <para>
    /// 端口被占用时不会导致启动失败：Electron 主进程会先探测，
    /// 占用了就自动回退到随机端口（见 <c>desktop-electron/main.js</c> 的 startApi）。
    /// 想恢复「每次随机」只需把本项改成 <c>0</c>，或设环境变量
    /// <c>MAF_FIXED_LOCAL_PORT=0</c>。
    /// </para>
    ///
    /// <para>
    /// <b>仅桌面版（Electron）读取本项</b>；Linux / Docker / systemd 的监听地址由
    /// <c>--urls</c> 决定，与本项无关。
    /// </para>
    /// </summary>
    public int FixedLocalPort { get; set; } = 5056;
}
