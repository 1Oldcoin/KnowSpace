using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>平台连接器产出的条目进入核心的端口。</summary>
public interface IPluginIngestionSink
{
    Task<PluginIngestionOutcome> IngestAsync(
        string pluginId,
        PlatformIngestionBatch batch,
        Guid? knowledgeBaseId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 平台条目的幂等摄取编排：查映射 → 判定动作 → 走内容仓储 → 写回映射。
///
/// <para>三条不变量：</para>
/// <list type="bullet">
///   <item><description><b>不直接写表</b>：内容最终仍由核心摄取用例落到知识库（见 <see cref="IPluginContentStore"/>）。</description></item>
///   <item><description><b>幂等</b>：同一条平台内容重复同步不会产生重复资料；内容哈希未变直接跳过。</description></item>
///   <item><description><b>撤回是删除</b>：平台说删了就真删，并留下墓碑映射，旧批次无法复活。</description></item>
/// </list>
///
/// <para>
/// 内容变化走"替换源文件 + 重新排队"，资料 ID 保持不变，因此映射不会断；
/// 旧内容版本由索引生命周期按 Superseded 清理。
/// </para>
/// </summary>
public sealed class KnowledgePluginIngestionSink(
    IPluginContentStore content,
    IPluginItemMap itemMap,
    IPluginRegistry registry,
    IOptions<PluginOptions> options,
    ILogger<KnowledgePluginIngestionSink> logger) : IPluginIngestionSink
{
    public async Task<PluginIngestionOutcome> IngestAsync(
        string pluginId,
        PlatformIngestionBatch batch,
        Guid? knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        var targetKnowledgeBase = knowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;

        if (!await content.KnowledgeBaseExistsAsync(targetKnowledgeBase, cancellationToken).ConfigureAwait(false))
            throw new ArgumentException($"Knowledge base '{targetKnowledgeBase}' does not exist.", nameof(knowledgeBaseId));

        var registration = await registry.GetAsync(pluginId, cancellationToken).ConfigureAwait(false);

        // 知识库范围：插件只能写进用户授权的知识库。
        if (registration is not null && !registration.CanWriteTo(targetKnowledgeBase))
        {
            throw new PluginRpcException(
                PluginProtocol.ErrorPermissionDenied,
                $"插件 '{pluginId}' 未被授权写入知识库 '{targetKnowledgeBase}'。允许的知识库："
                + string.Join(", ", registration.AllowedKnowledgeBaseIds));
        }

        var quota = ResolveQuota(registration);
        var activeCount = await itemMap
            .CountActiveAsync(pluginId, targetKnowledgeBase, cancellationToken)
            .ConfigureAwait(false);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var retracted = 0;
        var invalid = 0;
        var quotaRejected = 0;
        var diagnostics = new List<string>();

        foreach (var item in batch.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = await LookupAsync(pluginId, item, targetKnowledgeBase, cancellationToken).ConfigureAwait(false);

            switch (PlatformItemDecision.Decide(item, existing))
            {
                case PlatformItemAction.Reject:
                    invalid++;
                    diagnostics.Add($"{Describe(item)}: {string.Join("; ", item.Validate())}");
                    continue;

                case PlatformItemAction.Skip:
                    skipped++;
                    continue;

                case PlatformItemAction.Tombstone:
                    await itemMap.SaveAsync(
                        BuildLink(pluginId, item, targetKnowledgeBase, documentId: null, hash: null,
                            PlatformItemStates.Retracted, existing),
                        cancellationToken).ConfigureAwait(false);
                    retracted++;
                    diagnostics.Add($"{Describe(item)}: 平台已撤回，本地无对应资料，已记录墓碑");
                    continue;

                case PlatformItemAction.Retract:
                    await RetractAsync(pluginId, item, existing!, cancellationToken).ConfigureAwait(false);
                    retracted++;
                    continue;

                case PlatformItemAction.Create:
                {
                    if (quota > 0 && activeCount >= quota)
                    {
                        quotaRejected++;
                        diagnostics.Add($"{Describe(item)}: 已达到单知识库条目上限 {quota}，本条被拒绝");
                        continue;
                    }

                    var documentId = await content.CreateAsync(item, targetKnowledgeBase, cancellationToken).ConfigureAwait(false);
                    if (documentId is null)
                    {
                        invalid++;
                        diagnostics.Add($"{Describe(item)}: 创建资料失败");
                        continue;
                    }

                    await itemMap.SaveAsync(
                        BuildLink(pluginId, item, targetKnowledgeBase, documentId,
                            PlatformItemDecision.NormalizedHash(item), PlatformItemStates.Active, existing),
                        cancellationToken).ConfigureAwait(false);

                    created++;
                    activeCount++;
                    continue;
                }

                case PlatformItemAction.Update:
                {
                    var outcome = await ReplaceAsync(
                        pluginId, item, existing!, targetKnowledgeBase, quota, activeCount, cancellationToken)
                        .ConfigureAwait(false);

                    switch (outcome)
                    {
                        case ReplaceOutcome.Replaced:
                            updated++;
                            break;

                        case ReplaceOutcome.Recreated:
                            // 原资料已不存在（被手工删除或换库）：重新落库，仍算同一条目。
                            updated++;
                            activeCount++;
                            diagnostics.Add($"{Describe(item)}: 原资料已不存在，已重新落库");
                            break;

                        case ReplaceOutcome.QuotaRejected:
                            quotaRejected++;
                            diagnostics.Add($"{Describe(item)}: 原资料已不存在且已达到单知识库条目上限 {quota}，本条被拒绝");
                            break;

                        default:
                            // 文档正在处理中：保留旧哈希，下次同步重新判定。
                            skipped++;
                            diagnostics.Add($"{Describe(item)}: 资料正在处理中，本次跳过，下次同步重试");
                            break;
                    }

                    continue;
                }
            }
        }

        logger.LogInformation(
            "Plugin {PluginId} ingestion into {KnowledgeBaseId}: created={Created} updated={Updated} skipped={Skipped} retracted={Retracted} invalid={Invalid} quotaRejected={QuotaRejected}",
            pluginId, targetKnowledgeBase, created, updated, skipped, retracted, invalid, quotaRejected);

        return new PluginIngestionOutcome(created, updated, skipped, retracted, invalid, quotaRejected, diagnostics);
    }

    /// <summary>单知识库配额：插件级设置优先，0 表示跟随全局默认（0 = 不限制）。</summary>
    private int ResolveQuota(PluginRegistration? registration) =>
        registration is { MaxDocuments: > 0 }
            ? registration.MaxDocuments
            : Math.Max(0, options.Value.DefaultMaxDocumentsPerPlugin);

    private async Task<PlatformItemLink?> LookupAsync(
        string pluginId,
        PlatformIngestionItem item,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.ExternalId)) return null;

        return await itemMap
            .GetAsync(pluginId, item.Source, item.ExternalId, knowledgeBaseId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>替换结果：区分“就地替换”“重新落库”“处理中跳过”“配额拒绝”。</summary>
    private enum ReplaceOutcome
    {
        Replaced,
        Recreated,
        Busy,
        QuotaRejected
    }

    private async Task<ReplaceOutcome> ReplaceAsync(
        string pluginId,
        PlatformIngestionItem item,
        PlatformItemLink existing,
        Guid knowledgeBaseId,
        int quota,
        int activeCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var replaced = await content
                .ReplaceAsync(existing.DocumentId!.Value, item, knowledgeBaseId, cancellationToken)
                .ConfigureAwait(false);

            if (!replaced) return ReplaceOutcome.Busy;
        }
        catch (ArgumentException exception)
        {
            // 资料已被用户手工删除（或映射指向其他知识库）：退化为重新创建，
            // 避免映射永久卡在失效 ID 上；但重建同样要受配额约束。
            logger.LogWarning(
                exception,
                "Document {DocumentId} for plugin item {Source}/{ExternalId} is gone; re-creating it",
                existing.DocumentId, item.Source, item.ExternalId);

            if (quota > 0 && activeCount >= quota)
                return ReplaceOutcome.QuotaRejected;

            var recreated = await content.CreateAsync(item, knowledgeBaseId, cancellationToken).ConfigureAwait(false);
            if (recreated is null) return ReplaceOutcome.Busy;

            await itemMap.SaveAsync(
                BuildLink(pluginId, item, knowledgeBaseId, recreated,
                    PlatformItemDecision.NormalizedHash(item), PlatformItemStates.Active, existing),
                cancellationToken).ConfigureAwait(false);

            return ReplaceOutcome.Recreated;
        }

        await itemMap.SaveAsync(
            BuildLink(pluginId, item, knowledgeBaseId, existing.DocumentId,
                PlatformItemDecision.NormalizedHash(item), PlatformItemStates.Active, existing),
            cancellationToken).ConfigureAwait(false);

        return ReplaceOutcome.Replaced;
    }

    private async Task RetractAsync(
        string pluginId,
        PlatformIngestionItem item,
        PlatformItemLink existing,
        CancellationToken cancellationToken)
    {
        if (existing.DocumentId is { } documentId)
        {
            await content.DeleteAsync(documentId, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Retracted plugin item {Source}/{ExternalId}: deleted document {DocumentId}",
                item.Source, item.ExternalId, documentId);
        }

        await itemMap.SaveAsync(
            BuildLink(pluginId, item, existing.KnowledgeBaseId, documentId: null, hash: null,
                PlatformItemStates.Retracted, existing),
            cancellationToken).ConfigureAwait(false);
    }

    private static PlatformItemLink BuildLink(
        string pluginId,
        PlatformIngestionItem item,
        Guid knowledgeBaseId,
        Guid? documentId,
        string? hash,
        string state,
        PlatformItemLink? existing) => new(
            pluginId,
            item.Source,
            item.ExternalId,
            knowledgeBaseId,
            documentId,
            hash,
            item.Title,
            state,
            // 0 表示"尚未发布内容版本"，与 kb_documents.content_version 的语义一致。
            0,
            existing?.FirstSeenAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static string Describe(PlatformIngestionItem item) => $"{item.Source}/{item.ExternalId}";
}
