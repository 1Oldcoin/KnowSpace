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
        var exception = Assert.Throws<ChatImportException>(() => _parser.Parse("history.csv", "Alice,Hello"));

        Assert.Contains("支持", exception.Message);
    }

    [Fact]
    public void Parse_MessageLongerThanLimitThrowsChatImportException()
    {
        var content = JsonSerializer.Serialize(new[] { new { sender = "Alice", content = new string('x', 20_001) } });

        var exception = Assert.Throws<ChatImportException>(() => _parser.Parse("history.json", content));

        Assert.Contains("20,000", exception.Message);
    }
}
