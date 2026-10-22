namespace MyFirstMafAgent.Api.Contracts;

public sealed record NewCompanionSessionRequest(string? Title);
public sealed record CompanionChatRequest(string Message);