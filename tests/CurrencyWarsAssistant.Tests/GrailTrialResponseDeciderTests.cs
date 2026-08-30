using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>定稿决策树 F1~F17 祈愿弹框响应决策全分支测试。</summary>
public class GrailTrialResponseDeciderTests
{
    private static GrailRunSnapshot Snapshot(
        GrailUserGoal goal = GrailUserGoal.Single,
        int? health = 100,
        bool hasFiveCost = true,
        int wishesResponded = 1)
    {
        return new GrailRunSnapshot
        {
            Goal = goal,
            TeamHealth = health,
            HasFiveCostBody = hasFiveCost,
            WishesResponded = wishesResponded,
        };
    }

    private static GrailTrialPairContext Pair(
        string? leftName = null, string? leftReward = null,
        string? rightName = null, string? rightReward = null) =>
        new(leftName, leftReward, rightName, rightReward);

    [Fact]
    public void F9_FirstWish_BlindLeft_EvenIfKeyTrialPresent()
    {
        // 首祈愿（2 档）不识别、盲选左——即便右例试炼名含关键字也不改选（2 档池不可能出关键试炼）
        var s = Snapshot(wishesResponded: 0);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "五战开篇", rightName: "令咒决议·奇迹代偿"));

        Assert.Equal(GrailTrialResponseKind.BlindPickLeft, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
        Assert.Equal("F9", response.TreeNode);
        Assert.False(response.OpenLettersAfter);
    }

    [Fact]
    public void F13_HealthTooLow_MustSelectOtherSide()
    {
        var s = Snapshot(health: 80);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "铸剑"));

        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
        Assert.Equal("F13→F15→F15a", response.TreeNode);
    }

    [Fact]
    public void F13_HealthUnknown_DefensivelySelectOtherSide()
    {
        var s = Snapshot(health: null);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "铸剑"));

        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
    }

    [Fact]
    public void F13_Health89Boundary_IsEligible()
    {
        var s = Snapshot(health: 89);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "铸剑"));

        Assert.Equal(GrailTrialResponseKind.SelectMiracleCompensation, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
        Assert.Equal("F13→F14", response.TreeNode);
    }

    [Fact]
    public void F14_NoFiveCostBody_StillSelectsMiracle()
    {
        // 用户 2026-08-30 拍板：奇迹代偿血≥89 必选，本体不作当场前置（选完再凑，凑不到才重开）
        var s = Snapshot(health: 95, hasFiveCost: false);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "铸剑"));

        Assert.Equal(GrailTrialResponseKind.SelectMiracleCompensation, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
        Assert.Equal("F13→F14", response.TreeNode);
    }

    [Fact]
    public void F14_BodyAndHealthOk_SelectMiracle()
    {
        var s = Snapshot(health: 95, hasFiveCost: true);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "铸剑", rightName: "令咒决议·奇迹代偿"));

        Assert.Equal(GrailTrialResponseKind.SelectMiracleCompensation, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
    }

    [Fact]
    public void F10_LetterReward_SelectLetterTrial_AndOpenLetters()
    {
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·回路过载", leftReward: "2五费聘用书", rightName: "铸剑", rightReward: "12经验"));

        Assert.Equal(GrailTrialResponseKind.SelectLetterTrial, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
        Assert.True(response.OpenLettersAfter);
        Assert.Equal("F10→F11", response.TreeNode);
    }

    [Fact]
    public void F10_RestrictTrialWithLetterReward_AlsoSelected()
    {
        // 行为限制的奖励一旦识别出是五费聘用书，同样按 F11 选择（F4/F7 共用 F10）
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "铸剑", rightName: "令咒决议·行为限制", rightReward: "五费聘用书"));

        Assert.Equal(GrailTrialResponseKind.SelectLetterTrial, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
        Assert.True(response.OpenLettersAfter);
    }

    [Fact]
    public void F10_NoLetterReward_SelectOtherSide()
    {
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·回路过载", leftReward: "40经验", rightName: "铸剑"));

        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
        Assert.Equal("F10→F12", response.TreeNode);
        Assert.False(response.OpenLettersAfter);
    }

    [Fact]
    public void F8_Single_Cauldron_Finish()
    {
        var s = Snapshot(goal: GrailUserGoal.Single);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "无限之釜", rightName: "铸剑"));

        Assert.Equal(GrailTrialResponseKind.SelectCauldronFinish, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
        Assert.Equal("F8→F16", response.TreeNode);
    }

    [Fact]
    public void F8_All_Cauldron_Continue()
    {
        var s = Snapshot(goal: GrailUserGoal.All);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "无限之釜", rightName: "铸剑"));

        Assert.Equal(GrailTrialResponseKind.SelectCauldronContinue, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
        Assert.Equal("F8→F17", response.TreeNode);
    }

    [Fact]
    public void F5_NoKeyTrial_FallbackLeft()
    {
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "池子里全是红A！", rightName: "万能工坊"));

        Assert.Equal(GrailTrialResponseKind.PickLeftFallback, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
        Assert.Equal("F3→F5", response.TreeNode);
    }

    [Fact]
    public void Priority_MiracleBeatsLetterTrial()
    {
        // 奇迹代偿 + 回路过载（奖励是聘用书）同现：奇迹代偿优先（可完成 > 辅助）
        var s = Snapshot(health: 95, hasFiveCost: true);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "令咒决议·回路过载", rightReward: "2五费聘用书"));

        Assert.Equal(GrailTrialResponseKind.SelectMiracleCompensation, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
    }

    [Fact]
    public void F12_OtherSideIsCauldron_ApplyMarksFlagAndJudgeFinishes()
    {
        // 回路过载（无聘用书奖励）+ 无限之釜：F12 选另一侧=无限之釜，旗标按实际试炼名落地
        var s = Snapshot(goal: GrailUserGoal.Single);
        var ctx = Pair(leftName: "令咒决议·回路过载", leftReward: "40经验", rightName: "无限之釜");
        var response = GrailTrialResponseDecider.Decide(s, ctx);

        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
        Assert.Equal("无限之釜", response.SelectedTrialName);

        var applied = GrailTrialResponseDecider.ApplyTo(s, response, s.TeamHealth);
        Assert.True(applied.InfiniteCauldronSelected);
        Assert.Equal(2, applied.WishesResponded); // 测试快照默认已响应 1 次 + 本次 1 次

        var verdict = GrailFinalJudge.Judge(applied with { HasFiveCostBody = true });
        Assert.Equal(GrailVerdictKind.Success, verdict.Kind);
    }

    [Fact]
    public void Apply_LetterTrial_CountsTwoLettersObtained()
    {
        var s = Snapshot();
        var ctx = Pair(leftName: "令咒决议·回路过载", leftReward: "2五费聘用书", rightName: "铸剑", rightReward: "12经验");
        var response = GrailTrialResponseDecider.Decide(s, ctx);
        var applied = GrailTrialResponseDecider.ApplyTo(s, response, s.TeamHealth);

        Assert.Equal(2, applied.LettersObtained);
        Assert.Equal(0, applied.LettersOpened); // 打开由 F11a 执行层回填
        Assert.Equal(2, applied.WishesResponded); // 测试快照默认已响应 1 次 + 本次 1 次
    }

    [Fact]
    public void Apply_Miracle_RecordsHealthAtSelection()
    {
        var s = Snapshot(health: 90);
        var ctx = Pair(leftName: "令咒决议·奇迹代偿", rightName: "铸剑", rightReward: "12经验");
        var response = GrailTrialResponseDecider.Decide(s, ctx);
        var applied = GrailTrialResponseDecider.ApplyTo(s, response, s.TeamHealth);

        Assert.True(applied.MiracleCompensationSelected);
        Assert.Equal(90, applied.MiracleCompensationSelectedAtHealth);
    }

    [Fact]
    public void Fuzzy_MatchesOcrVariants_AndRejectsDifferentTrials()
    {
        // 编辑距离 ≤1 命中
        Assert.True(GrailFuzzyText.ContainsFuzzy("令人决议·奇迹代偿：扣88血", "奇迹代偿"));
        Assert.True(GrailFuzzyText.ContainsFuzzy("2五费聘用书", "五费聘用书"));
        Assert.True(GrailFuzzyText.ContainsFuzzy("五费聘用收", "五费聘用书"));
        // 不同试炼不得误命中
        Assert.False(GrailFuzzyText.ContainsFuzzy("令咒决议·行为禁锢", "行为限制"));
        Assert.False(GrailFuzzyText.ContainsFuzzy("回路升级·三", "回路过载"));
        Assert.False(GrailFuzzyText.ContainsFuzzy("漆黑之釜", "无限之釜"));
        Assert.False(GrailFuzzyText.ContainsFuzzy(null, "奇迹代偿"));
    }

    [Fact]
    public void Pathological_EligibleBothSidesMiracle_SelectsLeft()
    {
        // 病态局面：双侧都是奇迹代偿且都可选——按先左后右取左，正常选择
        var s = Snapshot(health: 95, hasFiveCost: true);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "令咒决议·奇迹代偿"));

        Assert.Equal(GrailTrialResponseKind.SelectMiracleCompensation, response.Kind);
        Assert.Equal(GrailTrialSide.Left, response.Side);
    }

    [Fact]
    public void Pathological_RejectedBothSidesMiracle_ForcedToOtherSide()
    {
        // 病态局面：双侧都是奇迹代偿且都不可选（血不足）——弹框强制二选一，仍必须给出另一侧
        //（游戏会拒绝，交由上层超时/人工兜底；这是唯一无解分支，如实暴露）
        var s = Snapshot(health: 80, hasFiveCost: true);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "令咒决议·奇迹代偿"));

        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
    }

    [Fact]
    public void F13_Health88Boundary_RejectsMiracle()
    {
        // 88 不满足 ">88"（需 ≥89）
        var s = Snapshot(health: 88);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "铸剑"));
        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
    }

    [Fact]
    public void MiracleRejected_OtherSideIsCauldron_ApplyLandsFlags()
    {
        // 血不足拒奇迹代偿 → 另一侧是无限之釜：改选落地釜旗标 + 釜自带 5 费本体事实
        var s = Snapshot(health: 80, hasFiveCost: false);
        var ctx = Pair(leftName: "令咒决议·奇迹代偿", rightName: "无限之釜");
        var response = GrailTrialResponseDecider.Decide(s, ctx);

        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);

        var applied = GrailTrialResponseDecider.ApplyTo(s, response, s.TeamHealth);
        Assert.True(applied.InfiniteCauldronSelected);
        Assert.True(applied.HasFiveCostBody); // 釜=全三星圣杯角色，选择即产生 5 费本体
        Assert.False(applied.MiracleCompensationSelected);
    }

    [Fact]
    public void F15a_OtherSideHasLetterReward_OpensLetters()
    {
        // 奇迹代偿血不足被拒 → 另一侧令咒决议奖励是聘用书：改选后同样立刻开聘用书（F11 规则推广）
        var s = Snapshot(health: 80);
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·奇迹代偿", rightName: "令咒决议·回路过载", rightReward: "2五费聘用书"));

        Assert.Equal(GrailTrialResponseKind.SelectOtherSide, response.Kind);
        Assert.True(response.OpenLettersAfter);

        var applied = GrailTrialResponseDecider.ApplyTo(s, response, s.TeamHealth);
        Assert.Equal(2, applied.LettersObtained);
    }

    [Fact]
    public void F10_LeftCurseNoLetter_RightCurseWithLetter_SelectsRight()
    {
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·行为限制", leftReward: "40经验",
                    rightName: "令咒决议·回路过载", rightReward: "2五费聘用书"));

        Assert.Equal(GrailTrialResponseKind.SelectLetterTrial, response.Kind);
        Assert.Equal(GrailTrialSide.Right, response.Side);
    }

    [Fact]
    public void F2_BothNamesUnread_NeverBlindClick()
    {
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(s, Pair());
        Assert.Equal(GrailTrialResponseKind.RecognitionUncertain, response.Kind);
        Assert.Null(response.Side);
    }

    [Fact]
    public void F2_OneNameUnread_NeverBlindClick()
    {
        // 左名未读出：右侧是普通试炼，但左侧可能藏着血不足的奇迹代偿 → 不盲点
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(s, Pair(leftName: null, rightName: "铸剑"));
        Assert.Equal(GrailTrialResponseKind.RecognitionUncertain, response.Kind);
    }

    [Fact]
    public void F10_CurseRewardUnread_DoesNotTreatAsNoLetter()
    {
        var s = Snapshot();
        var response = GrailTrialResponseDecider.Decide(
            s, Pair(leftName: "令咒决议·回路过载", leftReward: null, rightName: "铸剑", rightReward: "12经验"));
        Assert.Equal(GrailTrialResponseKind.RecognitionUncertain, response.Kind);
    }
}
