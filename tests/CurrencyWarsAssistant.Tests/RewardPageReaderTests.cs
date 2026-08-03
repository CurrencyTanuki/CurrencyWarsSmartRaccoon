using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

public sealed class RewardPageReaderTests
{
    [Fact]
    public void BatchSnapshotUsesAtMostTwoFramesAndAcceptsEmptySlots()
    {
        Assert.Equal(2, RewardShopBatchSnapshotPolicy.MaximumObservations);
        var character = Character("known", "known", "bond");
        var accumulator = new RewardShopRecognitionAccumulator(slotCount: 3);
        var frame = new[]
        {
            new RewardShopSlot(0, character, character.Name, 0.99),
            new RewardShopSlot(1, null, "", 0),
            new RewardShopSlot(2, null, "", 0)
        };

        accumulator.Observe(frame);
        accumulator.Observe(frame);

        var snapshot = accumulator.Snapshot();
        Assert.Equal(character.Id, snapshot[0].Character?.Id);
        Assert.Null(snapshot[1].Character);
        Assert.Null(snapshot[2].Character);
    }

    [Theory]
    [InlineData("reward_shop", "preparation_1_1", true)]
    [InlineData("preparation_1_1", "preparation_1_1", true)]
    [InlineData(null, "preparation_1_1", false)]
    [InlineData("preparation_1_2", "preparation_1_1", false)]
    public void BatchTransitionOnlyCompletesOnShopOrExpectedPreparation(
        string? pageId,
        string expectedPreparationPage,
        bool expected)
    {
        Assert.Equal(
            expected,
            RewardShopBatchTransitionPolicy.IsCompletedBatchPage(
                pageId,
                expectedPreparationPage));
    }

    [Fact]
    public void OneStableSnapshotPlansEachTargetSlotOnlyOnce()
    {
        var retained = Character("retained", "retained", "bond");
        var preset = Character("preset", "大丽花", "持续伤害");
        var planner = new RewardShopPurchasePlanner();
        var decisions = planner.Plan(
            [
                new RewardShopSlot(0, retained, retained.Name, 0.99),
                new RewardShopSlot(1, retained, retained.Name, 0.99),
                new RewardShopSlot(2, preset, preset.Name, 0.99)
            ],
            new RewardStageAutomationOptions
            {
                RetainedCharacterNames = new HashSet<string>(
                    [retained.Name],
                    StringComparer.OrdinalIgnoreCase),
                EnableEarlyStrongFormationPurchase = true
            },
            [],
            [],
            []);

        Assert.Equal([0, 1, 2], decisions.Select(item => item.Slot.Slot));
        Assert.Equal(3, decisions.Select(item => item.Slot.Slot).Distinct().Count());
    }

    [Fact]
    public void RealBatchCaseKeepsThirdTargetFromInitialSnapshot()
    {
        var aglaea = Character("aglaea", "阿格莱雅", "记忆");
        var huohuo = Character("huohuo", "藿藿", "仙舟");
        var jiaoqiu = Character("jiaoqiu", "椒丘", "持续伤害");
        var ignored = Character("ignored", "三月七", "存护");
        var feixiao = Character("feixiao", "飞霄", "追击");
        var decisions = new RewardShopPurchasePlanner().Plan(
            [
                new RewardShopSlot(0, aglaea, aglaea.Name, 0.99),
                new RewardShopSlot(1, ignored, ignored.Name, 0.99),
                new RewardShopSlot(2, feixiao, feixiao.Name, 0.99),
                new RewardShopSlot(3, huohuo, huohuo.Name, 0.99),
                new RewardShopSlot(4, jiaoqiu, jiaoqiu.Name, 0.99)
            ],
            new RewardStageAutomationOptions
            {
                FormationCharacterNames = new HashSet<string>(
                    ["existing-a", "existing-b", aglaea.Name],
                    StringComparer.OrdinalIgnoreCase),
                EnableEarlyStrongFormationPurchase = true
            },
            [],
            new HashSet<string>(
                ["existing-a", "existing-b"],
                StringComparer.OrdinalIgnoreCase),
            []);

        Assert.Equal(
            [aglaea.Name, huohuo.Name, jiaoqiu.Name],
            decisions.Select(item => item.Character.Name));
        Assert.Equal([0, 3, 4], decisions.Select(item => item.Slot.Slot));
    }

