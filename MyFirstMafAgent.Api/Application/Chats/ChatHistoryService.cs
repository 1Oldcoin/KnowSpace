using MyFirstMafAgent.Api.Domain.Chats;

namespace MyFirstMafAgent.Api.Application.Chats;

/// <summary>
/// 聊天历史回读。对外（MCP/HTTP）只暴露两件事：<b>有哪些对话</b> 与 <b>聊到过什么</b>。
///
/// <para>
/// 这一层不做检索算法，只做三件容易做错的事：来源名规范化、条数夹紧、错误信息说人话。
/// 具体 SQL 在 Infrastructure，保持 Application 不碰持久化细节的约束。
/// </para>
/// </summary>
public sealed class ChatHistoryService(IChatHistoryStore store)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    /// <summary>合法来源名，供工具描述与错误信息复用。</summary>
    public static IReadOnlyList<string> Sources => ChatHistorySources.All;

    public async Task<ChatHistoryResult<IReadOnlyList<ChatSessionSummary>>> ListSessionsAsync(
        string? source,
        Guid? knowledgeBaseId,
        Guid? companionProfileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var parsed = ParseSources(source);
        if (parsed.Error is { } error) return new ChatHistoryResult<IReadOnlyList<ChatSessionSummary>>(null, error);

        var sessions = await store.ListSessionsAsync(
            new ChatSessionQuery(parsed.Sources, knowledgeBaseId, companionProfileId, Clamp(limit)),
            cancellationToken);
        return new ChatHistoryResult<IReadOnlyList<ChatSessionSummary>>(sessions, null);
    }

    public async Task<ChatHistoryResult<IReadOnlyList<ChatMessageHit>>> SearchAsync(
        string? query,
        string? source,
        Guid? knowledgeBaseId,
        Guid? companionProfileId,
        int limit,
        CancellationToken cancellationToken)
    {
        var parsed = ParseSources(source);
        if (parsed.Error is { } error) return new ChatHistoryResult<IReadOnlyList<ChatMessageHit>>(null, error);

        var messages = await store.SearchMessagesAsync(
            new ChatMessageQuery(
                parsed.Sources,
                knowledgeBaseId,
                companionProfileId,
                string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
                Clamp(limit)),
            cancellationToken);
        return new ChatHistoryResult<IReadOnlyList<ChatMessageHit>>(messages, null);
    }

    private static int Clamp(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

    /// <summary>
    /// 来源名写错时<b>直接报错</b>，不要静默返回空列表：
    /// 空列表会被调用方理解成"用户没聊过"，而真实原因是参数拼错了。
    /// </summary>
    private static (IReadOnlyList<string> Sources, string? Error) ParseSources(string? source)
    {
        var normalized = ChatHistorySources.Normalize(source);
        if (normalized is null)
        {
            var known = string.Join(" / ", ChatHistorySources.All);
            return ([], $"未知的聊天来源：{source}。可用值：{known} / all。");
        }

        return (normalized == "all" ? [] : [normalized], null);
    }
}

/// <summary>应用层结果：成功带值，失败带一句能直接回给调用者的中文原因。</summary>
public sealed record ChatHistoryResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;
}
