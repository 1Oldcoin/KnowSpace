namespace MyFirstMafAgent.Api.Options;

/// <summary>
/// MCP 配置：既包含"把本机能力暴露出去"的服务端设置，也包含"调用外部 MCP 服务器"的客户端设置。
///
/// <para>
/// 服务端默认<b>要求客户端密钥</b>：监听回环地址并不等于鉴权，同机的其它进程同样能访问；
/// 把本项目当作"核心存储层"之后，未鉴权的读取面等于把整个知识库交出去。
/// </para>
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>是否把本机知识库暴露为 MCP 服务端（HTTP 端点 /mcp）。</summary>
    public bool EnableServer { get; set; } = true;

    /// <summary>MCP 服务端路径，默认 /mcp。</summary>
    public string ServerPath { get; set; } = "/mcp";

    /// <summary>是否允许聊天模型调用外部 MCP 服务器的工具。</summary>
    public bool EnableClient { get; set; } = true;

    /// <summary>外部 MCP 服务器列表。</summary>
    public List<McpRemoteServerOptions> Servers { get; set; } = [];

    /// <summary>是否要求 <c>/mcp</c> 携带客户端密钥。关闭仅供本机调试。</summary>
    public bool RequireClientKey { get; set; } = true;

    /// <summary>新客户端默认的每分钟请求上限（0 = 不限制）。</summary>
    public int DefaultRequestsPerMinute { get; set; } = 120;

    /// <summary>客户端数量上限，防止无限注册。</summary>
    public int MaxAgentClients { get; set; } = 50;

    /// <summary>是否记录每次 MCP 调用的审计。</summary>
    public bool EnableCallAudit { get; set; } = true;

    /// <summary>网关允许读取的最大请求体字节数（需要读取工具参数才能做权限判定）。</summary>
    public int MaxRequestBodyBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// 允许调用 <c>/mcp</c> 的浏览器 Origin（如 <c>http://localhost:6274</c> 的 MCP Inspector）。
    /// 带 Origin 头但不在此列表的请求一律 403；OAuth Issuer 与 Server:PublicBaseUrl 的源自动加入。
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// 允许的 Host 头（不含端口）。回环名、OAuth Issuer 与 Server:PublicBaseUrl 的主机自动加入。
    /// 防 DNS rebinding：攻击页面把自己的域名解析到 127.0.0.1 后，Host 仍是攻击者域名。
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];
}

public sealed class McpRemoteServerOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>传输方式：http（Streamable HTTP）或 stdio。</summary>
    public string Transport { get; set; } = "http";

    /// <summary>http 传输的端点，例如 http://localhost:5056/mcp。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>http 传输的附加请求头，例如鉴权信息。</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>stdio 传输的可执行文件与参数。</summary>
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>连接与调用超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 60;
}
