using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 把联网搜索能力暴露给聊天模型：<c>web_search</c>。
/// 按 <see cref="WebSearchOptions.Provider"/> 分发到 DuckDuckGo / Bing / Tavily；
/// 任何网络异常、超时或非 2xx 响应都以 JSON 错误返回，不抛异常，让模型自己决定下一步。
/// </summary>
public sealed class WebSearchToolSource(
    IHttpClientFactory httpClientFactory,
    IOptions<WebSearchOptions> options) : IAgentToolSource
{
    /// <summary>命名 HttpClient 的名称（与 Program.cs 中的 AddHttpClient 保持一致）。</summary>
    public const string HttpClientName = "websearch";

    public string Name => "web";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(AgentToolContext context, CancellationToken cancellationToken)
    {
        var tools = new List<AgentToolDefinition>
        {
            Create("web_search",
                "联网搜索最新信息。用户问到新闻、时效性事实、你不确定或需要引用的内容时使用；返回标题、链接与摘要，必要时再用 fetch_url 打开链接看正文。",
                """
                {"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"},"maxResults":{"type":"integer","description":"返回条数，默认 5，最大 10"}},"required":["query"]}
                """,
                SearchAsync)
        };

        return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(tools);
    }

    private async Task<string> SearchAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query)) return Error("搜索关键词 query 不能为空。");

        var settings = options.Value;
        var provider = WebSearchOptions.NormalizeProvider(settings.Provider);
        var maxResults = Math.Clamp(GetInt(arguments, "maxResults") ?? settings.MaxResults, 1, 10);

        try
        {
            return provider switch
            {
                "DuckDuckGo" => await DuckDuckGoAsync(query.Trim(), maxResults, settings, cancellationToken),
                "Bing" => await BingAsync(query.Trim(), maxResults, settings, cancellationToken),
                "Tavily" => await TavilyAsync(query.Trim(), maxResults, settings, cancellationToken),
                _ => Error("未配置联网搜索服务商：请在 appsettings.json 的 WebSearch:Provider 中设置 DuckDuckGo、Bing 或 Tavily 后重启 API。"
                        + "目前无法联网搜索，你可以让用户提供链接或资料。", provider)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户主动取消（例如客户端断开）不应被当作搜索失败。
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error($"联网搜索超时（超过 {settings.TimeoutSeconds} 秒），请稍后重试或换一个关键词。", provider, query);
        }
        catch (HttpRequestException exception)
        {
            return Error($"联网搜索请求失败：{exception.Message}", provider, query);
        }
    }

    private async Task<string> DuckDuckGoAsync(string query, int maxResults, WebSearchOptions settings, CancellationToken cancellationToken)
    {
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        using var request = CreateRequest(HttpMethod.Get, url, settings);
        using var response = await SendAsync(request, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Error($"DuckDuckGo 返回 {(int)response.StatusCode} {response.ReasonPhrase}。可能是被限流，请稍后重试。", "DuckDuckGo", query, (int)response.StatusCode);

        var results = SearchResultParser.ParseDuckDuckGo(body, maxResults);
        return Success("DuckDuckGo", query, results);
    }

    private async Task<string> BingAsync(string query, int maxResults, WebSearchOptions settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return Error("未配置 Bing 搜索密钥：请在 appsettings.json 或环境变量 WebSearch:ApiKey 中填入 Bing Web Search 的订阅密钥后重启 API。", "Bing", query);

        var endpoint = string.IsNullOrWhiteSpace(settings.BingEndpoint)
            ? "https://api.bing.microsoft.com/v7.0/search"
            : settings.BingEndpoint.Trim();
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{endpoint}{separator}q={Uri.EscapeDataString(query)}&count={maxResults}&mkt=zh-CN&setLang=zh-Hans";

        using var request = CreateRequest(HttpMethod.Get, url, settings);
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", settings.ApiKey.Trim());
        using var response = await SendAsync(request, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode is 401 or 403 ? "（请检查 WebSearch:ApiKey 是否有效）" : string.Empty;
            return Error($"Bing 返回 {(int)response.StatusCode} {response.ReasonPhrase}{hint}。", "Bing", query, (int)response.StatusCode);
        }

        return Success("Bing", query, SearchResultParser.ParseBing(body, maxResults));
    }

    private async Task<string> TavilyAsync(string query, int maxResults, WebSearchOptions settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return Error("未配置 Tavily 搜索密钥：请在 appsettings.json 或环境变量 WebSearch:ApiKey 中填入 Tavily API Key 后重启 API。", "Tavily", query);

        var endpoint = string.IsNullOrWhiteSpace(settings.TavilyEndpoint)
            ? "https://api.tavily.com/search"
            : settings.TavilyEndpoint.Trim();

        using var request = CreateRequest(HttpMethod.Post, endpoint, settings);
        request.Content = JsonContent.Create(new
        {
            api_key = settings.ApiKey.Trim(),
            query,
            max_results = maxResults,
            search_depth = "basic"
        });

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode is 401 or 403 ? "（请检查 WebSearch:ApiKey 是否有效）" : string.Empty;
            return Error($"Tavily 返回 {(int)response.StatusCode} {response.ReasonPhrase}{hint}。", "Tavily", query, (int)response.StatusCode);
        }

        return Success("Tavily", query, SearchResultParser.ParseTavily(body, maxResults));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, WebSearchOptions settings)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(settings.UserAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent.Trim());
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string Success(string provider, string query, IReadOnlyList<WebSearchResult> results)
    {
        var payload = new
        {
            provider,
            query,
            count = results.Count,
            results = results.Select(item => new { title = item.Title, url = item.Url, snippet = item.Snippet }),
            message = results.Count == 0 ? "没有找到结果，可以换一个更具体的关键词。" : null
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string Error(string message, string? provider = null, string? query = null, int? status = null) =>
        JsonSerializer.Serialize(new { error = message, provider, query, status }, JsonOptions);

    private static AgentToolDefinition Create(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, Task<string>> handler) =>
        new(name, description, schema, handler, "web");

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}
