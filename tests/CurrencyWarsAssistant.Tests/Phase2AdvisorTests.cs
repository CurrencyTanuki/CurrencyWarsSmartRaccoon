using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2AdvisorTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RuntimeGuideLoadsWithTraceableSourceAndStableIds()
    {
        var guides = new GuideRepository().LoadDirectory(GuideDirectory);

        var guide = Assert.Single(guides);
        Assert.Equal("guide-taptap-828363891523190942", guide.GuideId);
        Assert.Contains("currency_wars_character_01", guide.Signals.CoreCharacterIds);
        Assert.Equal("冻梨游研社", Assert.Single(guide.Sources).Author);
        Assert.All(
            guide.Rules.SelectMany(rule => rule.Sources),
            source => Assert.Equal(
                "taptap-828363891523190942",
                source.SourceId));
    }

    [Fact]
    public void AdvisorUsesRecognizedCoreAndKeepsSourceEvidence()
    {
        var guide = Assert.Single(
            new GuideRepository().LoadDirectory(GuideDirectory));
        var snapshot = KnownPreparationSnapshot(
            ["currency_wars_character_01", "currency_wars_character_67"],
            ["bond:列车同行", "bond:护盾"]);

        var result = new AdvisorEngine().Evaluate(
            snapshot,
            [guide],
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"));

        var recommendation = Assert.Single(result.Recommendations);
        Assert.False(recommendation.IsNoAction);
        Assert.Contains("保留姬子", recommendation.Action);
        Assert.Contains(
            recommendation.Sources,
            source => source.Locator.Contains("阵容优势", StringComparison.Ordinal));
    }

    [Fact]
    public void AdvisorDoesNotInventAdviceWhenPageIsUnknown()
    {
        var guide = Assert.Single(
            new GuideRepository().LoadDirectory(GuideDirectory));
        var snapshot = KnownPreparationSnapshot([], []) with
        {
            PageId = Observation<string>.Unknown("not recognized")
        };

        var result = new AdvisorEngine().Evaluate(
            snapshot,
            [guide],
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"));

        Assert.True(Assert.Single(result.Recommendations).IsNoAction);
    }

    [Fact]
    public void ReducerIsIdempotentAndUsesLaterReliableState()
    {
        var events = new[]
        {
            Event("e2", RunEventType.EconomyObserved, 2, 7),
            Event("e1", RunEventType.EconomyObserved, 1, 3),
            Event("e2", RunEventType.EconomyObserved, 2, 7)
        };

        var first = new RunEventReducer().Reduce(events).Snapshot;
        var second = new RunEventReducer().Reduce(events.Reverse()).Snapshot;

        Assert.Equal(7, first.Economy.Value);
        Assert.Equal(2, first.AppliedEventIds.Count);
        Assert.Equal(
            AdvisorJson.Serialize(first),
            AdvisorJson.Serialize(second));
    }

    [Fact]
    public void LaterRecognitionFailureMakesPreviousValueStale()
    {
        var known = Event("known", RunEventType.EconomyObserved, 1, 7);
        using var document = JsonDocument.Parse(
            "{\"status\":\"unknown\",\"uncertainty\":[\"blurred\"]}");
        var unknown = known with
        {
            EventId = "unknown",
            OccurredAt = T0.AddSeconds(2),
            ObservedAt = T0.AddSeconds(3),
            Confidence = 0,
            Uncertainty = ["blurred"],
            Payload = document.RootElement.Clone()
        };

        var snapshot = new RunEventReducer().Reduce([known, unknown]).Snapshot;

        Assert.Equal(ObservationStatus.Stale, snapshot.Economy.Status);
        Assert.Equal(7, snapshot.Economy.Value);
    }

    [Theory]
    [InlineData("preparation_1_1.jpg", "preparation_1_1", 3)]
    [InlineData("preparation_1_2.jpg", "preparation_1_2", 7)]
    public async Task RealPreparationScreenshotsProduceStructuredState(
        string fileName,
        string expectedStage,
        int expectedEconomy)
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var frame = CaptureFrameLoader.LoadFile(
            Path.Combine(FixtureDirectory, fileName));

        var result = await analyzer.AnalyzeAsync(
            frame,
            $"fixture:{fileName}",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal(expectedStage, result.Snapshot.Stage.Value);
        Assert.Equal(expectedEconomy, result.Snapshot.Economy.Value);
        Assert.Equal(ObservationStatus.Known, result.Snapshot.BenchCharacterIds.Status);
        Assert.NotEmpty(result.Snapshot.BenchCharacterIds.Value!);
        Assert.NotEmpty(result.RouteCandidates);
        Assert.DoesNotContain(
            result.Warnings,
            warning => warning.Contains("演示数据", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Different169ResolutionKeepsCharacterAndRouteRecognition()
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "preparation_five_cards_2048x1152.png"));

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:preparation_five_cards_2048x1152.png",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("preparation_1_1", result.Snapshot.Stage.Value);
        Assert.Contains(
            "currency_wars_character_01",
            result.Snapshot.LineupIds.Value!);
        Assert.Contains(
            result.Recommendations,
            recommendation => recommendation.Action.Contains("保留姬子", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompositeAnalyzerKeepsOverlayedHomePageOutOfBattlePipeline()
    {
        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var pageClassifier = CreatePageClassifier(matcher);
        var characterTemplates = LoadCharacterTemplates(catalog);
        var iconTemplates = Phase2IconTemplateCatalog.Load(DataDirectory);
        var operational = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            characterTemplates,
            iconRecognizer,
            iconTemplates,
            ocr,
            catalog,
            new WindowsOfflineOcr("en-US"),
            pageClassifier: pageClassifier,
            enableRobustFallback: false);
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            pageClassifier,
            characterRecognizer,
            characterTemplates,
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory,
            operational,
            new WindowsOfflineOcr("en-US"),
            iconTemplates);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "main_page_classifier_miss_2048x1152.png"));

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:overlayed-home",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("currency_wars_home", result.Snapshot.PageId.Value);
        Assert.Equal(Phase2PageFamily.Main, result.OperationalState?.PageFamily);
        Assert.Contains(result.Warnings, warning => warning.StartsWith(
            "recognition:classifier-miss",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealShopScreenshotProducesShopStateWithoutBeingTreatedAsPreparation()
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "reward_shop_after_two_purchases_2048x1152.png"));

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:reward_shop_after_two_purchases_2048x1152.png",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("reward_shop", result.Snapshot.PageId.Value);
        Assert.Equal("reward_shop", result.Snapshot.Stage.Value);
        Assert.Equal(ObservationStatus.Known, result.Snapshot.ShopCharacterIds.Status);
        Assert.NotEqual(ObservationStatus.Known, result.Snapshot.BoardCharacterIds.Status);
    }

    [Fact]
    public async Task EnemyOverviewResultIsCachedAfterAsyncOcrAndSurvivesNextPage()
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var selection = new AdvisorSelection(
            AdvisorMode.Auto,
            "stable",
            "4.4");
        var enemy = await analyzer.AnalyzeAsync(
            CaptureFrameLoader.LoadFile(Path.Combine(
                FixtureDirectory,
                "enemy_overview.jpg")),
            "fixture:enemy_overview.jpg",
            selection,
            CancellationToken.None,
            runId: "run-enemy-cache");
        var preparation = await analyzer.AnalyzeAsync(
            CaptureFrameLoader.LoadFile(Path.Combine(
                FixtureDirectory,
                "preparation_1_1.jpg")),
            "fixture:preparation_1_1.jpg",
            selection,
            CancellationToken.None,
            runId: "run-enemy-cache");

        Assert.Equal(ObservationStatus.Known, enemy.Snapshot.EnemyIds.Status);
        Assert.Equal(7, enemy.Snapshot.EnemyIds.Value!.Count);
        Assert.Equal(
            enemy.Snapshot.EnemyIds.Value,
            preparation.Snapshot.EnemyIds.Value);
    }

    [Fact]
    public async Task ChallengeSuccessPreservesHealthButDoesNotTreatTopThreeAsNodeDamage()
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "challenge_success_1_1.jpg"));

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:challenge_success_1_1.jpg",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("challenge_success", result.Snapshot.PageId.Value);
        Assert.Equal("node_complete", result.Snapshot.Stage.Value);
        Assert.True(
            result.Snapshot.Health.Status == ObservationStatus.Known,
            AdvisorJson.Serialize(result));
        Assert.Equal(82, result.Snapshot.Health.Value);
        Assert.Equal(
            ObservationStatus.Unknown,
            result.Snapshot.CurrentNodeDamage.Status);
    }

    [Fact]
    public async Task ChallengeFailurePreservesHealthWithoutInventingDamage()
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "challenge_failed.jpg"));

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:challenge_failed.jpg",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("challenge_failed", result.Snapshot.PageId.Value);
        Assert.Equal("node_failed", result.Snapshot.Stage.Value);
        Assert.Equal(80, result.Snapshot.Health.Value);
        Assert.NotEqual(
            ObservationStatus.Known,
            result.Snapshot.CurrentNodeDamage.Status);
    }

    [Fact]
    public async Task HealthDepletedChallengeEndDoesNotTurnNegativeDeficitIntoHealth()
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "challenge_failed_2_5_health_depleted_user.png"));

        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:challenge_failed_2_5_health_depleted_user.png",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("challenge_health_depleted", result.Snapshot.PageId.Value);
        Assert.Equal("node_failed", result.Snapshot.Stage.Value);
        Assert.Equal(ObservationStatus.Unknown, result.Snapshot.Health.Status);
        Assert.Contains(result.Snapshot.Health.Uncertainty, item =>
            item.Contains("生命值已耗尽", StringComparison.Ordinal));
        Assert.NotEqual(
            ObservationStatus.Known,
            result.Snapshot.CurrentNodeDamage.Status);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(2048, 1152)]
    public async Task ChallengeSuccessHealthScalesWithoutInventingNodeDamage(
        int width,
        int height)
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var source = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "challenge_success_1_1.jpg"));
        var frame = Resize(source, width, height);

        var result = await analyzer.AnalyzeAsync(
            frame,
            $"fixture:challenge-success-{width}x{height}",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("challenge_success", result.Snapshot.PageId.Value);
        Assert.True(
            result.Snapshot.Health.Status == ObservationStatus.Known,
            AdvisorJson.Serialize(result));
        Assert.Equal(82, result.Snapshot.Health.Value);
        Assert.Equal(
            ObservationStatus.Unknown,
            result.Snapshot.CurrentNodeDamage.Status);
    }

    [Fact]
    public async Task MissingZeroGlyphKeepsTotalDamageUnknownInsteadOfAssumingZero()
    {
        using var recognizer = new OpenCvCharacterCardRecognizer();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var matcher = new OpenCvTemplateMatcher();
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            CreatePageClassifier(matcher),
            recognizer,
            LoadCharacterTemplates(catalog),
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory);
        var source = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "challenge_success_1_1.jpg"));
        var pixels = (byte[])source.BgraPixels.Clone();
        for (var y = 790; y < 824; y++)
        {
            for (var x = 1196; x < 1224; x++)
            {
                var offset = y * source.Stride + x * 4;
                pixels[offset] = 35;
                pixels[offset + 1] = 35;
                pixels[offset + 2] = 35;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        var frame = source with { BgraPixels = pixels };
        var result = await analyzer.AnalyzeAsync(
            frame,
            "fixture:challenge-success-zero-glyph-removed",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            CancellationToken.None);

        Assert.Equal("challenge_success", result.Snapshot.PageId.Value);
        Assert.Equal(
            ObservationStatus.Unknown,
            result.Snapshot.CurrentNodeDamage.Status);
    }

    [Fact]
    public async Task LocalRunStorePreservesRawAnalysisEvidenceAndStructuredEvents()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalRunStore(root);
            var snapshot = KnownPreparationSnapshot(
                ["currency_wars_character_01"],
                ["bond:列车同行"]);
            var result = new ScreenshotAnalysisResult
            {
                AnalysisId = "analysis-1",
                Snapshot = snapshot,
                Warnings = ["economy was not visible on this fixture"]
            };
            var runEvent = Event(
                "event-1",
                RunEventType.EconomyObserved,
                1,
                7);

            await store.SaveAnalysisAsync(result, CancellationToken.None);
            await store.AppendEventAsync(runEvent, CancellationToken.None);

            var runDirectory = store.GetRunDirectory(snapshot.RunId);
            var analysisJson = await File.ReadAllTextAsync(Path.Combine(
                runDirectory,
                "analysis-analysis-1.json"));
            var eventLines = await File.ReadAllLinesAsync(Path.Combine(
                store.GetRunDirectory(runEvent.RunId),
                "events.jsonl"));
            Assert.Contains("economy was not visible", analysisJson, StringComparison.Ordinal);
            Assert.Single(eventLines);
            Assert.Contains("event-1", eventLines[0], StringComparison.Ordinal);
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
    public void SnapshotFingerprintIgnoresRosterOrderingButDetectsStateChanges()
    {
        var first = KnownPreparationSnapshot(
            ["currency_wars_character_01", "currency_wars_character_67"],
            ["bond:护盾", "bond:列车同行"]);
        var reordered = KnownPreparationSnapshot(
            ["currency_wars_character_67", "currency_wars_character_01"],
            ["bond:列车同行", "bond:护盾"]);
        var economyChanged = first with
        {
            Economy = Observation<int>.Known(8, 1)
        };

        Assert.Equal(
            LocalRunStore.Fingerprint(first),
            LocalRunStore.Fingerprint(reordered));
        Assert.NotEqual(
            LocalRunStore.Fingerprint(first),
            LocalRunStore.Fingerprint(economyChanged));
    }

    [Fact]
    public void VisionAssetInventoryMakesLocalCoverageAndMissingAssetsExplicit()
    {
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        var report = new VisionAssetInventoryBuilder().Build(
            catalog,
            Path.Combine(RepositoryRoot, "data"),
            T0);

        Assert.Equal(
            83,
            report.Items.Count(item =>
                item.Category == VisionAssetCategory.InvestmentEnvironment));
        Assert.Equal(
            334,
            report.Items.Count(item =>
                item.Category == VisionAssetCategory.InvestmentStrategy));
        Assert.Equal(
            157,
            report.Items.Count(item =>
                item.Category == VisionAssetCategory.Equipment &&
                item.Availability ==
                VisionAssetAvailability.LocalTemplateAvailable));
        Assert.Contains(
            report.Items,
            item => item.AssetId == "special_item:expert_invitation" &&
                    item.Availability ==
                    VisionAssetAvailability.UserScreenshotRequired);
        Assert.Contains(
            report.Items,
            item => item.Category == VisionAssetCategory.ExpertAdvisor &&
                    item.Availability !=
                    VisionAssetAvailability.LocalTemplateAvailable);
    }

    [Fact]
    public void SpecialItemConditionUsesKnownInventoryAndDoesNotGuessUnknownInventory()
    {
        var condition = new GuideCondition(
            "special_item",
            "contains_any",
            ["special_item:expert_invitation"],
            UnknownPolicy.RequireReview);
        var known = KnownPreparationSnapshot([], []) with
        {
            SpecialItemIds = Observation<IReadOnlyList<string>>.Known(
                ["special_item:expert_invitation"],
                0.95)
        };
        var unknown = KnownPreparationSnapshot([], []);

        Assert.Equal(
            TriState.True,
            new ConditionEvaluator().Evaluate(condition, known).Result);
        Assert.Equal(
            TriState.Unknown,
            new ConditionEvaluator().Evaluate(condition, unknown).Result);
    }

    private static RunSnapshot KnownPreparationSnapshot(
        IReadOnlyList<string> lineup,
        IReadOnlyList<string> synergies) => new()
    {
        RunId = "run-test",
        AsOf = T0,
        PageId = Observation<string>.Known("preparation_1_1", 1),
        Stage = Observation<string>.Known("preparation_1_1", 1),
        Economy = Observation<int>.Known(7, 1),
        LineupIds = Observation<IReadOnlyList<string>>.Known(lineup, 1),
        SynergyIds = Observation<IReadOnlyList<string>>.Known(synergies, 1)
    };

    private static RunEvent Event(
        string eventId,
        RunEventType eventType,
        int seconds,
        int value)
    {
        using var document = JsonDocument.Parse(
            $"{{\"status\":\"known\",\"value\":{value}}}");
        return new RunEvent
        {
            EventId = eventId,
            RunId = "run-1",
            EventType = eventType,
            OccurredAt = T0.AddSeconds(seconds),
            ObservedAt = T0.AddSeconds(seconds + 1),
            SourceAdapter = "test",
            Confidence = 1,
            Payload = document.RootElement.Clone()
        };
    }

    private static IGamePageClassifier CreatePageClassifier(
        ITemplateMatcher matcher)
    {
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        return new TemplateGamePageClassifier(matcher, config.Pages);
    }

    private static CaptureFrame Resize(
        CaptureFrame source,
        int width,
        int height)
    {
        var scaled = new TransformedBitmap(
            source.ToBitmapSource(),
            new ScaleTransform(
                width / (double)source.Width,
                height / (double)source.Height));
        scaled.Freeze();
        var stride = width * 4;
        var pixels = new byte[stride * height];
        scaled.CopyPixels(pixels, stride, 0);
        return new CaptureFrame(
            width,
            height,
            stride,
            pixels,
            new PixelRect(0, 0, width, height),
            source.CapturedAt);
    }

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(GameDataCatalog catalog)
    {
        var directory = Path.Combine(DataDirectory, "character-card-templates");
        var templates = catalog.CurrencyWarsCharacters
            .Select(character => new CharacterCardTemplateDefinition(
                character.Id,
                character.Name,
                Directory.GetFiles(
                    directory,
                    $"{character.Id}__*.png").Single()))
            .ToList();
        templates.Add(new CharacterCardTemplateDefinition(
            "bench_special_privilege_armament_box",
            "特权武装箱",
            Path.Combine(
                directory,
                "bench_special_privilege_armament_box.png"),
            CharacterCardTemplateKind.SpecialOccupied));
        return templates;
    }

    private static IReadOnlyList<GoldDigitTemplateDefinition>
        LoadGoldDigitTemplates()
    {
        var directory = Path.Combine(DataDirectory, "gold-digit-templates");
        return new[] { 3, 7 }
            .Select(digit => new GoldDigitTemplateDefinition(
                digit,
                Path.Combine(directory, $"digit_{digit}.png")))
            .ToArray();
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    private static string FixtureDirectory => Path.Combine(
        RepositoryRoot,
        "tests",
        "CurrencyWarsAssistant.Tests",
        "Fixtures",
        "PageReplay");

    private static string DataDirectory => Path.Combine(
        RepositoryRoot,
        "data",
        "4.4");

    private static string GuideDirectory => Path.Combine(
        RepositoryRoot,
        "data",
        "advisor",
        "1.0.0",
        "4.4",
        "guides");
}
