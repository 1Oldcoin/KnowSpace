using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>一条联网搜索结果。</summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>
/// 把各家搜索引擎的响应解析成 <see cref="WebSearchResult"/> 列表。
/// 全部是纯函数（仅正则 / JSON 解析，不发网络请求），便于单元测试。
/// </summary>
public static partial class SearchResultParser
{
    private const int MaxTitleLength = 200;
    private const int MaxSnippetLength = 400;

    [GeneratedRegex(@"<(?:a|div)[^>]*class=""[^""]*\bresult__a\b[^""]*""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DuckAnchorTagRegex();

    [GeneratedRegex(@"<(?:a|div)[^>]*class=""[^""]*\bresult__snippet\b[^""]*""[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DuckSnippetTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>解析 DuckDuckGo HTML 版（html.duckduckgo.com/html/）的结果页。</summary>
    public static IReadOnlyList<WebSearchResult> ParseDuckDuckGo(string? html, int maxResults = 5)
    {
        var results = new List<WebSearchResult>();
        if (string.IsNullOrWhiteSpace(html) || maxResults <= 0) return results;

        var anchors = DuckAnchorTagRegex().Matches(html);
        var snippets = DuckSnippetTagRegex().Matches(html);

        for (var index = 0; index < anchors.Count; index++)
        {
            // 每块范围：从当前标题标签到下一个标题标签（含摘要）。
            var start = anchors[index].Index + anchors[index].Length;
            var end = index + 1 < anchors.Count ? anchors[index + 1].Index : Math.Min(html.Length, start + 8_000);
            var anchorEnd = html.IndexOf("</a>", start, StringComparison.OrdinalIgnoreCase);
            if (anchorEnd < 0 || anchorEnd > end) anchorEnd = end;
            var href = ReadAttribute(html[anchors[index].Index..Math.Min(anchorEnd + 4, html.Length)], "href");
            var url = string.IsNullOrWhiteSpace(href) ? null : NormalizeUrl(href);
            if (string.IsNullOrEmpty(url)) continue;

            var title = Clean(html[start..anchorEnd]);
            var snippet = string.Empty;
            var snippetMatch = snippets.FirstOrDefault(match => match.Index >= start && match.Index < end);
            if (snippetMatch is not null)
            {
                var snippetStart = snippetMatch.Index + snippetMatch.Length;
                // 摘要容器可能是 <a> 也可能是 <div>/<span>：在下一个结果块之前找最近的结束标签，避免串到下一条结果。
                var closeIndex = NextClosingTag(html, snippetStart, end);
                snippet = Clean(html[snippetStart..closeIndex]);
            }

            results.Add(new WebSearchResult(
                TrimTo(string.IsNullOrWhiteSpace(title) ? url : title, MaxTitleLength),
                url,
                TrimTo(snippet, MaxSnippetLength)));

            if (results.Count >= maxResults) break;
        }

        return results;
    }

    /// <summary>解析 Bing Web Search v7 的 JSON 响应（webPages.value[].name/url/snippet）。</summary>
    public static IReadOnlyList<WebSearchResult> ParseBing(string? json, int maxResults = 5)
    {
        var results = new List<WebSearchResult>();
        if (string.IsNullOrWhiteSpace(json) || maxResults <= 0) return results;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetProperty(document.RootElement, "webPages", out var webPages)
                || !TryGetProperty(webPages, "value", out var values)
                || values.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var item in values.EnumerateArray())
            {
                var url = ReadString(item, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                results.Add(new WebSearchResult(
                    TrimTo(ReadString(item, "name") ?? url, MaxTitleLength),
                    url,
                    TrimTo(ReadString(item, "snippet") ?? string.Empty, MaxSnippetLength)));

                if (results.Count >= maxResults) break;
            }
        }
        catch (JsonException)
        {
            // 响应不是合法 JSON 时返回空列表，由调用方给出错误说明。
        }

        return results;
    }

    /// <summary>解析 Tavily 的 JSON 响应（results[].title/url/content）。</summary>
    public static IReadOnlyList<WebSearchResult> ParseTavily(string? json, int maxResults = 5)
    {
        var results = new List<WebSearchResult>();
        if (string.IsNullOrWhiteSpace(json) || maxResults <= 0) return results;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetProperty(document.RootElement, "results", out var items) || items.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var item in items.EnumerateArray())
            {
                var url = ReadString(item, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                results.Add(new WebSearchResult(
                    TrimTo(ReadString(item, "title") ?? url, MaxTitleLength),
                    url,
                    TrimTo(ReadString(item, "content") ?? string.Empty, MaxSnippetLength)));

                if (results.Count >= maxResults) break;
            }
        }
        catch (JsonException)
        {
            // 同上：非法 JSON 视为无结果。
        }

        return results;
    }

    /// <summary>
    /// 把搜索结果里的链接规范化：DuckDuckGo 的跳转链接形如
    /// <c>//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com&amp;rut=...</c>，需要取出 <c>uddg</c> 参数。
    /// </summary>
    public static string? NormalizeUrl(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;

        var value = WebUtility.HtmlDecode(href.Trim());
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        else if (value.StartsWith('/')) value = "https://duckduckgo.com" + value;

        var queryIndex = value.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0) return value;

        var query = value[(queryIndex + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;

            var key = Uri.UnescapeDataString(pair[..separator]);
            if (!key.Equals("uddg", StringComparison.OrdinalIgnoreCase)) continue;

            var target = Uri.UnescapeDataString(pair[(separator + 1)..].Replace("+", "%20"));
            return string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        }

        return value;
    }

    /// <summary>在 [start, end) 范围内找最近的闭合标签位置；找不到时返回范围末尾。</summary>
    private static int NextClosingTag(string html, int start, int end)
    {
        var limit = Math.Min(end, html.Length);
        var best = limit;
        foreach (var tag in new[] { "</a>", "</div>", "</span>", "</p>" })
        {
            var index = html.IndexOf(tag, start, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < best) best = index;
        }

        return best;
    }

    private static string? ReadAttribute(string html, string name)
    {
        var match = Regex.Match(html, name + @"\s*=\s*(?<quote>[""'])(?<value>.*?)\k<quote>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success) return match.Groups["value"].Value;

        match = Regex.Match(html, name + @"\s*=\s*(?<value>[^\s>]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim('"', '\'') : null;
    }

    /// <summary>去掉标签、解码实体并压缩空白。</summary>
    private static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = TagRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string TrimTo(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;

        // 兼容服务器返回不同大小写键名的情况。
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
