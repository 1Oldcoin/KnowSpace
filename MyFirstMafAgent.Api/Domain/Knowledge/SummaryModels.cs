namespace MyFirstMafAgent.Api.Domain.Knowledge;

public sealed record ModelEndpointConfig(string Endpoint, string Model, string ApiKey);

/// <summary>按知识库生效的摘要模型配置；为空时回退到全局聊天模型设置。</summary>
public sealed record SummaryModelBinding(
    bool UseCloudApi,
    ModelEndpointConfig? Local,
    ModelEndpointConfig? Online);

public sealed record SummaryRecord(
    Guid Id,
    Guid DocumentId,
    string ContentHash,
    string ModelFingerprint,
    string Summary,
    DateTimeOffset CreatedAt);

public interface IModelBindingStore
{
    Task<Guid?> GetEmbeddingSpecIdAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task SetEmbeddingSpecIdAsync(Guid knowledgeBaseId, Guid? embeddingSpecId, CancellationToken cancellationToken);

    Task<SummaryModelBinding?> GetSummaryBindingAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task SetSummaryBindingAsync(Guid knowledgeBaseId, SummaryModelBinding? binding, CancellationToken cancellationToken);
}

public interface ISummaryStore
{
    Task<SummaryRecord?> GetLatestAsync(Guid documentId, CancellationToken cancellationToken);

    Task<SummaryRecord?> FindAsync(Guid documentId, string contentHash, string modelFingerprint, CancellationToken cancellationToken);

    Task SaveAsync(SummaryRecord record, CancellationToken cancellationToken);

    /// <summary>同步资料的展示摘要，兼容现有资料列表 UI。</summary>
    Task SetDisplaySummaryAsync(Guid documentId, string summary, CancellationToken cancellationToken);
}

/// <summary>摘要模型客户端：按知识库解析模型配置，与向量索引生命周期完全解耦。</summary>
public interface ISummaryModelClient
{
    Task<string> GetModelFingerprintAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task<string> CompleteAsync(Guid knowledgeBaseId, string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}
