namespace MyFirstMafAgent.Api.Domain.Notes;

/// <summary>笔记仓储端口（由基础设施层实现）。</summary>
public interface INoteRepository
{
    /// <summary>列出一个知识库的全部笔记（导出、计数等确实需要全量的场景）。</summary>
    Task<IReadOnlyList<Note>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    /// <summary>按条件列出笔记；过滤在存储层完成，不把正文搬进内存。</summary>
    Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken cancellationToken);

    Task<Note?> GetAsync(Guid noteId, CancellationToken cancellationToken);

    Task<int> CountAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task AddAsync(Note note, CancellationToken cancellationToken);

    Task UpdateAsync(Note note, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid noteId, Guid knowledgeBaseId, CancellationToken cancellationToken);
}
