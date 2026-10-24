using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Study;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

public sealed class StudyCalculatorTests
{
    [Fact]
    public void Summarize_counts_consecutive_days_ending_today()
    {
        var today = new DateOnly(2026, 10, 18);
        var records = new List<StudyRecord>
        {
            new(today, 40, null),
            new(today.AddDays(-1), 30, "昨天"),
            new(today.AddDays(-2), 20, null),
            new(today.AddDays(-4), 50, null)
        };

        var statistics = StudyCalculator.Summarize(records, today, 30);

        Assert.True(statistics.TodayCheckedIn);
        Assert.Equal(3, statistics.CurrentStreak);
        Assert.Equal(3, statistics.LongestStreak);
        Assert.Equal(4, statistics.TotalDays);
        Assert.Equal(40, statistics.TodayMinutes);
        // 2026-10-18 是周日，本周从 10-12 开始，四条记录都在本周内。
        Assert.Equal(140, statistics.ThisWeekMinutes);
        Assert.Equal(7, statistics.Week.Count);
    }

    [Fact]
    public void Summarize_keeps_streak_alive_when_today_is_not_checked_in()
    {
        var today = new DateOnly(2026, 10, 18);
        var records = new List<StudyRecord>
        {
            new(today.AddDays(-1), 30, null),
            new(today.AddDays(-2), 30, null)
        };

        var statistics = StudyCalculator.Summarize(records, today, 30);

        Assert.False(statistics.TodayCheckedIn);
        Assert.Equal(2, statistics.CurrentStreak);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1441)]
    public void Validate_rejects_out_of_range_minutes(int minutes)
    {
        Assert.Throws<StudyValidationException>(() =>
            StudyCalculator.Validate(minutes, null, new DateOnly(2026, 10, 18)));
    }

    [Fact]
    public void Validate_trims_note_and_allows_empty()
    {
        var record = StudyCalculator.Validate(30, "  看完第三章  ", new DateOnly(2026, 10, 18));
        Assert.Equal("看完第三章", record.Note);

        var empty = StudyCalculator.Validate(0, "   ", new DateOnly(2026, 10, 18));
        Assert.Null(empty.Note);
    }
}

public sealed class NoteAggregateTests
{
    private static readonly Guid KnowledgeBaseId = Guid.NewGuid();

