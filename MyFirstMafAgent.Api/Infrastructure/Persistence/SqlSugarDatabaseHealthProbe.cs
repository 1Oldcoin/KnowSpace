using MyFirstMafAgent.Api.Application.Health;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>基于 SqlSugar 的数据库健康检查实现（端点通过 <see cref="IDatabaseHealthProbe"/> 使用）。</summary>
public sealed class SqlSugarDatabaseHealthProbe(SqlSugarScope database) : IDatabaseHealthProbe
{
    public Task CheckAsync(CancellationToken cancellationToken) =>
        Task.Run(() => database.Ado.CheckConnection(), cancellationToken);
}
