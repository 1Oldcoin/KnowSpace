using System.Buffers;
using System.Text;
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

/// <summary>请求体的轻量探测结果（只为诊断，不影响业务判定）。</summary>
public sealed record McpRequestPeek(string? Method, string? ProtocolVersion, string? Era)
{
    public static McpRequestPeek Empty { get; } = new(null, null, null);
}

/// <summary>
/// 在鉴权之前偷看请求体里的 <c>method</c> 与协议版本。
///
/// <para>
/// 限制是刻意的：只处理 JSON 且体积 <b>≤ 64 KiB</b> 的请求（<c>initialize</c> /
/// <c>server/discover</c> 都很小），并且读完后把流回卷，后续处理器照常读取。
/// 这样既不改变既有行为，也能把「客户端第一个请求是什么」记录下来。
/// </para>
/// </summary>
public static class McpRequestPeekReader
{
    public const int MaxPeekBytes = 64 * 1024;

    public static async Task<McpRequestPeek> TryPeekAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) return McpRequestPeek.Empty;

        var contentType = context.Request.ContentType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return McpRequestPeek.Empty;

        if (context.Request.ContentLength is { } declared && declared > MaxPeekBytes) return McpRequestPeek.Empty;

        context.Request.EnableBuffering();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxPeekBytes);
        int total = 0;
        try
        {
            while (total < MaxPeekBytes)
            {
                var read = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(total, MaxPeekBytes - total), cancellationToken);
                if (read == 0) break;
                total += read;
            }
        }
        catch (Exception exception) when (exception is IOException or BadHttpRequestException)
        {
            return McpRequestPeek.Empty;
        }
        finally
        {
            // 一定要回卷，否则 MCP 处理器读到的是空体。
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
        }

        try
        {
            if (total == 0) return McpRequestPeek.Empty;

            var json = Encoding.UTF8.GetString(buffer, 0, total);
            var node = JsonNode.Parse(json);
            var message = node is JsonArray batch ? batch.FirstOrDefault() as JsonObject : node as JsonObject;
            if (message is null) return McpRequestPeek.Empty;

            var method = message["method"]?.GetValue<string>();
            var parameters = message["params"] as JsonObject;

            var protocolVersion =
                parameters?["_meta"]?[MetaKeys.ProtocolVersion]?.GetValue<string>()
                ?? parameters?["protocolVersion"]?.GetValue<string>();

            return new McpRequestPeek(method, protocolVersion, DescribeEra(method, protocolVersion));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return McpRequestPeek.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
