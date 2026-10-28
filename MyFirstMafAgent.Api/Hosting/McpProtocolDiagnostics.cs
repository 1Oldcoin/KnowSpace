using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>一次 <c>/mcp</c> 请求的协议层采样。</summary>
public sealed record McpProtocolSample(
    DateTimeOffset OccurredAt,
    string? RemoteAddress,
    string? UserAgent,
    string? FirstMethod,
    string? ProtocolVersion,
    string? Era,
    bool Authenticated,
    int StatusCode);

/// <summary><c>/mcp</c> 协议层诊断：回答「客户端到底用的是 Legacy 还是 Modern」。</summary>
public sealed class McpProtocolDiagnostics
{
    private const int MaxSamples = 64;

    private readonly object _gate = new();
    private readonly LinkedList<McpProtocolSample> _samples = new();
    private readonly Dictionary<string, int> _methods = new(StringComparer.Ordinal);

    /// <summary>
    /// SDK 支持的协议版本（阶段一验证的基线）。
    ///
    /// <para>
    /// SDK 的 <c>McpProtocolVersions</c> 是 internal，不能直接引用，
    /// 因此这里显式列出 SDK 2.2.0 支持的版本；升级 SDK 时同步这张表即可。
    /// 诊断页展示它，是为了让「客户端协商到哪个版本」有可对照的基准。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SupportedProtocolVersions { get; } =
    [
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        "2025-11-25",
        "2026-07-28"
    ];

    /// <summary>2026-07-28 起改为「每请求 <c>_meta</c> 携带版本」，不再有 initialize 握手。</summary>
    public const string ModernProtocolVersion = "2026-07-28";

    public void Record(McpProtocolSample sample)
    {
        lock (_gate)
        {
            _samples.AddFirst(sample);
            while (_samples.Count > MaxSamples) _samples.RemoveLast();

            var key = sample.FirstMethod ?? "(none)";
            _methods[key] = _methods.TryGetValue(key, out var count) ? count + 1 : 1;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                supportedProtocolVersions = SupportedProtocolVersions,
                modernProtocolVersion = ModernProtocolVersion,
                initializeHandshakeSupported = true,
                perRequestMetadataSupported = true,
                serverDiscoverSupported = true,
                methodCounts = _methods
                    .OrderByDescending(pair => pair.Value)
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                recent = _samples.ToArray()
            };
        }
    }
}

/// <summary>一次 <c>tools/call</c> 的目标与判定所需参数。</summary>
public sealed record McpCallInfo(string? Method, string? Tool, Guid? KnowledgeBaseId, Guid? DocumentId, JsonNode? Id);

/// <summary>请求体的解析结论。除 <see cref="Json"/> 外都<b>不放行</b>（fail-closed）。</summary>
public enum McpBodyStatus
{
    /// <summary>没有请求体（GET / DELETE / OPTIONS，或空体）。</summary>
    None,

    /// <summary>合法的单条 JSON-RPC 消息。</summary>
    Json,

    /// <summary>Content-Type 不是 JSON。</summary>
    NotJson,

    /// <summary>JSON 解析失败，或含重复属性名。</summary>
    Malformed,

    /// <summary>超过网关接受的上限。</summary>
    TooLarge,

    /// <summary>读取失败（连接断开等）。</summary>
    Unreadable
}

/// <summary>
/// 请求体的解析结果。<b>认证与工具准入都以它为唯一依据</b>，
/// 避免"中间件解析出一份参数、SDK 绑定出另一份"的双解析器差异。
/// </summary>
public sealed record McpRequestPeek(
    McpBodyStatus Status,
    IReadOnlyList<McpCallInfo> Calls,
    bool IsBatch = false,
    string? Method = null,
    string? ProtocolVersion = null,
    string? Era = null,
    string? Error = null)
{
    public static McpRequestPeek Empty { get; } = new(McpBodyStatus.None, []);

    /// <summary>是否为"单条、且 method 恰好是 server/discover"的探测请求。</summary>
    public bool IsSingleServerDiscover =>
        Status == McpBodyStatus.Json
        && !IsBatch
        && Calls.Count == 1
        && string.Equals(Method, RequestMethods.ServerDiscover, StringComparison.Ordinal);
}

/// <summary>
/// 在鉴权之前解析请求体：拿到 method、协议版本、所有 <c>tools/call</c> 的目标与关键参数。
///
/// <para>
/// 与旧实现的区别：旧版在解析失败/非 JSON/超大时返回「空结果」并被当成"无需判定"，
/// 于是网关整体放行。现在这些情况都会得到明确的 <see cref="McpBodyStatus"/>，由网关拒绝。
/// 同时旧版只取批量请求的第一条做判定，现在<b>批量请求一律拒绝</b>。
/// </para>
///
/// <para>
/// 重复属性名会被判为 <see cref="McpBodyStatus.Malformed"/>：<c>{"knowledgeBaseId":"A","knowledgeBaseId":"B"}</c>
/// 在网关与 SDK 两套解析器下可能得到不同答案，这种请求不该被接受。
/// </para>
/// </summary>
public static class McpRequestPeekReader
{
    /// <summary>兼容旧引用：允许读取的最大请求体字节数由调用方（网关）按配置传入。</summary>
    public const int MaxPeekBytes = 8 * 1024 * 1024;

