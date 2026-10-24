using Microsoft.AspNetCore.Mvc;
using MyFirstMafAgent.Api.Companions;
using MyFirstMafAgent.Api.Contracts;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Endpoints;

public static class CompanionEndpoints
{
    public static void MapCompanionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var companions = endpoints.MapGroup("/api/companions").WithTags("AI 伴侣");

        companions.MapGet("", async (CompanionStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListProfilesAsync(cancellationToken)));

        companions.MapPost("/import", async Task<IResult> (
            [FromForm] string name,
            [FromForm] string targetSender,
            [FromForm] string platform,
            IFormFile file,
            ChatHistoryParser parser,
            CompanionStore store,
            CompanionMemoryIndexer indexer,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
                return Results.BadRequest(new { error = "人物名称必填且不能超过 100 个字符。" });
            if (string.IsNullOrWhiteSpace(targetSender) || targetSender.Length > 100)
                return Results.BadRequest(new { error = "请填写导出记录中对应人物的发言者名称。" });
            if (string.IsNullOrWhiteSpace(platform) || platform.Length > 40)
                return Results.BadRequest(new { error = "请填写有效的平台名称（最多 40 个字符）。" });
            if (file.Length is <= 0 or > 20 * 1024 * 1024)
                return Results.BadRequest(new { error = "聊天记录文件不能为空，且不能超过 20 MB。" });

            try
            {
                using var stream = new StreamReader(file.OpenReadStream(), new System.Text.UTF8Encoding(false, true));
                var content = await stream.ReadToEndAsync(cancellationToken);
                var messages = parser.Parse(file.FileName, content);
                var selectedSender = messages.FirstOrDefault(message =>
                    string.Equals(message.Sender, targetSender.Trim(), StringComparison.OrdinalIgnoreCase))?.Sender;
                if (selectedSender is null)
                    return Results.BadRequest(new { error = "导入记录中未找到该发言者，请确认名称与导出文件一致。" });
                var profile = await store.ImportAsync(name.Trim(), selectedSender, platform.Trim(), messages, cancellationToken);
                loggerFactory.CreateLogger("MyFirstMafAgent.Api.CompanionImports")
                    .LogInformation("Imported companion profile {ProfileId} with {MessageCount} messages from {FileName}",
                        profile.Id, messages.Count, file.FileName);

                // 向量化是后台任务：几万条消息的 embedding 不能让导入请求挂着。
                // 失败也不影响导入结果——检索会退回词面匹配，接口里的 indexed 会保持 0。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await indexer.IndexAsync(
                            profile.Id, profile.TargetSender, CompanionMemoryIndexer.DefaultBatchSize, CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        loggerFactory.CreateLogger("MyFirstMafAgent.Api.CompanionImports")
                            .LogWarning(exception, "Companion indexing failed for {ProfileId}; staying keyword-only", profile.Id);
                    }
                }, CancellationToken.None);

                return Results.Ok(profile);
            }
            catch (ChatImportException exception)
            {
                loggerFactory.CreateLogger("MyFirstMafAgent.Api.CompanionImports")
                    .LogWarning(exception, "Companion import rejected for {FileName}", file.FileName);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (System.Text.DecoderFallbackException exception)
            {
                loggerFactory.CreateLogger("MyFirstMafAgent.Api.CompanionImports")
                    .LogWarning(exception, "Companion import file is not valid UTF-8: {FileName}", file.FileName);
                return Results.BadRequest(new { error = "文件必须使用 UTF-8 编码。" });
            }
        }).DisableAntiforgery();

        companions.MapDelete("/{profileId:guid}", async (Guid profileId, CompanionStore store, CancellationToken cancellationToken) =>
            await store.DeleteProfileAsync(profileId, cancellationToken) ? Results.NoContent() : Results.NotFound());

        companions.MapPost("/{profileId:guid}/reindex", async (
            Guid profileId,
            int? limit,
            CompanionStore store,
            CompanionMemoryIndexer indexer,
            CancellationToken cancellationToken) =>
        {
            var profile = await store.GetProfileAsync(profileId, cancellationToken);
            if (profile is null) return Results.NotFound();

            var outcome = await indexer.IndexAsync(
                profile.Id, profile.TargetSender, limit ?? CompanionMemoryIndexer.DefaultBatchSize, cancellationToken);
            var indexed = await indexer.CountIndexedAsync(profileId, cancellationToken);

            return Results.Ok(new
            {
                profileId,
                indexed = outcome.Indexed,
                failed = outcome.Failed,
                total = indexed,
                warning = outcome.Warning
            });
        });

        companions.MapGet("/{profileId:guid}/index", async (
            Guid profileId,
            CompanionStore store,
            CompanionMemoryIndexer indexer,
            CancellationToken cancellationToken) =>
        {
            var profile = await store.GetProfileAsync(profileId, cancellationToken);
            if (profile is null) return Results.NotFound();
            return Results.Ok(new { profileId, indexed = await indexer.CountIndexedAsync(profileId, cancellationToken) });
        });

        companions.MapPost("/{profileId:guid}/persona", async (
            Guid profileId,
            CompanionService companion,
            CancellationToken cancellationToken) =>
        {
            var profile = await companion.GeneratePersonaAsync(profileId, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        companions.MapPost("/{profileId:guid}/sessions", async (
            Guid profileId,
            NewCompanionSessionRequest request,
            CompanionStore store,
            CancellationToken cancellationToken) =>
        {
            var profile = await store.GetProfileAsync(profileId, cancellationToken);
            if (profile is null) return Results.NotFound();
            var title = string.IsNullOrWhiteSpace(request.Title) ? "新的陪伴对话" : request.Title.Trim();
            return Results.Ok(await store.CreateSessionAsync(profileId, title[..Math.Min(title.Length, 100)], cancellationToken));
        });

        companions.MapGet("/{profileId:guid}/sessions", async (
            Guid profileId,
            CompanionStore store,
            CancellationToken cancellationToken) =>
        {
            var profile = await store.GetProfileAsync(profileId, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(await store.ListSessionsAsync(profileId, cancellationToken));
        });

        companions.MapGet("/sessions/{sessionId:guid}/messages", async (
            Guid sessionId,
            CompanionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.ListSessionMessagesAsync(sessionId, cancellationToken)));

        companions.MapPost("/{profileId:guid}/sessions/{sessionId:guid}/chat", async (
            Guid profileId,
            Guid sessionId,
            CompanionChatRequest request,
            CompanionService companion,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Message is required." });
            if (request.Message.Length > 8_000) return Results.BadRequest(new { error = "单条消息不能超过 8,000 个字符。" });
            var result = await companion.ChatAsync(profileId, sessionId, request.Message.Trim(), cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }
}
