namespace MyFirstMafAgent.Api.Contracts;

public sealed record CreateNoteRequest(
    string Title,
    string Content,
    string? Category,
    string? Color,
    List<string>? Tags,
    Guid? SourceDocumentId);

public sealed record UpdateNoteRequest(
    string Title,
    string Content,
    string? Category,
    string? Color,
    List<string>? Tags);

public sealed record PinNoteRequest(bool Pinned);
