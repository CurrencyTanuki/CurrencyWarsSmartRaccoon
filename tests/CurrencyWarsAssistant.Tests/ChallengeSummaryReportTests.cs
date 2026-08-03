using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class ChallengeSummaryReportTests
{
    [Fact]
    public async Task OptionalRealRunSampleGeneration()
    {
        var source = Environment.GetEnvironmentVariable(
            "CWA_REPORT_SAMPLE_SOURCE");
        var outputRoot = Environment.GetEnvironmentVariable(
            "CWA_REPORT_SAMPLE_OUTPUT");
        if (string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(outputRoot) ||
            !Directory.Exists(source))
        {
            return;
        }

        var runId = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var target = Path.Combine(outputRoot, runId);
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFiles(source, "analysis-*.json"))
        {
            File.Copy(path, Path.Combine(target, Path.GetFileName(path)), overwrite: true);
        }

        var sourceNodes = Path.Combine(source, "nodes");
        if (Directory.Exists(sourceNodes))
        {
            var targetNodes = Path.Combine(target, "nodes");
            Directory.CreateDirectory(targetNodes);
            foreach (var path in Directory.EnumerateFiles(
                         sourceNodes,
                         "node-*-final.json"))
            {
                File.Copy(
                    path,
                    Path.Combine(targetNodes, Path.GetFileName(path)),
                    overwrite: true);
            }
        }

        var store = new LocalRunStore(outputRoot);
        var archive = await store.CompleteRunAsync(
            runId,
            DateTimeOffset.Parse("2026-07-31T01:10:00+08:00"),
            "preview_from_partial_real_run",
            "3-7",
            null,
            "真实记录派生预览",
            CancellationToken.None);
        var dataDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "data",
            "4.4");
        var catalog = Directory.Exists(dataDirectory)
            ? GameDataCatalogLoader.Load(dataDirectory)
            : null;
        var reportPath = await new ChallengeSummaryReportGenerator(catalog)
            .GenerateAsync(target, archive, CancellationToken.None);
        Assert.True(File.Exists(reportPath));
        Assert.Contains(
            "真实记录派生预览",
            await File.ReadAllTextAsync(reportPath));
    }

    [Fact]
    public async Task FullReportContainsEffectiveFieldsAndIsIdempotent()
    {
        var root = TemporaryDirectory();
        try
        {
            var run = CompleteRun();
            var generator = new ChallengeSummaryReportGenerator();
            var first = await generator.GenerateAsync(root, run, CancellationToken.None);
            var second = await generator.GenerateAsync(root, run, CancellationToken.None);

            Assert.Equal(first, second);
            Assert.Equal(2, Directory.EnumerateFiles(
                Path.Combine(root, "reports"),
                "challenge-summary.*").Count());
            var html = await File.ReadAllTextAsync(first);
            Assert.Contains("挑战评价", html);
            Assert.Contains("第一位面", html);
            Assert.Contains("节点精确数据", html);
            Assert.Contains("阵容、装备与构筑变化", html);
            Assert.Contains("最终伤害构成", html);
            Assert.Contains("1.8亿", html);
            Assert.Contains("180,000,000", html);
            Assert.Contains("character-alpha", html);
            Assert.Contains("equipment-alpha", html);
            Assert.Contains("synergy-alpha", html);
            Assert.Contains("environment-alpha", html);
            Assert.Contains("strategy-alpha", html);
            Assert.Contains("affix-alpha", html);
            Assert.Contains("special-alpha", html);
            Assert.Contains("advisor-alpha", html);
            Assert.Contains("✓ 完美", html);
            Assert.Contains("WalterEstimated", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedAndFutureFieldsDegradeWithoutLosingTheReport()
    {
        var root = TemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(root, "completed-run.v1.json");
            await File.WriteAllTextAsync(archivePath, """
            {
              "schemaVersion":"1.0.0",
              "archiveVersion":1,
              "runId":"run-malformed",
              "completedAt":"2026-07-31T12:00:00+08:00",
              "isFinal":true,
              "completionPageId":"challenge_success",
              "completionNodeId":"3-7",
              "lastSnapshot":"invalid-object",
              "nodes":[
                {
                  "nodeId":"3-7",
                  "finalPreparationSnapshot":{
                    "runId":"run-malformed",
                    "asOf":"2026-07-31T11:59:00+08:00",
                    "futurePreparation":{"quality":"future"}
                  },
                  "finalBattle":"invalid-battle",
                  "newNodeMetric":{"kind":"future","value":42}
                }
              ],
              "sourceAnalysisFiles":[],
              "uncertainty":[],
              "futureMetric":{"name":"future-root-field"}
            }
            """);

            var reportPath = await new ChallengeSummaryReportGenerator()
                .GenerateFromArchiveAsync(archivePath, CancellationToken.None);
            var html = await File.ReadAllTextAsync(reportPath);
            Assert.Contains("run-malformed", html);
            Assert.Contains("格式异常", html);
            Assert.Contains("futureMetric", html);
            Assert.Contains("nodes[3-7].newNodeMetric", html);
            Assert.Contains("nodes[3-7].finalPreparationSnapshot.futurePreparation", html);
            Assert.Contains("malformed.nodes[3-7]", html);
            Assert.Contains("未记录", html);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingCoverageNeverProducesANegativePerformanceJudgment()
    {
        var run = CompleteRun() with
        {
            Nodes = [CompleteRun().Nodes[0]]
        };
        var report = new ChallengeReportModelBuilder().Build(run);

        Assert.False(report.OverallEvaluation.HasEnoughData);
        Assert.Contains("数据不足", report.OverallEvaluation.Title);
        Assert.DoesNotContain("表现不佳", report.OverallEvaluation.Summary);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("mid-run")]
    [InlineData("missing-node")]
    [InlineData("missing-fields")]
    [InlineData("reward-special")]
    [InlineData("extreme-mixed")]
    public async Task ScenarioMatrixAlwaysProducesAnOfflineReport(string scenario)
    {
        var root = TemporaryDirectory();
        try
        {
            var baseline = CompleteRun();
            var run = scenario switch
            {
                "mid-run" => baseline with { Nodes = baseline.Nodes.Skip(2).ToArray() },
                "missing-node" => baseline with { Nodes = baseline.Nodes.Where(item => item.NodeId != "2-1").ToArray() },
                "missing-fields" => baseline with
                {
                    LastSnapshot = null,
                    Nodes = baseline.Nodes.Select(item => item with
                    {
                        FinalPreparationSnapshot = null,
                        FinalPreparationState = null
                    }).ToArray()
                },
                "reward-special" => baseline with
                {
                    Nodes = baseline.Nodes.Select((item, index) => index == 1
                        ? item with { FinalBattle = item.FinalBattle! with { IsRewardNode = true } }
                        : item).ToArray()
                },
                "extreme-mixed" => baseline with
                {
                    Nodes = baseline.Nodes.Select((item, index) => item with
                    {
                        FinalBattle = index switch
                        {
                            0 => item.FinalBattle! with
                            {
                                SelectedDamage = 1,
                                TotalDamage = 1
                            },
                            1 => null,
                            _ => item.FinalBattle! with
                            {
                                SelectedDamage = 9_000_000_000_000,
                                TotalDamage = 9_000_000_000_000,
                                ClearStatus = NodeClearStatus.NotPerfect
                            }
                        }
                    }).ToArray()
                },
                _ => baseline
            };

            var reportPath = await new ChallengeSummaryReportGenerator()
                .GenerateAsync(root, run, CancellationToken.None);
            var html = await File.ReadAllTextAsync(reportPath);
            Assert.Contains("货币战争 · 挑战总结", html);
            Assert.Contains("数据质量、证据与附录", html);
            Assert.DoesNotContain("NaN", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Infinity", html, StringComparison.OrdinalIgnoreCase);
            Assert.True(new FileInfo(reportPath).Length > 5_000);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CompletedRunRecord CompleteRun()
    {
        var at = DateTimeOffset.Parse("2026-07-31T12:00:00+08:00");
        var evidence = new EvidenceReference(
            "fixture:report",
            "fixture",
            "trusted fixture",
            at,
            0.95);
        var nodes = new List<CompletedRunNodeRecord>();
        foreach (var (nodeId, damage, gold, action, plane) in new[]
                 {
                     ("1-1", 100_000L, 30, 120, 1),
                     ("1-3", 2_000_000L, 25, 80, 1),
                     ("2-1", 50_000_000L, 40, 90, 2),
                     ("3-1", 180_000_000L, 12, 60, 3)
                 })
        {
            var formation = new[]
            {
                new FormationCharacterState(
                    FormationZone.Front,
                    0,
                    "character-alpha",
                    2,
                    "front",
                    ["equipment-alpha"],
                    0.95,
                    evidence)
            };
            var snapshot = new RunSnapshot
            {
                RunId = "run-complete",
                AsOf = at.AddMinutes(plane),
                Stage = Observation<string>.Known(nodeId, 0.95),
                Economy = Observation<int>.Known(gold, 0.95),
                CumulativeSpend = Observation<int>.Known(plane * 5, 0.9),
                Health = Observation<int>.Known(100 - plane, 0.9),
                InvestmentEnvironmentId = Observation<string>.Known("environment-alpha", 0.9),
                InvestmentStrategyIds = Observation<IReadOnlyList<string>>.Known(["strategy-alpha"], 0.9),
                SpecialItemIds = Observation<IReadOnlyList<string>>.Known(["special-alpha"], 0.9),
                ExpertAdvisorIds = Observation<IReadOnlyList<string>>.Known(["advisor-alpha"], 0.9)
            };
            var state = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Preparation,
                PageId = "preparation_generic",
                NodeId = Observation<string>.Known(nodeId, 0.95),
                EnemyDifficulty = Observation<int>.Known(100 + plane, 0.9),
                Interest = Observation<int>.Known(2, 0.9),
                CumulativeSpend = Observation<int>.Known(plane * 5, 0.9),
                PlayerProgress = Observation<PlayerProgressState>.Known(new PlayerProgressState(plane + 2, 2, 8), 0.9),
                Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(formation, 0.95),
                ActiveSynergies = Observation<IReadOnlyList<ActiveSynergyState>>.Known([
                    new ActiveSynergyState("synergy-alpha", 2, 4, "left-1", 0.9, evidence)
                ], 0.9),
                DismantleToolCount = Observation<int>.Known(1, 0.9),
                SimpleEquipmentIds = Observation<IReadOnlyList<string>>.Known(["simple-alpha"], 0.9),
                NegativeAffixIds = Observation<IReadOnlyList<string>>.Known(["affix-alpha"], 0.9),
                InvestmentEnvironmentId = Observation<string>.Known("environment-alpha", 0.9),
                InvestmentStrategyIds = Observation<IReadOnlyList<string>>.Known(["strategy-alpha"], 0.9)
            };
            var characterDamage = new CharacterDamageState(
                1,
                "character-alpha",
                damage,
                damage.ToString(),
                0.95,
                0.95,
                new RelativeRegion(0.8, 0.2, 0.04, 0.05),
                new RelativeRegion(0.85, 0.2, 0.08, 0.05),
                evidence);
            var synergyDamage = new SynergyDamageState(
                2,
                "synergy-alpha",
                damage / 10,
                (damage / 10).ToString(),
                0.9,
                0.9,
                new RelativeRegion(0.8, 0.3, 0.04, 0.05),
                new RelativeRegion(0.85, 0.3, 0.08, 0.05),
                evidence);
            var battle = new FinalNodeBattleState(
                nodeId,
                [characterDamage],
                damage,
                RemainingActionValueState.Create(action / 100, action % 100),
                at.AddMinutes(plane),
                evidence,
                SynergyDamage: [synergyDamage],
                BattleScreenDamageCandidate: damage,
                SettlementScreenDamageCandidate: damage - 1,
                SelectedDamage: damage,
                SelectedDamageSource: FinalDamageSelectionSource.BattleLastFrame,
                SettlementTopThree: [characterDamage],
                GoldReward: 9,
                PreBattleHealth: 100,
                PostBattleHealth: 100,
                HealthDelta: 0,
                ClearStatus: NodeClearStatus.Perfect,
                TheoreticalDamageLimit: damage * 2,
                BaseMaximumActionValue: plane == 1 ? 180 : plane == 2 ? 150 : 120,
                EffectiveMaximumActionValue: plane == 1 ? 180 : plane == 2 ? 150 : 120,
                TheoreticalDamageQuality: plane == 3
                    ? TheoreticalDamageQuality.WalterEstimated
                    : TheoreticalDamageQuality.Exact,
                TheoreticalDamageRule: plane == 3 ? "WalterEstimated" : "Linear",
                IsRewardNode: nodeId == "1-3");
            nodes.Add(new CompletedRunNodeRecord(
                nodeId,
                snapshot,
                state,
                battle,
                $"analysis-{nodeId}.json",
                $"nodes/node-{nodeId}-final.json"));
        }

        return new CompletedRunRecord
        {
            RunId = "run-complete",
            CompletedAt = at,
            IsFinal = true,
            CompletionPageId = "challenge_success",
            CompletionNodeId = "3-7",
            CompletionScreenshotFile = "screenshots/final.png",
            RatingText = "SSS",
            LastSnapshot = nodes[^1].FinalPreparationSnapshot,
            LastOperationalState = nodes[^1].FinalPreparationState,
            Nodes = nodes,
            SourceAnalysisFiles = nodes.Select(item => item.PreparationAnalysisFile!).ToArray()
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
