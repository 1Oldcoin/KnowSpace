using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Domain.Avatar;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>一次向量化结果。</summary>
public sealed record AvatarIndexOutcome(int Indexed, int Pending, string? Warning);

/// <summary>
/// 分身记忆的向量化。复用知识库已注册的默认向量模型，但只写入 avatar_memory_vectors，
/// 不进入任何 kb_* 表，保证分身与知识库的隔离。
/// 向量化失败不会影响记忆入库：记忆保持 pending，用户可稍后重试。
/// </summary>
public sealed class AvatarMemoryIndexer(
    IAvatarRepository repository,
    EmbeddingSpecService specService,
    IEmbeddingGeneratorProvider generatorProvider,
    ILogger<AvatarMemoryIndexer> logger)
{
    private const int MaxEmbeddingChars = 4_000;

    public async Task<AvatarIndexOutcome> IndexAsync(
        IReadOnlyList<AvatarMemory> memories,
        CancellationToken cancellationToken)
    {
        if (memories.Count == 0) return new AvatarIndexOutcome(0, 0, null);

        try
        {
            var spec = await specService.GetDefaultAsync(cancellationToken);
            var generator = generatorProvider.GetOrCreate(spec);
            var vectors = new List<AvatarMemoryVector>();

            foreach (var memory in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var text = memory.ToRetrievalText();
                    if (text.Length > MaxEmbeddingChars) text = text[..MaxEmbeddingChars];
                    var embedding = await generator.CreateEmbeddingAsync(text, cancellationToken);
                    if (embedding.Length != spec.Dimensions)
                    {
                        logger.LogWarning(
                            "Avatar memory {MemoryId} embedding dimension {Actual} does not match spec {Expected}",
                            memory.Id, embedding.Length, spec.Dimensions);
                        await repository.MarkMemoryVectorStateAsync(memory.Id, AvatarMemory.VectorFailed, cancellationToken);
                        memory.MarkVectorState(AvatarMemory.VectorFailed, DateTimeOffset.UtcNow);
                        continue;
                    }

                    vectors.Add(new AvatarMemoryVector(
                        memory.Id, spec.Id, embedding, spec.Fingerprint, spec.Dimensions));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to embed avatar memory {MemoryId}", memory.Id);
                    await repository.MarkMemoryVectorStateAsync(memory.Id, AvatarMemory.VectorFailed, cancellationToken);
                    memory.MarkVectorState(AvatarMemory.VectorFailed, DateTimeOffset.UtcNow);
                }
            }

            if (vectors.Count > 0)
                await repository.UpsertMemoryVectorsAsync(vectors, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            foreach (var vector in vectors)
            {
                await repository.MarkMemoryVectorStateAsync(vector.MemoryId, AvatarMemory.VectorReady, cancellationToken);
                memories.FirstOrDefault(memory => memory.Id == vector.MemoryId)
                    ?.MarkVectorState(AvatarMemory.VectorReady, now);
            }

            return new AvatarIndexOutcome(vectors.Count, memories.Count - vectors.Count, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 没有可用向量模型时不让投喂失败：记忆已经入库，保持 pending。
            logger.LogWarning(exception, "Avatar memory indexing unavailable; memories stay pending.");
            return new AvatarIndexOutcome(0, memories.Count, exception.Message);
        }
    }

    /// <summary>检索查询向量；向量模型不可用时返回 null，由词面检索兜底。</summary>
    public async Task<IReadOnlyList<AvatarMemoryVectorMatch>?> TrySearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            var spec = await specService.GetDefaultAsync(cancellationToken);
            var generator = generatorProvider.GetOrCreate(spec);
            var embedding = await generator.CreateEmbeddingAsync(query, cancellationToken);
            if (embedding.Length != spec.Dimensions) return null;

            return await repository.SearchMemoryVectorsAsync(
                embedding, spec.Fingerprint, limit, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Avatar vector search unavailable; falling back to lexical ranking.");
            return null;
        }
    }

    public async Task<AvatarReindexResultDto> ReindexAsync(
        bool all,
        int limit,
        CancellationToken cancellationToken)
    {
        var memories = all
            ? await repository.ListMemoriesAsync(limit, cancellationToken)
            : await repository.ListMemoriesPendingVectorAsync(limit, cancellationToken);

        var outcome = await IndexAsync(memories, cancellationToken);
        var total = await repository.CountMemoriesAsync(cancellationToken);
        var pending = await repository.CountPendingMemoryVectorsAsync(cancellationToken);

        return new AvatarReindexResultDto(outcome.Indexed, pending, total, outcome.Warning);
    }
}
