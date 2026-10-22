namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// 回答反馈。点赞只是弱信号（用于重排和风格加权），纠正/补充才会创建新的投喂任务。
/// 模型自己生成的回答永远不会被当作关于用户的新事实。
/// </summary>
public sealed class AvatarFeedback
{
    public const string Like = "like";
    public const string Unlike = "unlike";
    public const string Correction = "correction";
    public const string Supplement = "supplement";

    public const string ReasonFactError = "fact_error";
    public const string ReasonJudgmentMismatch = "judgment_mismatch";
    public const string ReasonOverInference = "over_inference";
    public const string ReasonToneMismatch = "tone_mismatch";
    public const string ReasonRelationshipError = "relationship_error";
    public const string ReasonTimeError = "time_error";
    public const string ReasonMissingMemory = "missing_memory";
    public const string ReasonOther = "other";

    private AvatarFeedback()
    {
    }

    private AvatarFeedback(
        Guid id,
        long? chatMessageId,
        IReadOnlyList<Guid> memoryIds,
        string feedbackType,
        string reasonCode,
        string comment,
        string correctedText,
        Guid? ingestionId,
        DateTimeOffset createdAt)
    {
        Id = id;
        ChatMessageId = chatMessageId;
        MemoryIds = memoryIds;
        FeedbackType = feedbackType;
        ReasonCode = reasonCode;
        Comment = comment;
        CorrectedText = correctedText;
        IngestionId = ingestionId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public long? ChatMessageId { get; private set; }
    public IReadOnlyList<Guid> MemoryIds { get; private set; } = [];
    public string FeedbackType { get; private set; } = Like;
    public string ReasonCode { get; private set; } = string.Empty;
    public string Comment { get; private set; } = string.Empty;
    public string CorrectedText { get; private set; } = string.Empty;

    /// <summary>纠正/补充对应的后台投喂任务。</summary>
    public Guid? IngestionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AvatarFeedback Create(
        long? chatMessageId,
        IReadOnlyList<Guid>? memoryIds,
        string? feedbackType,
        string? reasonCode,
        string? comment,
        string? correctedText,
        Guid? ingestionId,
        DateTimeOffset now)
    {
        var type = NormalizeType(feedbackType);
        var reason = NormalizeReason(reasonCode);
        var correction = Truncate((correctedText ?? string.Empty).Trim(), 20_000);
        var note = Truncate((comment ?? string.Empty).Trim(), 4_000);

        if (type == Unlike && reason.Length == 0)
            throw new AvatarValidationException("“不像我”需要选择一个原因，方便我修正。");
        if (type is Correction or Supplement && correction.Length == 0 && note.Length == 0)
            throw new AvatarValidationException("纠正或补充需要写下正确的内容。");

        return new AvatarFeedback(
            Guid.NewGuid(),
            chatMessageId is > 0 ? chatMessageId : null,
            (memoryIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(50).ToList(),
            type,
            reason,
            note,
            correction,
            ingestionId,
            now);
    }

    public static AvatarFeedback Restore(
        Guid id,
        long? chatMessageId,
        IReadOnlyList<Guid> memoryIds,
        string feedbackType,
        string reasonCode,
        string comment,
        string correctedText,
        Guid? ingestionId,
        DateTimeOffset createdAt) =>
        new(id, chatMessageId, memoryIds, NormalizeType(feedbackType), reasonCode,
            comment, correctedText, ingestionId, createdAt);

    public static IReadOnlyList<(string Value, string Label)> Types { get; } =
    [
        (Like, "像我"),
        (Unlike, "不像我"),
        (Correction, "纠正"),
        (Supplement, "补充")
    ];

    public static IReadOnlyList<(string Value, string Label)> Reasons { get; } =
    [
        (ReasonFactError, "记错事实"),
        (ReasonJudgmentMismatch, "判断不符"),
        (ReasonOverInference, "过度推断"),
        (ReasonToneMismatch, "语气不对"),
        (ReasonRelationshipError, "关系理解错误"),
        (ReasonTimeError, "时间错误"),
        (ReasonMissingMemory, "漏掉重要记忆"),
        (ReasonOther, "其它")
    ];

    private static string NormalizeType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Unlike or "不像我" => Unlike,
            Correction or "纠正" => Correction,
            Supplement or "补充" => Supplement,
            _ => Like
        };

    private static string NormalizeReason(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            ReasonFactError => ReasonFactError,
            ReasonJudgmentMismatch => ReasonJudgmentMismatch,
            ReasonOverInference => ReasonOverInference,
            ReasonToneMismatch => ReasonToneMismatch,
            ReasonRelationshipError => ReasonRelationshipError,
            ReasonTimeError => ReasonTimeError,
            ReasonMissingMemory => ReasonMissingMemory,
            ReasonOther => ReasonOther,
            _ => string.Empty
        };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
