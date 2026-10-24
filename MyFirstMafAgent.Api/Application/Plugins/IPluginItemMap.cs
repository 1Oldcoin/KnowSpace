namespace MyFirstMafAgent.Api.Application.Plugins;

/// <summary>
/// 平台条目映射与同步游标的持久化端口。
///
/// <para>
/// 与 <see cref="IPluginRegistry"/> 分开：注册表回答"用户希望插件是什么状态"，
/// 这里回答"哪些平台内容已经进过库、同步到哪一页了"。
/// </para>
/// </summary>
public interface IPluginItemMap
{
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 按 (pluginId, source, externalId, knowledgeBaseId) 查映射；未见过返回 null。
    ///
    /// <para>
    /// 键里带知识库：同一条平台内容镜像到两个知识库时，两边的映射、配额与撤回
    /// 必须互不影响，否则一边撤回会把另一边的资料删掉。
    /// </para>
    /// </summary>
    Task<PlatformItemLink?> GetAsync(
        string pluginId,
        string source,
        string externalId,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken);

    /// <summary>写入或覆盖映射（以 pluginId + source + externalId + knowledgeBaseId 为键）。</summary>
    Task SaveAsync(PlatformItemLink link, CancellationToken cancellationToken);

    /// <summary>按插件列出映射，可选按知识库过滤。</summary>
    Task<IReadOnlyList<PlatformItemLink>> ListAsync(
        string pluginId,
        Guid? knowledgeBaseId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>统计某插件在指定知识库下的活跃条目数，用于配额。</summary>
    Task<int> CountActiveAsync(string pluginId, Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task DeleteByPluginAsync(string pluginId, CancellationToken cancellationToken);
}

/// <summary>同步游标存储。</summary>
public interface IPluginSyncStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>按作用域键读取游标；未同步过返回 null。</summary>
    Task<PluginSyncState?> GetByScopeAsync(string scopeKey, CancellationToken cancellationToken);

    Task SaveAsync(PluginSyncState state, CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginSyncState>> ListAsync(string pluginId, CancellationToken cancellationToken);

    /// <summary>
    /// 列出所有声明了自动同步间隔的游标（定时同步的补投来源）。
    /// 间隔为 0/null 的游标不会被返回——那代表“只手动同步”。
    /// </summary>
    Task<IReadOnlyList<PluginSyncState>> ListScheduledAsync(int limit, CancellationToken cancellationToken);

    Task DeleteAllForPluginAsync(string pluginId, CancellationToken cancellationToken);

    /// <summary>游标作用域键：同一插件、同一能力、同一知识库共享一条游标。</summary>
    static string BuildScopeKey(string pluginId, string capabilityId, Guid knowledgeBaseId) =>
        $"{pluginId}|{capabilityId}|{knowledgeBaseId:D}";
}

/// <summary>条目状态常量。</summary>
public static class PlatformItemStates
{
    /// <summary>正常在库。</summary>
    public const string Active = "active";

    /// <summary>平台已撤回（资料已删除），映射保留为墓碑，防止旧批次复活。</summary>
    public const string Retracted = "retracted";
}

/// <summary>
/// 一条 <c>source + externalId → documentId</c> 映射。
/// <see cref="DocumentId"/> 为空且状态为撤回时，表示墓碑（从未摄取过或已删除）。
/// </summary>
public sealed record PlatformItemLink(
    string PluginId,
    string Source,
    string ExternalId,
    Guid KnowledgeBaseId,
    Guid? DocumentId,
    string? ContentHash,
    string? Title,
    string State,
    int DocumentVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt)
{
    public bool IsRetracted => string.Equals(State, PlatformItemStates.Retracted, StringComparison.Ordinal);
}

/// <summary>一条同步游标。</summary>
public sealed record PluginSyncState(
    string ScopeKey,
    string PluginId,
    string CapabilityId,
    Guid KnowledgeBaseId,
    string? Cursor,
    int? NextSyncAfterSeconds,
    DateTimeOffset? LastSyncedAt,
    string? LastStatus,
    string? LastError,
    DateTimeOffset UpdatedAt)
{
    public static PluginSyncState Empty(string pluginId, string capabilityId, Guid knowledgeBaseId) => new(
        IPluginSyncStateStore.BuildScopeKey(pluginId, capabilityId, knowledgeBaseId),
        pluginId,
        capabilityId,
        knowledgeBaseId,
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.UtcNow);
}

/// <summary>同步结果状态。</summary>
public static class PluginSyncStatuses
{
    public const string Success = "success";

    /// <summary>有失败条目，但游标已推进（失败条目下次同步会重试）。</summary>
    public const string Partial = "partial";

    public const string Failed = "failed";
}
