using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>定稿决策树 N3~N11（1-3 投资策略选择）测试。</summary>
public class GrailInvestmentStrategyDeciderTests
{
    [Fact]
    public void N10_ColorSpecialist_SelectedWhenPresent()
    {
        var decision = GrailInvestmentStrategyDecider.Decide(
            ["investment_strategy_317", "investment_strategy_238", "investment_strategy_290"]);

        Assert.Equal(GrailInvestmentStrategyDecider.ColorSpecialistId, decision.StrategyId);
        Assert.Equal(1, decision.SlotIndex);
        Assert.Equal("N3→N10", decision.TreeNode);
    }

    [Fact]
    public void N10_FaultAndColorCoOccur_ColorWins()
    {
        // 树 N10：有且只有 333 与彩 238 可能同现，此时优先彩
        var decision = GrailInvestmentStrategyDecider.Decide(
            ["investment_strategy_333", "investment_strategy_238", null]);

        Assert.Equal(GrailInvestmentStrategyDecider.ColorSpecialistId, decision.StrategyId);
    }

    [Fact]
    public void N10_Fault_Alone_IsSelected()
    {
        var decision = GrailInvestmentStrategyDecider.Decide(
            ["investment_strategy_290", "investment_strategy_333", "investment_strategy_301"]);

        Assert.Equal(GrailInvestmentStrategyDecider.ItIsHisFaultId, decision.StrategyId);
        Assert.Equal(1, decision.SlotIndex);
    }

    [Fact]
    public void N10_GoldSpecialist_AndDiode_SelectedWhenPresent()
    {
        Assert.Equal(
            GrailInvestmentStrategyDecider.GoldSpecialistId,
            GrailInvestmentStrategyDecider.Decide([null, "investment_strategy_051", null]).StrategyId);
        Assert.Equal(
            GrailInvestmentStrategyDecider.DiodeId,
            GrailInvestmentStrategyDecider.Decide(["investment_strategy_276", null, null]).StrategyId);
    }

    [Fact]
    public void N11_NoneHit_PickLeftmost_NoReroll()
    {
        var decision = GrailInvestmentStrategyDecider.Decide(
            ["investment_strategy_290", "investment_strategy_301", "investment_strategy_317"]);

        Assert.Null(decision.StrategyId);
        Assert.Equal("N9→N11", decision.TreeNode);
        Assert.Contains("最左", decision.Reason);
    }
}
