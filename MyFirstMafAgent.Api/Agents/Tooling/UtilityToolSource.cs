using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 通用助手工具：抓取网页正文（fetch_url）、获取当前时间（current_time）、四则运算（calculate）。
/// 不依赖本机授权目录，任何会话都可以使用。
/// </summary>
public sealed class UtilityToolSource(
    IHttpClientFactory httpClientFactory,
    IOptions<WebSearchOptions> webSearchOptions,
    IOptions<AgentOptions> agentOptions) : IAgentToolSource
{
    public string Name => "utility";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>fetch_url 最多读取的响应字节数，防止把超大页面塞进上下文。</summary>
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>
    /// fetch_url 属于联网能力，必须与 web_search 归为同一分类，
    /// 否则用户关闭「允许联网搜索」后仍能通过它访问网络。
    /// </summary>
    private const string WebCategory = "web";

    public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(AgentToolContext context, CancellationToken cancellationToken)
    {
        var tools = new List<AgentToolDefinition>
        {
            Create("fetch_url",
                "打开一个网页或文本接口并返回正文（HTML 会自动抽取为纯文本）。用于阅读搜索结果或用户给出的链接。",
                """
                {"type":"object","properties":{"url":{"type":"string","description":"要抓取的 http/https 地址"}},"required":["url"]}
                """,
                FetchUrlAsync,
                WebCategory),

            Create("current_time",
                "获取当前时间（UTC、本地时间、时区与 Unix 时间戳）。涉及“今天”“最近”“多少天前”时先调用它。",
                """
                {"type":"object","properties":{},"required":[]}
                """,
                (arguments, token) => Task.FromResult(CurrentTime(arguments))),

            Create("calculate",
                "计算四则运算表达式，支持 + - * / % 与括号、小数、一元负号，例如 (1.5+2)*3-4/2。",
                """
                {"type":"object","properties":{"expression":{"type":"string","description":"数学表达式，例如 (1.5+2)*3-4/2"}},"required":["expression"]}
                """,
                (arguments, token) => Task.FromResult(Calculate(arguments)))
        };

        return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(tools);
    }

    private async Task<string> FetchUrlAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var raw = GetString(arguments, "url");
        if (string.IsNullOrWhiteSpace(raw))
            return Error("url 不能为空。", raw);

        if (!SafeUrlGuard.TryValidate(raw, out var uri, out var guardError) || uri is null)
            return Error(guardError ?? "url 不合法。", raw);

        // 域名解析后若指向本机/内网（内网 DNS 或 DNS rebinding），同样拒绝。
        if (await ResolvesToBlockedAddressAsync(uri, cancellationToken))
            return Error($"出于安全考虑，不允许访问本机或内网地址：{uri.Host}。", raw);

        var settings = webSearchOptions.Value;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 120));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var client = httpClientFactory.CreateClient(WebSearchToolSource.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(settings.UserAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent.Trim());
            request.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            var (body, truncated) = await ReadLimitedAsync(response, MaxResponseBytes, timeoutSource.Token);

            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isHtml = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                         || body.TrimStart().StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                         || body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase);

            if (isHtml)
            {
                // HTML 不把标签原样交给模型，统一走文档模块的正文抽取器。
                body = HtmlTextExtractor.ExtractText(body);
                contentType = string.IsNullOrEmpty(contentType) ? "text/html" : contentType;
                mediaType = "text/html";
            }

            var limit = Math.Clamp(agentOptions.Value.MaxToolResultChars, 1_000, 200_000);
            if (body.Length > limit)
            {
                body = body[..limit];
                truncated = true;
            }

            var payload = new
            {
                url = uri.ToString(),
                status = (int)response.StatusCode,
                contentType,
                truncated,
                content = body
            };
            return JsonSerializer.Serialize(payload, JsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error($"抓取超时（超过 {timeout.TotalSeconds:0} 秒）。", raw);
        }
        catch (HttpRequestException exception)
        {
            return Error($"抓取失败：{exception.Message}", raw);
        }
        catch (IOException exception)
        {
            return Error($"读取响应失败：{exception.Message}", raw);
        }
    }

    /// <summary>按上限读取响应体，超出的部分丢弃并标记为已截断。</summary>
    private static async Task<(string Body, bool Truncated)> ReadLimitedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        using var memory = new MemoryStream();
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            var remaining = maxBytes - (int)memory.Length;
            if (read >= remaining)
            {
                memory.Write(buffer, 0, remaining);
                truncated = true;
                break;
            }

            memory.Write(buffer, 0, read);
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = ResolveEncoding(charset);
        return (encoding.GetString(memory.ToArray()).TrimStart('\uFEFF'), truncated);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string CurrentTime(JsonElement arguments)
    {
        // 允许模型传入时区 ID（Windows 或 IANA），默认使用服务器本地时区。
        var requested = GetString(arguments, "timeZone");
        var zone = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(requested.Trim());
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return Error($"无法识别的时区：{requested}。", null);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var payload = new
        {
            utc = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            local = local.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            timeZone = zone.Id,
            localTimeZone = TimeZoneInfo.Local.Id,
            unixSeconds = now.ToUnixTimeSeconds(),
            dayOfWeek = local.DayOfWeek.ToString(),
            offsetMinutes = (int)local.Offset.TotalMinutes
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string Calculate(JsonElement arguments)
    {
        var expression = GetString(arguments, "expression");
        if (string.IsNullOrWhiteSpace(expression))
            return JsonSerializer.Serialize(new { error = "expression 不能为空。" }, JsonOptions);

        if (!CalculatorExpression.TryEvaluate(expression, out var value, out var error))
            return JsonSerializer.Serialize(new { expression, error }, JsonOptions);

        return JsonSerializer.Serialize(new
        {
            expression,
            result = value,
            formatted = CalculatorExpression.Format(value)
        }, JsonOptions);
    }

    /// <summary>域名解析后任一地址属于本机/内网即视为不安全；解析失败不拦截，交给 HTTP 客户端报错。</summary>
    private static async Task<bool> ResolvesToBlockedAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
            return SafeUrlGuard.IsBlockedHost(literal.ToString());

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Any(address => SafeUrlGuard.IsBlockedHost(address.ToString()));
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Error(string message, string? url, int? status = null) =>
        JsonSerializer.Serialize(new { url, status, error = message }, JsonOptions);

    private static AgentToolDefinition Create(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, Task<string>> handler,
        string category = "utility") =>
        new(name, description, schema, handler, category);

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
