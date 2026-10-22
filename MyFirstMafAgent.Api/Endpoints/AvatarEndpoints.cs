using System.Text;
using Microsoft.AspNetCore.Mvc;
using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Contracts;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Endpoints;

public static class AvatarEndpoints
{
    private const int MaxUploadBytes = 10 * 1024 * 1024;
    private const int MaxSegmentChars = 3_000;

    public static void MapAvatarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var avatar = endpoints.MapGroup("/api/avatar").WithTags("分身");

        avatar.MapGet("", (AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.GetOverviewAsync(cancellationToken))));

        avatar.MapPut("/profile", (SaveAvatarProfileRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var profile = await service.SaveProfileAsync(new AvatarProfileDraft(
                    request.DisplayName,
                    request.Headline,
                    request.Personality,
                    request.Values,
                    request.Strengths,
                    request.Weaknesses,
                    request.Skills,
                    request.Resources,
                    request.Constraints,
                    request.LongTermGoals,
                    request.RiskPreference), cancellationToken);
                return Results.Ok(profile);
            }));

        // ---- 投喂（聊天式，AI 自动整理） ----

        avatar.MapPost("/feed", (FeedAvatarRequest request, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await intake.FeedTextAsync(
                    request.Text, request.OccurredAt, "chat", cancellationToken))));

        avatar.MapGet("/feed", (int? limit, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await intake.ListFeedAsync(limit ?? 60, cancellationToken))));

        avatar.MapDelete("/feed", (AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                await intake.ClearFeedAsync(cancellationToken);
                return Results.NoContent();
            }));

        avatar.MapPost("/feed/upload", async Task<IResult> (
            [FromForm] string? occurrenceDate,
            IFormFile file,
            AvatarIntakeService intake,
            CancellationToken cancellationToken) =>
        {
            if (file.Length is <= 0 or > MaxUploadBytes)
                return Results.BadRequest(new { error = "文件不能为空，且不能超过 10 MB。" });

            try
            {
                using var memory = new MemoryStream();
                await file.CopyToAsync(memory, cancellationToken);
                var text = DecodeText(memory.ToArray());
                DateTimeOffset? occurredAt = DateTimeOffset.TryParse(occurrenceDate, out var parsed) ? parsed : null;
                var result = await intake.FeedFileAsync(file.FileName, text, occurredAt, cancellationToken);
                return Results.Ok(result);
            }
            catch (DecoderFallbackException)
            {
                return Results.BadRequest(new { error = "文件编码无法识别，请使用 UTF-8 保存后重试。" });
            }
            catch (AvatarValidationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        // ---- 整理后的记忆 ----

        avatar.MapGet("/memories", (string? query, int? limit, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await intake.ListMemoriesAsync(query, limit ?? 100, cancellationToken))));

        avatar.MapDelete("/memories/{memoryId:guid}", (Guid memoryId, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await intake.DeleteMemoryAsync(memoryId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));

        avatar.MapPost("/memories/reindex", (ReindexAvatarMemoriesRequest? request, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await intake.ReindexAsync(
                    request?.All ?? false, request?.Limit ?? 200, cancellationToken))));

        avatar.MapPost("/memories/migrate", (int? limit, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await intake.OrganizeLegacyFragmentsAsync(limit ?? 12, cancellationToken))));

        // ---- v2：投喂任务状态 / 证据 / 版本 ----

        avatar.MapGet("/ingestions", (int? limit, AvatarIngestionService ingestion, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await ingestion.ListIngestionsAsync(limit ?? 30, cancellationToken))));

        avatar.MapGet("/ingestions/{ingestionId:guid}", (Guid ingestionId, AvatarIngestionService ingestion, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var task = await ingestion.GetIngestionAsync(ingestionId, cancellationToken);
                return task is null ? Results.NotFound() : Results.Ok(task);
            }));

        avatar.MapPost("/ingestions/{ingestionId:guid}/retry", (Guid ingestionId, AvatarIngestionService ingestion, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await ingestion.RetryAsync(ingestionId, cancellationToken)
                    ? Results.Ok(new { retried = true })
                    : Results.NotFound()));

        avatar.MapGet("/memories/{memoryId:guid}/evidence", (Guid memoryId, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await intake.ListEvidenceAsync(memoryId, cancellationToken))));

        avatar.MapGet("/memories/{memoryId:guid}/versions", (Guid memoryId, AvatarIntakeService intake, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await intake.ListVersionsAsync(memoryId, cancellationToken))));

        // ---- v2：反馈闭环 ----

        avatar.MapPost("/feedback", (AvatarFeedbackRequest request, AvatarFeedbackService feedback, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await feedback.SubmitAsync(
                    request.ChatMessageId,
                    request.MemoryIds,
                    request.FeedbackType,
                    request.ReasonCode,
                    request.Comment,
                    request.CorrectedText,
                    cancellationToken))));

        avatar.MapGet("/feedback", (int? limit, AvatarFeedbackService feedback, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await feedback.ListAsync(limit ?? 50, cancellationToken))));

        // ---- v2：人格洞察候选 ----

        avatar.MapGet("/insights/candidates", (int? limit, IAvatarRepository repository, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok((await repository.ListInsightVersionsAsync(
                        AvatarInsightVersion.StatusCandidate, limit ?? 50, cancellationToken))
                    .Select(AvatarInsightVersionDto.FromDomain)
                    .ToList())));

        avatar.MapPost("/insights/candidates/{versionId:guid}/confirm", (Guid versionId, AvatarInsightService insights, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var confirmed = await insights.ConfirmAsync(versionId, cancellationToken);
                return confirmed is null
                    ? Results.NotFound()
                    : Results.Ok(AvatarInsightVersionDto.FromDomain(confirmed));
            }));

        // ---- v2：固定评测集与回归 ----

        avatar.MapGet("/eval/cases", (bool? includeInactive, AvatarEvalService eval, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await eval.ListCasesAsync(includeInactive ?? false, cancellationToken))));

        avatar.MapPost("/eval/cases", (CreateAvatarEvalCaseRequest request, AvatarEvalService eval, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await eval.AddCaseAsync(new AvatarEvalCaseDraft(
                    request.Category,
                    request.Question,
                    request.ExpectedAnswer,
                    request.MustMemoryIds,
                    request.ForbiddenMemoryIds,
                    request.Rubric,
                    request.Split,
                    request.Active), cancellationToken))));

        avatar.MapDelete("/eval/cases/{caseId:guid}", (Guid caseId, AvatarEvalService eval, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await eval.DeleteCaseAsync(caseId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));

        avatar.MapPost("/eval/run", (RunAvatarEvalRequest? request, AvatarEvalService eval, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await eval.RunAsync(
                    request?.IncludeHidden ?? false,
                    request?.Limit,
                    cancellationToken))));

        avatar.MapGet("/eval/runs", (int? limit, AvatarEvalService eval, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await eval.ListRunsAsync(limit ?? 20, cancellationToken))));

        avatar.MapGet("/eval/runs/{runId:guid}", (Guid runId, AvatarEvalService eval, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var run = await eval.GetRunAsync(runId, cancellationToken);
                return run is null ? Results.NotFound() : Results.Ok(run);
            }));

        // ---- 旧版碎片接口（保留兼容） ----

        avatar.MapGet("/fragments", (int? limit, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.ListFragmentsAsync(limit ?? 100, cancellationToken))));

        avatar.MapPost("/fragments", (CreateAvatarFragmentRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var fragment = await service.AddFragmentAsync(new AvatarFragmentDraft(
                    request.Kind,
                    request.Title,
                    request.Content,
                    request.OccurredAt,
                    "manual",
                    request.Tags), cancellationToken);
                return Results.Ok(fragment);
            }));

        avatar.MapPost("/fragments/bulk", (BulkCreateAvatarFragmentsRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var fragments = await service.AddFragmentsAsync(
                    request.Kind,
                    request.Contents ?? [],
                    request.OccurredAt,
                    "manual",
                    cancellationToken);
                return Results.Ok(fragments);
            }));

        avatar.MapPost("/fragments/upload", async Task<IResult> (
            [FromForm] string? kind,
            [FromForm] string? occurrenceDate,
            IFormFile file,
            AvatarService service,
            CancellationToken cancellationToken) =>
        {
            if (file.Length is <= 0 or > MaxUploadBytes)
                return Results.BadRequest(new { error = "文件不能为空，且不能超过 10 MB。" });

            try
            {
                using var memory = new MemoryStream();
                await file.CopyToAsync(memory, cancellationToken);
                var text = DecodeText(memory.ToArray());
                var segments = SplitIntoSegments(text);
                if (segments.Count == 0)
                    return Results.BadRequest(new { error = "文件内容为空或无法识别为文本。" });

                DateTimeOffset? occurredAt = DateTimeOffset.TryParse(occurrenceDate, out var parsed) ? parsed : null;
                var fragments = await service.AddFragmentsAsync(kind, segments, occurredAt, "upload", cancellationToken);
                return Results.Ok(new { imported = fragments.Count, fragments });
            }
            catch (DecoderFallbackException)
            {
                return Results.BadRequest(new { error = "文件编码无法识别，请使用 UTF-8 保存后重试。" });
            }
        }).DisableAntiforgery();

        avatar.MapDelete("/fragments/{fragmentId:guid}", (Guid fragmentId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await service.DeleteFragmentAsync(fragmentId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));

        // ---- 人格洞察 ----

        avatar.MapGet("/insights", (AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.ListInsightsAsync(cancellationToken))));

        avatar.MapPost("/digest", (AvatarDigestRequest? request, AvatarDigestService digest, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await digest.DigestAsync(request?.IncludeDigested ?? false, cancellationToken))));

        avatar.MapDelete("/insights", (AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                await service.ClearInsightsAsync(cancellationToken);
                return Results.NoContent();
            }));

        // ---- 问答（第一人称分身） ----

        avatar.MapGet("/chat/sessions", (AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.ListChatSessionsAsync(cancellationToken))));

        avatar.MapPost("/chat/sessions", (CreateAvatarChatSessionRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.CreateChatSessionAsync(request.Title, cancellationToken))));

        avatar.MapGet("/chat/sessions/{sessionId:guid}/messages", (Guid sessionId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var messages = await service.ListChatMessagesAsync(sessionId, cancellationToken);
                return messages is null ? Results.NotFound() : Results.Ok(messages);
            }));

        avatar.MapDelete("/chat/sessions/{sessionId:guid}", (Guid sessionId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await service.DeleteChatSessionAsync(sessionId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));

        avatar.MapPost("/chat", (AvatarChatRequest request, AvatarPersonaChatService chat, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await chat.AskAsync(request.SessionId, request.Message ?? string.Empty, cancellationToken))));

        // ---- 推演（保留能力） ----

        avatar.MapPost("/simulations", (RunAvatarSimulationRequest request, AvatarSimulationService simulations, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await simulations.RunAsync(
                    new AvatarSimulationDraft(request.Scenario, request.Goal, request.HorizonMonths),
                    cancellationToken))));

        avatar.MapGet("/simulations", (int? limit, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.ListSimulationsAsync(limit ?? 20, cancellationToken))));

        avatar.MapGet("/simulations/{simulationId:guid}", (Guid simulationId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var simulation = await service.GetSimulationAsync(simulationId, cancellationToken);
                return simulation is null ? Results.NotFound() : Results.Ok(simulation);
            }));

        avatar.MapDelete("/simulations/{simulationId:guid}", (Guid simulationId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await service.DeleteSimulationAsync(simulationId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));

        // ---- 补充输入：经历 / 行为 / 关系 ----

        avatar.MapGet("/experiences", (int? limit, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.ListExperiencesAsync(limit ?? 50, cancellationToken))));

        avatar.MapPost("/experiences", (CreateAvatarExperienceRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.AddExperienceAsync(new AvatarExperienceDraft(
                    request.OccurredAt,
                    request.Category,
                    request.Title,
                    request.Content,
                    request.Outcome,
                    request.Emotion,
                    request.Impact,
                    request.Tags), cancellationToken))));

        avatar.MapDelete("/experiences/{experienceId:guid}", (Guid experienceId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await service.DeleteExperienceAsync(experienceId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));

        avatar.MapGet("/behaviors", (int? days, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.ListBehaviorsAsync(days ?? 30, cancellationToken))));

        avatar.MapPost("/behaviors", (CreateAvatarBehaviorRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.AddBehaviorAsync(new AvatarBehaviorDraft(
                    request.OccurredOn,
                    request.Category,
                    request.Description,
                    request.DurationMinutes,
                    request.Mood,
                    request.Tags,
                    "manual"), cancellationToken))));

        avatar.MapDelete("/behaviors/{behaviorId:guid}", (Guid behaviorId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await service.DeleteBehaviorAsync(behaviorId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));

        avatar.MapGet("/relationships", (AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.ListRelationshipsAsync(cancellationToken))));

        avatar.MapPost("/relationships", (SaveAvatarRelationshipRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await service.AddRelationshipAsync(new AvatarRelationshipDraft(
                    request.Name,
                    request.Role,
                    request.Closeness,
                    request.Influence,
                    request.Notes), cancellationToken))));

        avatar.MapPut("/relationships/{relationshipId:guid}", (Guid relationshipId, SaveAvatarRelationshipRequest request, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var relationship = await service.UpdateRelationshipAsync(relationshipId, new AvatarRelationshipDraft(
                    request.Name,
                    request.Role,
                    request.Closeness,
                    request.Influence,
                    request.Notes), cancellationToken);
                return relationship is null ? Results.NotFound() : Results.Ok(relationship);
            }));

        avatar.MapDelete("/relationships/{relationshipId:guid}", (Guid relationshipId, AvatarService service, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                await service.DeleteRelationshipAsync(relationshipId, cancellationToken)
                    ? Results.NoContent()
                    : Results.NotFound()));
    }

    /// <summary>上传文本的编码识别：优先 UTF-8，失败回退 GB18030。</summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    /// <summary>按空行与长度切分，让一个文件变成多条可独立检索的碎片。</summary>
    private static List<string> SplitIntoSegments(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var segments = new List<string>();
        var buffer = new StringBuilder();

        void Flush()
        {
            var value = buffer.ToString().Trim();
            buffer.Clear();
            if (value.Length > 0) segments.Add(value.Length > MaxSegmentChars ? value[..MaxSegmentChars] : value);
        }

        foreach (var line in normalized.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (buffer.Length + line.Length > MaxSegmentChars) Flush();
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(line);
        }

        Flush();
        return segments.Take(200).ToList();
    }
}
