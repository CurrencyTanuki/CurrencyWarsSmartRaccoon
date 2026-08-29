using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」血量门限判定单测，验证阈值边界。</summary>
public sealed class FateGrailHealthGateTests
{
    [Theory]
    [InlineData(88)]
    [InlineData(94)]
    [InlineData(120)]
    public void HpAtOrAboveGate_NeedsNoDiode(int hp)
    {
        Assert.Equal(
            FateGrailHealthGate.HealthDecision.NoDiodeNeeded,
            FateGrailHealthGate.EvaluateHealthGate(hp));
    }

    [Theory]
    [InlineData(78)]
    [InlineData(80)]
    [InlineData(84)]
    [InlineData(87)]
    public void HpWithinDiodeReach_BuyDiodeAndProceed(int hp)
    {
        Assert.Equal(
            FateGrailHealthGate.HealthDecision.BuyDiodeAndProceed,
            FateGrailHealthGate.EvaluateHealthGate(hp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(76)]
    [InlineData(77)]
    public void HpBelowDiodeReach_MiracleLineUnavailable(int hp)
    {
        Assert.Equal(
            FateGrailHealthGate.HealthDecision.MiracleLineUnavailable,
            FateGrailHealthGate.EvaluateHealthGate(hp));
    }

    [Theory]
    [InlineData(88)]
    [InlineData(90)]
    public void MiracleClickable_WithoutDiode_WhenHpAtOrOver88(int hp)
    {
        Assert.True(FateGrailHealthGate.IsMiracleCompensationClickable(hp));
    }

    [Fact]
    public void MiracleClickable_AfterDiode_WhenHpWithinReach()
    {
        // 84 + 10 = 94 >= 88，可点
        Assert.True(
            FateGrailHealthGate.IsMiracleCompensationClickable(
                84, diodeAlreadyApplied: true));
        // 未吃二极管时 84<88 不可点
        Assert.False(FateGrailHealthGate.IsMiracleCompensationClickable(84));
    }

    [Fact]
    public void Boundary_77WithDiode_StillNotClickable()
    {
        // 77 + 10 = 87 < 88 -> 不可点
        Assert.False(
            FateGrailHealthGate.IsMiracleCompensationClickable(
                77, diodeAlreadyApplied: true));
        Assert.Equal(
            FateGrailHealthGate.HealthDecision.MiracleLineUnavailable,
            FateGrailHealthGate.EvaluateHealthGate(77));
    }
}
