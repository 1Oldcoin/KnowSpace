using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Tests;

public sealed class KnowledgeChatExportFormatterTests
{
    [Fact]
    public void Format_IncludesSessionMetadataAndMessagesInConversationOrder()
    {
        var createdAt = new DateTimeOffset(2025, 4, 5, 12, 13, 14, TimeSpan.FromHours(8));
        var updatedAt = createdAt.AddMinutes(2);
        var session = new KnowledgeChatSession(Guid.NewGuid(), Guid.NewGuid(), "学习问题", createdAt, updatedAt);
        var messages = new[]
        {
            new KnowledgeChatMessage(1, "user", "如何理解向量检索？", createdAt),
            new KnowledgeChatMessage(2, "assistant", "可以把它看作按语义寻找相近内容。", updatedAt),
        };

        var markdown = KnowledgeChatExportFormatter.Format(session, messages)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.StartsWith("# 学习问题", markdown);
        Assert.Contains("创建时间：2025-04-05 12:13:14 +08:00", markdown);
        Assert.Contains("最近更新：2025-04-05 12:15:14 +08:00", markdown);
        Assert.Contains("## 我 · 2025-04-05 12:13:14 +08:00\n\n如何理解向量检索？", markdown);
        Assert.Contains("## 知间 AI · 2025-04-05 12:15:14 +08:00\n\n可以把它看作按语义寻找相近内容。", markdown);
        Assert.True(markdown.IndexOf("## 我", StringComparison.Ordinal) < markdown.IndexOf("## 知间 AI", StringComparison.Ordinal));
    }
}
