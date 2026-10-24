using System.Text.Json;
using MyFirstMafAgent.Api.Companions;

namespace MyFirstMafAgent.Api.Tests;

public sealed class ChatHistoryParserTests
{
    private readonly ChatHistoryParser _parser = new();

    [Fact]
    public void Parse_JsonAcceptsCaseInsensitiveAliasesAndNumericValues()
    {
        const string content = """{"MESSAGES":[{"FROM":123,"MESSAGE":" hello ","TIME":"2025-01-02T03:04:05Z"},{"sender":"Bob","content":"Hi"}]}""";

        var messages = _parser.Parse("history.JSON", content);

        Assert.Equal(2, messages.Count);
        Assert.Equal("123", messages[0].Sender);
        Assert.Equal("hello", messages[0].Content);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), messages[0].SentAt);
        Assert.Equal("Bob", messages[1].Sender);
        Assert.Null(messages[1].SentAt);
    }

    [Fact]
    public void Parse_TextAppendsContinuationLinesAndReadsTimestamp()
    {
        const string content = "Alice: first line\ncontinuation\n[2025-01-02T03:04:05Z] Bob：第二条";

        var messages = _parser.Parse("history.txt", content);

        Assert.Equal(2, messages.Count);
        Assert.Equal("first line\ncontinuation", messages[0].Content);
        Assert.Equal("Bob", messages[1].Sender);
        Assert.Equal("第二条", messages[1].Content);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), messages[1].SentAt);
    }

    [Fact]
    public void Parse_UnsupportedExtensionThrowsChatImportException()
    {
        var exception = Assert.Throws<ChatImportException>(() => _parser.Parse("history.xml", "<messages />"));

        Assert.Contains("支持", exception.Message);
    }

    /// <summary>QQ 文本导出的块式：时间 + 发送者(号码) 独占一行，内容在下面若干行。</summary>
    [Fact]
    public void Parse_QqBlockFormatReadsSenderAndMultilineContent()
    {
        const string content = """
            ================================================================
            消息记录（此消息记录为文本格式，不支持重新导入）
            ================================================================
            消息分组:我的好友
            消息对象:小明
            ================================================================

            2023-05-12 21:03:11 小明(123456)
            今天月亮很圆

            2023-05-12 21:04:02 我(654321)
            是啊，可惜没拍到

            """;

        var messages = _parser.Parse("消息记录.txt", content);

        Assert.Equal(2, messages.Count);
        Assert.Equal("小明", messages[0].Sender);
        Assert.Equal("今天月亮很圆", messages[0].Content);
        Assert.NotNull(messages[0].SentAt);
        Assert.Equal("我", messages[1].Sender);
        Assert.Equal("是啊，可惜没拍到", messages[1].Content);
    }

    /// <summary>块式里正文带冒号（网址、时间）不能被当成新消息。</summary>
    [Fact]
    public void Parse_QqBlockFormatKeepsColonInsideContent()
    {
        const string content = """
            2023-05-12 21:03:11 小明(123456)
            链接在这里 https://example.com/a:b
            还有 21:30 见
            """;

        var messages = _parser.Parse("qq.txt", content);

        Assert.Single(messages);
        Assert.Contains("https://example.com/a:b", messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("21:30", messages[0].Content, StringComparison.Ordinal);
    }

    /// <summary>微信常用导出：时间 + 发送者 + 冒号内容在同一行。</summary>
    [Fact]
    public void Parse_WeChatInlineFormatReadsTimestamp()
    {
        const string content = """
            2023-05-12 21:03:11 小明: 今天月亮很圆
            2023-05-12 21:04:02 我: 是啊
            """;

        var messages = _parser.Parse("wechat.txt", content);

        Assert.Equal(2, messages.Count);
        Assert.Equal("小明", messages[0].Sender);
        Assert.Equal("今天月亮很圆", messages[0].Content);
        Assert.Equal(2023, messages[0].SentAt!.Value.Year);
    }

    /// <summary>发送者在前的形态：<c>小明 2023-05-12 21:03:11: 内容</c>。</summary>
    [Fact]
    public void Parse_SenderFirstFormatReadsTimestamp()
    {
        const string content = "小明 2023-05-12 21:03:11: 今天月亮很圆";

        var messages = _parser.Parse("wechat.txt", content);

        Assert.Single(messages);
        Assert.Equal("小明", messages[0].Sender);
        Assert.Equal("今天月亮很圆", messages[0].Content);
        Assert.NotNull(messages[0].SentAt);
    }

    /// <summary>WeChatMsg 那类工具导出的 CSV：按表头找列，表头不认识就按前三列读。</summary>
    [Fact]
    public void Parse_CsvUsesHeaderColumnsAndFallsBackToPosition()
    {
        const string withHeader = """
            StrTime,StrTalker,StrContent
            2023-05-12 21:03:11,小明,"今天月亮很圆, 真的"
            """;

        var messages = _parser.Parse("chat.csv", withHeader);

        Assert.Single(messages);
        Assert.Equal("小明", messages[0].Sender);
        Assert.Equal("今天月亮很圆, 真的", messages[0].Content);
        Assert.NotNull(messages[0].SentAt);

        const string noHeader = "2023-05-12 21:03:11,小明,你好";
        var fallback = _parser.Parse("chat.csv", noHeader);
        Assert.Single(fallback);
        Assert.Equal("小明", fallback[0].Sender);
        Assert.Equal("你好", fallback[0].Content);
    }

    [Fact]
    public void Parse_EmptyFileReportsSupportedFormats()
    {
        var exception = Assert.Throws<ChatImportException>(() => _parser.Parse("empty.txt", "\n\n"));

        Assert.Contains("QQ", exception.Message);
    }

    [Fact]
    public void Parse_MessageLongerThanLimitThrowsChatImportException()
    {
        var content = JsonSerializer.Serialize(new[] { new { sender = "Alice", content = new string('x', 20_001) } });

        var exception = Assert.Throws<ChatImportException>(() => _parser.Parse("history.json", content));

        Assert.Contains("20,000", exception.Message);
    }
}
