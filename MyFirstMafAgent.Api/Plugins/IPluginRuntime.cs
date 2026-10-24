using System.Text.Json.Nodes;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 同步与定时调度需要的插件运行时视图。
///
/// <para>
/// 为什么不直接依赖 <see cref="PluginManager"/>：它同时管进程、安装、版本、权限，
/// 而同步用例只需要"调用一个能力"和"这个插件是否还该被调度"。
/// 把这两个动作窄化成端口后，同步逻辑可以脱离真实子进程、文件系统和数据库做单测——
/// 这正是它能覆盖到"游标不倒退""批次超限"这类边界的前提。
/// </para>
/// </summary>
public interface IPluginRuntime
{
    /// <summary>调用一项能力；语义与 <see cref="PluginManager.InvokeAsync"/> 完全一致。</summary>
    Task<PluginInvocationResult> InvokeAsync(
        string pluginId,
        string capabilityId,
        JsonNode? arguments,
        PluginInvocationContext context,
        CancellationToken cancellationToken);

    /// <summary>定时同步是否应继续为该插件排队（已安装且未被用户停用）。</summary>
    bool IsScheduledSyncEligible(string pluginId);
}
