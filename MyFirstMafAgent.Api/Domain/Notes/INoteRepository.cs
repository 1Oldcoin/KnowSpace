namespace MyFirstMafAgent.Api.Domain.Notes;

/// <summary>笔记仓储端口（由基础设施层实现）。</summary>
public interface INoteRepository
{
    Task<IReadOnlyList<Note>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task<Note?> GetAsync(Guid noteId, CancellationToken cancellationToken);

    Task<int> CountAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task AddAsync(Note note, CancellationToken cancellationToken);

    Task UpdateAsync(Note note, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid noteId, Guid knowledgeBaseId, CancellationToken cancellationToken);
}
