using MyFirstMafAgent.Api.Domain.Avatar;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 回归：投喂任务的状态回写必须能在 PostgreSQL 上执行成功。
/// <para>
/// 背景：早期实现用 <c>Updateable.SetColumns</c> 更新 avatar_ingestions。SqlSugar 会把
/// 「求值为 null 的 DateTimeOffset?」声明成 <c>DbType.AnsiString</c> 参数，PostgreSQL 不做
/// text → timestamptz 的隐式赋值，于是第一条状态回写就抛出
/// <c>42804: column "completed_at" is of type timestamp with time zone but expression is of type text</c>，
/// 任务永远停在 running/extracting，投喂的内容再也进不了记忆库。
/// </para>
/// <para>
/// 这些用例直接锁住「不再让 SqlSugar 猜时间列类型」这个约束：SQL 里必须有显式 CAST，
/// 空时间戳必须是 DBNull（而不是一个被推断成字符串的空值）。
/// </para>
/// </summary>
public sealed class AvatarIngestionUpdateCommandTests
{
    private static AvatarIngestion CreateIngestion(bool completed)
    {
        var now = DateTimeOffset.UtcNow;
        var ingestion = AvatarIngestion.Create("text", "mcp-roundtrip", null, [Guid.NewGuid()], null, now);
        ingestion.MarkRunning(AvatarIngestion.StageExtracting, now);

        if (!completed) return ingestion;

        ingestion.Complete(AvatarIngestion.StageCompleted, "{}", 1, 0, now);
        return ingestion;
    }

    [Fact]
    public void Null_timestamps_are_sent_as_database_nulls_not_inferred_strings()
    {
        var ingestion = CreateIngestion(completed: false);
        var parameters = SqlSugarAvatarRepository.BuildIngestionUpdateParameters(ingestion);

        var completedAt = Assert.Single(parameters, parameter => parameter.ParameterName == "@completedAt");
        Assert.Equal(DBNull.Value, completedAt.Value);

        var startedAt = Assert.Single(parameters, parameter => parameter.ParameterName == "@startedAt");
        Assert.Equal(ingestion.StartedAt!.Value, startedAt.Value);
    }

    [Fact]
    public void Completed_timestamps_are_sent_as_datetimeoffset()
    {
        var ingestion = CreateIngestion(completed: true);
        var parameters = SqlSugarAvatarRepository.BuildIngestionUpdateParameters(ingestion);

        var completedAt = Assert.Single(parameters, parameter => parameter.ParameterName == "@completedAt");
        Assert.Equal(ingestion.CompletedAt, Assert.IsType<DateTimeOffset>(completedAt.Value));
    }

    [Theory]
    [InlineData("updated_at")]
    [InlineData("started_at")]
    [InlineData("completed_at")]
    public void Every_timestamp_column_is_written_through_an_explicit_cast(string column)
    {
        // 只要有一列漏了 CAST，SqlSugar 推断出来的 AnsiString 参数就会让整条 UPDATE 失败。
        Assert.Contains(
            $"{column} = CAST(",
            SqlSugarAvatarRepository.UpdateIngestionSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Update_command_covers_every_mutable_column_and_filters_by_id()
    {
        foreach (var column in new[]
                 {
                     "source_ref", "status", "stage", "error", "memory_count",
                     "insight_count", "stats_json"
                 })
        {
            Assert.Contains($"{column} = @", SqlSugarAvatarRepository.UpdateIngestionSql, StringComparison.Ordinal);
        }

        Assert.Contains("WHERE id = @id", SqlSugarAvatarRepository.UpdateIngestionSql, StringComparison.Ordinal);
    }

    /// <summary>
    /// 留存证据：这条断言描述的是被修掉的 SqlSugar 行为，一旦上游改了类型推断，
    /// 这个测试会失败并提示可以回退到 SetColumns。这里只在内存里生成 SQL，不连库。
    /// </summary>
    [Fact]
    public void Sugar_setcolumns_still_infers_an_string_for_null_timestamps_which_is_why_we_use_raw_sql()
    {
        var database = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = "Host=localhost;Port=5432;Database=unused;Username=unused;Password=unused",
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        });

        var entity = new AvatarIngestionSchemaEntity { CompletedAt = null };
        var sql = database.Updateable<AvatarIngestionSchemaEntity>()
            .SetColumns(item => new AvatarIngestionSchemaEntity { CompletedAt = entity.CompletedAt })
            .Where(item => item.Id == entity.Id)
            .ToSql();

        var completedAt = sql.Value.Single(parameter => parameter.ParameterName == "@Const0");
        Assert.Equal(System.Data.DbType.AnsiString, completedAt.DbType);
    }
}
