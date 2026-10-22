using System.Text.Json;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 从联网类工具的 JSON 返回值里提取来源链接，供聊天响应回显给用户。
/// <list type="bullet">
/// <item><c>web_search</c> 返回 <c>{ provider, query, count, results: [{ title, url, snippet }] }</c>；</item>
/// <item><c>fetch_url</c> 返回 <c>{ url, status, contentType, truncated, content }</c>。</item>
/// </list>
/// 两者都只在成功时带顶层 <c>url</c>/<c>results</c>，失败时返回 <c>{ url, status, error }</c>（url 为用户输入或为 null），
/// 因此这里要求顶层 <c>url</c> 必须同时满足 <c>status</c> 小于 400 才采纳，避免把失败地址当成来源。
/// 纯函数，不发网络请求，便于单元测试。
/// </summary>
public static class ToolSourceExtractor
{
    /// <summary>不会产生外部链接的工具（时间、计算、检索等），直接跳过以省掉 JSON 解析开销。</summary>
    private static readonly HashSet<string> WebToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search",
        "fetch_url"
    };

    /// <summary>单轮对话最多回显多少条来源链接，防止上下文被搜索结果淹没。</summary>
    public const int MaxSources = 20;

    /// <summary>是否为可能返回外部链接的联网工具。</summary>
    public static bool IsWebTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && WebToolNames.Contains(toolName);

    /// <summary>
    /// 解析一次工具调用的返回值，把其中的链接追加到 <paramref name="sources"/>。
    /// 解析失败、结果为空或格式不符时静默跳过，不影响聊天主流程。
    /// </summary>
    public static void Collect(string? toolName, string? result, ICollection<string> sources)
    {
        if (sources is null || string.IsNullOrWhiteSpace(result)) return;
        // UrlSources 只处理这两个工具；其它工具名进来会被直接忽略。
        var items = Parse(toolName, result);
        foreach (var url in items)
        {
            if (sources.Count >= MaxSources) return;
            sources.Add(url);
        }
    }

    /// <summary>
    /// 从模型的工具入参里补一条来源：<c>fetch_url</c> 的 <c>{"url":"https://..."}</c> 在正文抓取成功时，
    /// 返回值本身已经带 url；这里覆盖的是“结果里没有链接可提取”的情况，保证用户至少知道模型看过哪个地址。
    /// <c>web_search</c> 的入参只有关键词，不会被误当作链接。
    /// </summary>
    public static void CollectArguments(string? argumentsJson, ICollection<string> sources)
    {
        if (sources is null || sources.Count >= MaxSources || string.IsNullOrWhiteSpace(argumentsJson)) return;
        if (!argumentsJson.TrimStart().StartsWith('{')) return;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!TryGetProperty(document.RootElement, "url", out var value) || value.ValueKind != JsonValueKind.String) return;
            if (Normalize(value.GetString()) is { } url) AddToCollection(sources, url);
        }
        catch (JsonException)
        {
            // 入参不是合法 JSON 时静默跳过。
        }
    }

    /// <summary>解析工具返回值里的链接列表（保持返回顺序，已去重）。</summary>
    public static IReadOnlyList<string> Parse(string? toolName, string? result)
    {
        var urls = new List<string>();
        if (string.IsNullOrWhiteSpace(result) || !IsWebTool(toolName)) return urls;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result);
        }
        catch (JsonException)
        {
            // 工具已把内容截断或以纯文本返回时不是合法 JSON，视为没有可提取的链接。
            return urls;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return urls;

            // 失败响应：{ error: "...", url: null } 不应被当作来源。
            if (TryGetProperty(root, "error", out var error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString()))
                return urls;

            if (TryGetProperty(root, "results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (TryGetProperty(item, "url", out var value)
                        && value.ValueKind == JsonValueKind.String
                        && Normalize(value.GetString()) is { } url)
                        Add(urls, url);
                }
                return urls;
            }

            // fetch_url：顶层 url 仅在抓取成功（status < 400）时才算来源。
            if (TryGetProperty(root, "url", out var topUrl) && topUrl.ValueKind == JsonValueKind.String)
            {
                var succeeded = !TryGetProperty(root, "status", out var status)
                                || status.ValueKind != JsonValueKind.Number
                                || status.GetInt32() < 400;
                if (succeeded && Normalize(topUrl.GetString()) is { } url) Add(urls, url);
            }
        }

        return urls;
    }

    /// <summary>只保留 http/https 绝对地址；DuckDuckGo 跳转链接等再做一次规范化。</summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        if (TryGetLinkProperty(value) is { } nested) value = nested;

        var normalized = SearchResultParser.NormalizeUrl(value);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : null;
    }

    /// <summary>容错：个别 provider 会把 url 再包一层对象，例如 <c>{"url":{"value":"https://..."}}</c>。</summary>
    private static string? TryGetLinkProperty(string raw)
    {
        if (!raw.StartsWith('{')) return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (TryGetProperty(document.RootElement, "url", out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        catch (JsonException)
        {
            // 不是 JSON 就按普通字符串处理。
        }

        return null;
    }

    private static void Add(List<string> urls, string url) => AddToCollection(urls, url);

    private static void AddToCollection(ICollection<string> urls, string url)
    {
        if (urls.Count >= MaxSources) return;
        if (urls.Contains(url, StringComparer.OrdinalIgnoreCase)) return;
        urls.Add(url);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(name, out value)) return true;

        // 兼容不同大小写键名。
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}
