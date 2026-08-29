using System;
using System.Collections.Generic;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」备战禁卖策略单测。</summary>
public sealed class FateGrailPreparationPolicyTests
{
    private const string Rin = "远坂凛";
    private const string Gil = "吉尔伽美什";
    private const string Saber = "Saber";
    private const string ArcherName = "Archer";

    [Fact]
    public void ApplyRetained_AllGrailMembersGoToRetained_OnlyHeldGoesToRequired()
    {
        var source = new PreparationBoardOptions();

        var applied = FateGrailPreparationPolicy.ApplyRetained(
            source,
            heldBenchNames: new[] { Rin });

        // 所有命杯成员(含Archer)都进保留禁卖名单
        Assert.Contains(Rin, applied.RetainedCharacterNames);
        Assert.Contains(Gil, applied.RetainedCharacterNames);
        Assert.Contains(Saber, applied.RetainedCharacterNames);
        Assert.Contains(ArcherName, applied.RetainedCharacterNames);

        // 只有真正在场的命杯成员才进 Required(必留)，避免强约束误判
        Assert.Contains(Rin, applied.RequiredRetainedCharacterNames);
        Assert.DoesNotContain(Gil, applied.RequiredRetainedCharacterNames);
        Assert.DoesNotContain(ArcherName, applied.RequiredRetainedCharacterNames);
    }

    [Fact]
    public void ApplyRetained_PreservesExistingRetainedAndRequired()
    {
        var source = new PreparationBoardOptions
        {
            RetainedCharacterNames = new HashSet<string>(["黑塔"],
                System.StringComparer.OrdinalIgnoreCase),
            RequiredRetainedCharacterNames = new HashSet<string>(["黑塔"],
                System.StringComparer.OrdinalIgnoreCase),
        };

        var applied = FateGrailPreparationPolicy.ApplyRetained(source, null);

        Assert.Contains("黑塔", applied.RetainedCharacterNames);
        Assert.Contains("黑塔", applied.RequiredRetainedCharacterNames);
        // 源对象未被原地修改（保留/必留名单元素未变）
        Assert.Single(source.RetainedCharacterNames);
        Assert.Single(source.RequiredRetainedCharacterNames);
        Assert.Contains("黑塔", source.RetainedCharacterNames);
    }

    [Fact]
    public void ApplyRetained_PassesThroughOtherFields()
    {
        var source = new PreparationBoardOptions
        {
            BenchSaleMode = PreparationBenchSaleMode.SellAll,
            InterestThreshold = 12,
            FastReroll = FastRerollMode.Fast,
            EnablePresetPriorityDeployment = false,
            ProtectFirstBenchSlotFromSale = true,
        };

        var applied = FateGrailPreparationPolicy.ApplyRetained(source, null);

        Assert.Equal(PreparationBenchSaleMode.SellAll, applied.BenchSaleMode);
        Assert.Equal(12, applied.InterestThreshold);
        Assert.Equal(FastRerollMode.Fast, applied.FastReroll);
        Assert.False(applied.EnablePresetPriorityDeployment);
        Assert.True(applied.ProtectFirstBenchSlotFromSale);
    }

    [Fact]
    public void ApplyRetained_NullHeld_IsSafe()
    {
        var applied = FateGrailPreparationPolicy.ApplyRetained(
            new PreparationBoardOptions(), null);
        Assert.Contains(Saber, applied.RetainedCharacterNames);
        Assert.Empty(applied.RequiredRetainedCharacterNames);
    }
}
