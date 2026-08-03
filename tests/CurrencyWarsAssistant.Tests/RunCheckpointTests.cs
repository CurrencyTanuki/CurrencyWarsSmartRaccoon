using System.Text;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.App;

namespace CurrencyWarsAssistant.Tests;

public sealed class RunCheckpointTests
{
    [Fact]
    public async Task CheckpointWriteIsAtomicVersionedAndListed()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var first = Checkpoint("run-resume", "2-4") with
            {
                SavedObservationCount = 7
            };
            await store.SaveCheckpointAsync(first, CancellationToken.None);
            var second = first with
            {
                LastSavedAtUtc = first.LastSavedAtUtc.AddSeconds(10),
                SavedObservationCount = 8
            };
            await store.SaveCheckpointAsync(second, CancellationToken.None);

            var runDirectory = store.GetRunDirectory(first.RunId);
            Assert.True(File.Exists(Path.Combine(runDirectory, "checkpoint.v1.json")));
            Assert.True(File.Exists(Path.Combine(runDirectory, "checkpoint.v1.json.bak")));
            Assert.Empty(Directory.EnumerateFiles(runDirectory, "*.tmp-*"));

            var summary = Assert.Single(
                await store.ListIncompleteRunsAsync(CancellationToken.None));
            Assert.Equal("run-resume", summary.Checkpoint.RunId);
            Assert.Equal("2-4", summary.Checkpoint.LastConfirmedNodeId);
            Assert.Equal(8, summary.Checkpoint.SavedObservationCount);
            Assert.Equal(RunCheckpointHealth.Healthy, summary.Health);
            Assert.Equal(1, summary.Checkpoint.CheckpointVersion);
            Assert.Equal("1.0.0", summary.Checkpoint.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptPrimaryCheckpointFallsBackToLastGoodBackup()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var first = Checkpoint("run-backup", "2-4");
            await store.SaveCheckpointAsync(first, CancellationToken.None);
            await store.SaveCheckpointAsync(
                first with
                {
                    LastConfirmedNodeId = "2-5",
                    LastSavedAtUtc = first.LastSavedAtUtc.AddSeconds(10)
                },
                CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(store.GetRunDirectory(first.RunId), "checkpoint.v1.json"),
                "{broken",
                Encoding.UTF8);

            var summary = Assert.Single(
                await store.ListIncompleteRunsAsync(CancellationToken.None));
            Assert.Equal(RunCheckpointHealth.RecoveredFromBackup, summary.Health);
            Assert.Equal("2-4", summary.Checkpoint.LastConfirmedNodeId);
            Assert.Contains(summary.Diagnostics, value =>
                value.Contains("primary", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyIncompleteRunIsSynthesizedWithoutInventingMissingNodes()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var analysis = Analysis("run-legacy", "2-4");
            await store.SaveAnalysisAsync(analysis, CancellationToken.None);

            var summary = Assert.Single(
                await store.ListIncompleteRunsAsync(CancellationToken.None));
            Assert.Equal(RunCheckpointHealth.SynthesizedFromArtifacts, summary.Health);
            Assert.Equal("2-4", summary.Checkpoint.LastConfirmedNodeId);
            Assert.Empty(summary.Checkpoint.MissingNodeIds);
            Assert.Equal(1, summary.Checkpoint.SavedObservationCount);
            Assert.Contains(summary.Diagnostics, value =>
                value.Contains("legacy", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SynthesizedCheckpointMergesAllFramesAndKeepsMonotonicFacts()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var first = Analysis("run-legacy-merged", "1-3") with
            {
                AnalysisId = "analysis-legacy-first",
                Snapshot = Analysis("run-legacy-merged", "1-3").Snapshot with
                {
                    InvestmentStrategyIds =
                        Observation<IReadOnlyList<string>>.Known(["strategy-a"], 0.9)
                },
                OperationalState = Analysis("run-legacy-merged", "1-3")
                    .OperationalState! with
                {
                    NegativeAffixIds =
                        Observation<IReadOnlyList<string>>.Known(["affix-a"], 0.9)
                }
            };
            var second = first with
            {
                AnalysisId = "analysis-legacy-second",
                Snapshot = first.Snapshot with
                {
                    AsOf = first.Snapshot.AsOf.AddSeconds(2),
                    Economy = Observation<int>.Unknown("temporarily occluded"),
                    InvestmentStrategyIds =
                        Observation<IReadOnlyList<string>>.Known(["strategy-b"], 0.88)
                },
                OperationalState = first.OperationalState! with
                {
                    NegativeAffixIds =
                        Observation<IReadOnlyList<string>>.Known(["affix-b"], 0.88)
                }
            };
            await store.SaveAnalysisAsync(first, CancellationToken.None);
            await store.SaveAnalysisAsync(second, CancellationToken.None);

            var summary = Assert.Single(
                await store.ListIncompleteRunsAsync(CancellationToken.None));

            Assert.Equal(2, summary.Checkpoint.SavedObservationCount);
            Assert.Equal(30, summary.Checkpoint.LastSnapshot!.Economy.Value);
            Assert.Equal(
                ObservationStatus.Stale,
                summary.Checkpoint.LastSnapshot.Economy.Status);
            Assert.Equal(
                ["strategy-a", "strategy-b"],
                summary.Checkpoint.LastSnapshot.InvestmentStrategyIds.Value);
            Assert.Equal(
                ["affix-a", "affix-b"],
                summary.Checkpoint.LastOperationalState!.NegativeAffixIds.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("2-4", "2-4", RunResumeDecisionKind.ContinueExisting, "")]
    [InlineData("2-4", "2-7", RunResumeDecisionKind.ContinueExisting, "2-5,2-6")]
    [InlineData("2-4", "2-2", RunResumeDecisionKind.CreateNewRun, "")]
    [InlineData("2-4", null, RunResumeDecisionKind.RequireUserChoice, "")]
    public void ResumePolicyUsesNodeProgressionAndPreservesGaps(
        string previousNode,
        string? observedNode,
        RunResumeDecisionKind expectedKind,
        string expectedMissing)
    {
        var checkpoint = Checkpoint("run-policy", previousNode);
        var observation = new RunResumeObservation(
            observedNode,
            checkpoint.IdentityEvidence,
            DateTimeOffset.Parse("2026-07-31T08:00:00+08:00"));

        var decision = RunResumePolicy.Decide(checkpoint, observation);

        Assert.Equal(expectedKind, decision.Kind);
        Assert.Equal(
            expectedMissing,
            string.Join(',', decision.MissingNodeIds));
    }

    [Fact]
    public void NodeSequenceIncludesEightAndNineBeforeNextPlane()
    {
        Assert.True(RunResumePolicy.TryGetNodeRank("1-8", out var rank18));
        Assert.True(RunResumePolicy.TryGetNodeRank("1-9", out var rank19));
        Assert.True(RunResumePolicy.TryGetPreviousNode("1-9", out var before19));
        Assert.True(RunResumePolicy.TryGetPreviousNode("2-1", out var before21));

        Assert.Equal(rank18 + 1, rank19);
        Assert.Equal("1-8", before19);
        Assert.Equal("1-9", before21);
    }

    [Fact]
    public void ResumeGapAcrossPlaneKeepsNodesEightAndNine()
    {
        var checkpoint = Checkpoint("run-cross-plane", "1-7");
        var decision = RunResumePolicy.Decide(
            checkpoint,
            new RunResumeObservation(
                "2-1",
                checkpoint.IdentityEvidence,
                DateTimeOffset.Parse("2026-07-31T08:00:00+08:00")));

        Assert.Equal(RunResumeDecisionKind.ContinueExisting, decision.Kind);
        Assert.Equal(["1-8", "1-9"], decision.MissingNodeIds);
    }

    [Fact]
    public void ResumePolicyRequiresChoiceWhenRunIdentityConflicts()
    {
        var checkpoint = Checkpoint("run-conflict", "2-4") with
        {
            IdentityEvidence = new RunIdentityEvidence
            {
                InvestmentEnvironmentId = "environment-a",
                InvestmentStrategyIds = ["strategy-a"],
                EnemyAffixIds = ["affix-a"]
            }
        };
        var observation = new RunResumeObservation(
            "2-7",
            new RunIdentityEvidence
            {
                InvestmentEnvironmentId = "environment-b",
                InvestmentStrategyIds = ["strategy-a"],
                EnemyAffixIds = ["affix-a"]
            },
            DateTimeOffset.Parse("2026-07-31T08:00:00+08:00"));

        var decision = RunResumePolicy.Decide(checkpoint, observation);

        Assert.Equal(RunResumeDecisionKind.RequireUserChoice, decision.Kind);
        Assert.Contains(decision.Reasons, value =>
            value.Contains("investment environment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompletedRunsAreNotOfferedForResume()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var checkpoint = Checkpoint("run-completed", "3-7") with
            {
                LifecycleStatus = RunCheckpointLifecycleStatus.Completed
            };
            await store.SaveCheckpointAsync(checkpoint, CancellationToken.None);
            Directory.CreateDirectory(store.GetRunDirectory(checkpoint.RunId));
            await File.WriteAllTextAsync(
                Path.Combine(
                    store.GetRunDirectory(checkpoint.RunId),
                    "completed-run.v1.json"),
                "{}",
                Encoding.UTF8);

            Assert.Empty(
                await store.ListIncompleteRunsAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IncompleteRunCanBeDeletedWithoutTouchingOtherRuns()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            await store.SaveCheckpointAsync(
                Checkpoint("run-delete", "1-2"),
                CancellationToken.None);
            await store.SaveCheckpointAsync(
                Checkpoint("run-keep", "2-4"),
                CancellationToken.None);

            await store.DeleteIncompleteRunAsync(
                "run-delete",
                CancellationToken.None);

            Assert.False(Directory.Exists(
                store.GetRunDirectory("run-delete")));
            var remaining = Assert.Single(
                await store.ListIncompleteRunsAsync(
                    CancellationToken.None));
            Assert.Equal("run-keep", remaining.Checkpoint.RunId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IncompleteRunPresentationExposesRecoveryAndCompletenessWithoutGuessing()
    {
        var checkpoint = Checkpoint("run-presentation", "2-4") with
        {
            DataCompleteness = new RunDataCompleteness(4, 8, 2, 1)
        };
        var viewModel = new IncompleteRunViewModel(new RunCheckpointSummary(
            checkpoint,
            RunCheckpointHealth.RecoveredFromBackup,
            "checkpoint.v1.json.bak",
            ["primary corrupt"]));

        Assert.Equal("2-4", viewModel.LastNodeDisplay);
        Assert.Contains("50", viewModel.CompletenessDisplay, StringComparison.Ordinal);
        Assert.Contains("2 个最终节点", viewModel.CompletenessDisplay, StringComparison.Ordinal);
        Assert.Equal("已从备份恢复", viewModel.RecoveryDisplay);
    }

    [Fact]
    public void MainWindowContainsAnExplicitManualResumeEntry()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CurrencyWarsAssistant.App",
            "MainWindow.xaml"));

        Assert.Contains("ItemsSource=\"{Binding IncompleteRuns}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ContinueIncompleteRunCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ContinueIncompleteRunCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SettleIncompleteRunCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DeleteIncompleteRunCommand", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalUnknownFrameKeepsLastReliableBuildAsStaleCheckpointEvidence()
    {
        var now = DateTimeOffset.Parse("2026-08-01T18:00:00+08:00");
        var evidence = new EvidenceReference(
            "fixture:preparation",
            "vision:formation",
            CapturedAt: now,
            Confidence: 0.9);
        var formation = new[]
        {
            new FormationCharacterState(
                FormationZone.Front,
                0,
                "character-a",
                2,
                "front",
                ["equipment-a"],
                0.9,
                evidence,
                EquipmentSlots:
                [
                    new CharacterEquipmentSlotState(
                        0,
                        EquipmentSlotOccupancy.Equipped,
                        "equipment-a",
                        [],
                        0.9,
                        new RelativeRegion(0.4, 0.4, 0.02, 0.02),
                        evidence),
                    new CharacterEquipmentSlotState(
                        1,
                        EquipmentSlotOccupancy.Empty,
                        null,
                        [],
                        0.9,
                        new RelativeRegion(0.42, 0.4, 0.02, 0.02),
                        evidence)
                ])
        };
        var initial = RunCheckpointFactory.CreateInitial(
            "run-stale-build",
            RunEntryMode.DirectRecording,
            now);
        var preparation = new ScreenshotAnalysisResult
        {
            AnalysisId = "analysis-preparation",
            Snapshot = new RunSnapshot
            {
                RunId = initial.RunId,
                AsOf = now,
                PageId = Observation<string>.Known("preparation_generic", 1),
                Stage = Observation<string>.Known("1-3", 1),
                EquipmentIds = Observation<IReadOnlyList<string>>.Known(
                    ["equipment-a"],
                    0.9,
                    [evidence],
                    now)
            },
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Preparation,
                PageId = "preparation_generic",
                NodeId = Observation<string>.Known("1-3", 1),
                Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                    formation,
                    0.9,
                    [evidence],
                    now)
            }
        };
        var afterPreparation = RunCheckpointFactory.FromAnalysis(
            initial,
            preparation,
            1,
            RunCheckpointLifecycleStatus.Active,
            now);
        var terminal = new ScreenshotAnalysisResult
        {
            AnalysisId = "analysis-terminal",
            Snapshot = new RunSnapshot
            {
                RunId = initial.RunId,
                AsOf = now.AddSeconds(3),
                PageId = Observation<string>.Known("challenge_failed", 0.98),
                Stage = Observation<string>.Unknown("terminal page has no node"),
                EquipmentIds = Observation<IReadOnlyList<string>>.Unknown(
                    "terminal page has no equipment region")
            },
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.BattleSettlement,
                PageId = "challenge_failed",
                Formation = Observation<IReadOnlyList<FormationCharacterState>>.Unknown(
                    "terminal page has no formation region")
            }
        };

        var result = RunCheckpointFactory.FromAnalysis(
            afterPreparation,
            terminal,
            2,
            RunCheckpointLifecycleStatus.Completed,
            now.AddSeconds(3));

        Assert.Equal(ObservationStatus.Stale, result.LastSnapshot!.EquipmentIds.Status);
        Assert.Equal(["equipment-a"], result.LastSnapshot.EquipmentIds.Value);
        Assert.Equal(
            ObservationStatus.Stale,
            result.LastOperationalState!.Formation.Status);
        Assert.Equal(
            "equipment-a",
            Assert.Single(result.LastOperationalState.Formation.Value!)
                .FinalEquipmentSlots[0].EquipmentId);
        Assert.Contains(
            result.LastOperationalState.Formation.Uncertainty,
            value => value.Contains("保留上一帧", StringComparison.Ordinal));
    }

    private static RunCheckpointRecord Checkpoint(
        string runId,
        string nodeId)
    {
        var now = DateTimeOffset.Parse("2026-07-31T07:00:00+08:00");
        return new RunCheckpointRecord
        {
            RunId = runId,
            CreatedAtUtc = now,
            LastSavedAtUtc = now,
            LifecycleStatus = RunCheckpointLifecycleStatus.Paused,
            EntryMode = RunEntryMode.DirectRecording,
            LastConfirmedNodeId = nodeId,
            LastConfirmedPageId = "preparation_generic",
            SavedObservationCount = 1,
            DataCompleteness = new RunDataCompleteness(4, 8, 1, 0),
            IdentityEvidence = new RunIdentityEvidence
            {
                InvestmentEnvironmentId = "environment-a",
                InvestmentStrategyIds = ["strategy-a"],
                EnemyAffixIds = ["affix-a"]
            }
        };
    }

    private static ScreenshotAnalysisResult Analysis(
        string runId,
        string nodeId)
    {
        var now = DateTimeOffset.Parse("2026-07-31T07:00:00+08:00");
        return new ScreenshotAnalysisResult
        {
            AnalysisId = "analysis-legacy",
            Snapshot = new RunSnapshot
            {
                RunId = runId,
                AsOf = now,
                PageId = Observation<string>.Known(
                    "preparation_generic",
                    0.95,
                    observedAt: now),
                Stage = Observation<string>.Known(nodeId, 0.95, observedAt: now),
                Economy = Observation<int>.Known(30, 0.9, observedAt: now)
            },
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Preparation,
                PageId = "preparation_generic",
                NodeId = Observation<string>.Known(nodeId, 0.95, observedAt: now)
            }
        };
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
