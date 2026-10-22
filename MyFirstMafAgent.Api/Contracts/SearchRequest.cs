namespace MyFirstMafAgent.Api.Contracts;

public sealed record SearchRequest(string Query, int Limit = 5, Guid? KnowledgeBaseId = null);
