using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Tests;

public sealed class AvatarDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProfileUpdate_NormalizesRiskPreferenceAndTrimsFields()
    {
        var profile = AvatarProfile.Empty(Now);

        profile.Update(new AvatarProfileDraft(
            "  小林  ",
            "  白天上班，晚上写网文  ",
            "偏内向，自驱力强但容易拖延",
            "稳定感 + 作品被看见",
            "能独立完成长篇",
            "拖延、完美主义",
            "写作、产品设计",
            "每晚 3 小时，存款 6 个月",
            "工作日加班多",
            "一年内完成第一本长篇",
            "激进"), Now);

        Assert.Equal("小林", profile.DisplayName);
        Assert.Equal("白天上班，晚上写网文", profile.Headline);
        Assert.Equal("aggressive", profile.RiskPreference);
        Assert.True(profile.IsConfigured);
    }

    [Fact]
    public void ProfileUpdate_RequiresDisplayName()
    {
        var profile = AvatarProfile.Empty(Now);
        Assert.Throws<AvatarValidationException>(() =>
            profile.Update(new AvatarProfileDraft("  ", null, null, null, null, null, null, null, null, null, null), Now));
    }

    [Fact]
    public void ExperienceCreate_NormalizesUnknownCategoryAndClampsImpact()
    {
        var experience = AvatarExperience.Create(new AvatarExperienceDraft(
            null, "不存在的分类", "第一本长篇完结", "写了 8 个月", "success", "positive", 9,
            ["写作", "写作", "  "]), Now);

        Assert.Equal("其它", experience.Category);
        Assert.Equal("success", experience.Outcome);
        Assert.Equal(5, experience.Impact);
        Assert.Single(experience.Tags);
        Assert.Equal(Now, experience.OccurredAt);
    }

    [Fact]
    public void ExperienceCreate_RequiresTitle()
    {
        Assert.Throws<AvatarValidationException>(() => AvatarExperience.Create(
            new AvatarExperienceDraft(null, "成长", " ", "内容", null, null, 3, null), Now));
    }

    [Fact]
    public void BehaviorCreate_ClampsDurationAndNormalizesSource()
    {
        var behavior = AvatarBehavior.Create(
            new AvatarBehaviorDraft(null, "创作", "写第三章", 5_000, 9, null, "unknown"), Now);

        Assert.Equal(24 * 60, behavior.DurationMinutes);
        Assert.Equal(5, behavior.Mood);
        Assert.Equal("manual", behavior.Source);
    }

    [Fact]
    public void BehaviorCreate_KeepsToolSource()
    {
        var behavior = AvatarBehavior.Create(
            new AvatarBehaviorDraft(null, "学习", "读完一章拆文", 45, 4, null, "tool"), Now);

        Assert.Equal("tool", behavior.Source);
        Assert.Equal(45, behavior.DurationMinutes);
    }

    [Fact]
    public void RelationshipUpdate_ValidatesNameAndClampsScores()
    {
        var relationship = AvatarRelationship.Create(
            new AvatarRelationshipDraft("阿哲", "朋友", 4, 4, "一起写小说"), Now);

        relationship.Update(new AvatarRelationshipDraft("阿哲", "同事", 99, 0, "同组产品经理"), Now.AddDays(1));

        Assert.Equal("同事", relationship.Role);
        Assert.Equal(5, relationship.Closeness);
        Assert.Equal(3, relationship.Influence);
        Assert.Throws<AvatarValidationException>(() =>
            relationship.Update(new AvatarRelationshipDraft("", "朋友", 1, 1, null), Now));
    }

    [Fact]
    public void Simulation_CompleteClampsProbabilitiesAndKeepsStructuredFields()
    {
        var simulation = AvatarSimulation.CreatePending(
            new AvatarSimulationDraft("未来半年把副业写作做到月入 5000", "稳定更新", 6), "deepseek-chat", Now);

        simulation.Complete(new AvatarSimulationResult(
            "结论",
            "报告",
            130,
            -5,
            [new AvatarKeyFactor("更新稳定性", "positive", 35, "过去 20 章按时完成")],
            [new AvatarMilestone("第 1 个月", "建立存稿", "存稿 10 章")],
            [new AvatarRisk("加班挤压写作时间", 60, "固定早起 1 小时")],
            ["每周复盘一次"]), "deepseek-chat", Now.AddMinutes(1));

        Assert.Equal("completed", simulation.Status);
        Assert.Equal(100, simulation.SuccessProbability);
        Assert.Equal(0, simulation.Confidence);
        Assert.Single(simulation.KeyFactors);
        Assert.Single(simulation.Milestones);
        Assert.Single(simulation.Risks);
        Assert.Single(simulation.NextActions);
    }

    [Fact]
    public void Simulation_FailRecordsError()
    {
        var simulation = AvatarSimulation.CreatePending(
            new AvatarSimulationDraft("换城市工作", null, 12), "local", Now);

        simulation.Fail("模型连接失败", Now.AddSeconds(5));

        Assert.Equal("failed", simulation.Status);
        Assert.Equal("模型连接失败", simulation.ErrorMessage);
        Assert.NotNull(simulation.CompletedAt);
    }

    [Fact]
    public void SimulationCreate_RequiresScenario()
    {
        Assert.Throws<AvatarValidationException>(() =>
            AvatarSimulation.CreatePending(new AvatarSimulationDraft("  ", null, 6), "m", Now));
    }

    [Fact]
    public void Readiness_GrowsWithMoreInput()
    {
        var empty = AvatarService.EvaluateReadiness(AvatarProfile.Empty(Now), 0, 0, 0);
        Assert.Equal(0, empty.Score);
        Assert.Contains(empty.Missing, item => item.Contains("喂点什么"));

        var profile = AvatarProfile.Empty(Now);
        profile.Update(new AvatarProfileDraft("小林", "兼职作者", "内向", "稳定", "能写", "拖延",
            "写作", "每晚 3 小时", "加班多", "完成长篇", "balanced"), Now);

        var full = AvatarService.EvaluateReadiness(profile, 10, 5, 1);
        Assert.Equal(100, full.Score);
        Assert.Empty(full.Missing);

        var partial = AvatarService.EvaluateReadiness(profile, 2, 1, 0);
        Assert.InRange(partial.Score, 1, 99);
        Assert.NotEmpty(partial.Missing);
    }
}