    [Fact]
    public void Create_normalizes_tags_and_defaults_category()
    {
        var note = Note.Create(KnowledgeBaseId, "  标题  ", "内容", null, "unknown",
            new[] { " #学习方法 ", "学习方法", "输出", "" }, null, DateTimeOffset.UtcNow);

        Assert.Equal("标题", note.Title);
        Assert.Equal(NoteDefaults.Category, note.Category);
        Assert.Equal(NoteDefaults.Color, note.Color);
        Assert.Equal(new[] { "学习方法", "输出" }, note.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_title(string title)
    {
        Assert.Throws<NoteValidationException>(() =>
            Note.Create(KnowledgeBaseId, title, "内容", null, null, null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_rejects_too_many_tags()
    {
        var tags = Enumerable.Range(0, Note.MaxTagCount + 1).Select(index => $"tag{index}").ToArray();
        Assert.Throws<NoteValidationException>(() =>
            Note.Create(KnowledgeBaseId, "标题", "内容", null, null, tags, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Excerpt_collapses_newlines_and_truncates()
    {
        var content = new string('a', 300) + "\n\n第二段";
        var note = Note.Create(KnowledgeBaseId, "标题", content, null, null, null, null, DateTimeOffset.UtcNow);

        Assert.DoesNotContain("\n", note.Excerpt);
        Assert.EndsWith("…", note.Excerpt);
    }
}

public sealed class NoteServiceTests
{
    [Fact]
    public async Task CreateAsync_persists_note_for_existing_knowledge_base()
    {
        var repository = new InMemoryNoteRepository();
        var service = new NoteService(repository, new AlwaysExistsDirectory());

        var dto = await service.CreateAsync(new CreateNoteCommand(
            Guid.NewGuid(), "第一篇", "正文", "读书摘录", "mint", new[] { "输出" }, null), CancellationToken.None);

        Assert.Equal("第一篇", dto.Title);
        Assert.Equal(1, await service.CountAsync(dto.KnowledgeBaseId, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_rejects_unknown_knowledge_base()
    {
        var service = new NoteService(new InMemoryNoteRepository(), new NeverExistsDirectory());

        await Assert.ThrowsAsync<KnowledgeBaseNotFoundException>(() =>
            service.CreateAsync(new CreateNoteCommand(Guid.NewGuid(), "标题", "正文", null, null, null, null),
                CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_orders_pinned_notes_first_and_filters_by_query()
    {
        var repository = new InMemoryNoteRepository();
        var knowledgeBaseId = Guid.NewGuid();
        var service = new NoteService(repository, new AlwaysExistsDirectory());

        var pinned = await service.CreateAsync(new CreateNoteCommand(knowledgeBaseId, "置顶笔记", "alpha", null, null, null, null), CancellationToken.None);
        await service.SetPinnedAsync(knowledgeBaseId, pinned.Id, true, CancellationToken.None);
        await service.CreateAsync(new CreateNoteCommand(knowledgeBaseId, "普通笔记", "beta", null, null, null, null), CancellationToken.None);

        var all = await service.ListAsync(knowledgeBaseId, null, null, CancellationToken.None);
        Assert.Equal("置顶笔记", all[0].Title);

        var filtered = await service.ListAsync(knowledgeBaseId, "beta", null, CancellationToken.None);
        Assert.Single(filtered);
        Assert.Equal("普通笔记", filtered[0].Title);
    }

    private sealed class AlwaysExistsDirectory : IKnowledgeBaseDirectory
    {
        public Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class NeverExistsDirectory : IKnowledgeBaseDirectory
    {
        public Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class InMemoryNoteRepository : INoteRepository
    {
        private readonly Dictionary<Guid, Note> _notes = new();

        public Task<IReadOnlyList<Note>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Note>>(_notes.Values.Where(note => note.KnowledgeBaseId == knowledgeBaseId).ToList());

        /// <summary>
        /// 内存实现同样把过滤放在"存储层"做，行为与 SQL 版对齐（大小写不敏感、按资料/专题过滤）。
        /// 用它来钉住 NoteService 不再自己做过滤这件事。
        /// </summary>
        public Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken cancellationToken)
        {
            IEnumerable<Note> filtered = _notes.Values.Where(note => note.KnowledgeBaseId == query.KnowledgeBaseId);

            if (query.NormalizedCategory is { } category)
                filtered = filtered.Where(note => string.Equals(note.Category, category, StringComparison.OrdinalIgnoreCase));

            if (query.EffectiveDocumentId is { } documentId)
                filtered = filtered.Where(note =>
                    note.SourceDocumentId == documentId || note.Sources.Any(source => source.DocumentId == documentId));

            if (query.EffectiveTopicId is { } topicId)
                filtered = filtered.Where(note => note.TopicId == topicId);

            if (query.NormalizedSearch is { } search)
                filtered = filtered.Where(note =>
                    note.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || note.Content.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || note.Tags.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase)));

            var result = filtered
                .OrderByDescending(note => note.IsPinned)
                .ThenByDescending(note => note.UpdatedAt)
                .ToList();
            return Task.FromResult<IReadOnlyList<Note>>(result);
        }

        public Task<Note?> GetAsync(Guid noteId, CancellationToken cancellationToken) =>
            Task.FromResult(_notes.TryGetValue(noteId, out var note) ? note : null);

        public Task<int> CountAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult(_notes.Values.Count(note => note.KnowledgeBaseId == knowledgeBaseId));

        public Task AddAsync(Note note, CancellationToken cancellationToken)
        {
            _notes[note.Id] = note;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Note note, CancellationToken cancellationToken)
        {
            _notes[note.Id] = note;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid noteId, Guid knowledgeBaseId, CancellationToken cancellationToken)
        {
            var removed = _notes.TryGetValue(noteId, out var note) && note.KnowledgeBaseId == knowledgeBaseId && _notes.Remove(noteId);
            return Task.FromResult(removed);
        }
    }
}

public sealed class DocumentContentServiceTests
{
    [Fact]
    public async Task GetPageAsync_pages_through_concatenated_chunks()
    {
        var chunks = new List<DocumentChunk>
        {
            new(0, new string('a', 100)),
            new(1, new string('b', 100))
        };
        var service = new DocumentContentService(new StubDocumentTextRepository(
            new DocumentText(Guid.NewGuid(), "材料.md", ".md", chunks)));

        var first = await service.GetPageAsync(Guid.NewGuid(), 0, 120, CancellationToken.None);
        Assert.Equal(100 + 2 + 100, first.TotalLength);
        Assert.True(first.HasMore);
        Assert.Equal(120, first.Content.Length);

        var second = await service.GetPageAsync(Guid.NewGuid(), 120, 120, CancellationToken.None);
        Assert.False(second.HasMore);
        Assert.Equal(first.TotalLength - 120, second.Content.Length);
    }

    [Fact]
    public async Task GetPageAsync_throws_when_document_missing()
    {
        var service = new DocumentContentService(new StubDocumentTextRepository(null));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            service.GetPageAsync(Guid.NewGuid(), 0, 100, CancellationToken.None));
    }

    private sealed class StubDocumentTextRepository(DocumentText? document) : IDocumentTextRepository
    {
        public Task<DocumentText?> GetTextAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult(document);
    }
}
