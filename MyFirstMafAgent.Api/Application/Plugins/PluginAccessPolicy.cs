using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Application.Plugins;

/// <summary>一次启动/调用授权判定的结论。</summary>
public sealed record PluginAccessDecision(
    bool IsAllowed,
    string? Reason,
    IReadOnlyList<string> MissingItems)
{
    public static PluginAccessDecision Allow() => new(true, null, []);

    public static PluginAccessDecision Deny(string reason, IReadOnlyList<string> missing) =>
        new(false, reason, missing);
}

/// <summary>
/// 权限与副作用策略。纯函数，便于在单元测试里穷举矩阵。
///
/// <para>
/// 两条独立的闸门：
/// 1. <b>启动闸门</b>：manifest 声明的权限必须已被用户授予（除非开启自动授予）。
/// 2. <b>调用闸门</b>：<c>write-external</c> / <c>destructive</c> 能力需要显式确认；
///    需要确认的能力不会桥接给聊天模型，只能由管理端点带 <c>confirm</c> 调用。
/// </para>
///
/// <para>
/// 需要澄清的边界：权限是<b>声明与授权</b>机制，不是内核级强制。
/// 子进程与核心同用户同权限，真正的强制隔离需要受限令牌/容器（见文档"安全边界"）。
/// </para>
/// </summary>
public static class PluginAccessPolicy
{
    /// <summary>检查插件是否可以启动。</summary>
    public static PluginAccessDecision CheckStart(
        IReadOnlyList<string> declaredPermissions,
        IReadOnlyList<string> grantedPermissions,
        bool autoGrant)
    {
        if (declaredPermissions.Count == 0) return PluginAccessDecision.Allow();

        var unknown = declaredPermissions.Where(permission => !PluginPermissions.IsKnown(permission)).ToList();
        if (unknown.Count > 0)
            return PluginAccessDecision.Deny(
                $"manifest 声明了未知权限：{string.Join(", ", unknown)}",
                unknown);

        if (autoGrant) return PluginAccessDecision.Allow();

        var granted = new HashSet<string>(grantedPermissions, StringComparer.OrdinalIgnoreCase);
        var missing = declaredPermissions.Where(permission => !granted.Contains(permission)).ToList();

        return missing.Count == 0
            ? PluginAccessDecision.Allow()
            : PluginAccessDecision.Deny(
                $"插件请求了尚未授予的权限：{string.Join(", ", missing)}",
                missing);
    }

    /// <summary>授权请求本身是否合法：不能授予未定义的权限，也不能授予插件未声明的权限。</summary>
    public static IReadOnlyList<string> ValidateGrantRequest(
        IReadOnlyList<string> declaredPermissions,
        IReadOnlyList<string> requestedPermissions)
    {
        var declared = new HashSet<string>(declaredPermissions, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var permission in requestedPermissions)
        {
            if (!PluginPermissions.IsKnown(permission))
                errors.Add($"未知权限 '{permission}'");
            else if (!declared.Contains(permission))
                errors.Add($"插件未在 manifest 中声明权限 '{permission}'，不能授予");
        }

        return errors;
    }

    /// <summary>该能力在调用前是否需要显式确认。</summary>
    public static bool RequiresConfirmation(string? sideEffect) =>
        PluginSideEffectLevels.RequiresConfirmation(sideEffect);

    /// <summary>
    /// 能力是否可以桥接给聊天模型。模型无法提供确认信息，
    /// 因此在强制确认策略下，需要确认的能力不会出现在工具列表里。
    /// </summary>
    public static bool IsModelCallable(string? kind, string? sideEffect, bool requireConfirmationForSideEffects)
    {
        if (!string.Equals(kind, PluginCapabilityKinds.AgentTool, StringComparison.Ordinal)) return false;
        if (!requireConfirmationForSideEffects) return true;

        return !PluginSideEffectLevels.RequiresConfirmation(sideEffect);
    }

    /// <summary>
    /// 判定一次能力调用是否放行。模型调用永远是"未确认"。
    /// </summary>
    public static PluginAccessDecision CheckInvocation(
        PluginCapability capability,
        bool confirmed,
        string actor,
        bool requireConfirmationForSideEffects)
    {
        if (!PluginSideEffectLevels.RequiresConfirmation(capability.SideEffect))
            return PluginAccessDecision.Allow();

        if (!requireConfirmationForSideEffects) return PluginAccessDecision.Allow();

        if (confirmed) return PluginAccessDecision.Allow();

        var reason = string.Equals(actor, PluginActors.Agent, StringComparison.Ordinal)
            ? $"能力 '{capability.Id}' 的副作用等级为 {capability.SideEffect}，模型不能直接调用，需要用户在界面确认后由管理端点执行"
            : $"能力 '{capability.Id}' 的副作用等级为 {capability.SideEffect}，需要显式确认（confirm=true）";

        return PluginAccessDecision.Deny(reason, [capability.Id]);
    }
}
