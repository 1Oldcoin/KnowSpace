using MyFirstMafAgent.Api.Domain.Chats;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 聊天历史的 SqlSugar 实现：三个来源（知识库问答 / 分身对话 / 伴侣对话）加一张导入记录表，
/// 用 <c>UNION ALL</c> 合成一个视图，让外部 Agent 用一个工具就能"回想起"过去聊过什么。
///
/// <para>
/// 为什么用原生 SQL 而不是 SqlSugar 查询表达式：四个来源表结构不同（会话表+消息表、
/// 无会话的导入表），表达式 API 要么需要多次往返再在内存里拼，要么写出四套几乎相同的代码。
/// 一条 UNION 语句也便于给每个分支单独加过滤条件（知识库范围、伴侣档案范围）。
/// </para>
///
/// <para>
/// 隐私边界：这里只读，不提供任何写入；<c>chat:read</c> 是独立能力，
/// 只拿到 <c>knowledge:read</c> 的客户端看不到用户的私人对话。
/// </para>
/// </summary>
public sealed class SqlSugarChatHistoryStore(SqlSugarScope database) : IChatHistoryStore
{
    /// <summary>消息预览长度：够看清"聊了什么"，又不至于把整段回答搬进列表。</summary>
    private const int PreviewChars = 160;

    /// <summary>单条命中最多带回的正文长度。更长的回答用户要去原文里看。</summary>
    private const int MaxHitChars = 1_200;

    public async Task<IReadOnlyList<ChatSessionSummary>> ListSessionsAsync(
        ChatSessionQuery query,
        CancellationToken cancellationToken)
    {
        var branches = new List<string>();
        var parameters = new List<SugarParameter> { new("@limit", Math.Clamp(query.Limit, 1, 200)) };

        if (Wants(query.Sources, ChatHistorySources.Knowledge))
        {
            branches.Add(BuildKnowledgeSessionBranch(query.KnowledgeBaseId, query.AllowedKnowledgeBaseIds, parameters));
        }

        if (Wants(query.Sources, ChatHistorySources.Avatar))
        {
            branches.Add("""
                SELECT 'avatar' AS "Source", s.id AS "SessionId", s.title AS "Title",
                       NULL::text AS "ScopeLabel",
                       (SELECT count(*) FROM avatar_chat_messages m WHERE m.session_id = s.id)::int AS "MessageCount",
                       s.created_at AS "CreatedAt",
                       s.updated_at AS "UpdatedAt",
                       (SELECT left(m.content, 160) FROM avatar_chat_messages m WHERE m.session_id = s.id ORDER BY m.id DESC LIMIT 1) AS "LastMessagePreview"
                FROM avatar_chat_sessions AS s
                """);
        }

        if (Wants(query.Sources, ChatHistorySources.Companion))
        {
            branches.Add(BuildCompanionSessionBranch(query.CompanionProfileId, parameters));
        }

        if (branches.Count == 0) return [];

        var sql = $"""
            SELECT * FROM (
            {string.Join("\nUNION ALL\n", branches)}
            ) AS history
            ORDER BY "UpdatedAt" DESC
            LIMIT @limit
            """;

        var rows = await database.Ado.SqlQueryAsync<ChatSessionRow>(sql, parameters, cancellationToken);
        return rows.Select(row => new ChatSessionSummary(
            row.Source,
            row.SessionId,
            string.IsNullOrWhiteSpace(row.Title) ? "未命名对话" : row.Title,
            row.ScopeLabel,
            row.MessageCount,
            row.CreatedAt,
            row.UpdatedAt,
            Clamp(row.LastMessagePreview, PreviewChars))).ToList();
    }

