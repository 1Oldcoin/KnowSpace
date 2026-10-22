namespace MyFirstMafAgent.Api.Documents;

public sealed record DocumentUploadResponse(
    string Id,
    string FileName,
    string Extension,
    long SizeBytes,
    string Status,
    string? Error = null);
