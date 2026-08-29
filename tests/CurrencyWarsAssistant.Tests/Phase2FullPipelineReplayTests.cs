using System.Diagnostics;
using System.Globalization;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 阶段 2：全管线功能回放（E5 门禁第 5 步"整局功能回放"）。
/// 用 run-20260814-143614 的真实截图按捕获时间顺序驱动生产各层：
/// 页面分类器 → 帧选择器 → 分析器（真实 OCR/模板/图标识别）→ tracker
/// → checkpoint 工厂 → 历史投影，不允许绕过任何一层构造最终对象。
///
/// 断言：1-1（奖励关）被封存且无伪造伤害；1-2 仍封存且顺序正确；
/// 投影可见节点与 checkpoint 封存列表一致。
/// 同时输出每层耗时统计作为阶段 3 性能优化的基线数据。
///
/// 截图含玩家 UID（隐私），不固化进 Git 仓库；本测试用本机 run 目录
/// （存在性门控：目录缺失时跳过）。可用环境变量
/// CWA_PHASE2_FULL_REPLAY_RUN 覆盖截图目录。
/// </summary>
public sealed class Phase2FullPipelineReplayTests(ITestOutputHelper output)
{
    private static readonly string DefaultScreenshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CurrencyWarsSmartRaccoon",
        "runs",
        "run-20260814-143614",
        "screenshots");

    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Fact]
    public async Task FullPipelineReplayFinalizesRewardNodeOneOneAndProjectsConsistently()
    {
        var runDir = Environment.GetEnvironmentVariable(
            "CWA_PHASE2_FULL_REPLAY_RUN");
        var screenshotDir = string.IsNullOrWhiteSpace(runDir)
            ? DefaultScreenshotDir
            : Path.Combine(runDir, "screenshots");
        if (!Directory.Exists(screenshotDir))
        {
            output.WriteLine(
                $"Screenshot directory missing ({screenshotDir}); full pipeline replay skipped.");
            return;
        }

        var files = Directory.EnumerateFiles(screenshotDir, "*.png")
            .OrderBy(ParseCapturedAt)
            .ToArray();
        Assert.NotEmpty(files);
        output.WriteLine($"Replaying {files.Length} frames from {screenshotDir}");

        // 生产配置与各层对象（与 LiveCollectionService 相同的类型与配置）。
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        var classifier = new Phase2FastPageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
        var selector = new Phase2RealtimeFrameSelector();
        using var characterRecognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds:
            [
                "currency_wars_character_05",
            ]);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        // 角色模板对"节点封存"断言无影响（节点号来自 OCR）；此处不加载
        // 角色卡片模板，阵容字段可能为 Unknown——阵容正确性由红测 A 与
        // 阶段 5 字段夹具验证，本回放聚焦封存与投影链路。
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            [],
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr(),
            gameData,
            new WindowsOfflineOcr("en-US"),
            pageClassifier: new TemplateGamePageClassifier(
                new OpenCvTemplateMatcher(),
                config.Pages));

        var tracker = new Phase2OperationalStateTracker();
        var projection = new HistoricalDashboardProjection();
        var runId = $"replay-{Guid.NewGuid():N}";
        var checkpoint = RunCheckpointFactory.CreateInitial(
            runId,
            RunEntryMode.DirectRecording,
            DateTimeOffset.UtcNow);
        var savedCount = 0;
        var lastKnownPage = Phase2PageFamily.Unknown;
        var finalizedNodes = new List<string>();
        var finalBattles = new Dictionary<string, FinalNodeBattleState>(
            StringComparer.OrdinalIgnoreCase);
        var classifyTimes = new List<double>(files.Length);
        var analyzeTimes = new List<double>();
        var commitTimes = new List<double>();

        foreach (var path in files)
        {
            var frame = CaptureFrameLoader.LoadFile(path) with
            {
                CapturedAt = ParseCapturedAt(path)
            };

            // 层 1：页面分类（生产分类器）
            var classifyStopwatch = Stopwatch.StartNew();
            var hint = classifier.Classify(frame);
            classifyStopwatch.Stop();
            classifyTimes.Add(classifyStopwatch.Elapsed.TotalMilliseconds);

            // 层 2：帧选择（生产选帧器）
            var selection = selector.Observe(
                frame,
                wasReliable: hint.IsMatched,
                lastKnownPage,
                hint);
            if (hint.PageFamily != Phase2PageFamily.Unknown)
            {
                lastKnownPage = hint.PageFamily;
            }

            foreach (var item in selection.FramesToRecognize)
            {
                var buffered = item.BufferedFrame.Frame;

                // 层 3：分析（真实 OCR/模板/图标识别）
                var analyzeStopwatch = Stopwatch.StartNew();
                var state = await analyzer.AnalyzeAsync(
                    buffered,
                    hint.PageId ?? "unknown",
                    $"replay:{Path.GetFileName(path)}",
                    EmptySnapshot(runId, buffered.CapturedAt),
                    CancellationToken.None);
                analyzeStopwatch.Stop();
                analyzeTimes.Add(analyzeStopwatch.Elapsed.TotalMilliseconds);

                // 层 4-6：tracker → FinalBattle 注入 → checkpoint/投影
                //（组装逻辑与 Phase2LiveCollectionService 一致）
                var commitStopwatch = Stopwatch.StartNew();
                var analysis = new ScreenshotAnalysisResult
                {
                    AnalysisId = $"replay:{Path.GetFileName(path)}",
                    Snapshot = EmptySnapshot(runId, buffered.CapturedAt),
                    OperationalState = state
                };
                var tracking = tracker.Observe(state, analysis.Snapshot.Health);
                analysis = analysis with
                {
                    OperationalState = tracking.Current
                };
                if (tracking.FinalizedBattle is { } finalized)
                {
                    if (!finalizedNodes.Contains(finalized.NodeId))
                    {
                        finalizedNodes.Add(finalized.NodeId);
                        finalBattles[finalized.NodeId] = finalized;
                    }

                    analysis = analysis with
                    {
                        OperationalState = tracking.Current with
                        {
                            FinalBattle = finalized.IsComplete
                                ? Observation<FinalNodeBattleState>.Known(
                                    finalized,
                                    0.85,
                                    [finalized.Evidence],
                                    finalized.CapturedAt)
                                : new Observation<FinalNodeBattleState>
                                {
                                    Status = ObservationStatus.Unknown,
                                    Value = finalized,
                                    Confidence = 0,
                                    Evidence = [finalized.Evidence],
                                    Uncertainty = finalized.FinalUncertainty.Count > 0
                                        ? finalized.FinalUncertainty
                                        : ["最终战斗帧包含残缺对象；仅供复盘，不能驱动高风险决策。"],
                                    ObservedAt = finalized.CapturedAt
                                }
                        }
                    };
                }

                savedCount++;
                checkpoint = RunCheckpointFactory.FromAnalysis(
                    checkpoint,
                    analysis,
                    savedCount,
                    RunCheckpointLifecycleStatus.Active,
                    DateTimeOffset.UtcNow);
                projection.Observe(runId, analysis);
                commitStopwatch.Stop();
                commitTimes.Add(commitStopwatch.Elapsed.TotalMilliseconds);
            }
        }

        output.WriteLine(
            $"Finalized nodes: [{string.Join(", ", finalizedNodes)}]");
        output.WriteLine(
            $"Checkpoint finalizedNodeIds: [{string.Join(", ", checkpoint.FinalizedNodeIds)}]");

        // 断言 1：1-1 奖励关必须封存（不再从历史消失）
        Assert.Contains("1-1", finalizedNodes);

        // 断言 2：1-2（有真实结算证据）仍封存，且顺序 1-1 先于 1-2
        Assert.Contains("1-2", finalizedNodes);
        var i1 = finalizedNodes.IndexOf("1-1");
        var i2 = finalizedNodes.IndexOf("1-2");
        Assert.True(i1 < i2,
            $"unexpected finalization order: [{string.Join(", ", finalizedNodes)}]");

        // 断言 3：1-1 无伪造伤害（奖励关降级封存不得编造数值）
        var oneOne = finalBattles["1-1"];
        Assert.True(
            oneOne.SelectedDamage is null || !oneOne.CanDriveDecisions,
            "1-1 奖励关不得伪造战斗伤害");

        // 断言 4：投影可见节点与 checkpoint 封存列表一致（主历史只显示封存节点）
        var projectedNodes = projection.Current.Nodes
            .Select(node => node.NodeId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var checkpointNodes = checkpoint.FinalizedNodeIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(checkpointNodes, projectedNodes);

        // 耗时报告（阶段 3 性能基线数据；本阶段不做硬断言）
        output.WriteLine(
            $"classify ms: {MetricsText(classifyTimes)}");
        output.WriteLine(
            $"analyze ms: {MetricsText(analyzeTimes)}");
        output.WriteLine(
            $"tracker+checkpoint+projection ms: {MetricsText(commitTimes)}");
    }

    private static RunSnapshot EmptySnapshot(
        string runId,
        DateTimeOffset capturedAt) => new()
    {
        RunId = runId,
        AsOf = capturedAt
    };

    private static DateTimeOffset ParseCapturedAt(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        // 生产截图文件名可能带 -heartbeat/-reset 等后缀，先剥离。
        if (stem.EndsWith("-heartbeat", StringComparison.Ordinal))
        {
            stem = stem[..^"-heartbeat".Length];
        }

        return DateTimeOffset.ParseExact(
            stem,
            "yyyyMMdd-HHmmssfff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
    }

    private static string MetricsText(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return "n/a";
        }

        var ordered = values.Order().ToArray();
        return $"n={values.Count} avg={ordered.Average():F0} " +
               $"P50={Percentile(ordered, 0.5):F0} " +
               $"P95={Percentile(ordered, 0.95):F0} " +
               $"max={ordered[^1]:F0}";
    }

    private static double Percentile(IReadOnlyList<double> values, double p) =>
        values[(int)Math.Floor((values.Count - 1) * p)];
}
