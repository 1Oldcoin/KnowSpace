namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>一段重要的人际关系，会影响推演中的支持与阻力。</summary>
public sealed class AvatarRelationship
{
    public const int MaxNameLength = 60;
    public const int MaxNotesLength = 4_000;

    private AvatarRelationship()
    {
    }

    private AvatarRelationship(
        Guid id,
        string name,
        string role,
        int closeness,
        int influence,
        string notes,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Name = name;
        Role = role;
        Closeness = closeness;
        Influence = influence;
        Notes = notes;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Role { get; private set; } = "其它";

    /// <summary>亲近程度 1-5。</summary>
    public int Closeness { get; private set; } = 3;

    /// <summary>对决策与情绪的影响程度 1-5。</summary>
    public int Influence { get; private set; } = 3;

    public string Notes { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AvatarRelationship Create(AvatarRelationshipDraft draft, DateTimeOffset now)
    {
        var name = NormalizeRequired(draft.Name, "关系对象名称", MaxNameLength);
        var notes = NormalizeOptional(draft.Notes, "关系说明", MaxNotesLength);
        return new AvatarRelationship(
            Guid.NewGuid(),
            name,
            NormalizeRole(draft.Role),
            Math.Clamp(draft.Closeness <= 0 ? 3 : draft.Closeness, 1, 5),
            Math.Clamp(draft.Influence <= 0 ? 3 : draft.Influence, 1, 5),
            notes,
            now,
            now);
    }

    public void Update(AvatarRelationshipDraft draft, DateTimeOffset now)
    {
        Name = NormalizeRequired(draft.Name, "关系对象名称", MaxNameLength);
        Role = NormalizeRole(draft.Role);
        Closeness = Math.Clamp(draft.Closeness <= 0 ? 3 : draft.Closeness, 1, 5);
        Influence = Math.Clamp(draft.Influence <= 0 ? 3 : draft.Influence, 1, 5);
        Notes = NormalizeOptional(draft.Notes, "关系说明", MaxNotesLength);
        UpdatedAt = now;
    }

    public static AvatarRelationship Restore(
        Guid id,
        string name,
        string role,
        int closeness,
        int influence,
        string notes,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(id, name, role, closeness, influence, notes, createdAt, updatedAt);

    private static string NormalizeRole(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return AvatarDefaults.RelationshipRoles.Contains(trimmed) ? trimmed : "其它";
    }

    private static string NormalizeRequired(string? value, string field, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new AvatarValidationException($"{field}不能为空。");
        if (trimmed.Length > maxLength) throw new AvatarValidationException($"{field}不能超过 {maxLength} 个字符。");
        return trimmed;
    }

    private static string NormalizeOptional(string? value, string field, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length > maxLength) throw new AvatarValidationException($"{field}不能超过 {maxLength} 个字符。");
        return trimmed;
    }
}

public sealed record AvatarRelationshipDraft(
    string? Name,
    string? Role,
    int Closeness,
    int Influence,
    string? Notes);
