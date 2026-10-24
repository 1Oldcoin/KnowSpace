namespace MyFirstMafAgent.Api.Domain.Agents;

/// <summary>
/// 外部智能体接入的权限词表。
///
/// <para>
/// 用「能力:读/写」两段式而不是单一「是否只读」布尔：外部 Agent 往往只需要知识库，
/// 却又没有任何理由读本机任意授权目录。<b>授权粒度必须能在不写代码的前提下收窄</b>。
/// </para>
///
/// <para>
/// 匹配规则（由窄到宽）：<c>knowledge:read</c> → <c>knowledge:*</c> → <c>*</c>。
/// </para>
/// </summary>
public static class AgentClientCapabilities
{
    /// <summary>通配：全部能力。</summary>
    public const string All = "*";

    public const string KnowledgeRead = "knowledge:read";
    public const string KnowledgeWrite = "knowledge:write";
    public const string WorkspaceRead = "workspace:read";
    public const string WorkspaceWrite = "workspace:write";
    public const string FilesRead = "files:read";
    public const string FilesWrite = "files:write";
    public const string AvatarRead = "avatar:read";
    public const string AvatarWrite = "avatar:write";

    /// <summary>
    /// 读取聊天历史。刻意与 <c>knowledge:read</c> 分开：私人对话比资料更敏感，
    /// 只拿到知识库权限的客户端不应该顺带看见用户聊过什么。
    /// </summary>
    public const string ChatRead = "chat:read";

    /// <summary>可以被授予的全部能力（UI 与校验都以此为准）。</summary>
    public static readonly IReadOnlyList<string> AllKnown =
    [
        KnowledgeRead,
        KnowledgeWrite,
        WorkspaceRead,
        WorkspaceWrite,
        FilesRead,
        FilesWrite,
        AvatarRead,
        AvatarWrite,
        ChatRead
    ];

    /// <summary>默认能力：只读知识库。新建客户端的最小可用权限。</summary>
    public static readonly IReadOnlyList<string> DefaultForNewClient = [KnowledgeRead];

    public static bool IsKnown(string capability) =>
        string.Equals(capability, All, StringComparison.Ordinal)
        || AllKnown.Contains(capability, StringComparer.Ordinal);

    /// <summary>单个能力是否满足要求。</summary>
    public static bool Satisfies(string granted, string required)
    {
        if (string.Equals(granted, All, StringComparison.Ordinal)) return true;
        if (string.Equals(granted, required, StringComparison.Ordinal)) return true;

        // knowledge:* 覆盖 knowledge:read / knowledge:write。
        var separator = required.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0) return false;

        return string.Equals(granted, required[..(separator + 1)] + "*", StringComparison.Ordinal);
    }

    /// <summary>拆分能力名，得到它的作用域前缀（<c>knowledge:read</c> → <c>knowledge</c>）。</summary>
    public static string ScopeOf(string capability)
    {
        var separator = capability.IndexOf(':', StringComparison.Ordinal);
        return separator <= 0 ? capability : capability[..separator];
    }
}

/// <summary>一个 MCP 工具需要什么权限、是否写操作、是否作用于知识库。</summary>
public sealed record McpToolDescriptor(string Name, string Capability, bool IsWrite, bool UsesKnowledgeBase);

/// <summary>
/// MCP 工具清单：鉴权闸门据此判断「这次调用需要什么能力」。
///
/// <para>
/// <b>清单之外的工具默认拒绝</b>。新增 MCP 工具却忘记登记时，网关会挡住它——
/// 宁可新工具短期不可用，也不要出现「悄悄对所有人开放」的越权面。
/// </para>
/// </summary>
public static class McpToolCatalog
{
    public static readonly IReadOnlyList<McpToolDescriptor> All =
    [
        // 知识库：读
        new("list_knowledge_bases", AgentClientCapabilities.KnowledgeRead, IsWrite: false, UsesKnowledgeBase: false),
        new("list_documents", AgentClientCapabilities.KnowledgeRead, IsWrite: false, UsesKnowledgeBase: true),
        new("search_knowledge_base", AgentClientCapabilities.KnowledgeRead, IsWrite: false, UsesKnowledgeBase: true),
        new("get_document_content", AgentClientCapabilities.KnowledgeRead, IsWrite: false, UsesKnowledgeBase: false),
        new("get_document_summary", AgentClientCapabilities.KnowledgeRead, IsWrite: false, UsesKnowledgeBase: false),
        new("get_document_status", AgentClientCapabilities.KnowledgeRead, IsWrite: false, UsesKnowledgeBase: false),

        // 知识库：写
        new("upload_document", AgentClientCapabilities.KnowledgeWrite, IsWrite: true, UsesKnowledgeBase: true),

        // Agent 工作区
        new("workspace_list_files", AgentClientCapabilities.WorkspaceRead, IsWrite: false, UsesKnowledgeBase: false),
        new("workspace_read_file", AgentClientCapabilities.WorkspaceRead, IsWrite: false, UsesKnowledgeBase: false),
        new("workspace_write_file", AgentClientCapabilities.WorkspaceWrite, IsWrite: true, UsesKnowledgeBase: false),

        // 用户授权目录
        new("list_file_roots", AgentClientCapabilities.FilesRead, IsWrite: false, UsesKnowledgeBase: false),
        new("local_list_directory", AgentClientCapabilities.FilesRead, IsWrite: false, UsesKnowledgeBase: false),
        new("local_read_file", AgentClientCapabilities.FilesRead, IsWrite: false, UsesKnowledgeBase: false),
        new("local_search_files", AgentClientCapabilities.FilesRead, IsWrite: false, UsesKnowledgeBase: false),
        new("local_grep", AgentClientCapabilities.FilesRead, IsWrite: false, UsesKnowledgeBase: false),
        new("local_write_file", AgentClientCapabilities.FilesWrite, IsWrite: true, UsesKnowledgeBase: false),

        // 分身
        new("avatar_get_persona", AgentClientCapabilities.AvatarRead, IsWrite: false, UsesKnowledgeBase: false),
        new("avatar_search_memories", AgentClientCapabilities.AvatarRead, IsWrite: false, UsesKnowledgeBase: false),
        new("avatar_list_fragments", AgentClientCapabilities.AvatarRead, IsWrite: false, UsesKnowledgeBase: false),
        new("avatar_ask_as_me", AgentClientCapabilities.AvatarRead, IsWrite: false, UsesKnowledgeBase: false),
        new("avatar_feed", AgentClientCapabilities.AvatarWrite, IsWrite: true, UsesKnowledgeBase: false),
        new("avatar_digest", AgentClientCapabilities.AvatarWrite, IsWrite: true, UsesKnowledgeBase: false),

        // 聊天历史回读（只读，独立能力）
        new("list_chat_sessions", AgentClientCapabilities.ChatRead, IsWrite: false, UsesKnowledgeBase: false),
        new("chat_search_history", AgentClientCapabilities.ChatRead, IsWrite: false, UsesKnowledgeBase: false)
    ];

    private static readonly Dictionary<string, McpToolDescriptor> Index =
        All.ToDictionary(descriptor => descriptor.Name, StringComparer.Ordinal);

    public static bool TryGet(string name, out McpToolDescriptor descriptor) =>
        Index.TryGetValue(name, out descriptor!);

    public static McpToolDescriptor? Find(string name) =>
        Index.TryGetValue(name, out var descriptor) ? descriptor : null;

    /// <summary>工具名是否已登记（用于客户端白名单校验）。</summary>
    public static bool IsKnownTool(string name) => Index.ContainsKey(name);
}
