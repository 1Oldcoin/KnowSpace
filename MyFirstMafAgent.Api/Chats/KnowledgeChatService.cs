using MyFirstMafAgent.Api.Agents;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Domain.Settings;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Chats;

public sealed class KnowledgeChatService(
    KnowledgeChatStore store,
    KnowledgeSearchService search,
    ToolCallingAgent agent,
    AgentPreferenceProvider preferences,
    AvatarActivityRecorder activityRecorder)
{
    /// <summary>
    /// 单轮问答。三个工具开关为可空值：null 表示沿用设置页偏好，
    /// 非 null 时只覆盖本次问答（不落库），用于问答页上的临时工具开关。
    /// </summary>
    public async Task<KnowledgeChatResult?> ChatAsync(
        Guid knowledgeBaseId,
        Guid sessionId,
        string message,
        CancellationToken cancellationToken,
        bool? enableToolCalling = null,
        bool? enableWebSearch = null,
        bool? enableFileAccess = null)
    {
        if (!await store.SessionBelongsToKnowledgeBaseAsync(sessionId, knowledgeBaseId, cancellationToken)) return null;

        // 同一轮问答里复用同一次偏好读数，保证“响应提示”与“Agent 实际使用的开关”一致。
        var userPreferences = (await preferences.GetAsync(cancellationToken))
            .WithOverrides(enableToolCalling, enableWebSearch, enableFileAccess);

        var history = await store.ListRecentSessionMessagesAsync(sessionId, knowledgeBaseId, 30, cancellationToken);
        var references = await search.SearchAsync(message, knowledgeBaseId, 5, cancellationToken);
        var answer = await agent.AskAsync(message, knowledgeBaseId, references, history, cancellationToken, userPreferences);
        await store.AddSessionExchangeAsync(sessionId, knowledgeBaseId, message, answer.Text, cancellationToken);

        // 时刻记录：把问答活动沉淀为分身的行为数据（30 分钟内同类只记一次）。
        await activityRecorder.TryRecordAsync("学习",
            $"知识库问答：{(message.Length > 40 ? message[..40] + "…" : message)}",
            5,
            "chat",
            cancellationToken);

        var citations = references.Select(item => new KnowledgeChatCitation(item.DocumentId, item.FileName, item.Similarity)).ToList();
        var flags = KnowledgeChatFlags.From(userPreferences);
        return new KnowledgeChatResult(answer.Text, citations, answer.ToolCalls, answer.Sources,
            flags.ToolCallingEnabled, flags.WebSearchEnabled, flags.FileAccessEnabled);
    }
}

/// <summary>
/// 问答响应中透出的开关状态，便于前端提示“当前是否在用工具/联网/本地文件”。
/// 工具调用取用户意向即可：全局总闸关闭时 registry 不提供工具，前端展示为不可用。
/// </summary>
public sealed record KnowledgeChatFlags(bool ToolCallingEnabled, bool WebSearchEnabled, bool FileAccessEnabled = false)
{
    public static KnowledgeChatFlags From(AgentPreferences preferences) =>
        new(preferences.EnableToolCalling, preferences.EnableWebSearch, preferences.EnableFileAccess);
}

public sealed record KnowledgeChatResult(
    string Answer,
    IReadOnlyList<KnowledgeChatCitation> Citations,
    IReadOnlyList<AgentToolCallTrace>? ToolCalls = null,
    IReadOnlyList<string>? Sources = null,
    bool ToolCallingEnabled = false,
    bool WebSearchEnabled = false,
    bool FileAccessEnabled = false);

public sealed record KnowledgeChatCitation(Guid DocumentId, string FileName, double Similarity);
