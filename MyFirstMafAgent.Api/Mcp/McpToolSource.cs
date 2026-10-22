using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Mcp;

/// <summary>
/// 把外部 MCP 服务器的工具桥接为聊天模型可调用的本地工具。
/// 工具名统一加 mcp__{server}__ 前缀，避免与内置工具重名。
/// </summary>
public sealed class McpToolSource(
    McpClientRegistry registry,
    IOptions<AgentOptions> agentOptions,
    ILogger<McpToolSource> logger) : IAgentToolSource
{
    public string Name => "mcp";

    public async Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        if (!agentOptions.Value.EnableMcpToolBridge || !registry.HasServers) return [];

        var (_, remoteTools) = await registry.GetToolsAsync(cancellationToken);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<AgentToolDefinition>(remoteTools.Count);

        foreach (var remote in remoteTools)
        {
            var name = BuildToolName(remote.Server, remote.Name, usedNames);
            definitions.Add(new AgentToolDefinition(
                name,
                $"[MCP:{remote.Server}] {remote.Description}",
                NormalizeSchema(remote.ParametersJsonSchema),
                async (arguments, token) =>
                {
                    try
                    {
                        var parameters = ToParameterDictionary(arguments);
                        var result = await remote.Tool.CallAsync(parameters, cancellationToken: token);
                        var text = Flatten(result);
                        return result.IsError == true ? $"[MCP 工具错误] {text}" : text;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogError(exception, "MCP tool {Server}/{Tool} failed", remote.Server, remote.Name);
                        throw new InvalidOperationException($"MCP 工具 {remote.Server}/{remote.Name} 调用失败：{exception.Message}", exception);
                    }
                },
                "mcp"));
        }

        return definitions;
    }

    private static string BuildToolName(string server, string tool, HashSet<string> usedNames)
    {
        var baseName = Truncate($"mcp__{Sanitize(server)}__{Sanitize(tool)}", 64);
        var candidate = baseName;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            var ending = $"_{suffix++}";
            candidate = Truncate(baseName, 64 - ending.Length) + ending;
        }
        return candidate;
    }

    /// <summary>OpenAI 工具名只允许字母、数字、下划线和短横线。</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) && character < 128 ? character : '_');
        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "tool" : sanitized;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string NormalizeSchema(string schema) =>
        string.IsNullOrWhiteSpace(schema) ? """{"type":"object","properties":{}}""" : schema;

    private static IReadOnlyDictionary<string, object?> ToParameterDictionary(JsonElement arguments)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind != JsonValueKind.Object) return parameters;

        foreach (var property in arguments.EnumerateObject())
            parameters[property.Name] = property.Value.Clone();
        return parameters;
    }

    /// <summary>把 MCP 的多种内容块合并为纯文本，便于聊天模型消费。</summary>
    private static string Flatten(CallToolResult result)
    {
        var parts = new List<string>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    parts.Add(text.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    parts.Add(resource.Text);
                    break;
                case ImageContentBlock:
                    parts.Add("[图片内容已省略]");
                    break;
            }
        }

        if (result.StructuredContent is { } structured)
            parts.Add(structured.GetRawText());

        return parts.Count == 0 ? "[MCP 工具没有返回文本内容]" : string.Join("\n", parts);
    }
}
