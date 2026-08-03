using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class RunCompletionArchiveTests
{
    [Fact]
    public void CompletedRunRequiresConfirmedNewPreparationBeforeRollover()
    {
        var detector = new Phase2PostCompletionBoundaryDetector();
        var first = Analysis("old-run", "1-1", "preparation_generic")
            .OperationalState!;

        Assert.False(detector.Observe(first));
        Assert.False(detector.Observe(first with
        {
            PageFamily = Phase2PageFamily.Unknown
        }));
        Assert.True(detector.Observe(first));

        detector.Reset();
        Assert.False(detector.Observe(first));
        Assert.False(detector.Observe(first with
        {
            NodeId = Observation<string>.Known("1-2", 0.95)
        }));
        Assert.False(detector.Observe(first));
        Assert.True(detector.Observe(first));
    }

    [Fact]
    public void FinalChallengeSuccessRequiresTheFixedLastNode()
    {
        Assert.False(new LiveCollectionStartOptions().DeleteScreenshotsOnCompletion);
        var nodeSuccess = Analysis("run-final", "3-7", "challenge_success");

        Assert.False(Phase2RunCompletionDetector.IsCompletedRunPage(
            nodeSuccess,
            trackedNodeId: "3-7"));
        var finalSuccess = nodeSuccess with
        {
            Snapshot = nodeSuccess.Snapshot with
            {
                Stage = Observation<string>.Known("run_rating", 0.9)
            },
            OperationalState = nodeSuccess.OperationalState! with
            {
                NodeId = Observation<string>.Unknown("final rating page has no node")
            }
        };
        Assert.True(Phase2RunCompletionDetector.IsCompletedRunPage(
            finalSuccess,
            trackedNodeId: "3-7"));
        var titleOnlyFinalSuccess = finalSuccess with
        {
            Snapshot = finalSuccess.Snapshot with
            {
                PageId = finalSuccess.Snapshot.PageId with { Evidence = [] }
            }
        };
        Assert.False(Phase2RunCompletionDetector.IsCompletedRunPage(
            titleOnlyFinalSuccess,
            trackedNodeId: "3-7"));
        Assert.False(Phase2RunCompletionDetector.IsCompletedRunPage(
            finalSuccess with
            {
                Snapshot = finalSuccess.Snapshot with
                {
                    Stage = Observation<string>.Known("2-6", 0.9)
                },
                OperationalState = finalSuccess.OperationalState! with
                {
                    NodeId = Observation<string>.Unknown("final rating page has no node")
                }
            },
            trackedNodeId: "2-6"));
        var failedSeed = Analysis("run-failed", "2-5", "challenge_failed");
        var failed = failedSeed with
        {
            Snapshot = failedSeed.Snapshot with
            {
                Health = Observation<int>.Known(83, 0.95)
            }
        };
        Assert.True(Phase2RunCompletionDetector.IsCompletedRunPage(
            failed,
            trackedNodeId: "2-5"));
        Assert.Equal(
            Phase2RunCompletionPageKind.FinalFailure,
            Phase2RunCompletionDetector.Classify(failed, trackedNodeId: "2-5"));
        Assert.True(Phase2RunCompletionDetector.IsFailedRunPage(failed));
        Assert.False(Phase2RunCompletionDetector.IsHealthDepletedRunPage(failed));
        var titleOnlyFailure = failed with
        {
            Snapshot = failed.Snapshot with
            {
                PageId = failed.Snapshot.PageId with { Evidence = [] }
            }
        };
        // 仅标题的 challenge_failed 页（无结算语义证据）也应判定整局失败——
        // 玩家主动结算（保存并退出）时失败页往往没有完整结算摘要。
        Assert.Equal(
            Phase2RunCompletionPageKind.FinalFailure,
            Phase2RunCompletionDetector.Classify(
                titleOnlyFailure,
                trackedNodeId: "2-5"));
        var depleted = Analysis(
            "run-depleted",
            "2-5",
            "challenge_health_depleted");
        Assert.False(Phase2RunCompletionDetector.IsCompletedRunPage(
            depleted,
            trackedNodeId: "2-5"));
        Assert.False(Phase2RunCompletionDetector.IsFailedRunPage(depleted));
        Assert.True(
            Phase2RunCompletionDetector.IsFailureSettlementTransitionPage(
                depleted));
        Assert.True(Phase2RunCompletionDetector.IsHealthDepletedRunPage(depleted));
        Assert.Equal(
            "SSS",
            Phase2RunCompletionDetector.ParseRating(["对局评价", "S S S"]));
    }

    [Fact]
    public async Task OptionalCompletionCleanupDeletesOnlyImageDirectories()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var runDirectory = store.GetRunDirectory("run-cleanup");
            foreach (var directory in new[]
                     {
                         "screenshots",
                         "unresolved-icons",
                         "recognition-failures"
                     })
            {
                Directory.CreateDirectory(Path.Combine(runDirectory, directory));
                await File.WriteAllTextAsync(
                    Path.Combine(runDirectory, directory, "sample.png"),
                    "image-placeholder");
            }

            Directory.CreateDirectory(Path.Combine(runDirectory, "reports"));
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "completed-run.v1.json"),
                "{}");
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "reports", "challenge-summary.html"),
                "report");

            var deleted = await store.DeleteRunImageArtifactsAsync(
                "run-cleanup",
                CancellationToken.None);

            Assert.Equal(3, deleted.Count);
            Assert.False(Directory.Exists(Path.Combine(runDirectory, "screenshots")));
            Assert.False(Directory.Exists(Path.Combine(runDirectory, "unresolved-icons")));
            Assert.False(Directory.Exists(Path.Combine(runDirectory, "recognition-failures")));
            Assert.True(File.Exists(Path.Combine(runDirectory, "completed-run.v1.json")));
            Assert.True(File.Exists(Path.Combine(
                runDirectory,
                "reports",
                "challenge-summary.html")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletionArchiveIsAtomicAndIdempotent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var runId = "run-final";
            var preparation = Analysis(runId, "3-7", "preparation_generic");
            await store.SaveAnalysisAsync(preparation, CancellationToken.None);
            var evidence = new EvidenceReference(
                "fixture:3-7",
                "battle:last-frame",
                CapturedAt: preparation.Snapshot.AsOf,
                Confidence: 0.9);
            await store.SaveFinalNodeBattleAsync(
                runId,
                new FinalNodeBattleState(
                    "3-7",
                    [],
                    12_345,
                    RemainingActionValueState.Create(0, 20),
                    preparation.Snapshot.AsOf,
                    evidence,
                    SelectedDamage: 12_345,
                    SelectedDamageSource:
                        FinalDamageSelectionSource.BattleLastFrame),
                CancellationToken.None);

            var first = await store.CompleteRunAsync(
                runId,
                preparation.Snapshot.AsOf.AddMinutes(1),
                "challenge_success",
                "3-7",
                "screenshots/final.png",
                "SSS",
                CancellationToken.None);
            var second = await store.CompleteRunAsync(
                runId,
                preparation.Snapshot.AsOf.AddMinutes(2),
                "challenge_success",
                "3-7",
                "screenshots/other.png",
                "S",
                CancellationToken.None);

            Assert.True(first.IsFinal);
            Assert.Equal(1, first.ArchiveVersion);
            Assert.False(string.IsNullOrWhiteSpace(first.SourceRevision));
            Assert.Equal(first.SourceRevision, second.SourceRevision);
            Assert.Equal(1, second.ArchiveVersion);
            Assert.Equal(first.CompletedAt, second.CompletedAt);
            Assert.Equal("SSS", second.RatingText);
            Assert.Equal(12_345, Assert.Single(second.Nodes).FinalBattle!.TotalDamage);
            Assert.True(File.Exists(Path.Combine(
                store.GetRunDirectory(runId),
                "completed-run.v1.json")));
            Assert.True(File.Exists(Path.Combine(
                store.GetRunDirectory(runId),
                "completed-run.v1.json.sha256")));
            var reportPath = await new ChallengeSummaryReportGenerator()
                .GenerateFromArchiveAsync(
                    Path.Combine(
                        store.GetRunDirectory(runId),
                        "completed-run.v1.json"),
                    CancellationToken.None);
            var report = await File.ReadAllTextAsync(reportPath);
            Assert.Equal(".html", Path.GetExtension(reportPath));
            Assert.Equal("reports", Path.GetFileName(Path.GetDirectoryName(reportPath)));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(reportPath)!,
                "challenge-summary.md")));
            Assert.Contains("货币战争挑战总结", report);
            Assert.Contains("3-7", report);
            Assert.Contains("12,345", report);
            Assert.Contains("已封存，不再修改", report);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletionArchiveRebuildsWhenDurableSourceDataIsCorrected()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var runId = "run-corrected-after-completion";
            var initial = Analysis(runId, "1-3", "preparation_generic") with
            {
                AnalysisId = "stable-preparation",
                Snapshot = Analysis(runId, "1-3", "preparation_generic").Snapshot with
                {
                    Economy = Observation<int>.Known(13, 0.72)
                }
            };
            await store.SaveAnalysisAsync(initial, CancellationToken.None);
            var first = await store.CompleteRunAsync(
                runId,
                initial.Snapshot.AsOf.AddMinutes(1),
                "challenge_failed",
                "1-3",
                "screenshots/final.png",
                "C",
                CancellationToken.None);

            var corrected = initial with
            {
                Snapshot = initial.Snapshot with
                {
                    Economy = Observation<int>.Known(3, 0.97),
                    AsOf = initial.Snapshot.AsOf.AddSeconds(1)
                }
            };
            await store.SaveAnalysisAsync(corrected, CancellationToken.None);
            var rebuilt = await store.CompleteRunAsync(
                runId,
                initial.Snapshot.AsOf.AddMinutes(2),
                "challenge_failed",
                "1-3",
                "screenshots/ignored.png",
                "B",
                CancellationToken.None);

            Assert.Equal(1, first.ArchiveVersion);
            Assert.Equal(2, rebuilt.ArchiveVersion);
            Assert.NotEqual(first.SourceRevision, rebuilt.SourceRevision);
            Assert.Equal(first.CompletedAt, rebuilt.CompletedAt);
            Assert.Equal("C", rebuilt.RatingText);
            Assert.Equal(
                3,
                Assert.Single(rebuilt.Nodes).FinalPreparationSnapshot!.Economy.Value);
            var historyPath = Path.Combine(
                store.GetRunDirectory(runId),
                "archive-history",
                "completed-run.v1.archive-v1.json");
            Assert.True(File.Exists(historyPath));
            var historical = AdvisorJson.Deserialize<CompletedRunRecord>(
                await File.ReadAllTextAsync(historyPath));
            Assert.Equal(13, Assert.Single(historical.Nodes)
                .FinalPreparationSnapshot!.Economy.Value);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletionArchiveMergesLastReliablePreparationFieldsPerNode()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var first = Analysis("run-merged-prep", "1-3", "preparation_generic");
            var evidence = new EvidenceReference(
                "fixture:first-preparation",
                "vision:formation",
                CapturedAt: first.Snapshot.AsOf,
                Confidence: 0.9);
            first = first with
            {
                AnalysisId = "analysis-preparation-first",
                Snapshot = first.Snapshot with
                {
                    EquipmentIds = Observation<IReadOnlyList<string>>.Known(
                        ["equipment-a"],
                        0.9,
                        [evidence],
                        first.Snapshot.AsOf)
                },
                OperationalState = first.OperationalState! with
                {
                    Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                        [
                            new FormationCharacterState(
                                FormationZone.Front,
                                0,
                                "character-a",
                                2,
                                "front",
                                ["equipment-a"],
                                0.9,
                                evidence)
                        ],
                        0.9,
                        [evidence],
                        first.Snapshot.AsOf)
                }
            };
            var secondTime = first.Snapshot.AsOf.AddSeconds(2);
            var second = Analysis(
                "run-merged-prep",
                "1-3",
                "preparation_generic") with
            {
                AnalysisId = "analysis-preparation-later-partial",
                Snapshot = first.Snapshot with
                {
                    AsOf = secondTime,
                    EquipmentIds = Observation<IReadOnlyList<string>>.Unknown(
                        "equipment region temporarily occluded")
                },
                OperationalState = first.OperationalState! with
                {
                    Formation = Observation<IReadOnlyList<FormationCharacterState>>.Unknown(
                        "formation region temporarily occluded")
                }
            };
            await store.SaveAnalysisAsync(first, CancellationToken.None);
            await store.SaveAnalysisAsync(second, CancellationToken.None);

            var archive = await store.CompleteRunAsync(
                "run-merged-prep",
                secondTime.AddSeconds(2),
                "challenge_failed",
                "1-3",
                "screenshots/final.png",
                "C",
                CancellationToken.None);

            var node = Assert.Single(archive.Nodes);
            Assert.Equal(
                ObservationStatus.Stale,
                node.FinalPreparationSnapshot!.EquipmentIds.Status);
            Assert.Equal(
                ["equipment-a"],
                node.FinalPreparationSnapshot.EquipmentIds.Value);
            Assert.Equal(
                "character-a",
                Assert.Single(node.FinalPreparationState!.Formation.Value!)
                    .CharacterId);
            Assert.Equal(
                ObservationStatus.Stale,
                node.FinalPreparationState.Formation.Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HealthDepletedFailureReportKeepsLastDamageAndUnknownHealthLoss()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var analysis = Analysis(
                "run-health-depleted",
                "2-5",
                "challenge_health_depleted");
            await store.SaveAnalysisAsync(analysis, CancellationToken.None);
            var evidence = new EvidenceReference(
                "fixture:health-depleted",
                "page:challenge-ended",
                CapturedAt: analysis.Snapshot.AsOf,
                Confidence: 0.95);
            await store.SaveFinalNodeBattleAsync(
                "run-health-depleted",
                new FinalNodeBattleState(
                    "2-5",
                    [],
                    736_000_000,
                    RemainingActionValueState.Create(0, 0),
                    analysis.Snapshot.AsOf,
                    evidence,
                    SelectedDamage: 736_000_000,
                    SelectedDamageSource: FinalDamageSelectionSource.BattleLastFrame,
                    HealthDelta: null,
                    ClearStatus: NodeClearStatus.NotPerfect,
                    TheoreticalDamageLimit: 736_000_000,
                    TheoreticalDamageQuality: TheoreticalDamageQuality.ActionExhausted,
                    HealthDepleted: true),
                CancellationToken.None);
            var archive = await store.CompleteRunAsync(
                "run-health-depleted",
                analysis.Snapshot.AsOf,
                "challenge_health_depleted",
                "2-5",
                "screenshots/final.png",
                null,
                CancellationToken.None);
            var reportPath = await new ChallengeSummaryReportGenerator()
                .GenerateAsync(store.GetRunDirectory(archive.RunId), archive, CancellationToken.None);
            var report = await File.ReadAllTextAsync(reportPath);

            Assert.Contains("2-5", report);
            Assert.Contains("7.36亿", report);
            Assert.Contains("已耗尽（具体变化未知）", report);
            Assert.Contains("✕ 未完美", report);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletedRunsListExcludesIncompleteAndReadsArchives()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);

            var completed = Analysis("run-completed", "2-4", "preparation_generic");
            await store.SaveAnalysisAsync(completed, CancellationToken.None);
            await store.CompleteRunAsync(
                "run-completed",
                completed.Snapshot.AsOf.AddMinutes(1),
                "challenge_success",
                "2-4",
                null,
                null,
                CancellationToken.None);

            // 仅创建断点的未完成对局不应出现在已完成列表。
            await store.SaveCheckpointAsync(
                new RunCheckpointRecord
                {
                    RunId = "run-incomplete",
                    CreatedAtUtc = completed.Snapshot.AsOf,
                    LastSavedAtUtc = completed.Snapshot.AsOf,
                    LastConfirmedNodeId = "1-1"
                },
                CancellationToken.None);

            var runs = await store.ListCompletedRunsAsync(CancellationToken.None);
            var record = Assert.Single(runs);
            Assert.Equal("run-completed", record.RunId);
            Assert.Equal("2-4", Assert.Single(record.Nodes).NodeId);
            Assert.Equal("challenge_success", record.CompletionPageId);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletionArchiveCarriesIdentityEvidenceFromCheckpoint()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var now = DateTimeOffset.Parse("2026-07-31T03:00:00+08:00");
            var checkpoint = new RunCheckpointRecord
            {
                RunId = "run-identity",
                CreatedAtUtc = now,
                LastSavedAtUtc = now,
                LastConfirmedNodeId = "1-1",
                IdentityEvidence = new RunIdentityEvidence
                {
                    InvestmentEnvironmentId = "environment-a",
                    EnemyIds = ["competitor-b", "competitor-c"],
                    EnemyAffixIds = ["affix-d"],
                    InvestmentStrategyIds = ["strategy-e"]
                }
            };
            await store.SaveCheckpointAsync(checkpoint, CancellationToken.None);

            var analysis = Analysis("run-identity", "1-1", "preparation_generic");
            await store.SaveAnalysisAsync(analysis, CancellationToken.None);
            var archive = await store.CompleteRunAsync(
                "run-identity",
                now.AddMinutes(1),
                "challenge_success",
                "1-1",
                null,
                null,
                CancellationToken.None);

            Assert.Equal("environment-a", archive.IdentityEvidence.InvestmentEnvironmentId);
            Assert.Equal(["competitor-b", "competitor-c"], archive.IdentityEvidence.EnemyIds);
            Assert.Equal(["affix-d"], archive.IdentityEvidence.EnemyAffixIds);
            Assert.Equal(["strategy-e"], archive.IdentityEvidence.InvestmentStrategyIds);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAnalysesAsyncReturnsSavedAnalysesInTimeOrder()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var early = Analysis("run-load", "1-1", "preparation_generic");
            var late = Analysis("run-load", "1-2", "preparation_generic") with
            {
                Snapshot = early.Snapshot with
                {
                    AsOf = early.Snapshot.AsOf.AddMinutes(1)
                }
            };
            await store.SaveAnalysisAsync(early, CancellationToken.None);
            await store.SaveAnalysisAsync(late, CancellationToken.None);

            var loaded = await store.LoadAnalysesAsync(
                "run-load",
                CancellationToken.None);

            Assert.Equal(2, loaded.Count);
            Assert.True(loaded[0].Snapshot.AsOf < loaded[1].Snapshot.AsOf);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SettlementContentPreventsHealthDepletedFinalization()
    {
        // 回归：1-9 通关结算页（"挑战结束"标题+金币总览+数据统计）被模板
        // challenge_ended_title 误判为生命耗尽终局页，截断对局导致 2-1 无记录。
        // 识别到结算内容时不得判定对局结束。
        var baseAnalysis = Analysis("run-s", "1-9", "challenge_health_depleted");
        var withSettlement = baseAnalysis with
        {
            OperationalState = baseAnalysis.OperationalState! with
            {
                PageId = "challenge_health_depleted",
                PageFamily = Phase2PageFamily.BattleSettlement,
                SettlementGoldReward = Observation<int>.Known(11, 0.9),
                SettlementScreenDamageCandidate = Observation<long>.Unknown(
                    "no damage rows")
            }
        };
        var withoutSettlement = baseAnalysis with
        {
            OperationalState = baseAnalysis.OperationalState! with
            {
                PageId = "challenge_health_depleted",
                PageFamily = Phase2PageFamily.BattleSettlement,
                SettlementGoldReward = Observation<int>.Unknown(
                    "终局页无结算金币"),
                SettlementScreenDamageCandidate = Observation<long>.Unknown(
                    "终局页无结算伤害")
            }
        };

        Assert.False(
            Phase2RunCompletionDetector.IsHealthDepletedRunPage(withSettlement));
        Assert.True(
            Phase2RunCompletionDetector.IsHealthDepletedRunPage(withoutSettlement));
    }

    [Fact]
    public async Task IncompleteRunsListExcludesEmptyObservationCheckpoints()
    {
        // 回归：刷开局阶段产生的空 run（无任何观测记录）不应出现在续玩
        // 列表——用户选到空断点会导致节点历史为空（0.2.776 实测丢失）。
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var now = DateTimeOffset.Parse("2026-08-01T10:00:00+08:00");
            await store.SaveCheckpointAsync(
                new RunCheckpointRecord
                {
                    RunId = "run-empty",
                    CreatedAtUtc = now,
                    LastSavedAtUtc = now,
                    SavedObservationCount = 0,
                    LastConfirmedNodeId = null
                },
                CancellationToken.None);
            var analysis = Analysis("run-real", "1-1", "preparation_generic");
            await store.SaveAnalysisAsync(analysis, CancellationToken.None);
            await store.SaveCheckpointAsync(
                new RunCheckpointRecord
                {
                    RunId = "run-real",
                    CreatedAtUtc = now,
                    LastSavedAtUtc = now,
                    SavedObservationCount = 1,
                    LastConfirmedNodeId = "1-1"
                },
                CancellationToken.None);

            var runs = await store.ListIncompleteRunsAsync(
                CancellationToken.None);
            var record = Assert.Single(runs);
            Assert.Equal("run-real", record.Checkpoint.RunId);
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
        string runId,
        string nodeId,
        string pageId)
    {
        var now = DateTimeOffset.Parse("2026-07-31T03:00:00+08:00");
        var pageFamily = pageId == "preparation_generic"
            ? Phase2PageFamily.Preparation
            : Phase2PageFamily.BattleSettlement;
        var semanticEvidence = pageId.StartsWith(
            "challenge_",
            StringComparison.Ordinal)
            ? new[]
            {
                new EvidenceReference(
                    $"fixture:{pageId}",
                    "ocr:settlement-semantic-layout",
                    "title + action + settlement layout",
                    now,
                    0.95)
            }
            : [];
        return new ScreenshotAnalysisResult
        {
            AnalysisId = $"analysis-{pageId}-{nodeId}",
            Snapshot = new RunSnapshot
            {
                RunId = runId,
                AsOf = now,
                PageId = Observation<string>.Known(
                    pageId,
                    0.95,
                    semanticEvidence,
                    observedAt: now),
                Stage = Observation<string>.Known(nodeId, 0.95, observedAt: now),
                Economy = Observation<int>.Known(42, 0.9, observedAt: now)
            },
            OperationalState = new Phase2OperationalState
            {
                PageFamily = pageFamily,
                PageId = pageId,
                NodeId = Observation<string>.Known(nodeId, 0.95, observedAt: now)
            }
        };
    }
}
