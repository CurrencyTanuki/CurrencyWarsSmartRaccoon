using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Tests;

public sealed class PreparationBenchSalePlannerTests
{
    private readonly PreparationBenchSalePlanner planner = new();

    [Fact]
    public void MineCapacityCountsUncertainAndSpecialSlotsAsOccupied()
    {
        var slots = Enumerable.Range(0, 9)
            .Select(index => new CharacterCardSlotRecognition(
                index,
                new PixelRect(index * 10, 0, 10, 10),
                index switch
                {
                    7 => CharacterCardSlotState.Uncertain,
                    8 => CharacterCardSlotState.SpecialOccupied,
                    _ => CharacterCardSlotState.Recognized
                },
                index < 7 ? $"character_{index}" : null,
                index < 7 ? $"角色{index}" : null,
                0.8,
                0.4,
                30))
            .ToArray();

        Assert.Equal(
            9,
            PreparationBenchOccupancyPolicy.CountOccupied(slots));
    }

    [Fact]
    public void SellAllExcludesDeployedExplicitlyRetainedAndAutomaticRetention()
    {
        var deployed = Bench(0, "已上场", 3);
        var retained = Bench(1, "用户保留", 3);
        var automatic = Bench(2, "仙舟组件", 2, "仙舟");
        var sellable = Bench(3, "可出售", 1);
        var options = new PreparationBoardOptions
        {
            BenchSaleMode = PreparationBenchSaleMode.SellAll,
            RetainedCharacterNames = new HashSet<string>(
                ["用户保留"],
                StringComparer.OrdinalIgnoreCase),
            EnableEarlyStrongFormationRetention = true
        };

        var plan = planner.Plan(
            [deployed, retained, automatic, sellable],
            [new PreparationPlacement(deployed, PreparationLane.Front, 0)],
            options,
            currentGold: null);

        Assert.True(plan.ShouldSell);
        Assert.Equal("可出售", Assert.Single(plan.Candidates).Character.Name);
        Assert.Equal(1, plan.TotalSaleValue);

        var deployedRetained = Bench(0, "同名保留", 1, "仙舟");
        var duplicate = Bench(1, "同名保留", 1, "仙舟");
        var threeCost = Bench(2, "三费角色", 3);
        var oneCost = Bench(3, "一费角色", 1);
        var duplicatePlan = planner.Plan(
            [deployedRetained, duplicate, threeCost, oneCost],
            [new PreparationPlacement(
                deployedRetained,
                PreparationLane.Front,
                0)],
            new PreparationBoardOptions
            {
                BenchSaleMode = PreparationBenchSaleMode.SellAll,
                RetainedCharacterNames = new HashSet<string>(
                    ["同名保留"],
                    StringComparer.OrdinalIgnoreCase),
                EnableEarlyStrongFormationRetention = true
            },
            currentGold: null);

        Assert.Equal(
            ["三费角色", "一费角色"],
            duplicatePlan.Candidates.Select(item => item.Character.Name));
        Assert.Equal(4, duplicatePlan.TotalSaleValue);

        var uncertainPurchasedRetention = planner.Plan(
            [Bench(0, "可能是刚买角色的误识别结果", 2)],
            [],
            new PreparationBoardOptions
            {
                BenchSaleMode = PreparationBenchSaleMode.SellAll,
                RequiredRetainedCharacterNames = new HashSet<string>(
                    ["刚买的角色"],
                    StringComparer.OrdinalIgnoreCase)
            },
            currentGold: null);

        Assert.False(uncertainPurchasedRetention.ShouldSell);
        Assert.Empty(uncertainPurchasedRetention.Candidates);
        Assert.Contains("本批整体跳过出售", uncertainPurchasedRetention.Message);
    }

    [Fact]
    public void InterestModeOnlySellsWhenSaleReachesConfiguredThreshold()
    {
        (int CurrentGold, int SaleValue, int Threshold, bool Expected)[] cases =
        [
            (7, 3, 10, true),
            (6, 3, 10, false),
            (10, 3, 10, false),
            (17, 3, 20, true)
        ];
        foreach (var test in cases)
        {
            var options = new PreparationBoardOptions
            {
                BenchSaleMode = PreparationBenchSaleMode.InterestThreshold,
                InterestThreshold = test.Threshold
            };

            var plan = planner.Plan(
                [Bench(0, "候选", test.SaleValue)],
                [],
                options,
                test.CurrentGold);

            Assert.Equal(test.Expected, plan.ShouldSell);
        }
    }

    [Fact]
    public void InterestModeDoesNotSellWhenGoldIsUnknown()
    {
        var plan = planner.Plan(
            [Bench(0, "候选", 3)],
            [],
            new PreparationBoardOptions
            {
                BenchSaleMode = PreparationBenchSaleMode.InterestThreshold
            },
            currentGold: null);

        Assert.False(plan.ShouldSell);
    }

    [Fact]
    public void GoldParserRequiresOneUnambiguousValue()
    {
        (string Text, int? Expected)[] cases =
        [
            ("3", 3),
            ("金币 20", 20),
            ("0 0 03 商店", 3),
            ("0 0 020 商店", 20),
            ("0 0 03 商店 0.05 KB/s CPU 13% 显卡42%", 3),
            ("0 0 商店 5.24 KB/s CPU 37% 显卡40%", null),
            ("0 3", null),
            ("没有数字", null)
        ];
        foreach (var test in cases)
        {
            Assert.Equal(
                test.Expected,
                PreparationGoldParser.Parse(
                    new OcrTextResult(test.Text, [test.Text])));
        }
    }

    private static RecognizedBenchCharacter Bench(
        int slot,
        string name,
        int cost,
        params string[] bonds) =>
        new(
            slot,
            new CurrencyWarsCharacterData(
                $"character_{slot}",
                name,
                "前台",
                [cost],
                false,
                bonds),
            0.95);
}
