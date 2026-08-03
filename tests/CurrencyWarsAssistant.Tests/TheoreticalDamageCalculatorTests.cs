using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class TheoreticalDamageCalculatorTests
{
    [Theory]
    [InlineData("1-6", 180, 60, 1_500)]
    [InlineData("2-6", 150, 50, 1_500)]
    [InlineData("3-4", 120, 20, 1_200)]
    public void PerfectNodeUsesPlaneMaximum(
        string nodeId,
        int maximum,
        int remaining,
        long expected)
    {
        var result = TheoreticalDamageCalculator.Calculate(
            nodeId,
            1_000,
            RemainingActionValueState.Create(
                remaining / 100,
                remaining % 100),
            NodeClearStatus.Perfect,
            hasWalter: false,
            walterStarLevel: null,
            confirmedActionIncrease: 0,
            reliableActionSamples: 4);

        Assert.Equal(maximum, result.EffectiveMaximumActionValue);
        Assert.Equal(expected, result.Value);
        Assert.Equal(TheoreticalDamageQuality.Exact, result.Quality);
    }

    [Fact]
    public void NotPerfectNodeUsesFinalDamageDirectly()
    {
        var result = TheoreticalDamageCalculator.Calculate(
            "2-4",
            987_654,
            RemainingActionValueState.Create(0, 3),
            NodeClearStatus.NotPerfect,
            hasWalter: false,
            walterStarLevel: null,
            confirmedActionIncrease: 0,
            reliableActionSamples: 2);

        Assert.Equal(987_654, result.Value);
        Assert.Equal(TheoreticalDamageQuality.ActionExhausted, result.Quality);
    }

    [Fact]
    public void SparseThreeStarWalterUsesMarkedEstimate()
    {
        var result = TheoreticalDamageCalculator.Calculate(
            "3-4",
            1_000,
            RemainingActionValueState.Create(0, 19),
            NodeClearStatus.Perfect,
            hasWalter: true,
            walterStarLevel: 3,
            confirmedActionIncrease: 0,
            reliableActionSamples: 1);

        Assert.Equal(1_119, result.EffectiveMaximumActionValue);
        Assert.Equal(TheoreticalDamageQuality.WalterEstimated, result.Quality);
        Assert.Contains("estimated", result.Rule);
    }
}
