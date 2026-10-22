namespace MyFirstMafAgent.Api.Domain.Topics;

/// <summary>知识库下的专题。资料通过 <c>kb_documents.topic_id</c> 归属专题。</summary>
public sealed record Topic(
    Guid Id,
    Guid KnowledgeBaseId,
    string Name,
    string Color,
    DateTimeOffset CreatedAt,
    int DocumentCount)
{
    public const int MaxNameLength = 50;

    public static readonly string[] Colors = ["lavender", "mint", "peach", "blue", "rose", "sage"];

    public static string NormalizeColor(string? color) =>
        !string.IsNullOrWhiteSpace(color) && Colors.Contains(color.Trim(), StringComparer.OrdinalIgnoreCase)
            ? color.Trim().ToLowerInvariant()
            : "lavender";

    public static string NormalizeName(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.Length == 0) throw new TopicValidationException("专题名称不能为空。");
        if (value.Length > MaxNameLength) throw new TopicValidationException($"专题名称不能超过 {MaxNameLength} 个字符。");
        return value;
    }
}

public enum DocumentTopicAssignResult
{
    Success,
    DocumentNotFound,
    TopicNotFound
}

public interface ITopicRepository
{
    Task<IReadOnlyList<Topic>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    Task<Topic?> GetAsync(Guid knowledgeBaseId, Guid topicId, CancellationToken cancellationToken);

    Task<bool> NameExistsAsync(Guid knowledgeBaseId, string name, Guid? excludeTopicId, CancellationToken cancellationToken);

    Task AddAsync(Topic topic, CancellationToken cancellationToken);

    Task UpdateAsync(Topic topic, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid knowledgeBaseId, Guid topicId, CancellationToken cancellationToken);

    Task<DocumentTopicAssignResult> AssignDocumentAsync(Guid documentId, Guid? topicId, CancellationToken cancellationToken);

    Task<Guid?> GetDocumentTopicAsync(Guid documentId, CancellationToken cancellationToken);
}

public sealed class TopicValidationException(string message) : Exception(message);
