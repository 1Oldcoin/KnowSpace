using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Domain.Notes;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// P2-2「笔记与资料关联」的领域规则。
/// 关注三点：引用片段要能存回来、重复引用不能膨胀、清空引用必须能表达出来。
/// </summary>
public sealed class NoteSourceTests
{
    private static readonly Guid KnowledgeBaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_derives_source_document_from_first_source()
    {
        var documentId = Guid.NewGuid();
        var note = Note.Create(
            KnowledgeBaseId, "标题", "内容", null, null, null, null, Now,
            sources: new[] { new NoteSource(documentId, "资料.pdf", 2, 0.72, "片段") });

        Assert.Equal(documentId, note.SourceDocumentId);
        Assert.Single(note.Sources);
        Assert.Equal(2, note.Sources[0].ChunkIndex);
    }

    [Fact]
    public void Create_keeps_explicit_source_document_over_sources()
    {
        var explicitId = Guid.NewGuid();
        var note = Note.Create(
            KnowledgeBaseId, "标题", "内容", null, null, null, explicitId, Now,
            sources: new[] { new NoteSource(Guid.NewGuid(), "别的.pdf", 0, 0.5, null) });

        Assert.Equal(explicitId, note.SourceDocumentId);
    }

    [Fact]
    public void Sources_are_deduplicated_by_document_and_chunk()
    {
        var documentId = Guid.NewGuid();
        var note = Note.Create(
            KnowledgeBaseId, "标题", "内容", null, null, null, null, Now,
            sources: new[]
            {
                new NoteSource(documentId, "a.md", 1, 0.9, "第一次"),
                new NoteSource(documentId, "a.md", 1, 0.8, "重复"),
                new NoteSource(documentId, "a.md", 2, 0.7, "同文件另一段")
            });

        Assert.Equal(2, note.Sources.Count);
        Assert.Equal("第一次", note.Sources[0].Snippet);
    }

    [Fact]
    public void Sources_drop_empty_document_id_and_cap_count()
    {
        var many = Enumerable.Range(0, Note.MaxSourceCount + 5)
            .Select(index => new NoteSource(Guid.NewGuid(), $"f{index}.md", index, 0.5, null))
            .Prepend(new NoteSource(Guid.Empty, "无效.md", 0, 0.5, null));

        var note = Note.Create(KnowledgeBaseId, "标题", "内容", null, null, null, null, Now, sources: many);

        Assert.Equal(Note.MaxSourceCount, note.Sources.Count);
        Assert.DoesNotContain(note.Sources, source => source.DocumentId == Guid.Empty);
    }

    [Fact]
    public void Sources_truncate_snippet_and_name_and_survive_nan_similarity()
    {
        var longSnippet = new string('字', Note.MaxSourceSnippetLength + 40);
        var longName = new string('n', Note.MaxSourceFileNameLength + 20);
        var note = Note.Create(
            KnowledgeBaseId, "标题", "内容", null, null, null, null, Now,
            sources: new[] { new NoteSource(Guid.NewGuid(), longName, 0, double.NaN, longSnippet) });

        Assert.Equal(Note.MaxSourceFileNameLength, note.Sources[0].FileName.Length);
        Assert.Equal(Note.MaxSourceSnippetLength + 1, note.Sources[0].Snippet!.Length);
        Assert.True(note.Sources[0].Snippet!.EndsWith('…'));
        Assert.Equal(0, note.Sources[0].Similarity);
    }

    [Fact]
    public void Sources_with_blank_name_fall_back_to_placeholder()
    {
        var note = Note.Create(
            KnowledgeBaseId, "标题", "内容", null, null, null, null, Now,
            sources: new[] { new NoteSource(Guid.NewGuid(), "   ", 0, 0.5, null) });

        Assert.Equal("未命名资料", note.Sources[0].FileName);
    }

    [Fact]
    public void Update_keeps_sources_when_null_is_passed()
    {
        var documentId = Guid.NewGuid();
        var note = Note.Create(
            KnowledgeBaseId, "标题", "内容", null, null, null, null, Now,
            sources: new[] { new NoteSource(documentId, "a.md", 0, 0.5, null) },
            sourceSessionId: Guid.NewGuid());

        note.Update("新标题", "新内容", null, null, null, Now.AddHours(1));

        Assert.Single(note.Sources);
        Assert.Equal(documentId, note.SourceDocumentId);
        Assert.NotNull(note.SourceSessionId);
        Assert.Equal("新标题", note.Title);
    }

    [Fact]
    public void Update_can_clear_sources_and_topic()
    {
        var note = Note.Create(
            KnowledgeBaseId, "标题", "内容", null, null, null, null, Now,
            topicId: Guid.NewGuid(),
            sources: new[] { new NoteSource(Guid.NewGuid(), "a.md", 0, 0.5, null) });

        note.Update("标题", "内容", null, null, null, Now.AddHours(1), Guid.Empty, Array.Empty<NoteSource>());

        Assert.Empty(note.Sources);
        Assert.Null(note.SourceDocumentId);
        Assert.Null(note.TopicId);
    }

    [Fact]
    public void Restore_roundtrips_sources_and_topic()
    {
        var documentId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var note = Note.Restore(
            Guid.NewGuid(), KnowledgeBaseId, "标题", "内容", "资料摘录", "rose", new[] { "a" }, true, documentId, Now, Now.AddMinutes(5),
            topicId, new[] { new NoteSource(documentId, "a.md", 3, 0.61, "片段") }, sessionId);

        Assert.Equal(topicId, note.TopicId);
        Assert.Equal(sessionId, note.SourceSessionId);
        Assert.Single(note.Sources);
        Assert.Equal(0.61, note.Sources[0].Similarity);
    }
}
