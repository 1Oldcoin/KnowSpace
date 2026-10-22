using MyFirstMafAgent.Api.Domain.Study;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

[SugarTable("study_checkins")]
[SugarIndex("ux_study_checkins_date", nameof(CheckinDate), OrderByType.Asc, true)]
public sealed class StudyCheckinSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "int8", IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "checkin_date", ColumnDataType = "date", IsNullable = false)]
    public DateTime CheckinDate { get; set; }

    [SugarColumn(ColumnName = "minutes", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Minutes { get; set; }

    [SugarColumn(ColumnName = "note", ColumnDataType = "text", IsNullable = true)]
    public string? Note { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqlSugarStudyRepository(SqlSugarScope database) : IStudyRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        database.CodeFirst.InitTables<StudyCheckinSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<StudyRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await database.Queryable<StudyCheckinSchemaEntity>()
            .OrderBy(item => item.CheckinDate, OrderByType.Asc)
            .ToListAsync(cancellationToken);
        return rows
            .Select(row => new StudyRecord(DateOnly.FromDateTime(row.CheckinDate), row.Minutes, row.Note))
            .ToList();
    }

    public async Task SaveAsync(StudyRecord record, CancellationToken cancellationToken)
    {
        var date = record.Date.ToDateTime(TimeOnly.MinValue);
        var existing = await database.Queryable<StudyCheckinSchemaEntity>()
            .Where(item => item.CheckinDate == date)
            .FirstAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            await database.Insertable(new StudyCheckinSchemaEntity
            {
                CheckinDate = date,
                Minutes = record.Minutes,
                Note = record.Note,
                CreatedAt = now,
                UpdatedAt = now
            }).ExecuteCommandAsync(cancellationToken);
            return;
        }

        await database.Updateable<StudyCheckinSchemaEntity>()
            .SetColumns(item => new StudyCheckinSchemaEntity
            {
                Minutes = record.Minutes,
                Note = record.Note,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Id == existing.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        var deleted = await database.Deleteable<StudyCheckinSchemaEntity>()
            .Where(item => item.CheckinDate == value)
            .ExecuteCommandAsync(cancellationToken);
        return deleted > 0;
    }
}
