using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

public sealed class UtilityToolTests
{
    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("10/4", 2.5)]
    [InlineData("7%4", 3)]
    [InlineData("-(2+3)", -5)]
    [InlineData("--5", 5)]
    [InlineData("2*-3", -6)]
    [InlineData("1.5+2.25", 3.75)]
    [InlineData("-1.5*4", -6)]
    [InlineData("(8-2)/(1+2)", 2)]
    [InlineData(" 3 + 4 ", 7)]
    [InlineData("（1+2）×3", 9)]
    [InlineData("100-20-30", 50)]
    [InlineData("2*(3+(4-1))", 12)]
    public void Calculator_EvaluatesValidExpressions(string expression, double expected)
    {
        Assert.True(CalculatorExpression.TryEvaluate(expression, out var actual, out var error), error);
        Assert.Equal(expected, actual, 10);
    }

    [Theory]
    [InlineData("1/0", "除数")]
    [InlineData("5%0", "除数")]
    [InlineData("1+", "不完整")]
    [InlineData("(1+2", "括号不匹配")]
    [InlineData("1+2)", "无法识别")]
    [InlineData("2^3", "无法识别")]
    [InlineData("abc", "应为数字")]
    [InlineData("", "表达式为空")]
    [InlineData("   ", "表达式为空")]
    [InlineData(".", "数字格式")]
    public void Calculator_RejectsInvalidExpressionsWithChineseReason(string expression, string expectedHint)
    {
        Assert.False(CalculatorExpression.TryEvaluate(expression, out _, out var error));
        Assert.Contains(expectedHint, error, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => CalculatorExpression.Evaluate(expression));
    }

    [Fact]
    public void Calculator_RejectsOverlongExpression()
    {
        var expression = new string('1', 2_001);
        Assert.False(CalculatorExpression.TryEvaluate(expression, out _, out var error));
        Assert.Contains("过长", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(7, "7")]
    [InlineData(2.5, "2.5")]
    [InlineData(-5, "-5")]
    [InlineData(0.1 + 0.2, "0.3")]
    public void Calculator_FormatsResultsReadably(double value, string expected)
    {
        Assert.Equal(expected, CalculatorExpression.Format(value));
    }

    [Fact]
    public async Task CalculateTool_ReturnsStructuredResult()
    {
        var tool = await GetToolAsync(NewSource(), "calculate");
        using var arguments = JsonDocument.Parse("""{"expression":"(1.5+2)*3-4/2"}""");
        var raw = await tool.Handler(arguments.RootElement, CancellationToken.None);

        using var document = JsonDocument.Parse(raw);
        Assert.Equal(8.5, document.RootElement.GetProperty("result").GetDouble(), 10);
        Assert.Equal("8.5", document.RootElement.GetProperty("formatted").GetString());
        Assert.Equal("(1.5+2)*3-4/2", document.RootElement.GetProperty("expression").GetString());
    }

    [Fact]
    public async Task CalculateTool_ReturnsErrorJsonForDivisionByZero()
    {
        var tool = await GetToolAsync(NewSource(), "calculate");
        using var arguments = JsonDocument.Parse("""{"expression":"1/0"}""");
        var raw = await tool.Handler(arguments.RootElement, CancellationToken.None);

        using var document = JsonDocument.Parse(raw);
        Assert.Contains("除数", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentTime_ReturnsExpectedShape()
    {
        var tool = await GetToolAsync(NewSource(), "current_time");
        using var arguments = JsonDocument.Parse("{}");
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var raw = await tool.Handler(arguments.RootElement, CancellationToken.None);
        var after = DateTimeOffset.UtcNow.AddSeconds(5);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("utc").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("local").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("timeZone").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("timeZone").GetString()));

        var unix = root.GetProperty("unixSeconds").GetInt64();
        Assert.InRange(unix, before.ToUnixTimeSeconds(), after.ToUnixTimeSeconds());

        Assert.True(DateTimeOffset.TryParse(root.GetProperty("utc").GetString(), out var utc));
        Assert.InRange(utc, before, after);
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("local").GetString(), out var local));
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(utc.UtcDateTime), local.Offset);
    }

    [Fact]
    public async Task CurrentTime_SupportsExplicitTimeZone()
    {
        var tool = await GetToolAsync(NewSource(), "current_time");
        using var arguments = JsonDocument.Parse("""{"timeZone":"UTC"}""");
        var raw = await tool.Handler(arguments.RootElement, CancellationToken.None);

        using var document = JsonDocument.Parse(raw);
        Assert.Equal("UTC", document.RootElement.GetProperty("timeZone").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("offsetMinutes").GetInt32());
    }

    [Theory]
    [InlineData("""{"url":"ftp://example.com/a"}""", "只允许")]
    [InlineData("""{"url":"not a url"}""", "合法")]
    [InlineData("""{}""", "url 不能为空")]
    public async Task FetchUrl_RejectsUnsupportedInput(string argumentsJson, string expectedHint)
    {
        var tool = await GetToolAsync(NewSource(), "fetch_url");
        using var arguments = JsonDocument.Parse(argumentsJson);
        var raw = await tool.Handler(arguments.RootElement, CancellationToken.None);

        using var document = JsonDocument.Parse(raw);
        Assert.Contains(expectedHint, document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UtilitySourceExposesThreeTools()
    {
        var source = NewSource();
        Assert.Equal("utility", source.Name);

        var tools = await source.GetToolsAsync(new AgentToolContext(null), CancellationToken.None);

        Assert.Equal(3, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "fetch_url");
        Assert.Contains(tools, tool => tool.Name == "current_time");
        Assert.Contains(tools, tool => tool.Name == "calculate");

        // fetch_url 是联网能力，必须与 web_search 同分类（category "web"），
        // 否则用户关闭「允许联网搜索」后仍能通过它访问网络。
        var fetchUrl = Assert.Single(tools, tool => tool.Name == "fetch_url");
        Assert.Equal("web", fetchUrl.Category);
        Assert.All(tools.Where(tool => tool.Name != "fetch_url"), tool => Assert.Equal("utility", tool.Category));
    }

    private static UtilityToolSource NewSource() =>
        new(new ThrowingHttpClientFactory(),
            Microsoft.Extensions.Options.Options.Create(new WebSearchOptions()),
            Microsoft.Extensions.Options.Options.Create(new AgentOptions()));

    private static async Task<AgentToolDefinition> GetToolAsync(UtilityToolSource source, string name)
    {
        var tools = await source.GetToolsAsync(new AgentToolContext(null), CancellationToken.None);
        return Assert.Single(tools, tool => tool.Name == name);
    }

    /// <summary>测试里绝不发真实网络请求：任何创建 HttpClient 的调用都会直接失败。</summary>
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("测试环境禁止发真实网络请求。");
    }
}
