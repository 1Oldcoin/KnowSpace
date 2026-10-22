using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Tests;

public sealed class AvatarV2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MemoryDefaultsToActiveButCanBeDowngradedToCandidate()
    {
        var memory = AvatarMemory.Create(
            new AvatarMemoryDraft(
                null, "preference", "在意执行力", "我更尊重有行动的人，而不是只讲想法的人。",
                null, Now, null, null, null, 4, "chat",
                "value", "stable", 55),
            Now);

        Assert.Equal(AvatarMemoryTypes.Value, memory.MemoryType);
        Assert.Equal(AvatarMemoryStability.Stable, memory.Stability);
        Assert.Equal(55, memory.Confidence);
        Assert.Equal(AvatarMemoryLifecycle.Active, memory.LifecycleStatus);

        memory.MarkLifecycle(AvatarMemoryLifecycle.Historical, null, Now.AddDays(1));

        Assert.Equal(AvatarMemoryLifecycle.Historical, memory.LifecycleStatus);
        Assert.NotNull(memory.ValidTo);
        Assert.False(AvatarMemoryLifecycle.IsCurrent(memory.LifecycleStatus));
    }

    [Fact]
    public void MemoryDraftWithoutV2FieldsStillWorks()
    {
        var memory = AvatarMemory.Create(
            new AvatarMemoryDraft(null, "habit", null, "我每天睡前写第二天的计划。", null, null, null, null, null, 3, "chat"),
            Now);

        Assert.Equal(AvatarMemoryLifecycle.Active, memory.LifecycleStatus);
        Assert.Equal(60, memory.Confidence);
        Assert.Equal(Now, memory.ValidFrom);
        Assert.Equal("{}", memory.StructuredData);
    }

    [Fact]
    public void QueryClassifier_RoutesStyleHistoryAndRelationship()
    {
        Assert.Equal(AvatarQuestionType.Style, AvatarQueryClassifier.Analyze("帮我写一段回复，像我平时说话那样").Type);
        Assert.Equal(AvatarQuestionType.History, AvatarQueryClassifier.Analyze("我以前为什么拒绝那个机会？").Type);
        Assert.Equal(AvatarQuestionType.Relationship, AvatarQueryClassifier.Analyze("我对老王的态度是什么？", ["老王"]).Type);
        Assert.Equal(AvatarQuestionType.Future, AvatarQueryClassifier.Analyze("如果现在有个合作，我会接受吗？").Type);
    }

    [Fact]
    public void QueryClassifier_HistoryIntentCarriesChangeSignal()
    {
        var analysis = AvatarQueryClassifier.Analyze("我以前和现在的想法有什么变化？");

        Assert.True(analysis.NeedsHistory);
        Assert.Equal(AvatarQueryAnalysis.TimeIntentChange, analysis.TimeIntent);
        Assert.Contains(AvatarMemoryTypes.Episodic, analysis.NeededMemoryTypes);
    }

    [Fact]
    public void TextSimilarity_JaccardDetectsNearDuplicate()
    {
        var score = AvatarTextSimilarity.Jaccard(
            "我更喜欢先行动再根据反馈调整，而不是一直空想。",
            "我更喜欢先行动再根据反馈调整，而不是一直空想。");

        Assert.Equal(1, score, 3);
        Assert.True(score > 0.82);
    }

    [Fact]
    public void Feedback_RequiresReasonWhenUnlike()
    {
        Assert.Throws<AvatarValidationException>(() => AvatarFeedback.Create(
            1, [], "unlike", null, null, null, null, Now));
    }

    [Fact]
    public void Feedback_CorrectionKeepsCorrectedText()
    {
        var feedback = AvatarFeedback.Create(
            1, [Guid.NewGuid()], "correction", "fact_error", null, "我不是 2024 年离职，是 2023 年。", null, Now);

        Assert.Equal(AvatarFeedback.Correction, feedback.FeedbackType);
        Assert.Contains("2023", feedback.CorrectedText);
    }

    [Fact]
    public void Ingestion_TransitionsFromQueuedToCompleted()
    {
        var ingestion = AvatarIngestion.Create(
            AvatarIngestion.SourceFile, "diary.txt", "hash:file:v2", [Guid.NewGuid()], null, Now);

        Assert.Equal(AvatarIngestion.Queued, ingestion.Status);

        ingestion.MarkRunning(AvatarIngestion.StageExtracting, Now.AddSeconds(1));
        Assert.Equal(AvatarIngestion.Running, ingestion.Status);

        ingestion.Complete(AvatarIngestion.StageCompleted, "{}", 3, 1, Now.AddSeconds(5));
        Assert.True(ingestion.IsTerminal);
        Assert.Equal(3, ingestion.MemoryCount);
    }

    [Fact]
    public void EvalCase_NormalizesCategoryAndSplit()
    {
        var evalCase = AvatarEvalCase.Create(
            "表达", "别人否定我时会怎么回应？", "先问依据，再决定要不要争。",
            [], [], ["像我的语气"], "hidden", true, Now);

        Assert.Equal("style", evalCase.Category);
        Assert.Equal(AvatarEvalCase.SplitHidden, evalCase.Split);
        Assert.Single(evalCase.Rubric);
    }

    [Fact]
    public void InsightVersion_KeepsCandidateUntilConfirmed()
    {
        var candidate = AvatarInsight.Create("values", "看重执行力", "我更尊重持续行动的人。", 60, [], Now);
        var version = AvatarInsightVersion.Create(candidate, [Guid.NewGuid()], "工作", AvatarInsightVersion.StatusCandidate, null, Now);

        Assert.Equal(AvatarInsightVersion.StatusCandidate, version.Status);
        Assert.Null(version.InsightId);

        version.MarkConfirmed(Guid.NewGuid(), Now.AddDays(1));
        Assert.Equal(AvatarInsightVersion.StatusConfirmed, version.Status);
        Assert.NotNull(version.InsightId);
    }
}
