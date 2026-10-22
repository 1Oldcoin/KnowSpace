using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Mcp;

/// <summary>
/// 把“分身”通过 MCP 暴露给其他智能体：读取人格画像、投喂素材、归纳人格、以本人身份回答。
/// </summary>
[McpServerToolType]
public sealed class AvatarMcpTools(
    AvatarService avatars,
    AvatarDigestService digest,
    AvatarPersonaChatService personaChat,
    AvatarIntakeService intake)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "avatar_get_persona")]
    [Description("读取用户分身的人格画像、素材数量与完备度。")]
    public async Task<string> GetPersonaAsync(CancellationToken cancellationToken)
    {
        var overview = await avatars.GetOverviewAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            profile = overview.Profile,
            readinessScore = overview.ReadinessScore,
            missingInputs = overview.MissingInputs,
            counts = new
            {
                fragments = overview.FragmentCount,
                memories = overview.MemoryCount,
                pendingVectors = overview.PendingVectorCount,
                undigested = overview.UndigestedCount,
                insights = overview.InsightCount,
                conversations = overview.ChatSessionCount
            },
            insights = overview.Insights
        }, JsonOptions);
    }

    [McpServerTool(Name = "avatar_feed")]
    [Description("把一段自由文本投喂给分身：后台会自动整理成带原文证据的记忆卡并向量化，不需要固定格式。")]
    public async Task<string> FeedAsync(
        [Description("要保存的原始内容")] string content,
        CancellationToken cancellationToken = default)
    {
        var result = await intake.FeedTextAsync(content, null, "chat", cancellationToken);
        return JsonSerializer.Serialize(new
        {
            reply = result.Reply,
            ingestionId = result.IngestionId,
            status = result.IngestionStatus,
            warning = result.Warning
        }, JsonOptions);
    }

    [McpServerTool(Name = "avatar_search_memories")]
    [Description("在用户的分身记忆里做向量 + 关键词混合检索。")]
    public async Task<string> SearchMemoriesAsync(
        [Description("要检索的主题或问题")] string query,
        [Description("返回条数，默认 8，最大 50")] int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var memories = await intake.ListMemoriesAsync(query, Math.Clamp(limit, 1, 50), cancellationToken);
        return JsonSerializer.Serialize(memories.Select(memory => new
        {
            memory.Id,
            memory.MemoryTypeLabel,
            memory.LifecycleLabel,
            memory.Title,
            memory.Summary,
            memory.OccurredAt,
            memory.Confidence,
            memory.Importance,
            memory.EvidenceCount,
            memory.Tags
        }), JsonOptions);
    }

    [McpServerTool(Name = "avatar_digest")]
    [Description("让 AI 归纳用户的人格画像（性格、价值观、习惯、语言风格、决策方式）。")]
    public async Task<string> DigestAsync(
        [Description("是否连同已分析素材一起重跑，默认 false")] bool includeAll = false,
        CancellationToken cancellationToken = default)
    {
        var result = await digest.DigestAsync(includeAll, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            message = $"已从 {result.FragmentCount} 段素材归纳出 {result.InsightCount} 条洞察，还有 {result.PendingFragmentCount} 段待分析。",
            insights = result.Insights
        }, JsonOptions);
    }

    [McpServerTool(Name = "avatar_ask_as_me")]
    [Description("以用户本人的第一人称身份回答：按我的性格、习惯与见识，我会怎么处理这个问题。")]
    public async Task<string> AskAsMeAsync(
        [Description("要替用户本人回答的问题或情境")] string question,
        CancellationToken cancellationToken = default)
    {
        var reply = await personaChat.AskAsync(null, question, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            answer = reply.Answer,
            citations = reply.Citations
        }, JsonOptions);
    }

    [McpServerTool(Name = "avatar_list_fragments")]
    [Description("列出用户投喂的分身素材（按时间倒序）。")]
    public async Task<string> ListFragmentsAsync(
        [Description("返回条数，默认 20，最大 100")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var fragments = await avatars.ListFragmentsAsync(Math.Clamp(limit, 1, 100), cancellationToken);
        return JsonSerializer.Serialize(fragments.Select(item => new
        {
            item.OccurredAt,
            item.KindLabel,
            item.Title,
            content = item.Content.Length > 400 ? item.Content[..400] + "…" : item.Content,
            item.IsDigested
        }), JsonOptions);
    }
}
