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
        CancellationToken cancellationToken,
        ChatHistoryScope? scope = null)
    {
        var parsed = ParseSources(source);
        if (parsed.Error is { } error) return new ChatHistoryResult<IReadOnlyList<ChatSessionSummary>>(null, error);

        var narrowed = ApplyScope(parsed.Sources, knowledgeBaseId, scope);
        if (narrowed.Error is { } scopeError)
            return new ChatHistoryResult<IReadOnlyList<ChatSessionSummary>>(null, scopeError);

        var sessions = await store.ListSessionsAsync(
            new ChatSessionQuery(
                narrowed.Sources, knowledgeBaseId, companionProfileId, Clamp(limit), narrowed.AllowedKnowledgeBaseIds),
            cancellationToken);
        return new ChatHistoryResult<IReadOnlyList<ChatSessionSummary>>(sessions, null);
    }

    public async Task<ChatHistoryResult<IReadOnlyList<ChatMessageHit>>> SearchAsync(
        string? query,
        string? source,
        Guid? knowledgeBaseId,
        Guid? companionProfileId,
        int limit,
        CancellationToken cancellationToken,
        ChatHistoryScope? scope = null)
    {
        var parsed = ParseSources(source);
        if (parsed.Error is { } error) return new ChatHistoryResult<IReadOnlyList<ChatMessageHit>>(null, error);

        var narrowed = ApplyScope(parsed.Sources, knowledgeBaseId, scope);
        if (narrowed.Error is { } scopeError)
            return new ChatHistoryResult<IReadOnlyList<ChatMessageHit>>(null, scopeError);

        var messages = await store.SearchMessagesAsync(
            new ChatMessageQuery(
                narrowed.Sources,
                knowledgeBaseId,
                companionProfileId,
                string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
                Clamp(limit),
                narrowed.AllowedKnowledgeBaseIds),
            cancellationToken);
        return new ChatHistoryResult<IReadOnlyList<ChatMessageHit>>(messages, null);
    }

    /// <summary>
    /// 把调用方的数据范围落到查询上：受限客户端只能看白名单内的知识库，且看不到私密来源。
    ///
    /// <para>
    /// 这一层是<b>纵深防御</b>而不是唯一防线——MCP 网关已经按能力与白名单挡过一次；
    /// 但工具实现一旦漏判（例如调用方不传 <c>knowledgeBaseId</c>），
    /// "不传就返回全部"就是一条静默越权路径。所以范围必须在这里再收一次。
    /// </para>
    /// </summary>
    private static (IReadOnlyList<string> Sources, IReadOnlyList<Guid>? AllowedKnowledgeBaseIds, string? Error)
        ApplyScope(IReadOnlyList<string> sources, Guid? requestedKnowledgeBaseId, ChatHistoryScope? scope)
    {
        if (scope is null) return (sources, null, null);

        if (requestedKnowledgeBaseId is { } requested
            && scope.AllowedKnowledgeBaseIds.Count > 0
            && !scope.AllowedKnowledgeBaseIds.Contains(requested))
        {
            return ([], null, $"未被授权访问知识库 '{requested:D}'。");
        }

        var allowedSources = scope.AllowPersonalSources
            ? sources
            : sources.Where(source => string.Equals(source, ChatHistorySources.Knowledge, StringComparison.Ordinal)).ToList();

        if (!scope.AllowPersonalSources && allowedSources.Count == 0)
            return ([], null, "当前客户端没有读取私人对话（分身 / 伴侣）的权限，请指定 source=knowledge。");

        return (allowedSources,
            scope.AllowedKnowledgeBaseIds.Count > 0 ? scope.AllowedKnowledgeBaseIds : null,
            null);
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

/// <summary>
/// 调用方的数据范围（由 MCP 客户端的能力/白名单翻译而来）。
/// </summary>
/// <param name="AllowedKnowledgeBaseIds">允许访问的知识库；空数组表示不限制。</param>
/// <param name="AllowPersonalSources">
/// 是否允许读分身 / 伴侣对话。<c>chat:read</c> 只表示"能读聊天历史"，
/// 私人对话还需要显式的范围授权，否则一个只想看知识库问答的客户端能读到用户全部私聊。
/// </param>
public sealed record ChatHistoryScope(
    IReadOnlyList<Guid> AllowedKnowledgeBaseIds,
    bool AllowPersonalSources = false)
{
    /// <summary>从 MCP 客户端推导范围；<c>null</c> 客户端（本机直接调用）不限制。</summary>
    public static ChatHistoryScope? FromClient(Domain.Agents.AgentClient? client)
    {
        if (client is null) return null;

        return new ChatHistoryScope(
            client.AllowedKnowledgeBaseIds,
            // 受限客户端（配了知识库白名单）一律不返回私人来源。
            AllowPersonalSources: !client.IsKnowledgeBaseRestricted);
    }
}
