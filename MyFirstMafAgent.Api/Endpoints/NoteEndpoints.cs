using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Contracts;

namespace MyFirstMafAgent.Api.Endpoints;

public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/knowledge-bases/{knowledgeBaseId:guid}/notes").WithTags("学习笔记");

        routes.MapGet("", (Guid knowledgeBaseId, string? query, string? category, NoteService notes, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await notes.ListAsync(knowledgeBaseId, query, category, cancellationToken))));

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
                    request.SourceDocumentId), cancellationToken);
                return Results.Ok(note);
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
                    request.Tags), cancellationToken);
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
}
