using System;
using System.Collections.Generic;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」商店流程决策面（编排门面）单测。</summary>
public sealed class FateGrailFlowDeciderTests
{
    private const string Rin = "远坂凛";
    private const string Gil = "吉尔伽美什";
    private const string Saber = "Saber";
    private const string ArcherName = "Archer";

    [Fact]
    public void Compute_ProducesStorePurchaseNames_AndSpecialist()
    {
        var d = FateGrailFlowDecider.Compute(
            currentHp: 84,
            availableStrategyIds: new[]
                { InvestmentStrategyPicker.PurchaseSpecialistGold },
            heldOrOwnedNames: null);

        // 商店购买名单 = 命杯 1/2/3 费，不含 Archer
        Assert.Contains(Rin, d.AutoPurchaseNames);
        Assert.Contains(Gil, d.AutoPurchaseNames);
        Assert.Contains(Saber, d.AutoPurchaseNames);
        Assert.DoesNotContain(ArcherName, d.AutoPurchaseNames);

        // 血量：84 -> BuyDiodeAndProceed
        Assert.Equal(
            FateGrailHealthGate.HealthDecision.BuyDiodeAndProceed,
            d.Health);

        // 只有金 051 -> 选金
        Assert.Equal(InvestmentStrategyPicker.PurchaseSpecialistGold,
            d.ChosenPurchaseSpecialist);

        // 保留名单 = 全部 4 名命杯成员（N5：Archer 无条件禁卖）；购买名单仍不含 Archer
        Assert.Contains(Saber, d.RetainedNames);
        Assert.Contains(ArcherName, d.RetainedNames);
    }

    [Fact]
    public void Compute_ColorSpecialist_PreferredOverGold()
    {
        var d = FateGrailFlowDecider.Compute(
            90 /* no diode needed */,
            new[]
            {
                InvestmentStrategyPicker.PurchaseSpecialistGold,
                InvestmentStrategyPicker.PurchaseSpecialistColor
            },
            null);

        Assert.Equal(
            FateGrailHealthGate.HealthDecision.NoDiodeNeeded, d.Health);
        Assert.Equal(InvestmentStrategyPicker.PurchaseSpecialistColor,
            d.ChosenPurchaseSpecialist);
    }

    [Fact]
    public void Compute_NoSpecialistAndBadHealth_ReportsBoth()
    {
        var d = FateGrailFlowDecider.Compute(
            60,
            availableStrategyIds: null,
            heldOrOwnedNames: new[] { Rin });

        Assert.Equal(
            FateGrailHealthGate.HealthDecision.MiracleLineUnavailable,
            d.Health);
        Assert.Null(d.ChosenPurchaseSpecialist);
        // 已持有凛 -> 保留名单含凛；Archer 未持有也无条件禁卖（N5）
        Assert.Contains(Rin, d.RetainedNames);
        Assert.Contains(ArcherName, d.RetainedNames);
    }

    [Fact]
    public void Compute_NullInputs_IsSafe()
    {
        // null 策略 + null 已持有 + hp 任意 -> 不抛异常
        var d = FateGrailFlowDecider.Compute(84, null, null);
        Assert.NotNull(d.AutoPurchaseNames);
        Assert.NotNull(d.RetainedNames);
    }
}
