namespace MyFirstMafAgent.Api.Contracts;

public sealed record RenameKnowledgeChatSessionRequest(string? Title);

public sealed record DeleteKnowledgeChatSessionsRequest(IReadOnlyList<Guid>? SessionIds);
