using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;

namespace MyFirstMafAgent.Api.Application.Notes;

public sealed record CreateNoteCommand(
    Guid KnowledgeBaseId,
    string Title,
    string Content,
    string? Category,
    string? Color,
    IReadOnlyList<string>? Tags,
    Guid? SourceDocumentId,
    Guid? TopicId = null,
    IReadOnlyList<NoteSource>? Sources = null,
    Guid? SourceSessionId = null);

public sealed record UpdateNoteCommand(
    string Title,
    string Content,
    string? Category,
    string? Color,
    IReadOnlyList<string>? Tags,
    Guid? TopicId = null,
    IReadOnlyList<NoteSource>? Sources = null);

public sealed record NoteDto(
    Guid Id,
    Guid KnowledgeBaseId,
    string Title,
    string Excerpt,
    string Content,
    string Category,
    string Color,
    IReadOnlyList<string> Tags,
    bool IsPinned,
    Guid? SourceDocumentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? TopicId = null,
    IReadOnlyList<NoteSource>? Sources = null,
    Guid? SourceSessionId = null)
{
    public static NoteDto FromDomain(Note note) => new(
        note.Id,
        note.KnowledgeBaseId,
        note.Title,
        note.Excerpt,
        note.Content,
        note.Category,
        note.Color,
        note.Tags,
        note.IsPinned,
        note.SourceDocumentId,
        note.CreatedAt,
        note.UpdatedAt,
        note.TopicId,
        note.Sources,
        note.SourceSessionId);
}

public sealed class NoteNotFoundException(Guid noteId) : NotFoundException($"笔记 {noteId} 不存在。")
{
    public Guid NoteId { get; } = noteId;
}

/// <summary>笔记应用服务：编排领域规则与仓储，不包含持久化细节。</summary>
public sealed class NoteService(INoteRepository repository, IKnowledgeBaseDirectory knowledgeBases)
{
    public async Task<IReadOnlyList<NoteDto>> ListAsync(
        Guid knowledgeBaseId,
        string? query,
        string? category,
        CancellationToken cancellationToken,
        Guid? documentId = null,
        Guid? topicId = null)
    {
        if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken))
            throw new KnowledgeBaseNotFoundException(knowledgeBaseId);

        // 过滤交给仓储翻译成 WHERE：不再把整库笔记（含 50 KB 正文）读进内存再筛。
        var notes = await repository.ListAsync(
            new NoteQuery(knowledgeBaseId, query, category, documentId, topicId),
            cancellationToken);
        return notes.Select(NoteDto.FromDomain).ToList();
    }

    public async Task<NoteDto> GetAsync(Guid knowledgeBaseId, Guid noteId, CancellationToken cancellationToken)
    {
        var note = await LoadAsync(knowledgeBaseId, noteId, cancellationToken);
        return NoteDto.FromDomain(note);
    }

    public async Task<NoteDto> CreateAsync(CreateNoteCommand command, CancellationToken cancellationToken)
    {
        if (!await knowledgeBases.ExistsAsync(command.KnowledgeBaseId, cancellationToken))
            throw new KnowledgeBaseNotFoundException(command.KnowledgeBaseId);

        var note = Note.Create(
            command.KnowledgeBaseId,
            command.Title,
            command.Content,
            command.Category,
            command.Color,
            command.Tags,
            command.SourceDocumentId,
            DateTimeOffset.UtcNow,
            command.TopicId,
            command.Sources,
            command.SourceSessionId);
        await repository.AddAsync(note, cancellationToken);
        return NoteDto.FromDomain(note);
    }

    public async Task<NoteDto> UpdateAsync(Guid knowledgeBaseId, Guid noteId, UpdateNoteCommand command, CancellationToken cancellationToken)
    {
        var note = await LoadAsync(knowledgeBaseId, noteId, cancellationToken);
        note.Update(command.Title, command.Content, command.Category, command.Color, command.Tags,
            DateTimeOffset.UtcNow, command.TopicId, command.Sources);
        await repository.UpdateAsync(note, cancellationToken);
        return NoteDto.FromDomain(note);
    }

    /// <summary>
    /// 批量导出为 Markdown。<paramref name="noteIds"/> 为空时导出整个知识库的笔记，
    /// 顺序保持与列表一致（置顶在前、最近更新优先）。无效 ID 直接忽略，不让整次导出失败。
    /// </summary>
    public async Task<NoteExport> ExportAsync(
        Guid knowledgeBaseId,
        IReadOnlyCollection<Guid>? noteIds,
        CancellationToken cancellationToken)
    {
        var notes = await ListAsync(knowledgeBaseId, null, null, cancellationToken);
        var selected = noteIds is { Count: > 0 }
            ? notes.Where(note => noteIds.Contains(note.Id)).ToList()
            : notes.ToList();

        if (selected.Count == 0)
            throw new NoteValidationException("没有可导出的笔记。");

        return new NoteExport(NoteExportFormatter.FormatMarkdown(selected), $"学习笔记-{DateTimeOffset.Now:yyyyMMdd-HHmm}.md", selected.Count);
    }

    public async Task<NoteDto> SetPinnedAsync(Guid knowledgeBaseId, Guid noteId, bool pinned, CancellationToken cancellationToken)
    {
        var note = await LoadAsync(knowledgeBaseId, noteId, cancellationToken);
        note.SetPinned(pinned, DateTimeOffset.UtcNow);
        await repository.UpdateAsync(note, cancellationToken);
        return NoteDto.FromDomain(note);
    }

    public async Task DeleteAsync(Guid noteId, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteAsync(noteId, knowledgeBaseId, cancellationToken);
        if (!deleted) throw new NoteNotFoundException(noteId);
    }

    public Task<int> CountAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
        repository.CountAsync(knowledgeBaseId, cancellationToken);

    private async Task<Note> LoadAsync(Guid knowledgeBaseId, Guid noteId, CancellationToken cancellationToken)
    {
        var note = await repository.GetAsync(noteId, cancellationToken) ?? throw new NoteNotFoundException(noteId);
        if (note.KnowledgeBaseId != knowledgeBaseId) throw new NoteNotFoundException(noteId);
        return note;
    }
}
