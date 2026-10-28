using MyFirstMafAgent.Api.Application.Chats;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.Domain.Chats;
using MyFirstMafAgent.Api.Infrastructure.Persistence;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 聊天历史回读（MCP <c>list_chat_sessions</c> / <c>chat_search_history</c>）的边界用例。
///
/// <para>
/// 这里不碰数据库：SQL 的形状由真实运行验证，单测锁的是"参数解释"这一层——
/// 来源名写错要报错而不是返回空、条数要有上下限、LIKE 元字符要被转义。
/// </para>
/// </summary>
public sealed class ChatHistoryTests
{
    private sealed class RecordingStore : IChatHistoryStore
    {
        public ChatSessionQuery? SessionQuery { get; private set; }
        public ChatMessageQuery? MessageQuery { get; private set; }

        public Task<IReadOnlyList<ChatSessionSummary>> ListSessionsAsync(
            ChatSessionQuery query,
            CancellationToken cancellationToken)
        {
            SessionQuery = query;
            return Task.FromResult<IReadOnlyList<ChatSessionSummary>>([]);
        }

        public Task<IReadOnlyList<ChatMessageHit>> SearchMessagesAsync(
            ChatMessageQuery query,
            CancellationToken cancellationToken)
        {
            MessageQuery = query;
            return Task.FromResult<IReadOnlyList<ChatMessageHit>>([]);
        }
    }

    [Theory]
    [InlineData("knowledge", "knowledge")]
    [InlineData("  Avatar ", "avatar")]
    [InlineData("COMPANION", "companion")]
    [InlineData("companion-import", "companion-import")]
    [InlineData("all", "all")]
    [InlineData("*", "all")]
    [InlineData("memory", null)]
    [InlineData("", "all")]
    [InlineData(null, "all")]
    public void Source_names_are_normalised_or_rejected(string? input, string? expected) =>
        Assert.Equal(expected, ChatHistorySources.Normalize(input));

    [Fact]
    public async Task Unknown_source_is_an_error_instead_of_an_empty_result()
    {
        var service = new ChatHistoryService(new RecordingStore());
        var result = await service.SearchAsync("胃镜", "memories", null, null, 10, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("未知的聊天来源", result.Error, StringComparison.Ordinal);
        // 报错要把可用值列出来，否则调用方只能猜。
        Assert.Contains("companion-import", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, ChatHistoryService.DefaultLimit)]
    [InlineData(-5, ChatHistoryService.DefaultLimit)]
    [InlineData(30, 30)]
    [InlineData(10_000, ChatHistoryService.MaxLimit)]
    public async Task Limit_is_clamped(int requested, int expected)
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);

        await service.ListSessionsAsync("all", null, null, requested, CancellationToken.None);

