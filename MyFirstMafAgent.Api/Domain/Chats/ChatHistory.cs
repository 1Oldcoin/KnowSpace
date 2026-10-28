namespace MyFirstMafAgent.Api.Domain.Chats;

/// <summary>
/// 聊天历史的对外投影。**故意与"记忆"分开**：记忆是被整理过的结论，
/// 聊天历史是原样的对话流水，两者的可信度与隐私等级不同，不能混成一个接口。
/// </summary>
public sealed record ChatSessionSummary(
    string Source,
    Guid SessionId,
    string Title,
    string? ScopeLabel,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? LastMessagePreview);

/// <summary>
/// 一条命中的历史消息。<paramref name="SessionId"/> 在导入记录里是伴侣档案 ID，
/// 因为那类历史没有会话概念，<paramref name="ScopeLabel"/> 会写清楚是哪份档案。
/// </summary>
public sealed record ChatMessageHit(
    string Source,
    Guid SessionId,
    string SessionTitle,
    string Role,
    string Content,
    string? Snippet,
    DateTimeOffset CreatedAt);

/// <summary>
/// 聊天历史来源。名字直接出现在 MCP 返回值里，不要随手改。
/// </summary>
public static class ChatHistorySources
{
    /// <summary>知识库问答（knowledge_chat_*）。</summary>
    public const string Knowledge = "knowledge";

    /// <summary>分身对话（avatar_chat_*）。</summary>
    public const string Avatar = "avatar";

    /// <summary>伴侣对话（companion_sessions / companion_session_messages）。</summary>
    public const string Companion = "companion";

    /// <summary>导入的聊天记录（companion_messages，QQ/微信/CSV 导出）。</summary>
    public const string CompanionImport = "companion-import";

    public static readonly IReadOnlyList<string> All =
        [Knowledge, Avatar, Companion, CompanionImport];

    public static string Describe(string source) => source switch
    {
        Knowledge => "知识库问答",
        Avatar => "分身对话",
        Companion => "伴侣对话",
        CompanionImport => "导入的聊天记录",
        _ => source
    };

    /// <summary>
    /// 把调用方传来的来源名规范化。
    /// <para>
    /// <c>null</c>/空串视为「没有指定」＝全部来源（调用方不写参数时自然就是这个意图）；
    /// 写错的名字返回 <c>null</c>，由上层报错，<b>不能静默当成 all</b>——
    /// 那样拼错参数的客户端会拿到全部私人对话。
    /// </para>
    /// </summary>
    public static string? Normalize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "all";
        var trimmed = source.Trim().ToLowerInvariant();
        if (trimmed is "all" or "*") return "all";
        return All.FirstOrDefault(item => string.Equals(item, trimmed, StringComparison.Ordinal));
    }
}

/// <summary>会话列表查询。来源为空表示"全部来源"。</summary>
/// <param name="AllowedKnowledgeBaseIds">
/// 非空表示"只允许这些知识库"（MCP 受限客户端的知识库白名单）。
/// <c>null</c> 或空数组表示不限制。
/// </param>
public sealed record ChatSessionQuery(
    IReadOnlyList<string> Sources,
    Guid? KnowledgeBaseId,
    Guid? CompanionProfileId,
    int Limit,
    IReadOnlyList<Guid>? AllowedKnowledgeBaseIds = null);

/// <summary>消息检索查询。<paramref name="Text"/> 为空时退化为"最近消息"。</summary>
public sealed record ChatMessageQuery(
    IReadOnlyList<string> Sources,
    Guid? KnowledgeBaseId,
    Guid? CompanionProfileId,
    string? Text,
    int Limit,
    IReadOnlyList<Guid>? AllowedKnowledgeBaseIds = null);

/// <summary>
/// 聊天历史的读取端口。实现放在 Infrastructure；Application 只依赖这个接口。
/// <para>
/// 只提供"读"：聊天历史是既有功能的副产品，外部 Agent 不应该通过 MCP 改写它。
/// </para>
/// </summary>
public interface IChatHistoryStore
{
    Task<IReadOnlyList<ChatSessionSummary>> ListSessionsAsync(
        ChatSessionQuery query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatMessageHit>> SearchMessagesAsync(
        ChatMessageQuery query,
        CancellationToken cancellationToken);
}