    public static async Task<McpRequestPeek> TryPeekAsync(
        HttpContext context,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) return McpRequestPeek.Empty;

        var contentType = context.Request.ContentType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return new McpRequestPeek(McpBodyStatus.NotJson, [], Error: $"Content-Type 不是 JSON：'{contentType}'");
        }

        if (maxBytes <= 0) maxBytes = MaxPeekBytes;

        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            return new McpRequestPeek(McpBodyStatus.TooLarge, [],
                Error: $"请求体 {declared} 字节超过上限 {maxBytes} 字节");
        }

        context.Request.EnableBuffering();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxBytes, 64 * 1024));
        using var body = new MemoryStream();
        try
        {
            while (body.Length <= maxBytes)
            {
                var read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                body.Write(buffer, 0, read);
            }
        }
        catch (Exception exception) when (exception is IOException or BadHttpRequestException)
        {
            return new McpRequestPeek(McpBodyStatus.Unreadable, [], Error: exception.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            // 一定要回卷，否则 MCP 处理器读到的是空体。
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
        }

        if (body.Length == 0) return McpRequestPeek.Empty;
        if (body.Length > maxBytes)
        {
            return new McpRequestPeek(McpBodyStatus.TooLarge, [],
                Error: $"请求体超过上限 {maxBytes} 字节");
        }

        var json = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var isBatch = root.ValueKind == JsonValueKind.Array;
            var messages = isBatch
                ? root.EnumerateArray().ToList()
                : [root];

            if (messages.Any(message => message.ValueKind != JsonValueKind.Object))
            {
                return new McpRequestPeek(McpBodyStatus.Malformed, [], Error: "JSON-RPC 消息必须是对象");
            }

            var duplicate = messages
                .Select(FindDuplicateProperty)
                .FirstOrDefault(found => found is not null);
            if (duplicate is not null)
            {
                return new McpRequestPeek(McpBodyStatus.Malformed, [], isBatch,
                    Error: $"JSON 含重复属性名 '{duplicate}'，拒绝处理");
            }

            var calls = messages.Select(ParseCall).ToList();
            var first = calls.Count > 0 ? calls[0] : null;

            return new McpRequestPeek(
                McpBodyStatus.Json,
                calls,
                isBatch,
                first?.Method,
                ReadProtocolVersion(messages[0]),
                DescribeEra(first?.Method, ReadProtocolVersion(messages[0])));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return new McpRequestPeek(McpBodyStatus.Malformed, [], Error: exception.Message);
        }

        static McpCallInfo ParseCall(JsonElement message)
        {
            var method = message.TryGetProperty("method", out var methodElement)
                && methodElement.ValueKind == JsonValueKind.String
                    ? methodElement.GetString()
                    : null;

            JsonNode? id = message.TryGetProperty("id", out var idElement)
                ? JsonNode.Parse(idElement.GetRawText())
                : null;

            if (!string.Equals(method, "tools/call", StringComparison.Ordinal))
                return new McpCallInfo(method, null, null, null, id);

            if (!message.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object)
                return new McpCallInfo(method, null, null, null, id);

            var tool = parameters.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;

            Guid? knowledgeBaseId = null;
            Guid? documentId = null;
            if (parameters.TryGetProperty("arguments", out var arguments)
                && arguments.ValueKind == JsonValueKind.Object)
            {
                knowledgeBaseId = ReadGuid(arguments, "knowledgeBaseId");
                documentId = ReadGuid(arguments, "documentId");
            }

            return new McpCallInfo(method, tool, knowledgeBaseId, documentId, id);
        }

        static Guid? ReadGuid(JsonElement arguments, string name)
        {
            if (!arguments.TryGetProperty(name, out var element)) return null;
            if (element.ValueKind != JsonValueKind.String) return null;
            return Guid.TryParse(element.GetString(), out var id) && id != Guid.Empty ? id : null;
        }

        static string? ReadProtocolVersion(JsonElement message)
        {
            if (!message.TryGetProperty("params", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object)
                return null;

            if (parameters.TryGetProperty("_meta", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty(MetaKeys.ProtocolVersion, out var fromMeta)
                && fromMeta.ValueKind == JsonValueKind.String)
                return fromMeta.GetString();

            if (parameters.TryGetProperty("protocolVersion", out var direct)
                && direct.ValueKind == JsonValueKind.String)
                return direct.GetString();

            return null;
        }
    }

    /// <summary>
    /// 递归找同名（含仅大小写不同）属性。返回第一个冲突名，没有则 <c>null</c>。
    /// </summary>
    internal static string? FindDuplicateProperty(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) return property.Name;
                    if (FindDuplicateProperty(property.Value) is { } nested) return nested;
                }

                return null;
            }

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindDuplicateProperty(item) is { } nested) return nested;
                }

                return null;

            default:
                return null;
        }
    }

    private static string? DescribeEra(string? method, string? protocolVersion)
    {
        if (string.Equals(method, RequestMethods.ServerDiscover, StringComparison.Ordinal)) return "modern";
        if (string.Equals(method, "initialize", StringComparison.Ordinal)) return "legacy";

        if (!string.IsNullOrWhiteSpace(protocolVersion))
        {
            // ISO 日期字符串可直接按序比较。
            return string.CompareOrdinal(protocolVersion, McpProtocolDiagnostics.ModernProtocolVersion) >= 0
                ? "modern"
                : "legacy";
        }

        return null;
    }
}
