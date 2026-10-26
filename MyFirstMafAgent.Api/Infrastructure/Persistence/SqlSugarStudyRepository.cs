using MyFirstMafAgent.Api.Domain.Study;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 学习打卡表结构。这里只用于 <c>CodeFirst.InitTables</c> 建表，读写一律走
/// <see cref="SqlSugarStudyRepository"/> 里的显式 SQL。
/// </summary>
[SugarTable("study_checkins")]
[SugarIndex("ux_study_checkins_date", nameof(CheckinDate), OrderByType.Asc, true)]
public sealed class StudyCheckinSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "checkin_date", ColumnDataType = "date", IsNullable = false)]
    public DateOnly CheckinDate { get; set; }

    [SugarColumn(ColumnName = "minutes", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Minutes { get; set; }

    [SugarColumn(ColumnName = "note", ColumnDataType = "text", IsNullable = true)]
    public string? Note { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 学习打卡仓储。
///
/// <para>
/// 这个仓储<b>故意不用</b> <c>Insertable/Updateable/Deleteable</c> 表达式 API，全部走显式 SQL + 显式 DbType，
/// 因为实体映射路线在这里踩过两次同一个坑：
/// </para>
///
/// <list type="number">
///   <item>
///     属性写 <c>DateTime</c> 时：SqlSugar 按 CLR 类型把参数定成 <c>DbType.DateTime</c>，而 Npgsql 把它映射到
///     <c>timestamptz</c>（不是 <c>date</c>），于是 <c>DateOnly.ToDateTime(TimeOnly.MinValue)</c> 产生的
///     Kind=Unspecified 值被客户端拒绝：
///     <c>Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone'</c>。
///   </item>
///   <item>
///     哪怕属性改成 <c>DateOnly</c>，SqlSugar 5.1.4.221 在插入时仍会<b>额外</b>塞一个
///     <c>@checkin_date_ts0</c> 参数（<c>DbType.DateTime</c>，值还是 Kind=Unspecified）。这条参数在 SQL 文本里
///     根本没被引用，但 Npgsql 会校验参数集合里的每一个值，照炸不误。
///   </item>
/// </list>
///
/// <para>
/// 显式 SQL 的写法把两件事钉死：日期参数一律 <c>DbType.Date</c>（<c>DateOnly</c> → 当天零点，
/// Kind=Unspecified，正好是 <c>date</c> 能接受的形态），时间列一律 <c>DateTimeOffset</c>。
/// </para>
/// </summary>
public sealed class SqlSugarStudyRepository(SqlSugarScope database) : IStudyRepository
{
    internal const string SelectAllSql = """
        SELECT checkin_date AS "CheckinDate", minutes AS "Minutes", note AS "Note"
        FROM study_checkins
        ORDER BY checkin_date ASC
        """;

    internal const string SelectIdByDateSql = """
        SELECT id FROM study_checkins WHERE checkin_date = CAST(@checkinDate AS date) LIMIT 1
        """;

    internal const string InsertSql = """
        INSERT INTO study_checkins (checkin_date, minutes, note, created_at, updated_at)
        VALUES (CAST(@checkinDate AS date), @minutes, @note, @createdAt, @updatedAt)
        """;

    internal const string UpdateSql = """
        UPDATE study_checkins
        SET minutes = @minutes, note = @note, updated_at = NOW()
        WHERE id = @id
        """;

    internal const string DeleteByDateSql = "DELETE FROM study_checkins WHERE checkin_date = CAST(@checkinDate AS date)";

    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<StudyCheckinSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<StudyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Ado.SqlQueryAsync<StudyCheckinRow>(SelectAllSql, new List<SugarParameter>());
        return rows
            .Select(row => new StudyRecord(DateOnly.FromDateTime(row.CheckinDate), row.Minutes, row.Note))
            .ToList();
    }

    public async Task SaveAsync(StudyRecord record, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var existingId = await database.Ado.SqlQueryAsync<long>(
            SelectIdByDateSql,
            new List<SugarParameter> { CheckinDateParameter(record.Date) });

        if (existingId.Count == 0)
        {
            await database.Ado.ExecuteCommandAsync(
                InsertSql,
                new List<SugarParameter>
                {
                    CheckinDateParameter(record.Date),
                    new("@minutes", record.Minutes),
                    new("@note", (object?)record.Note ?? DBNull.Value),
                    new("@createdAt", now),
                    new("@updatedAt", now)
                });
            return;
        }

        await database.Ado.ExecuteCommandAsync(
            UpdateSql,
            new List<SugarParameter>
            {
                new("@id", existingId[0]),
                new("@minutes", record.Minutes),
                new("@note", (object?)record.Note ?? DBNull.Value)
            });
    }

    public async Task<bool> DeleteAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var deleted = await database.Ado.ExecuteCommandAsync(
            DeleteByDateSql,
            new List<SugarParameter> { CheckinDateParameter(date) });
        return deleted > 0;
    }

    /// <summary>
    /// 日期参数必须显式声明成 <see cref="System.Data.DbType.Date"/>：
    /// 交给 SqlSugar/Npgsql 自行推断会得到 <c>DbType.DateTime</c> → <c>timestamptz</c>，
    /// 而 <c>DateOnly</c> 转出来的 DateTime Kind 是 Unspecified，timestamptz 只收 UTC，直接抛异常。
    /// </summary>
    internal static SugarParameter CheckinDateParameter(DateOnly date) =>
        new("@checkinDate", date.ToDateTime(TimeOnly.MinValue)) { DbType = System.Data.DbType.Date };

    private sealed class StudyCheckinRow
    {
        public DateTime CheckinDate { get; set; }

        public int Minutes { get; set; }

        public string? Note { get; set; }
    }
}
