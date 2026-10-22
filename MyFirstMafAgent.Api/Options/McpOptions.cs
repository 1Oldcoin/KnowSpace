namespace MyFirstMafAgent.Api.Options;

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
