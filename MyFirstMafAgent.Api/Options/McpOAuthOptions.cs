namespace MyFirstMafAgent.Api.Options;

/// <summary>
/// MCP 的 OAuth 2.1 设置（<c>Mcp:OAuth</c>）。
///
/// <para>
/// 分两层，界线必须清楚：
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>协议层</b>：OAuth 只回答「谁被授权」。<c>scope</c>、<c>aud</c>、PKCE 都在这一层。
///   </description></item>
///   <item><description>
///     <b>业务层</b>：授权同意时选中的知识库范围写进 <c>oauth_consents</c>，
///     每次 <c>tools/call</c> 仍由 <see cref="Hosting.AgentClientGate"/> 逐次判定。
///   </description></item>
/// </list>
///
/// <para>
/// 默认<b>关闭</b>：<c>maf_</c> 客户端密钥通道是既有能力，OAuth 是新增主路径，
/// 两者并存而不是替换。没配 <see cref="Issuer"/> 时也会自动关闭并打警告——
/// 签发出去的 token 必须能指向一个稳定的公网地址，否则 <c>aud</c> 形同虚设。
/// </para>
/// </summary>
public sealed class McpOAuthOptions
{
    public const string SectionName = "Mcp:OAuth";

    /// <summary>是否启用 OAuth（资源服务器 + 内置授权服务器）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 授权服务器签发者标识，例如 <c>https://knowspace.example.com</c>。
    ///
    /// <para>
    /// 留空时不启用 OAuth：<c>aud</c> 与 metadata 都需要绝对地址，
    /// 靠请求头猜出来的地址一旦被伪造（或代理没配好），受众绑定就失去意义。
    /// </para>
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// 资源标识（RFC 8707 的 <c>resource</c>，同时也是 token 的 <c>aud</c>）。
    /// 留空时取 <c>{Issuer}{Mcp:ServerPath}</c>。
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// 单用户 owner 模型的固定主体标识。token 的 <c>sub</c> 绑定到它。
    /// 多用户扩展时改成「登录用户 ID」即可，数据结构不用重建。
    /// </summary>
    public string OwnerSubject { get; set; } = "owner";

    /// <summary>同意页上显示的用户名。</summary>
    public string OwnerDisplayName { get; set; } = "本机用户";

    /// <summary>访问令牌有效期（分钟）。受众绑定的短令牌能显著降低误兑换的影响面。</summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 60;

    /// <summary>刷新令牌有效期（天）。轮换后旧的立即失效。</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    /// <summary>授权码有效期（秒）。OAuth 2.1 要求授权码短命且一次性。</summary>
    public int AuthorizationCodeLifetimeSeconds { get; set; } = 300;

    /// <summary>待确认授权请求的有效期（秒）。</summary>
    public int AuthorizationRequestLifetimeSeconds { get; set; } = 600;

    /// <summary>是否开放动态客户端注册（<c>/oauth/register</c>）。DCR 是兜底路径，不是默认路径。</summary>
    public bool AllowDynamicRegistration { get; set; } = true;

    /// <summary>注册客户端数量上限，防止匿名灌表。</summary>
    public int MaxRegisteredClients { get; set; } = 100;

    /// <summary>
    /// OAuth 通道每分钟请求上限（0 = 不限）。
    ///
    /// <para>
    /// 现有 <c>maf_</c> 客户端的额度是逐客户端配置的；OAuth 客户端没有那行记录，
    /// 因此用这一项兜底，避免同一个 consent 被拿来做压测。
    /// </para>
    /// </summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// 令牌签名私钥的存放路径。留空时落到 <c>{数据根}/oauth/signing-key.pem</c>。
    /// 密钥轮换 = 删掉这个文件并重启（所有已签发 token 立即失效）。
    /// </summary>
    public string SigningKeyPath { get; set; } = string.Empty;

    /// <summary>允许的时钟偏移（秒），用于 <c>exp</c> / <c>iat</c> 判定。</summary>
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>CIMD（客户端元数据文档）拉取设置。</summary>
    public CimdClientMetadataOptions Cimd { get; set; } = new();
}

/// <summary>
/// CIMD：客户端把 <c>client_id</c> 设成 HTTPS URL，由授权服务器主动拉取该 URL 下的 JSON。
///
/// <para>
/// 这是一次<b>服务端发起的出站请求</b>，因此每一条限制都是 SSRF 防线：
/// 只允许 HTTPS、拒绝重定向、拒绝私有/特殊地址段、限制响应体积与超时。
/// 客户端注册顺序是「预注册 → CIMD → DCR → 手动输入」，CIMD 是默认路径而不是可选项。
/// </para>
/// </summary>
public sealed class CimdClientMetadataOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>出站请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>响应体字节上限。元数据文档很小，超过就是异常输入。</summary>
    public int MaxResponseBytes { get; set; } = 32 * 1024;

    /// <summary>
    /// 是否允许解析到回环/私有地址。<b>只在本机联调时打开</b>：
    /// 一旦对公网开放，这条开关就是 SSRF 后门。
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }

    /// <summary>
    /// 是否允许 <c>http://</c>。默认拒绝；仅本机调试（如 <c>http://localhost:5199/client.json</c>）时打开。
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>拉取结果缓存时长（分钟），避免每次授权都打一次客户端站点。</summary>
    public int CacheMinutes { get; set; } = 60;
}
