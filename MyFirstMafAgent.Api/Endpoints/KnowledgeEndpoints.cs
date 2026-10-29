using System.Text;
using MyFirstMafAgent.Api.Agents;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Chats;
using MyFirstMafAgent.Api.Contracts;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Endpoints;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        var knowledgeBaseRoutes = api.MapGroup("/knowledge-bases").WithTags("知识库");
        var documentRoutes = api.MapGroup("/documents").WithTags("资料");
        var uploadRoutes = api.MapGroup("/uploads").WithTags("分块上传");
        var historyRoutes = knowledgeBaseRoutes.MapGroup("/{knowledgeBaseId:guid}/sessions").WithTags("问答历史");

        knowledgeBaseRoutes.MapGet("", async (KnowledgeBaseStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListKnowledgeBasesAsync(cancellationToken)));

        knowledgeBaseRoutes.MapPost("", async (
            CreateKnowledgeBaseRequest request,
            KnowledgeBaseStore store,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
                return Results.BadRequest(new { error = "知识库名称必填且不能超过 100 个字符。" });

            var knowledgeBase = await store.CreateKnowledgeBaseAsync(request.Name.Trim(), cancellationToken);
            return knowledgeBase is null
                ? Results.Conflict(new { error = "已存在同名知识库。" })
                : Results.Ok(knowledgeBase);
        });

        // 删除知识库是破坏性且不可恢复的动作：资料、切片、向量、笔记、专题、问答历史一起消失。
        // 默认知识库由应用服务拒绝（409），这里只负责把三种结果翻译成状态码。
        knowledgeBaseRoutes.MapDelete("/{knowledgeBaseId:guid}", async (
            Guid knowledgeBaseId,
            KnowledgeBaseDeletionService deletion,
            CancellationToken cancellationToken) =>
        {
            var outcome = await deletion.DeleteAsync(knowledgeBaseId, cancellationToken);
            if (outcome.NotFound) return Results.NotFound(new { error = outcome.Error });
            if (!outcome.Deleted) return Results.Conflict(new { error = outcome.Error });

            var report = outcome.Report!;
            return Results.Ok(new
            {
                knowledgeBaseId = report.KnowledgeBaseId,
                name = report.Name,
                deletedDocuments = report.DocumentCount,
                deletedChunks = report.ChunkCount,
                deletedNotes = report.NoteCount,
                deletedTopics = report.TopicCount,
                deletedChatSessions = report.ChatSessionCount
            });
        });

        documentRoutes.MapGet("", async (
            Guid? knowledgeBaseId,
            KnowledgeBaseStore store,
            CancellationToken cancellationToken) =>
        {
            var id = knowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;
            if (await store.GetKnowledgeBaseAsync(id, cancellationToken) is null) return Results.NotFound();
            return Results.Ok(await store.ListDocumentsAsync(id, cancellationToken));
        });

        documentRoutes.MapGet("/{documentId:guid}", async (
            Guid documentId,
            KnowledgeBaseStore store,
            CancellationToken cancellationToken) =>
        {
            var document = await store.GetDocumentAsync(documentId, cancellationToken);
            return document is null ? Results.NotFound() : Results.Ok(document);
        });

        documentRoutes.MapPost("/{documentId:guid}/retry", async (
            Guid documentId,
            KnowledgeBaseStore store,
            CancellationToken cancellationToken) =>
        {
            var document = await store.GetDocumentAsync(documentId, cancellationToken);
            if (document is null) return Results.NotFound();
            if (document.Status is not ("Failed" or "Ready"))
                return Results.Conflict(new { error = "Only failed or ready documents can be reprocessed." });
            if (!await store.RetryDocumentAsync(documentId, cancellationToken))
                return Results.Conflict(new { error = "The document status changed before it could be retried." });
            return Results.Ok(await store.GetDocumentAsync(documentId, cancellationToken));
        });

        documentRoutes.MapGet("/{documentId:guid}/download", async (
            Guid documentId,
            KnowledgeBaseStore store,
            CancellationToken cancellationToken) =>
        {
            var document = await store.GetDocumentAsync(documentId, cancellationToken);
            if (document is null) return Results.NotFound();
            var storagePath = await store.GetDocumentStoragePathAsync(documentId, cancellationToken);
            if (string.IsNullOrWhiteSpace(storagePath) || !File.Exists(storagePath))
                return Results.NotFound(new { error = "原始文件不存在或已被清理。" });
            return Results.File(storagePath, document.ContentType, document.FileName, enableRangeProcessing: true);
        });

        documentRoutes.MapDelete("/{documentId:guid}", async (
            Guid documentId,
            KnowledgeBaseStore store,
            LocalDocumentStore storage,
            CancellationToken cancellationToken) =>
        {
            var storagePath = await store.DeleteDocumentAsync(documentId, cancellationToken);
            if (storagePath is null) return Results.NotFound();
            storage.DeleteStoredFile(storagePath);
            return Results.NoContent();
        });

        uploadRoutes.MapPost("", async Task<IResult> (
            CreateUploadSessionRequest request,
            ResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var session = await uploads.StartAsync(request.KnowledgeBaseId, request.FileName, request.ContentType,
                    request.SizeBytes, cancellationToken);
                return Results.Ok(new UploadSessionResponse(session.Id.ToString(), session.FileName, session.SizeBytes,
                    session.UploadedBytes, uploads.ChunkSizeBytes, session.Status, session.DocumentId?.ToString()));
            }
            catch (UploadKnowledgeBaseNotFoundException)
            {
                return Results.NotFound(new { error = "The selected knowledge base does not exist." });
            }
            catch (DocumentUploadException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        uploadRoutes.MapGet("/{uploadId:guid}", async (
            Guid uploadId,
            ResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            var session = await uploads.GetAsync(uploadId, cancellationToken);
            return session is null
                ? Results.NotFound()
                : Results.Ok(new UploadSessionResponse(session.Id.ToString(), session.FileName, session.SizeBytes,
                    session.UploadedBytes, uploads.ChunkSizeBytes, session.Status, session.DocumentId?.ToString()));
        });

        uploadRoutes.MapPut("/{uploadId:guid}/chunks", async Task<IResult> (
            Guid uploadId,
            long offset,
            HttpRequest request,
            ResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            if (request.ContentLength is > 0 && request.ContentLength > uploads.ChunkSizeBytes)
                return Results.Json(new { error = $"A single upload chunk cannot exceed {uploads.ChunkSizeBytes} bytes." }, statusCode: StatusCodes.Status413PayloadTooLarge);

            try
            {
                var uploadedBytes = await uploads.AppendChunkAsync(uploadId, offset, request.Body, cancellationToken);
                return Results.Ok(new UploadChunkResponse(uploadedBytes));
            }
            catch (UploadSessionNotFoundException)
            {
                return Results.NotFound();
            }
            catch (UploadOffsetConflictException exception)
            {
                return Results.Conflict(new { error = "The upload offset is out of date.", uploadedBytes = exception.ExpectedOffset });
            }
            catch (DocumentUploadException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        uploadRoutes.MapPost("/{uploadId:guid}/complete", async Task<IResult> (
            Guid uploadId,
            ResumableUploadService uploads,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await uploads.CompleteAsync(uploadId, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (UploadIncompleteException exception)
            {
                return Results.Conflict(new { error = "The upload is incomplete.", uploadedBytes = exception.UploadedBytes, sizeBytes = exception.ExpectedBytes });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (DocumentUploadException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        documentRoutes.MapPost("", async Task<IResult> (
            IFormFile file,
            Guid? knowledgeBaseId,
            KnowledgeBaseStore knowledgeBases,
            KnowledgeIngestionService ingestion,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var id = knowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;
            if (await knowledgeBases.GetKnowledgeBaseAsync(id, cancellationToken) is null) return Results.NotFound();

            try
            {
                return Results.Ok(await ingestion.UploadAsync(file, id, cancellationToken));
            }
            catch (DocumentUploadException exception)
            {
                loggerFactory.CreateLogger("MyFirstMafAgent.Api.DocumentUploads")
                    .LogWarning(exception, "Document upload rejected for {FileName} ({FileSizeBytes} bytes)", file.FileName, file.Length);
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        api.MapPost("/search", async (
            SearchRequest request,
            KnowledgeBaseStore knowledgeBases,
            KnowledgeSearchService search,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query)) return Results.BadRequest(new { error = "Query is required." });
            var id = request.KnowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;
            if (await knowledgeBases.GetKnowledgeBaseAsync(id, cancellationToken) is null) return Results.NotFound();
            var mode = RetrievalModes.Parse(request.Mode);
            var outcome = await search.SearchWithDiagnosticsAsync(request.Query, id, request.Limit, cancellationToken, mode);
            return Results.Ok(new
            {
                results = outcome.Chunks,
                indexId = outcome.IndexId,
                embeddingSpec = outcome.EmbeddingSpecName,
                warning = outcome.Warning,
                // strategy/mode 让评估脚本能确认"这一轮到底跑的是哪条链路"。
                strategy = outcome.Strategy,
                mode = mode?.ToWire() ?? outcome.Strategy,
                // 拒答与得分：评估脚本靠 topScore 标定阈值，前端靠 abstained 提示"资料里没有"。
                abstained = outcome.Abstained,
                topScore = outcome.TopScore,
                abstainThreshold = outcome.AbstainThreshold
            });
        }).WithTags("检索");

        api.MapPost("/chat", async (
            ChatRequest request,
            KnowledgeBaseStore knowledgeBases,
            KnowledgeSearchService search,
            ToolCallingAgent agent,
            AgentPreferenceProvider preferences,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Message is required." });
            var id = request.KnowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;
            if (await knowledgeBases.GetKnowledgeBaseAsync(id, cancellationToken) is null) return Results.NotFound();
            var retrieval = await search.SearchWithDiagnosticsAsync(request.Message, id, 5, cancellationToken);

            // 单轮工具开关：null 沿用设置页偏好，非 null 只覆盖本次请求。
            var effective = (await preferences.GetAsync(cancellationToken))
                .WithOverrides(request.EnableToolCalling, request.EnableWebSearch, request.EnableFileAccess);
            var completion = await agent.AskAsync(request.Message, id, retrieval.Chunks,
                Array.Empty<KnowledgeChatMessage>(), cancellationToken, effective,
                KnowledgeChatService.BuildReferenceNote(retrieval));

            // 引用元数据与多轮问答保持同一形状：rank/chunkId/chunkIndex/snippet 都能回溯原文。
            var citations = KnowledgeChatJson.FromChunks(retrieval.Chunks);

            // 开关状态随响应透出，前端据此提示“已开启工具/联网”，无需再单独查设置。
            var flags = KnowledgeChatFlags.From(effective);
            var retrievalInfo = new KnowledgeChatRetrieval(
                retrieval.IndexId,
                retrieval.EmbeddingSpecName,
                retrieval.Warning,
                retrieval.IndexId is null,
                retrieval.Strategy,
                retrieval.Abstained,
                retrieval.TopScore);
            return Results.Ok(new
            {
                answer = completion.Text,
                citations,
                toolCalls = completion.ToolCalls,
                sources = completion.Sources,
                retrieval = retrievalInfo,
                toolCallingEnabled = flags.ToolCallingEnabled,
                webSearchEnabled = flags.WebSearchEnabled,
                fileAccessEnabled = flags.FileAccessEnabled
            });
        }).WithTags("问答");

        historyRoutes.MapPost("", async (
            Guid knowledgeBaseId,
            NewKnowledgeChatSessionRequest request,
            KnowledgeBaseStore knowledgeBases,
            KnowledgeChatStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (await knowledgeBases.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken) is null) return Results.NotFound();
            var title = string.IsNullOrWhiteSpace(request.Title) ? "新的对话" : request.Title.Trim();
            if (title.Length > 100) return Results.BadRequest(new { error = "对话名称不能超过 100 个字符。" });
            return Results.Ok(await sessions.CreateSessionAsync(knowledgeBaseId, title, cancellationToken));
        });

        historyRoutes.MapGet("", async (
            Guid knowledgeBaseId,
            KnowledgeBaseStore knowledgeBases,
            KnowledgeChatStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (await knowledgeBases.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken) is null) return Results.NotFound();
            return Results.Ok(await sessions.ListSessionsAsync(knowledgeBaseId, cancellationToken));
        });

        historyRoutes.MapPut("/{sessionId:guid}", async (
            Guid knowledgeBaseId,
            Guid sessionId,
            RenameKnowledgeChatSessionRequest request,
            KnowledgeChatStore sessions,
            CancellationToken cancellationToken) =>
        {
            var title = request.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title) || title.Length > 100)
                return Results.BadRequest(new { error = "对话名称必填且不能超过 100 个字符。" });
            return await sessions.RenameSessionAsync(sessionId, knowledgeBaseId, title, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        historyRoutes.MapDelete("/{sessionId:guid}", async (
            Guid knowledgeBaseId,
            Guid sessionId,
            KnowledgeChatStore sessions,
            CancellationToken cancellationToken) =>
            await sessions.DeleteSessionAsync(sessionId, knowledgeBaseId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound());

        historyRoutes.MapPost("/batch-delete", async (
            Guid knowledgeBaseId,
            DeleteKnowledgeChatSessionsRequest request,
            KnowledgeBaseStore knowledgeBases,
            KnowledgeChatStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (await knowledgeBases.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken) is null) return Results.NotFound();
            if (request.SessionIds is not { Count: > 0 and <= 100 })
                return Results.BadRequest(new { error = "请选择 1 到 100 条对话进行删除。" });
            var deletedCount = await sessions.DeleteSessionsAsync(knowledgeBaseId, request.SessionIds, cancellationToken);
            return Results.Ok(new { deletedCount });
        });

        historyRoutes.MapGet("/{sessionId:guid}/export", async (
            Guid knowledgeBaseId,
            Guid sessionId,
            KnowledgeChatStore sessions,
            CancellationToken cancellationToken) =>
        {
            var session = await sessions.GetSessionAsync(sessionId, knowledgeBaseId, cancellationToken);
            if (session is null) return Results.NotFound();
            var messages = await sessions.ListSessionMessagesAsync(sessionId, knowledgeBaseId, cancellationToken);
            var markdown = KnowledgeChatExportFormatter.Format(session, messages);
            var fileName = CreateExportFileName(session.Title);
            return Results.File(Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", $"{fileName}.md");
        });

        historyRoutes.MapGet("/{sessionId:guid}/messages", async (
            Guid knowledgeBaseId,
            Guid sessionId,
            KnowledgeChatStore sessions,
            CancellationToken cancellationToken) =>
        {
            if (!await sessions.SessionBelongsToKnowledgeBaseAsync(sessionId, knowledgeBaseId, cancellationToken)) return Results.NotFound();
            return Results.Ok(await sessions.ListSessionMessagesAsync(sessionId, knowledgeBaseId, cancellationToken));
        });

        historyRoutes.MapPost("/{sessionId:guid}/chat", async (
            Guid knowledgeBaseId,
            Guid sessionId,
            KnowledgeChatRequest request,
            KnowledgeChatService chat,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Message is required." });
            if (request.Message.Length > 8_000) return Results.BadRequest(new { error = "单条消息不能超过 8,000 个字符。" });
            var result = await chat.ChatAsync(knowledgeBaseId, sessionId, request.Message.Trim(), cancellationToken,
                request.EnableToolCalling, request.EnableWebSearch, request.EnableFileAccess);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }

    private static string CreateExportFileName(string title)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var fileName = string.Concat(title.Select(character => char.IsControl(character) || invalidCharacters.Contains(character) ? '_' : character))
            .Trim()
            .Trim('.');
        if (fileName.Length > 80) fileName = fileName[..80];
        return string.IsNullOrWhiteSpace(fileName) ? "问答记录" : fileName;
    }
}
