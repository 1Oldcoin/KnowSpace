namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>知识库的最简标识（用于授权同意页的知识库勾选列表）。</summary>
public sealed record KnowledgeBaseSummary(Guid Id, string Name);

/// <summary>知识库目录端口：判断知识库是否存在、列出可选知识库，避免应用层依赖持久化实现。</summary>
public interface IKnowledgeBaseDirectory
{
    Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    /// <summary>列出全部知识库（按名称排序）。授权同意页据此渲染勾选项。</summary>
    Task<IReadOnlyList<KnowledgeBaseSummary>> ListAsync(CancellationToken cancellationToken);
}
