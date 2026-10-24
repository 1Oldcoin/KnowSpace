namespace MyFirstMafAgent.Api.Contracts;

/// <summary>笔记引用的一条资料依据（写入请求里的一层）。</summary>
public sealed record NoteSourceRequest(
    Guid DocumentId,
    string? FileName,
    int? ChunkIndex,
    double? Similarity,
    string? Snippet);

public sealed record CreateNoteRequest(
    string Title,
    string Content,
    string? Category,
    string? Color,
    List<string>? Tags,
    Guid? SourceDocumentId,
    Guid? TopicId = null,
    List<NoteSourceRequest>? Sources = null,
    Guid? SourceSessionId = null);

public sealed record UpdateNoteRequest(
    string Title,
    string Content,
    string? Category,
    string? Color,
    List<string>? Tags,
    Guid? TopicId = null,
    List<NoteSourceRequest>? Sources = null);

public sealed record PinNoteRequest(bool Pinned);

/// <summary>批量导出笔记请求；<see cref="NoteIds"/> 为空表示导出全部。</summary>
public sealed record ExportNotesRequest(List<Guid>? NoteIds);
