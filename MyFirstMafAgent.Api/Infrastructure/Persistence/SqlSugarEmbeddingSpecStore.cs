using System.Text.Json;
using MyFirstMafAgent.Api.Domain.Knowledge;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>向量模型规格与知识库模型绑定的 SqlSugar 实现。</summary>
public sealed class SqlSugarEmbeddingSpecStore(SqlSugarScope database) : IEmbeddingSpecStore, IModelBindingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<EmbeddingSpec>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<EmbeddingSpecSchemaEntity>()
            .OrderBy(item => item.CreatedAt, OrderByType.Asc)
            .ToListAsync(cancellationToken);
        return rows.Select(ToSpec).ToList();
    }

    public async Task<EmbeddingSpec?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<EmbeddingSpecSchemaEntity>()
            .Where(item => item.Id == id)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToSpec(row);
    }

    public async Task<EmbeddingSpec?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<EmbeddingSpecSchemaEntity>()
            .Where(item => item.Fingerprint == fingerprint)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToSpec(row);
    }

    public async Task<EmbeddingSpec> UpsertAsync(EmbeddingSpecDraft draft, CancellationToken cancellationToken)
    {
        var fingerprint = EmbeddingSpec.CreateFingerprint(
            draft.Provider, draft.Model, draft.Dimensions, draft.MaxSequenceLength, draft.Revision,
            draft.FingerprintVariant);
        var existing = await FindByFingerprintAsync(fingerprint, cancellationToken);
        if (existing is not null) return existing;

        var entity = new EmbeddingSpecSchemaEntity
        {
            Id = Guid.NewGuid(),
            Name = draft.Name.Trim(),
            Provider = draft.Provider.Trim().ToLowerInvariant(),
            Model = draft.Model.Trim(),
            Dimensions = draft.Dimensions,
            MaxSequenceLength = draft.MaxSequenceLength,
            Revision = draft.Revision.Trim(),
            Fingerprint = fingerprint,
            ConfigJson = JsonSerializer.Serialize(draft.Runtime, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        };
        try
        {
            await database.Insertable(entity).ExecuteCommandAsync(cancellationToken);
            return ToSpec(entity);
        }
        catch
        {
            // 并发注册同一指纹时由唯一索引兜底，返回已存在的规格。
            var concurrent = await FindByFingerprintAsync(fingerprint, cancellationToken);
            if (concurrent is not null) return concurrent;
            throw;
        }
    }

    public async Task<Guid?> GetEmbeddingSpecIdAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<ModelBindingSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .FirstAsync(cancellationToken);
        return row?.EmbeddingSpecId;
    }

    public async Task SetEmbeddingSpecIdAsync(Guid knowledgeBaseId, Guid? embeddingSpecId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<ModelBindingSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .FirstAsync(cancellationToken);
        if (row is null)
        {
            await database.Insertable(new ModelBindingSchemaEntity
            {
                KnowledgeBaseId = knowledgeBaseId,
                EmbeddingSpecId = embeddingSpecId,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ExecuteCommandAsync(cancellationToken);
            return;
        }

        await database.Updateable<ModelBindingSchemaEntity>()
            .SetColumns(item => new ModelBindingSchemaEntity
            {
                EmbeddingSpecId = embeddingSpecId,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<SummaryModelBinding?> GetSummaryBindingAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<ModelBindingSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .FirstAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.SummarySettings)) return null;

        try
        {
            return JsonSerializer.Deserialize<SummaryModelBinding>(row.SummarySettings, JsonOptions);
        }
        catch (JsonException)
        {
            // 配置损坏时回退到全局模型设置，避免摘要功能整体不可用。
            return null;
        }
    }

    public async Task SetSummaryBindingAsync(Guid knowledgeBaseId, SummaryModelBinding? binding, CancellationToken cancellationToken)
    {
        var json = binding is null ? null : JsonSerializer.Serialize(binding, JsonOptions);
        var row = await database.Queryable<ModelBindingSchemaEntity>()
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .FirstAsync(cancellationToken);
        if (row is null)
        {
            await database.Insertable(new ModelBindingSchemaEntity
            {
                KnowledgeBaseId = knowledgeBaseId,
                SummarySettings = json,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ExecuteCommandAsync(cancellationToken);
            return;
        }

        await database.Updateable<ModelBindingSchemaEntity>()
            .SetColumns(item => new ModelBindingSchemaEntity
            {
                SummarySettings = json,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.KnowledgeBaseId == knowledgeBaseId)
            .ExecuteCommandAsync(cancellationToken);
    }

    private static EmbeddingSpec ToSpec(EmbeddingSpecSchemaEntity entity)
    {
        EmbeddingRuntimeConfig runtime = new();
        if (!string.IsNullOrWhiteSpace(entity.ConfigJson))
        {
            try
            {
                runtime = JsonSerializer.Deserialize<EmbeddingRuntimeConfig>(entity.ConfigJson, JsonOptions) ?? new();
            }
            catch (JsonException)
            {
                runtime = new();
            }
        }

        return new EmbeddingSpec(entity.Id, entity.Name, entity.Provider, entity.Model, entity.Dimensions,
            entity.MaxSequenceLength, entity.Revision, entity.Fingerprint, runtime, entity.CreatedAt);
    }
}