    public async Task<IReadOnlyList<ChatMessageHit>> SearchMessagesAsync(
        ChatMessageQuery query,
        CancellationToken cancellationToken)
    {
        var text = query.Text?.Trim();
        var hasText = !string.IsNullOrEmpty(text);

        var parameters = new List<SugarParameter>
        {
            new("@limit", Math.Clamp(query.Limit, 1, 200)),
            new("@pattern", hasText ? $"%{EscapeLike(text!)}%" : "%")
        };

        var branches = new List<string>();

        if (Wants(query.Sources, ChatHistorySources.Knowledge))
        {
            var scope = BuildKnowledgeScope(query.KnowledgeBaseId, query.AllowedKnowledgeBaseIds, parameters, "kb");

            branches.Add($"""
                SELECT 'knowledge' AS "Source", s.id AS "SessionId", s.title AS "SessionTitle",
                       kb.name AS "ScopeLabel", m.role AS "Role",
                       left(m.content, {MaxHitChars}) AS "Content", m.created_at AS "CreatedAt"
                FROM knowledge_chat_messages AS m
                JOIN knowledge_chat_sessions AS s ON s.id = m.session_id
                JOIN kb_knowledge_bases AS kb ON kb.id = s.knowledge_base_id
                WHERE m.content ILIKE @pattern{scope}
                """);
        }

        if (Wants(query.Sources, ChatHistorySources.Avatar))
        {
            branches.Add($"""
                SELECT 'avatar' AS "Source", s.id AS "SessionId", s.title AS "SessionTitle",
                       NULL::text AS "ScopeLabel", m.role AS "Role",
                       left(m.content, {MaxHitChars}) AS "Content", m.created_at AS "CreatedAt"
                FROM avatar_chat_messages AS m
                JOIN avatar_chat_sessions AS s ON s.id = m.session_id
                WHERE m.content ILIKE @pattern
                """);
        }

        if (Wants(query.Sources, ChatHistorySources.Companion))
        {
            var scope = query.CompanionProfileId is { } profileId
                ? " AND p.id = @companionProfileId"
                : string.Empty;
            if (query.CompanionProfileId is { } id) parameters.Add(new("@companionProfileId", id));

            branches.Add($"""
                SELECT 'companion' AS "Source", s.id AS "SessionId", s.title AS "SessionTitle",
                       p.name AS "ScopeLabel", m.role AS "Role",
                       left(m.content, {MaxHitChars}) AS "Content", m.created_at AS "CreatedAt"
                FROM companion_session_messages AS m
                JOIN companion_sessions AS s ON s.id = m.session_id
                JOIN companion_profiles AS p ON p.id = s.profile_id
                WHERE m.content ILIKE @pattern{scope}
                """);
        }

        if (Wants(query.Sources, ChatHistorySources.CompanionImport))
        {
            var scope = query.CompanionProfileId is { } profileId
                ? " AND p.id = @companionProfileId"
                : string.Empty;
            if (query.CompanionProfileId is { } id && !parameters.Any(item => item.ParameterName == "@companionProfileId"))
                parameters.Add(new("@companionProfileId", id));

            // 导入记录没有会话概念：会话位填档案 ID，标题位填档案名，发送者放在 role 上。
            branches.Add($"""
                SELECT 'companion-import' AS "Source", p.id AS "SessionId", p.name AS "SessionTitle",
                       p.name AS "ScopeLabel", m.sender AS "Role",
                       left(m.content, {MaxHitChars}) AS "Content",
                       COALESCE(m.sent_at, m.created_at) AS "CreatedAt"
                FROM companion_messages AS m
                JOIN companion_profiles AS p ON p.id = m.profile_id
                WHERE m.content ILIKE @pattern{scope}
                """);
        }

        if (branches.Count == 0) return [];

        var sql = $"""
            SELECT * FROM (
            {string.Join("\nUNION ALL\n", branches)}
            ) AS history
            ORDER BY "CreatedAt" DESC
            LIMIT @limit
            """;

        var rows = await database.Ado.SqlQueryAsync<ChatMessageRow>(sql, parameters, cancellationToken);
        return rows.Select(row => new ChatMessageHit(
            row.Source,
            row.SessionId,
            string.IsNullOrWhiteSpace(row.SessionTitle) ? "未命名对话" : row.SessionTitle,
            row.Role,
            row.Content,
            hasText ? BuildSnippet(row.Content, text!) : Clamp(row.Content, PreviewChars),
            row.CreatedAt)).ToList();
    }

    private static string BuildKnowledgeSessionBranch(
        Guid? knowledgeBaseId,
        IReadOnlyList<Guid>? allowedKnowledgeBaseIds,
        List<SugarParameter> parameters)
    {
        var scope = BuildKnowledgeScope(knowledgeBaseId, allowedKnowledgeBaseIds, parameters, "kb");
        if (scope.Length > 0)
            scope = "WHERE " + scope[5..];

        return $$"""
            SELECT 'knowledge' AS "Source", s.id AS "SessionId", s.title AS "Title",
                   kb.name AS "ScopeLabel",
                   (SELECT count(*) FROM knowledge_chat_messages m WHERE m.session_id = s.id)::int AS "MessageCount",
                   s.created_at AS "CreatedAt",
                   s.updated_at AS "UpdatedAt",
                   (SELECT left(m.content, 160) FROM knowledge_chat_messages m WHERE m.session_id = s.id ORDER BY m.id DESC LIMIT 1) AS "LastMessagePreview"
            FROM knowledge_chat_sessions AS s
            JOIN kb_knowledge_bases AS kb ON kb.id = s.knowledge_base_id
            {{scope}}
            """;
    }

