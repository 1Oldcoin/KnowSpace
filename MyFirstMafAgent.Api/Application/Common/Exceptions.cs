namespace MyFirstMafAgent.Api.Application.Common;

public class NotFoundException(string message) : Exception(message);

public class ConflictException(string message) : Exception(message);

public sealed class KnowledgeBaseNotFoundException(Guid knowledgeBaseId)
    : NotFoundException($"知识库 {knowledgeBaseId} 不存在。");