    [Fact]
    public void ContextualShopRequiresTwoStableNonTargetSupportingSlots()
    {
        var target = Character("target", "target", "bond");
        var left = Character("left", "left", "bond");
        var right = Character("right", "right", "bond");
        var changed = Character("changed", "changed", "bond");
        RewardShopSlot[] before =
        [
            new(0, target, "target", 0.99),
            new(1, left, "left", 0.99),
            new(2, right, "right", 0.99)
        ];

        Assert.True(RewardShopPurchaseContextPolicy.IsContextualShopFrame(
            before,
            [new(0, null, "", 0), new(1, left, "left", 0.99), new(2, right, "right", 0.99)],
            targetSlot: 0));
        Assert.False(RewardShopPurchaseContextPolicy.IsContextualShopFrame(
            before,
            [new(0, null, "", 0), new(1, left, "left", 0.99), new(2, changed, "changed", 0.99)],
            targetSlot: 0));
        Assert.False(RewardShopPurchaseContextPolicy.IsContextualShopFrame(
            before,
            [new(0, null, "", 0), new(1, left, "left", 0.99), new(2, right, "right", 0.99)],
            targetSlot: 0,
            consumedSlots: new HashSet<int> { 2 }));
    }

    [Fact]
    public void UnknownPageCanReuseStrongShopContextButNotOneSupportingSlot()
    {
        var target = Character("target", "target", "bond");
        var left = Character("left", "left", "bond");
        var right = Character("right", "right", "bond");
        RewardShopSlot[] before =
        [
            new(0, target, "target", 0.99),
            new(1, left, "left", 0.99),
            new(2, right, "right", 0.99)
        ];

        Assert.True(RewardShopPurchaseContextPolicy.CanUseObservation(
            classifiedPageId: null,
            before,
            [new(0, target, "target", 0.99), new(1, left, "left", 0.99), new(2, right, "right", 0.99)],
            targetSlot: 0));
        Assert.False(RewardShopPurchaseContextPolicy.CanUseObservation(
            classifiedPageId: null,
            before,
            [new(0, target, "target", 0.99), new(1, left, "left", 0.99), new(2, null, "", 0)],
            targetSlot: 0));
        Assert.True(RewardShopPurchaseContextPolicy.CanUseObservation(
            classifiedPageId: "reward_shop",
            before,
            [new(0, target, "target", 0.99), new(1, left, "left", 0.99), new(2, null, "", 0)],
            targetSlot: 0));
    }

    [Fact]
    public void ContextualShopTargetStillNeedsTwoAbsentFramesToConfirm()
    {
        var target = Character("target", "target", "bond");
        var tracker = new RewardShopPurchasePageTracker(
            0,
            target.Id,
            "preparation_1_1");
        RewardShopSlot[] contextual = [new(0, null, "", 0)];

        Assert.Equal(
            RewardShopPurchasePostcondition.Uncertain,
            tracker.Observe("reward_shop", contextual).Postcondition);
        Assert.Equal(
            RewardShopPurchasePostcondition.ConfirmedInShop,
            tracker.Observe("reward_shop", contextual).Postcondition);
    }

    [Fact]
    public void ProtectedUncertainPurchaseContinuesIntoRecoveryPath()
    {
        var uncertain = new RewardShopPurchaseVerification(
            RewardShopPurchasePostcondition.Uncertain,
            null);

        Assert.True(RewardShopPurchaseSafetyPolicy
            .ContinueRewardStageAfterProtectedUncertainPurchase(uncertain));
    }

    [Fact]
    public void PurchasedEmptySlotDoesNotBlockStableShopButOtherUnknownDoes()
    {
        var character = Character("known", "known", "bond");
        var consumed = new RewardShopRecognitionAccumulator(
            slotCount: 2,
            ignoredSlots: new HashSet<int> { 0 });
        var ordinary = new RewardShopRecognitionAccumulator(slotCount: 2);
        var observation = new[]
        {
            new RewardShopSlot(0, null, "", 0),
            new RewardShopSlot(1, character, "known", 0.99)
        };

        consumed.Observe(observation);
        consumed.Observe(observation);
        ordinary.Observe(observation);
        ordinary.Observe(observation);

        Assert.True(consumed.IsComplete);
        Assert.False(ordinary.IsComplete);
    }

