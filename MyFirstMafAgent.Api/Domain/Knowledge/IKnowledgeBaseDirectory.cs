namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>知识库目录端口：仅判断知识库是否存在，避免应用层依赖持久化实现。</summary>
public interface IKnowledgeBaseDirectory
{
    Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);
}
