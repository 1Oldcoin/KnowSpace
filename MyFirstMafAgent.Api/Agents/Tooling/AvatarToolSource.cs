using System.Text.Json;
using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// 把“分身”能力暴露给聊天模型：投喂碎片素材、归纳人格、以用户本人的口径回答。
/// 分身数据与知识库索引完全隔离，互不检索。
/// </summary>
public sealed class AvatarToolSource(
    AvatarService avatars,
    AvatarDigestService digest,
    AvatarPersonaChatService personaChat,
    AvatarIntakeService intake) : IAgentToolSource
{
    public string Name => "avatar";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(AgentToolContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentToolDefinition> tools =
        [
            Create("avatar_get_persona",
                "读取用户分身当前的人格画像（由碎片素材归纳出的洞察）、素材数量与完备度。涉及“我平时会怎么做”的问题前先调用。",
                """
                {"type":"object","properties":{},"required":[]}
                """,
                async (_, token) =>
                {
                    var overview = await avatars.GetOverviewAsync(token);
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
                }),

            Create("avatar_feed",
                "把用户刚刚讲的一段内容（日记、经历、想法、习惯、自我描述）投喂给分身。会由 AI 自动整理成记忆卡并向量化，无需用户按格式填写。用户说“我今天……”“我一般会……”时主动投喂。",
                """
                {"type":"object","properties":{"content":{"type":"string","description":"要保存的原始内容，尽量保留用户原话"}},"required":["content"]}
                """,
                async (arguments, token) =>
                {
                    var result = await intake.FeedTextAsync(
                        GetRequiredString(arguments, "content"), null, "chat", token);
                    return JsonSerializer.Serialize(new
                    {
                        reply = result.Reply,
                        ingestionId = result.IngestionId,
                        status = result.IngestionStatus,
                        warning = result.Warning
                    }, JsonOptions);
                }),

            Create("avatar_search_memories",
                "在用户的分身记忆里检索（向量 + 关键词混合），回答“我以前怎么看/怎么做/喜欢什么”之前先调用。",
                """
                {"type":"object","properties":{"query":{"type":"string","description":"要检索的主题或问题"},"limit":{"type":"integer","description":"返回条数，默认 8"}},"required":["query"]}
                """,
                async (arguments, token) =>
                {
                    var limit = arguments.TryGetProperty("limit", out var value) && value.TryGetInt32(out var parsed)
                        ? parsed
                        : 8;
                    var memories = await intake.ListMemoriesAsync(
                        GetRequiredString(arguments, "query"), limit, token);
                    return JsonSerializer.Serialize(memories.Select(memory => new
                    {
                        memory.KindLabel,
                        memory.Title,
                        memory.Summary,
                        memory.OccurredAt,
                        memory.Importance,
                        memory.Tags
                    }), JsonOptions);
                }),

            Create("avatar_digest",
                "让 AI 重新归纳人格画像：读取新增素材，提取性格、价值观、习惯、语言风格、决策方式等洞察。用户说“更新一下我的画像”时调用。",
                """
                {"type":"object","properties":{"includeAll":{"type":"boolean","description":"是否连同已分析过的素材一起重跑，默认 false"}},"required":[]}
                """,
                async (arguments, token) =>
                {
                    var includeAll = arguments.TryGetProperty("includeAll", out var value) && value.ValueKind == JsonValueKind.True;
                    var result = await digest.DigestAsync(includeAll, token);
                    return JsonSerializer.Serialize(new
                    {
                        message = $"已从 {result.FragmentCount} 段素材归纳出 {result.InsightCount} 条人格洞察，还有 {result.PendingFragmentCount} 段待分析。",
                        insights = result.Insights.Select(item => new
                        {
                            item.DimensionLabel,
                            item.Title,
                            item.Content,
                            item.Confidence
                        })
                    }, JsonOptions);
                }),

            Create("avatar_ask_as_me",
                "以用户本人的身份（第一人称）回答一个问题：按我的性格、习惯、见识和语言风格，我会怎么处理。",
                """
                {"type":"object","properties":{"question":{"type":"string","description":"要替用户本人回答的问题或情境"}},"required":["question"]}
                """,
                async (arguments, token) =>
                {
                    var reply = await personaChat.AskAsync(null, GetRequiredString(arguments, "question"), token);
                    return reply.Answer;
                })
        ];

        return Task.FromResult(tools);
    }

    private static AgentToolDefinition Create(
        string name,
        string description,
        string schema,
        Func<JsonElement, CancellationToken, Task<string>> handler) =>
        new(name, description, schema, handler, "avatar");

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetRequiredString(JsonElement arguments, string name) =>
        GetString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"缺少必填参数 {name}。");
}
