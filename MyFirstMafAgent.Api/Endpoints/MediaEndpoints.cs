using MyFirstMafAgent.Api.Documents;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// 语音转写模型（Whisper / ggml）的管理端点。
///
/// <para>
/// 存在的理由：模型 141 MB 起，既不该硬塞进安装包，也不该让离线用户在导入视频时
/// 卡在一个必然失败的下载上。所以把它变成一件"可以查看、可以导入、可以点按下载"的资产。
/// </para>
/// </summary>
public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/media").WithTags("媒体能力");

        // 当前状态：路径、体积、是否在下载、下载进度、上次失败原因。
        routes.MapGet("/whisper", (WhisperModelManager manager) => Results.Ok(manager.GetStatus()));

        // 后台下载：立刻返回 202，前端轮询 GET 看进度。
        routes.MapPost("/whisper/download", (WhisperModelManager manager) =>
        {
            var status = manager.StartBackgroundDownload();
            return Results.Json(status, statusCode: StatusCodes.Status202Accepted);
        });

        // 导入本地模型（原始字节流，推荐）：141 MB～3 GB 的模型不该走 multipart 内存/临时文件缓冲。
        // 文件名通过查询参数传，例如 ?fileName=ggml-base.bin。
        routes.MapPost("/whisper/import-raw", async (
            string? fileName,
            HttpRequest request,
            WhisperModelManager manager,
            CancellationToken cancellationToken) =>
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? "ggml-model.bin" : Path.GetFileName(fileName);
            if (!string.Equals(Path.GetExtension(name), ".bin", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Whisper 模型必须是 .bin 文件（whisper.cpp 的 ggml 格式，如 ggml-base.bin）。" });

            try
            {
                var status = await manager.ImportAsync(name, request.Body, cancellationToken);
                return Results.Ok(status);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (IOException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        // 导入本地模型（multipart，兼容脚本与低版本的旧调用）。
        routes.MapPost("/whisper/import", async (HttpRequest request, WhisperModelManager manager, CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "请使用 multipart/form-data 上传模型文件。" });

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "没有收到模型文件（表单字段名应为 file）。" });

            try
            {
                await using var stream = file.OpenReadStream();
                var status = await manager.ImportAsync(file.FileName, stream, cancellationToken);
                return Results.Ok(status);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (IOException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        // 删除模型：用于回收磁盘，也是验证"离线提示"的最快方式。
        routes.MapDelete("/whisper", (WhisperModelManager manager) => Results.Ok(manager.Delete()));
    }
}