    [Fact]
    public void PurchaseVerificationRequiresTwoConsistentPostconditionFrames()
    {
        var character = Character("target", "目标角色", "群攻");
        var accumulator = new RewardShopPurchaseVerificationAccumulator(
            slotIndex: 2,
            expectedCharacterId: character.Id);

        Assert.Equal(
            RewardShopPurchaseVerificationStatus.Pending,
            accumulator.Observe(
                [new RewardShopSlot(2, character, "目标角色", 0.99)]));
        Assert.Equal(
            RewardShopPurchaseVerificationStatus.NotPurchased,
            accumulator.Observe(
                [new RewardShopSlot(2, character, "目标角色", 0.99)]));

        var confirmed = new RewardShopPurchaseVerificationAccumulator(
            slotIndex: 2,
            expectedCharacterId: character.Id);
        Assert.Equal(
            RewardShopPurchaseVerificationStatus.Pending,
            confirmed.Observe(
                [new RewardShopSlot(2, null, "", 0)]));
        Assert.Equal(
            RewardShopPurchaseVerificationStatus.Confirmed,
            confirmed.Observe(
                [new RewardShopSlot(2, null, "", 0)]));
    }

    [Theory]
    [InlineData("preparation_1_1")]
    [InlineData("preparation_1_2")]
    public void PurchasePageTrackerAcceptsShopRemainingAndAutomaticClosePaths(
        string preparationPage)
    {
        var character = Character("target", "目标角色", "持续伤害");
        var inShop = new RewardShopPurchasePageTracker(
            2,
            character.Id,
            preparationPage);
        Assert.False(inShop.Observe(
            "reward_shop",
            [new RewardShopSlot(2, null, "", 0)]).Confirmed);
        var shopConfirmed = inShop.Observe(
            "reward_shop",
            [new RewardShopSlot(2, null, "", 0)]);

        var autoClose = new RewardShopPurchasePageTracker(
            2,
            character.Id,
            preparationPage);
        Assert.False(autoClose.Observe(preparationPage).Confirmed);
        var autoCloseConfirmed = autoClose.Observe(preparationPage);

        Assert.Equal(
            RewardShopPurchasePostcondition.ConfirmedInShop,
            shopConfirmed.Postcondition);
        Assert.False(shopConfirmed.RequiresFreshShopSnapshot);
        Assert.Equal(
            RewardShopPurchasePostcondition.ConfirmedAfterAutomaticClose,
            autoCloseConfirmed.Postcondition);
        Assert.True(autoCloseConfirmed.RequiresFreshShopSnapshot);
    }

