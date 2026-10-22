namespace MyFirstMafAgent.Api.Options;

/// <summary>
/// 联网搜索与网页抓取配置。默认使用无需密钥的 DuckDuckGo HTML 端点，
/// 也可以切换到 Bing / Tavily（需要 ApiKey），或设为 None 直接关闭联网搜索。
/// </summary>
public sealed class WebSearchOptions
{
    public const string SectionName = "WebSearch";

    /// <summary>搜索引擎：DuckDuckGo（默认）、Bing、Tavily 或 None。</summary>
    public string Provider { get; set; } = "DuckDuckGo";

    /// <summary>Bing / Tavily 使用的 API 密钥；留空时对应工具返回“未配置”提示。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Bing Web Search v7 端点。</summary>
    public string BingEndpoint { get; set; } = "https://api.bing.microsoft.com/v7.0/search";

    /// <summary>Tavily 搜索端点。</summary>
    public string TavilyEndpoint { get; set; } = "https://api.tavily.com/search";

    /// <summary>单次搜索最多返回多少条结果（1..10）。</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>搜索与抓取的超时秒数（1..120）。</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>请求时使用的 User-Agent；留空时使用内置默认值。</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    /// <summary>规范化后的搜索服务商名称，用于分发与结果回显。</summary>
    public string NormalizedProvider => NormalizeProvider(Provider);

    /// <summary>把配置值规范化为 DuckDuckGo / Bing / Tavily / None（无法识别时按 None 处理）。</summary>
    public static string NormalizeProvider(string? provider) => (provider ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "duckduckgo" or "ddg" => "DuckDuckGo",
        "bing" => "Bing",
        "tavily" => "Tavily",
        "none" or "" => "None",
        _ => "None"
    };
}
