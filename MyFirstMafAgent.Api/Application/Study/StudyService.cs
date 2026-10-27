using MyFirstMafAgent.Api.Domain.Study;

namespace MyFirstMafAgent.Api.Application.Study;

/// <summary>学习打卡应用服务。</summary>
public sealed class StudyService(IStudyRepository repository)
{
    public async Task<StudyStatistics> GetStatisticsAsync(int days, CancellationToken cancellationToken)
    {
        var records = await repository.ListAsync(cancellationToken);
        return StudyCalculator.Summarize(records, DateOnly.FromDateTime(DateTime.Today), days);
    }

    /// <summary>日历弹窗用：取 [from, to] 内确实有打卡的日期（不补零，见 <see cref="StudyCalculator.Range"/>）。</summary>
    public async Task<IReadOnlyList<StudyDay>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // 先校验再读库：跨越 1970→今天这种请求没必要先去把整表捞出来。
        var records = await repository.ListAsync(cancellationToken);
        return StudyCalculator.Range(records, from, to);
    }

    public async Task<StudyStatistics> CheckInAsync(int minutes, string? note, CancellationToken cancellationToken)
    {
        var record = StudyCalculator.Validate(minutes, note, DateOnly.FromDateTime(DateTime.Today));
        await repository.SaveAsync(record, cancellationToken);
        return await GetStatisticsAsync(30, cancellationToken);
    }

    public async Task<StudyStatistics> CancelTodayAsync(CancellationToken cancellationToken)
    {
        await repository.DeleteAsync(DateOnly.FromDateTime(DateTime.Today), cancellationToken);
        return await GetStatisticsAsync(30, cancellationToken);
    }
}
