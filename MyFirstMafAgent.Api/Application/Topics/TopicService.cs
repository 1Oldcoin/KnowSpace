using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Topics;

namespace MyFirstMafAgent.Api.Application.Topics;

/// <summary>专题应用服务。</summary>
public sealed class TopicService(ITopicRepository repository, IKnowledgeBaseDirectory knowledgeBases)
{
    public async Task<IReadOnlyList<Topic>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        await EnsureKnowledgeBaseAsync(knowledgeBaseId, cancellationToken);
        return await repository.ListAsync(knowledgeBaseId, cancellationToken);
    }

    public async Task<Topic> CreateAsync(Guid knowledgeBaseId, string name, string? color, CancellationToken cancellationToken)
    {
        await EnsureKnowledgeBaseAsync(knowledgeBaseId, cancellationToken);
        var normalizedName = Topic.NormalizeName(name);
        if (await repository.NameExistsAsync(knowledgeBaseId, normalizedName, null, cancellationToken))
            throw new ConflictException("已存在同名专题。");

        var topic = new Topic(Guid.NewGuid(), knowledgeBaseId, normalizedName, Topic.NormalizeColor(color), DateTimeOffset.UtcNow, 0);
        await repository.AddAsync(topic, cancellationToken);
        return topic;
    }

    public async Task<Topic> UpdateAsync(Guid knowledgeBaseId, Guid topicId, string name, string? color, CancellationToken cancellationToken)
    {
        await EnsureKnowledgeBaseAsync(knowledgeBaseId, cancellationToken);
        var normalizedName = Topic.NormalizeName(name);
        if (await repository.NameExistsAsync(knowledgeBaseId, normalizedName, topicId, cancellationToken))
            throw new ConflictException("已存在同名专题。");

        var existing = await repository.GetAsync(knowledgeBaseId, topicId, cancellationToken)
            ?? throw new TopicNotFoundException(topicId);
        var updated = existing with { Name = normalizedName, Color = Topic.NormalizeColor(color ?? existing.Color) };
        await repository.UpdateAsync(updated, cancellationToken);
        return updated;
    }

    public async Task DeleteAsync(Guid knowledgeBaseId, Guid topicId, CancellationToken cancellationToken)
    {
        if (!await repository.DeleteAsync(knowledgeBaseId, topicId, cancellationToken))
            throw new TopicNotFoundException(topicId);
    }

    public Task<DocumentTopicAssignResult> AssignDocumentAsync(Guid documentId, Guid? topicId, CancellationToken cancellationToken) =>
        repository.AssignDocumentAsync(documentId, topicId, cancellationToken);

    public Task<Guid?> GetDocumentTopicAsync(Guid documentId, CancellationToken cancellationToken) =>
        repository.GetDocumentTopicAsync(documentId, cancellationToken);

    private async Task EnsureKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken))
            throw new KnowledgeBaseNotFoundException(knowledgeBaseId);
    }
}

public sealed class TopicNotFoundException(Guid topicId) : NotFoundException($"专题 {topicId} 不存在。");
