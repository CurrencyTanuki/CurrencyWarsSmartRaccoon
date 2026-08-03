using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class DataChainRegressionTests
{
    [Fact]
    public void EnemyOverviewIdentityFilterDoesNotMixAffixesIntoFaction()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-01T12:00:00+08:00");
        var source = Observation<IReadOnlyList<string>>.Known(
            ["competitor-a", "affix-a", "affix-b"],
            0.9,
            observedAt: observedAt);

        var filtered = CurrencyWarsSituationScreenshotAnalyzer
            .FilterIdentityObservation(
                source,
                new HashSet<string>(["competitor-a"], StringComparer.Ordinal),
                "faction missing");

        Assert.Equal(ObservationStatus.Known, filtered.Status);
        Assert.Equal(["competitor-a"], filtered.Value);
        Assert.DoesNotContain("affix-a", filtered.Value!);
    }

    [Fact]
    public void SynergyProgressKeepsIconIdentityAndReadsAdjacentLevelText()
    {
        Assert.Equal(
            (2, 4),
            Phase2OperationalScreenshotAnalyzer.ParseSynergyProgress(
                ["燃血 2/4/6/8"]));
        Assert.Equal(
            (null, null),
            Phase2OperationalScreenshotAnalyzer.ParseSynergyProgress(
                ["燃血"]));
    }

    [Theory]
    [InlineData("2", 2, true)]
    [InlineData("2 | 2", 2, true)]
    [InlineData("G1", 1, false)]
    [InlineData("Q2", 2, false)]
    [InlineData("1è", 1, false)]
    public void EconomyAuxiliaryNumbersRequireCleanOcrTokens(
        string rawText,
        int value,
        bool expected)
    {
        Assert.Equal(
            expected,
            Phase2OperationalScreenshotAnalyzer.IsCleanIntegerOcrText(
                rawText,
                value));
    }

    [Fact]
    public void LiveEventProducerEmitsEveryImplementedSnapshotDataChain()
    {
        var now = DateTimeOffset.Parse("2026-08-01T12:30:00+08:00");
        var snapshot = new RunSnapshot
        {
            RunId = "run-events",
            AsOf = now,
            PageId = Known("preparation_1_2", now),
            Stage = Known("1-2", now),
            Economy = Known(20, now),
            CumulativeSpend = Known(3, now),
            Health = Known(82, now),
            ActionPoints = Known(155, now),
            CurrentNodeDamage = Known(1_234L, now),
            BoardCharacterIds = KnownList(["character-a"], now),
            BenchCharacterIds = KnownList(["character-b"], now),
            ShopCharacterIds = KnownList(["character-c"], now),
            LineupIds = KnownList(["character-a"], now),
            SynergyIds = KnownList(["synergy-a"], now),
            InvestmentEnvironmentId = Known("environment-a", now),
            InvestmentStrategyIds = KnownList(["strategy-a"], now),
            EquipmentIds = KnownList(["equipment-a"], now),
            SpecialItemIds = KnownList(["special-a"], now),
            ExpertAdvisorIds = KnownList(["advisor-a"], now),
            EnemyIds = KnownList(["competitor-a"], now)
        };
        var analysis = new ScreenshotAnalysisResult
        {
            AnalysisId = "analysis-events",
            Snapshot = snapshot,
            OperationalState = new Phase2OperationalState
            {
                SettlementGoldReward = Known(9, now)
            }
        };

        var types = Phase2LiveCollectionService.CreateEvents(analysis)
            .Select(item => item.EventType)
            .ToHashSet();

        Assert.Contains(RunEventType.ActionPointsObserved, types);
        Assert.Contains(RunEventType.NodeDamageObserved, types);
        Assert.Contains(RunEventType.ShopObserved, types);
        Assert.Contains(RunEventType.SpecialItemObserved, types);
        Assert.Contains(RunEventType.ExpertAdvisorObserved, types);
        Assert.Contains(RunEventType.RewardObserved, types);
    }

    [Fact]
    public async Task CompletedArchiveRejectsPageFamilyNamesAsNodeIds()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var now = DateTimeOffset.Parse("2026-08-01T13:00:00+08:00");
            await store.SaveAnalysisAsync(
                Analysis(
                    "analysis-pseudo-node",
                    "run-node-filter",
                    now,
                    "BattleSettlement",
                    Observation<string>.Unknown("no node")),
                CancellationToken.None);
            await store.SaveAnalysisAsync(
                Analysis(
                    "analysis-valid-node",
                    "run-node-filter",
                    now.AddSeconds(1),
                    "preparation",
                    Known("1-2", now.AddSeconds(1))),
                CancellationToken.None);

            var archive = await store.CompleteRunAsync(
                "run-node-filter",
                now.AddMinutes(1),
                "challenge_failed",
                "1-2",
                null,
                null,
                CancellationToken.None);

            var node = Assert.Single(archive.Nodes);
            Assert.Equal("1-2", node.NodeId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ScreenshotAnalysisResult Analysis(
        string analysisId,
        string runId,
        DateTimeOffset asOf,
        string stage,
        Observation<string> node) => new()
    {
        AnalysisId = analysisId,
        Snapshot = new RunSnapshot
        {
            RunId = runId,
            AsOf = asOf,
            Stage = Known(stage, asOf)
        },
        OperationalState = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            NodeId = node
        }
    };

    private static Observation<T> Known<T>(T value, DateTimeOffset observedAt) =>
        Observation<T>.Known(value, 0.95, observedAt: observedAt);

    private static Observation<IReadOnlyList<string>> KnownList(
        IReadOnlyList<string> values,
        DateTimeOffset observedAt) =>
        Observation<IReadOnlyList<string>>.Known(
            values,
            0.95,
            observedAt: observedAt);
}
