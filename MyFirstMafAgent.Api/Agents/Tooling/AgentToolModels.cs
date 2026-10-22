using System.Text.Json;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>工具作用域上下文：决定知识库工具访问哪个库。</summary>
public sealed record AgentToolContext(Guid? KnowledgeBaseId, Guid? SessionId = null);

/// <summary>
/// 一个可被聊天模型调用的工具。参数使用 JSON Schema 描述，
/// 处理函数接收模型给出的参数 JSON 并返回文本结果。
/// </summary>
public sealed record AgentToolDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema,
    Func<JsonElement, CancellationToken, Task<string>> Handler,
    string Category = "local");

/// <summary>工具来源（本地文件、知识库、MCP 桥接）。</summary>
public interface IAgentToolSource
{
    string Name { get; }

    Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(AgentToolContext context, CancellationToken cancellationToken);
}

/// <summary>一次工具执行的审计记录，随聊天响应返回便于排查。</summary>
public sealed record AgentToolCallTrace(
    string Name,
    string Arguments,
    string ResultPreview,
    bool Succeeded,
    long ElapsedMilliseconds);

public sealed record AgentToolCall(string Id, string Name, string Arguments);

/// <summary>OpenAI 兼容协议中的一条消息，支持工具调用与工具结果。</summary>
public sealed record AgentChatTurn(
    string Role,
    string? Content,
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null);

public sealed record AgentCompletion(string? Content, IReadOnlyList<AgentToolCall> ToolCalls);

public sealed record AgentAnswer(
    string Text,
    IReadOnlyList<AgentToolCallTrace> ToolCalls,
    IReadOnlyList<string>? Sources = null)
{
    public static AgentAnswer Plain(string text) => new(text, []);

    /// <summary>带上本轮从网页工具收集到的来源链接；没有链接时保持 null，避免前端显示空区块。</summary>
    public AgentAnswer WithSources(IReadOnlyList<string>? sources) =>
        sources is { Count: > 0 } ? this with { Sources = sources } : this;
}
