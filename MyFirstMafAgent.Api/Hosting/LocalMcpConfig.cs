using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 本地 MCP 配置的一键复制内容（Cursor / Claude Desktop）。
///
/// <para>
/// 这是<b>确定可用的保底入口</b>：不依赖 OAuth、不依赖 Server Card、不依赖 mDNS。
/// 桌面版把实际随机端口读出来填进去，用户复制粘贴就能连——这也是 OAuth 出问题时的退路。
/// </para>
/// </summary>
public static class LocalMcpConfig
{
    public const string ServerKey = "knowspace";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Cursor 的 <c>mcp.json</c> 片段：直接给 url + Authorization 头。</summary>
    public static string BuildCursorConfig(string baseUrl, string mcpPath, string? clientKey)
    {
        var headers = new JsonObject();
        if (!string.IsNullOrWhiteSpace(clientKey))
            headers["Authorization"] = "Bearer " + clientKey;

        var server = new JsonObject
        {
            ["url"] = BuildEndpoint(baseUrl, mcpPath)
        };

        if (headers.Count > 0) server["headers"] = headers;

        return Wrap(server);
    }

    /// <summary>
    /// Claude Desktop 的 <c>claude_desktop_config.json</c> 片段。
    /// Claude Desktop 走 stdio，因此用 <c>mcp-remote</c> 桥到 HTTP。
    /// </summary>
    public static string BuildClaudeDesktopConfig(string baseUrl, string mcpPath, string? clientKey)
    {
        var args = new JsonArray("-y", "mcp-remote", BuildEndpoint(baseUrl, mcpPath));

        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            args.Add("--header");
            args.Add("Authorization: Bearer " + clientKey);
        }

        var server = new JsonObject
        {
            ["command"] = "npx",
            ["args"] = args
        };

        return Wrap(server);
    }

    /// <summary>终端直接验证用的 curl 片段。</summary>
    public static string BuildCurlSnippet(string baseUrl, string mcpPath, string? clientKey)
    {
        var endpoint = BuildEndpoint(baseUrl, mcpPath);
        var auth = string.IsNullOrWhiteSpace(clientKey) ? string.Empty : $" \\\n  -H \"Authorization: Bearer {clientKey}\"";

        return "curl.exe -s -X POST " + endpoint + auth + " \\\n"
            + "  -H \"Content-Type: application/json\" -H \"Accept: application/json, text/event-stream\" \\\n"
            + "  -d \"{\\\"jsonrpc\\\":\\\"2.0\\\",\\\"id\\\":1,\\\"method\\\":\\\"tools/list\\\"}\"";
    }

    public static string BuildEndpoint(string baseUrl, string mcpPath)
    {
        var root = (baseUrl ?? string.Empty).TrimEnd('/');
        var path = string.IsNullOrWhiteSpace(mcpPath) ? "/mcp" : (mcpPath.StartsWith('/') ? mcpPath : "/" + mcpPath);
        return root + path;
    }

    private static string Wrap(JsonObject server)
    {
        var payload = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [ServerKey] = server }
        };

        return payload.ToJsonString(Options);
    }
}
