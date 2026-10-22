namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>记忆修订历史。旧版本永不覆盖，便于回答“我以前为什么那样想”。</summary>
public sealed class AvatarMemoryVersion
{
    public const string ChangeCreated = "created";
    public const string ChangeUpdated = "updated";
    public const string ChangeMerged = "merged";
    public const string ChangeSuperseded = "superseded";
    public const string ChangeCorrected = "corrected";
    public const string ChangeConfirmed = "confirmed";

    private AvatarMemoryVersion()
    {
    }

    private AvatarMemoryVersion(
        Guid id,
        Guid memoryId,
        int version,
        string snapshotJson,
        string changeType,
        Guid? supersedesId,
        string reason,
        DateTimeOffset createdAt)
    {
        Id = id;
        MemoryId = memoryId;
        Version = version;
        SnapshotJson = snapshotJson;
        ChangeType = changeType;
        SupersedesId = supersedesId;
        Reason = reason;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid MemoryId { get; private set; }
    public int Version { get; private set; }
    public string SnapshotJson { get; private set; } = "{}";
    public string ChangeType { get; private set; } = ChangeCreated;
    public Guid? SupersedesId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public static AvatarMemoryVersion Create(
        Guid memoryId,
        int version,
        string? snapshotJson,
        string changeType,
        Guid? supersedesId,
        string? reason,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            memoryId,
            Math.Max(1, version),
            string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson,
            (changeType ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                ChangeUpdated => ChangeUpdated,
                ChangeMerged => ChangeMerged,
                ChangeSuperseded => ChangeSuperseded,
                ChangeCorrected => ChangeCorrected,
                ChangeConfirmed => ChangeConfirmed,
                _ => ChangeCreated
            },
            supersedesId,
            (reason ?? string.Empty).Trim() is { Length: > 0 } value
                ? (value.Length > 500 ? value[..500] : value)
                : string.Empty,
            now);

    public static AvatarMemoryVersion Restore(
        Guid id,
        Guid memoryId,
        int version,
        string snapshotJson,
        string changeType,
        Guid? supersedesId,
        string reason,
        DateTimeOffset createdAt) =>
        new(id, memoryId, version, snapshotJson, changeType, supersedesId, reason, createdAt);
}