    [Fact]
    public void PurchaseTrackerWaitsThroughSevenSecondUnknownAnimationBeforeTwoFrameClose()
    {
        var character = Character("target", "目标角色", "持续伤害");
        var tracker = new RewardShopPurchasePageTracker(
            2,
            character.Id,
            "preparation_1_1");

        for (var index = 0; index < 28; index++)
        {
            Assert.False(tracker.Observe(null).Confirmed);
        }

        Assert.False(tracker.Observe("preparation_1_1").Confirmed);
        Assert.True(tracker.Observe("preparation_1_1").Confirmed);
        Assert.InRange(
            RewardShopPurchaseTiming.VerificationTimeout,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void UnresolvedPurchasePostconditionRequiresProtectedRecovery()
    {
        var unresolved = new RewardShopPurchaseVerification(
            RewardShopPurchasePostcondition.Uncertain,
            null);
        var confirmed = new RewardShopPurchaseVerification(
            RewardShopPurchasePostcondition.ConfirmedAfterAutomaticClose,
            "preparation_1_1");

        Assert.True(RewardShopPurchaseSafetyPolicy
            .RequiresProtectedRecoveryAfterTimeout(unresolved));
        Assert.False(RewardShopPurchaseSafetyPolicy
            .RequiresProtectedRecoveryAfterTimeout(confirmed));
    }

    [Theory]
    [InlineData("初始已拥有")]
    [InlineData("第一商店已确认购买")]
    [InlineData("第一商店可信点击但结果不确定")]
    public void EarlyStrongPresetNeverPlansSameOwnedOrCrediblyAttemptedName(
        string source)
    {
        var asta = Character("asta", "艾丝妲", "持续伤害");
        var decisions = new RewardShopPurchasePlanner().Plan(
            [new RewardShopSlot(0, asta, asta.Name, 0.99)],
            new RewardStageAutomationOptions
            {
                EnableEarlyStrongFormationPurchase = true,
                FormationCharacterNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
            },
            [asta.Name],
            [],
            source == "初始已拥有" ? [asta] : []);

        Assert.Empty(decisions);
    }

    [Theory]
    [InlineData("艾丝妲", "持续伤害")]
    [InlineData("爻光", "仙舟")]
    public void MineAcquiredPresetCharacterIsSkippedInFollowingShop(
        string name,
        string bond)
    {
        var character = Character(name, name, bond);
        var suppressed = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var owned = new Dictionary<string, CurrencyWarsCharacterData>(
            StringComparer.OrdinalIgnoreCase);

        var added = RewardMineOwnershipPolicy.Synchronize(
            [new RecognizedBenchCharacter(0, character, 0.99)],
            enableEarlyStrongPreset: true,
            suppressed,
            owned);
        var decisions = new RewardShopPurchasePlanner().Plan(
            [new RewardShopSlot(0, character, name, 0.99)],
            new RewardStageAutomationOptions
            {
                EnableEarlyStrongFormationPurchase = true,
                FormationCharacterNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
            },
            suppressed,
            [],
            owned.Values);

        Assert.Equal([name], added);
        Assert.Contains(name, suppressed);
        Assert.Empty(decisions);
    }

    [Fact]
    public void ShopPurchasePlannerUsesOwnedBenchScholarAndSkipsDuplicate()
    {
        var asta = Character("asta", "艾丝妲", "银河学者", "持续伤害");
        var herta = Character("herta", "黑塔", "银河学者", "群攻");
        var decisions = new RewardShopPurchasePlanner().Plan(
        [
            new RewardShopSlot(0, asta, "艾丝妲", 0.99),
            new RewardShopSlot(1, herta, "黑塔", 0.99)
        ],
        new RewardStageAutomationOptions
        {
            EnableEarlyStrongFormationPurchase = true,
            EnableGalaxyScholarRewardStrategy = true,
            FormationCharacterNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
        },
        ["艾丝妲"],
        ["艾丝妲"],
        [asta]);

        var decision = Assert.Single(decisions);
        Assert.Equal("黑塔", decision.Character.Name);
        Assert.True(decision.IsGalaxyScholarPairCandidate);
        Assert.Contains("2名不同银河学者", decision.Reason);
    }

    [Fact]
    public void ShopPurchasePlannerTakesOnlyTwoDifferentScholars()
    {
        var asta = Character("asta", "艾丝妲", "银河学者");
        var herta = Character("herta", "黑塔", "银河学者");
        var ruanMei = Character("ruan_mei", "阮•梅", "银河学者");
        var decisions = new RewardShopPurchasePlanner().Plan(
        [
            new RewardShopSlot(0, asta, "艾丝妲", 0.99),
            new RewardShopSlot(1, asta, "艾丝妲", 0.99),
            new RewardShopSlot(2, herta, "黑塔", 0.99),
            new RewardShopSlot(3, ruanMei, "阮•梅", 0.99)
        ],
        new RewardStageAutomationOptions
        {
            EnableEarlyStrongFormationPurchase = true,
            EnableGalaxyScholarRewardStrategy = true,
            FormationCharacterNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
        },
        [],
        [],
        []);

        Assert.Equal(["艾丝妲", "黑塔"], decisions
            .Where(item => item.IsGalaxyScholarPairCandidate)
            .Select(item => item.Character.Name));
    }

    [Fact]
    public void ShopPurchasePlannerDoesNotBuyLoneScholarWithoutASecondDistinctScholar()
    {
        var herta = Character(
            "herta",
            "Herta",
            GalaxyScholarPairPolicy.BondName);
        var unrelatedOwnedCharacter = Character("mydei", "Mydei", "Warrior");
        var decisions = new RewardShopPurchasePlanner().Plan(
        [
            new RewardShopSlot(0, herta, "Herta", 0.99)
        ],
        new RewardStageAutomationOptions
        {
            EnableEarlyStrongFormationPurchase = true,
            EnableGalaxyScholarRewardStrategy = true,
            FormationCharacterNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
        },
        [],
        [],
        [unrelatedOwnedCharacter]);

        Assert.Empty(decisions);
    }

    [Fact]
    public void ScholarPurchaseRequiresDedicatedSwitchAndFirstShopWindow()
    {
        var herta = Character("herta", "黑塔", "银河学者");
        var slots = new[]
        {
            new RewardShopSlot(0, herta, "黑塔", 0.99)
        };
        var planner = new RewardShopPurchasePlanner();
        var disabled = planner.Plan(
            slots,
            new RewardStageAutomationOptions
            {
                EnableEarlyStrongFormationPurchase = true,
                EnableGalaxyScholarRewardStrategy = false,
                FormationCharacterNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
            },
            [],
            [],
            []);
        var secondShop = planner.Plan(
            slots,
            new RewardStageAutomationOptions
            {
                EnableGalaxyScholarRewardStrategy = true,
                FormationCharacterNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
            },
            [],
            [],
            [],
            allowGalaxyScholarPairPurchase: false);

        Assert.Empty(disabled);
        Assert.Empty(secondShop);
    }

    [Fact]
    public void ShopPurchasePlannerSkipsDeployedDuplicateAndChoosesDifferentFormationName()
    {
        static CurrencyWarsCharacterData Character(string id, string name) =>
            new(id, name, "前台", [3], false);
        var planner = new RewardShopPurchasePlanner();
        var decisions = planner.Plan(
        [
            new RewardShopSlot(0, Character("a1", "阿格莱雅"), "阿格莱雅", 0.99),
            new RewardShopSlot(1, Character("d1", "大丽花"), "大丽花", 0.99),
            new RewardShopSlot(2, Character("d2", "大丽花"), "大丽花", 0.99),
            new RewardShopSlot(3, Character("k1", "卡芙卡"), "卡芙卡", 0.99)
        ],
        new RewardStageAutomationOptions
        {
            FormationCharacterNames = new HashSet<string>(
                ["阿格莱雅", "乱破", "大丽花", "卡芙卡"],
                StringComparer.OrdinalIgnoreCase),
            // 场上已部署 2 个名单角色（阿格莱雅、乱破）：仍可补 1 个名单角色。
            InitialFormationPlacements =
            [
                new PreparationPlacement(
                    new RecognizedBenchCharacter(0, Character("a1", "阿格莱雅"), 0.99),
                    PreparationLane.Front,
                    0),
                new PreparationPlacement(
                    new RecognizedBenchCharacter(1, Character("l1", "乱破"), 0.99),
                    PreparationLane.Front,
                    1)
            ]
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(
            ["阿格莱雅", "乱破"],
            StringComparer.OrdinalIgnoreCase));

        var decision = Assert.Single(decisions);
        Assert.Equal("大丽花", decision.Character.Name);
        Assert.True(decision.IsFormationCandidate);
        Assert.Contains("补足奖励关不同角色阵容", decision.Reason);
    }

    [Fact]
    public void RetainedCharacterPurchasesEveryShopCopyEvenWhenAlreadyOwned()
    {
        var retained = Character("retained", "用户保留角色");
        var decisions = new RewardShopPurchasePlanner().Plan(
        [
            new RewardShopSlot(0, retained, retained.Name, 0.99),
            new RewardShopSlot(1, retained, retained.Name, 0.99),
            new RewardShopSlot(2, retained, retained.Name, 0.99)
        ],
        new RewardStageAutomationOptions
        {
            RetainedCharacterNames = new HashSet<string>(
                [retained.Name],
                StringComparer.OrdinalIgnoreCase),
            FormationCharacterNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
        },
        [retained.Name],
        [],
        [retained]);

        Assert.Equal(3, decisions.Count);
        Assert.All(decisions, decision =>
        {
            Assert.Equal(retained.Name, decision.Character.Name);
            Assert.Contains("商店所有副本全买", decision.Reason);
        });
    }

    private static CurrencyWarsCharacterData Character(
        string id,
        string name,
        params string[] bonds) =>
        new(id, name, "前台", [1], false, bonds);

    [Fact]
    public async Task ReadsFiveCharacterNamesFromShopReplay()
    {
        var ocr = new WindowsOfflineOcr();
        if (!ocr.IsAvailable)
        {
            return;
        }

        var data = LoadData();
        var reader = new RewardShopReader(ocr, data);

        var result = await reader.ReadAsync(
            LoadFrame("shop_open_1_1.jpg"),
            CancellationToken.None);

        Assert.Equal(
            new[] { "大丽花", "赛飞儿", "爻光", "椒丘", "爻光" },
            result.Select(item => item.Character?.Name).ToArray());
    }

    [Fact]
    public async Task ReadsThreeInvestmentStrategiesFromReplay()
    {
        var ocr = new WindowsOfflineOcr();
        if (!ocr.IsAvailable)
        {
            return;
        }

        var data = LoadData();
        var reader = new InvestmentStrategyPageReader(ocr, data);

        var result = await reader.ReadAsync(
            LoadFrame("investment_strategy.jpg"),
            CancellationToken.None);

        Assert.Equal(
            new[] { "空仓", "狸职手续", "装备党" },
            result.Select(item => item.Strategy?.Name).ToArray());
    }

    [Fact]
    public async Task ReadsLongGoldInvestmentStrategyNamesFromLiveReplay()
    {
        var ocr = new WindowsOfflineOcr();
        if (!ocr.IsAvailable)
        {
            return;
        }

        var reader = new InvestmentStrategyPageReader(ocr, LoadData());
        var result = await reader.ReadAsync(
            LoadFrame("investment_strategy_long_gold_2048x1152.png"),
            CancellationToken.None);

        Assert.Equal(
            new[] { "爆晶矿•金", "红钻闪耀", "公司军火更新•金" },
            result.Select(item => item.Strategy?.Name).ToArray());
    }

    [Fact]
    public async Task ReadsShortInvestmentStrategyNamesFromLiveReplay()
    {
        var ocr = new WindowsOfflineOcr();
        if (!ocr.IsAvailable)
        {
            return;
        }

        var reader = new InvestmentStrategyPageReader(ocr, LoadData());
        var result = await reader.ReadAsync(
            LoadFrame("investment_strategy_lottery_2048x1152.png"),
            CancellationToken.None);

        Assert.Equal(
            new[] { "溜佩佩", "买彩票", "尾款交付" },
            result.Select(item => item.Strategy?.Name).ToArray());
    }

    [Fact]
    public void RewardTextVariantsCorrectCommonGoldRarityGlyph()
    {
        var variants = RewardOcrTextVariants.Expand(
            new OcrTextResult(
                "好 运 令 牌 · 釒",
                ["好 运 令 牌 · 釒"]));

        Assert.Contains(
            variants,
            item => GameDataNameMatcher.Normalize(item) ==
                    GameDataNameMatcher.Normalize("好运令牌•金"));
    }

    [Fact]
    public async Task ShopReaderUsesTighterNameFallbackForUnknownPrimaryRegion()
    {
        var ocr = new StubOcr(
            "大丽花",
            "赛飞儿",
            "爻光",
            "乱码",
            "椒丘",
            "爻光");
        var reader = new RewardShopReader(
            ocr,
            LoadData());

        var result = await reader.ReadAsync(
            new CaptureFrame(
                1920,
                1080,
                1920 * 4,
                new byte[1920 * 1080 * 4],
                new PixelRect(0, 0, 1920, 1080),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(
            new[] { "大丽花", "赛飞儿", "爻光", "椒丘", "爻光" },
            result.Select(item => item.Character?.Name).ToArray());
        Assert.Equal(new PixelRect(1155, 288, 180, 37), ocr.Regions[4]);
    }

    [Fact]
    public async Task ShopReaderRecoversRepeatedComplexNameFromLiveOcrShapeAndBond()
    {
        var reader = new RewardShopReader(
            new StubOcr(
                "远坂凛",
                "． 1 出 忄 隹 隹",
                "隹 隹",
                "盐 盐",
                "口 了 厶 匕 旦 + 《 月 匕 里 仙舟",
                "乱破",
                "翡翠",
                "乱破"),
            LoadData());

        var result = await reader.ReadAsync(
            new CaptureFrame(
                1920,
                1080,
                1920 * 4,
                new byte[1920 * 1080 * 4],
                new PixelRect(0, 0, 1920, 1080),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(
            new[] { "远坂凛", "藿藿", "乱破", "翡翠", "乱破" },
            result.Select(item => item.Character?.Name).ToArray());
    }

    [Fact]
    public async Task ShopReaderUsesIsolatedNameStripBeforeBondFallback()
    {
        var ocr = new StubOcr(
            "远坂凛",
            "． 1 出 忄 隹 隹",
            "藿藿",
            "乱破",
            "翡翠",
            "乱破");
        var reader = new RewardShopReader(ocr, LoadData());

        var result = await reader.ReadAsync(
            new CaptureFrame(
                1920,
                1080,
                1920 * 4,
                new byte[1920 * 1080 * 4],
                new PixelRect(0, 0, 1920, 1080),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal("藿藿", result[1].Character?.Name);
        Assert.Equal(6, ocr.Regions.Count);
        Assert.Equal(new PixelRect(625, 288, 180, 37), ocr.Regions[2]);
    }

    [Fact]
    public async Task ShopReaderDoesNotGuessRepeatedNameWithoutSupportingBond()
    {
        var reader = new RewardShopReader(
            new StubOcr(
                "远坂凛",
                "． 1 出 忄 隹 隹",
                "隹 隹",
                "盐 盐",
                "巡海游侠",
                "乱破",
                "翡翠",
                "乱破"),
            LoadData());

        var result = await reader.ReadAsync(
            new CaptureFrame(
                1920,
                1080,
                1920 * 4,
                new byte[1920 * 1080 * 4],
                new PixelRect(0, 0, 1920, 1080),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Null(result[1].Character);
    }

    [Fact]
    public void ShopAccumulatorDoesNotTreatRepeatedUnknownAsStable()
    {
        var data = LoadData().CurrencyWarsCharacters.Take(5).ToArray();
        var accumulator = new RewardShopRecognitionAccumulator();
        var partial = Enumerable.Range(0, 5)
            .Select(index => new RewardShopSlot(
                index,
                index == 3 ? null : data[index],
                index == 3 ? "unknown" : data[index].Name,
                index == 3 ? 0 : 0.95))
            .ToArray();

        accumulator.Observe(partial);
        accumulator.Observe(partial);

        Assert.False(accumulator.IsComplete);
        Assert.Null(accumulator.Snapshot()[3].Character);
    }

    private static GameDataCatalog LoadData() =>
        GameDataCatalogLoader.Load(
            Path.Combine(RepositoryRoot, "data", "4.4"));

    private static CaptureFrame LoadFrame(string file)
    {
        var path = Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file);
        return LoadAbsoluteFrame(path);
    }

    private static CaptureFrame LoadAbsoluteFrame(string path)
    {
        using var bgr = Cv2.ImRead(path, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Cols,
            bgra.Rows,
            checked(bgra.Cols * 4),
            pixels,
            new PixelRect(0, 0, bgra.Cols, bgra.Rows),
            DateTimeOffset.UtcNow);
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));

    private sealed class StubOcr(params string[] texts) : IOfflineOcr
    {
        private readonly Queue<string> _texts = new(texts);

        public bool IsAvailable => true;
        public List<PixelRect> Regions { get; } = [];

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Regions.Add(region);
            var text = _texts.Dequeue();
            return ValueTask.FromResult(new OcrTextResult(text, [text]));
        }
    }
}
