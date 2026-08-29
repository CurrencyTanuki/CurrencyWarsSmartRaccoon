using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>定稿决策树 L1~L10 最终判定全分支测试。</summary>
public class GrailFinalJudgeTests
{
    private static GrailRunSnapshot Snapshot(
        GrailUserGoal goal = GrailUserGoal.Single,
        bool hasFiveCost = true,
        bool allLettersOpened = true,
        bool cauldron = false,
        bool miracle = false,
        int? miracleHealth = null,
        bool xilian = false)
    {
        return new GrailRunSnapshot
        {
            Goal = goal,
            HasFiveCostBody = hasFiveCost,
            LettersObtained = 2,
            LettersOpened = allLettersOpened ? 2 : 0,
            InfiniteCauldronSelected = cauldron,
            MiracleCompensationSelected = miracle,
            MiracleCompensationSelectedAtHealth = miracleHealth,
            XilianOnField = xilian,
        };
    }

    [Fact]
    public void L4_Fails_WithoutFiveCostBody()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(hasFiveCost: false));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
        Assert.Equal("L4", verdict.TreeNode);
    }

    [Fact]
    public void L4_Fails_WithUnopenedLetters()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(allLettersOpened: false, cauldron: true));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
        Assert.Equal("L4", verdict.TreeNode);
    }

    [Fact]
    public void L6_Single_IsSuccess()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(goal: GrailUserGoal.Single, cauldron: true));
        Assert.Equal(GrailVerdictKind.Success, verdict.Kind);
        Assert.Equal("L6→L3→L7", verdict.TreeNode);
    }

    [Fact]
    public void L6_All_IsNotYet_FullMembersRequireMiraclePlusXilian()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(goal: GrailUserGoal.All, cauldron: true));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
        Assert.Equal("L6+L3→L5", verdict.TreeNode);
    }

    [Fact]
    public void L2_Single_Health89_IsSuccess()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.Single, miracle: true, miracleHealth: 89));
        Assert.Equal(GrailVerdictKind.Success, verdict.Kind);
        Assert.Equal("L2→L9→L7", verdict.TreeNode);
    }

    [Fact]
    public void L2_Health88_Boundary_IsNotYet()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.Single, miracle: true, miracleHealth: 88));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
        Assert.Equal("L2→L5", verdict.TreeNode);
    }

    [Fact]
    public void L2_Health89_Boundary_IsSuccess()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.Single, miracle: true, miracleHealth: 89));
        Assert.Equal(GrailVerdictKind.Success, verdict.Kind);
    }

    [Fact]
    public void L2_All_WithXilian_IsSuccess()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.All, miracle: true, miracleHealth: 95, xilian: true));
        Assert.Equal(GrailVerdictKind.Success, verdict.Kind);
        Assert.Equal("L2→L9→L10→L7", verdict.TreeNode);
    }

    [Fact]
    public void L2_All_WithoutXilian_IsNotYet()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.All, miracle: true, miracleHealth: 95, xilian: false));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
        Assert.Equal("L10→L5", verdict.TreeNode);
    }

    [Fact]
    public void NeitherTrialSelected_IsNotYet()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(goal: GrailUserGoal.Single));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
        Assert.Equal("L2→L5", verdict.TreeNode);
    }

    [Fact]
    public void L2_HealthUnknown_IsNotYet()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.Single, miracle: true, miracleHealth: null));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
    }

    [Fact]
    public void All_CauldronPlusMiraclePlusXilian_IsSuccess()
    {
        // 子代理审查发现的树结构死角修正：全员 + 无限之釜 + 奇迹代偿（血≥89）+ 昔涟在场 → 收工
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.All, cauldron: true,
            miracle: true, miracleHealth: 95, xilian: true));
        Assert.Equal(GrailVerdictKind.Success, verdict.Kind);
        Assert.Equal("L2→L9→L10→L7", verdict.TreeNode);
    }

    [Fact]
    public void All_CauldronOnly_StillNotYet()
    {
        var verdict = GrailFinalJudge.Judge(Snapshot(
            goal: GrailUserGoal.All, cauldron: true));
        Assert.Equal(GrailVerdictKind.NotYet, verdict.Kind);
        Assert.Equal("L6+L3→L5", verdict.TreeNode);
    }
}
