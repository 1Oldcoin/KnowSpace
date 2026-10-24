using MyFirstMafAgent.Api.Domain.Notes;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 技术债 T-1 的领域侧规则：过滤条件怎么归一化，决定了 SQL 到底要不要加 WHERE。
/// 这里尤其要钉住「空白字符串 = 不过滤」，否则一次空搜索会把整库笔记拉出来。
/// </summary>
public sealed class NoteQueryTests
{
    private static readonly Guid KnowledgeBaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Defaults_mean_no_filter()
    {
        var query = new NoteQuery(KnowledgeBaseId);

        Assert.Null(query.NormalizedSearch);
        Assert.Null(query.NormalizedCategory);
        Assert.Null(query.EffectiveDocumentId);
        Assert.Null(query.EffectiveTopicId);
    }

    [Fact]
    public void Blank_strings_are_treated_as_absent()
    {
        var query = new NoteQuery(KnowledgeBaseId, "   ", "\t\n", Guid.Empty, Guid.Empty);

        Assert.Null(query.NormalizedSearch);
        Assert.Null(query.NormalizedCategory);
        Assert.Null(query.EffectiveDocumentId);
        Assert.Null(query.EffectiveTopicId);
    }

    [Fact]
    public void Values_are_trimmed_but_not_lowercased()
    {
        // 大小写不敏感由 SQL 的 ILIKE 负责，领域层不做破坏性的小写化——
        // 否则回显/日志里的分类名会变成全小写。
        var query = new NoteQuery(KnowledgeBaseId, "  插件协议  ", " 问答笔记 ");

        Assert.Equal("插件协议", query.NormalizedSearch);
        Assert.Equal("问答笔记", query.NormalizedCategory);
    }

    [Fact]
    public void Document_and_topic_filters_survive_when_real()
    {
        var documentId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var query = new NoteQuery(KnowledgeBaseId, DocumentId: documentId, TopicId: topicId);

        Assert.Equal(documentId, query.EffectiveDocumentId);
        Assert.Equal(topicId, query.EffectiveTopicId);
    }
}
