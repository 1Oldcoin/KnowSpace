using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 固定评测集与回归。指标只看实际表现：
/// 事实错误率、引用记忆正确率、决策/表达一致性、以及“不知道题”是否编造。
/// 每次调整整理器、检索或提示词后都跑一遍，防止越改越差。
/// </summary>
public sealed class AvatarEvalService(
    IAvatarRepository repository,
    AvatarPersonaChatService chat,
    ILogger<AvatarEvalService> logger)
{
    public async Task<IReadOnlyList<AvatarEvalCaseDto>> ListCasesAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        (await repository.ListEvalCasesAsync(includeInactive, cancellationToken))
        .Select(AvatarEvalCaseDto.FromDomain)
        .ToList();

    public async Task<AvatarEvalCaseDto> AddCaseAsync(
        AvatarEvalCaseDraft draft,
        CancellationToken cancellationToken)
    {
        var evalCase = AvatarEvalCase.Create(
            draft.Category,
            draft.Question,
            draft.ExpectedAnswer,
            draft.MustMemoryIds,
            draft.ForbiddenMemoryIds,
            draft.Rubric,
            draft.Split,
            draft.Active,
            DateTimeOffset.UtcNow);
        await repository.AddEvalCaseAsync(evalCase, cancellationToken);
        return AvatarEvalCaseDto.FromDomain(evalCase);
    }

    public Task<bool> DeleteCaseAsync(Guid caseId, CancellationToken cancellationToken) =>
        repository.DeleteEvalCaseAsync(caseId, cancellationToken);

    public async Task<IReadOnlyList<AvatarEvalRunDto>> ListRunsAsync(
        int limit,
        CancellationToken cancellationToken) =>
        (await repository.ListEvalRunsAsync(limit, cancellationToken))
        .Select(run => AvatarEvalRunDto.FromDomain(run))
        .ToList();

    public async Task<AvatarEvalRunDto?> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await repository.GetEvalRunAsync(runId, cancellationToken);
        if (run is null) return null;

        var results = await repository.ListEvalResultsAsync(runId, cancellationToken);
        return AvatarEvalRunDto.FromDomain(run, results);
    }

    public async Task<AvatarEvalRunDto> RunAsync(
        bool includeHidden,
        int? limit,
        CancellationToken cancellationToken)
    {
        var cases = (await repository.ListEvalCasesAsync(false, cancellationToken))
            .Where(evalCase => includeHidden || evalCase.Split == AvatarEvalCase.SplitPublic)
            .Take(limit is > 0 ? limit.Value : 100)
            .ToList();
        if (cases.Count == 0)
            throw new AvatarValidationException("还没有评测题。先添加几条你认可的问答。");

        var now = DateTimeOffset.UtcNow;
        var run = AvatarEvalRun.Create(cases.Count, now);
        await repository.AddEvalRunAsync(run, cancellationToken);

        var results = new List<AvatarEvalResult>(cases.Count);
        var passed = 0;
        var factErrors = 0;
        var citationHits = 0;
        var citationTotal = 0;
        var styleScores = new List<int>();

        foreach (var evalCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string answer;
            IReadOnlyList<Guid> citationIds;
            try
            {
                var reply = await chat.AskAsync(null, evalCase.Question, cancellationToken, persist: false);
                answer = reply.Answer;
                citationIds = reply.Citations.Select(citation => citation.MemoryId).Distinct().ToList();
            }
            catch (AvatarValidationException exception)
            {
                answer = exception.Message;
                citationIds = [];
            }

            var mustOk = evalCase.MustMemoryIds.All(citationIds.Contains);
            var forbiddenHit = evalCase.ForbiddenMemoryIds.Where(citationIds.Contains).ToList();
            var forbiddenOk = forbiddenHit.Count == 0;
            var memoryGapOk = evalCase.Category != "memory_gap"
                || ContainsUncertainty(answer);

            if (evalCase.MustMemoryIds.Count > 0)
            {
                citationTotal++;
                if (mustOk) citationHits++;
            }

            var styleScore = ScoreStyle(answer, evalCase.Category);
            if (evalCase.Category == "style") styleScores.Add(styleScore);

            var casePassed = mustOk && forbiddenOk && memoryGapOk;
            if (casePassed) passed++;
            if (!forbiddenOk) factErrors++;

            var detail = BuildDetail(mustOk, forbiddenOk, memoryGapOk, forbiddenHit, evalCase);
            results.Add(AvatarEvalResult.Create(
                run.Id,
                evalCase.Id,
                evalCase.Question,
                answer,
                citationIds,
                casePassed,
                casePassed ? styleScore : Math.Min(60, styleScore),
                detail,
                DateTimeOffset.UtcNow));
        }

        if (results.Count > 0)
            await repository.AddEvalResultsAsync(results, cancellationToken);

        var citationAccuracy = citationTotal == 0 ? 1d : (double)citationHits / citationTotal;
        var styleAverage = styleScores.Count == 0 ? 100 : (int)Math.Round(styleScores.Average());
        var summary =
            $"共 {cases.Count} 题，通过 {passed} 题；引用准确率 {citationAccuracy:P0}；事实错误 {factErrors} 次；表达评分 {styleAverage}。";

        run.Complete(passed, factErrors, citationAccuracy, styleAverage, summary, DateTimeOffset.UtcNow);
        await repository.UpdateEvalRunAsync(run, cancellationToken);

        logger.LogInformation("Avatar eval run {RunId} completed: {Summary}", run.Id, summary);

        return AvatarEvalRunDto.FromDomain(run, results);
    }

    private static int ScoreStyle(string answer, string category)
    {
        var length = answer?.Length ?? 0;
        var hasFirstPerson = answer?.Contains('我') == true;
        if (!hasFirstPerson) return 30;
        if (category == "style")
            return length is >= 80 and <= 600 ? 100 : 70;
        return length is >= 150 and <= 600 ? 100 : 80;
    }

    private static bool ContainsUncertainty(string answer) =>
        answer.Contains("记不清", StringComparison.Ordinal)
        || answer.Contains("不确定", StringComparison.Ordinal)
        || answer.Contains("没有相关记忆", StringComparison.Ordinal)
        || answer.Contains("不太记得", StringComparison.Ordinal);

    private static string BuildDetail(
        bool mustOk,
        bool forbiddenOk,
        bool memoryGapOk,
        IReadOnlyList<Guid> forbiddenHit,
        AvatarEvalCase evalCase)
    {
        var parts = new List<string>(4);
        parts.Add(mustOk ? "必须引用记忆齐全" : "缺少必须引用的记忆");
        if (!forbiddenOk) parts.Add($"引用了禁止记忆：{string.Join(',', forbiddenHit)}");
        if (evalCase.Category == "memory_gap") parts.Add(memoryGapOk ? "记忆不足时明确承认" : "记忆不足时没有承认");
        return string.Join("；", parts);
    }
}

public sealed record AvatarEvalCaseDraft(
    string? Category,
    string? Question,
    string? ExpectedAnswer,
    IReadOnlyList<Guid>? MustMemoryIds,
    IReadOnlyList<Guid>? ForbiddenMemoryIds,
    IReadOnlyList<string>? Rubric,
    string? Split,
    bool? Active);