        Assert.NotNull(store.SessionQuery);
        Assert.Equal(expected, store.SessionQuery!.Limit);
    }

    [Fact]
    public async Task All_source_expands_to_every_stream()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);

        await service.SearchAsync("x", null, null, null, 5, CancellationToken.None);

        Assert.NotNull(store.MessageQuery);
        Assert.Empty(store.MessageQuery!.Sources);
    }

    [Fact]
    public async Task Single_source_is_passed_through_and_blank_query_becomes_recency_mode()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);

        await service.SearchAsync("   ", "avatar", null, null, 5, CancellationToken.None);

        Assert.Equal([ChatHistorySources.Avatar], store.MessageQuery!.Sources);
        Assert.Null(store.MessageQuery.Text);
    }

    [Fact]
    public async Task Query_text_is_trimmed_before_searching()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);

        await service.SearchAsync("  胃镜  ", "all", null, null, 5, CancellationToken.None);

        Assert.Equal("胃镜", store.MessageQuery!.Text);
    }

    [Fact]
    public void Like_metacharacters_are_escaped()
    {
        // 不转义时搜 "%" 会把全部历史捞出来，搜 "_" 会变成单字符通配。
        Assert.Equal("100\\%", SqlSugarChatHistoryStore.EscapeLike("100%"));
        Assert.Equal("a\\_b", SqlSugarChatHistoryStore.EscapeLike("a_b"));
        Assert.Equal("c\\\\d", SqlSugarChatHistoryStore.EscapeLike("c\\d"));
    }

    // ───────── 知识库范围（P1：不传 knowledgeBaseId 不能等于"全部"） ─────────

    private static readonly Guid AllowedKb = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherKb = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Restricted_scope_drops_personal_sources()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);
        var scope = new ChatHistoryScope([AllowedKb], AllowPersonalSources: false);

        await service.ListSessionsAsync("all", null, null, 10, CancellationToken.None, scope);

        // "all" 被收窄成只剩 knowledge，分身/伴侣/导入一律不出现。
        Assert.Equal([ChatHistorySources.Knowledge], store.SessionQuery!.Sources);
        Assert.Equal([AllowedKb], store.SessionQuery.AllowedKnowledgeBaseIds);
    }

    [Fact]
    public async Task Restricted_scope_rejects_explicit_knowledge_base_outside_whitelist()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);
        var scope = new ChatHistoryScope([AllowedKb], AllowPersonalSources: false);

        var result = await service.SearchAsync("x", "knowledge", OtherKb, null, 10, CancellationToken.None, scope);

        Assert.False(result.Succeeded);
        Assert.Contains("未被授权", result.Error, StringComparison.Ordinal);
        Assert.Null(store.MessageQuery);
    }

    [Fact]
    public async Task Restricted_scope_allows_explicit_knowledge_base_inside_whitelist()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);
        var scope = new ChatHistoryScope([AllowedKb], AllowPersonalSources: false);

        var result = await service.ListSessionsAsync("knowledge", AllowedKb, null, 10, CancellationToken.None, scope);

        Assert.True(result.Succeeded);
        Assert.Equal(AllowedKb, store.SessionQuery!.KnowledgeBaseId);
        Assert.Equal([AllowedKb], store.SessionQuery.AllowedKnowledgeBaseIds);
    }

    [Fact]
    public async Task Restricted_scope_explains_when_only_personal_sources_were_requested()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);
        var scope = new ChatHistoryScope([AllowedKb], AllowPersonalSources: false);

        var result = await service.SearchAsync("x", "avatar", null, null, 10, CancellationToken.None, scope);

        Assert.False(result.Succeeded);
        Assert.Contains("source=knowledge", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unrestricted_scope_keeps_all_sources_and_no_whitelist()
    {
        var store = new RecordingStore();
        var service = new ChatHistoryService(store);
        var scope = new ChatHistoryScope([], AllowPersonalSources: true);

        await service.ListSessionsAsync("all", null, null, 10, CancellationToken.None, scope);

        Assert.Empty(store.SessionQuery!.Sources);
        Assert.Null(store.SessionQuery.AllowedKnowledgeBaseIds);
    }

    [Fact]
    public void Scope_is_derived_from_client_knowledge_base_restriction()
    {
        var restricted = ClientWithKnowledgeBases([AllowedKb]);
        var scope = ChatHistoryScope.FromClient(restricted);

        Assert.NotNull(scope);
        Assert.False(scope!.AllowPersonalSources);
        Assert.Equal([AllowedKb], scope.AllowedKnowledgeBaseIds);

        var unrestricted = ChatHistoryScope.FromClient(ClientWithKnowledgeBases([]));
        Assert.NotNull(unrestricted);
        Assert.True(unrestricted!.AllowPersonalSources);

        // 本机直接调用（没有 MCP 客户端上下文）不做任何收窄。
        Assert.Null(ChatHistoryScope.FromClient(null));
    }

    private static AgentClient ClientWithKnowledgeBases(IReadOnlyList<Guid> knowledgeBases) =>
        new(
            "ac_test",
            "测试客户端",
            null,
            "maf_abcd",
            [AgentClientCapabilities.ChatRead],
            knowledgeBases,
            [],
            60,
            Enabled: true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null);

    [Fact]
    public void Snippet_keeps_the_match_in_context_with_ellipses()
    {
        var content = new string('前', 100) + "胃镜检查" + new string('后', 100);
        var snippet = SqlSugarChatHistoryStore.BuildSnippet(content, "胃镜");

        Assert.Contains("胃镜检查", snippet, StringComparison.Ordinal);
        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
        Assert.True(snippet.Length < content.Length);
    }

    [Fact]
    public void Snippet_falls_back_to_head_when_the_term_is_not_found()
    {
        // 词面没命中却进了结果集（例如大小写/分词差异）时不能返回空串。
        var snippet = SqlSugarChatHistoryStore.BuildSnippet(new string('x', 500), "不存在");
        Assert.False(string.IsNullOrWhiteSpace(snippet));
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list_chat_sessions")]
    [InlineData("chat_search_history")]
    public void Chat_history_tools_are_read_only_and_gated_by_chat_read(string tool)
    {
        var descriptor = McpToolCatalog.Find(tool);

        Assert.NotNull(descriptor);
        Assert.False(descriptor!.IsWrite);
        Assert.Equal(AgentClientCapabilities.ChatRead, descriptor.Capability);
        Assert.False(descriptor.UsesKnowledgeBase);
    }

    [Fact]
    public void Chat_read_is_a_grantable_capability_that_knowledge_read_does_not_imply()
    {
        Assert.Contains(AgentClientCapabilities.ChatRead, AgentClientCapabilities.AllKnown);
        Assert.False(AgentClientCapabilities.Satisfies(AgentClientCapabilities.KnowledgeRead, AgentClientCapabilities.ChatRead));
        Assert.False(AgentClientCapabilities.Satisfies(AgentClientCapabilities.AvatarRead, AgentClientCapabilities.ChatRead));
        Assert.True(AgentClientCapabilities.Satisfies(AgentClientCapabilities.All, AgentClientCapabilities.ChatRead));
    }
}
