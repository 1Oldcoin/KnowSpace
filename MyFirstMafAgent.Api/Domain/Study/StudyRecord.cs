namespace MyFirstMafAgent.Api.Domain.Study;

/// <summary>每日学习打卡记录（值对象，日期为主键语义）。</summary>
public sealed record StudyRecord(DateOnly Date, int Minutes, string? Note);

/// <summary>某一天的学习数据，用于图表展示。</summary>
public sealed record StudyDay(string Date, int Minutes, string? Note);

/// <summary>学习打卡统计聚合结果。</summary>
public sealed record StudyStatistics(
    bool TodayCheckedIn,
    int CurrentStreak,
    int LongestStreak,
    int TotalDays,
    int TodayMinutes,
    int ThisWeekMinutes,
    IReadOnlyList<StudyDay> RecentDays,
    IReadOnlyList<StudyDay> Week);

public interface IStudyRepository
{
    Task<IReadOnlyList<StudyRecord>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(StudyRecord record, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(DateOnly date, CancellationToken cancellationToken);
}

public sealed class StudyValidationException(string message) : Exception(message);

/// <summary>打卡统计规则：连续天数、本周节奏等纯领域计算。</summary>
public static class StudyCalculator
{
    public const int MaxMinutesPerDay = 1440;
    public const int MaxNoteLength = 500;

    /// <summary>日历查询一次最多取多少天：够翻一整年，又不给「from=1970」这种请求开口子。</summary>
    public const int MaxRangeDays = 400;

    public static StudyStatistics Summarize(IReadOnlyList<StudyRecord> records, DateOnly today, int rangeDays)
    {
        var range = Math.Clamp(rangeDays, 7, 365);
        var map = records
            .GroupBy(record => record.Date)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(record => record.Date).First());

        var todayCheckedIn = map.ContainsKey(today);
        var anchor = todayCheckedIn ? today : today.AddDays(-1);

        var currentStreak = 0;
        while (map.ContainsKey(anchor.AddDays(-currentStreak)))
            currentStreak++;

        var longestStreak = 0;
        var running = 0;
        DateOnly? previous = null;
        foreach (var date in map.Keys.OrderBy(value => value))
        {
            running = previous is not null && date.DayNumber - previous.Value.DayNumber == 1 ? running + 1 : 1;
            if (running > longestStreak) longestStreak = running;
            previous = date;
        }

        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var week = new List<StudyDay>(7);
        var thisWeekMinutes = 0;
        for (var index = 0; index < 7; index++)
        {
            var date = weekStart.AddDays(index);
            var minutes = map.TryGetValue(date, out var record) ? record.Minutes : 0;
            if (date <= today) thisWeekMinutes += minutes;
            week.Add(new StudyDay(date.ToString("yyyy-MM-dd"), minutes, record?.Note));
        }

        var recent = new List<StudyDay>(range);
        for (var index = range - 1; index >= 0; index--)
        {
            var date = today.AddDays(-index);
            if (map.TryGetValue(date, out var record))
                recent.Add(new StudyDay(date.ToString("yyyy-MM-dd"), record.Minutes, record.Note));
        }

        return new StudyStatistics(
            todayCheckedIn,
            currentStreak,
            longestStreak,
            map.Count,
            map.TryGetValue(today, out var todayRecord) ? todayRecord.Minutes : 0,
            thisWeekMinutes,
            recent,
            week);
    }

    /// <summary>
    /// 取 [from, to] 内**确实有打卡记录**的日期，按日期升序。
    ///
    /// <para>
    /// 刻意不补零：日历要把「已打卡」和「没打卡」区分开，补零会让两种状态在数据层就分不出来；
    /// 而且未来日期补零等于凭空捏造「当天学习了 0 分钟」。只返回存在的记录，缺的那天由前端判空。
    /// </para>
    /// </summary>
    public static IReadOnlyList<StudyDay> Range(IReadOnlyList<StudyRecord> records, DateOnly from, DateOnly to)
    {
        if (to < from)
            throw new StudyValidationException("结束日期不能早于开始日期。");
        if (to.DayNumber - from.DayNumber > MaxRangeDays)
            throw new StudyValidationException($"日期跨度不能超过 {MaxRangeDays} 天。");

        return records
            .Where(record => record.Date >= from && record.Date <= to)
            .GroupBy(record => record.Date)
            .OrderBy(group => group.Key)
            // 同一日期理论上只有一行（有唯一索引兜底），重复时取最后一条，与 Summarize 保持一致。
            .Select(group => group.Last())
            .Select(record => new StudyDay(record.Date.ToString("yyyy-MM-dd"), record.Minutes, record.Note))
            .ToList();
    }

    public static StudyRecord Validate(int minutes, string? note, DateOnly date)
    {
        if (minutes is < 0 or > MaxMinutesPerDay)
            throw new StudyValidationException($"学习时长需在 0 到 {MaxMinutesPerDay} 分钟之间。");

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
            throw new StudyValidationException($"打卡备注不能超过 {MaxNoteLength} 个字符。");

        return new StudyRecord(date, minutes, trimmed);
    }
}
