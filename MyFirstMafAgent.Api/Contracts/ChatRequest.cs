namespace MyFirstMafAgent.Api.Contracts;

/// <summary>
/// 无会话问答请求。工具开关为可空值：null 表示沿用设置页偏好，
/// 非 null 时只对本次请求生效。
/// </summary>
public sealed record ChatRequest(
    string Message,
    Guid? KnowledgeBaseId = null,
    bool? EnableToolCalling = null,
    bool? EnableWebSearch = null,
    bool? EnableFileAccess = null);
