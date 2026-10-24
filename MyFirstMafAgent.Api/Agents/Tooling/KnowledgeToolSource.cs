using System.Text.Json;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>把本地知识库的检索与阅读能力暴露为聊天模型可调用的工具。</summary>
public sealed class KnowledgeToolSource(
    KnowledgeSearchService search,
    DocumentContentService content,
    KnowledgeBaseStore store) : IAgentToolSource
{
    public string Name => "knowledge";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(AgentToolContext context, CancellationToken cancellationToken)
    {
        var knowledgeBaseId = context.KnowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;
        // 对话绑定知识库时，模型只能看这个库；未绑定则维持跨库检索。
        var scope = context.KnowledgeBaseId;

        IReadOnlyList<AgentToolDefinition> tools =
        [
            new AgentToolDefinition(
                "list_knowledge_bases",
                scope is null
                    ? "列出用户本地的全部知识库（id 与名称）。"
                    : "列出本次对话可访问的知识库（已按对话范围限定）。",
                """
                {"type":"object","properties":{},"required":[]}
                """,
                async (_, token) =>
                {
                    var items = await store.ListKnowledgeBasesAsync(token);
                    var visible = AgentKnowledgeScope.VisibleKnowledgeBaseIds(scope, items.Select(item => item.Id));
                    var filtered = items.Where(item => visible.Contains(item.Id));
                    return JsonSerializer.Serialize(new
                    {
                        scoped = scope is not null,
                        items = filtered.Select(item => new { id = item.Id, name = item.Name })
                    }, JsonOptions);
                },
                "knowledge"),

            new AgentToolDefinition(
                "list_documents",
                "列出指定知识库中的资料（文件名、状态、大小）。默认使用当前对话所属知识库。",
                """
                {"type":"object","properties":{"knowledgeBaseId":{"type":"string","description":"知识库 ID，留空表示当前知识库"}},"required":[]}
                """,
                async (arguments, token) =>
                {
                    var target = ParseGuid(arguments, "knowledgeBaseId") ?? knowledgeBaseId;
                    EnsureScope(scope, target);

                    var documents = await store.ListDocumentsAsync(target, token);
                    return JsonSerializer.Serialize(documents.Select(item => new
                    {
                        documentId = item.Id,
                        fileName = item.FileName,
                        status = item.Status,
                        extension = item.Extension,
                        sizeBytes = item.SizeBytes,
                        summary = item.Summary
                    }), JsonOptions);
                },
                "knowledge"),

            new AgentToolDefinition(
                "search_knowledge_base",
                "在本地知识库中做语义检索，返回最相关的资料片段与相似度。回答资料相关问题前应优先调用。",
                """
                {"type":"object","properties":{"query":{"type":"string","description":"检索问题或关键词"},"limit":{"type":"integer","description":"返回条数，默认 5，最大 20"},"knowledgeBaseId":{"type":"string","description":"知识库 ID，留空表示当前知识库"}},"required":["query"]}
                """,
                async (arguments, token) =>
                {
                    var query = GetRequiredString(arguments, "query");
                    var limit = GetInt(arguments, "limit") ?? 5;
                    var target = ParseGuid(arguments, "knowledgeBaseId") ?? knowledgeBaseId;
                    EnsureScope(scope, target);

                    var outcome = await search.SearchWithDiagnosticsAsync(query, target, limit, token);
                    return JsonSerializer.Serialize(new
                    {
                        indexId = outcome.IndexId,
                        embeddingSpec = outcome.EmbeddingSpecName,
                        warning = outcome.Warning,
                        results = outcome.Chunks.Select(item => new
                        {
                            documentId = item.DocumentId,
                            fileName = item.FileName,
                            similarity = Math.Round(item.Similarity, 4),
                            content = item.Content
                        })
                    }, JsonOptions);
                },
                "knowledge"),

            new AgentToolDefinition(
                "read_document_content",
                "按字符偏移分页读取已解析资料的原文，用于核对上下文或读取长文档。",
                """
                {"type":"object","properties":{"documentId":{"type":"string","description":"资料 ID"},"offset":{"type":"integer","description":"起始字符偏移，默认 0"},"length":{"type":"integer","description":"读取长度，默认 8000，最大 40000"}},"required":["documentId"]}
                """,
                async (arguments, token) =>
                {
                    var documentId = ParseGuid(arguments, "documentId")
                        ?? throw new ArgumentException("缺少必填参数 documentId。");

                    // 资料 ID 也能猜到：按资料所属知识库再校验一次作用域。
                    if (scope is not null)
                    {
                        var documentKnowledgeBase = await store.GetDocumentKnowledgeBaseIdAsync(documentId, token);
                        if (documentKnowledgeBase is { } owner
                            && AgentKnowledgeScope.Check(scope, owner) is { } denied)
                        {
                            throw new InvalidOperationException(denied);
                        }
                    }

                    var page = await content.GetPageAsync(
                        documentId,
                        GetInt(arguments, "offset") ?? 0,
                        GetInt(arguments, "length") ?? DocumentContentService.DefaultPageSize,
                        token);
                    return JsonSerializer.Serialize(new
                    {
                        page.DocumentId,
                        page.FileName,
                        page.Offset,
                        page.TotalLength,
                        page.HasMore,
                        page.Content
                    }, JsonOptions);
                },
                "knowledge")
        ];

        return Task.FromResult(tools);
    }

    /// <summary>越权访问直接报错，让模型看到原因并向用户说明，而不是静默返回空结果。</summary>
    private static void EnsureScope(Guid? scope, Guid target)
    {
        if (AgentKnowledgeScope.Check(scope, target) is { } denied)
            throw new InvalidOperationException(denied);
    }

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GetRequiredString(JsonElement arguments, string name) =>
        GetString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"缺少必填参数 {name}。");

    private static int? GetInt(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static Guid? ParseGuid(JsonElement arguments, string name) =>
        Guid.TryParse(GetString(arguments, name), out var id) ? id : null;
}
