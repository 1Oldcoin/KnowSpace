using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>一次平台条目要执行的动作。</summary>
public enum PlatformItemAction
{
    /// <summary>第一次见到：新建资料。</summary>
    Create,

    /// <summary>内容变了：替换资料源文件并重新解析（产生新的内容版本）。</summary>
    Update,

    /// <summary>内容没变：跳过，不做任何写入。</summary>
    Skip,

    /// <summary>平台已撤回且本地有资料：删除资料并保留墓碑映射。</summary>
    Retract,

    /// <summary>平台说已撤回，但本地从未摄取过：只记墓碑，防止旧批次复活。</summary>
    Tombstone,

    /// <summary>条目本身不合法（结构错误、内容为空）。</summary>
    Reject
}

/// <summary>
/// 平台条目的幂等判定。故意做成纯函数：去重与撤回的规则最容易出错，
/// 必须能不启动数据库就把矩阵穷举出来。
///
/// <para>
/// 判定表（行=本地映射，列=本次条目）：
/// </para>
///
/// <list type="table">
///   <item><description>无映射 + 正常 → <see cref="PlatformItemAction.Create"/></description></item>
///   <item><description>无映射 + 撤回 → <see cref="PlatformItemAction.Tombstone"/></description></item>
///   <item><description>活跃 + 正常 + 同哈希 → <see cref="PlatformItemAction.Skip"/></description></item>
///   <item><description>活跃 + 正常 + 异哈希 → <see cref="PlatformItemAction.Update"/></description></item>
///   <item><description>活跃 + 撤回 → <see cref="PlatformItemAction.Retract"/></description></item>
///   <item><description>撤回（墓碑）+ 撤回 → <see cref="PlatformItemAction.Skip"/></description></item>
///   <item><description>撤回（墓碑）+ 正常 → <see cref="PlatformItemAction.Create"/></description></item>
/// </list>
/// </summary>
public static class PlatformItemDecision
{
    public static PlatformItemAction Decide(PlatformIngestionItem item, PlatformItemLink? existing)
    {
        var errors = item.Validate();
        if (errors.Count > 0) return PlatformItemAction.Reject;

        if (existing is null)
            return item.Retracted ? PlatformItemAction.Tombstone : PlatformItemAction.Create;

        if (item.Retracted)
            return existing.IsRetracted ? PlatformItemAction.Skip : PlatformItemAction.Retract;

        // 本地是墓碑（曾被撤回或从未有资料）：平台又给出来了，视为重新发布。
        if (existing.IsRetracted || existing.DocumentId is null)
            return PlatformItemAction.Create;

        var hash = NormalizedHash(item);
        if (string.Equals(existing.ContentHash, hash, StringComparison.Ordinal))
            return PlatformItemAction.Skip;

        return PlatformItemAction.Update;
    }

    /// <summary>
    /// 规范化内容哈希：平台没给就算；给了但大小写/空白不一致也按核心算法统一，
    /// 避免"同一个内容因为哈希写法不同而被反复重新摄取"。
    /// </summary>
    public static string NormalizedHash(PlatformIngestionItem item) =>
        string.IsNullOrWhiteSpace(item.ContentHash)
            ? PlatformIngestionItem.ComputeContentHash(item.Content)
            : item.ContentHash.Trim().ToLowerInvariant();
}
