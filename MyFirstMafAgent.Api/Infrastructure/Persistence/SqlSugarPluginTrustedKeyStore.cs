using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 受信任公钥的 SqlSugar 实现。单独一个类而不是塞进 <c>SqlSugarPluginRegistry</c>：
/// 后者已经实现了三个端口，信任链属于独立的关注点。
/// </summary>
public sealed class SqlSugarPluginTrustedKeyStore(SqlSugarScope database) : IPluginTrustedKeyStore
{
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<PluginTrustedKeySchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<PluginTrustedKeyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<PluginTrustedKeySchemaEntity>()
            .OrderBy(row => row.PluginId)
            .OrderBy(row => row.KeyId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToRecord).ToList();
    }

    public async Task<PluginTrustedKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken)
    {
        var row = await database.Queryable<PluginTrustedKeySchemaEntity>()
            .Where(item => item.KeyId == keyId)
            .FirstAsync(cancellationToken);

        return row is null ? null : ToRecord(row);
    }

    public async Task SaveAsync(PluginTrustedKeyRecord record, CancellationToken cancellationToken)
    {
        var exists = await database.Queryable<PluginTrustedKeySchemaEntity>()
            .AnyAsync(item => item.KeyId == record.KeyId, cancellationToken);

        if (!exists)
        {
            await database.Insertable(new PluginTrustedKeySchemaEntity
            {
                KeyId = record.KeyId,
                PluginId = record.PluginId,
                Algorithm = record.Algorithm,
                PublicKeyPem = record.PublicKeyPem,
                Fingerprint = record.Fingerprint,
                Note = record.Note,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ExecuteCommandAsync(cancellationToken);

            return;
        }

        await database.Updateable<PluginTrustedKeySchemaEntity>()
            .SetColumns(item => new PluginTrustedKeySchemaEntity
            {
                PluginId = record.PluginId,
                Algorithm = record.Algorithm,
                PublicKeyPem = record.PublicKeyPem,
                Fingerprint = record.Fingerprint,
                Note = record.Note,
                UpdatedAt = SqlSugar.SqlFunc.GetDate()
            })
            .Where(item => item.KeyId == record.KeyId)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string keyId, CancellationToken cancellationToken)
    {
        var affected = await database.Deleteable<PluginTrustedKeySchemaEntity>()
            .Where(item => item.KeyId == keyId)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    private static PluginTrustedKeyRecord ToRecord(PluginTrustedKeySchemaEntity row) => new(
        row.KeyId,
        row.PluginId,
        row.Algorithm,
        row.PublicKeyPem,
        row.Fingerprint,
        row.Note,
        row.CreatedAt,
        row.UpdatedAt);
}
