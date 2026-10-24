using System.Text;
using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Contracts;
using MyFirstMafAgent.Api.Domain.Notes;

namespace MyFirstMafAgent.Api.Endpoints;

public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/knowledge-bases/{knowledgeBaseId:guid}/notes").WithTags("学习笔记");

        routes.MapGet("", (Guid knowledgeBaseId, string? query, string? category, Guid? documentId, Guid? topicId, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await notes.ListAsync(knowledgeBaseId, query, category, cancellationToken, documentId, topicId))));

        routes.MapPost("", (Guid knowledgeBaseId, CreateNoteRequest request, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var note = await notes.CreateAsync(new CreateNoteCommand(
                    knowledgeBaseId,
                    request.Title,
                    request.Content,
                    request.Category,
                    request.Color,
                    request.Tags,
                    request.SourceDocumentId,
                    request.TopicId,
                    ToSources(request.Sources),
                    request.SourceSessionId), cancellationToken);
                return Results.Ok(note);
            }));

        // 批量导出必须在 /{noteId:guid} 之前注册，避免 export 被当成 ID 匹配。
        routes.MapPost("/export", (Guid knowledgeBaseId, ExportNotesRequest? request, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var result = await notes.ExportAsync(knowledgeBaseId, request?.NoteIds, cancellationToken);
                return Results.File(Encoding.UTF8.GetBytes(result.Content), "text/markdown; charset=utf-8", result.FileName);
            }));

        routes.MapGet("/{noteId:guid}", (Guid knowledgeBaseId, Guid noteId, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await notes.GetAsync(knowledgeBaseId, noteId, cancellationToken))));

        routes.MapPut("/{noteId:guid}", (Guid knowledgeBaseId, Guid noteId, UpdateNoteRequest request, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var note = await notes.UpdateAsync(knowledgeBaseId, noteId, new UpdateNoteCommand(
                    request.Title,
                    request.Content,
                    request.Category,
                    request.Color,
                    request.Tags,
                    request.TopicId,
                    ToSources(request.Sources)), cancellationToken);
                return Results.Ok(note);
            }));

        routes.MapPost("/{noteId:guid}/pin", (Guid knowledgeBaseId, Guid noteId, PinNoteRequest request, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await notes.SetPinnedAsync(knowledgeBaseId, noteId, request.Pinned, cancellationToken))));

        routes.MapDelete("/{noteId:guid}", (Guid knowledgeBaseId, Guid noteId, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                await notes.DeleteAsync(knowledgeBaseId, noteId, cancellationToken);
                return Results.NoContent();
            }));
    }

    /// <summary>请求里的可空字段在领域层归一化（空 ID 丢弃、去重、截断），端点只负责换算。</summary>
    private static IReadOnlyList<NoteSource>? ToSources(List<NoteSourceRequest>? sources)
    {
        if (sources is null) return null;
        return sources
            .Where(source => source is not null)
            .Select(source => new NoteSource(
                source.DocumentId,
                source.FileName ?? string.Empty,
                source.ChunkIndex ?? -1,
                source.Similarity ?? 0,
                source.Snippet))
            .ToList();
    }
}
