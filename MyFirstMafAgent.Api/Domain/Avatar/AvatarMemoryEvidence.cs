namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// 记忆到原文的证据。每条规范化记忆至少有一条证据，用户以后纠正时可以回到最初出处。
/// </summary>
public sealed class AvatarMemoryEvidence
{
    public const int MaxEvidenceTextLength = 4_000;

    private AvatarMemoryEvidence()
    {
    }

    private AvatarMemoryEvidence(
        Guid id,
        Guid memoryId,
        Guid? fragmentId,
        string evidenceText,
        int startOffset,
        int endOffset,
        int? pageNo,
        string? messageId,
        string? sourceHash,
        DateTimeOffset? occurredAt,
        DateTimeOffset recordedAt)
    {
        Id = id;
        MemoryId = memoryId;
        FragmentId = fragmentId;
        EvidenceText = evidenceText;
        StartOffset = startOffset;
        EndOffset = endOffset;
        PageNo = pageNo;
        MessageId = messageId;
        SourceHash = sourceHash;
        OccurredAt = occurredAt;
        RecordedAt = recordedAt;
    }

    public Guid Id { get; private set; }
    public Guid MemoryId { get; private set; }
    public Guid? FragmentId { get; private set; }

    /// <summary>原文摘录，用来证明这条记忆不是说谎或凭空推断。</summary>
    public string EvidenceText { get; private set; } = string.Empty;

    public int StartOffset { get; private set; }
    public int EndOffset { get; private set; }
    public int? PageNo { get; private set; }
    public string? MessageId { get; private set; }
    public string? SourceHash { get; private set; }

    /// <summary>事件发生时间（原文提到的时间）。</summary>
    public DateTimeOffset? OccurredAt { get; private set; }

    /// <summary>系统收到这条信息的时间。</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    public static AvatarMemoryEvidence Create(
        Guid memoryId,
        Guid? fragmentId,
        string? evidenceText,
        int startOffset,
        int endOffset,
        int? pageNo,
        string? messageId,
        string? sourceHash,
        DateTimeOffset? occurredAt,
        DateTimeOffset now)
    {
        var text = (evidenceText ?? string.Empty).Trim();
        if (text.Length > MaxEvidenceTextLength) text = text[..MaxEvidenceTextLength];
        if (startOffset < 0) startOffset = 0;
        if (endOffset < startOffset) endOffset = startOffset + text.Length;

        return new AvatarMemoryEvidence(
            Guid.NewGuid(),
            memoryId,
            fragmentId,
            text,
            startOffset,
            endOffset,
            pageNo,
            string.IsNullOrWhiteSpace(messageId) ? null : messageId.Trim(),
            string.IsNullOrWhiteSpace(sourceHash) ? null : sourceHash.Trim(),
            occurredAt,
            now);
    }

    public static AvatarMemoryEvidence Restore(
        Guid id,
        Guid memoryId,
        Guid? fragmentId,
        string evidenceText,
        int startOffset,
        int endOffset,
        int? pageNo,
        string? messageId,
        string? sourceHash,
        DateTimeOffset? occurredAt,
        DateTimeOffset recordedAt) =>
        new(id, memoryId, fragmentId, evidenceText, startOffset, endOffset, pageNo,
            messageId, sourceHash, occurredAt, recordedAt);
}
