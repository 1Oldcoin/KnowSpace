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
    Guid? SourceDocumentId);

public sealed record UpdateNoteCommand(
    string Title,
    string Content,
    string? Category,
    string? Color,
    IReadOnlyList<string>? Tags);

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
    DateTimeOffset UpdatedAt)
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
        note.UpdatedAt);
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
        CancellationToken cancellationToken)
    {
        if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken))
            throw new KnowledgeBaseNotFoundException(knowledgeBaseId);

        var notes = await repository.ListAsync(knowledgeBaseId, cancellationToken);
        var normalizedQuery = query?.Trim();
        var normalizedCategory = category?.Trim();

        IEnumerable<Note> filtered = notes;
        if (!string.IsNullOrEmpty(normalizedCategory))
            filtered = filtered.Where(note => string.Equals(note.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(normalizedQuery))
            filtered = filtered.Where(note =>
                note.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || note.Content.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || note.Tags.Any(tag => tag.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)));

        return filtered
            .OrderByDescending(note => note.IsPinned)
            .ThenByDescending(note => note.UpdatedAt)
            .Select(NoteDto.FromDomain)
            .ToList();
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
            DateTimeOffset.UtcNow);
        await repository.AddAsync(note, cancellationToken);
        return NoteDto.FromDomain(note);
    }

    public async Task<NoteDto> UpdateAsync(Guid knowledgeBaseId, Guid noteId, UpdateNoteCommand command, CancellationToken cancellationToken)
    {
        var note = await LoadAsync(knowledgeBaseId, noteId, cancellationToken);
        note.Update(command.Title, command.Content, command.Category, command.Color, command.Tags, DateTimeOffset.UtcNow);
        await repository.UpdateAsync(note, cancellationToken);
        return NoteDto.FromDomain(note);
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
