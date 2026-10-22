using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

public sealed class WebSearchToolTests
{
    private const string DuckDuckGoSample = """
        <!DOCTYPE html>
        <html><body>
        <div class="result results_links results_links_deep web-result ">
          <div class="links_main links_deep result__body">
            <h2 class="result__title">
              <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Flearn.microsoft.com%2Fdotnet%2F&amp;rut=abc123">.NET 官方文档</a>
            </h2>
            <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Flearn.microsoft.com%2Fdotnet%2F&amp;rut=abc123">Microsoft 提供的 <b>.NET</b> 文档与教程。</a>
          </div>
        </div>
        <div class="result results_links results_links_deep web-result ">
          <h2 class="result__title">
            <a class="result__a" href="https://example.com/page?x=1">Example &amp; 示例</a>
          </h2>
          <div class="result__snippet">一个用于演示的示例页面。</div>
        </div>
        <div class="result results_links results_links_deep web-result ">
          <h2 class="result__title">
            <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fdotnet.microsoft.com%2F&amp;rut=zzz">第三 条</a>
          </h2>
        </div>
        </body></html>
        """;

    [Fact]
    public void ParseDuckDuckGo_ExtractsTitlesUrlsAndSnippets()
    {
        var results = SearchResultParser.ParseDuckDuckGo(DuckDuckGoSample, 5);

        Assert.Equal(3, results.Count);
        Assert.Equal(".NET 官方文档", results[0].Title);
        Assert.Equal("https://learn.microsoft.com/dotnet/", results[0].Url);
        Assert.Contains(".NET", results[0].Snippet, StringComparison.Ordinal);
        Assert.Equal("Example & 示例", results[1].Title);
        Assert.Equal("https://example.com/page?x=1", results[1].Url);
        Assert.Equal("一个用于演示的示例页面。", results[1].Snippet);
        Assert.Equal("https://dotnet.microsoft.com/", results[2].Url);
        Assert.Equal(string.Empty, results[2].Snippet);
    }


    [Fact]
    public void ParseDuckDuckGo_HonoursMaxResults()
    {
        var results = SearchResultParser.ParseDuckDuckGo(DuckDuckGoSample, 2);
        Assert.Equal(2, results.Count);
    }

    [Theory]
    [InlineData("//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa%3Fb%3D1&rut=x", "https://example.com/a?b=1")]
    [InlineData("https://example.com/plain", "https://example.com/plain")]
    [InlineData("/l/?uddg=http%3A%2F%2Flocalhost%3A8080%2F", "http://localhost:8080/")]
    public void NormalizeUrl_DecodesDuckDuckGoRedirect(string href, string expected)
    {
        Assert.Equal(expected, SearchResultParser.NormalizeUrl(href));
    }

    [Fact]
    public void ParseDuckDuckGo_ReturnsEmptyForGarbage()
    {
        Assert.Empty(SearchResultParser.ParseDuckDuckGo(null));
        Assert.Empty(SearchResultParser.ParseDuckDuckGo("<html><body>nothing here</body></html>"));
    }

