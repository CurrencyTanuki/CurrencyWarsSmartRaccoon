using System;
using System.Collections.Generic;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」投资策略自选辅助单测。</summary>
public sealed class InvestmentStrategyPickerTests
{
    [Fact]
    public void PicksColor_WhenBothAvailable()
    {
        var pick = InvestmentStrategyPicker.PickPurchaseSpecialist(
            new[] { InvestmentStrategyPicker.PurchaseSpecialistGold,
                    InvestmentStrategyPicker.PurchaseSpecialistColor });
        // 彩(每5刷) 优先于 金(每7刷)
        Assert.Equal(InvestmentStrategyPicker.PurchaseSpecialistColor, pick);
    }

    [Fact]
    public void PicksGold_WhenOnlyGold()
    {
        Assert.Equal(
            InvestmentStrategyPicker.PurchaseSpecialistGold,
            InvestmentStrategyPicker.PickPurchaseSpecialist(
                new[] { InvestmentStrategyPicker.PurchaseSpecialistGold, "other" }));
    }

    [Fact]
    public void ReturnsNull_WhenNoPurchaseSpecialist()
    {
        Assert.Null(
            InvestmentStrategyPicker.PickPurchaseSpecialist(
                new[] { "some_other", InvestmentStrategyPicker.PurchaseSpecialistColor + "x" }));
        Assert.Null(InvestmentStrategyPicker.PickPurchaseSpecialist(Array.Empty<string>()));
        Assert.Null(InvestmentStrategyPicker.PickPurchaseSpecialist(null));
    }

    [Theory]
    [InlineData("investment_strategy_051")]
    [InlineData("investment_strategy_238")]
    [InlineData("INVESTMENT_STRATEGY_238")]
    public void IsPurchaseSpecialist_TrueForBothCasesInsensitive(string id)
    {
        Assert.True(InvestmentStrategyPicker.IsPurchaseSpecialist(id));
    }

    [Fact]
    public void IsPurchaseSpecialist_FalseForOthers()
    {
        Assert.False(InvestmentStrategyPicker.IsPurchaseSpecialist("investment_strategy_276"));
        Assert.False(InvestmentStrategyPicker.IsPurchaseSpecialist(""));
    }
}
