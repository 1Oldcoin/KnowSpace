using MyFirstMafAgent.Api.Application.Topics;
using MyFirstMafAgent.Api.Contracts;
using MyFirstMafAgent.Api.Domain.Topics;

namespace MyFirstMafAgent.Api.Endpoints;

public static class TopicEndpoints
{
    public static void MapTopicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var topicRoutes = endpoints.MapGroup("/api/knowledge-bases/{knowledgeBaseId:guid}/topics").WithTags("专题");
        var documentRoutes = endpoints.MapGroup("/api/documents").WithTags("资料");

        topicRoutes.MapGet("", (Guid knowledgeBaseId, TopicService topics, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await topics.ListAsync(knowledgeBaseId, cancellationToken))));

        topicRoutes.MapPost("", (Guid knowledgeBaseId, CreateTopicRequest request, TopicService topics, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await topics.CreateAsync(knowledgeBaseId, request.Name, request.Color, cancellationToken))));

        topicRoutes.MapPut("/{topicId:guid}", (Guid knowledgeBaseId, Guid topicId, UpdateTopicRequest request, TopicService topics, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await topics.UpdateAsync(knowledgeBaseId, topicId, request.Name, request.Color, cancellationToken))));

        topicRoutes.MapDelete("/{topicId:guid}", (Guid knowledgeBaseId, Guid topicId, TopicService topics, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                await topics.DeleteAsync(knowledgeBaseId, topicId, cancellationToken);
                return Results.NoContent();
            }));

        documentRoutes.MapPut("/{documentId:guid}/topic", (Guid documentId, AssignDocumentTopicRequest request, TopicService topics, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var result = await topics.AssignDocumentAsync(documentId, request.TopicId, cancellationToken);
                return result switch
                {
                    DocumentTopicAssignResult.Success => Results.NoContent(),
                    DocumentTopicAssignResult.TopicNotFound => Results.NotFound(new { error = "专题不存在。" }),
                    _ => Results.NotFound(new { error = "资料不存在。" })
                };
            }));

        documentRoutes.MapGet("/{documentId:guid}/topic", (Guid documentId, TopicService topics, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(new { topicId = await topics.GetDocumentTopicAsync(documentId, cancellationToken) })));
    }
}
