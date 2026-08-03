using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class HistoricalDashboardProjectionTests
{
    [Fact]
    public void Observe_ProjectsFinalNodeHistoryAndEconomyDeltas()
    {
        var projection = new HistoricalDashboardProjection();
        var changed = new List<HistoricalDashboardSnapshot>();
        projection.Changed += (_, snapshot) => changed.Add(snapshot);

        projection.Observe("run-1", Preparation("run-1", "1-1", 12, 0));
        projection.Observe(
            "run-1",
            FinalBattle("run-1", "1-1", 1_000, 80, 5));
        projection.Observe("run-1", Preparation("run-1", "1-2", 17, 3));
        projection.Observe(
            "run-1",
            FinalBattle("run-1", "1-2", 200_000, 150, 9));

        var snapshot = projection.Current;
        Assert.Equal("run-1", snapshot.RunId);
        Assert.Equal(HistoricalDamageScale.Logarithmic, snapshot.DamageScale);
        Assert.Equal(2, snapshot.Nodes.Count);

        var first = snapshot.Nodes[0];
        Assert.Equal("1-1", first.NodeId);
        Assert.Equal(1_000, first.FinalDamage);
        Assert.Equal(80, first.RemainingActionValue);
        Assert.Equal(0, first.GoldSpentSincePreviousNode);
        Assert.Null(first.GoldDeltaSincePreviousNode);
        Assert.Equal(5, first.GoldReward);
        Assert.Equal(17, first.AbsoluteGold);

        var second = snapshot.Nodes[1];
        Assert.Equal("1-2", second.NodeId);
        Assert.Equal(200_000, second.FinalDamage);
        Assert.Equal(150, second.RemainingActionValue);
        Assert.Equal(3, second.GoldSpentSincePreviousNode);
        Assert.Equal(5, second.GoldDeltaSincePreviousNode);
        Assert.Equal(9, second.GoldReward);
        Assert.Null(second.AbsoluteGold);
        Assert.True(second.IsComplete);
        Assert.NotEmpty(changed);
    }

    [Fact]
    public void Observe_PreservesIncompleteFinalDataAsUnknownInsteadOfZero()
    {
        var projection = new HistoricalDashboardProjection();
        var evidence = Evidence("partial");
        var partial = new FinalNodeBattleState(
            "2-4",
            [],
            null,
            null,
            evidence.CapturedAt!.Value,
            evidence,
            IsComplete: false,
            CanDriveDecisions: false,
            Uncertainty: ["damage and remaining action are unavailable"]);

        projection.Observe("run-partial", Analysis(
            "run-partial",
            "2-4",
            Phase2PageFamily.BattleSettlement,
            finalBattle: partial));

        var row = Assert.Single(projection.Current.Nodes);
        Assert.Null(row.FinalDamage);
        Assert.Null(row.RemainingActionValue);
        Assert.Null(row.GoldReward);
        Assert.False(row.IsComplete);
    }

    [Fact]
    public void Observe_NewRunClearsPreviousRunRows()
    {
        var projection = new HistoricalDashboardProjection();
        projection.Observe(
            "run-old",
            FinalBattle("run-old", "3-1", 42_000, 25, 4));

        projection.Observe(
            "run-new",
            FinalBattle("run-new", "1-1", 99_000, 60, 6));

        var row = Assert.Single(projection.Current.Nodes);
        Assert.Equal("run-new", projection.Current.RunId);
        Assert.Equal("1-1", row.NodeId);
        Assert.Equal(99_000, row.FinalDamage);
    }

    [Fact]
    public void Observe_KeepsRealtimeFormationEquipmentAndRecognitionDetails()
    {
        var projection = new HistoricalDashboardProjection();
        var analysis = Preparation("run-detail", "1-3", 23, 4);
        var evidence = Evidence("detail");
        analysis = analysis with
        {
            Snapshot = analysis.Snapshot with
            {
                EquipmentIds = Observation<IReadOnlyList<string>>.Known(
                    ["equipment-slot-a"],
                    0.9,
                    [evidence])
            },
            OperationalState = analysis.OperationalState! with
            {
                Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                    [
                        new FormationCharacterState(
                            FormationZone.Front,
                            0,
                            "currency_wars_character_01",
                            2,
                            "front",
                            ["equipment-a", "equipment-b"],
                            0.92,
                            evidence)
                    ],
                    0.92,
                    [evidence]),
                RecognitionTrace =
                [
                    new Phase2FieldRecognitionTrace(
                        "economy",
                        "1-3",
                        "preparation_generic",
                        ["23"],
                        "23",
                        ObservationStatus.Known,
                        0.9,
                        1,
                        null,
                        new RelativeRegion(0.8, 0.8, 0.1, 0.1),
                        evidence.CapturedAt!.Value)
                ]
            }
        };

        projection.Observe("run-detail", analysis);

        var detail = Assert.Single(projection.Current.DetailNodes);
        var formation = Assert.Single(detail.LatestPreparationState!.Formation.Value!);
        Assert.Equal("currency_wars_character_01", formation.CharacterId);
        Assert.Equal(["equipment-a", "equipment-b"], formation.EquipmentIds);
        Assert.Equal(["equipment-slot-a"], detail.LatestSnapshot!.EquipmentIds.Value);
        Assert.Single(detail.LatestState!.RecognitionTrace);
    }

    [Fact]
    public void Observe_PartialStrategyUpdateCannotShrinkConfirmedSet()
    {
        var projection = new HistoricalDashboardProjection();
        var evidence = Evidence("strategy-monotonic");
        var first = Preparation("run-strategy", "1-3", 20, 0);
        first = first with
        {
            Snapshot = first.Snapshot with
            {
                InvestmentStrategyIds =
                    Observation<IReadOnlyList<string>>.Known(
                        ["strategy-a"],
                        0.9,
                        [evidence])
            },
            OperationalState = first.OperationalState! with
            {
                InvestmentStrategyIds =
                    Observation<IReadOnlyList<string>>.Known(
                        ["strategy-a"],
                        0.9,
                        [evidence])
            }
        };
        var partial = Preparation("run-strategy", "1-3", 20, 0);
        var partialStrategies = new Observation<IReadOnlyList<string>>
        {
            Status = ObservationStatus.Unknown,
            Value = ["strategy-b"],
            Confidence = 0,
            Evidence = [evidence with { Locator = "strategy-slot-2" }],
            Uncertainty = ["One occupied strategy slot is unresolved."],
            ObservedAt = evidence.CapturedAt
        };
        partial = partial with
        {
            Snapshot = partial.Snapshot with
            {
                InvestmentStrategyIds = partialStrategies
            },
            OperationalState = partial.OperationalState! with
            {
                InvestmentStrategyIds = partialStrategies
            }
        };

        projection.Observe("run-strategy", first);
        projection.Observe("run-strategy", partial);

        var detail = Assert.Single(projection.Current.DetailNodes);
        Assert.Equal(
            ObservationStatus.Unknown,
            detail.LatestSnapshot!.InvestmentStrategyIds.Status);
        Assert.Equal(
            ["strategy-a", "strategy-b"],
            detail.LatestSnapshot.InvestmentStrategyIds.Value);
        Assert.Equal(
            ["strategy-a", "strategy-b"],
            detail.LatestState!.InvestmentStrategyIds.Value);
    }

    [Fact]
    public void Observe_AttachesTransientPageLabelsToLastCanonicalNode()
    {
        var projection = new HistoricalDashboardProjection();
        projection.Observe("run-detail", Preparation("run-detail", "2-4", 12, 0));
        projection.Observe("run-detail", Analysis(
            "run-detail",
            "battle_generic",
            Phase2PageFamily.Battle));

        var detail = Assert.Single(projection.Current.DetailNodes);
        Assert.Equal("2-4", detail.NodeId);
        Assert.DoesNotContain(
            projection.Current.DetailNodes,
            item => string.Equals(item.NodeId, "battle_generic", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1_000, 50_000, HistoricalDamageScale.Linear)]
    [InlineData(1_000, 100_000, HistoricalDamageScale.Logarithmic)]
    public void SelectDamageScale_UsesRangeWithoutChangingExactValues(
        long low,
        long high,
        HistoricalDamageScale expected)
    {
        var scale = HistoricalDashboardProjection.SelectDamageScale([low, high]);

        Assert.Equal(expected, scale);
        Assert.InRange(
            HistoricalDashboardProjection.NormalizeDamage(
                low,
                [low, high],
                scale),
            0,
            1);
        Assert.Equal(
            1,
            HistoricalDashboardProjection.NormalizeDamage(
                high,
                [low, high],
                scale),
            10);
    }

    private static ScreenshotAnalysisResult Preparation(
        string runId,
        string nodeId,
        int gold,
        int cumulativeSpend) => Analysis(
        runId,
        nodeId,
        Phase2PageFamily.Preparation,
        gold,
        cumulativeSpend);

    private static ScreenshotAnalysisResult FinalBattle(
        string runId,
        string nodeId,
        long damage,
        int remainingAction,
        int reward)
    {
        var evidence = Evidence($"final-{nodeId}");
        var battle = new FinalNodeBattleState(
            nodeId,
            [],
            damage,
            RemainingActionValueState.Create(
                remainingAction / 100,
                remainingAction % 100),
            evidence.CapturedAt!.Value,
            evidence,
            SelectedDamage: damage,
            SelectedDamageSource: FinalDamageSelectionSource.BattleLastFrame,
            GoldReward: reward);
        return Analysis(
            runId,
            nodeId,
            Phase2PageFamily.BattleSettlement,
            finalBattle: battle);
    }

    private static ScreenshotAnalysisResult Analysis(
        string runId,
        string nodeId,
        Phase2PageFamily pageFamily,
        int? gold = null,
        int? cumulativeSpend = null,
        FinalNodeBattleState? finalBattle = null,
        string? pageId = null)
    {
        var now = DateTimeOffset.Parse("2026-07-30T23:00:00+08:00");
        var state = new Phase2OperationalState
        {
            PageFamily = pageFamily,
            PageId = pageId,
            NodeId = Observation<string>.Known(nodeId, 0.95, observedAt: now),
            CumulativeSpend = cumulativeSpend is null
                ? Observation<int>.Unknown("not visible")
                : Observation<int>.Known(
                    cumulativeSpend.Value,
                    0.95,
                    observedAt: now),
            FinalBattle = finalBattle is null
                ? Observation<FinalNodeBattleState>.Unknown("not finalized")
                : finalBattle.IsComplete
                    ? Observation<FinalNodeBattleState>.Known(
                        finalBattle,
                        0.9,
                        [finalBattle.Evidence],
                        now)
                    : new Observation<FinalNodeBattleState>
                    {
                        Status = ObservationStatus.Unknown,
                        Value = finalBattle,
                        Confidence = 0,
                        Evidence = [finalBattle.Evidence],
                        Uncertainty = finalBattle.FinalUncertainty,
                        ObservedAt = now
                    }
        };
        return new ScreenshotAnalysisResult
        {
            AnalysisId = $"analysis-{runId}-{nodeId}-{pageFamily}",
            Snapshot = new RunSnapshot
            {
                RunId = runId,
                AsOf = now,
                Stage = Observation<string>.Known(nodeId, 0.95, observedAt: now),
                Economy = gold is null
                    ? Observation<int>.Unknown("not visible")
                    : Observation<int>.Known(gold.Value, 0.95, observedAt: now),
                CumulativeSpend = cumulativeSpend is null
                    ? Observation<int>.Unknown("not visible")
                    : Observation<int>.Known(
                        cumulativeSpend.Value,
                        0.95,
                        observedAt: now)
            },
            OperationalState = state
        };
    }

    private static EvidenceReference Evidence(string id) => new(
        id,
        "test:historical-dashboard",
        CapturedAt: DateTimeOffset.Parse("2026-07-30T23:00:00+08:00"),
        Confidence: 0.95);

    [Fact]
    public void Observe_DoesNotInventRewardFromPreparationGoldDelta()
    {
        var projection = new HistoricalDashboardProjection();
        var evidence = Evidence("final-1-1");
        var battle = new FinalNodeBattleState(
            "1-1",
            [],
            1_000,
            RemainingActionValueState.Create(0, 60),
            evidence.CapturedAt!.Value,
            evidence,
            SelectedDamage: 1_000,
            SelectedDamageSource: FinalDamageSelectionSource.BattleLastFrame,
            GoldReward: null);

        // 1-1 备战 15 → finalize（结算奖励缺失，模拟结算页被快速跳过）
        projection.Observe("run-1", Preparation("run-1", "1-1", 15, 0));
        projection.Observe(
            "run-1",
            Analysis(
                "run-1",
                "1-1",
                Phase2PageFamily.BattleSettlement,
                finalBattle: battle));
        // 下一节点备战金币 23 只能证明净变化为 8；该差值同时受消费、
        // 利息和特殊规则影响，不能伪装成结算奖励。
        projection.Observe("run-1", Preparation("run-1", "1-2", 23, 0));

        projection.Observe("run-1", Preparation("run-1", "1-2", 23, 0));
        var secondEvidence = Evidence("final-1-2");
        var secondBattle = new FinalNodeBattleState(
            "1-2",
            [],
            2_000,
            RemainingActionValueState.Create(0, 50),
            secondEvidence.CapturedAt!.Value,
            secondEvidence,
            SelectedDamage: 2_000,
            SelectedDamageSource: FinalDamageSelectionSource.BattleLastFrame,
            GoldReward: null);
        projection.Observe(
            "run-1",
            Analysis(
                "run-1",
                "1-2",
                Phase2PageFamily.BattleSettlement,
                finalBattle: secondBattle));

        var row = projection.Current.Nodes.Single(node => node.NodeId == "1-1");
        Assert.Null(row.GoldReward);
        Assert.Equal(8, projection.Current.Nodes
            .Single(node => node.NodeId == "1-2")
            .GoldDeltaSincePreviousNode);
    }

    [Fact]
    public void Observe_RewardShopFrameDoesNotPollutePreparationGold()
    {
        var projection = new HistoricalDashboardProjection();
        projection.Observe("run-1", Preparation("run-1", "1-1", 15, 0));
        projection.Observe(
            "run-1",
            FinalBattle("run-1", "1-1", 1_000, 60, 5));

        // 补给选择页（金币 99）不是备战页，不得回填上一节点 EndingGold
        projection.Observe(
            "run-1",
            Analysis(
                "run-1",
                "1-1",
                Phase2PageFamily.Preparation,
                gold: 99,
                pageId: "reward_shop"));
        // 1-2 备战金币 23 正常回填 1-1 的 EndingGold
        projection.Observe("run-1", Preparation("run-1", "1-2", 23, 0));

        var row = projection.Current.Nodes.Single(node => node.NodeId == "1-1");
        Assert.Equal(23, row.AbsoluteGold);
    }
}
