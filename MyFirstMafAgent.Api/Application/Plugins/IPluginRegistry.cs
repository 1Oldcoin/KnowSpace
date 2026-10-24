namespace MyFirstMafAgent.Api.Application.Plugins;

/// <summary>
/// 插件安装态与审计的持久化端口。
///
/// <para>
/// 运行时状态（进程、健康）是易失的，不入库；这里只保存"用户意图"：
/// 是否启用、配置内容、已授予的权限，以及不可篡改的调用审计。
/// </para>
/// </summary>
public interface IPluginRegistry
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginRegistration>> GetAllAsync(CancellationToken cancellationToken);

    Task<PluginRegistration?> GetAsync(string pluginId, CancellationToken cancellationToken);

    /// <summary>按 pluginId 新增或覆盖安装态。</summary>
    Task SaveAsync(PluginRegistration registration, CancellationToken cancellationToken);

    Task DeleteAsync(string pluginId, CancellationToken cancellationToken);

    Task RecordAuditAsync(PluginAuditRecord record, CancellationToken cancellationToken);

    /// <summary>按时间倒序查询审计；<paramref name="pluginId"/> 为空表示全部插件。</summary>
    Task<IReadOnlyList<PluginAuditRecord>> QueryAuditAsync(
        string? pluginId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>插件的持久化安装态。除 <see cref="LastKnownState"/> 与 <see cref="LastError"/> 外均由用户操作写入。</summary>
public sealed record PluginRegistration(
    string PluginId,
    string Name,
    string Version,
    string Publisher,
    bool Enabled,
    string ConfigJson,
    IReadOnlyList<string> GrantedPermissions,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    string? LastKnownState,
    string? LastError,
    IReadOnlyList<Guid> AllowedKnowledgeBaseIds,
    int MaxDocuments)
{
    /// <summary>
    /// 该插件能否写入指定知识库。<paramref name="allowedKnowledgeBaseIds"/> 为空表示不限制。
    /// </summary>
    public bool CanWriteTo(Guid knowledgeBaseId) =>
        AllowedKnowledgeBaseIds.Count == 0
        || AllowedKnowledgeBaseIds.Contains(knowledgeBaseId);

    /// <summary>新建安装态：默认启用、未授予任何权限、知识库不限制、配额跟随全局默认。</summary>
    public static PluginRegistration Create(
        string pluginId,
        string name,
        string version,
        string publisher,
        bool enabled,
        string configJson,
        IReadOnlyList<string> grantedPermissions) => new(
            pluginId,
            name,
            version,
            publisher,
            enabled,
            configJson,
            grantedPermissions,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            [],
            0);
}

/// <summary>一条插件审计记录。Id 为 0 表示待写入的新记录。</summary>
public sealed record PluginAuditRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string PluginId,
    string Action,
    string? CapabilityId,
    string Actor,
    string Outcome,
    long DurationMs,
    string? Detail);

/// <summary>审计动作常量，避免各处拼字符串。</summary>
public static class PluginAuditActions
{
    public const string Install = "install";
    public const string Uninstall = "uninstall";
    public const string Rollback = "rollback";
    public const string Enable = "enable";
    public const string Disable = "disable";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Configure = "configure";
    public const string Grant = "grant";
    public const string Scope = "scope";
    public const string Invoke = "invoke";
    public const string Sync = "sync";
    public const string HealthCheck = "health";
    public const string IntegrityRejected = "integrity-rejected";

    /// <summary>升级前主动排空（不再接受新调用，等待在途调用结束）。</summary>
    public const string Drain = "drain";

    /// <summary>受信任发布者公钥的增删。</summary>
    public const string TrustKey = "trust-key";
}

/// <summary>审计结果分类。</summary>
public static class PluginAuditOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Denied = "denied";
    public const string Cancelled = "cancelled";
}

/// <summary>调用方身份，用于区分模型触发与管理端点触发。</summary>
public static class PluginActors
{
    public const string Api = "api";
    public const string Agent = "agent";
    public const string System = "system";
}
