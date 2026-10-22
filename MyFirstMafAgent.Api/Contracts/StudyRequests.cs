namespace MyFirstMafAgent.Api.Contracts;

public sealed record StudyCheckinRequest(int Minutes, string? Note);
