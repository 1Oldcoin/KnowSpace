namespace MyFirstMafAgent.Api.Domain.Settings;

/// <summary>
/// 用户级智能体偏好（值对象）：由用户在设置页自行开启，默认全部关闭工具调用与联网搜索。
/// 它与 <c>Agent:EnableToolCalling</c> 的全局开关是“与”的关系——全局开关是总闸，
/// 这里记录的是用户本人的意愿。
/// </summary>
public sealed class AgentPreferences
{
    /// <summary>是否允许聊天模型通过 function calling 调用工具（默认关闭）。</summary>
    public bool EnableToolCalling { get; init; }

    /// <summary>是否允许模型联网搜索、打开网页（默认关闭）。</summary>
    public bool EnableWebSearch { get; init; }

    /// <summary>是否允许模型查看本地文件（工作目录、本机授权目录、知识库原文，默认开启）。</summary>
    public bool EnableFileAccess { get; init; } = true;

    /// <summary>默认偏好：不调用工具、不联网，但允许查看本地文件。</summary>
    public static AgentPreferences Default { get; } = new();

    /// <summary>用可空值构造偏好；某项为 null 时回落到默认值，便于表单局部提交。</summary>
    public static AgentPreferences Create(bool? enableToolCalling, bool? enableWebSearch, bool? enableFileAccess)
    {
        var defaults = Default;
        return new AgentPreferences
        {
            EnableToolCalling = enableToolCalling ?? defaults.EnableToolCalling,
            EnableWebSearch = enableWebSearch ?? defaults.EnableWebSearch,
            EnableFileAccess = enableFileAccess ?? defaults.EnableFileAccess
        };
    }

    /// <summary>
    /// 在已保存的偏好上叠加单次请求的临时开关：某项为 null 时保留原值。
    /// 供“AI 知识问答”页面在单轮对话里临时开启/关闭联网、本地文件等工具，而不改动全局设置。
    /// </summary>
    public AgentPreferences WithOverrides(bool? enableToolCalling, bool? enableWebSearch, bool? enableFileAccess) =>
        new()
        {
            EnableToolCalling = enableToolCalling ?? EnableToolCalling,
            EnableWebSearch = enableWebSearch ?? EnableWebSearch,
            EnableFileAccess = enableFileAccess ?? EnableFileAccess
        };
}
