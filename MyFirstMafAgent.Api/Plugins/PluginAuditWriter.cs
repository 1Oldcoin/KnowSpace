using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 插件审计写入器。
///
/// <para>
/// 审计写入失败只记日志，<b>不</b>反过来阻断业务调用：
/// 审计的可用性低于业务可用性。但成功的调用必须尝试记录，
/// 因此写失败会在核心日志里留下对应插件与动作。
/// </para>
/// </summary>
public sealed class PluginAuditWriter(IPluginRegistry registry, ILogger<PluginAuditWriter> logger)
{
    /// <summary>detail 字段上限：审计表不存储无限增长的 JSON。</summary>
    public const int MaxDetailLength = 2000;

    private const int MaxStringLength = 300;

    /// <summary>审计里的中文保持可读（不转义成 \uXXXX），便于直接在界面与日志里查看。</summary>
    private static readonly JsonSerializerOptions SummarizeOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task RecordAsync(
        string pluginId,
        string action,
        string? capabilityId,
        string actor,
        string outcome,
        long durationMs,
        string? detail = null,
        JsonNode? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var composed = Compose(detail, arguments);

        try
        {
            await registry.RecordAuditAsync(
                new PluginAuditRecord(
                    0,
                    DateTimeOffset.UtcNow,
                    pluginId,
                    action,
                    capabilityId,
                    actor,
                    outcome,
                    durationMs,
                    composed),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to write plugin audit entry {Action} for {PluginId} ({Outcome})",
                action,
                pluginId,
                outcome);
        }
    }

    /// <summary>把说明与入参拼成一条可读描述，并对敏感字段脱敏、超长内容截断。</summary>
    public static string? Compose(string? detail, JsonNode? arguments)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(detail)) parts.Add(detail.Trim());

        var summary = Summarize(arguments);
        if (summary is not null) parts.Add("args=" + summary);

        if (parts.Count == 0) return null;

        var text = string.Join(" | ", parts);
        return text.Length <= MaxDetailLength
            ? text
            : text[..MaxDetailLength] + "…(truncated)";
    }

    /// <summary>脱敏后的入参摘要。键名含 key/token/secret/password 等一律替换为 ***。</summary>
    public static string? Summarize(JsonNode? arguments)
    {
        if (arguments is null) return null;

        try
        {
            var sanitized = Sanitize(arguments, depth: 0);
            var json = sanitized?.ToJsonString(SummarizeOptions);
            if (string.IsNullOrEmpty(json)) return null;

            return json.Length <= MaxStringLength ? json : json[..MaxStringLength] + "…";
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return "(unserializable)";
        }
    }

    private static JsonNode? Sanitize(JsonNode? node, int depth)
    {
        if (node is null || depth > 8) return null;

        switch (node)
        {
            case JsonObject source:
            {
                var result = new JsonObject();
                foreach (var property in source)
                {
                    result[property.Key] = IsSensitive(property.Key)
                        ? JsonValue.Create("***")
                        : Sanitize(property.Value, depth + 1);
                }

                return result;
            }

            case JsonArray source:
            {
                var result = new JsonArray();
                foreach (var item in source)
                    result.Add(Sanitize(item, depth + 1));

                return result;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(text.Length <= MaxStringLength ? text : text[..MaxStringLength] + "…");

            default:
                return node.DeepClone();
        }
    }

    private static bool IsSensitive(string key) =>
        key.Contains("key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || key.Contains("cookie", StringComparison.OrdinalIgnoreCase);
}

/// <summary>生命周期操作的结果。失败时状态与原因都返回给调用方，便于界面直接展示。</summary>
public sealed record PluginOperationResult(
    bool Success,
    string? PluginId,
    string? State,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public static PluginOperationResult Ok(string pluginId, string? state, string? message = null) =>
        new(true, pluginId, state, message, []);

    public static PluginOperationResult Fail(string pluginId, params string[] errors) =>
        new(false, pluginId, null, null, errors);
}
