namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>人物/组织/项目等实体。用于把记忆按“对谁、关于什么”聚起来。</summary>
public sealed class AvatarEntity
{
    private AvatarEntity()
    {
    }

    private AvatarEntity(
        Guid id,
        string entityType,
        string name,
        IReadOnlyList<string> aliases,
        string metadataJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        EntityType = entityType;
        Name = name;
        Aliases = aliases;
        MetadataJson = metadataJson;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public string EntityType { get; private set; } = "person";
    public string Name { get; private set; } = string.Empty;
    public IReadOnlyList<string> Aliases { get; private set; } = [];
    public string MetadataJson { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AvatarEntity Create(
        string? entityType,
        string? name,
        IReadOnlyList<string>? aliases,
        string? metadataJson,
        DateTimeOffset now)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length == 0)
            throw new AvatarValidationException("实体名称不能为空。");
        if (normalizedName.Length > 120) normalizedName = normalizedName[..120];

        return new AvatarEntity(
            Guid.NewGuid(),
            NormalizeType(entityType),
            normalizedName,
            (aliases ?? []).Select(item => (item ?? string.Empty).Trim())
                .Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
            string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson,
            now,
            now);
    }

    public static AvatarEntity Restore(
        Guid id,
        string entityType,
        string name,
        IReadOnlyList<string> aliases,
        string metadataJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(id, entityType, name, aliases, metadataJson, createdAt, updatedAt);

    public void Touch(DateTimeOffset now) => UpdatedAt = now;

    private static string NormalizeType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "organization" or "组织" => "organization",
            "project" or "项目" => "project",
            "place" or "地点" => "place",
            _ => "person"
        };
}

/// <summary>记忆与实体的关联。</summary>
public sealed record AvatarMemoryEntityLink(Guid MemoryId, Guid EntityId, string Role);
