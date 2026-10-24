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

    /// <summary>P2-5：导出件脱离应用后仍要能核对答案依据，引用与来源必须写进 Markdown。</summary>
    [Fact]
    public void Format_AppendsCitationsAndSourcesForAssistantMessages()
    {
        var createdAt = new DateTimeOffset(2025, 4, 5, 12, 13, 14, TimeSpan.FromHours(8));
        var session = new KnowledgeChatSession(Guid.NewGuid(), Guid.NewGuid(), "引用导出", createdAt, createdAt);
        var citations = new[]
        {
            new KnowledgeChatCitation(Guid.NewGuid(), "架构说明.md", 0.8123, 1, 101, 0, "分层与依赖方向"),
            new KnowledgeChatCitation(Guid.NewGuid(), "部署手册.md", 0.5012, 2, 202, 3, null),
        };
        var meta = new KnowledgeChatAnswerMeta(
            new KnowledgeChatRetrieval(Guid.NewGuid(), "bge-m3@1024", "索引正在重建，已回退旧版检索", false),
            Sources: new[] { "https://example.com/doc" });
        var messages = new[]
        {
            new KnowledgeChatMessage(1, "user", "服务怎么分层？", createdAt),
            new KnowledgeChatMessage(2, "assistant", "按领域与应用服务分。", createdAt, citations, meta),
        };

        var markdown = KnowledgeChatExportFormatter.Format(session, messages)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("> 引用资料（2 条，按相关度排序）", markdown);
        Assert.Contains("> - [1] 架构说明.md（第 1 段） · 相关度 81%", markdown);
        Assert.Contains(">   分层与依赖方向", markdown);
        Assert.Contains("> - [2] 部署手册.md（第 4 段） · 相关度 50%", markdown);
        Assert.Contains("> 向量模型：bge-m3@1024", markdown);
        Assert.Contains("> 检索提示：索引正在重建，已回退旧版检索", markdown);
        Assert.Contains("> 网页来源（1 条）", markdown);
        Assert.Contains("> - https://example.com/doc", markdown);
    }

    /// <summary>用户消息没有引用时不能凭空多出空引用块。</summary>
    [Fact]
    public void Format_OmitsCitationBlockWhenMessageHasNone()
    {
        var createdAt = new DateTimeOffset(2025, 4, 5, 12, 13, 14, TimeSpan.FromHours(8));
        var session = new KnowledgeChatSession(Guid.NewGuid(), Guid.NewGuid(), "无引用", createdAt, createdAt);
        var messages = new[] { new KnowledgeChatMessage(1, "user", "你好", createdAt) };

        var markdown = KnowledgeChatExportFormatter.Format(session, messages);

        Assert.DoesNotContain("引用资料", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("网页来源", markdown, StringComparison.Ordinal);
    }
}
