using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 凑息材料过滤纯逻辑（1.2.116，用户拍板"1-2 出战前凑 10 金"）契约守卫：
/// 同名合计 ≥2（备战席+上场跨区合计）=升星材料链不卖；识别误名/无名明细保守跳过；
/// 星徽标记不参与名字（[星徽] 尾被剥掉后同名计数）。
/// </summary>
public sealed class InterestTopUpPlannerTests
{
    [Fact]
    public void SingletonBenchCardIsInterestSellable()
    {
        var counts = InterestTopUpPlanner.CountNames(
            ["0:黑塔", "2:艾丝妲"], ["F0:希儿"]);
        Assert.True(InterestTopUpPlanner.IsInterestSellable("0:黑塔", counts));
        Assert.True(InterestTopUpPlanner.IsInterestSellable("2:艾丝妲", counts));
    }

    [Fact]
    public void DuplicateAcrossBenchAndDeployedIsMaterialAndBlocked()
    {
        // 跨区同名合计=2：备战席一张+场上一张=升星材料链，凑息绝不卖（保守口径）。
        var counts = InterestTopUpPlanner.CountNames(
            ["0:希儿", "2:艾丝妲"], ["F0:希儿"]);
        Assert.False(InterestTopUpPlanner.IsInterestSellable("0:希儿", counts));
        Assert.True(InterestTopUpPlanner.IsInterestSellable("2:艾丝妲", counts));
    }

    [Fact]
    public void BadgeSuffixIsStrippedBeforeCounting()
    {
        var counts = InterestTopUpPlanner.CountNames(
            ["0:希儿[星徽]", "1:希儿"], []);
        Assert.False(InterestTopUpPlanner.IsInterestSellable("0:希儿[星徽]", counts));
        Assert.False(InterestTopUpPlanner.IsInterestSellable("1:希儿", counts));
    }

    [Theory]
    [InlineData("没有冒头的明细")]
    [InlineData(":无名")]
    [InlineData("0:")]
    public void UnparsableDetailsAreConservativelyBlocked(string detail)
    {
        var counts = InterestTopUpPlanner.CountNames([detail], []);
        Assert.False(InterestTopUpPlanner.IsInterestSellable(detail, counts));
    }

    [Fact]
    public void TripleSameNameIsMaterialAndBlocked()
    {
        var counts = InterestTopUpPlanner.CountNames(
            ["0:黑塔", "1:黑塔", "2:黑塔"], []);
        Assert.False(InterestTopUpPlanner.IsInterestSellable("0:黑塔", counts));
    }

    [Fact]
    public void MultiEquipSuffixIsStrippedBeforeCounting()
    {
        // 真实明细格式（GrailSnapshotAssembler）：[星徽+装备066] 复合尾——剥整尾后同名计数：
        // 场上一张带复合尾 + 备战席一张素名 = 合计 2 = 材料链，备战席那张不卖。
        var counts = InterestTopUpPlanner.CountNames(
            ["1:希儿"], ["F0:希儿[星徽+装备066]"]);
        Assert.False(InterestTopUpPlanner.IsInterestSellable("1:希儿", counts));
        Assert.False(InterestTopUpPlanner.IsInterestSellable("3:希儿", counts));
    }

    [Theory]
    [InlineData(9, 1, 10, true)]   // 9 金 + 1 费卡 = 10 达标
    [InlineData(8, 2, 10, true)]   // 8 金 + 2 费卡 = 10 达标
    [InlineData(5, 1, 10, false)]  // 审查 P1-1 白卖边界：卖了也到不了 → 不卖
    [InlineData(5, 4, 10, false)]  // 5+4=9 仍不足
    public void ReachesTargetBoundarySemantics(int gold, int saleValue, int target, bool expected)
    {
        Assert.Equal(expected, InterestTopUpPlanner.ReachesTarget(gold, saleValue, target));
    }

    [Fact]
    public void UnknownSaleValueNeverReachesTarget()
    {
        // 费用解析不出=null=永不达标（保守不卖；顺带覆盖 Gold=0 假帧误启动）。
        Assert.False(InterestTopUpPlanner.ReachesTarget(9, null, 10));
        Assert.False(InterestTopUpPlanner.ReachesTarget(0, 5, 10));
    }
}
