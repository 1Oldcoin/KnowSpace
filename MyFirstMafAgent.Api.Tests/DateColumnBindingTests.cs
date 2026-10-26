using System.Reflection;
using MyFirstMafAgent.Api.Domain.Study;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// PostgreSQL 日期参数绑定的回归测试。
///
/// <para>
/// 事故现场（同一张表炸了两次，都是客户端拒绝）：
/// <c>Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone',
/// only UTC is supported. … (Parameter 'value')</c>
/// </para>
///
/// <list type="number">
///   <item>
///     属性写 <c>DateTime</c>：SqlSugar 按 CLR 类型把参数定成 <c>DbType.DateTime</c>，Npgsql 9 把它映射到
///     <c>timestamptz</c>（不是 <c>date</c>），而 <c>DateOnly.ToDateTime(TimeOnly.MinValue)</c> 的 Kind 是 Unspecified。
///   </item>
///   <item>
///     属性改成 <c>DateOnly</c> 也不够：SqlSugar 5.1.4.221 插入时会额外塞一个
///     <c>@checkin_date_ts0</c> 参数（<c>DbType.DateTime</c>），SQL 里根本不引用它，
///     但 Npgsql 会校验参数集合里的每一个值 → 照炸。
///   </item>
/// </list>
///
/// <para>
/// 结论：这张表的读写走显式 SQL，日期参数显式声明成 <c>DbType.Date</c>。
/// 下面的断言把这两件事钉住，并且禁止任何 schema 实体再出现裸 <c>DateTime</c>。
/// </para>
/// </summary>
public sealed class DateColumnBindingTests
{
    [Fact]
    public void Checkin_date_parameter_is_declared_as_a_date()
    {
        var parameter = SqlSugarStudyRepository.CheckinDateParameter(new DateOnly(2026, 10, 25));

        Assert.Equal(System.Data.DbType.Date, parameter.DbType);
        Assert.NotEqual(System.Data.DbType.DateTime, parameter.DbType);

        // 值本身是 Kind=Unspecified 的当天零点——这正是 DbType.Date 能接受、timestamptz 会拒绝的形态。
        var value = Assert.IsType<DateTime>(parameter.Value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
        Assert.Equal(new DateTime(2026, 10, 25), value);
    }

    [Theory]
    [InlineData("select")]
    [InlineData("insert")]
    [InlineData("delete")]
    public void Date_columns_are_compared_and_written_through_an_explicit_cast(string statement)
    {
        var sql = statement switch
        {
            "select" => SqlSugarStudyRepository.SelectIdByDateSql,
            "insert" => SqlSugarStudyRepository.InsertSql,
            _ => SqlSugarStudyRepository.DeleteByDateSql
        };

        Assert.Contains("CAST(@checkinDate AS date)", sql);
    }

    [Fact]
    public void Insert_statement_carries_the_timestamp_parameters()
    {
        Assert.Contains("@minutes", SqlSugarStudyRepository.InsertSql);
        Assert.Contains("@createdAt", SqlSugarStudyRepository.InsertSql);
        Assert.Contains("@updatedAt", SqlSugarStudyRepository.InsertSql);
    }

    [Fact]
    public void Study_record_round_trips_the_calendar_date()
    {
        // 领域层始终用 DateOnly，仓储边界上只做「DateOnly → 当天零点」这一层换算。
        var record = StudyCalculator.Validate(30, "  note  ", new DateOnly(2026, 10, 25));
        Assert.Equal(new DateOnly(2026, 10, 25), record.Date);
        Assert.Equal("note", record.Note);

        var parameter = SqlSugarStudyRepository.CheckinDateParameter(record.Date);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Unspecified), parameter.Value);
    }

    /// <summary>
    /// 全仓扫描：任何带 <c>[SugarTable]</c> 的实体都不许出现 <c>DateTime</c> / <c>DateTime?</c> 属性。
    /// 日期用 <c>DateOnly</c>，时刻用 <c>DateTimeOffset</c>——没有第三种选择。
    /// </summary>
    [Fact]
    public void No_schema_entity_declares_a_bare_DateTime()
    {
        var offenders = typeof(StudyCheckinSchemaEntity).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<SugarTable>() is not null)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetCustomAttribute<SugarColumn>() is not null)
                .Where(property => property.PropertyType == typeof(DateTime)
                                   || property.PropertyType == typeof(DateTime?))
                .Select(property => $"{type.Name}.{property.Name} : {property.PropertyType.Name}"))
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "这些持久化属性用了 DateTime，会被 Npgsql 绑成 timestamptz 并在 Kind=Unspecified 时抛异常。"
            + "日期改用 DateOnly，时刻改用 DateTimeOffset："
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }
}
