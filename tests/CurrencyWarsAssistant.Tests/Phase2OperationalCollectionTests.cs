using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2OperationalCollectionTests
{
    [Theory]
    [InlineData("preparation_1_2", "1-2")]
    [InlineData("preparation_1-9", "1-9")]
    [InlineData("preparation_2_1", "2-1")]
    [InlineData("preparation_3_9", "3-9")]
    [InlineData("preparation_4_1", null)] // 位面越界（OCR 把 1 读成 4）
    [InlineData("preparation_1_0", null)] // 节点越界（OCR 把 2 读成 0）
    [InlineData("preparation_0_1", null)]
    [InlineData("preparation_9_9", null)]
    [InlineData("preparation_1_10", null)]
    [InlineData("preparation_1_2_3", null)] // 多余段
    [InlineData("battle_1_2", null)] // 非备战前缀
    public void PreparationNodeFromPageIdRejectsOutOfRangeNodes(
        string pageId,
        string? expected)
    {
        // 货币战争节点为 1~3 位面、每面 1~9 节点；OCR 混淆产生的越界
        // 节点号必须被拒绝，避免污染对局归档（如 2-8 挑战失败实为 1-2）。
        Assert.Equal(
            expected,
            CurrencyWarsAssistant.Tasks.Phase2OperationalScreenshotAnalyzer
                .PreparationNodeFromPageId(pageId));
    }

    private readonly ITestOutputHelper _output;

    public Phase2OperationalCollectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Theory]
    [InlineData("enemy_overview", true)]
    [InlineData("investment_environment", true)]
    [InlineData("investment_strategy", true)]
    [InlineData("preparation_1_1", false)]
    [InlineData("reward_battle", false)]
    public void OneTimeOpeningPagesDoNotEnterTheOperationalRecognitionLoop(
        string pageId,
        bool expected)
    {
        Assert.Equal(
            expected,
            CurrencyWarsSituationScreenshotAnalyzer.IsOneTimeSelectionPage(pageId));
    }

    [Fact]
    public void StableInvestmentStrategyPageRemainsKnownWithoutOperationalState()
    {
        var frame = LoadLiveCapture("investment-strategy-selection-stable.png");
        var classified = CreatePageClassifier().Classify(frame);

        Assert.NotNull(classified);
        Assert.Equal("investment_strategy", classified!.PageId);
        var analysis = new ScreenshotAnalysisResult
        {
            AnalysisId = "known-selection-page",
            Snapshot = EmptySnapshot(frame.CapturedAt) with
            {
                PageId = Observation<string>.Known(
                    classified.PageId,
                    classified.Confidence,
                    observedAt: frame.CapturedAt)
            },
            OperationalState = null
        };

        Assert.True(Phase2PageRecognition.IsKnown(analysis));
        Assert.False(Phase2PageRecognition.IsKnown(analysis with
        {
            Snapshot = EmptySnapshot(frame.CapturedAt)
        }));
    }

    [Theory]
    [InlineData("125924.png", Phase2PageFamily.Preparation)]
    [InlineData("132307.png", Phase2PageFamily.Preparation)]
    [InlineData("130104.png", Phase2PageFamily.Battle)]
    [InlineData("130123.png", Phase2PageFamily.Battle)]
    [InlineData("130112.png", Phase2PageFamily.Battle)]
    [InlineData("132328.png", Phase2PageFamily.Battle)]
    public async Task RealReferenceFramesAreClassifiedIntoExpectedPageFamily(
        string fileName,
        Phase2PageFamily expectedPage)
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            LoadCharacterTemplates(dataDirectory, gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr(),
            gameData,
            new WindowsOfflineOcr("en-US"));
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-2026-07-28",
            fileName));

        var result = await analyzer.DetectPageFamilyAsync(
            frame,
            "unknown",
            CancellationToken.None);

        Assert.Equal(expectedPage, result);
    }

    [Theory]
    [InlineData("battle-1-6-early.png", Phase2PageFamily.Battle)]
    [InlineData("battle-1-6-late.png", Phase2PageFamily.Battle)]
    [InlineData("battle-1-7.png", Phase2PageFamily.Battle)]
    [InlineData("battle-1-3-action-indicator-mid.png", Phase2PageFamily.Battle)]
    [InlineData("preparation-1-7-user.png", Phase2PageFamily.Preparation)]
    [InlineData("preparation-1-2-blank-board.png", Phase2PageFamily.Preparation)]
    public async Task LiveCapturedFramesUseTheProductionFastPagePath(
        string fileName,
        Phase2PageFamily expectedPage)
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateRealtimeAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadLiveCapture(fileName);
        var diagnosticOcr = new WindowsOfflineOcr(
            "zh-Hans",
            OfflineOcrRecognitionMode.Fast,
            maximumConcurrency: 1);
        var panelText = await diagnosticOcr.RecognizeAsync(
            frame,
            Phase2RecognitionRegions.BattleDamagePanel.ToPixels(
                frame.Width,
                frame.Height),
            CancellationToken.None);
        _output.WriteLine($"battle-panel OCR: {panelText.Text}");
        var classifier = Assert.IsAssignableFrom<IGamePageClassifierDiagnostics>(
            CreatePageClassifier());
        ((IGamePageClassifier)classifier).Classify(frame);
        foreach (var item in classifier.LastDiagnostics
                     .Where(item => item.PageId is
                         "battle_generic" or
                         "preparation_generic" or
                         "preparation_1_1" or
                         "preparation_1_2" or
                         "reward_shop")
                     .OrderByDescending(item => item.Confidence))
        {
            _output.WriteLine(
                $"{item.PageId}/{item.AnchorId}: " +
                $"{item.Confidence:F3}/{item.Threshold:F3}");
        }
        var stopwatch = Stopwatch.StartNew();

        var result = await analyzer.DetectPageFamilyAsync(
            frame,
            "unknown",
            CancellationToken.None);

        stopwatch.Stop();
        _output.WriteLine(
            $"{fileName}: {result}, {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(expectedPage, result);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Fast page recognition took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Theory]
    [InlineData("run-171955-preparation-1-3-stable.png", Phase2PageFamily.Preparation, "1-3")]
    [InlineData("run-171955-battle-1-3.png", Phase2PageFamily.Battle, "1-3")]
    public async Task Run171955CoreFramesSeparatePageFamilyFromNodeIdentity(
        string fileName,
        Phase2PageFamily expectedFamily,
        string expectedNode)
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateRealtimeAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadLiveCapture(fileName);
        var classifier = Assert.IsAssignableFrom<IGamePageClassifierDiagnostics>(
            CreatePageClassifier());
        ((IGamePageClassifier)classifier).Classify(frame);
        foreach (var item in classifier.LastDiagnostics
                     .OrderByDescending(item => item.Confidence)
                     .Take(8))
        {
            _output.WriteLine(
                $"anchor={item.AnchorId}; page={item.PageId}; " +
                $"score={item.Confidence:F3}; threshold={item.Threshold:F3}");
        }
        var diagnosticOcr = new WindowsOfflineOcr(
            "zh-Hans",
            OfflineOcrRecognitionMode.Fast,
            maximumConcurrency: 1);
        var diagnosticRegions = await Task.WhenAll(
            diagnosticOcr.RecognizeAsync(
                    frame,
                    Phase2RecognitionRegions.BattleNodeValue.ToPixels(
                        frame.Width,
                        frame.Height),
                    CancellationToken.None)
                .AsTask(),
            diagnosticOcr.RecognizeAsync(
                    frame,
                    Phase2RecognitionRegions.BattleDamagePanel.ToPixels(
                        frame.Width,
                        frame.Height),
                    CancellationToken.None)
                .AsTask(),
            diagnosticOcr.RecognizeAsync(
                    frame,
                    new NormalizedRect(0.345, 0.000, 0.075, 0.075).ToPixels(
                        frame.Width,
                        frame.Height),
                    CancellationToken.None)
                .AsTask(),
            diagnosticOcr.RecognizeAsync(
                    frame,
                    new NormalizedRect(0.800, 0.055, 0.195, 0.130).ToPixels(
                        frame.Width,
                        frame.Height),
                    CancellationToken.None)
                .AsTask(),
            diagnosticOcr.RecognizeAsync(
                    frame,
                    new NormalizedRect(0.830, 0.820, 0.165, 0.170).ToPixels(
                        frame.Width,
                        frame.Height),
                    CancellationToken.None)
                .AsTask());

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            $"fixture:{fileName}",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        _output.WriteLine(
            $"{fileName}: family={state.PageFamily}; " +
            $"node={state.NodeId.Status}/{state.NodeId.Value}; " +
            $"page={state.PageId}; nodeOcr={diagnosticRegions[0].Text}; " +
            $"panelOcr={diagnosticRegions[1].Text}; " +
            $"wideNode={diagnosticRegions[2].Text}; " +
            $"header={diagnosticRegions[3].Text}; " +
            $"action={diagnosticRegions[4].Text}");
        Assert.Equal(expectedFamily, state.PageFamily);
        Assert.Equal(ObservationStatus.Known, state.NodeId.Status);
        Assert.Equal(expectedNode, state.NodeId.Value);
    }

    [Fact]
    public async Task Run171955UnknownAnimationIsDiscardableWithoutHidingStablePages()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateRealtimeAnalyzer(characterRecognizer, iconRecognizer);
        var transitionFrame = LoadLiveCapture(
            "run-171955-preparation-entry-transition.png");
        var rawTransition = new ScreenshotAnalysisResult
        {
            AnalysisId = "transition-fixture",
            Snapshot = EmptySnapshot(transitionFrame.CapturedAt),
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Unknown,
                PageId = "unknown"
            }
        };
        var bufferedTransition = new Phase2BufferedFrame(
            1,
            transitionFrame,
            Phase2RealtimeFrameBuffer.CreateSignature(transitionFrame),
            Phase2FrameChangeKind.SceneTransition,
            IsReliable: false);

        var marked = Phase2TransitionFramePolicy.MarkIfApplicable(
            rawTransition,
            bufferedTransition);

        Assert.Equal(
            Phase2PageFamily.Transition,
            marked.OperationalState!.PageFamily);
        Assert.Equal("transition_animation", marked.Snapshot.PageId.Value);
        Assert.False(Phase2PageRecognition.IsKnown(marked));
        Assert.True(Phase2TransitionFramePolicy.ShouldDiscard(marked));

        var stableFrame = LoadLiveCapture("run-171955-preparation-1-3-stable.png");
        var stableState = await analyzer.AnalyzeAsync(
            stableFrame,
            "unknown",
            "fixture:stable-preparation",
            EmptySnapshot(stableFrame.CapturedAt),
            CancellationToken.None);
        var stable = new ScreenshotAnalysisResult
        {
            AnalysisId = "stable-preparation-fixture",
            Snapshot = EmptySnapshot(stableFrame.CapturedAt),
            OperationalState = stableState
        };
        var bufferedStable = new Phase2BufferedFrame(
            2,
            stableFrame,
            Phase2RealtimeFrameBuffer.CreateSignature(stableFrame),
            Phase2FrameChangeKind.SceneTransition,
            IsReliable: false);

        var preserved = Phase2TransitionFramePolicy.MarkIfApplicable(
            stable,
            bufferedStable);

        Assert.Equal(Phase2PageFamily.Preparation, preserved.OperationalState!.PageFamily);
        Assert.False(Phase2TransitionFramePolicy.ShouldDiscard(preserved));

        var settlement = new ScreenshotAnalysisResult
        {
            AnalysisId = "settlement-animation-fixture",
            Snapshot = EmptySnapshot(transitionFrame.CapturedAt) with
            {
                PageId = Observation<string>.Known(
                    "challenge_success",
                    0.92,
                    observedAt: transitionFrame.CapturedAt)
            },
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.BattleSettlement,
                PageId = "challenge_success"
            }
        };
        var preservedSettlement = Phase2TransitionFramePolicy.MarkIfApplicable(
            settlement,
            bufferedTransition);

        Assert.Equal(
            Phase2PageFamily.BattleSettlement,
            preservedSettlement.OperationalState!.PageFamily);
        Assert.False(Phase2TransitionFramePolicy.ShouldDiscard(
            preservedSettlement));
    }

    [Theory]
    [InlineData("battle-1-6-early.png", "1-6", 180, 7_805_000L)]
    [InlineData("battle-1-6-late.png", "1-6", 152, 26_911_000L)]
    [InlineData("battle-1-7.png", "1-7", 180, 521_000L)]
    [InlineData("battle-1-3-action-indicator-mid.png", "1-3", 109, 18_000L)]
    [InlineData("battle-1-3-action-indicator-round-zero.png", "1-3", 95, 5_060_000L)]
    [InlineData("battle-1-3-action-value-leading-digit.png", "1-3", 76, 5_086_000L)]
    [InlineData("battle-1-4-action-row-obscured.png", "1-4", null, 16_335_000L)]
    public async Task LiveCapturedBattleFramesProduceCoreStateWithinRealtimeBudget(
        string fileName,
        string expectedNode,
        int? expectedActionValue,
        long expectedDamage)
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateRealtimeAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadLiveCapture(fileName);
        var snapshot = EmptySnapshot(frame.CapturedAt) with
        {
            RunId = $"live-capture-{fileName}"
        };
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));
        var indicators = Phase2ActionIndicatorLocator.LocateCandidates(
            frame,
            templates);
        _output.WriteLine(indicators.Count == 0
            ? "action indicator: missing"
            : "action indicators: " + string.Join(
                " | ",
                indicators.Select(indicator =>
                    $"{indicator.TemplateId}@{indicator.Confidence:F3}," +
                    $"{indicator.Region}")));
        await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            $"fixture:warm:{fileName}",
            snapshot,
            CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            $"fixture:measure:{fileName}",
            snapshot,
            CancellationToken.None);

        stopwatch.Stop();
        _output.WriteLine(
            $"{fileName}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
            $"node={state.NodeId.Status}/{state.NodeId.Value}; " +
            $"action={state.RemainingActionValue.Status}/" +
            $"{state.RemainingActionValue.Value?.TotalActionValue}; " +
            $"damage={state.BattleScreenDamageCandidate.Status}/" +
            $"{state.BattleScreenDamageCandidate.Value}");
        _output.WriteLine(
            "action evidence: " + string.Join(
                " | ",
                state.RemainingActionValue.Evidence.Select(item => item.Summary)));
        _output.WriteLine(
            "node evidence: " + string.Join(
                " | ",
                state.NodeId.Evidence.Select(item => item.Summary)));
        _output.WriteLine(
            "damage uncertainty: " + string.Join(
                " | ",
                state.BattleScreenDamageCandidate.Uncertainty));
        Assert.Equal(Phase2PageFamily.Battle, state.PageFamily);
        Assert.Equal(ObservationStatus.Known, state.NodeId.Status);
        Assert.Equal(expectedNode, state.NodeId.Value);
        if (expectedActionValue is not null)
        {
            Assert.Equal(ObservationStatus.Known, state.RemainingActionValue.Status);
            Assert.Equal(
                expectedActionValue,
                state.RemainingActionValue.Value!.TotalActionValue);
        }
        else
        {
            Assert.Equal(ObservationStatus.Unknown, state.RemainingActionValue.Status);
            Assert.NotEmpty(state.RemainingActionValue.Evidence);
            Assert.NotEmpty(state.RemainingActionValue.Uncertainty);
        }
        Assert.Equal(expectedDamage, state.BattleScreenDamageCandidate.Value);
        Assert.NotEmpty(state.BattleScreenDamageCandidate.Evidence);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Battle recognition took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task ProductionOcrSeparatesRoundZeroFromTwoDigitActionValues()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        using var ppOcr = new PpOcrOfflineOcr(
            Path.Combine(
                RepositoryRoot,
                "data",
                "ocr",
                "rapidocr",
                "PP-OCRv6_rec_small.onnx"),
            maximumConcurrency: 4);
        var textOcr = new ConfidenceFallbackOfflineOcr(
            ppOcr,
            new WindowsOfflineOcr(
                "zh-Hans",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 4));
        var numericOcr = new ConfidenceFallbackOfflineOcr(
            ppOcr,
            new WindowsOfflineOcr(
                "en-US",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 4));
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            LoadCharacterTemplates(dataDirectory, gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            textOcr,
            gameData,
            numericOcr,
            pageClassifier: CreatePageClassifier(),
            enableRobustFallback: false);
        var samples = new[]
        {
            (File: "battle-action-value-72-round-zero.png", Expected: 72),
            (File: "battle-action-value-15-round-zero.png", Expected: 15),
            (File: "battle-action-value-87-round-zero.png", Expected: 87),
            (File: "battle-action-value-79-round-zero.png", Expected: 79)
        };

        foreach (var sample in samples)
        {
            var frame = LoadLiveCapture(sample.File);
            var state = await analyzer.AnalyzeAsync(
                frame,
                "battle_generic",
                $"fixture:{sample.File}",
                EmptySnapshot(frame.CapturedAt),
                CancellationToken.None);

            Assert.Equal(
                ObservationStatus.Known,
                state.RemainingActionValue.Status);
            Assert.Equal(
                sample.Expected,
                state.RemainingActionValue.Value!.TotalActionValue);
            Assert.Equal(0, state.RemainingActionValue.Value.RemainingRounds);
        }

        foreach (var fileName in new[]
                 {
                     "battle-no-action-indicator-wave-counter-1.png",
                     "battle-no-action-indicator-wave-counter-2.png",
                     "battle-no-action-indicator-wave-counter-3.png"
                 })
        {
            var frame = LoadLiveCapture(fileName);
            var state = await analyzer.AnalyzeAsync(
                frame,
                "battle_generic",
                $"fixture:{fileName}",
                EmptySnapshot(frame.CapturedAt),
                CancellationToken.None);

            Assert.Equal(
                ObservationStatus.Unknown,
                state.RemainingActionValue.Status);
            Assert.Null(state.RemainingActionValue.Value);
            Assert.NotEmpty(state.RemainingActionValue.Evidence);
        }
    }

    [Fact]
    public void GlowingActionValueUsesLocalizedDigitTemplatesWithoutRelaxingOcr()
    {
        var frame = LoadLiveCapture("battle-1-7.png");
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));
        var recognizer = new OpenCvUiDigitSequenceRecognizer();

        var result = recognizer.Recognize(
            frame,
            new PixelRect(145, 1005, 102, 65),
            templates,
            0,
            100);

        _output.WriteLine(
            $"value={result.Value}; confidence={result.Confidence:F3}; " +
            $"runner-up={result.RunnerUpConfidence:F3}; " +
            $"reason={result.FailureReason}; glyphs=" +
            string.Join(
                " | ",
                result.Glyphs.Select(item =>
                    $"{item.Digit}@{item.Confidence:F3}/" +
                    $"{item.RunnerUpConfidence:F3}:{item.Region}")));
        Assert.True(result.IsRecognized, result.FailureReason);
        Assert.Equal(80, result.Value);
    }

    [Fact]
    public async Task LiveCapturedPreparationProducesVisibleCoreStateWithinRealtimeBudget()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateRealtimeAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadLiveCapture("preparation-1-7-user.png");
        var snapshot = EmptySnapshot(frame.CapturedAt) with
        {
            RunId = "live-capture-preparation-1-7"
        };
        await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            "fixture:warm:preparation-1-7",
            snapshot,
            CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            "fixture:measure:preparation-1-7",
            snapshot,
            CancellationToken.None);

        stopwatch.Stop();
        _output.WriteLine(
            $"preparation-1-7: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; " +
            $"node={state.NodeId.Status}/{state.NodeId.Value}; " +
            $"difficulty={state.EnemyDifficulty.Status}/{state.EnemyDifficulty.Value}; " +
            $"interest={state.Interest.Status}/{state.Interest.Value}; " +
            $"spend={state.CumulativeSpend.Status}/{state.CumulativeSpend.Value}");
        _output.WriteLine(
            "difficulty evidence: " + string.Join(
                " | ",
                state.EnemyDifficulty.Evidence.Select(item => item.Summary)));
        _output.WriteLine(
            "interest evidence: " + string.Join(
                " | ",
                state.Interest.Evidence.Select(item => item.Summary)));
        _output.WriteLine(
            "spend evidence: " + string.Join(
                " | ",
                state.CumulativeSpend.Evidence.Select(item => item.Summary)));
        foreach (var diagnostic in state.Diagnostics.Where(item =>
                     item.StartsWith("perf:", StringComparison.Ordinal)))
        {
            _output.WriteLine(diagnostic);
        }
        Assert.Equal(Phase2PageFamily.Preparation, state.PageFamily);
        Assert.Equal("1-7", state.NodeId.Value);
        Assert.Equal(126, state.EnemyDifficulty.Value);
        Assert.Equal(3, state.Interest.Value);
        Assert.Equal(2, state.CumulativeSpend.Value);
        Assert.Equal(2, state.DismantleToolCount.Value);
        Assert.Equal(ObservationStatus.Known, state.PlayerProgress.Status);
        Assert.Equal(new PlayerProgressState(5, 4, 20), state.PlayerProgress.Value);
        Assert.Equal(ObservationStatus.Known, state.Formation.Status);
        var formation = state.Formation.Value!;
        foreach (var item in formation)
        {
            _output.WriteLine(
                $"{item.Zone}[{item.SlotIndex}]={item.CharacterId}; " +
                string.Join(", ", item.FinalEquipmentSlots.Select(slot =>
                    $"slot{slot.SlotIndex}:{slot.Occupancy}/" +
                    $"{slot.EquipmentId ?? "-"}/{slot.Confidence:F3}/" +
                    $"[{string.Join('|', slot.CandidateEquipmentIds)}]")));
        }
        _output.WriteLine("inventory: " + string.Join(", ",
            (state.InventorySlots.Value ?? []).Select(slot =>
                $"slot{slot.SlotIndex}:{slot.Occupancy}/{slot.ItemKind}/" +
                $"{slot.ItemId ?? "-"}/{slot.Confidence:F3}/" +
                $"[{string.Join('|', slot.CandidateItemIds)}]")));
        Assert.Equal(
            new[]
            {
                (FormationZone.Front, 0, "currency_wars_character_24", 1),
                (FormationZone.Front, 1, "currency_wars_character_21", 1),
                (FormationZone.Front, 2, "currency_wars_character_59", 3),
                (FormationZone.Front, 3, "currency_wars_character_47", 1),
                (FormationZone.Back, 6, "currency_wars_character_02", 1),
                (FormationZone.Bench, 1, "currency_wars_character_39", 1),
                (FormationZone.Bench, 2, "currency_wars_character_23", 1)
            },
            formation.Select(item =>
                (item.Zone,
                    item.SlotIndex,
                    item.CharacterId,
                    item.StarLevel!.Value)));
        Assert.All(formation, item => Assert.NotNull(item.CardRegion));
        var equippedOwner = Assert.Single(formation.Where(item =>
            item.CharacterId == "currency_wars_character_24"));
        Assert.Equal(
            EquipmentSlotOccupancy.Empty,
            equippedOwner.FinalEquipmentSlots[0].Occupancy);
        Assert.Equal(
            EquipmentSlotOccupancy.Unknown,
            equippedOwner.FinalEquipmentSlots[1].Occupancy);
        Assert.Equal(
            new[]
            {
                "currency_wars_equipment_061",
                "currency_wars_equipment_100"
            },
            equippedOwner.FinalEquipmentSlots[1].CandidateEquipmentIds);
        Assert.Equal(
            EquipmentSlotOccupancy.Empty,
            equippedOwner.FinalEquipmentSlots[2].Occupancy);
        Assert.All(
            formation.Where(item => item.CharacterId != equippedOwner.CharacterId),
            item => Assert.All(item.FinalEquipmentSlots, slot =>
                Assert.Equal(EquipmentSlotOccupancy.Empty, slot.Occupancy)));
        Assert.Equal(ObservationStatus.Known, state.InventorySlots.Status);
        Assert.Equal(
            new[]
            {
                (0, EquipmentSlotOccupancy.Equipped,
                    InventoryItemKind.SimpleEquipment,
                    "currency_wars_equipment_044"),
                (1, EquipmentSlotOccupancy.Equipped,
                    InventoryItemKind.SimpleEquipment,
                    "currency_wars_equipment_045"),
                (2, EquipmentSlotOccupancy.Empty,
                    InventoryItemKind.Unknown,
                    (string?)null),
                (3, EquipmentSlotOccupancy.Empty,
                    InventoryItemKind.Unknown,
                    (string?)null)
            },
            state.InventorySlots.Value!.Select(item => (
                item.SlotIndex,
                item.Occupancy,
                item.ItemKind,
                item.ItemId)));
        var persistedFormation = JsonSerializer.Deserialize<Phase2OperationalState>(
            JsonSerializer.Serialize(state, AdvisorJson.Options),
            AdvisorJson.Options)!.Formation.Value!;
        Assert.Equal(
            formation.Select(item =>
                (item.Zone,
                    item.SlotIndex,
                    item.CharacterId,
                    item.StarLevel!.Value)),
            persistedFormation.Select(item =>
                (item.Zone,
                    item.SlotIndex,
                    item.CharacterId,
                    item.StarLevel!.Value)));
        Assert.All(
            new[]
            {
                state.EnemyDifficulty.Status,
                state.Interest.Status,
                state.CumulativeSpend.Status,
                state.DismantleToolCount.Status
            },
            status => Assert.Equal(ObservationStatus.Known, status));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Preparation recognition took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public void LocalizedUiDigitsReadLivePreparationResourcesWithoutGeneralOcr()
    {
        var frame = LoadLiveCapture("preparation-1-7-user.png");
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));
        var recognizer = new OpenCvUiDigitSequenceRecognizer();
        (string Name, NormalizedRect Region, int Minimum, int Maximum, int Expected,
            UiDigitForegroundStyle Style)[]
            cases =
            [
                ("difficulty", Phase2RecognitionRegions.PreparationDifficultyDigits,
                    100, 999, 126, UiDigitForegroundStyle.BrightOnDark),
                ("tools", Phase2RecognitionRegions.DismantleToolCountValue,
                    0, 99, 2, UiDigitForegroundStyle.BrightOnDark),
                ("spend", Phase2RecognitionRegions.CumulativeSpend,
                    0, 9999, 2, UiDigitForegroundStyle.BrightOnDark),
                ("interest", Phase2RecognitionRegions.InterestValue,
                    0, 5, 3, UiDigitForegroundStyle.GoldSaturated),
                ("economy", Phase2RecognitionRegions.EconomyValue,
                    0, 999, 32, UiDigitForegroundStyle.DarkOnLight)
            ];

        var failures = new List<string>();
        foreach (var test in cases)
        {
            var result = recognizer.Recognize(
                frame,
                test.Region.ToPixels(frame.Width, frame.Height),
                templates,
                test.Minimum,
                test.Maximum,
                test.Style);
            _output.WriteLine(
                $"{test.Name}: value={result.Value}; " +
                $"confidence={result.Confidence:F3}; " +
                $"runner-up={result.RunnerUpConfidence:F3}; " +
                $"reason={result.FailureReason}; glyphs=" +
                string.Join(",", result.Glyphs.Select(glyph =>
                    $"{glyph.Digit}@{glyph.Region}")));
            if (!result.IsRecognized || result.Value != test.Expected)
            {
                failures.Add(
                    $"{test.Name}: expected={test.Expected}, value={result.Value}, " +
                    $"reason={result.FailureReason}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task LivePreparationInventoryPreservesDuplicateSimpleItemsAndEmptySlot()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateRealtimeAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadLiveCapture("preparation-1-4-user-2026-08-01.png");

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:inventory:preparation-1-4-user-2026-08-01",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(ObservationStatus.Known, state.InventorySlots.Status);
        Assert.Equal(
            new[]
            {
                (0, EquipmentSlotOccupancy.Equipped,
                    "currency_wars_equipment_041"),
                (1, EquipmentSlotOccupancy.Equipped,
                    "currency_wars_equipment_041"),
                (2, EquipmentSlotOccupancy.Equipped,
                    "currency_wars_equipment_040"),
                (3, EquipmentSlotOccupancy.Empty, (string?)null)
            },
            state.InventorySlots.Value!.Select(item => (
                item.SlotIndex,
                item.Occupancy,
                item.ItemId)));
        Assert.Equal(
            new[]
            {
                "currency_wars_equipment_041",
                "currency_wars_equipment_041",
                "currency_wars_equipment_040"
            },
            state.SimpleEquipmentIds.Value);
        Assert.Empty(state.SpecialItemIds.Value!);
    }

    [Fact]
    public async Task PreparationSnapshotReadsAbsoluteHealthAndGoldFromTightRegions()
    {
        var frame = LoadLiveCapture("preparation-1-7-user.png");
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var phase2Templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        using var primaryNumericOcr = new PpOcrOfflineOcr(Path.Combine(
            dataDirectory,
            "ocr",
            "rapidocr",
            "PP-OCRv6_rec_small.onnx"));
        var textOcr = new WindowsOfflineOcr(
            "zh-Hans",
            OfflineOcrRecognitionMode.Fast,
            maximumConcurrency: 4);
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            new FixedPageClassifier("preparation_generic"),
            new EmptyCharacterRecognizer(),
            [],
            new FixedGoldDigitRecognizer(3),
            [],
            new OcrOpeningPageReader(textOcr, gameData),
            new RewardShopReader(textOcr, gameData),
            textOcr,
            gameData,
            new GuideRepository(),
            new AdvisorEngine(),
            Path.Combine(dataDirectory, "advisor", "1.0.0", "4.4", "guides"),
            numericOcr: new ConfidenceFallbackOfflineOcr(
                primaryNumericOcr,
                new WindowsOfflineOcr(
                    "en-US",
                    OfflineOcrRecognitionMode.Fast,
                    maximumConcurrency: 2)),
            phase2IconTemplates: phase2Templates);

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:preparation-absolute-values",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        _output.WriteLine(
            $"health={result.Snapshot.Health.Status}/{result.Snapshot.Health.Value}; " +
            $"uncertainty={string.Join(" | ", result.Snapshot.Health.Uncertainty)}; " +
            $"evidence={string.Join(" | ", result.Snapshot.Health.Evidence.Select(item => item.Summary))}");
        _output.WriteLine(
            $"economy={result.Snapshot.Economy.Status}/{result.Snapshot.Economy.Value}; " +
            $"uncertainty={string.Join(" | ", result.Snapshot.Economy.Uncertainty)}; " +
            $"evidence={string.Join(" | ", result.Snapshot.Economy.Evidence.Select(item => item.Summary))}");
        Assert.Equal(ObservationStatus.Known, result.Snapshot.Health.Status);
        Assert.Equal(84, result.Snapshot.Health.Value);
        Assert.Equal(ObservationStatus.Known, result.Snapshot.Economy.Status);
        Assert.Equal(32, result.Snapshot.Economy.Value);
    }

    [Fact]
    public async Task ExpandedShopEconomyUsesTightEvidenceInsteadOfAdjacentZero()
    {
        var frame = LoadLiveCapture("preparation-shop-1-4.png");
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var phase2Templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var localizedProbe = new OpenCvUiDigitSequenceRecognizer().Recognize(
            frame,
            Phase2RecognitionRegions.EconomyValue.ToPixels(
                frame.Width,
                frame.Height),
            phase2Templates,
            0,
            999,
            UiDigitForegroundStyle.BrightOnDark);
        var interestProbe = new OpenCvUiDigitSequenceRecognizer().Recognize(
            frame,
            Phase2RecognitionRegions.InterestValue.ToPixels(
                frame.Width,
                frame.Height),
            phase2Templates,
            0,
            5,
            UiDigitForegroundStyle.DarkOnLight);
        _output.WriteLine(
            $"localized={localizedProbe.Value}; confidence={localizedProbe.Confidence:F3}; " +
            $"runner-up={localizedProbe.RunnerUpConfidence:F3}; " +
            localizedProbe.FailureReason + "; glyphs=" +
            string.Join(",", localizedProbe.Glyphs.Select(item =>
                $"{item.Digit}:{item.Region}")));
        _output.WriteLine(
            $"interest={interestProbe.Value}; confidence={interestProbe.Confidence:F3}; " +
            $"runner-up={interestProbe.RunnerUpConfidence:F3}; " +
            interestProbe.FailureReason);
        Assert.True(interestProbe.IsRecognized, interestProbe.FailureReason);
        Assert.Equal(1, interestProbe.Value);
        var ocr = new WindowsOfflineOcr(
            "zh-Hans",
            OfflineOcrRecognitionMode.Fast,
            maximumConcurrency: 4);
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            new FixedPageClassifier("preparation_generic"),
            new EmptyCharacterRecognizer(),
            [],
            new EmptyGoldDigitRecognizer(),
            [],
            new OcrOpeningPageReader(ocr, gameData),
            new RewardShopReader(ocr, gameData),
            ocr,
            gameData,
            new GuideRepository(),
            new AdvisorEngine(),
            Path.Combine(
                RepositoryRoot,
                "data",
                "advisor",
                "1.0.0",
                "4.4",
                "guides"),
            numericOcr: new WindowsOfflineOcr(
                "en-US",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 2),
            phase2IconTemplates: phase2Templates);

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:expanded-shop-economy",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        _output.WriteLine(
            $"shop economy={result.Snapshot.Economy.Value}; " +
            $"status={result.Snapshot.Economy.Status}; " +
            $"evidence={string.Join(" | ", result.Snapshot.Economy.Evidence.Select(item => item.Summary))}");
        Assert.Equal(ObservationStatus.Known, result.Snapshot.Economy.Status);
        Assert.Equal(6, result.Snapshot.Economy.Value);
    }

    [Fact]
    public async Task PreparationEconomyKeepsLeadingDigitAtShopCardBoundary()
    {
        var frame = LoadLiveCapture("preparation-1-3-gold-23-user.png");
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var phase2Templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var ocr = new WindowsOfflineOcr(
            "zh-Hans",
            OfflineOcrRecognitionMode.Fast,
            maximumConcurrency: 4);
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            new FixedPageClassifier("preparation_generic"),
            new EmptyCharacterRecognizer(),
            [],
            new EmptyGoldDigitRecognizer(),
            [],
            new OcrOpeningPageReader(ocr, gameData),
            new RewardShopReader(ocr, gameData),
            ocr,
            gameData,
            new GuideRepository(),
            new AdvisorEngine(),
            Path.Combine(
                dataDirectory,
                "advisor",
                "1.0.0",
                "4.4",
                "guides"),
            numericOcr: new WindowsOfflineOcr(
                "en-US",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 2),
            phase2IconTemplates: phase2Templates);

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:preparation-1-3-gold-23-user",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        _output.WriteLine(
            $"economy={result.Snapshot.Economy.Status}/" +
            $"{result.Snapshot.Economy.Value}; evidence=" +
            string.Join(" | ", result.Snapshot.Economy.Evidence.Select(item =>
                $"{item.Locator}:{item.Summary}")));
        Assert.Equal(ObservationStatus.Known, result.Snapshot.Economy.Status);
        Assert.Equal(23, result.Snapshot.Economy.Value);
    }

    [Fact]
    public async Task SettlementPageClassificationReplay()
    {
        // 真实帧回放：4 种结算页的分类判定（用户 2026-08-01 提供截图）。
        // ①② 对局结束（挑战失败/生命耗尽）；③④ 挑战成功（③为动画中）。
        // ③ 动画中帧不得被判为对局结束页——它没有金币总览/数据统计，
        // 0.2.768 的"结算内容排除"对它无效，必须靠分类层正确区分。
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var phase2Templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var ocr = new WindowsOfflineOcr(
            "zh-Hans",
            OfflineOcrRecognitionMode.Fast,
            maximumConcurrency: 4);
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(),
            new EmptyCharacterRecognizer(),
            [],
            new EmptyGoldDigitRecognizer(),
            [],
            new OcrOpeningPageReader(ocr, gameData),
            new RewardShopReader(ocr, gameData),
            ocr,
            gameData,
            new GuideRepository(),
            new AdvisorEngine(),
            Path.Combine(dataDirectory, "advisor", "1.0.0", "4.4", "guides"),
            numericOcr: new WindowsOfflineOcr("en-US", OfflineOcrRecognitionMode.Fast, maximumConcurrency: 2),
            phase2IconTemplates: phase2Templates);

        var samples = new[]
        {
            new SettlementReplayCase(
                "settlement-failed-c.png",
                "challenge_failed",
                IsWholeRunCompletion: true),
            new SettlementReplayCase(
                "settlement-health-depleted.png",
                "challenge_health_depleted",
                IsWholeRunCompletion: false),
            new SettlementReplayCase(
                "settlement-success-anim.png",
                "challenge_success",
                IsWholeRunCompletion: false),
            new SettlementReplayCase(
                "settlement-success-final.png",
                "challenge_success",
                IsWholeRunCompletion: false),
            new SettlementReplayCase(
                "settlement-final-failure-positive-health.png",
                "challenge_failed",
                IsWholeRunCompletion: true),
            new SettlementReplayCase(
                "settlement-failure-transition.png",
                "challenge_health_depleted",
                IsWholeRunCompletion: false),
            new SettlementReplayCase(
                "settlement-success-animation-current.png",
                "challenge_success",
                IsWholeRunCompletion: false),
            new SettlementReplayCase(
                "settlement-success-details-current.png",
                "challenge_success",
                IsWholeRunCompletion: false),
            new SettlementReplayCase(
                "run-final-success-rating.png",
                "challenge_success",
                IsWholeRunCompletion: true,
                TrackedNodeId: "3-7")
        };
        foreach (var sample in samples)
        {
            var f = PadToExact16By9(LoadLiveCapture(sample.FileName));
            var result = await analyzer.AnalyzeAsync(
                f,
                $"fixture:{sample.FileName}",
                new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
                CancellationToken.None);
            _output.WriteLine(
                $"{sample.FileName}: pageId={result.Snapshot.PageId.Status}/" +
                $"{result.Snapshot.PageId.Value} " +
                $"conf={result.Snapshot.PageId.Confidence:F3} " +
                $"family={result.OperationalState?.PageFamily}");

            var pageId = result.Snapshot.PageId.Status == ObservationStatus.Known
                ? result.Snapshot.PageId.Value
                : result.OperationalState?.PageId;
            Assert.Equal(sample.ExpectedPageId, pageId);
            Assert.Equal(
                sample.IsWholeRunCompletion,
                Phase2RunCompletionDetector.IsCompletedRunPage(
                    result,
                    trackedNodeId: sample.TrackedNodeId));
        }

        var partiallyOccludedFailure = ApplyOpaqueOcclusion(
            PadToExact16By9(LoadLiveCapture(
                "settlement-final-failure-positive-health.png")),
            new NormalizedRect(0.66, 0.34, 0.24, 0.40));
        var occludedResult = await analyzer.AnalyzeAsync(
            partiallyOccludedFailure,
            "fixture:settlement-final-failure-positive-health:partial-occlusion",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);
        Assert.Equal(
            "challenge_failed",
            occludedResult.Snapshot.PageId.Value);
        Assert.True(
            Phase2RunCompletionDetector.IsCompletedRunPage(
                occludedResult,
                trackedNodeId: "2-5"));

        var preparation = await analyzer.AnalyzeAsync(
            LoadLiveCapture("preparation-1-4-user-2026-08-01.png"),
            "fixture:ordinary-preparation-negative-control",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);
        Assert.False(
            Phase2RunCompletionDetector.IsCompletedRunPage(
                preparation,
                trackedNodeId: "1-3"));
    }

    [Fact]
    public async Task PreparationHealthIsReadFromTopProgressBarRightEnd()
    {
        // 回归：0.2.765 前备战血量区域错位（偏左/偏上），即使备战停留很久
        // 血量也识别失败，导致节点血量Δ缺失。真实用户截图验证新区域。
        var frame = LoadLiveCapture("preparation-1-4-user-2026-08-01.png");
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var phase2Templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var ocr = new WindowsOfflineOcr(
            "zh-Hans",
            OfflineOcrRecognitionMode.Fast,
            maximumConcurrency: 4);
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            new FixedPageClassifier("preparation_generic"),
            new EmptyCharacterRecognizer(),
            [],
            new EmptyGoldDigitRecognizer(),
            [],
            new OcrOpeningPageReader(ocr, gameData),
            new RewardShopReader(ocr, gameData),
            ocr,
            gameData,
            new GuideRepository(),
            new AdvisorEngine(),
            Path.Combine(
                dataDirectory,
                "advisor",
                "1.0.0",
                "4.4",
                "guides"),
            numericOcr: new WindowsOfflineOcr(
                "en-US",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 2),
            phase2IconTemplates: phase2Templates);

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:preparation-1-4-user-2026-08-01",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        _output.WriteLine(
            $"health={result.Snapshot.Health.Status}/" +
            $"{result.Snapshot.Health.Value}; evidence=" +
            string.Join(" | ", result.Snapshot.Health.Evidence.Select(item =>
                $"{item.Locator}:{item.Summary}")));
        Assert.Equal(ObservationStatus.Known, result.Snapshot.Health.Status);
        Assert.Equal(86, result.Snapshot.Health.Value);
    }

    [Fact]
    public void ExpandedEconomyCropDoesNotPrependCoinIconToSingleDigitGold()
    {
        var frame = LoadPageReplay(
            "preparation_privilege_boxes_gold3_2559x1439.png");
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));
        var recognizer = new OpenCvUiDigitSequenceRecognizer();
        var wide = recognizer.Recognize(
            frame,
            Phase2RecognitionRegions.EconomyValue.ToPixels(
                frame.Width,
                frame.Height),
            templates,
            0,
            999,
            UiDigitForegroundStyle.DarkOnLight);
        var narrow = recognizer.Recognize(
            frame,
            Phase2RecognitionRegions.EconomyValueNarrow.ToPixels(
                frame.Width,
                frame.Height),
            templates,
            0,
            999,
            UiDigitForegroundStyle.DarkOnLight);

        Assert.Equal(3, wide.Value);
        Assert.Equal(3, narrow.Value);
    }

    [Fact]
    public void ExpandedShopUsesConfiguredFastPageAnchor()
    {
        var frame = LoadLiveCapture("preparation-shop-1-4.png");
        var classifier = CreatePageClassifier();

        var result = classifier.Classify(frame);
        var diagnostics = Assert.IsAssignableFrom<IGamePageClassifierDiagnostics>(
                classifier)
            .LastDiagnostics
            .Where(item => item.PageId is "reward_shop" or "preparation_generic")
            .OrderByDescending(item => item.Confidence)
            .ToArray();
        _output.WriteLine(string.Join(
            " | ",
            diagnostics.Select(item =>
                $"{item.PageId}/{item.AnchorId}={item.Confidence:F3}/" +
                $"{item.Threshold:F3}")));

        Assert.NotNull(result);
        Assert.Equal("reward_shop", result!.PageId);
    }

    [Fact]
    public void ExpandedShopFormationUsesTheCompactReferenceLayout()
    {
        var frame = LoadLiveCapture("preparation-shop-1-4.png");
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var recognizer = new OpenCvCharacterCardRecognizer();

        var result = recognizer.Recognize(
            frame,
            LoadCharacterTemplates(dataDirectory, gameData),
            Phase2RecognitionRegions.RewardShopCharacterSlots1920,
            CharacterCardRecognitionOptions.RewardShopCompact);
        foreach (var slot in result)
        {
            _output.WriteLine(
                $"shop slot {slot.SlotIndex}: {slot.State}; " +
                $"id={slot.CharacterId}; score={slot.Confidence:F3}; " +
                $"runner-up={slot.RunnerUpCharacterId}/" +
                $"{slot.RunnerUpConfidence:F3}; bounds={slot.ReferenceBounds}");
        }

        Assert.Equal(10, result.Count);
        Assert.True(
            result.Count(item => item.State == CharacterCardSlotState.Recognized) >= 4,
            "The expanded shop fixture contains three front units and one back unit.");
    }

    [Fact]
    public async Task ExpandedShopAnalyzerKeepsVisibleFormationWithCompactEvidenceRegions()
    {
        var frame = LoadLiveCapture("preparation-shop-1-4.png");
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateRealtimeAnalyzer(characterRecognizer, iconRecognizer);

        var state = await analyzer.AnalyzeAsync(
            frame,
            "reward_shop",
            "fixture:expanded-shop-formation",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Preparation, state.PageFamily);
        Assert.Contains(
            state.Formation.Status,
            new[] { ObservationStatus.Known, ObservationStatus.Unknown });
        var formation = state.Formation.Value!;
        foreach (var item in formation)
        {
            _output.WriteLine(
                $"{item.Zone}[{item.SlotIndex}]={item.CharacterId}; " +
                string.Join(", ", item.FinalEquipmentSlots.Select(slot =>
                    $"slot{slot.SlotIndex}:{slot.Occupancy}/" +
                    $"{slot.EquipmentId ?? "-"}/{slot.Confidence:F3}/" +
                    $"[{string.Join('|', slot.CandidateEquipmentIds)}]")));
        }
        _output.WriteLine("inventory: " + string.Join(", ",
            (state.InventorySlots.Value ?? []).Select(slot =>
                $"slot{slot.SlotIndex}:{slot.Occupancy}/{slot.ItemKind}/" +
                $"{slot.ItemId ?? "-"}/{slot.Confidence:F3}/" +
                $"[{string.Join('|', slot.CandidateItemIds)}]")));
        var equippedOwner = formation.First(item =>
            item.CharacterId == "currency_wars_character_44");
        Assert.Equal(ObservationStatus.Known, state.InventorySlots.Status);
        Assert.Equal(
            new[]
            {
                (0, InventoryItemKind.SimpleEquipment,
                    (string?)"currency_wars_equipment_044"),
                (1, InventoryItemKind.SimpleEquipment,
                    (string?)"currency_wars_equipment_045"),
                (2, InventoryItemKind.SimpleEquipment,
                    (string?)"currency_wars_equipment_041"),
                (3, InventoryItemKind.AdvancedEquipment,
                    (string?)"currency_wars_equipment_123")
            },
            state.InventorySlots.Value!.Select(item => (
                item.SlotIndex,
                item.ItemKind,
                item.ItemId)));
        Assert.All(state.InventorySlots.Value!, item =>
            Assert.Equal(EquipmentSlotOccupancy.Equipped, item.Occupancy));
        Assert.Equal(
            EquipmentSlotOccupancy.Empty,
            equippedOwner.FinalEquipmentSlots[0].Occupancy);
        Assert.Equal(
            EquipmentSlotOccupancy.Unknown,
            equippedOwner.FinalEquipmentSlots[1].Occupancy);
        Assert.Equal(
            new[]
            {
                "currency_wars_equipment_066",
                "currency_wars_equipment_105"
            },
            equippedOwner.FinalEquipmentSlots[1].CandidateEquipmentIds);
        Assert.Equal(
            EquipmentSlotOccupancy.Empty,
            equippedOwner.FinalEquipmentSlots[2].Occupancy);
        Assert.Contains(formation, item =>
            item.CharacterId == "currency_wars_character_44" &&
            item.Zone == FormationZone.Front);
        Assert.Contains(formation, item =>
            item.CharacterId == "currency_wars_character_43" &&
            item.Zone == FormationZone.Back);
        Assert.All(formation, item => Assert.NotNull(item.CardRegion));
        Assert.All(formation, item =>
            Assert.Equal(3, item.FinalEquipmentSlots.Count));
    }

    [Fact]
    public async Task UnknownFormationEvidenceUsesReferenceSpaceAtAnyResolution()
    {
        var referenceSlot = new PixelRect(681, 329, 128, 140);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new SingleUncertainCharacterRecognizer(referenceSlot),
            [],
            new EmptyIconRecognizer(),
            [],
            new StaticTextOcr(string.Empty),
            GameDataCatalogLoader.Load(Path.Combine(RepositoryRoot, "data", "4.4")),
            new StaticTextOcr(string.Empty));
        var frame = EmptyFrame(2560, 1440);

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:formation-reference-space",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var pending = Assert.Single(state.PendingIcons.Where(item =>
            item.Category == PendingIconCategory.CharacterAvatar));
        Assert.Equal(referenceSlot.X / 1920d, pending.Region.X, 8);
        Assert.Equal(referenceSlot.Y / 1080d, pending.Region.Y, 8);
        var character = Assert.Single(state.Formation.Value!);
        Assert.Equal(pending.Region, character.CardRegion);
    }

    [Fact]
    public async Task KnownSpecialFormationUnitIsPreservedAsNonDecisionEvidence()
    {
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new SingleSpecialCharacterRecognizer(),
            [],
            new EmptyIconRecognizer(),
            [],
            new StaticTextOcr(string.Empty),
            GameDataCatalogLoader.Load(Path.Combine(RepositoryRoot, "data", "4.4")),
            new StaticTextOcr(string.Empty));
        var frame = EmptyFrame(1920, 1080);

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:special-formation-unit",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var character = Assert.Single(state.Formation.Value!);
        Assert.False(character.CanDriveDecisions);
        Assert.StartsWith("special-formation-unit-", character.TemporaryId);
        Assert.Contains(
            "bench_special_privilege_armament_box",
            character.CandidateCharacterIds!);
        var pending = Assert.Single(state.PendingIcons.Where(item =>
            item.TemporaryId == character.TemporaryId));
        Assert.Equal("special-unit-recognized", pending.Status);
    }

    [Fact]
    public async Task MultiDigitNodeSuffixIsRejectedInsteadOfPersistingImpossibleNode()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            new EmptyIconRecognizer(),
            [],
            new StaticTextOcr("1-40"),
            gameData,
            new StaticTextOcr("1-40"));
        var frame = EmptyFrame(1920, 1080);

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:impossible-node",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(ObservationStatus.Unknown, state.NodeId.Status);
        Assert.Null(state.NodeId.Value);
    }

    [Fact]
    public async Task SecondaryPreparationOcrEvidenceUsesReadableChineseLabels()
    {
        var frame = EmptyFrame(1920, 1080);
        var secondaryRegion = new NormalizedRect(0.430, 0.190, 0.220, 0.180)
            .ToPixels(frame.Width, frame.Height);
        var ocr = new RegionSelectiveOcr(secondaryRegion, "前台区域");
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            new EmptyIconRecognizer(),
            [],
            ocr,
            GameDataCatalogLoader.Load(Path.Combine(RepositoryRoot, "data", "4.4")),
            ocr);

        var result = await analyzer.DetectPageFamilyAsync(
            frame,
            "unknown",
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Preparation, result);
    }

    [Fact]
    public async Task MainPageWithDiagnosticOverlayIsNotMisclassifiedAsBattle()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadLiveCapture("main_page_classifier_miss_2048x1152.png");

        var page = await analyzer.DetectPageFamilyAsync(
            frame,
            "unknown",
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Main, page);

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            "fixture:overlayed-home",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Main, state.PageFamily);
        Assert.Empty(state.PartialFields);
    }

    [Fact]
    public async Task RealSettlementReferenceExposesGoldAndTopThreeDamage()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadPageReplay("challenge_success_1_1.jpg");

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            "fixture:challenge-success-1-1",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.BattleSettlement, state.PageFamily);
        Assert.Equal(ObservationStatus.Known, state.SettlementGoldReward.Status);
        Assert.Equal(4, state.SettlementGoldReward.Value);
        Assert.Equal(ObservationStatus.Known, state.SettlementScreenDamageCandidate.Status);
        Assert.Equal(212_000, state.SettlementScreenDamageCandidate.Value);
        Assert.Equal(new long[] { 136_000L, 76_000L, 0L },
            state.SettlementDamage.Value!.Select(item => item.Damage).ToArray());
    }

    [Fact]
    public async Task SettlementRewardUsesObtainedGoldOverviewInsteadOfBreakdownRow()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadPageReplay("challenge_success_1_4_gold8_user.png");

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            "fixture:challenge-success-1-4-gold8-user",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        _output.WriteLine(
            $"settlement gold={state.SettlementGoldReward.Status}/" +
            $"{state.SettlementGoldReward.Value}; evidence=" +
            string.Join(" | ", state.SettlementGoldReward.Evidence.Select(item =>
                $"{item.Locator}:{item.Summary}")));
        Assert.Equal(Phase2PageFamily.BattleSettlement, state.PageFamily);
        Assert.Equal(ObservationStatus.Known, state.SettlementGoldReward.Status);
        Assert.Equal(8, state.SettlementGoldReward.Value);
    }

    [Fact]
    public async Task RunStableContentLeavesTheThreeSecondHotPathAfterConfirmation()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var iconRecognizer = new LifecycleCountingIconRecognizer(
            gameData.InvestmentEnvironments[0].Id,
            gameData.InvestmentStrategies.Take(2).Select(item => item.Id).ToArray());
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            iconRecognizer,
            [],
            new StaticTextOcr(string.Empty),
            gameData,
            new StaticTextOcr(string.Empty));
        const string runId = "lifecycle-run";
        var affixIds = gameData.EnemyAffixes.Take(4)
            .Select(item => item.Id)
            .ToArray();
        analyzer.ObserveOpeningEnemyIds(
            runId,
            new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = affixIds,
                Confidence = 0.6,
                Uncertainty = ["competitor identity unresolved"],
                ObservedAt = DateTimeOffset.UtcNow
            });
        var frame = EmptyFrame(1920, 1080);
        var snapshot = EmptySnapshot(frame.CapturedAt) with { RunId = runId };

        var first = await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "test:first",
            snapshot,
            CancellationToken.None);
        var second = await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "test:second",
            snapshot,
            CancellationToken.None);

        Assert.Equal(affixIds, first.NegativeAffixIds.Value);
        Assert.Equal(affixIds, second.NegativeAffixIds.Value);
        Assert.Equal(0, iconRecognizer.Count("negative-affix"));
        Assert.Equal(1, iconRecognizer.Count("investment-environment"));
        Assert.Equal(1, iconRecognizer.Count("investment-strategy"));
        Assert.Contains(second.Diagnostics, item =>
            item.Contains("未重复识别", StringComparison.Ordinal));

        analyzer.NotifyPageObserved(runId, "investment_strategy");
        var afterSelection = await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "test:after-selection",
            snapshot,
            CancellationToken.None);

        Assert.Equal(2, iconRecognizer.Count("investment-strategy"));
        Assert.Equal(
            gameData.InvestmentStrategies.Take(2)
                .Select(item => item.Id)
                .Order(StringComparer.Ordinal),
            afterSelection.InvestmentStrategyIds.Value!
                .Order(StringComparer.Ordinal));
        var statistics = analyzer.GetStableRecognitionStatistics(runId);
        Assert.True(statistics.NegativeAffixesFinal);
        Assert.True(statistics.EnvironmentFinal);
        Assert.Equal(2, statistics.StrategyCount);
    }

    [Fact]
    public async Task UnresolvedStableRegionsUseABoundedBudgetAndRecoverAfterVisualChange()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            new ThrowingIconRecognizer(),
            [],
            new StaticTextOcr(string.Empty),
            gameData,
            new StaticTextOcr(string.Empty));
        const string runId = "bounded-stable-retry";
        var frame = EmptyFrame(1920, 1080);
        var snapshot = EmptySnapshot(frame.CapturedAt) with { RunId = runId };

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await analyzer.AnalyzeAsync(
                frame,
                "preparation_1_1",
                $"test:bounded:{attempt}",
                snapshot,
                CancellationToken.None);
        }

        var exhausted = analyzer.GetStableRecognitionStatistics(runId);
        Assert.Equal(3, exhausted.NegativeAffixRecognitionCount);
        Assert.Equal(3, exhausted.EnvironmentRecognitionCount);
        Assert.Equal(1, exhausted.StrategyRecognitionCount);

        var changed = FrameWithChangedStableRegions(1920, 1080);
        await analyzer.AnalyzeAsync(
            changed,
            "preparation_1_1",
            "test:recovered-visual",
            snapshot,
            CancellationToken.None);

        var recovered = analyzer.GetStableRecognitionStatistics(runId);
        Assert.Equal(4, recovered.NegativeAffixRecognitionCount);
        Assert.Equal(4, recovered.EnvironmentRecognitionCount);
        Assert.Equal(2, recovered.StrategyRecognitionCount);
    }

    [Fact]
    public async Task RepeatedPreparationFramesMeetTheTwoSecondRealtimeBudget()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            LoadCharacterTemplates(dataDirectory, gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr(
                "zh-Hans",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 4),
            gameData,
            new WindowsOfflineOcr(
                "en-US",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 4),
            enableRobustFallback: false);
        var frame = LoadReference("132307.png");
        var snapshot = EmptySnapshot(frame.CapturedAt) with
        {
            RunId = "realtime-performance-run"
        };

        await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "test:warm-up",
            snapshot,
            CancellationToken.None);

        var elapsed = new List<TimeSpan>();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            await analyzer.AnalyzeAsync(
                frame,
                "preparation_1_1",
                $"test:realtime:{iteration}",
                snapshot,
                CancellationToken.None);
            stopwatch.Stop();
            elapsed.Add(stopwatch.Elapsed);
            _output.WriteLine(
                $"Warm preparation iteration {iteration + 1}: " +
                $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        }

        Assert.All(elapsed, value => Assert.True(
            value < TimeSpan.FromSeconds(2),
            $"Warm preparation recognition took {value.TotalMilliseconds:F1} ms."));
    }

    [Fact]
    public void CachedRunConstantsAreNotReEmittedAsFreshObservations()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now - TimeSpan.FromSeconds(3);
        var snapshot = EmptySnapshot(now) with
        {
            RunId = "fresh-event-run",
            AsOf = now,
            EnemyIds = Observation<IReadOnlyList<string>>.Known(
                ["enemy_affix_1"],
                0.9,
                observedAt: now),
            InvestmentEnvironmentId = Observation<string>.Known(
                "investment_environment_001",
                0.9,
                observedAt: old),
            InvestmentStrategyIds = Observation<IReadOnlyList<string>>.Known(
                ["investment_strategy_001"],
                0.9,
                observedAt: old)
        };
        var analysis = new ScreenshotAnalysisResult
        {
            AnalysisId = "fresh-event-analysis",
            Snapshot = snapshot
        };

        var events = Phase2LiveCollectionService.CreateEvents(analysis);

        Assert.Contains(events, item => item.EventType == RunEventType.EnemyObserved);
        Assert.DoesNotContain(events, item =>
            item.EventType == RunEventType.InvestmentEnvironmentObserved);
        Assert.DoesNotContain(events, item =>
            item.EventType == RunEventType.InvestmentStrategyObserved);

        var unknownEvents = Phase2LiveCollectionService.CreateEvents(
            analysis with
            {
                AnalysisId = "unknown-static-event-analysis",
                Snapshot = EmptySnapshot(now) with
                {
                    RunId = "unknown-static-event-run",
                    AsOf = now
                }
            });
        Assert.DoesNotContain(unknownEvents, item =>
            item.EventType == RunEventType.EnemyObserved);
    }

    [Fact]
    public async Task BatchImagesNeverShareRunScopedRecognitionCache()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(input);
        try
        {
            File.Copy(
                Path.Combine(
                    RepositoryRoot,
                    "tests",
                    "CurrencyWarsAssistant.Tests",
                    "Fixtures",
                    "phase2-2026-07-28",
                    "125924.png"),
                Path.Combine(input, "first.png"));
            File.Copy(
                Path.Combine(
                    RepositoryRoot,
                    "tests",
                    "CurrencyWarsAssistant.Tests",
                    "Fixtures",
                    "phase2-2026-07-28",
                    "125924.png"),
                Path.Combine(input, "second.png"));
            var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
            var gameData = GameDataCatalogLoader.Load(dataDirectory);
            var iconRecognizer = new LifecycleCountingIconRecognizer(
                gameData.InvestmentEnvironments[0].Id,
                gameData.InvestmentStrategies.Take(2)
                    .Select(item => item.Id)
                    .ToArray());
            var analyzer = new Phase2OperationalScreenshotAnalyzer(
                new EmptyCharacterRecognizer(),
                [],
                iconRecognizer,
                [],
                new StaticTextOcr(string.Empty),
                gameData,
                new StaticTextOcr(string.Empty),
                pageClassifier: CreatePageClassifier());
            var service = new Phase2BatchImageAnalysisService(analyzer);

            var report = await service.AnalyzeDirectoryAsync(
                input,
                output,
                CancellationToken.None);

            Assert.Equal(2, report.Images.Count);
            Assert.All(report.Images, image => Assert.Null(image.Error));
            Assert.Equal(2, iconRecognizer.Count("investment-environment"));
            Assert.Equal(2, iconRecognizer.Count("investment-strategy"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("13249.8万", 132_498_000L)]
    [InlineData("430.3万", 4_303_000L)]
    [InlineData("2.5亿", 250_000_000L)]
    [InlineData("0", 0L)]
    [InlineData("1,234万", 12_340_000L)]
    [InlineData("1,234,567万", 12_345_670_000L)]
    [InlineData("12,3万", 123_000L)]
    [InlineData("1234万", 12_340_000L)]
    public void DamageUnitsAreNormalizedBeforeCandidatesAreCompared(
        string text,
        long expected)
    {
        var candidate = Assert.Single(
            Phase2OperationalScreenshotAnalyzer.ParseDamageCandidates(text));

        Assert.Equal(expected, candidate.Value);
    }

    [Fact]
    public void SettlementDecimalWithoutReadableUnitUsesLocalWanFallback()
    {
        var candidates = Phase2OperationalScreenshotAnalyzer
            .ParseSettlementDamageCandidates("13.6")
            .ToArray();
        var best = candidates.OrderByDescending(item => item.Score).First();

        Assert.Equal(136_000, best.Value);
    }

    [Theory]
    [InlineData("1,234", 12_340_000L)]
    [InlineData("13,600", 136_000_000L)]
    [InlineData("1,001", 10_010_000L)]
    public void SettlementThousandsSeparatorIsNotTreatedAsDecimalPoint(
        string text,
        long expected)
    {
        var candidate = Phase2OperationalScreenshotAnalyzer
            .ParseSettlementDamageCandidates(text)
            .OrderByDescending(item => item.Score)
            .First();

        Assert.Equal(expected, candidate.Value);
    }

    [Theory]
    [InlineData("337018.1万", true)]
    [InlineData("9215万", true)]
    [InlineData("01", false)]
    [InlineData("1,001 (settlement unit inferred as 万)", false)]
    public void BattleDamageRequiresExplicitScaleBeforeReliableSummation(
        string text,
        bool expected)
    {
        Assert.Equal(
            expected,
            Phase2OperationalScreenshotAnalyzer.HasExplicitDamageScaleSafe(text));
    }

    [Fact]
    public async Task BatchSettlementReportIncludesGoldDamageAndNoRunRecords()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(input);
        try
        {
            File.Copy(
                Path.Combine(
                    RepositoryRoot,
                    "tests",
                    "CurrencyWarsAssistant.Tests",
                    "Fixtures",
                    "PageReplay",
                    "challenge_success_1_1.jpg"),
                Path.Combine(input, "settlement.jpg"));
            using var characterRecognizer = new OpenCvCharacterCardRecognizer();
            using var iconRecognizer = new OpenCvPhase2IconRecognizer();
            var service = new Phase2BatchImageAnalysisService(
                CreateAnalyzer(characterRecognizer, iconRecognizer));

            var report = await service.AnalyzeDirectoryAsync(
                input,
                output,
                CancellationToken.None,
                continuousSequence: false,
                writeAnnotations: false);

            Assert.False(report.WritesFormalRunRecords);
            var image = Assert.Single(report.Images);
            Assert.Equal(Phase2PageFamily.BattleSettlement, image.PageType);
            Assert.Equal(3, image.Recognitions.Count(item =>
                item.RecognitionObject == "SettlementCharacterDamage"));
            var gold = Assert.Single(image.Recognitions.Where(item =>
                item.RecognitionObject == "SettlementGoldReward"));
            Assert.Equal("4", gold.RecognizedFields!["goldReward"]);
            var candidate = Assert.Single(image.Recognitions.Where(item =>
                item.RecognitionObject == "SettlementScreenDamageCandidate"));
            Assert.Equal("212000", candidate.RecognizedFields!["damage"]);
            Assert.Empty(image.AnnotatedImagePath);
            Assert.False(Directory.Exists(Path.Combine(output, "annotated")));
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
    public async Task BatchAnalysisBoundsAnUnresponsiveSingleImageAndWritesProgress()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(input);
        try
        {
            File.Copy(
                Path.Combine(
                    RepositoryRoot,
                    "tests",
                    "CurrencyWarsAssistant.Tests",
                    "Fixtures",
                    "PageReplay",
                    "challenge_success_1_1.jpg"),
                Path.Combine(input, "unresponsive.jpg"));
            using var characterRecognizer = new OpenCvCharacterCardRecognizer();
            using var iconRecognizer = new OpenCvPhase2IconRecognizer();
            var service = new Phase2BatchImageAnalysisService(
                CreateAnalyzer(characterRecognizer, iconRecognizer),
                new NeverCompletingSituationAnalyzer(),
                TimeSpan.FromMilliseconds(50));

            var report = await service.AnalyzeDirectoryAsync(
                input,
                output,
                CancellationToken.None);

            var image = Assert.Single(report.Images);
            Assert.Contains("TimeoutException", image.Error);
            var progress = await File.ReadAllTextAsync(Path.Combine(
                output,
                "phase2-batch-progress.jsonl"));
            Assert.Contains("\"started\"", progress);
            Assert.Contains("\"timed-out\"", progress);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("125924.png", "1-8", 120)]
    [InlineData("132307.png", "2-4", 123)]
    public async Task PreparationReferencesExposeNodeDifficultyAndFormation(
        string fileName,
        string expectedNode,
        int expectedDifficulty)
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadReference(fileName);

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            $"fixture:{fileName}",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Preparation, state.PageFamily);
        AssertKnownOrExplicitlyUnknown(state.NodeId, expectedNode);
        AssertKnownOrExplicitlyUnknown(state.EnemyDifficulty, expectedDifficulty);
        Assert.Contains(
            state.Formation.Status,
            new[] { ObservationStatus.Known, ObservationStatus.Unknown });
        Assert.NotEmpty(state.Formation.Value!);
        Assert.NotEmpty(state.PendingIcons);
        var pendingEquipment = state.PendingIcons.Where(item =>
            item.Category == PendingIconCategory.AdvancedEquipment).ToArray();
        Assert.NotEmpty(pendingEquipment);
        Assert.All(pendingEquipment, item =>
            Assert.False(string.IsNullOrWhiteSpace(item.TemplateId)));
    }

    [Theory]
    [InlineData("125924.png")]
    [InlineData("132307.png")]
    public void CharacterShortlistPreservesExhaustiveFormationDecisions(
        string fileName)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = LoadCharacterTemplates(dataDirectory, gameData);
        var frame = LoadReference(fileName);
        using var exhaustive = new OpenCvCharacterCardRecognizer(int.MaxValue);
        using var indexed = new OpenCvCharacterCardRecognizer();

        foreach (var slots in new[]
                 {
                     Phase2RecognitionRegions.PreparationCharacterSlots1920,
                     Phase2RecognitionRegions.BenchCharacterSlots1920
                 })
        {
            var expected = exhaustive.Recognize(frame, templates, slots);
            var actual = indexed.Recognize(frame, templates, slots);

            Assert.Equal(expected, actual);
            Assert.True(indexed.LastDecisiveShortlistCount > 0);
        }
    }

    [Fact]
    public void IconCatalogLoadsExistingAndImportedResourcesWithExplicitVisualAmbiguity()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");

        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);

        Assert.Equal(
            Directory.GetFiles(Path.Combine(
                dataDirectory,
                "character-small-avatar-templates"), "*.png").Length,
            templates.Count(item => item.Category == "character-avatar"));
        Assert.Equal(2, templates.Count(item =>
            item.Category == "action-value-indicator"));
        Assert.Equal(8, templates.Count(item =>
            item.Category == "simple-equipment"));
        Assert.Equal(70, templates.Count(item =>
            item.Category == "advanced-equipment"));
        Assert.Equal(122, templates
            .Where(item => item.Category == "advanced-equipment")
            .SelectMany(item => item.CandidateIds ?? [item.Id])
            .Distinct(StringComparer.Ordinal)
            .Count());
        Assert.Contains(templates, item =>
            item.Category == "advanced-equipment" &&
            !item.ResolvesExactIdentity &&
            (item.CandidateIds ?? []).SequenceEqual(
                new[]
                {
                    "currency_wars_equipment_061",
                    "currency_wars_equipment_100"
                }));
        Assert.Contains(templates, item =>
            item.Category == "advanced-equipment" &&
            !item.ResolvesExactIdentity &&
            (item.CandidateIds ?? []).SequenceEqual(
                new[]
                {
                    "currency_wars_equipment_066",
                    "currency_wars_equipment_105"
                }));
        Assert.Empty(
            templates.Where(item => item.Category == "simple-equipment")
                .Select(item => item.Id)
                .Intersect(
                    templates.Where(item =>
                            item.Category == "advanced-equipment")
                        .Select(item => item.Id),
                    StringComparer.Ordinal));
        Console.WriteLine(string.Join(
            ", ",
            templates.GroupBy(item => item.Category)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()} " +
                    $"ambiguous={group.Count(item => !item.ResolvesExactIdentity)}")));
        Assert.Equal(33, templates.Count(item => item.Category == "synergy"));
        Assert.Equal(44, templates.Count(item => item.Category == "negative-affix"));
        Assert.Equal(83, templates.Count(item => item.Category == "investment-environment"));
        Assert.Equal(306, templates.Count(item => item.Category == "investment-strategy"));
        Assert.Equal(21, templates.Count(item => item.Category == "special-item"));

        Assert.Equal(32, templates.Count(item =>
            item.Category == "synergy" && item.ResolvesExactIdentity));
        var disabledSynergy = Assert.Single(templates.Where(item =>
            item.Category == "synergy" && !item.ResolvesExactIdentity));
        Assert.Equal("bond_昼之半神", disabledSynergy.Id);
        Assert.Equal(4, templates.Count(item =>
            item.Category == "negative-affix" && !item.ResolvesExactIdentity));
        Assert.Equal(23, templates.Count(item =>
            item.Category == "investment-strategy" && !item.ResolvesExactIdentity));
        Assert.Single(templates.Where(item =>
            item.Category == "special-item" && !item.ResolvesExactIdentity));
        Assert.Contains(templates, item =>
            item.Id == "special_item_020" && item.ResolvesExactIdentity);
        Assert.Contains(templates, item =>
            item.Id == "special_item_021" && item.ResolvesExactIdentity);
    }

    [Fact]
    public void ImportedAssetManifestUsesRelativeDecodablePaths()
    {
        var assetRoot = Path.Combine(
            RepositoryRoot,
            "data",
            "4.4",
            "phase2-icon-assets");
        var manifest = Path.Combine(assetRoot, "asset-manifest.jsonl");
        var lines = File.ReadAllLines(manifest);

        Assert.Equal(630, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.False(root.TryGetProperty("local_source_path", out _));
            Assert.False(root.TryGetProperty("raw_path", out _));
            Assert.False(root.TryGetProperty("composed_from", out _));
            var relative = root.GetProperty("standardized_path").GetString();
            Assert.NotNull(relative);
            Assert.False(Path.IsPathRooted(relative));
            var path = Path.GetFullPath(Path.Combine(
                assetRoot,
                relative!.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(
                Path.TrimEndingDirectorySeparator(assetRoot) +
                Path.DirectorySeparatorChar,
                path,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path), path);
            using var image = Cv2.ImDecode(
                File.ReadAllBytes(path),
                ImreadModes.Unchanged);
            Assert.False(image.Empty(), path);
            Assert.Equal(256, image.Width);
            Assert.Equal(256, image.Height);
        }
    }

    [Fact]
    public void RuntimeDataDoesNotExposeDeveloperMachinePaths()
    {
        var dataRoot = Path.Combine(RepositoryRoot, "data", "4.4");
        foreach (var path in Directory.EnumerateFiles(
                     dataRoot,
                     "*.json",
                     SearchOption.AllDirectories))
        {
            var json = File.ReadAllText(path);
            Assert.DoesNotMatch(
                @"(?i)(?:""|\s)[a-z]:(?:\\\\|/)",
                json);
        }
    }

    [Fact]
    public void ImportedBusinessIdsAndNamesMatchCurrentDataCatalogs()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var imported = ReadImportedManifest(dataDirectory);

        AssertImportedMappingsMatch(
            imported,
            "investment_environment",
            Path.Combine(dataDirectory, "investment-environments.json"));
        AssertImportedMappingsMatch(
            imported,
            "investment_strategy",
            Path.Combine(dataDirectory, "investment-strategies.json"));
        AssertImportedMappingsMatch(
            imported,
            "enemy_affix",
            Path.Combine(dataDirectory, "enemy-affixes.json"));
    }

    [Fact]
    public void DuplicateStrategyVisualRemainsUnknownAndReportsAllCandidates()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var ambiguous = templates.Single(item =>
            item.Category == "investment-strategy" &&
            item.CandidateIds is not null &&
            item.CandidateIds.Contains("investment_strategy_001") &&
            item.CandidateIds.Contains("investment_strategy_002"));
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var frame = LoadTemplateOnOpaqueBackground(ambiguous.FilePath);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            "investment-strategy",
            [new NormalizedRect(0, 0, 1, 1)],
            templates));

        Assert.False(result.IsKnown);
        Assert.NotNull(result.CandidateTemplateIds);
        Assert.Contains("investment_strategy_001", result.CandidateTemplateIds!);
        Assert.Contains("investment_strategy_002", result.CandidateTemplateIds!);
        Assert.True(result.Confidence >= ambiguous.MinimumConfidence);
    }

    [Fact]
    public void DuplicateNegativeAffixVisualRemainsUnknownAndReportsAllCandidates()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var ambiguous = templates.First(item =>
            item.Category == "negative-affix" &&
            !item.ResolvesExactIdentity &&
            item.CandidateIds is { Count: > 1 });
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var frame = LoadTemplateOnOpaqueBackground(ambiguous.FilePath);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            "negative-affix",
            [new NormalizedRect(0, 0, 1, 1)],
            templates));

        Assert.False(result.IsKnown);
        Assert.Equal(ambiguous.CandidateIds, result.CandidateTemplateIds);
        Assert.True(result.Confidence >= ambiguous.MinimumConfidence);
    }

    [Fact]
    public void CompactTemplateShortlistPreservesExhaustiveDecisions()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        using var exhaustive = new OpenCvPhase2IconRecognizer(int.MaxValue);
        using var indexed = new OpenCvPhase2IconRecognizer();
        var reducedComparisons = 0;

        foreach (var category in templates
                     .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 64))
        {
            foreach (var template in category)
            {
                var frame = LoadTemplateOnOpaqueBackground(template.FilePath);
                var expected = Assert.Single(exhaustive.Recognize(
                    frame,
                    category.Key,
                    [new NormalizedRect(0, 0, 1, 1)],
                    templates));
                var actual = Assert.Single(indexed.Recognize(
                    frame,
                    category.Key,
                    [new NormalizedRect(0, 0, 1, 1)],
                    templates));

                Assert.Equal(expected.TemplateId, actual.TemplateId);
                Assert.Equal(expected.IsKnown, actual.IsKnown);
                Assert.Equal(
                    expected.CandidateTemplateIds,
                    actual.CandidateTemplateIds);
                Assert.Equal(expected.Confidence, actual.Confidence, 8);
                var expectedTopFive = expected.RankedCandidates!.Take(5)
                    .Select(item => item.TemplateId)
                    .ToArray();
                var actualTopFive = actual.RankedCandidates!.Take(5)
                    .Select(item => item.TemplateId)
                    .ToArray();
                Assert.True(
                    expectedTopFive.SequenceEqual(actualTopFive),
                    $"{category.Key}/{template.Id}: expected " +
                    $"[{string.Join(',', expectedTopFive)}], actual " +
                    $"[{string.Join(',', actualTopFive)}]");
                Assert.True(indexed.LastExactComparisonCount <= 64);
                if (indexed.LastExactComparisonCount < category.Count())
                {
                    reducedComparisons++;
                }
            }
        }

        Assert.True(reducedComparisons > 0);
    }

    [Fact]
    public void ReviewedThreeAndFourCostHiringBooksAreEnabledForRecognition()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var imported = ReadImportedManifest(dataDirectory);
        var derived = imported.Where(item =>
            item.GetProperty("id").GetString() is
                "special_item_020" or "special_item_021").ToArray();
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);

        Assert.Equal(2, derived.Length);
        Assert.All(derived, item =>
            Assert.True(item.GetProperty("derived_asset").GetBoolean()));
        foreach (var id in new[] { "special_item_020", "special_item_021" })
        {
            var template = Assert.Single(templates.Where(item =>
                item.Id == id &&
                string.Equals(
                    item.Category,
                    "special-item",
                    StringComparison.OrdinalIgnoreCase)));
            Assert.True(template.ResolvesExactIdentity);
            Assert.Equal([id], template.CandidateIds);
            Assert.Contains(templates, item =>
                item.Id == id &&
                string.Equals(
                    item.Category,
                    "inventory-item",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SpecialItemsReuseCanonicalEquipmentIdsWithoutHidingNewItems()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory)
            .Where(item => item.Category == "special-item")
            .ToArray();

        Assert.Contains(templates, item =>
            item.CandidateIds?.Contains("currency_wars_equipment_153") == true);
        Assert.Contains(templates, item =>
            item.CandidateIds?.Contains("currency_wars_equipment_157") == true);
        Assert.Contains(templates, item =>
            item.CandidateIds?.Contains("special_item_001") == true);
        Assert.Contains(templates, item =>
            item.CandidateIds?.Contains("special_item_022") == true);
        var dice = Assert.Single(templates.Where(item =>
            item.CandidateIds?.Contains("currency_wars_equipment_082") == true));
        Assert.False(dice.ResolvesExactIdentity);
        Assert.Contains("currency_wars_equipment_121", dice.CandidateIds!);
    }

    [Theory]
    [InlineData("special_item_020")]
    [InlineData("special_item_021")]
    public void ReviewedHiringBookMatchesItselfAndNotFiveCostBook(
        string expectedId)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var derivedPath = Path.Combine(
            dataDirectory,
            "phase2-icon-assets",
            "standardized",
            "special_item",
            $"{expectedId}.png");
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var frame = LoadTemplateOnOpaqueBackground(derivedPath);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            "special-item",
            [new NormalizedRect(0, 0, 1, 1)],
            templates));

        Console.WriteLine(string.Join(
            "; ",
            result.RankedCandidates!.Select(item =>
                $"{item.TemplateId}={item.Confidence:F6}")));

        Assert.True(result.IsKnown);
        Assert.Equal(expectedId, result.TemplateId);
        Assert.DoesNotContain(
            result.RankedCandidates!,
            item => item.TemplateId == "special_item_022" &&
                    item.Confidence >= result.Confidence);
    }

    [Theory]
    [InlineData("125924.png")]
    [InlineData("132307.png")]
    public void ReferencePreparationIconsProduceTraceableRecognitionEvidence(
        string fileName)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        var frame = LoadReference(fileName);
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var cases = new[]
        {
            ("negative-affix", Phase2RecognitionRegions.NegativeAffixSlots),
            ("investment-environment", (IReadOnlyList<NormalizedRect>)[
                Phase2RecognitionRegions.InvestmentIconSlots[0]]),
            ("investment-strategy", (IReadOnlyList<NormalizedRect>)
                Phase2RecognitionRegions.InvestmentIconSlots.Skip(1).ToArray()),
            ("synergy", Phase2RecognitionRegions.SynergyIconSlots)
        };

        foreach (var (category, slots) in cases)
        {
            var results = recognizer.Recognize(
                frame,
                category,
                slots,
                templates);
            Assert.Equal(slots.Count, results.Count);
            foreach (var result in results)
            {
                Assert.NotNull(result.TemplateId);
                Assert.NotNull(result.CandidateTemplateIds);
                Console.WriteLine(
                    $"{fileName} {category}[{result.SlotIndex}] " +
                    $"known={result.IsKnown} id={result.TemplateId} " +
                    $"id64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(result.TemplateId!))} " +
                    $"score={result.Confidence:F4} candidates=" +
                    string.Join('|', result.CandidateTemplateIds!));
                Console.WriteLine("ranked=" + string.Join(
                    ';',
                    result.RankedCandidates!.Select(candidate =>
                        $"{candidate.TemplateId}:{candidate.Confidence:F4}")));
            }
        }

        var environment = Assert.Single(recognizer.Recognize(
            frame,
            "investment-environment",
            [Phase2RecognitionRegions.InvestmentIconSlots[0]],
            templates));
        Assert.True(environment.IsKnown);
        Assert.Equal("investment_environment_082", environment.TemplateId);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void RealEnvironmentRecognitionIsStableAcrossSixteenByNineScaling(
        int width,
        int height)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var frame = ResizeReference("125924.png", width, height);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            "investment-environment",
            [Phase2RecognitionRegions.InvestmentIconSlots[0]],
            templates));

        Assert.True(result.IsKnown);
        Assert.Equal("investment_environment_082", result.TemplateId);
    }

    [Theory]
    [InlineData("130104.png", "1-9", 200, 199000)]
    [InlineData("130112.png", "1-9", 200, 623000)]
    [InlineData("130123.png", "1-9", 188, 2634000)]
    [InlineData("132328.png", "2-4", 128, 449000)]
    public async Task BattleReferencesExposeCharacterDamageAndRemainingAction(
        string fileName,
        string expectedNode,
        int expectedActionValue,
        long expectedVisibleDamage)
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = CreateAnalyzer(characterRecognizer, iconRecognizer);
        var frame = LoadReference(fileName);

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            $"fixture:{fileName}",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Battle, state.PageFamily);
        if (state.RemainingActionValue.Status == ObservationStatus.Known)
        {
            Assert.Equal(
                expectedActionValue,
                state.RemainingActionValue.Value!.TotalActionValue);
        }
        else
        {
            Assert.Equal(ObservationStatus.Unknown, state.RemainingActionValue.Status);
            Assert.NotEmpty(state.RemainingActionValue.Uncertainty);
        }

        if (state.BattleDamage.Status == ObservationStatus.Known)
        {
            var damage = state.BattleDamage.Value!;
            Assert.Equal(
                expectedVisibleDamage,
                damage.Sum(item => item.Damage));
            Assert.All(
                damage.Where(item => item.Damage > 0),
                item => Assert.False(string.IsNullOrWhiteSpace(item.CharacterId)));
        }
        else
        {
            Assert.Equal(ObservationStatus.Unknown, state.BattleDamage.Status);
            Assert.NotEmpty(state.BattleDamage.Uncertainty);
            Assert.Contains(
                state.PendingIcons,
                item => item.Category == PendingIconCategory.CharacterAvatar);
        }

        AssertKnownOrExplicitlyUnknown(state.NodeId, expectedNode);
    }

    [Theory]
    [InlineData("130104.png", 0.66, 0.76)]
    [InlineData("130112.png", 0.66, 0.76)]
    [InlineData("130123.png", 0.66, 0.76)]
    [InlineData("132328.png", 0.42, 0.52)]
    public void ActionIndicatorTemplateFindsCountdownRow(
        string fileName,
        double minimumY,
        double maximumY)
    {
        var frame = LoadReference(fileName);
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));

        var result = Phase2ActionIndicatorLocator.Locate(frame, templates);

        Assert.NotNull(result);
        Assert.InRange(
            result!.Region.Y / (double)frame.Height,
            minimumY,
            maximumY);
    }

    [Fact]
    public void ActionIndicatorCanMoveIntoUpperTimeline()
    {
        var frame = LoadLiveCapture("battle-1-3-action-indicator-mid.png");
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));

        var result = Phase2ActionIndicatorLocator.Locate(frame, templates);

        Assert.NotNull(result);
        Assert.InRange(result!.Region.Y / (double)frame.Height, 0.15, 0.23);
    }

    [Fact]
    public void ActionIndicatorTemplateDoesNotOverrideCurrentRoundZero()
    {
        var frame = LoadLiveCapture(
            "battle-1-3-action-indicator-round-zero.png");
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));

        var result = Phase2ActionIndicatorLocator.Locate(frame, templates);

        Assert.NotNull(result);
        Assert.InRange(result!.Region.Y / (double)frame.Height, 0.36, 0.44);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(2559, 1439)]
    public void ConfirmedRegionsScaleInsideSixteenByNineFrames(
        int width,
        int height)
    {
        foreach (var definition in Phase2RecognitionRegions.All)
        {
            var pixels = definition.Region.ToPixels(width, height);
            Assert.False(pixels.IsEmpty, definition.Field);
            Assert.InRange(pixels.X, 0, width - 1);
            Assert.InRange(pixels.Y, 0, height - 1);
            Assert.InRange(pixels.Right, 1, width);
            Assert.InRange(pixels.Bottom, 1, height);
        }

        for (var row = 0; row < 8; row++)
        {
            var avatar = Phase2RecognitionRegions.BattleDamageAvatar(row)
                .ToPixels(width, height);
            var damage = Phase2RecognitionRegions.BattleDamageValue(row)
                .ToPixels(width, height);
            Assert.True(avatar.Right <= damage.X);
            Assert.True(damage.Bottom <= height);
        }
    }

    [Fact]
    public void PersistentStateNeedsTwoConsecutiveFrames()
    {
        var tracker = new Phase2OperationalStateTracker();
        var frame = PreparationState("1-8", 21);

        var first = tracker.Observe(frame);
        var second = tracker.Observe(frame);

        Assert.False(first.PersistentStateConfirmed);
        Assert.True(second.PersistentStateConfirmed);
    }

    [Fact]
    public void ConfirmedInvestmentStrategiesOnlyGrow()
    {
        var tracker = new Phase2OperationalStateTracker();
        var first = PreparationState("1-3", 20) with
        {
            InvestmentStrategyIds =
                Observation<IReadOnlyList<string>>.Known(["strategy-a"], 0.9)
        };
        tracker.Observe(first);
        tracker.Observe(first);

        var later = PreparationState("1-4", 24) with
        {
            InvestmentStrategyIds =
                Observation<IReadOnlyList<string>>.Known(["strategy-b"], 0.9)
        };
        tracker.Observe(later);
        var confirmed = tracker.Observe(later);

        Assert.Equal(
            ["strategy-a", "strategy-b"],
            confirmed.Current.InvestmentStrategyIds.Value);
    }

    [Fact]
    public void ConfirmedPageTransitionsProduceMilestones()
    {
        var tracker = new Phase2OperationalStateTracker();
        var preparation = PreparationState("1-1", 20);

        tracker.Observe(preparation);
        var preparationConfirmed = tracker.Observe(preparation);

        Assert.True(preparationConfirmed.PageChanged);
        Assert.Contains("进入备战节点 1-1", preparationConfirmed.Message);

        var battle = BattleState("1-1", [100_000], 1, 90);
        tracker.Observe(battle);
        var battleConfirmed = tracker.Observe(battle);

        Assert.True(battleConfirmed.PageChanged);
        Assert.Contains("战斗开始", battleConfirmed.Message);
    }

    [Fact]
    public void SemanticStateConfirmsWhenOnlyEvidenceMetadataChanges()
    {
        var tracker = new Phase2OperationalStateTracker();
        var firstCapturedAt = DateTimeOffset.Parse(
            "2026-07-29T14:42:12+08:00");
        var secondCapturedAt = firstCapturedAt.AddSeconds(3);

        var first = PreparationStateWithFormation(firstCapturedAt);
        var second = PreparationStateWithFormation(secondCapturedAt);

        Assert.False(tracker.Observe(first).PersistentStateConfirmed);
        Assert.True(tracker.Observe(second).PersistentStateConfirmed);
    }

    [Fact]
    public void SingleDifferentFrameDoesNotConfirmChangedState()
    {
        var tracker = new Phase2OperationalStateTracker();
        var original = PreparationState("1-8", 21);
        tracker.Observe(original);
        Assert.True(tracker.Observe(original).PersistentStateConfirmed);

        var oneOff = PreparationState("1-8", 62);
        var result = tracker.Observe(oneOff);

        Assert.False(result.PersistentStateConfirmed);
    }

    [Fact]
    public void LeavingBattleKeepsOnlyLatestCompleteFrameAsFinal()
    {
        var tracker = new Phase2OperationalStateTracker();
        var early = BattleState("1-9", [100_000, 20_000], 1, 100);
        var final = BattleState("1-9", [298_000, 199_000, 126_000], 1, 88);

        tracker.Observe(early);
        tracker.Observe(early);
        tracker.Observe(final);
        tracker.Observe(PreparationState("2-1", 40));
        var transition = tracker.Observe(PreparationState("2-1", 40));

        Assert.NotNull(transition.FinalizedBattle);
        Assert.Equal(623_000, transition.FinalizedBattle!.TotalDamage);
        Assert.NotNull(transition.FinalizedBattle.RemainingActionValue);
        Assert.Equal(188, transition.FinalizedBattle.RemainingActionValue!.TotalActionValue);
        Assert.Equal(3, transition.FinalizedBattle.CharacterDamage.Count);
    }

    [Fact]
    public void IncompleteBattleFrameCannotOverwriteLastCompleteEvidence()
    {
        var tracker = new Phase2OperationalStateTracker();
        var complete = BattleState("2-4", [311_000, 138_000], 1, 28);
        tracker.Observe(complete);
        tracker.Observe(complete with
        {
            BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>
                .Unknown("temporary panel occlusion")
        });
        tracker.Observe(PreparationState("2-5", 30));
        var result = tracker.Observe(PreparationState("2-5", 30));

        Assert.NotNull(result.FinalizedBattle);
        Assert.Equal(449_000, result.FinalizedBattle!.TotalDamage);
        Assert.NotNull(result.FinalizedBattle.RemainingActionValue);
        Assert.Equal(128, result.FinalizedBattle.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void SettlementFinalizationKeepsBothCandidatesAndSelectsMaximum()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battle = BattleState("1-9", [298_000, 199_000, 126_000], 1, 88);
        var settlement = SettlementState(
            "1-9",
            [500_000, 150_000, 50_000],
            9);
        tracker.Observe(battle);
        tracker.Observe(battle);

        Assert.Null(tracker.Observe(settlement).FinalizedBattle);
        var finalized = tracker.Observe(settlement).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Equal(623_000, finalized!.BattleScreenDamageCandidate);
        Assert.Equal(700_000, finalized.SettlementScreenDamageCandidate);
        Assert.Equal(700_000, finalized.SelectedDamage);
        Assert.Equal(700_000, finalized.TotalDamage);
        Assert.Equal(
            FinalDamageSelectionSource.SettlementTopThree,
            finalized.SelectedDamageSource);
        Assert.Equal(9, finalized.GoldReward);
        Assert.Equal(3, finalized.FinalSettlementTopThree.Count);
    }

    [Fact]
    public void SettlementAnimationDoesNotFinalizeBeforeStableSummaryEvidence()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battle = BattleState("1-2", [240_000, 63_000], 0, 55);
        var animation = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.BattleSettlement,
            NodeId = Observation<string>.Known("1-2", 0.9),
            SettlementGoldReward = Observation<int>.Known(777, 0.65)
        };
        var stable = SettlementState("1-2", [240_000, 63_000, 0], 6);
        tracker.Observe(battle);
        tracker.Observe(battle);

        Assert.Null(tracker.Observe(animation).FinalizedBattle);
        Assert.Null(tracker.Observe(animation).FinalizedBattle);
        Assert.Null(tracker.Observe(animation).FinalizedBattle);
        Assert.Null(tracker.Observe(stable).FinalizedBattle);
        var finalized = tracker.Observe(stable).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Equal(6, finalized!.GoldReward);
        Assert.Equal(303_000, finalized.SettlementScreenDamageCandidate);
        Assert.Equal(303_000, finalized.SelectedDamage);
    }

    [Fact]
    public void StableSettlementCanFinalizeWhenBattleFramesWereUnavailable()
    {
        var tracker = new Phase2OperationalStateTracker();
        var settlement = SettlementState("1-6", [7_701_000, 2_948_200, 1_070_800], 9);

        Assert.Null(tracker.Observe(settlement).FinalizedBattle);
        var finalized = tracker.Observe(settlement).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Equal(11_720_000, finalized!.SelectedDamage);
        Assert.Equal(
            FinalDamageSelectionSource.SettlementTopThree,
            finalized.SelectedDamageSource);
        Assert.Equal(9, finalized.GoldReward);
        Assert.Null(finalized.RemainingActionValue);
        Assert.False(finalized.IsComplete);
    }

    [Fact]
    public void LargerBattleCandidateWinsWithoutDiscardingSettlementEvidence()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battle = BattleState("2-4", [500_000, 200_000], 1, 28);
        var settlement = SettlementState("2-4", [400_000, 100_000, 50_000], 5);
        tracker.Observe(battle);
        tracker.Observe(battle);
        tracker.Observe(settlement);

        var finalized = tracker.Observe(settlement).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Equal(700_000, finalized!.SelectedDamage);
        Assert.Equal(FinalDamageSelectionSource.BattleLastFrame,
            finalized.SelectedDamageSource);
        Assert.Equal(550_000, finalized.SettlementScreenDamageCandidate);
    }

    [Fact]
    public void UnknownSettlementCharactersStillContributeToNumericCandidate()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battleContext = BattleContextState("1-6", 1, 20);
        var settlement = SettlementState(
            "1-6",
            [132_498_000, 4_303_000, 4_192_000],
            9,
            identitiesKnown: false);
        tracker.Observe(battleContext);
        tracker.Observe(battleContext);
        tracker.Observe(settlement);

        var finalized = tracker.Observe(settlement).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Equal(140_993_000, finalized!.SelectedDamage);
        Assert.Equal(FinalDamageSelectionSource.SettlementTopThree,
            finalized.SelectedDamageSource);
        Assert.All(finalized.FinalSettlementTopThree, item =>
            Assert.False(item.CanDriveDecisions));
        Assert.False(finalized.IsComplete);
        Assert.False(finalized.CanDriveDecisions);
    }

    [Fact]
    public void ContextOnlyBattleFinalizesAsIncompleteWithoutThrowing()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battle = BattleContextState("1-1", 0, 80);
        var settlement = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.BattleSettlement,
            NodeId = Observation<string>.Known("1-1", 0.9)
        };
        tracker.Observe(battle);
        tracker.Observe(battle);
        var finalized = ObserveUntilFinalized(tracker, settlement);

        Assert.NotNull(finalized);
        Assert.Null(finalized!.TotalDamage);
        Assert.False(finalized.IsComplete);
        Assert.Equal(
            FinalDamageSelectionSource.Unavailable,
            finalized.SelectedDamageSource);
    }

    [Fact]
    public void FinalDamageAndActionMayComeFromDifferentBattleFrames()
    {
        var tracker = new Phase2OperationalStateTracker();
        var actionFrame = BattleContextState("1-6", 0, 55);
        var damageFrame = BattleState(
            "1-6",
            [77_010_000, 29_482_000, 10_708_000],
            0,
            0) with
        {
            RemainingActionValue = Observation<RemainingActionValueState>.Unknown(
                "action indicator disappeared at battle end")
        };

        tracker.Observe(actionFrame);
        tracker.Observe(damageFrame);
        tracker.Observe(PreparationState("1-7", 32));
        var finalized = tracker.Observe(PreparationState("1-7", 32)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Equal(117_200_000, finalized!.TotalDamage);
        Assert.NotNull(finalized.RemainingActionValue);
        Assert.Equal(55, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void ActionIncreaseWithoutWalterCannotOverwriteTrustedValue()
    {
        var tracker = new Phase2OperationalStateTracker();
        tracker.Observe(BattleState("2-4", [311_000, 138_000], 1, 28));
        tracker.Observe(BattleContextState("2-4", 1, 88));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(128, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void TerminalRoundHundredFramesCannotOverwriteLastStableActionValue()
    {
        var tracker = new Phase2OperationalStateTracker();
        tracker.Observe(BattleContextState("2-4", 1, 46));
        tracker.Observe(BattleContextState("2-4", 0, 100));
        tracker.Observe(BattleContextState("2-4", 0, 100));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(146, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Theory]
    [InlineData(1, 1, 48, 148)]
    [InlineData(2, 1, 48, 148)]
    [InlineData(3, 2, 28, 228)]
    public void WalterCanLegitimatelyDelayBattleCountdown(
        int starLevel,
        int delayedRounds,
        int delayedActionValue,
        int expectedTotal)
    {
        var tracker = new Phase2OperationalStateTracker();
        var preparation = PreparationStateWithCharacter(
            "2-4",
            30,
            "currency_wars_character_20",
            starLevel,
            FormationZone.Front);
        tracker.Observe(preparation);
        tracker.Observe(preparation);
        tracker.Observe(BattleContextState("2-4", 1, 28));
        tracker.Observe(BattleContextState(
            "2-4",
            delayedRounds,
            delayedActionValue));
        tracker.Observe(BattleContextState(
            "2-4",
            delayedRounds,
            delayedActionValue));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(expectedTotal, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OneAndTwoStarWalterCannotProduceThreeStarCountdownJump(
        int starLevel)
    {
        var tracker = new Phase2OperationalStateTracker();
        var preparation = PreparationStateWithCharacter(
            "2-4",
            30,
            "currency_wars_character_20",
            starLevel,
            FormationZone.Front);
        tracker.Observe(preparation);
        tracker.Observe(preparation);
        tracker.Observe(BattleContextState("2-4", 1, 28));
        tracker.Observe(BattleContextState("2-4", 2, 28));
        tracker.Observe(BattleContextState("2-4", 2, 28));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(128, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void WalterOnBenchDoesNotPermitCountdownIncrease()
    {
        var tracker = new Phase2OperationalStateTracker();
        var preparation = PreparationStateWithCharacter(
            "2-4",
            30,
            "currency_wars_character_20",
            3,
            FormationZone.Bench);
        tracker.Observe(preparation);
        tracker.Observe(preparation);
        tracker.Observe(BattleContextState("2-4", 1, 28));
        tracker.Observe(BattleContextState("2-4", 2, 28));
        tracker.Observe(BattleContextState("2-4", 2, 28));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(128, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void SingleLargeActionDropCannotPoisonRecoveredTimelineValue()
    {
        var tracker = new Phase2OperationalStateTracker();
        tracker.Observe(BattleContextState("2-4", 0, 76));
        tracker.Observe(BattleContextState("2-4", 0, 6));
        tracker.Observe(BattleContextState("2-4", 0, 2));
        tracker.Observe(BattleContextState("2-4", 0, 72));
        tracker.Observe(BattleContextState("2-4", 0, 71));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(71, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void SingleSpuriousRecoveryDoesNotUndoGenuineLargeDrop()
    {
        var tracker = new Phase2OperationalStateTracker();
        tracker.Observe(BattleContextState("2-4", 1, 28));
        tracker.Observe(BattleContextState("2-4", 0, 55));
        tracker.Observe(BattleContextState("2-4", 0, 53));
        tracker.Observe(BattleContextState("2-4", 1, 20));
        tracker.Observe(BattleContextState("2-4", 0, 52));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(52, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void RepeatedLargeActionDropCanBecomeTrusted()
    {
        var tracker = new Phase2OperationalStateTracker();
        tracker.Observe(BattleContextState("2-4", 1, 28));
        tracker.Observe(BattleContextState("2-4", 0, 55));
        tracker.Observe(BattleContextState("2-4", 0, 53));
        tracker.Observe(PreparationState("2-5", 30));
        var finalized = tracker.Observe(PreparationState("2-5", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.NotNull(finalized!.RemainingActionValue);
        Assert.Equal(53, finalized.RemainingActionValue!.TotalActionValue);
    }

    [Fact]
    public void DamageWithoutReadableActionIsStillFinalizedAsIncomplete()
    {
        var tracker = new Phase2OperationalStateTracker();
        var damageOnly = BattleState("3-2", [300_000, 120_000], 0, 0) with
        {
            RemainingActionValue = Observation<RemainingActionValueState>.Unknown(
                "action indicator occluded")
        };

        tracker.Observe(damageOnly);
        tracker.Observe(PreparationState("3-3", 40));
        var finalized = tracker.Observe(PreparationState("3-3", 40)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Equal(420_000, finalized!.TotalDamage);
        Assert.Null(finalized.RemainingActionValue);
        Assert.False(finalized.IsComplete);
        Assert.False(finalized.CanDriveDecisions);
    }

    [Fact]
    public void PartialCandidatesArePreservedButNeverPromotedToFinalZero()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battle = BattleContextState("3-2", 0, 40) with
        {
            BattleScreenDamageCandidate = PartialLong(111_000, "battle rows incomplete")
        };
        var settlement = SettlementState("3-2", [90_000, 20_000, 0], 4) with
        {
            SettlementScreenDamageCandidate = PartialLong(
                110_000,
                "settlement row missing")
        };
        tracker.Observe(battle);
        tracker.Observe(battle);
        var finalized = ObserveUntilFinalized(tracker, settlement);

        Assert.NotNull(finalized);
        Assert.Equal(111_000, finalized!.BattleScreenDamageCandidate);
        Assert.Equal(110_000, finalized.SettlementScreenDamageCandidate);
        Assert.Null(finalized.SelectedDamage);
        Assert.Null(finalized.TotalDamage);
        Assert.Equal(FinalDamageSelectionSource.Unavailable,
            finalized.SelectedDamageSource);
        Assert.False(finalized.IsComplete);
    }

    [Fact]
    public void RepeatedSettlementCandidateWithoutDamageUnitStaysUntrusted()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battle = BattleContextState("3-2", 0, 40);
        var settlement = SettlementState("3-2", [2_000_000, 700_000, 300_000], 4) with
        {
            SettlementScreenDamageCandidate = PartialLong(
                3_000_000,
                "结算伤害单位未识别；禁止把推测的万位值作为最终伤害。")
        };
        tracker.Observe(battle);
        tracker.Observe(battle);

        var finalized = ObserveUntilFinalized(tracker, settlement);

        Assert.NotNull(finalized);
        Assert.Equal(3_000_000, finalized!.SettlementScreenDamageCandidate);
        Assert.Null(finalized.SelectedDamage);
        Assert.Equal(
            FinalDamageSelectionSource.Unavailable,
            finalized.SelectedDamageSource);
    }

    [Fact]
    public void KnownBattleCandidateIsUsedWhenSettlementCandidateIsPartial()
    {
        var tracker = new Phase2OperationalStateTracker();
        var battle = BattleState("2-2", [300_000, 120_000], 0, 88);
        var settlement = SettlementState("2-2", [250_000, 100_000, 60_000], 6) with
        {
            SettlementScreenDamageCandidate = PartialLong(
                410_000,
                "one settlement row uncertain")
        };
        tracker.Observe(battle);
        tracker.Observe(battle);
        var finalized = ObserveUntilFinalized(tracker, settlement);

        Assert.NotNull(finalized);
        Assert.Equal(420_000, finalized!.SelectedDamage);
        Assert.Equal(FinalDamageSelectionSource.BattleLastFrame,
            finalized.SelectedDamageSource);
        Assert.Equal(410_000, finalized.SettlementScreenDamageCandidate);
    }

    [Fact]
    public void PartialOcclusionKeepsOnlyCoveredFieldAsTimestampedStale()
    {
        var tracker = new Phase2OperationalStateTracker();
        var observedAt = DateTimeOffset.Parse("2026-07-29T10:00:00+08:00");
        var evidence = new EvidenceReference(
            "fixture:full",
            "ocr:node",
            CapturedAt: observedAt,
            Confidence: 0.95);
        var full = PreparationState("1-8", 21) with
        {
            NodeId = Observation<string>.Known(
                "1-8",
                0.95,
                [evidence],
                observedAt),
            EnemyDifficulty = Observation<int>.Known(
                170,
                0.92,
                [evidence with { Locator = "ocr:difficulty" }],
                observedAt)
        };
        tracker.Observe(full);
        Assert.True(tracker.Observe(full).PersistentStateConfirmed);

        var popupOccluded = full with
        {
            NodeId = Observation<string>.Unknown(
                "popup occluded node region",
                observedAt: observedAt.AddSeconds(2)),
            EnemyDifficulty = Observation<int>.Known(
                171,
                0.93,
                [evidence with { Locator = "ocr:difficulty:new" }],
                observedAt.AddSeconds(2))
        };
        var result = tracker.Observe(popupOccluded);

        Assert.False(result.PersistentStateConfirmed);
        Assert.Equal(ObservationStatus.Stale, result.Current.NodeId.Status);
        Assert.Equal("1-8", result.Current.NodeId.Value);
        Assert.Equal(observedAt, result.Current.NodeId.ObservedAt);
        Assert.Contains("可能已过期", Assert.Single(result.Current.NodeId.Uncertainty));
        Assert.Equal(ObservationStatus.Known, result.Current.EnemyDifficulty.Status);
        Assert.Equal(171, result.Current.EnemyDifficulty.Value);
    }

    [Fact]
    public void UnknownTransitionFrameIsSkippedAndRecognitionRecovers()
    {
        var tracker = new Phase2OperationalStateTracker();
        var stable = PreparationState("1-8", 21);
        tracker.Observe(stable);
        tracker.Observe(stable);

        var transition = tracker.Observe(new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Unknown,
            Diagnostics = ["transition animation"]
        });

        Assert.False(transition.PersistentStateConfirmed);
        Assert.Null(transition.FinalizedBattle);
        Assert.Equal(Phase2PageFamily.Unknown, transition.Current.PageFamily);
        Assert.Equal(ObservationStatus.Stale, transition.Current.NodeId.Status);
        Assert.Contains("安全跳过", transition.Message);

        var recovered = PreparationState("1-9", 30);
        Assert.False(tracker.Observe(recovered).PersistentStateConfirmed);
        var confirmed = tracker.Observe(recovered);
        Assert.True(confirmed.PersistentStateConfirmed);
        Assert.Equal(ObservationStatus.Known, confirmed.Current.NodeId.Status);
        Assert.Equal("1-9", confirmed.Current.NodeId.Value);
    }

    [Fact]
    public void UnknownFrameCannotFinalizeBattleBeforeConfirmedPageRecovery()
    {
        var tracker = new Phase2OperationalStateTracker();
        var finalBattle = BattleState("2-4", [311_000, 138_000], 1, 28);
        tracker.Observe(finalBattle);
        tracker.Observe(finalBattle);

        var transition = tracker.Observe(new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Unknown,
            Diagnostics = ["full screen transition"]
        });
        Assert.Null(transition.FinalizedBattle);

        var preparation = PreparationState("2-5", 30);
        Assert.Null(tracker.Observe(preparation).FinalizedBattle);
        var recovered = tracker.Observe(preparation);
        Assert.NotNull(recovered.FinalizedBattle);
        Assert.Equal(449_000, recovered.FinalizedBattle!.TotalDamage);
    }

    [Fact]
    public async Task RegionalRecognizerFailuresDoNotFailTheWholeFrame()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new ThrowingCharacterRecognizer(),
            LoadCharacterTemplates(dataDirectory, gameData),
            new ThrowingIconRecognizer(),
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new ThrowingOcr(),
            gameData,
            new ThrowingOcr());
        var frame = LoadReference("125924.png");

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "fixture:regional-failure",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Preparation, state.PageFamily);
        // 节点号以页面 ID 提取为准（preparation_1_1 → 1-1），
        // 区域 OCR 失败不再导致节点号丢失（OCR 仅兜底）。
        Assert.Equal(ObservationStatus.Known, state.NodeId.Status);
        Assert.Equal("1-1", state.NodeId.Value);
        Assert.Equal(ObservationStatus.Unknown, state.Formation.Status);
        Assert.NotEmpty(state.Formation.Value!);
        Assert.All(state.Formation.Value!, item =>
            Assert.False(item.CanDriveDecisions));
        Assert.Equal(ObservationStatus.Unknown, state.InvestmentEnvironmentId.Status);
        Assert.NotEmpty(state.NamedContent);
        Assert.All(
            state.NamedContent,
            item => Assert.NotEqual(ObservationStatus.Known, item.Status));

        var unknown = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            "fixture:fully-unknown",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);
        Assert.Equal(Phase2PageFamily.Unknown, unknown.PageFamily);
    }

    [Fact]
    public async Task BatchUnknownImageProducesReportWithoutFormalRunRecords()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            File.Copy(
                Path.Combine(
                    RepositoryRoot,
                    "tests",
                    "CurrencyWarsAssistant.Tests",
                    "Fixtures",
                    "phase2-2026-07-28",
                    "125924.png"),
                Path.Combine(source, "unknown.png"));
            var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
            var gameData = GameDataCatalogLoader.Load(dataDirectory);
            var analyzer = new Phase2OperationalScreenshotAnalyzer(
                new ThrowingCharacterRecognizer(),
                LoadCharacterTemplates(dataDirectory, gameData),
                new ThrowingIconRecognizer(),
                Phase2IconTemplateCatalog.Load(dataDirectory),
                new ThrowingOcr(),
                gameData,
                new ThrowingOcr());
            var service = new Phase2BatchImageAnalysisService(analyzer);

            var report = await service.AnalyzeDirectoryAsync(
                source,
                output,
                CancellationToken.None);

            Assert.False(report.WritesFormalRunRecords);
            var image = Assert.Single(report.Images);
            Assert.Equal(Phase2PageFamily.Unknown, image.PageType);
            Assert.True(File.Exists(Path.Combine(output, "phase2-batch-report.json")));
            Assert.True(File.Exists(Path.Combine(output, "phase2-batch-report.jsonl")));
            Assert.True(File.Exists(Path.Combine(output, image.AnnotatedImagePath)));
            Assert.Equal(
                "unknown.png",
                Assert.Single(Directory.GetFiles(source).Select(Path.GetFileName)));
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
    public async Task BatchReportPreservesKnownSelectionPageClassification()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(source);
        try
        {
            File.Copy(
                Path.Combine(
                    RepositoryRoot,
                    "tests",
                    "CurrencyWarsAssistant.Tests",
                    "Fixtures",
                    "phase2-live-2026-07-29",
                    "investment-strategy-selection-stable.png"),
                Path.Combine(source, "selection.png"));
            var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
            var gameData = GameDataCatalogLoader.Load(dataDirectory);
            var analyzer = new Phase2OperationalScreenshotAnalyzer(
                new ThrowingCharacterRecognizer(),
                [],
                new ThrowingIconRecognizer(),
                [],
                new ThrowingOcr(),
                gameData,
                new ThrowingOcr());
            var service = new Phase2BatchImageAnalysisService(
                analyzer,
                new FixedKnownPageSituationAnalyzer("investment_strategy"));

            var report = await service.AnalyzeDirectoryAsync(
                source,
                output,
                CancellationToken.None,
                writeAnnotations: false);

            var image = Assert.Single(report.Images);
            Assert.Equal("investment_strategy", image.ClassifiedPageId);
            Assert.Equal(ObservationStatus.Known, image.PageRecognitionStatus);
            Assert.True(image.PageConfidence > 0);
            var page = Assert.Single(image.Recognitions.Where(item =>
                item.RecognitionObject == "Page"));
            Assert.Equal(ObservationStatus.Known, page.Status);
            Assert.DoesNotContain(image.UnknownItems, item =>
                item.RecognitionObject == "Page");
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
    public void UnknownCharacterDamageKeepsStableIdAndFinalValue()
    {
        var tracker = new Phase2OperationalStateTracker();
        var capturedAt = DateTimeOffset.Parse("2026-07-29T11:00:00+08:00");
        var evidence = new EvidenceReference(
            "fixture:unknown-character",
            "crop:battle-damage-source:1",
            CapturedAt: capturedAt,
            Confidence: 0.42);
        var row = new CharacterDamageState(
            1,
            "unknown-character-slot-1",
            456_000,
            "45.6万",
            0.42,
            0.68,
            new RelativeRegion(0.85, 0.23, 0.03, 0.05),
            new RelativeRegion(0.88, 0.23, 0.06, 0.04),
            evidence,
            "unknown-character-slot-1",
            ["currency_wars_character_017", "currency_wars_character_021"],
            "avatar ambiguous",
            false);
        var partial = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Battle,
            NodeId = Observation<string>.Known("1-9", 0.9),
            BattleDamage = new Observation<IReadOnlyList<CharacterDamageState>>
            {
                Status = ObservationStatus.Unknown,
                Value = [row],
                Uncertainty = ["one avatar unresolved"],
                ObservedAt = capturedAt
            },
            BattleSynergyDamage = Observation<IReadOnlyList<SynergyDamageState>>.Known([], 1),
            BattleUnresolvedDamage = Observation<IReadOnlyList<UnresolvedDamageSourceState>>.Known([], 1),
            BattleScreenDamageCandidate = Observation<long>.Known(456_000, 0.68),
            RemainingActionValue = Observation<RemainingActionValueState>.Known(
                RemainingActionValueState.Create(1, 28),
                0.9)
        };

        var first = tracker.Observe(partial);
        var second = tracker.Observe(partial with
        {
            BattleDamage = partial.BattleDamage with
            {
                Value = [row with { Damage = 500_000, RawText = "50万" }]
            },
            BattleScreenDamageCandidate = Observation<long>.Known(500_000, 0.68)
        });
        Assert.Equal(
            "unknown-character-1",
            Assert.Single(first.Current.BattleDamage.Value!).CharacterId);
        Assert.Equal(
            "unknown-character-1",
            Assert.Single(second.Current.BattleDamage.Value!).CharacterId);

        tracker.Observe(PreparationState("2-1", 30));
        var finalized = tracker.Observe(PreparationState("2-1", 30)).FinalizedBattle;
        Assert.NotNull(finalized);
        Assert.False(finalized!.IsComplete);
        Assert.False(finalized.CanDriveDecisions);
        var finalRow = Assert.Single(finalized.CharacterDamage);
        Assert.Equal("unknown-character-1", finalRow.CharacterId);
        Assert.Equal(500_000, finalRow.Damage);
        Assert.False(finalRow.CanDriveDecisions);
    }

    [Fact]
    public void UnknownSpecialDamageSourceIsPreservedWithoutBeingCalledCharacter()
    {
        var tracker = new Phase2OperationalStateTracker();
        var capturedAt = DateTimeOffset.Parse("2026-07-29T11:10:00+08:00");
        var evidence = new EvidenceReference(
            "fixture:unknown-special-unit",
            "crop:battle-damage-source:2",
            CapturedAt: capturedAt,
            Confidence: 0.3);
        var unresolved = new UnresolvedDamageSourceState(
            2,
            "unknown-damage-source-slot-2",
            BattleDamageSourceKind.Unknown,
            null,
            120_000,
            "12万",
            0.3,
            0.68,
            new RelativeRegion(0.85, 0.28, 0.03, 0.05),
            new RelativeRegion(0.88, 0.28, 0.06, 0.04),
            ["special_unit_gemi_li", "bond_unknown"],
            "could be a special unit or synergy",
            evidence);
        var battle = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Battle,
            NodeId = Observation<string>.Known("1-9", 0.9),
            BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known([], 1),
            BattleSynergyDamage = Observation<IReadOnlyList<SynergyDamageState>>.Known([], 1),
            BattleUnresolvedDamage = new Observation<IReadOnlyList<UnresolvedDamageSourceState>>
            {
                Status = ObservationStatus.Unknown,
                Value = [unresolved],
                Uncertainty = [unresolved.FailureReason]
            },
            BattleScreenDamageCandidate = Observation<long>.Known(120_000, 0.68),
            RemainingActionValue = Observation<RemainingActionValueState>.Known(
                RemainingActionValueState.Create(1, 10),
                0.9)
        };
        tracker.Observe(battle);
        tracker.Observe(battle);
        tracker.Observe(PreparationState("2-1", 30));
        var finalized = tracker.Observe(PreparationState("2-1", 30)).FinalizedBattle;

        Assert.NotNull(finalized);
        Assert.Empty(finalized!.CharacterDamage);
        var finalUnknown = Assert.Single(finalized.FinalUnresolvedDamage);
        Assert.Equal(120_000, finalUnknown.Damage);
        Assert.Equal("unknown-damage-source-1", finalUnknown.TemporaryId);
        Assert.Equal(120_000, finalized.AllRecordedDamage);
        Assert.False(finalized.CanDriveDecisions);
    }

    [Fact]
    public async Task UnknownPagePreservesReadableIndependentRegions()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new ThrowingCharacterRecognizer(),
            LoadCharacterTemplates(dataDirectory, gameData),
            new ThrowingIconRecognizer(),
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new StaticTextOcr("局部可见"),
            gameData,
            new StaticTextOcr("局部可见"));
        var frame = LoadReference("125924.png");

        var state = await analyzer.AnalyzeAsync(
            frame,
            "unknown",
            "fixture:unknown-page-partial",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        Assert.Equal(Phase2PageFamily.Unknown, state.PageFamily);
        Assert.NotEmpty(state.PartialFields);
        Assert.All(state.PartialFields, item =>
        {
            Assert.False(item.CanDriveDecisions);
            Assert.Equal("局部可见", item.RecognizedFields["text"]);
            Assert.NotEqual(string.Empty, item.TemporaryId);
            Assert.NotEmpty(item.FailureReason);
        });
    }

    [Fact]
    public void RemainingActionValueUsesRoundTimesOneHundredPlusCounter()
    {
        Assert.Equal(200, RemainingActionValueState.Create(1, 100).TotalActionValue);
        Assert.Equal(128, RemainingActionValueState.Create(1, 28).TotalActionValue);
    }

    [Fact]
    public void NodeRetentionKeepsOnlyLatestPreparationAndFinalBattleEvidence()
    {
        Assert.True(Phase2NodeRetentionPolicy.ShouldBufferPreparation(
            Phase2PageFamily.Preparation,
            confirmed: true));
        Assert.False(Phase2NodeRetentionPolicy.ShouldBufferPreparation(
            Phase2PageFamily.Preparation,
            confirmed: false));
        Assert.True(Phase2NodeRetentionPolicy.ShouldFlushPreparation(
            Phase2PageFamily.Battle));
        Assert.False(Phase2NodeRetentionPolicy.ShouldFlushPreparation(
            Phase2PageFamily.Unknown));

        Assert.False(Phase2NodeRetentionPolicy.ShouldPersistCurrent(
            Phase2PageFamily.Preparation,
            recognizedPage: true,
            changed: true,
            confirmed: true,
            finalizedBattle: false,
            checkpointDue: true));
        Assert.False(Phase2NodeRetentionPolicy.ShouldPersistCurrent(
            Phase2PageFamily.Battle,
            recognizedPage: true,
            changed: true,
            confirmed: true,
            finalizedBattle: false,
            checkpointDue: true));
        Assert.True(Phase2NodeRetentionPolicy.ShouldPersistCurrent(
            Phase2PageFamily.BattleSettlement,
            recognizedPage: true,
            changed: true,
            confirmed: true,
            finalizedBattle: true,
            checkpointDue: false));
        Assert.True(Phase2NodeRetentionPolicy.ShouldPersistCurrent(
            Phase2PageFamily.Preparation,
            recognizedPage: true,
            changed: true,
            confirmed: true,
            finalizedBattle: true,
            checkpointDue: false));

        Assert.True(Phase2NodeRetentionPolicy.ShouldPersistDegraded(
            recognizedPage: false,
            critical: true,
            enteringUnknown: false,
            checkpointDue: false));
        Assert.True(Phase2NodeRetentionPolicy.ShouldPersistDegraded(
            recognizedPage: false,
            critical: false,
            enteringUnknown: true,
            checkpointDue: false));
        Assert.True(Phase2NodeRetentionPolicy.ShouldPersistDegraded(
            recognizedPage: false,
            critical: false,
            enteringUnknown: false,
            checkpointDue: true));
        Assert.False(Phase2NodeRetentionPolicy.ShouldPersistDegraded(
            recognizedPage: true,
            critical: true,
            enteringUnknown: true,
            checkpointDue: true));
    }

    [Fact]
    public void CaptureFailuresReceiveBoundedRecoveryTimeInsteadOfStoppingAtFive()
    {
        var started = DateTimeOffset.Parse("2026-07-31T12:00:00+08:00");

        Assert.False(Phase2LiveCollectionService.FailureRecoveryExpired(
            started,
            started.AddSeconds(30)));
        Assert.False(Phase2LiveCollectionService.FailureRecoveryExpired(
            started,
            started.AddMinutes(1).AddSeconds(59)));
        Assert.True(Phase2LiveCollectionService.FailureRecoveryExpired(
            started,
            started.Add(
                Phase2LiveCollectionService.MaximumFailureRecoveryDuration)));
        Assert.Equal(
            TimeSpan.FromMilliseconds(750),
            Phase2LiveCollectionService.FailureRecoveryDelay(5));
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            Phase2LiveCollectionService.FailureRecoveryDelay(100));
    }

    [Fact]
    public async Task PersistedUnknownEvidenceReferencesOnlyCropsThatExist()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.Parse("2026-08-01T12:00:00+08:00");
            var evidence = new EvidenceReference(
                "fixture:unknown-icon",
                "region:investment-slot-2",
                CapturedAt: now,
                Confidence: 0.42);
            var analysis = new ScreenshotAnalysisResult
            {
                AnalysisId = "unknown-crops",
                Snapshot = new RunSnapshot
                {
                    RunId = "run-unknown-crops",
                    AsOf = now,
                    PageId = Observation<string>.Known(
                        "preparation_generic",
                        0.9,
                        observedAt: now),
                    Stage = Observation<string>.Known("1-3", 0.9, observedAt: now)
                },
                OperationalState = new Phase2OperationalState
                {
                    PageFamily = Phase2PageFamily.Preparation,
                    PageId = "preparation_generic",
                    NodeId = Observation<string>.Known("1-3", 0.9, observedAt: now),
                    PendingIcons =
                    [
                        new PendingIconObservation(
                            PendingIconCategory.InvestmentStrategy,
                            "strategy/slot:2",
                            new RelativeRegion(0.4, 0.1, 0.08, 0.08),
                            null,
                            0.42,
                            evidence,
                            "unknown")
                    ],
                    RecognitionTrace =
                    [
                        new Phase2FieldRecognitionTrace(
                            "investment.strategy[2]",
                            "1-3",
                            "preparation_generic",
                            [],
                            null,
                            ObservationStatus.Unknown,
                            0.42,
                            1,
                            "no template matched",
                            new RelativeRegion(0.4, 0.1, 0.08, 0.08),
                            now)
                    ]
                }
            };
            var store = new LocalRunStore(root);
            var service = new Phase2LiveCollectionService(
                null!,
                null!,
                null!,
                store);
            var frame = EmptyFrame(1600, 900);

            await service.SaveObservationAsync(
                frame,
                "capture-unknown.png",
                analysis,
                null,
                CancellationToken.None);

            var persisted = Assert.Single(await store.LoadAnalysesAsync(
                "run-unknown-crops",
                CancellationToken.None));
            var pending = Assert.Single(persisted.OperationalState!.PendingIcons);
            var trace = Assert.Single(persisted.OperationalState.RecognitionTrace);
            Assert.False(string.IsNullOrWhiteSpace(pending.CropFile));
            Assert.False(string.IsNullOrWhiteSpace(trace.CropFile));
            Assert.True(File.Exists(Path.Combine(
                store.GetRunDirectory("run-unknown-crops"),
                pending.CropFile!.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(File.Exists(Path.Combine(
                store.GetRunDirectory("run-unknown-crops"),
                trace.CropFile!.Replace('/', Path.DirectorySeparatorChar))));
            Assert.DoesNotContain('/', Path.GetFileName(pending.CropFile));
            Assert.DoesNotContain(':', Path.GetFileName(pending.CropFile));
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
    public async Task ApplicationManifestRequestsAdministratorElevation()
    {
        var manifest = await File.ReadAllTextAsync(Path.Combine(
            RepositoryRoot,
            "src",
            "CurrencyWarsAssistant.App",
            "app.manifest"));

        // 必须请求管理员权限：游戏（星穹铁道）通常以管理员（高完整性）运行，
        // 普通权限软件向管理员游戏注入鼠标/键盘会被 Windows UIPI 拦截
        // （0.2.771-0.2.775 刷开局"光标不动"的根因）。旧版 build04 与 BGI 均
        // 在启动时申请管理员权限。
        Assert.Contains(
            "level=\"requireAdministrator\"",
            manifest,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalNodeBattleIsStoredSeparatelyFromIntermediateFrames()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var state = BattleState("2-4", [311_000, 138_000], 1, 28);
            var rows = state.BattleDamage.Value!;
            var final = new FinalNodeBattleState(
                "2-4",
                rows,
                rows.Sum(item => item.Damage),
                state.RemainingActionValue.Value!,
                rows[0].Evidence.CapturedAt!.Value,
                rows[0].Evidence,
                BattleScreenDamageCandidate: 449_000,
                SettlementScreenDamageCandidate: 460_000,
                SelectedDamage: 460_000,
                SelectedDamageSource: FinalDamageSelectionSource.SettlementTopThree,
                SettlementTopThree: rows,
                GoldReward: 9);

            await store.SaveFinalNodeBattleAsync(
                "phase2-store",
                final,
                CancellationToken.None);

            var file = Path.Combine(
                root,
                "phase2-store",
                "nodes",
                "node-2-4-final.json");
            Assert.True(File.Exists(file));
            var json = await File.ReadAllTextAsync(file);
            Assert.Contains("449000", json, StringComparison.Ordinal);
            Assert.Contains("460000", json, StringComparison.Ordinal);
            Assert.Contains("settlementTopThree", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"goldReward\": 9", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("128", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Phase2OperationalState PreparationState(
        string node,
        int economy) => new()
    {
        PageFamily = Phase2PageFamily.Preparation,
        NodeId = Observation<string>.Known(node, 0.9),
        Interest = Observation<int>.Known(Math.Min(economy / 10, 5), 0.9)
    };

    private static Phase2OperationalState PreparationStateWithFormation(
        DateTimeOffset capturedAt)
    {
        var evidence = new EvidenceReference(
            $"fixture:{capturedAt:HHmmss}",
            "vision:formation",
            CapturedAt: capturedAt,
            Confidence: capturedAt.Second % 2 == 0 ? 0.91 : 0.94);
        var formation = new[]
        {
            new FormationCharacterState(
                FormationZone.Front,
                0,
                "currency_wars_character_01",
                1,
                "front",
                ["currency_wars_equipment_001"],
                evidence.Confidence ?? 0.9,
                evidence)
        };
        return PreparationState("1-8", 21) with
        {
            Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                formation,
                evidence.Confidence ?? 0.9,
                [evidence],
                capturedAt),
            ActiveSynergies = Observation<IReadOnlyList<ActiveSynergyState>>.Known(
                [new ActiveSynergyState(
                    "bond_test",
                    2,
                    4,
                    "synergy-1",
                    evidence.Confidence ?? 0.9,
                    evidence)],
                evidence.Confidence ?? 0.9,
                [evidence],
                capturedAt)
        };
    }

    private static Phase2OperationalState BattleState(
        string node,
        IReadOnlyList<long> damage,
        int rounds,
        int actionValue)
    {
        var capturedAt = DateTimeOffset.Parse("2026-07-28T13:00:00+08:00");
        var evidence = new EvidenceReference(
            "fixture",
            "vision:battle-damage",
            CapturedAt: capturedAt,
            Confidence: 0.9);
        var rows = damage.Select((value, index) => new CharacterDamageState(
            index + 1,
            $"currency_wars_character_{index + 1:00}",
            value,
            value.ToString(),
            0.9,
            0.9,
            new RelativeRegion(0.85, 0.18 + index * 0.05, 0.03, 0.04),
            new RelativeRegion(0.88, 0.18 + index * 0.05, 0.08, 0.04),
            evidence)).ToArray();
        return new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Battle,
            NodeId = Observation<string>.Known(node, 0.9),
            BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
                rows,
                0.9),
            BattleSynergyDamage = Observation<IReadOnlyList<SynergyDamageState>>.Known(
                [],
                1),
            BattleUnresolvedDamage = Observation<IReadOnlyList<UnresolvedDamageSourceState>>.Known(
                [],
                1),
            BattleScreenDamageCandidate = Observation<long>.Known(
                rows.Sum(item => item.Damage),
                0.9),
            RemainingActionValue = Observation<RemainingActionValueState>.Known(
                RemainingActionValueState.Create(rounds, actionValue),
                0.9)
        };
    }

    private static Phase2OperationalState BattleContextState(
        string node,
        int rounds,
        int actionValue) => new()
    {
        PageFamily = Phase2PageFamily.Battle,
        NodeId = Observation<string>.Known(node, 0.9),
        RemainingActionValue = Observation<RemainingActionValueState>.Known(
            RemainingActionValueState.Create(rounds, actionValue),
            0.9)
    };

    private static Phase2OperationalState PreparationStateWithCharacter(
        string node,
        int economy,
        string characterId,
        int? starLevel,
        FormationZone zone)
    {
        var evidence = new EvidenceReference(
            $"fixture:{node}:{characterId}",
            "vision:formation",
            CapturedAt: DateTimeOffset.Parse("2026-07-30T12:00:00+08:00"),
            Confidence: 0.95);
        var character = new FormationCharacterState(
            zone,
            0,
            characterId,
            starLevel,
            zone == FormationZone.Front ? "front" : "back",
            [],
            0.95,
            evidence);
        return PreparationState(node, economy) with
        {
            Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [character],
                0.95,
                [evidence],
                evidence.CapturedAt)
        };
    }

    private static Phase2OperationalState SettlementState(
        string node,
        IReadOnlyList<long> damage,
        int gold,
        bool identitiesKnown = true)
    {
        var capturedAt = DateTimeOffset.Parse("2026-07-28T13:01:00+08:00");
        var evidence = new EvidenceReference(
            "fixture:settlement",
            "vision:settlement-damage",
            CapturedAt: capturedAt,
            Confidence: 0.9);
        var rows = damage.Select((value, index) =>
        {
            var known = new CharacterDamageState(
                index + 1,
                $"currency_wars_character_{index + 1:00}",
                value,
                value.ToString(),
                0.9,
                0.9,
                new RelativeRegion(0.58, 0.56 + index * 0.075, 0.04, 0.06),
                new RelativeRegion(0.62, 0.57 + index * 0.075, 0.10, 0.05),
                evidence);
            return identitiesKnown
                ? known
                : known with
                {
                    CharacterId = $"unknown-settlement-character-{index + 1}",
                    TemporaryId = $"unknown-settlement-character-{index + 1}",
                    CandidateCharacterIds = [],
                    FailureReason = "avatar unresolved",
                    CanDriveDecisions = false
                };
        }).ToArray();
        return new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.BattleSettlement,
            NodeId = Observation<string>.Known(node, 0.9),
            SettlementDamage = identitiesKnown
                ? Observation<IReadOnlyList<CharacterDamageState>>.Known(rows, 0.9)
                : new Observation<IReadOnlyList<CharacterDamageState>>
                {
                    Status = ObservationStatus.Unknown,
                    Value = rows,
                    Confidence = 0.6,
                    Evidence = [evidence],
                    Uncertainty = ["one or more settlement avatars unresolved"],
                    ObservedAt = capturedAt
                },
            SettlementScreenDamageCandidate = Observation<long>.Known(
                rows.Sum(item => item.Damage),
                0.9),
            SettlementGoldReward = Observation<int>.Known(gold, 0.9)
        };
    }

    private static Observation<long> PartialLong(long value, string reason) =>
        new()
        {
            Status = ObservationStatus.Unknown,
            Value = value,
            Confidence = 0.4,
            Uncertainty = [reason]
        };

    private static FinalNodeBattleState? ObserveUntilFinalized(
        Phase2OperationalStateTracker tracker,
        Phase2OperationalState settlement,
        int maximumFrames = 20)
    {
        for (var index = 0; index < maximumFrames; index++)
        {
            var finalized = tracker.Observe(settlement).FinalizedBattle;
            if (finalized is not null)
            {
                return finalized;
            }
        }

        return null;
    }

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(
            string dataDirectory,
            GameDataCatalog gameData)
    {
        var templateDirectory = Path.Combine(
            dataDirectory,
            "character-card-templates");
        var templates = gameData.CurrencyWarsCharacters
            .Select(character => new CharacterCardTemplateDefinition(
                character.Id,
                character.Name,
                Directory.GetFiles(
                    templateDirectory,
                    $"{character.Id}__*.png").Single()))
            .ToList();
        templates.Add(new CharacterCardTemplateDefinition(
            "bench_special_privilege_armament_box",
            "特权武装箱",
            Path.Combine(
                templateDirectory,
                "bench_special_privilege_armament_box.png"),
            CharacterCardTemplateKind.SpecialOccupied));
        return templates;
    }

    private static Phase2OperationalScreenshotAnalyzer CreateAnalyzer(
        ICharacterCardRecognizer characterRecognizer,
        IPhase2IconRecognizer iconRecognizer)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        return new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            LoadCharacterTemplates(dataDirectory, gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr(),
            gameData,
            new WindowsOfflineOcr("en-US"),
            pageClassifier: CreatePageClassifier());
    }

    private static Phase2OperationalScreenshotAnalyzer CreateRealtimeAnalyzer(
        ICharacterCardRecognizer characterRecognizer,
        IPhase2IconRecognizer iconRecognizer)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        return new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            LoadCharacterTemplates(dataDirectory, gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr(
                "zh-Hans",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 4),
            gameData,
            new WindowsOfflineOcr(
                "en-US",
                OfflineOcrRecognitionMode.Fast,
                maximumConcurrency: 4),
            pageClassifier: CreatePageClassifier(),
            enableRobustFallback: false);
    }

    private static IGamePageClassifier CreatePageClassifier()
    {
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        return new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
    }

    private static CaptureFrame LoadReference(string fileName) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-2026-07-28",
            fileName));

    private static CaptureFrame LoadPageReplay(string fileName) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            fileName));

    private static CaptureFrame LoadLiveCapture(string fileName) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            fileName));

    private sealed record SettlementReplayCase(
        string FileName,
        string ExpectedPageId,
        bool IsWholeRunCompletion,
        string? TrackedNodeId = null);

    private static CaptureFrame ApplyOpaqueOcclusion(
        CaptureFrame source,
        NormalizedRect normalized)
    {
        var pixels = source.BgraPixels.ToArray();
        var region = normalized.ToPixels(source.Width, source.Height);
        for (var y = region.Y; y < region.Bottom; y++)
        {
            for (var x = region.X; x < region.Right; x++)
            {
                var offset = y * source.Stride + x * 4;
                pixels[offset] = 12;
                pixels[offset + 1] = 12;
                pixels[offset + 2] = 16;
                pixels[offset + 3] = 255;
            }
        }

        return source with { BgraPixels = pixels };
    }

    private static CaptureFrame PadToExact16By9(CaptureFrame source)
    {
        if (source.Width * 9 == source.Height * 16)
        {
            return source;
        }

        // Clipboard exports can omit a few outer border pixels. Preserve the
        // complete captured image and add only an inert outer canvas; never
        // crop, stretch, or geometrically alter the game UI under test.
        var scale = Math.Max(
            (int)Math.Ceiling(source.Width / 16d),
            (int)Math.Ceiling(source.Height / 9d));
        var width = scale * 16;
        var height = scale * 9;
        var stride = width * 4;
        var pixels = new byte[checked(stride * height)];
        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
        }

        var offsetX = (width - source.Width) / 2;
        var offsetY = (height - source.Height) / 2;
        for (var y = 0; y < source.Height; y++)
        {
            Buffer.BlockCopy(
                source.BgraPixels,
                y * source.Stride,
                pixels,
                (y + offsetY) * stride + offsetX * 4,
                source.Width * 4);
        }

        return new CaptureFrame(
            width,
            height,
            stride,
            pixels,
            new PixelRect(0, 0, width, height),
            source.CapturedAt);
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset capturedAt) => new()
    {
        RunId = "phase2-fixture",
        AsOf = capturedAt
    };

    private static CaptureFrame LoadTemplateOnOpaqueBackground(string path)
    {
        using var source = Cv2.ImDecode(
            File.ReadAllBytes(path),
            ImreadModes.Unchanged);
        Assert.False(source.Empty(), path);
        using var bgr = new Mat(
            source.Height,
            source.Width,
            MatType.CV_8UC3,
            new Scalar(12, 12, 18));
        if (source.Channels() == 4)
        {
            using var sourceBgr = new Mat();
            Cv2.CvtColor(source, sourceBgr, ColorConversionCodes.BGRA2BGR);
            using var alpha = new Mat();
            Cv2.ExtractChannel(source, alpha, 3);
            sourceBgr.CopyTo(bgr, alpha);
        }
        else
        {
            source.CopyTo(bgr);
        }

        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked((int)(bgra.Total() * bgra.ElemSize()))];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Width,
            bgra.Height,
            bgra.Width * 4,
            pixels,
            new PixelRect(0, 0, bgra.Width, bgra.Height),
            DateTimeOffset.UtcNow);
    }

    private static CaptureFrame EmptyFrame(int width, int height) => new(
        width,
        height,
        width * 4,
        new byte[checked(width * height * 4)],
        new PixelRect(0, 0, width, height),
        DateTimeOffset.UtcNow);

    private static CaptureFrame FrameWithChangedStableRegions(
        int width,
        int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        foreach (var normalized in new[]
                 {
                     Phase2RecognitionRegions.PreparationAffixes,
                     Phase2RecognitionRegions.InvestmentSlots
                 })
        {
            var region = normalized.ToPixels(width, height);
            for (var y = region.Y; y < region.Bottom; y++)
            {
                for (var x = region.X; x < region.Right; x++)
                {
                    var value = (byte)(((x / 4 + y / 4) & 1) == 0 ? 240 : 16);
                    var offset = y * width * 4 + x * 4;
                    pixels[offset] = value;
                    pixels[offset + 1] = value;
                    pixels[offset + 2] = value;
                    pixels[offset + 3] = 255;
                }
            }
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }

    private static CaptureFrame ResizeReference(
        string fileName,
        int width,
        int height)
    {
        var path = Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-2026-07-28",
            fileName);
        using var source = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(width, height));
        using var bgra = new Mat();
        Cv2.CvtColor(resized, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked((int)(bgra.Total() * bgra.ElemSize()))];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }

    private static JsonElement[] ReadImportedManifest(string dataDirectory) =>
        File.ReadAllLines(Path.Combine(
                dataDirectory,
                "phase2-icon-assets",
                "asset-manifest.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private sealed class ThrowingOcr : IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated covered OCR region");
    }

    private sealed class NeverCompletingSituationAnalyzer :
        ISituationScreenshotAnalyzer
    {
        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId = null) =>
            new TaskCompletionSource<ScreenshotAnalysisResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }

    private sealed class FixedKnownPageSituationAnalyzer(string pageId) :
        ISituationScreenshotAnalyzer
    {
        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScreenshotAnalysisResult
            {
                AnalysisId = evidenceSourceId,
                Snapshot = EmptySnapshot(frame.CapturedAt) with
                {
                    RunId = runId ?? "batch-selection-test",
                    PageId = Observation<string>.Known(
                        pageId,
                        0.91,
                        observedAt: frame.CapturedAt)
                }
            });
        }
    }

    private sealed class StaticTextOcr(string text) : IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrTextResult(text, [text]));
    }

    private sealed class RegionSelectiveOcr(PixelRect target, string text) :
        IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(region == target
                ? new OcrTextResult(text, [text])
                : new OcrTextResult(string.Empty, []));
    }

    private sealed class ThrowingIconRecognizer : IPhase2IconRecognizer
    {
        public IReadOnlyList<Phase2IconRecognition> Recognize(
            CaptureFrame frame,
            string category,
            IReadOnlyList<NormalizedRect> slots,
            IReadOnlyList<Phase2IconTemplateDefinition> templates) =>
            throw new InvalidOperationException("simulated icon-region failure");
    }

    private sealed class EmptyIconRecognizer : IPhase2IconRecognizer
    {
        public IReadOnlyList<Phase2IconRecognition> Recognize(
            CaptureFrame frame,
            string category,
            IReadOnlyList<NormalizedRect> slots,
            IReadOnlyList<Phase2IconTemplateDefinition> templates) =>
            slots.Select((slot, index) => new Phase2IconRecognition(
                    index,
                    slot.ToPixels(frame.Width, frame.Height),
                    null,
                    0,
                    false,
                    [],
                    []))
                .ToArray();
    }

    private sealed class EmptyGoldDigitRecognizer : IGoldDigitRecognizer
    {
        public GoldDigitRecognition Recognize(
            CaptureFrame frame,
            IReadOnlyList<GoldDigitTemplateDefinition> templates,
            PixelRect referenceRegion) => new(null, 0, 0);
    }

    private sealed class FixedGoldDigitRecognizer(int value) : IGoldDigitRecognizer
    {
        public GoldDigitRecognition Recognize(
            CaptureFrame frame,
            IReadOnlyList<GoldDigitTemplateDefinition> templates,
            PixelRect referenceRegion) => new(value, 0.99, 0.10);
    }

    private sealed class FixedPageClassifier(string pageId) : IGamePageClassifier
    {
        public PageClassificationResult? Classify(CaptureFrame frame) => new(
            pageId,
            pageId,
            1,
            []);
    }

    private sealed class EmptyCharacterRecognizer : ICharacterCardRecognizer
    {
        public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            IReadOnlyList<PixelRect> referenceSlots) =>
            referenceSlots.Select((slot, index) =>
                    new CharacterCardSlotRecognition(
                        index,
                        slot,
                        CharacterCardSlotState.Empty,
                        null,
                        null,
                        1,
                        0,
                        0))
                .ToArray();
    }

    private sealed class SingleUncertainCharacterRecognizer(PixelRect referenceSlot)
        : ICharacterCardRecognizer
    {
        public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            IReadOnlyList<PixelRect> referenceSlots)
        {
            if (referenceSlots.Count == Phase2RecognitionRegions.BenchCharacterSlots1920.Count)
            {
                return referenceSlots.Select((slot, index) =>
                        new CharacterCardSlotRecognition(
                            index,
                            slot,
                            CharacterCardSlotState.Empty,
                            null,
                            null,
                            1,
                            0,
                            0))
                    .ToArray();
            }

            return referenceSlots.Select((slot, index) =>
                    index == 0
                        ? new CharacterCardSlotRecognition(
                            index,
                            referenceSlot,
                            CharacterCardSlotState.Uncertain,
                            "candidate-a",
                            "候选 A",
                            0.63,
                            0.61,
                            0.02,
                            "candidate-b")
                        : new CharacterCardSlotRecognition(
                            index,
                            slot,
                            CharacterCardSlotState.Empty,
                            null,
                            null,
                            1,
                            0,
                            0))
                .ToArray();
        }
    }

    private sealed class SingleSpecialCharacterRecognizer : ICharacterCardRecognizer
    {
        public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            IReadOnlyList<PixelRect> referenceSlots) =>
            referenceSlots.Select((slot, index) =>
                    index == 0 &&
                    referenceSlots.Count ==
                    Phase2RecognitionRegions.PreparationCharacterSlots1920.Count
                        ? new CharacterCardSlotRecognition(
                            index,
                            slot,
                            CharacterCardSlotState.SpecialOccupied,
                            null,
                            "特权武装箱",
                            0.92,
                            0.28,
                            36,
                            MatchedTemplateId:
                                "bench_special_privilege_armament_box")
                        : new CharacterCardSlotRecognition(
                            index,
                            slot,
                            CharacterCardSlotState.Empty,
                            null,
                            null,
                            1,
                            0,
                            0))
                .ToArray();
    }

    private sealed class LifecycleCountingIconRecognizer(
        string environmentId,
        IReadOnlyList<string> strategyIds) : IPhase2IconRecognizer
    {
        private readonly Dictionary<string, int> _counts =
            new(StringComparer.Ordinal);

        public int Count(string category) =>
            _counts.GetValueOrDefault(category);

        public IReadOnlyList<Phase2IconRecognition> Recognize(
            CaptureFrame frame,
            string category,
            IReadOnlyList<NormalizedRect> slots,
            IReadOnlyList<Phase2IconTemplateDefinition> templates)
        {
            _counts[category] = Count(category) + 1;
            return slots.Select((slot, index) =>
            {
                string? id = category switch
                {
                    "investment-environment" when index == 0 => environmentId,
                    "investment-strategy" when
                        index < Math.Min(
                            _counts[category],
                            strategyIds.Count) => strategyIds[index],
                    _ => null
                };
                return new Phase2IconRecognition(
                    index,
                    slot.ToPixels(frame.Width, frame.Height),
                    id,
                    id is null ? 0 : 0.95,
                    id is not null,
                    [],
                    []);
            }).ToArray();
        }
    }

    private sealed class ThrowingCharacterRecognizer : ICharacterCardRecognizer
    {
        public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            IReadOnlyList<PixelRect> referenceSlots) =>
            throw new InvalidOperationException("simulated character-region failure");
    }

    private static void AssertImportedMappingsMatch(
        IReadOnlyList<JsonElement> imported,
        string category,
        string currentDataPath)
    {
        using var currentDocument = JsonDocument.Parse(
            File.ReadAllText(currentDataPath));
        var expected = currentDocument.RootElement.EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString()!,
                item => item.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
        var actual = imported.Where(item => string.Equals(
                item.GetProperty("category").GetString(),
                category,
                StringComparison.Ordinal))
            .ToDictionary(
                item => item.GetProperty("id").GetString()!,
                item => item.GetProperty("name").GetString()!,
                StringComparer.Ordinal);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected, actual);
    }

    private static void AssertKnownOrExplicitlyUnknown<T>(
        Observation<T> observation,
        T expected)
    {
        if (observation.Status == ObservationStatus.Known)
        {
            Assert.Equal(expected, observation.Value);
            return;
        }

        Assert.Equal(ObservationStatus.Unknown, observation.Status);
        Assert.NotEmpty(observation.Uncertainty);
        Assert.NotEmpty(observation.Evidence);
    }
}

