using MyFirstMafAgent.Api.Application.Knowledge;

namespace MyFirstMafAgent.Api.Endpoints;

public static class DocumentContentEndpoints
{
    public static void MapDocumentContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/documents").WithTags("资料阅读");

        // 原文阅读：按字符偏移分页返回已解析文本，可连续加载全文。
        routes.MapGet("/{documentId:guid}/content", (
            Guid documentId,
            int? offset,
            int? length,
            DocumentContentService content,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await content.GetPageAsync(
                    documentId,
                    offset ?? 0,
                    length is > 0 ? length.Value : DocumentContentService.DefaultPageSize,
                    cancellationToken))));
    }
}
