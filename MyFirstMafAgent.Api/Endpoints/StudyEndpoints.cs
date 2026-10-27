using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Application.Study;
using MyFirstMafAgent.Api.Contracts;

namespace MyFirstMafAgent.Api.Endpoints;

public static class StudyEndpoints
{
    public static void MapStudyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/study").WithTags("学习打卡");

        routes.MapGet("/checkins", (int? days, StudyService study, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await study.GetStatisticsAsync(days ?? 30, cancellationToken))));

        // 日历弹窗：按显式日期区间取「有打卡的那些天」。
        // 与 /checkins?days=N 的区别是这里有上界（MaxRangeDays）而不是「最近 N 天」，
        // 所以可以自由翻月；不传参时默认返回当月。
        routes.MapGet("/checkins/range", (DateOnly? from, DateOnly? to, StudyService study, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var start = from ?? new DateOnly(today.Year, today.Month, 1);
                var end = to ?? new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
                return Results.Ok(await study.GetRangeAsync(start, end, cancellationToken));
            }));

        routes.MapPost("/checkins", (StudyCheckinRequest request, StudyService study, AvatarActivityRecorder activity, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var summary = await study.CheckInAsync(request.Minutes, request.Note, cancellationToken);

                // 时刻记录：学习打卡同时写入分身行为流。
                await activity.TryRecordAsync(
                    "学习",
                    string.IsNullOrWhiteSpace(request.Note) ? "完成今日学习打卡" : $"学习打卡：{request.Note.Trim()}",
                    request.Minutes,
                    "checkin",
                    cancellationToken);

                return Results.Ok(summary);
            }));

        routes.MapDelete("/checkins/today", (StudyService study, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await study.CancelTodayAsync(cancellationToken))));
    }
}
