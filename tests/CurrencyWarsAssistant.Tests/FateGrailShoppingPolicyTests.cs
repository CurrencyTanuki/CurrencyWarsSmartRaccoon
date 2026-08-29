using System.Collections.Generic;
using System.Linq;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 「1-3 三星五费」商店阶段命运圣杯羁绊采购策略的单测。
/// 验证核心承诺：只买命杯成员(1/2/3费)、Archer 不进自动购买、命杯成员绝不进自动卖出(Retained)。
/// </summary>
public sealed class FateGrailShoppingPolicyTests
{
    private const string Rin = "远坂凛";
    private const string Gil = "吉尔伽美什";
    private const string Saber = "Saber";
    private const string ArcherName = "Archer";

    private static CurrencyWarsCharacterData NewCharacter(string name) =>
        new("id_" + name, name, "front", new[] { 1 }, false);

    [Fact]
    public void AutoPurchase_IsOnlyTheThreeOneToThreeCostGrailMembers()
    {
        var auto = FateGrailShoppingPolicy.BuildAutoPurchaseNames();

        // 恰为 1/2/3 费命杯成员，不含 Archer(5费)
        Assert.Equal(3, auto.Count);
        Assert.Contains(Rin, auto);
        Assert.Contains(Gil, auto);
        Assert.Contains(Saber, auto);
        Assert.DoesNotContain(ArcherName, auto);
    }

    [Fact]
    public void AutoPurchase_IsCaseInsensitiveContainable()
    {
        var auto = FateGrailShoppingPolicy.BuildAutoPurchaseNames();
        Assert.Contains("saber", auto); // 大小写不敏感
    }

    [Fact]
    public void Retained_WithNothingHeld_IncludesAllFourGrailMembers()
    {
        var retained = FateGrailShoppingPolicy.BuildRetainedNames(
            Enumerable.Empty<string>());

        Assert.Contains(Rin, retained);
        Assert.Contains(Gil, retained);
        Assert.Contains(Saber, retained);
        // （N5 回归）Archer 是 5 费本体/复制目标，无条件禁卖，不依赖“是否已持有”
        Assert.Contains(ArcherName, retained);
        Assert.Equal(4, retained.Count);
    }

    [Fact]
    public void Retained_WhenArcherHeld_IncludesArcher()
    {
        var retained = FateGrailShoppingPolicy.BuildRetainedNames([ArcherName]);
        // 商店可买三人必在（无论如何都不卖）
        Assert.Contains(Saber, retained);
        // 已持有的 Archer 也是命杯成员 -> 禁卖
        Assert.Contains(ArcherName, retained);
    }

    [Fact]
    public void ApplyTo_OverridesAutoPurchase_AndAddsGrailToRetained()
    {
        var source = new RewardStageAutomationOptions
        {
            // 假设旧配置会自动买一些非命杯角色
            AutoPurchaseCharacterNames = new HashSet<string>(
                ["黑塔", "黄泉"],
                System.StringComparer.OrdinalIgnoreCase),
            RetainedCharacterNames = new HashSet<string>(
                ["黑塔"],
                System.StringComparer.OrdinalIgnoreCase),
        };

        var applied = FateGrailShoppingPolicy.ApplyTo(source);

        // 自动购买被替换为命杯成员(1/2/3费)
        Assert.Equal(3, applied.AutoPurchaseCharacterNames.Count);
        Assert.Contains(Rin, applied.AutoPurchaseCharacterNames);
        Assert.DoesNotContain(ArcherName, applied.AutoPurchaseCharacterNames);

        // 保留名单 = 原有保留 + 命杯购买成员(禁卖)
        Assert.Contains(Rin, applied.RetainedCharacterNames);
        Assert.Contains(Gil, applied.RetainedCharacterNames);
        Assert.Contains(Saber, applied.RetainedCharacterNames);
        Assert.Contains("黑塔", applied.RetainedCharacterNames);

        // 不原地修改源对象（纯派生）
        Assert.Equal(2, source.AutoPurchaseCharacterNames.Count);
    }

    [Fact]
    public void ApplyTo_UsesInitialOwnedCharacterNames_ForRetained()
    {
        var source = new RewardStageAutomationOptions
        {
            InitialOwnedCharacters = new[]
            {
                new RecognizedBenchCharacter(0, NewCharacter(Gil), 0.9d)
            }
        };

        var applied = FateGrailShoppingPolicy.ApplyTo(source);
        // 已持有吉尔伽美什 -> 应出现在保留名单(禁卖)
        Assert.Contains(Gil, applied.RetainedCharacterNames);
    }

    [Fact]
    public void BuildRetained_WithNullEnumerable_IsSafeAndReturnsAllFour()
    {
        IEnumerable<string>? nothing = null;
        var retained = FateGrailShoppingPolicy.BuildRetainedNames(nothing!);
        Assert.Contains(Rin, retained);
        Assert.Contains(Gil, retained);
        Assert.Contains(Saber, retained);
        Assert.Contains(ArcherName, retained);
    }

    [Fact]
    public void ApplyTo_PassesThroughAllOtherFields()
    {
        var sb = new System.Text.StringBuilder();
        var source = new RewardStageAutomationOptions
        {
            EnableEarlyStrongFormationPurchase = true,
            EnableGalaxyScholarRewardStrategy = true,
            FormationCharacterNames = new HashSet<string>(["A", "B"], System.StringComparer.OrdinalIgnoreCase),
            PreferredInvestmentStrategyIds = new HashSet<string>(["p1"], System.StringComparer.OrdinalIgnoreCase),
            SelectedInvestmentEnvironmentId = "env-x",
        };

        var applied = FateGrailShoppingPolicy.ApplyTo(source);

        Assert.True(applied.EnableEarlyStrongFormationPurchase);
        Assert.True(applied.EnableGalaxyScholarRewardStrategy);
        Assert.Contains("A", applied.FormationCharacterNames);
        Assert.Contains("p1", applied.PreferredInvestmentStrategyIds);
        Assert.Equal("env-x", applied.SelectedInvestmentEnvironmentId);
        Assert.Same(source.InitialFormationPlacements, applied.InitialFormationPlacements);
        Assert.Same(source.PreparationCompletionOptions, applied.PreparationCompletionOptions);
    }

    [Fact]
    public void ApplyTo_NullHeld_UsesInitialOwned_ButEmptyArray_MeansNoHeld()
    {
        // null -> 用 source.InitialOwnedCharacters（持有吉尔伽美什 -> 保留名单含他）
        var withOwned = new RewardStageAutomationOptions
        {
            InitialOwnedCharacters = new[]
            {
                new RecognizedBenchCharacter(0, NewCharacter(Gil), 0.9d)
            }
        };
        Assert.Contains(
            Gil,
            FateGrailShoppingPolicy.ApplyTo(withOwned)
                .RetainedCharacterNames);

        // 显式传空数组 -> 视为无持有 -> 但禁卖名单仍含全部 4 名命杯成员（N5：Archer 无条件禁卖）
        var retainedEmpty = FateGrailShoppingPolicy.ApplyTo(
            withOwned, alreadyHeldOrOwned: Array.Empty<string>())
            .RetainedCharacterNames;
        Assert.Contains(ArcherName, retainedEmpty);
        Assert.Contains(Saber, retainedEmpty);
    }
}
