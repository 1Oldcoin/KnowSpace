using System.Text.Json.Nodes;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>插件运行状态。</summary>
public enum PluginState
{
    /// <summary>已发现但未启动（未启用或校验失败）。</summary>
    Discovered = 0,

    Starting = 1,

    Running = 2,

    /// <summary>健康检查不通过，仍保留进程以便恢复。</summary>
    Degraded = 3,

    /// <summary>启动失败或进程异常退出。</summary>
    Failed = 4,

    Stopped = 5,

    /// <summary>manifest 声明了必填配置但用户尚未填写。</summary>
    NeedsConfiguration = 6,

    /// <summary>用户或配置明确停用。</summary>
    Disabled = 7,

    /// <summary>manifest 请求的权限尚未授予。</summary>
    PermissionRequired = 8,

    /// <summary>
    /// 正在排空：不再接受新调用，等已发出的调用跑完后再停进程。
    /// 只出现在升级/回滚/停用这类“要换代码或退出”的过程里。
    /// </summary>
    Draining = 9
}

/// <summary>
/// 插件目录的解析结果：manifest + 校验 + 入口可执行文件 + 版本布局。
/// </summary>
/// <param name="Directory">当前生效的包目录（含 manifest.json）。</param>
/// <param name="PluginRootDirectory">插件根目录（含 <c>versions/</c> 与 <c>active.json</c>）。</param>
/// <param name="ActiveVersion">生效版本；扁平布局或读取失败时为 null（本地版本）。</param>
/// <param name="InstalledVersions">已安装版本，按 SemVer 降序。</param>
public sealed record PluginManifestLoadResult(
    string Directory,
    PluginManifest? Manifest,
    PluginManifestValidationResult Validation,
    bool EntryExists,
    string? EntryPath,
    string PluginRootDirectory,
    string? ActiveVersion,
    IReadOnlyList<string> InstalledVersions)
{
    public bool IsUsable => Manifest is not null && Validation.IsValid && EntryExists;

    public string Id => Manifest?.Id ?? Path.GetFileName(PluginRootDirectory);
}

/// <summary>面向 API 的插件状态快照。</summary>
public sealed record PluginStatusSnapshot(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string State,
    string Directory,
    IReadOnlyList<string> Capabilities,
    string? Health,
    string? LastError,
    bool Enabled,
    string? ActiveVersion,
    IReadOnlyList<string> InstalledVersions,
    IReadOnlyList<string> GrantedPermissions,
    IReadOnlyList<string> MissingPermissions,
    string? Integrity,
    bool Configured,
    int InFlightInvocations = 0,
    bool Draining = false,
    string? Isolation = null);

/// <summary>单次能力调用结果，包含耗时便于审计。</summary>
public sealed record PluginInvocationResult(
    string PluginId,
    string CapabilityId,
    JsonNode? Result,
    long ElapsedMilliseconds);

/// <summary>
/// 摄取结果统计。撤回已真实生效（删除资料），因此不再有 <c>RetractionDeferred</c>。
/// <see cref="Ingested"/> 保留为“新建 + 更新”的汇总，便于旧调用方与日志平滑过渡。
/// </summary>
public sealed record PluginIngestionOutcome(
    int Created,
    int Updated,
    int Skipped,
    int Retracted,
    int Invalid,
    int QuotaRejected,
    IReadOnlyList<string> Diagnostics)
{
    public int Ingested => Created + Updated;

    public bool HasFailures => Invalid > 0 || QuotaRejected > 0;
}

/// <summary>
/// 一次调用上下文：谁发起、是否已确认。
/// 模型调用永远是“未确认”，因此不能触发需要确认的副作用能力。
/// </summary>
public sealed record PluginInvocationContext(string Actor, bool Confirmed)
{
    public static readonly PluginInvocationContext System = new(PluginActors.System, true);

    public static PluginInvocationContext FromApi(bool confirmed) => new(PluginActors.Api, confirmed);

    public static PluginInvocationContext FromAgent() => new(PluginActors.Agent, false);
}

/// <summary>
/// 重新扫描插件目录的结果。
/// <para>
/// <see cref="Discovered"/> 是这次扫描看到的全部插件（含已在运行的），
/// <see cref="Started"/> / <see cref="Failed"/> 只包含本次真正尝试启动的，
/// <see cref="Removed"/> 是目录已消失、被清理掉的。
/// </para>
/// </summary>
public sealed record PluginRescanResult(
    IReadOnlyList<string> Discovered,
    IReadOnlyList<string> Started,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Removed);
