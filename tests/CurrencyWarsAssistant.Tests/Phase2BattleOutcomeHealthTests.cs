using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2BattleOutcomeHealthTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ConfirmedHealthLoss_CorrectsSmallTerminalActionToZero(
        int rawTerminalAction)
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), 84);
        ObserveTwice(tracker, Battle("1-1", rawTerminalAction), null);

        tracker.Observe(Preparation("1-2"), Health(80));
        var update = tracker.Observe(Preparation("1-2"), Health(80));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(0, final.RemainingActionValue?.TotalActionValue);
        Assert.Equal(84, final.PreBattleHealth);
        Assert.Equal(80, final.PostBattleHealth);
        Assert.Equal(-4, final.HealthDelta);
        Assert.Equal(NodeClearStatus.NotPerfect, final.ClearStatus);
        Assert.Contains(rawTerminalAction.ToString(), update.Diagnostic);
    }

    [Fact]
    public void UnchangedHealthAlone_DoesNotClaimPerfectClear()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), 84);
        ObserveTwice(tracker, BattleContext("1-1", 3), null);

        tracker.Observe(Preparation("1-2"), Health(84));
        var update = tracker.Observe(Preparation("1-2"), Health(84));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(0, final.HealthDelta);
        Assert.Equal(NodeClearStatus.Unknown, final.ClearStatus);
    }

    [Fact]
    public void MissingHealth_WithMoreThanTenActions_MarksPerfect()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), null);
        ObserveTwice(tracker, Battle("1-1", 55), null);

        tracker.Observe(Preparation("1-2"));
        var update = tracker.Observe(Preparation("1-2"));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Null(final.HealthDelta);
        Assert.Equal(55, final.RemainingActionValue?.TotalActionValue);
        Assert.Equal(NodeClearStatus.Perfect, final.ClearStatus);
    }

    [Fact]
    public void MissingHealth_WithTenActions_RemainsUnknown()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), null);
        ObserveTwice(tracker, Battle("1-1", 10), null);

        tracker.Observe(Preparation("1-2"));
        var update = tracker.Observe(Preparation("1-2"));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Null(final.HealthDelta);
        Assert.Equal(10, final.RemainingActionValue?.TotalActionValue);
        Assert.Equal(NodeClearStatus.Unknown, final.ClearStatus);
    }

    [Fact]
    public void ZeroActionWithoutHealthMarksNormalNodeFailedAndCapsTheoryAtDamage()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("2-1"), null);
        ObserveTwice(tracker, Battle("2-1", 0), null);

        tracker.Observe(Preparation("2-2"));
        var update = tracker.Observe(Preparation("2-2"));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(NodeClearStatus.NotPerfect, final.ClearStatus);
        Assert.Equal(final.TotalDamage, final.TheoreticalDamageLimit);
        Assert.Equal(TheoreticalDamageQuality.ActionExhausted, final.TheoreticalDamageQuality);
    }

    [Fact]
    public void HealthRecoveryByTwo_MarksPerfectWithoutActionEvidence()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), 80);
        ObserveTwice(tracker, Battle("1-1", 0), null);

        tracker.Observe(Preparation("1-2"), Health(82));
        var update = tracker.Observe(Preparation("1-2"), Health(82));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(2, final.HealthDelta);
        Assert.Equal(NodeClearStatus.Perfect, final.ClearStatus);
    }

    [Fact]
    public void FullPostBattleHealth_MarksPerfect()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), 99);
        ObserveTwice(tracker, Battle("1-1", 0), null);

        tracker.Observe(Preparation("1-2"), Health(100));
        var update = tracker.Observe(Preparation("1-2"), Health(100));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(NodeClearStatus.Perfect, final.ClearStatus);
    }

    [Fact]
    public void BattleFrameWithUnknownNodeDoesNotConfirmBattleStart()
    {
        // 回归："竞争对手生成中"过场被模板误匹配为战斗页时，节点未知的
        // 战斗帧不得确认"战斗开始"，避免假战斗状态吞掉后续备战关键帧。
        var tracker = new Phase2OperationalStateTracker();
        var unknownBattle = Battle("1-1", 0) with
        {
            NodeId = Observation<string>.Unknown("not visible")
        };

        var first = tracker.Observe(unknownBattle);
        var second = tracker.Observe(unknownBattle);

        Assert.DoesNotContain("战斗开始", first.Message);
        Assert.DoesNotContain("战斗开始", second.Message);
    }

    [Fact]
    public void PreparationBackToNodeOneOneConfirmsNewRunBoundaryImmediately()
    {
        // 回归：玩家重开（挑战失败 C 级后回到 1-1 备战）应立即分段，
        // 不等 1-1 第一战结算，旧局数据与新局分离。
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), 90);
        ObserveTwice(tracker, Battle("1-1", 0), null);
        tracker.Observe(Preparation("1-2"), Health(90));
        tracker.Observe(Preparation("1-2"), Health(90));
        ObserveTwice(tracker, Battle("1-2", 0), null);
        tracker.Observe(Preparation("1-3"), Health(90));
        tracker.Observe(Preparation("1-3"), Health(90));

        var first = tracker.Observe(Preparation("1-1"), Health(90));
        var second = tracker.Observe(Preparation("1-1"), Health(90));

        Assert.True(second.NewRunBoundaryConfirmed);
    }

    [Fact]
    public void RewardBattle_IsPerfectAndIgnoresNonexistentSettlementGoldRow()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-2"), 82);
        var rewardBattle = Battle("1-2", 0) with
        {
            PageId = "reward_battle"
        };
        ObserveTwice(tracker, rewardBattle, null);

        var settlement = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.BattleSettlement,
            NodeId = Observation<string>.Known("1-2", 0.95),
            SettlementGoldReward = Observation<int>.Known(841, 0.7)
        };
        tracker.Observe(settlement);
        tracker.Observe(settlement);
        var firstExit = tracker.Observe(Preparation("1-3"), Health(84));
        var secondExit = tracker.Observe(Preparation("1-3"), Health(84));
        var update = firstExit.FinalizedBattle is not null ? firstExit : secondExit;

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.True(final.IsRewardNode);
        Assert.Null(final.GoldReward);
        Assert.Equal(NodeClearStatus.Perfect, final.ClearStatus);
    }

    [Fact]
    public void NormalBattle_IgnoresUnanchoredImpossibleSettlementGold()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-1"), 82);
        ObserveTwice(tracker, Battle("1-1", 0), null);

        var settlement = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.BattleSettlement,
            NodeId = Observation<string>.Known("1-1", 0.95),
            SettlementGoldReward = Observation<int>.Known(841, 0.7)
        };
        tracker.Observe(settlement);
        tracker.Observe(settlement);
        var firstExit = tracker.Observe(Preparation("1-2"), Health(84));
        var secondExit = tracker.Observe(Preparation("1-2"), Health(84));
        var update = firstExit.FinalizedBattle is not null ? firstExit : secondExit;

        Assert.Null(
            Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle)
                .GoldReward);
    }

    [Fact]
    public void RewardBattle_AcceptsSemanticallyAnchoredChallengeSuccessGold()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-2"), 82);
        ObserveTwice(tracker, Battle("1-2", 0) with { PageId = "reward_battle" }, null);
        var evidence = new EvidenceReference(
            "fixture:challenge-success",
            "ocr:settlement-gold-reward-labeled-row",
            "获得金币总览 6",
            DateTimeOffset.UtcNow,
            0.7);
        var settlement = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.BattleSettlement,
            PageId = "challenge_success",
            NodeId = Observation<string>.Known("1-2", 0.95),
            SettlementGoldReward = Observation<int>.Known(6, 0.7, [evidence])
        };
        tracker.Observe(settlement);
        var firstExit = tracker.Observe(Preparation("1-3"), Health(84));
        var secondExit = tracker.Observe(Preparation("1-3"), Health(84));
        var update = firstExit.FinalizedBattle is not null ? firstExit : secondExit;

        Assert.Equal(6, Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle).GoldReward);
    }

    [Fact]
    public void OneConflictingBreakdownLikeGoldFrameCannotOverwriteOverviewTotal()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-4"), 80);
        ObserveTwice(tracker, Battle("1-4", 0), null);
        tracker.Observe(SettlementWithGold("1-4", 8, "overview-frame"));
        tracker.Observe(SettlementWithGold("1-4", 2, "conflicting-frame"));
        var firstExit = tracker.Observe(Preparation("1-5"), Health(80));
        var secondExit = tracker.Observe(Preparation("1-5"), Health(80));
        var update = firstExit.FinalizedBattle is not null ? firstExit : secondExit;

        Assert.Equal(
            8,
            Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle).GoldReward);
    }

    [Fact]
    public void SingleYellowPageHealthOutranksLaterPreparationFallback()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-2"), 82);
        ObserveTwice(tracker, Battle("1-2", 0), null);
        var settlement = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.BattleSettlement,
            PageId = "challenge_success",
            NodeId = Observation<string>.Known("1-2", 0.95)
        };
        tracker.Observe(settlement, Health(80));
        var firstExit = tracker.Observe(Preparation("1-3"), Health(40));
        var secondExit = tracker.Observe(Preparation("1-3"), Health(40));
        var update = firstExit.FinalizedBattle is not null ? firstExit : secondExit;

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(80, final.PostBattleHealth);
        Assert.Equal(-2, final.HealthDelta);
    }

    [Fact]
    public void LaterNodeTotalDoesNotEraseEarlierReadableCharacterRows()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-3"), 80);
        tracker.Observe(Battle("1-3", 40));
        tracker.Observe(BattleContext("1-3", 35) with
        {
            BattleScreenDamageCandidate = Observation<long>.Known(200_000, 0.9)
        });
        tracker.Observe(Preparation("1-4"), Health(80));
        var update = tracker.Observe(Preparation("1-4"), Health(80));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(200_000, final.TotalDamage);
        Assert.Equal(123_000, Assert.Single(final.CharacterDamage).Damage);
    }

    [Fact]
    public void TwoMatchingPartialBattleTotalsBecomeUsableWithoutOverwritingRows()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-3"), 80);
        var first = Battle("1-3", 40) with
        {
            BattleScreenDamageCandidate = PartialTotal(
                5_060_000,
                DateTimeOffset.Parse("2026-07-30T20:00:01+08:00"))
        };
        var second = first with
        {
            BattleScreenDamageCandidate = PartialTotal(
                5_060_000,
                DateTimeOffset.Parse("2026-07-30T20:00:02+08:00"))
        };
        tracker.Observe(first);
        tracker.Observe(second);
        tracker.Observe(Preparation("1-4"), Health(80));
        var update = tracker.Observe(Preparation("1-4"), Health(80));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal(5_060_000, final.TotalDamage);
        Assert.Equal(123_000, Assert.Single(final.CharacterDamage).Damage);
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<string>>(
            final.Uncertainty), item =>
            item.Contains("两帧", StringComparison.Ordinal));
    }

    [Fact]
    public void NewRunBoundaryConfirmsAtFirstOneOnePreparation()
    {
        // 玩家重开：节点回到 1-1 备战（连续确认）即分段，不等第一战结算。
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Battle("1-4", 20), null);
        tracker.Observe(Preparation("1-5"));
        tracker.Observe(Preparation("1-5"));

        var first = tracker.Observe(Preparation("1-1"));
        var second = tracker.Observe(Preparation("1-1"));

        Assert.False(first.NewRunBoundaryConfirmed);
        Assert.True(second.NewRunBoundaryConfirmed);
    }

    [Fact]
    public void SingleFalseOneOneObservationDoesNotConfirmNewRun()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Battle("1-4", 20), null);
        tracker.Observe(Preparation("1-5"));
        tracker.Observe(Preparation("1-5"));

        var falseReading = tracker.Observe(Preparation("1-1"));
        var recovered = tracker.Observe(Preparation("1-5"));

        Assert.False(falseReading.NewRunBoundaryConfirmed);
        Assert.False(recovered.NewRunBoundaryConfirmed);
        Assert.Equal("1-4", tracker.LastFinalizedNode);
    }

    [Fact]
    public void OneYellowFrameAndFirstOneNinePreparationFinalizeOneEight()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-7"), 84);
        ObserveTwice(tracker, Battle("1-7", 0), null);
        tracker.Observe(Preparation("1-8"), Health(80));
        tracker.Observe(Preparation("1-8"), Health(80));
        Assert.Equal("1-7", tracker.LastFinalizedNode);

        // Simulate the delayed node label seen in the user's failure: all
        // pending evidence says 1-9 even though this is the 1-8 battle.
        ObserveTwice(tracker, Battle("1-9", 0), null);
        tracker.Observe(SettlementWithGold("1-9", 74, "single-yellow"), Health(76));
        var update = tracker.Observe(Preparation("1-9"), Health(76));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal("1-8", final.NodeId);
        Assert.Equal(80, final.PreBattleHealth);
        Assert.Equal(76, final.PostBattleHealth);
        Assert.Equal(-4, final.HealthDelta);
        Assert.Equal(74, final.GoldReward);
    }

    [Fact]
    public void FailedTwoOneMisreadAsTwoTwoKeepsDamageTheoryAndReward()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("1-9"), 80);
        ObserveTwice(tracker, Battle("1-9", 1), null);
        tracker.Observe(Preparation("2-1"), Health(80));
        tracker.Observe(Preparation("2-1"), Health(80));
        Assert.Equal("1-9", tracker.LastFinalizedNode);

        var delayedBattle = Battle("2-2", 0) with
        {
            BattleScreenDamageCandidate = Observation<long>.Known(
                1_104_000_000,
                0.95)
        };
        ObserveTwice(tracker, delayedBattle, null);
        tracker.Observe(SettlementWithGold("2-2", 8, "single-yellow-2-1"), Health(41));
        var update = tracker.Observe(Preparation("2-2"), Health(41));

        var final = Assert.IsType<FinalNodeBattleState>(update.FinalizedBattle);
        Assert.Equal("2-1", final.NodeId);
        Assert.Equal(1_104_000_000, final.TotalDamage);
        Assert.Equal(1_104_000_000, final.TheoreticalDamageLimit);
        Assert.Equal(NodeClearStatus.NotPerfect, final.ClearStatus);
        Assert.Equal(0, final.RemainingActionValue?.TotalActionValue);
        Assert.Equal(8, final.GoldReward);
        Assert.Equal(-39, final.HealthDelta);
    }

    [Fact]
    public void ChallengeFailedPageFinalizesLastNodeWithoutInventingHealthDelta()
    {
        var tracker = new Phase2OperationalStateTracker();
        ObserveTwice(tracker, Preparation("2-5"), 14);
        ObserveTwice(tracker, Battle("2-5", 0), null);

        var final = Assert.IsType<FinalNodeBattleState>(
            tracker.CompleteFailedRun());

        Assert.Equal("2-5", final.NodeId);
        Assert.Equal(123_000, final.TotalDamage);
        Assert.Equal(123_000, final.TheoreticalDamageLimit);
        Assert.Equal(0, final.RemainingActionValue?.TotalActionValue);
        Assert.Equal(NodeClearStatus.NotPerfect, final.ClearStatus);
        Assert.True(final.HealthDepleted);
        Assert.Null(final.PostBattleHealth);
        Assert.Null(final.HealthDelta);
        Assert.Contains(final.FinalUncertainty, item =>
            item.Contains("exact health delta", StringComparison.OrdinalIgnoreCase));
    }

    private static void ObserveTwice(
        Phase2OperationalStateTracker tracker,
        Phase2OperationalState state,
        int? health)
    {
        var observation = health.HasValue ? Health(health.Value) : null;
        tracker.Observe(state, observation);
        tracker.Observe(state, observation);
    }

    private static Observation<int> Health(int value) =>
        Observation<int>.Known(value, 0.95);

    private static Observation<long> PartialTotal(
        long value,
        DateTimeOffset observedAt) => new()
    {
        Status = ObservationStatus.Unknown,
        Value = value,
        Confidence = 0,
        Evidence =
        [
            new EvidenceReference(
                $"fixture:{observedAt:O}",
                "partial:battle-damage-total-candidate",
                value.ToString(),
                observedAt)
        ],
        Uncertainty = ["当前帧可能仍有伤害行被遮挡。"],
        ObservedAt = observedAt
    };

    private static Phase2OperationalState SettlementWithGold(
        string node,
        int gold,
        string source) => new()
    {
        PageFamily = Phase2PageFamily.BattleSettlement,
        PageId = "challenge_success",
        NodeId = Observation<string>.Known(node, 0.95),
        SettlementGoldReward = Observation<int>.Known(
            gold,
            0.70,
            [
                new EvidenceReference(
                    source,
                    "ocr:settlement-gold-reward-labeled-row",
                    $"获得金币总览 {gold}",
                    DateTimeOffset.UtcNow,
                    0.70)
            ])
    };

    private static Phase2OperationalState Preparation(string node) => new()
    {
        PageFamily = Phase2PageFamily.Preparation,
        NodeId = Observation<string>.Known(node, 0.95)
    };

    private static Phase2OperationalState BattleContext(
        string node,
        int action) => new()
    {
        PageFamily = Phase2PageFamily.Battle,
        NodeId = Observation<string>.Known(node, 0.95),
        RemainingActionValue = Observation<RemainingActionValueState>.Known(
            RemainingActionValueState.Create(0, action),
            0.95)
    };

    private static Phase2OperationalState Battle(string node, int action)
    {
        var evidence = new EvidenceReference(
            "fixture:battle",
            "vision:battle-damage",
            CapturedAt: DateTimeOffset.Parse("2026-07-30T20:00:00+08:00"),
            Confidence: 0.95);
        var damage = new CharacterDamageState(
            1,
            "currency_wars_character_01",
            123_000,
            "12.3万",
            0.95,
            0.95,
            new RelativeRegion(0.85, 0.2, 0.04, 0.05),
            new RelativeRegion(0.89, 0.2, 0.08, 0.05),
            evidence);
        return BattleContext(node, action) with
        {
            BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
                [damage],
                0.95),
            BattleSynergyDamage = Observation<IReadOnlyList<SynergyDamageState>>.Known(
                [],
                1),
            BattleUnresolvedDamage = Observation<IReadOnlyList<UnresolvedDamageSourceState>>.Known(
                [],
                1),
            BattleScreenDamageCandidate = Observation<long>.Known(123_000, 0.95)
        };
    }
}
