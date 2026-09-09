using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// PickWinningSide/IsWinningTrial 表征测试（09-10 审查 P3-2 建议）：钉死默认路径
/// （WishTrialSelectionAutomation 无 select 委托时）的关键试炼命中口径——
/// 特别是"奇迹代偿不查血量"这一既有行为，防止与决策层 F13/F15a 血量门混淆；
/// 以及 C4 死锁修复的输入契约：两侧均为非关键试炼（如 回路升级·一/自古枪兵）
/// 时 PickWinningSide 返回 null，由调用方回落 rule 四.7 兜底选左。
/// </summary>
public class WishTrialSidePickTests
{
    [Fact]
    public void MiracleCompensation_MatchesByName_RegardlessOfReward()
    {
        Assert.True(WishTrialSelectionAutomation.IsWinningTrial("令咒决议·奇迹代偿", "扣88血换奖励"));
        Assert.True(WishTrialSelectionAutomation.IsWinningTrial("奇迹代偿", null));
    }

    [Fact]
    public void LetterCurse_MatchesOnlyWithFiveCostLetterReward()
    {
        Assert.True(WishTrialSelectionAutomation.IsWinningTrial("令咒决议·行为限制", "五费聘用书"));
        Assert.True(WishTrialSelectionAutomation.IsWinningTrial("令咒决议·回路过载", "五费聘用书x2"));
        // 奖励非聘用书 → 不命中（F10：本侧不选）。
        Assert.False(WishTrialSelectionAutomation.IsWinningTrial("令咒决议·行为限制", "财富"));
        // 四费聘用书与五费聘用书编辑距离 1，必须不被模糊匹配误命中。
        Assert.False(WishTrialSelectionAutomation.IsWinningTrial("令咒决议·行为限制", "四费聘用书"));
    }

    [Fact]
    public void NonKeyTrials_BothSides_ReturnNull_C4DeadlockInput()
    {
        // 09-09 C4 实拍弹框：两侧均非关键——修复前此处 null→"不点击"=永久卡死；
        // 修复后由调用方回落选左。本测试钉死 null 契约（回落逻辑在自动化层）。
        var side = WishTrialSelectionAutomation.PickWinningSide(
            "回路升级·一", "金币",
            "自古枪兵…？", "穿刺死棘之枪");
        Assert.Null(side);
    }

    [Fact]
    public void WinningOnBothSides_PicksLeft()
    {
        var side = WishTrialSelectionAutomation.PickWinningSide(
            "无限之釜", "三星五费",
            "令咒决议·回路过载", "五费聘用书");
        Assert.Equal(0, side);
    }

    [Fact]
    public void WinningOnRightOnly_PicksRight()
    {
        var side = WishTrialSelectionAutomation.PickWinningSide(
            "灵脉红利.二", "经验",
            "令咒决议·奇迹代偿", "扣88血");
        Assert.Equal(1, side);
    }

    [Fact]
    public void NullNames_NeverWin()
    {
        Assert.False(WishTrialSelectionAutomation.IsWinningTrial(null, "五费聘用书"));
        Assert.False(WishTrialSelectionAutomation.IsWinningTrial("", "五费聘用书"));
    }
}
