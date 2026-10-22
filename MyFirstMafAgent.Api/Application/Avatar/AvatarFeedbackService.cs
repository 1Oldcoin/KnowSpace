using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 回答反馈闭环：
/// like 只作为弱正向信号；unlike 必须带原因；
/// correction / supplement 会创建新的投喂任务，走记忆提取与版本修订。
/// 模型生成的回答永远不会被当成关于用户的新事实。
/// </summary>
public sealed class AvatarFeedbackService(
    IAvatarRepository repository,
    AvatarIngestionService ingestion,
    ILogger<AvatarFeedbackService> logger)
{
    public async Task<AvatarFeedbackDto> SubmitAsync(
        long? chatMessageId,
        IReadOnlyList<Guid>? memoryIds,
        string? feedbackType,
        string? reasonCode,
        string? comment,
        string? correctedText,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedType = NormalizeType(feedbackType);

        var cited = (memoryIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (cited.Count == 0 && chatMessageId is > 0)
        {
            var message = await repository.GetChatMessageAsync(chatMessageId.Value, cancellationToken);
            cited = message?.Citations.Select(citation => citation.MemoryId).Distinct().ToList() ?? [];
        }

        Guid? ingestionId = null;
        if (normalizedType is AvatarFeedback.Correction or AvatarFeedback.Supplement)
        {
            var text = !string.IsNullOrWhiteSpace(correctedText) ? correctedText : comment;
            if (string.IsNullOrWhiteSpace(text))
                throw new AvatarValidationException("纠正或补充需要写下正确的内容。");

            var enqueue = await ingestion.EnqueueTextAsync(
                text,
                null,
                normalizedType == AvatarFeedback.Correction
                    ? AvatarIngestion.SourceCorrection
                    : AvatarIngestion.SourceSupplement,
                chatMessageId is null ? null : $"chat:{chatMessageId}",
                normalizedType == AvatarFeedback.Correction ? cited : null,
                cancellationToken);
            ingestionId = enqueue.IngestionId;
        }

        var feedback = AvatarFeedback.Create(
            chatMessageId,
            cited,
            normalizedType,
            reasonCode,
            comment,
            correctedText,
            ingestionId,
            now);

        await repository.AddFeedbackAsync(feedback, cancellationToken);

        // like 只影响后续重排，不改人格；纠正/补充交给后台整理任务处理。
        logger.LogInformation(
            "Avatar feedback {Type} ({Reason}) recorded for {MemoryCount} memories; ingestion {IngestionId}",
            feedback.FeedbackType, feedback.ReasonCode, feedback.MemoryIds.Count, feedback.IngestionId);

        return AvatarFeedbackDto.FromDomain(feedback);
    }

    public async Task<IReadOnlyList<AvatarFeedbackDto>> ListAsync(
        int limit,
        CancellationToken cancellationToken) =>
        (await repository.ListFeedbackAsync(limit, cancellationToken))
        .Select(AvatarFeedbackDto.FromDomain)
        .ToList();

    private static string NormalizeType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            AvatarFeedback.Unlike or "不像我" => AvatarFeedback.Unlike,
            AvatarFeedback.Correction or "纠正" => AvatarFeedback.Correction,
            AvatarFeedback.Supplement or "补充" => AvatarFeedback.Supplement,
            _ => AvatarFeedback.Like
        };
}
