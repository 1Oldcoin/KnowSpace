using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Companions;

/// <summary>一次伴侣历史向量化的结果。</summary>
/// <param name="Indexed">本次新写入的向量数。</param>
/// <param name="Pending">仍在排队（受批量上限截至）的消息数估算。</param>
/// <param name="Warning">向量模型不可用等降级原因；null 表示一切正常。</param>
public sealed record CompanionIndexOutcome(int Indexed, int Failed, string? Warning);

/// <summary>
/// 伴侣聊天历史的向量化与向量检索。
///
/// <para>
/// 复用知识库已经注册好的默认向量模型，但**只写 <c>companion_message_vectors</c>**，
/// 不进任何 <c>kb_*</c> 表。理由很直接：伴侣记录是私人对话，混进资料索引之后
/// "问我的资料" 会把私聊当证据引用出来，等于把两件事的边界弄丢了。
/// </para>
///
/// <para>
/// 检索只命中**同一份导入记录里、对方说的**消息：<c>profile_id</c> 与 <c>sender</c> 都是必需过滤。
/// 少了前者两个人的聊天会互相串，少了后者"TA 说过什么"会变成"我们说过什么"。
/// </para>
///
/// <para>
/// 向量模型不可用时不抛异常：返回 null，由调用方回落到词面检索。
/// 伴侣对话不能因为没装 embedding 模型就整体不可用。
/// </para>
/// </summary>
public sealed class CompanionMemoryIndexer(
    CompanionStore store,
    EmbeddingSpecService specService,
    ISettingsRepository settingsRepository,
    IEmbeddingGeneratorProvider generatorProvider,
    ILogger<CompanionMemoryIndexer> logger)
{
    /// <summary>单条消息送去向量化的最大字符数（一条长语音转写可能上万字）。</summary>
    private const int MaxEmbeddingChars = 1_000;

    /// <summary>单次调用最多处理多少条消息：导入后是后台任务，不能一次把几万条全塞进去。</summary>
    public const int DefaultBatchSize = 2_000;

    /// <summary>
    /// 给某个画像补齐向量。返回本次写了多少条。可重复调用：已索引的消息会被跳过。
    /// </summary>
    public async Task<CompanionIndexOutcome> IndexAsync(
        Guid profileId,
        string sender,
        int batchSize,
        CancellationToken cancellationToken)
    {
        EmbeddingSpec spec;
        IEmbeddingGenerator generator;
        try
        {
            spec = await ResolveSpecAsync(cancellationToken);
            generator = generatorProvider.GetOrCreate(spec);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Companion indexing unavailable; history stays keyword-searchable only.");
            return new CompanionIndexOutcome(0, 0, exception.Message);
        }

        var sources = await store.ListMessagesForVectorAsync(
            profileId, sender, spec.Fingerprint, batchSize <= 0 ? DefaultBatchSize : batchSize, cancellationToken);

        if (sources.Count == 0) return new CompanionIndexOutcome(0, 0, null);

        var vectors = new List<CompanionMessageVector>(sources.Count);
        var failed = 0;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = source.Content.Length > MaxEmbeddingChars
                    ? source.Content[..MaxEmbeddingChars]
                    : source.Content;

                var embedding = await generator.CreateEmbeddingAsync(text, cancellationToken);
                if (embedding.Length != spec.Dimensions)
                {
                    failed++;
                    logger.LogWarning(
                        "Companion message {MessageId} embedding dimension {Actual} does not match spec {Expected}",
                        source.MessageId, embedding.Length, spec.Dimensions);
                    continue;
                }

                vectors.Add(new CompanionMessageVector(source.MessageId, spec.Id, spec.Fingerprint, spec.Dimensions, embedding));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogWarning(exception, "Failed to embed companion message {MessageId}", source.MessageId);
            }
        }

        if (vectors.Count > 0) await store.UpsertMessageVectorsAsync(vectors, cancellationToken);

        logger.LogInformation(
            "Companion profile {ProfileId}: indexed {Indexed} message vector(s), {Failed} failed, batch {Batch}",
            profileId, vectors.Count, failed, sources.Count);

        return new CompanionIndexOutcome(vectors.Count, failed, null);
    }

    /// <summary>查询向量检索；不可用时返回 null（调用方回落词面检索）。</summary>
    public async Task<IReadOnlyList<CompanionMessageVectorMatch>?> TrySearchAsync(
        Guid profileId,
        string sender,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            var spec = await ResolveSpecAsync(cancellationToken);
            var generator = generatorProvider.GetOrCreate(spec);

            var embedding = await generator.CreateEmbeddingAsync(query, cancellationToken);
            if (embedding.Length != spec.Dimensions) return null;

            return await store.SearchMessageVectorsAsync(
                profileId, sender, embedding, spec.Fingerprint, limit, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Companion vector search unavailable; falling back to keyword search.");
            return null;
        }
    }

    /// <summary>给界面用：已索引条数（当前模型下）。</summary>
    public async Task<int> CountIndexedAsync(Guid profileId, CancellationToken cancellationToken)
    {
        try
        {
            var spec = await ResolveSpecAsync(cancellationToken);
            return await store.CountVectorsAsync(profileId, spec.Fingerprint, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Companion vector count unavailable.");
            return 0;
        }
    }

    /// <summary>
    /// 解析向量规格：**优先用设置里选定的默认规格**，再回落到配置默认与最早注册的规格。
    /// 若这里直接取最早注册的规格，用户在设置页切换向量模型后伴侣记忆仍会用旧模型，
    /// 于是「已索引 N 条」会显示成与检索实际使用的模型不一致的数字。
    /// </summary>
    private async Task<EmbeddingSpec> ResolveSpecAsync(CancellationToken cancellationToken)
    {
        var stored = await settingsRepository.GetAsync(cancellationToken);
        return await specService.ResolveDefaultAsync(stored?.Embedding?.DefaultSpecId, cancellationToken);
    }
}
