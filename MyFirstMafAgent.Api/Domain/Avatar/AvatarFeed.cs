namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// 投喂页的一条对话记录。用户的原文和分身的整理回执成对出现，
/// 让“投喂”像聊天一样自然，而不是填表。
/// </summary>
public sealed record AvatarFeedEntry(
    long Id,
    string Role,
    string Content,
    string? Payload,
    DateTimeOffset CreatedAt)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";
}

/// <summary>一条记忆的向量记录，用于 pgvector 检索。</summary>
public sealed record AvatarMemoryVector(
    Guid MemoryId,
    Guid EmbeddingSpecId,
    float[] Embedding,
    string SpecFingerprint,
    int Dimensions);

/// <summary>记忆检索命中：保留向量相似度、词面得分与重排依据，便于解释排序。</summary>
public sealed record AvatarMemoryHit(
    AvatarMemory Memory,
    double Score,
    bool MatchedByVector,
    double VectorScore = 0,
    double LexicalScore = 0,
    string Reason = "");

/// <summary>向量检索的一行结果。</summary>
public sealed record AvatarMemoryVectorMatch(Guid MemoryId, double Similarity);
