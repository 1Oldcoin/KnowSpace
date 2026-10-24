using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 可空列更新时的参数绑定回归。
///
/// <para>
/// 实测规律（用 <c>ToSql()</c> 拿到 SqlSugar 真正生成的参数）：
/// </para>
/// <list type="bullet">
///   <item><c>SetColumns(it =&gt; new T { Col = null })</c> 字面量 → 按列类型正确绑定（<c>DateTimeOffset</c>/<c>Guid</c>）；</item>
///   <item><c>SetColumns(it =&gt; new T { Col = entity.Col })</c> 变量求值为 null → 被推断成 <c>AnsiString</c> → PostgreSQL <c>42804</c>；</item>
///   <item><c>Updateable(entity).UpdateColumns(...)</c> 实体驱动 → 按实体属性类型绑定，空值也正确；</item>
///   <item><c>Insertable(entity)</c> → 按实体属性类型绑定，空值正确。</item>
/// </list>
///
/// <para>
/// 所以规则是：<b>可空列的值来自变量时，不要用 SetColumns</b>。这条测试把这个事实钉住，
/// 并覆盖当前仓库里三处必须用实体驱动的更新（记忆、推演、评测运行）。
/// </para>
/// </summary>
public sealed class NullableColumnBindingTests
{
    private static SqlSugarScope Database() =>
        new(new ConnectionConfig
        {
            ConnectionString = "Host=localhost;Port=5432;Database=unused;Username=unused;Password=unused",
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });

    [Fact]
    public void SetColumns_with_a_literal_null_binds_the_column_type_correctly()
    {
        var sql = Database().Updateable<AvatarMemorySchemaEntity>()
            .SetColumns(item => new AvatarMemorySchemaEntity { ValidTo = null, SupersedesId = null })
            .Where(item => item.Id == Guid.Empty)
            .ToSql();

        Assert.Equal(System.Data.DbType.DateTimeOffset, Parameter(sql, "@Const0").DbType);
        Assert.Equal(System.Data.DbType.Guid, Parameter(sql, "@Const1").DbType);
    }

    [Fact]
    public void SetColumns_with_a_variable_that_is_null_degrades_to_ansi_string()
    {
        // 这就是 42804 的成因：参数值确实是空，但声明类型是 text。
        var entity = new AvatarMemorySchemaEntity { Id = Guid.NewGuid(), ValidTo = null };
        var sql = Database().Updateable<AvatarMemorySchemaEntity>()
            .SetColumns(item => new AvatarMemorySchemaEntity { ValidTo = entity.ValidTo })
            .Where(item => item.Id == entity.Id)
            .ToSql();

        Assert.Equal(System.Data.DbType.AnsiString, Parameter(sql, "@Const0").DbType);
    }

    [Fact]
    public void UpdateColumns_binds_nullable_timestamps_and_guids_from_the_entity_type()
    {
        var entity = new AvatarMemorySchemaEntity
        {
            Id = Guid.NewGuid(),
            Title = "t",
            ValidFrom = null,
            ValidTo = null,
            SupersedesId = null,
            LastConfirmedAt = null,
            LastUsedAt = null
        };

        var sql = Database().Updateable(entity)
            .UpdateColumns(item => new
            {
                item.Title,
                item.ValidFrom,
                item.ValidTo,
                item.SupersedesId,
                item.LastConfirmedAt,
                item.LastUsedAt
            })
            .ToSql();

        Assert.Equal(System.Data.DbType.DateTimeOffset, Parameter(sql, "@valid_from").DbType);
        Assert.Equal(System.Data.DbType.DateTimeOffset, Parameter(sql, "@valid_to").DbType);
        Assert.Equal(System.Data.DbType.Guid, Parameter(sql, "@supersedes_id").DbType);
        Assert.Equal(System.Data.DbType.DateTimeOffset, Parameter(sql, "@last_confirmed_at").DbType);
        Assert.Equal(System.Data.DbType.DateTimeOffset, Parameter(sql, "@last_used_at").DbType);
    }

    [Fact]
    public void Insert_binds_nullable_timestamps_from_the_entity_type()
    {
        // 导入聊天记录时 sent_at 可能为空；插入一直是安全的，这条用来防止未来有人改成手工拼参数。
        var sql = Database().Insertable(new CompanionMessageSchemaEntity
        {
            ProfileId = Guid.NewGuid(),
            Sender = "x",
            Content = "c",
            SentAt = null,
            CreatedAt = DateTimeOffset.UtcNow
        }).ToSql();

        Assert.Equal(System.Data.DbType.DateTimeOffset, Parameter(sql, "@sent_at").DbType);
    }

    [Fact]
    public void Entity_driven_update_knows_the_primary_key()
    {
        // UpdateColumns 的 WHERE 来自主键：迁移 SetColumns → UpdateColumns 时不需要再写 .Where。
        var entity = new AvatarMemorySchemaEntity { Id = Guid.NewGuid(), Title = "t" };
        var sql = Database().Updateable(entity).UpdateColumns(item => new { item.Title }).ToSql();

        Assert.Contains("WHERE", sql.Key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(entity.Id, Parameter(sql, "@id").Value);
    }

    private static SugarParameter Parameter(
        KeyValuePair<string, List<SugarParameter>> sql,
        string name) =>
        sql.Value.Single(parameter => parameter.ParameterName == name);
}
