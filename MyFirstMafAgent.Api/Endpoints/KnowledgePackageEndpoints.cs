using Microsoft.AspNetCore.Http.Features;
using MyFirstMafAgent.Api.Application.KnowledgePackages;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// 知识库包（导出/导入）：为“搬到另一台机器”“云端备份”“把自己的库分享到市场”准备的可移植格式。
/// </summary>
public static class KnowledgePackageEndpoints
{
    public static void MapKnowledgePackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/knowledge-bases").WithTags("知识库包");

        routes.MapGet("/{knowledgeBaseId:guid}/package", async (
            Guid knowledgeBaseId,
            KnowledgePackageExporter exporter,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            // 1) 先把数据读齐、算好条数：响应头必须在响应体开始之前发出，之后改不了。
            var plan = await exporter.PrepareAsync(knowledgeBaseId, cancellationToken);

            var response = httpContext.Response;
            response.ContentType = "application/zip";
            // 中文库名不能直接进 header，按 RFC 5987 用 filename*=UTF-8''… 编码。
            response.Headers.ContentDisposition =
                $"attachment; filename*=UTF-8''{Uri.EscapeDataString(plan.FileName)}";
            // 统计写进响应头：前端不必再单独请求就能提示“导出了多少条”。
            response.Headers["X-Package-Documents"] = plan.Documents.Count.ToString();
            response.Headers["X-Package-Notes"] = plan.Notes.Count.ToString();
            response.Headers["X-Package-Topics"] = plan.Topics.Count.ToString();
            response.Headers["X-Package-Warnings"] = plan.Warnings.Count.ToString();
            response.Headers["Access-Control-Expose-Headers"] =
                "X-Package-Documents, X-Package-Notes, X-Package-Topics, X-Package-Warnings, Content-Disposition";

            // 2) 直接把 zip 流写进响应体：服务端不落临时文件，客户端边收边存。
            //    注意：ZipArchive 在 Dispose 时同步写中央目录，而 Kestrel 默认禁止同步 IO，
            //    所以只对这一个请求放开（正文拷贝仍是下面的异步循环完成）。
            httpContext.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
            await exporter.WriteAsync(plan, response.Body, cancellationToken);
            return Results.Empty;
        });

        // 注意：这里不能写 async —— lambda 已经是「直接返回 ApiResults 的 Task」，
        // 加了 async 会变成 Task<Task<IResult>>，框架会把内层 Task 当数据去序列化，直接 500。
        routes.MapPost("/import", (
            HttpRequest request,
            KnowledgePackageImporter importer,
            CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest(new { error = "请以 multipart/form-data 上传知识库包。" });

                Microsoft.AspNetCore.Http.IFormCollection form;
                try
                {
                    form = await request.ReadFormAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException
                                                  or Microsoft.AspNetCore.Http.BadHttpRequestException)
                {
                    // 上传中断或 multipart 结构损坏：属于请求本身有问题，回 400 而不是 500。
                    return Results.BadRequest(new { error = "上传内容不完整或格式错误，请重试。" });
                }

                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "没有收到导入文件。" });

                await using var stream = file.OpenReadStream();
                var result = await importer.ImportAsync(stream, cancellationToken);
                return Results.Ok(result);
            }));
    }
}
