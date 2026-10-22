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
