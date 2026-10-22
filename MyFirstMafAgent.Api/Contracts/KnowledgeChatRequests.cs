namespace MyFirstMafAgent.Api.Contracts;

public sealed record NewKnowledgeChatSessionRequest(string? Title);

/// <summary>
/// 单轮问答请求。三个开关都是可空值：为 null 时沿用设置页保存的偏好，
/// 非 null 时只对本次问答生效（前端“AI 知识问答”里的工具开关）。
/// </summary>
public sealed record KnowledgeChatRequest(
    string Message,
    bool? EnableToolCalling = null,
    bool? EnableWebSearch = null,
    bool? EnableFileAccess = null);
