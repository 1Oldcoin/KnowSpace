namespace MyFirstMafAgent.Api.Contracts;

public sealed record CreateUploadSessionRequest(
    Guid KnowledgeBaseId,
    string FileName,
    long SizeBytes,
    string? ContentType);

public sealed record UploadSessionResponse(
    string Id,
    string FileName,
    long SizeBytes,
    long UploadedBytes,
    int ChunkSizeBytes,
    string Status,
    string? DocumentId);

public sealed record UploadChunkResponse(long UploadedBytes);
