namespace MyFirstMafAgent.Api.Contracts;

public sealed record CreateTopicRequest(string Name, string? Color);
public sealed record UpdateTopicRequest(string Name, string? Color);
public sealed record AssignDocumentTopicRequest(Guid? TopicId);
