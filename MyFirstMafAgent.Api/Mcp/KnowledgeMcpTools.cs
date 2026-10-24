using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Mcp;

/// <summary>
/// 本机知识库的 MCP 服务端工具。其他大模型/智能体可通过 /mcp 端点读取本地知识库。
/// </summary>
[McpServerToolType]
public sealed class KnowledgeMcpTools(
    KnowledgeBaseStore store,
    KnowledgeSearchService search,
    DocumentContentService content,
    SummaryService summaries,
    KnowledgeIngestionService ingestion,
    IHttpContextAccessor httpContext,
    AgentWorkspace workspace)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "list_knowledge_bases")]
    [Description("列出本机知识库的 ID 与名称。受限客户端只能看到自己授权范围内的库。")]
    public async Task<string> ListKnowledgeBasesAsync(CancellationToken cancellationToken)
    {
        var items = await store.ListKnowledgeBasesAsync(cancellationToken);

        // 受限客户端不应该通过列表接口看到范围之外的知识库名字（元数据也是信息）。
        var client = AgentClientGate.GetClient(httpContext.HttpContext);
        if (client is { IsKnowledgeBaseRestricted: true })
            items = items.Where(item => client.CanAccessKnowledgeBase(item.Id)).ToList();

        return JsonSerializer.Serialize(items.Select(item => new { id = item.Id, name = item.Name }), JsonOptions);
    }

    [McpServerTool(Name = "list_documents")]
    [Description("列出指定知识库中的资料，包括文件名、状态、类型和摘要。不传 knowledgeBaseId 时使用默认知识库。")]
    public async Task<string> ListDocumentsAsync(
        [Description("知识库 ID，可留空")] string? knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        var id = ParseKnowledgeBaseId(knowledgeBaseId);
        var documents = await store.ListDocumentsAsync(id, cancellationToken);
        return JsonSerializer.Serialize(documents.Select(item => new
        {
            documentId = item.Id,
            fileName = item.FileName,
            extension = item.Extension,
            status = item.Status,
            sizeBytes = item.SizeBytes,
            summary = item.Summary
        }), JsonOptions);
    }

    [McpServerTool(Name = "search_knowledge_base")]
    [Description("在本地知识库中做语义检索，返回最相关的资料片段和相似度。回答资料问题前应先调用。")]
    public async Task<string> SearchKnowledgeBaseAsync(
        [Description("检索问题或关键词")] string query,
        [Description("返回条数，1-20，默认 5")] int limit = 5,
        [Description("知识库 ID，可留空")] string? knowledgeBaseId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "[错误] query 不能为空。";

        var id = ParseKnowledgeBaseId(knowledgeBaseId);
        var outcome = await search.SearchWithDiagnosticsAsync(query, id, limit, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            knowledgeBaseId = id,
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
    }

    [McpServerTool(Name = "get_document_content")]
    [Description("按字符偏移分页读取已解析资料的原文。")]
    public async Task<string> GetDocumentContentAsync(
        [Description("资料 ID")] string documentId,
        [Description("起始字符偏移，默认 0")] int offset = 0,
        [Description("读取长度，默认 8000，最大 40000")] int length = 8_000,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(documentId, out var id))
            return "[错误] documentId 不是有效的 GUID。";

        var page = await content.GetPageAsync(id, offset, length, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            page.DocumentId,
            page.FileName,
            page.Offset,
            page.TotalLength,
            page.HasMore,
            page.Content
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_document_summary")]
    [Description("读取资料已缓存的摘要。")]
    public async Task<string> GetDocumentSummaryAsync(
        [Description("资料 ID")] string documentId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(documentId, out var id))
            return "[错误] documentId 不是有效的 GUID。";

        var summary = await summaries.GetLatestAsync(id, cancellationToken);
        return summary is null
            ? "[无摘要] 该资料还没有生成摘要。"
            : summary.Summary;
    }

    [McpServerTool(Name = "get_document_status")]
    [Description("查询资料的解析/分块/索引状态与进度。上传后用它确认是否可用。")]
    public async Task<string> GetDocumentStatusAsync(
        [Description("资料 ID")] string documentId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(documentId, out var id))
            return "[错误] documentId 不是有效的 GUID。";

        var document = await store.GetDocumentAsync(id, cancellationToken);
        if (document is null) return $"[错误] 资料不存在：{id:D}";

        return JsonSerializer.Serialize(new
        {
            documentId = document.Id,
            fileName = document.FileName,
            status = document.Status,
            progress = document.Progress,
            processingStage = document.ProcessingStage,
            chunkCount = document.ChunkCount,
            error = document.Error,
            createdAt = document.CreatedAt
        }, JsonOptions);
    }

    [McpServerTool(Name = "upload_document")]
    [Description("把一段文本作为新资料写入指定知识库（写操作）。写入后会排队解析、分块并建立索引，可用 get_document_status 跟踪进度。")]
    public async Task<string> UploadDocumentAsync(
        [Description("资料标题")] string title,
        [Description("资料正文（Markdown 或纯文本）")] string content,
        [Description("文件名，可留空；留空时由标题生成 .md 文件名")] string? fileName = null,
        [Description("目标知识库 ID，可留空（默认知识库）")] string? knowledgeBaseId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return "[错误] title 不能为空。";
        if (string.IsNullOrWhiteSpace(content)) return "[错误] content 不能为空。";

        var id = ParseKnowledgeBaseId(knowledgeBaseId);
        if (await store.GetKnowledgeBaseAsync(id, cancellationToken) is null)
            return $"[错误] 知识库不存在：{id:D}";

        var name = BuildDocumentFileName(title, fileName);
        var contentType = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? "text/markdown; charset=utf-8"
            : "text/plain; charset=utf-8";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        var upload = new FormFile(stream, 0, stream.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };

        var response = await ingestion.UploadAsync(upload, id, cancellationToken);

        return JsonSerializer.Serialize(new
        {
            documentId = response.Id,
            fileName = response.FileName,
            status = response.Status,
            knowledgeBaseId = id,
            hint = "解析在后台进行，可用 get_document_status 查询进度。"
        }, JsonOptions);
    }

    [McpServerTool(Name = "workspace_list_files")]
    [Description("列出智能体本地工作目录中的文件。")]
    public Task<string> ListWorkspaceFilesAsync(
        [Description("相对工作目录的子目录，可留空")] string? path = null,
        [Description("文件通配符，例如 *.md")] string? pattern = null,
        [Description("是否递归子目录")] bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = workspace.EnumerateFiles(path, pattern, recursive)
            .Select(file => new
            {
                path = workspace.ToRelative(file.FullName),
                size = file.Length,
                modified = file.LastWriteTimeUtc.ToString("O")
            })
            .ToList();
        return Task.FromResult(JsonSerializer.Serialize(new { count = files.Count, files }, JsonOptions));
    }

    [McpServerTool(Name = "workspace_read_file")]
    [Description("读取智能体本地工作目录中的文本文件。")]
    public Task<string> ReadWorkspaceFileAsync(
        [Description("相对工作目录的文件路径")] string path,
        [Description("起始字符偏移，默认 0")] int offset = 0,
        [Description("最多读取字符数，默认 4000")] int maxChars = 4_000,
        CancellationToken cancellationToken = default) =>
        workspace.ReadAsync(path, offset, maxChars, cancellationToken);

    [McpServerTool(Name = "workspace_write_file")]
    [Description("把文本写入智能体本地工作目录（自动创建子目录）。这是写操作，请仅在用户明确要求时使用。")]
    public Task<string> WriteWorkspaceFileAsync(
        [Description("相对工作目录的文件路径")] string path,
        [Description("要写入的完整文本")] string content,
        [Description("是否追加，默认覆盖")] bool append = false,
        CancellationToken cancellationToken = default) =>
        workspace.WriteAsync(path, content, append, cancellationToken);

    private static Guid ParseKnowledgeBaseId(string? value) =>
        Guid.TryParse(value, out var id) ? id : KnowledgeBaseStore.DefaultKnowledgeBaseId;

    /// <summary>
    /// 生成安全的文件名：外部客户端传入的文件名一律当作不可信输入，
    /// 去掉目录成分与非法字符，并强制限定为文本扩展名。
    /// </summary>
    private static string BuildDocumentFileName(string title, string? fileName)
    {
        var candidate = string.IsNullOrWhiteSpace(fileName) ? title : fileName!;
        candidate = Path.GetFileName(candidate.Replace('\\', '/')).Trim();

        var extension = Path.GetExtension(candidate);
        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".md";
        }

        var stem = Path.GetFileNameWithoutExtension(candidate);
        var safe = new StringBuilder(stem.Length);
        foreach (var character in stem)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or ' ') safe.Append(character);
        }

        var trimmed = safe.ToString().Trim();
        if (trimmed.Length == 0) trimmed = "document";
        if (trimmed.Length > 80) trimmed = trimmed[..80];

        return trimmed + extension.ToLowerInvariant();
    }
}