    [Fact]
    public void ParseBing_ReadsWebPages()
    {
        const string json = """
            {"webPages":{"value":[
              {"name":"中山大学","url":"https://www.sysu.edu.cn/","snippet":"中山大学是综合性大学。"},
              {"name":"第二 条","url":"https://example.org/","snippet":"摘要二"}
            ]}}
            """;

        var results = SearchResultParser.ParseBing(json, 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("中山大学", results[0].Title);
        Assert.Equal("https://www.sysu.edu.cn/", results[0].Url);
        Assert.Equal("中山大学是综合性大学。", results[0].Snippet);
        Assert.Single(SearchResultParser.ParseBing(json, 1).ToList());
    }

    [Fact]
    public void ParseBing_ReturnsEmptyForInvalidOrMissingResponse()
    {
        Assert.Empty(SearchResultParser.ParseBing("not json"));
        Assert.Empty(SearchResultParser.ParseBing("{}"));
        Assert.Empty(SearchResultParser.ParseBing("""{"webPages":{"value":[]}}"""));
    }

    [Fact]
    public void ParseTavily_ReadsResults()
    {
        const string json = """
            {"query":"net10","results":[
              {"title":"What's new in .NET 10","url":"https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10","content":"概述 .NET 10 的新特性。"},
              {"title":"第二条","url":"https://example.com/b","content":"内容二"}
            ]}
            """;

        var results = SearchResultParser.ParseTavily(json, 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("What's new in .NET 10", results[0].Title);
        Assert.Equal("概述 .NET 10 的新特性。", results[0].Snippet);
        Assert.Single(SearchResultParser.ParseTavily(json, 1).ToList());
    }

    [Fact]
    public async Task WebSearch_ReturnsProviderErrorWhenNotConfigured()
    {
        // Provider=None 时不允许发网络请求：直接返回中文提示。
        var tool = await GetToolAsync(NewSource("None", apiKey: ""));

        using var arguments = JsonDocument.Parse("""{"query":"今天天气"}""");
        var result = await tool.Handler(arguments.RootElement, CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("None", document.RootElement.GetProperty("provider").GetString());
        Assert.Contains("未配置联网搜索服务商", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSearch_ReturnsErrorWhenKeyMissingForBingOrTavily()
    {
        foreach (var provider in new[] { "Bing", "Tavily" })
        {
            var tool = await GetToolAsync(NewSource(provider, apiKey: ""));
            using var arguments = JsonDocument.Parse("""{"query":"x"}""");
            var result = await tool.Handler(arguments.RootElement, CancellationToken.None);

            using var document = JsonDocument.Parse(result);
            Assert.Equal(provider, document.RootElement.GetProperty("provider").GetString());
            Assert.Contains("未配置", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WebSearch_RejectsEmptyQuery()
    {
        var tool = await GetToolAsync(NewSource("DuckDuckGo", apiKey: ""));
        using var arguments = JsonDocument.Parse("{}");
        var result = await tool.Handler(arguments.RootElement, CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.Contains("query", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebSearch_SourceExposesSingleWebSearchTool()
    {
        var source = NewSource("DuckDuckGo", apiKey: "");
        Assert.Equal("web", source.Name);

        var tools = await source.GetToolsAsync(new AgentToolContext(null), CancellationToken.None);

        var tool = Assert.Single(tools);
        Assert.Equal("web_search", tool.Name);
        Assert.Equal("web", tool.Category);
        using var schema = JsonDocument.Parse(tool.ParametersJsonSchema);
        Assert.Equal("query", schema.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void Options_NormalizeProviderFallsBackToNone()
    {
        Assert.Equal("DuckDuckGo", WebSearchOptions.NormalizeProvider("duckduckgo"));
        Assert.Equal("Bing", WebSearchOptions.NormalizeProvider(" BING "));
        Assert.Equal("Tavily", WebSearchOptions.NormalizeProvider("tavily"));
        Assert.Equal("None", WebSearchOptions.NormalizeProvider("google"));
        Assert.Equal("None", WebSearchOptions.NormalizeProvider(null));

        var defaults = new WebSearchOptions();
        Assert.Equal("DuckDuckGo", defaults.NormalizedProvider);
        Assert.Equal(5, defaults.MaxResults);
        Assert.Equal(15, defaults.TimeoutSeconds);
        Assert.Equal("https://api.bing.microsoft.com/v7.0/search", defaults.BingEndpoint);
        Assert.Equal("https://api.tavily.com/search", defaults.TavilyEndpoint);
    }

    private static WebSearchToolSource NewSource(string provider, string apiKey) =>
        new(new StubHttpClientFactory(),
            Microsoft.Extensions.Options.Options.Create(new WebSearchOptions { Provider = provider, ApiKey = apiKey }));

    private static async Task<AgentToolDefinition> GetToolAsync(WebSearchToolSource source)
    {
        var tools = await source.GetToolsAsync(new AgentToolContext(null), CancellationToken.None);
        return Assert.Single(tools);
    }

    /// <summary>测试里绝不发真实网络请求：任何创建 HttpClient 的调用都会直接失败。</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("测试环境禁止发真实网络请求。");
    }
}