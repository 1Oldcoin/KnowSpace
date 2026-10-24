using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 把插件声明的 <c>agent-tool</c> 能力桥接为聊天模型可调用的工具。
/// 命名统一加 <c>plugin__{plugin}__{capability}</c> 前缀，避免与内置/MCP 工具重名。
///
/// <para>
/// 只有声明了 <c>kind = agent-tool</c> 的能力会暴露给模型；
/// <c>platform-connector</c> 只经由同步端点使用，模型不能直接触发平台抓取。
/// </para>
/// </summary>
public sealed class PluginToolSource(
    PluginManager manager,
    IOptions<PluginOptions> options,
    ILogger<PluginToolSource> logger) : IAgentToolSource
{
    public string Name => "plugins";

    public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.EnableToolBridge)
            return Task.FromResult<IReadOnlyList<AgentToolDefinition>>([]);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<AgentToolDefinition>();

        foreach (var host in manager.RunningHosts)
        {
            foreach (var capability in host.Capabilities)
            {
                // 模型无法提供确认信息：在强制确认策略下，需要确认的能力不桥接给模型。
                if (!PluginAccessPolicy.IsModelCallable(
                        capability.Kind,
                        capability.SideEffect,
                        settings.RequireConfirmationForSideEffects))
                    continue;

                var name = BuildToolName(host.Id, capability.Id, usedNames);
                var description = BuildDescription(host.Id, capability);

                definitions.Add(new AgentToolDefinition(
                    name,
                    description,
                    NormalizeSchema(capability.Parameters),
                    (arguments, token) => InvokeAsync(host.Id, capability.Id, arguments, token),
                    "plugin"));
            }
        }

        return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(definitions);
    }

    private async Task<string> InvokeAsync(
        string pluginId,
        string capabilityId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = ParseArguments(arguments);
            var result = await manager.InvokeAsync(
                pluginId,
                capabilityId,
                payload,
                PluginInvocationContext.FromAgent(),
                cancellationToken).ConfigureAwait(false);
            return Render(result.Result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Plugin tool {PluginId}/{CapabilityId} failed", pluginId, capabilityId);
            throw new InvalidOperationException($"插件工具 {pluginId}/{capabilityId} 调用失败：{exception.Message}", exception);
        }
    }

    private static JsonNode? ParseArguments(JsonElement arguments)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new JsonObject();

        return arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(arguments.GetRawText())
            : new JsonObject();
    }

    /// <summary>模型消费的是文本，这里把 JSON 结果压平，字符串节点直接取值。</summary>
    private static string Render(JsonNode? node)
    {
        if (node is null) return "（插件未返回内容）";
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string BuildDescription(string pluginId, PluginCapability capability)
    {
        var builder = new StringBuilder();
        builder.Append("[插件:").Append(pluginId).Append("] ");
        builder.Append(string.IsNullOrWhiteSpace(capability.Description) ? capability.Name : capability.Description);

        if (PluginSideEffectLevels.RequiresConfirmation(capability.SideEffect))
            builder.Append("（该操作会修改外部数据，需用户确认）");

        if (capability.Permissions.Count > 0)
            builder.Append(" 需要权限: ").Append(string.Join(',', capability.Permissions));

        return builder.ToString();
    }

    internal static string BuildToolName(string pluginId, string capabilityId, HashSet<string> usedNames)
    {
        var baseName = Truncate($"plugin__{Sanitize(pluginId)}__{Sanitize(capabilityId)}", 64);
        var candidate = baseName;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            var ending = $"_{suffix++}";
            candidate = Truncate(baseName, 64 - ending.Length) + ending;
        }

        return candidate;
    }

    /// <summary>OpenAI 工具名只允许字母、数字、下划线与短横线。</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character < 128 && (char.IsLetterOrDigit(character) || character is '_' or '-'))
                builder.Append(character);
            else
                builder.Append('_');
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "tool" : sanitized;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string NormalizeSchema(string? schema) =>
        string.IsNullOrWhiteSpace(schema)
            ? """{"type":"object","properties":{},"additionalProperties":false}"""
            : schema;
}
