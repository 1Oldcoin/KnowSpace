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
