namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>分身对话会话：以用户本人的口径回答问题，与知识库问答完全隔离。</summary>
public sealed class AvatarChatSession
{
    public const int MaxTitleLength = 120;

    private AvatarChatSession()
    {
    }

    private AvatarChatSession(Guid id, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Id = id;
        Title = title;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AvatarChatSession Create(string? title, DateTimeOffset now)
    {
        var normalized = (title ?? string.Empty).Trim();
        if (normalized.Length == 0) normalized = "新的对话";
        if (normalized.Length > MaxTitleLength) normalized = normalized[..MaxTitleLength];
        return new AvatarChatSession(Guid.NewGuid(), normalized, now, now);
    }

    public static AvatarChatSession Restore(Guid id, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt) =>
        new(id, title, createdAt, updatedAt);
}

public sealed record AvatarChatMessage(
    long Id,
    Guid SessionId,
    string Role,
    string Content,
    IReadOnlyList<AvatarChatCitation> Citations,
    DateTimeOffset CreatedAt);

/// <summary>回答所依据的记忆，便于用户核对“分身为什么这样说”。</summary>
public sealed record AvatarChatCitation(Guid MemoryId, string Kind, string Title, double Score);