    /// <summary>
    /// 知识库范围片段（以 <c> AND </c> 开头，便于直接拼在已有 WHERE 后面）。
    /// 白名单用展开的 <c>IN</c> 而不是数组参数：SqlSugar 的 <c>Ado.SqlQueryAsync</c> 对
    /// <c>Guid[]</c> 参数的展开行为依 Npgsql 版本而异，展开成具名参数最可控。
    /// </summary>
    private static string BuildKnowledgeScope(
        Guid? knowledgeBaseId,
        IReadOnlyList<Guid>? allowedKnowledgeBaseIds,
        List<SugarParameter> parameters,
        string alias)
    {
        if (knowledgeBaseId is { } single)
        {
            parameters.Add(new SugarParameter("@knowledgeBaseId", single));
            return $" AND {alias}.id = @knowledgeBaseId";
        }

        if (allowedKnowledgeBaseIds is not { Count: > 0 } allowed) return string.Empty;

        var names = new List<string>(allowed.Count);
        for (var index = 0; index < allowed.Count; index++)
        {
            var name = $"@allowedKb{index}";
            names.Add(name);
            parameters.Add(new SugarParameter(name, allowed[index]));
        }

        return $" AND {alias}.id IN ({string.Join(", ", names)})";
    }

    private static string BuildCompanionSessionBranch(Guid? companionProfileId, List<SugarParameter> parameters)
    {
        var scope = string.Empty;
        if (companionProfileId is { } id)
        {
            scope = "WHERE p.id = @companionProfileId";
            parameters.Add(new SugarParameter("@companionProfileId", id));
        }

        return $$"""
            SELECT 'companion' AS "Source", s.id AS "SessionId", s.title AS "Title",
                   p.name AS "ScopeLabel",
                   (SELECT count(*) FROM companion_session_messages m WHERE m.session_id = s.id)::int AS "MessageCount",
                   s.created_at AS "CreatedAt",
                   (SELECT COALESCE(max(m.created_at), s.created_at) FROM companion_session_messages m WHERE m.session_id = s.id) AS "UpdatedAt",
                   (SELECT left(m.content, 160) FROM companion_session_messages m WHERE m.session_id = s.id ORDER BY m.id DESC LIMIT 1) AS "LastMessagePreview"
            FROM companion_sessions AS s
            JOIN companion_profiles AS p ON p.id = s.profile_id
            {{scope}}
            """;
    }

    private static bool Wants(IReadOnlyList<string> sources, string source) =>
        sources.Count == 0 || sources.Contains(source, StringComparer.Ordinal);

    /// <summary>
    /// 把用户输入里的 LIKE 元字符转义掉。不转义时搜 "%" 会把全部历史捞出来，
    /// 搜 "_" 会变成单字符通配——两种都是"看起来能用、结果莫名其妙"的坑。
    /// </summary>
    internal static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>在命中词附近截一段上下文；找不到（大小写/分词差异）时退回开头。</summary>
    internal static string BuildSnippet(string content, string needle)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var index = content.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return Clamp(content, PreviewChars * 2) ?? string.Empty;

        const int radius = 60;
        var start = Math.Max(0, index - radius);
        var length = Math.Min(content.Length - start, needle.Length + radius * 2);
        var snippet = content.Substring(start, length).ReplaceLineEndings(" ").Trim();
        return (start > 0 ? "…" : string.Empty) + snippet + (start + length < content.Length ? "…" : string.Empty);
    }

    private static string? Clamp(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= limit) return value;
        return value[..limit] + "…";
    }
}

internal sealed class ChatSessionRow
{
    public string Source { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ScopeLabel { get; set; }
    public int MessageCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? LastMessagePreview { get; set; }
}

internal sealed class ChatMessageRow
{
    public string Source { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public string SessionTitle { get; set; } = string.Empty;
    public string? ScopeLabel { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
