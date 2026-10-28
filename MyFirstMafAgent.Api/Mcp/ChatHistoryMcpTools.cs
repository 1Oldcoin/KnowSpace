using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using MyFirstMafAgent.Api.Application.Chats;
using MyFirstMafAgent.Api.Domain.Chats;
using MyFirstMafAgent.Api.Hosting;

namespace MyFirstMafAgent.Api.Mcp;

/// <summary>
/// 把"过去聊了什么"通过 MCP 暴露给其他智能体。
///
/// <para>
/// 与 <see cref="AvatarMcpTools"/> 的分工：分身工具读的是<b>整理过的记忆</b>，
/// 这里读的是<b>原样的对话流水</b>。外部 Agent 想接上"上次聊到哪"就必须两者都能读，
/// 否则用户上次说过的话对它永远是隐形的。
/// </para>
///
/// <para>
/// 单独用 <c>chat:read</c> 能力而不是复用 <c>knowledge:read</c>：私人对话比资料更敏感，
/// 只拿到知识库权限的客户端不应该顺带看见。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ChatHistoryMcpTools(ChatHistoryService history, IHttpContextAccessor httpContext)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 当前 MCP 客户端的数据范围。网关已经把 OAuth 令牌翻译成同一个 <c>AgentClient</c>，
    /// 因此两条认证通道在这里没有区别。
    /// </summary>
    private ChatHistoryScope? Scope =>
        ChatHistoryScope.FromClient(AgentClientGate.GetClient(httpContext.HttpContext));

    [McpServerTool(Name = "list_chat_sessions")]
    [Description("列出保存下来的对话（知识库问答 / 分身对话 / 伴侣对话），按最近更新排序。用来定位\"上次那个对话\"。受限客户端只能看到授权范围内知识库的问答。")]
    public async Task<string> ListSessionsAsync(
        [Description("来源过滤：knowledge / avatar / companion / companion-import / all，默认 all")] string? source = null,
        [Description("只看某个知识库的问答历史（仅作用于 knowledge 来源，要只看它请同时传 source=knowledge）")] Guid? knowledgeBaseId = null,
        [Description("只看某个伴侣档案的对话（仅作用于 companion / companion-import 来源）")] Guid? companionProfileId = null,
        [Description("返回条数，默认 20，最大 100")] int limit = ChatHistoryService.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var result = await history.ListSessionsAsync(
            source, knowledgeBaseId, companionProfileId, limit, cancellationToken, Scope);
        if (!result.Succeeded)
        {
            return JsonSerializer.Serialize(new { error = result.Error }, JsonOptions);
        }

        var sessions = result.Value ?? [];
        return JsonSerializer.Serialize(new
        {
            count = sessions.Count,
            sessions = sessions.Select(session => new
            {
                session.Source,
                sourceLabel = ChatHistorySources.Describe(session.Source),
                session.SessionId,
                session.Title,
                session.ScopeLabel,
                session.MessageCount,
                session.CreatedAt,
                session.UpdatedAt,
                session.LastMessagePreview
            })
        }, JsonOptions);
    }

    [McpServerTool(Name = "chat_search_history")]
    [Description("在保存的聊天历史里按关键词检索，返回命中的原话与所在对话。query 留空则返回最近的消息。受限客户端只能检索授权范围内知识库的问答。")]
    public async Task<string> SearchAsync(
        [Description("要查找的关键词或问法（可选，留空取最近消息）")] string? query = null,
        [Description("来源过滤：knowledge / avatar / companion / companion-import / all，默认 all")] string? source = null,
        [Description("只搜某个知识库的问答历史（仅作用于 knowledge 来源，要只搜它请同时传 source=knowledge）")] Guid? knowledgeBaseId = null,
        [Description("只搜某个伴侣档案的对话（仅作用于 companion / companion-import 来源）")] Guid? companionProfileId = null,
        [Description("返回条数，默认 20，最大 100")] int limit = ChatHistoryService.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var result = await history.SearchAsync(
            query, source, knowledgeBaseId, companionProfileId, limit, cancellationToken, Scope);
        if (!result.Succeeded)
        {
            return JsonSerializer.Serialize(new { error = result.Error }, JsonOptions);
        }

        var messages = result.Value ?? [];
        return JsonSerializer.Serialize(new
        {
            count = messages.Count,
            query = string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            matches = messages.Select(message => new
            {
                message.Source,
                sourceLabel = ChatHistorySources.Describe(message.Source),
                message.SessionId,
                message.SessionTitle,
                message.Role,
                message.CreatedAt,
                message.Snippet,
                message.Content
            })
        }, JsonOptions);
    }
}
