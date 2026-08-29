using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class StableFactDataChainTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-08-01T09:00:00+08:00");
    private static readonly EvidenceReference Evidence = new(
        "real-frame:test",
        "crop:stable-content",
        CapturedAt: CapturedAt);

    [Fact]
    public async Task EveryNewPreparationNodeArmsOneBoundedStrategyRescan()
    {
        var gameData = LoadGameData();
        var strategyIds = gameData.InvestmentStrategies.Take(2)
            .Select(item => item.Id)
            .ToArray();
        var recognizer = new GrowingStrategyRecognizer(
            gameData.InvestmentEnvironments[0].Id,
            strategyIds);
        var analyzer = CreateAnalyzer(gameData, recognizer);
        var frame = EmptyFrame();
        var snapshot = EmptySnapshot("node-rescan");

        analyzer.NotifyPageObserved(snapshot.RunId, "preparation_1_1");
        var first = await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "real-frame:node-1-1",
            snapshot,
            CancellationToken.None);
        analyzer.NotifyPageObserved(snapshot.RunId, "preparation_1_1");
        await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "real-frame:node-1-1-repeat",
            snapshot,
            CancellationToken.None);

        Assert.Equal(1, recognizer.StrategyScans);
        Assert.Equal([strategyIds[0]], first.InvestmentStrategyIds.Value);

        analyzer.NotifyPageObserved(snapshot.RunId, "preparation_1_2");
        var nextNode = await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_2",
            "real-frame:node-1-2",
            snapshot,
            CancellationToken.None);

        Assert.Equal(2, recognizer.StrategyScans);
        Assert.Equal(
            strategyIds.Order(StringComparer.Ordinal),
            nextNode.InvestmentStrategyIds.Value!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GenericPreparationPageRescansWhenNodeRegionChanges()
    {
        var gameData = LoadGameData();
        var strategyIds = gameData.InvestmentStrategies.Take(2)
            .Select(item => item.Id)
            .ToArray();
        var recognizer = new GrowingStrategyRecognizer(
            gameData.InvestmentEnvironments[0].Id,
            strategyIds);
        var analyzer = CreateAnalyzer(gameData, recognizer);
        var snapshot = EmptySnapshot("generic-node-rescan");
        var firstNode = FrameWithNodePattern(invert: false);
        var nextNode = FrameWithNodePattern(invert: true);

        await analyzer.AnalyzeAsync(
            firstNode,
            "preparation_generic",
            "real-frame:generic-node-first",
            snapshot,
            CancellationToken.None);
        await analyzer.AnalyzeAsync(
            firstNode,
            "preparation_generic",
            "real-frame:generic-node-repeat",
            snapshot,
            CancellationToken.None);
        Assert.Equal(1, recognizer.StrategyScans);

        await analyzer.AnalyzeAsync(
            nextNode,
            "preparation_generic",
            "real-frame:generic-node-change-candidate",
            snapshot,
            CancellationToken.None);
        Assert.Equal(1, recognizer.StrategyScans);
        var updated = await analyzer.AnalyzeAsync(
            nextNode,
            "preparation_generic",
            "real-frame:generic-node-change-confirmed",
            snapshot,
            CancellationToken.None);

        Assert.Equal(2, recognizer.StrategyScans);
        Assert.Equal(
            strategyIds.Order(StringComparer.Ordinal),
            updated.InvestmentStrategyIds.Value!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ConfirmedNodeOcrArmsStrategyRescanOnTheSameGenericFrame()
    {
        var gameData = LoadGameData();
        var strategyIds = gameData.InvestmentStrategies.Take(2)
            .Select(item => item.Id)
            .ToArray();
        var recognizer = new GrowingStrategyRecognizer(
            gameData.InvestmentEnvironments[0].Id,
            strategyIds);
        var numericOcr = new PreparationNodeOcr("1-1");
        var analyzer = CreateAnalyzer(gameData, recognizer, numericOcr);
        var frame = EmptyFrame();
        var snapshot = EmptySnapshot("ocr-node-rescan");

        await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "real-frame:ocr-node-1-1",
            snapshot,
            CancellationToken.None);
        await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "real-frame:ocr-node-1-1-repeat",
            snapshot,
            CancellationToken.None);
        Assert.Equal(1, recognizer.StrategyScans);

        numericOcr.NodeText = "1-2";
        var nextNode = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "real-frame:ocr-node-1-2",
            snapshot,
            CancellationToken.None);

        Assert.Equal(2, recognizer.StrategyScans);
        Assert.Equal("1-2", nextNode.NodeId.Value);
        Assert.Equal(
            strategyIds.Order(StringComparer.Ordinal),
            nextNode.InvestmentStrategyIds.Value!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PartialOpeningAffixesRetainIdsAndEvidenceButNeverBecomeKnown()
    {
        var gameData = LoadGameData();
        var partialAffixes = gameData.EnemyAffixes.Take(2)
            .Select(item => item.Id)
            .ToArray();
        var analyzer = CreateAnalyzer(
            gameData,
            new GrowingStrategyRecognizer(
                gameData.InvestmentEnvironments[0].Id,
                []));
        const string runId = "partial-affix-real-frame";
        analyzer.ObserveOpeningEnemyIds(
            runId,
            new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = partialAffixes,
                Confidence = 0,
                Evidence = [Evidence],
                Uncertainty = ["Only two of four affixes were readable."],
                ObservedAt = CapturedAt
            });

        var state = await analyzer.AnalyzeAsync(
            EmptyFrame(),
            "preparation_1_1",
            "real-frame:partially-visible-enemy-affixes",
            EmptySnapshot(runId),
            CancellationToken.None);

        Assert.Equal(ObservationStatus.Unknown, state.NegativeAffixIds.Status);
        Assert.Equal(partialAffixes, state.NegativeAffixIds.Value);
        Assert.Contains(Evidence, state.NegativeAffixIds.Evidence);
        Assert.NotEmpty(state.NegativeAffixIds.Uncertainty);
    }

    [Fact]
    public async Task RealPreparationFrameNeverPromotesPartialAffixSetToKnown()
    {
        var gameData = LoadGameData();
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            recognizer,
            Phase2IconTemplateCatalog.Load(Path.Combine(
                RepositoryRoot,
                "data",
                "4.4")),
            new EmptyOcr(),
            gameData,
            new EmptyOcr());
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "preparation-1-4-user-2026-08-01.png"));

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_1_1",
            "real-frame:preparation-1-4-user-2026-08-01",
            EmptySnapshot("real-partial-affix-frame"),
            CancellationToken.None);
        var count = state.NegativeAffixIds.Value?
            .Distinct(StringComparer.Ordinal)
            .Count() ?? 0;

        Assert.Equal(
            count == Phase2RecognitionRegions.NegativeAffixSlots.Count,
            state.NegativeAffixIds.Status == ObservationStatus.Known);
        if (count != Phase2RecognitionRegions.NegativeAffixSlots.Count)
        {
            Assert.Equal(
                ObservationStatus.Unknown,
                state.NegativeAffixIds.Status);
            Assert.NotEmpty(state.NegativeAffixIds.Uncertainty);
        }
    }

    [Fact]
    public async Task RealPreparationHudDoesNotTreatEmptyStrategySlotsAsIcons()
    {
        var gameData = LoadGameData();
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            recognizer,
            Phase2IconTemplateCatalog.Load(Path.Combine(
                RepositoryRoot,
                "data",
                "4.4")),
            new EmptyOcr(),
            gameData,
            new EmptyOcr());
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "run-171955-preparation-1-3-stable.png"));

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "real-frame:run-171955-preparation-1-3-stable",
            EmptySnapshot("real-strategy-empty-slots"),
            CancellationToken.None);
        var strategyContent = state.NamedContent
            .Where(item =>
                item.Kind == Phase2NamedContentKind.InvestmentStrategy)
            .ToArray();
        var strategyPending = state.PendingIcons
            .Where(item =>
                item.Category == PendingIconCategory.InvestmentStrategy)
            .ToArray();

        // This frame has one occupied strategy slot followed by two empty
        // slots. The occupied icon has no audited ground-truth ID yet, so this
        // test deliberately verifies occupancy only and never promotes the
        // recognizer's leading candidate to truth.
        Assert.Single(strategyContent);
        Assert.All(strategyContent, item =>
            Assert.Equal("InvestmentStrategy-1", item.SlotKey));
        Assert.All(strategyPending, item =>
            Assert.Equal("InvestmentStrategy-1", item.SlotKey));
    }

    [Fact]
    public void PartialStrategyScanAddsConfirmedIdsWithoutDiscardingPriorIds()
    {
        var tracker = new Phase2OperationalStateTracker();
        var first = PreparationState("1-1") with
        {
            InvestmentStrategyIds =
                Observation<IReadOnlyList<string>>.Known(
                    ["strategy-a"],
                    0.95,
                    [Evidence],
                    CapturedAt)
        };
        tracker.Observe(first);
        tracker.Observe(first);

        var partial = PreparationState("1-2") with
        {
            InvestmentStrategyIds = new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = ["strategy-b"],
                Confidence = 0,
                Evidence = [Evidence with { Locator = "crop:strategy-2" }],
                Uncertainty = ["A third occupied slot is unresolved."],
                ObservedAt = CapturedAt.AddSeconds(3)
            }
        };
        tracker.Observe(partial);
        var update = tracker.Observe(partial);

        Assert.Equal(
            ObservationStatus.Unknown,
            update.Current.InvestmentStrategyIds.Status);
        Assert.Equal(
            ["strategy-a", "strategy-b"],
            update.Current.InvestmentStrategyIds.Value);
        Assert.NotEmpty(update.Current.InvestmentStrategyIds.Uncertainty);
    }

    [Fact]
    public void ConfirmedEnvironmentCannotSilentlyChangeWithinOneRun()
    {
        var tracker = new Phase2OperationalStateTracker();
        var first = PreparationState("1-1") with
        {
            InvestmentEnvironmentId = Observation<string>.Known(
                "environment-a",
                0.94,
                [Evidence],
                CapturedAt)
        };
        tracker.Observe(first);
        tracker.Observe(first);

        var contradictory = PreparationState("1-2") with
        {
            InvestmentEnvironmentId = Observation<string>.Known(
                "environment-b",
                0.96,
                [Evidence with { Locator = "crop:environment-later" }],
                CapturedAt.AddSeconds(3))
        };
        tracker.Observe(contradictory);
        var update = tracker.Observe(contradictory);

        Assert.Equal(
            ObservationStatus.Conflict,
            update.Current.InvestmentEnvironmentId.Status);
        Assert.Equal(
            "environment-a",
            update.Current.InvestmentEnvironmentId.Value);
        Assert.Contains(
            update.Current.InvestmentEnvironmentId.Uncertainty,
            value => value.Contains("environment-b", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckpointPersistsPartialStableFactsWithoutPromotingIdentity()
    {
        var current = RunCheckpointFactory.CreateInitial(
            "partial-stable-checkpoint",
            RunEntryMode.DirectRecording,
            CapturedAt);
        var analysis = new ScreenshotAnalysisResult
        {
            AnalysisId = "real-frame:partial-stable-checkpoint",
            Snapshot = EmptySnapshot(current.RunId) with
            {
                InvestmentStrategyIds = PartialList("strategy-a")
            },
            OperationalState = PreparationState("1-2") with
            {
                NegativeAffixIds = PartialList("affix-a", "affix-b")
            }
        };

        var checkpoint = RunCheckpointFactory.FromAnalysis(
            current,
            analysis,
            1,
            RunCheckpointLifecycleStatus.Active,
            CapturedAt.AddSeconds(1));

        Assert.Equal(
            ["strategy-a"],
            checkpoint.LastSnapshot!.InvestmentStrategyIds.Value);
        Assert.Equal(
            ObservationStatus.Unknown,
            checkpoint.LastSnapshot.InvestmentStrategyIds.Status);
        Assert.Equal(
            ["affix-a", "affix-b"],
            checkpoint.LastOperationalState!.NegativeAffixIds.Value);
        Assert.Equal(
            ObservationStatus.Unknown,
            checkpoint.LastOperationalState.NegativeAffixIds.Status);
        Assert.Equal(
            ["strategy-a"],
            checkpoint.IdentityEvidence.InvestmentStrategyIds);
        Assert.Empty(checkpoint.IdentityEvidence.EnemyAffixIds);

        var later = analysis with
        {
            AnalysisId = "real-frame:partial-stable-checkpoint-later",
            Snapshot = analysis.Snapshot with
            {
                InvestmentStrategyIds = PartialList("strategy-b")
            }
        };
        checkpoint = RunCheckpointFactory.FromAnalysis(
            checkpoint,
            later,
            2,
            RunCheckpointLifecycleStatus.Active,
            CapturedAt.AddSeconds(2));

        Assert.Equal(
            ObservationStatus.Unknown,
            checkpoint.LastSnapshot!.InvestmentStrategyIds.Status);
        Assert.Equal(
            ["strategy-a", "strategy-b"],
            checkpoint.LastSnapshot.InvestmentStrategyIds.Value);
        Assert.Equal(
            ["strategy-a", "strategy-b"],
            checkpoint.IdentityEvidence.InvestmentStrategyIds);
    }

    private static Observation<IReadOnlyList<string>> PartialList(
        params string[] values) => new()
    {
        Status = ObservationStatus.Unknown,
        Value = values,
        Confidence = 0,
        Evidence = [Evidence],
        Uncertainty = ["Partial stable fact."],
        ObservedAt = CapturedAt
    };

    private static Phase2OperationalState PreparationState(string nodeId) => new()
    {
        PageFamily = Phase2PageFamily.Preparation,
        PageId = $"preparation_{nodeId}",
        NodeId = Observation<string>.Known(
            nodeId,
            0.95,
            [Evidence],
            CapturedAt)
    };

    private static RunSnapshot EmptySnapshot(string runId) => new()
    {
        RunId = runId,
        AsOf = CapturedAt
    };

    private static Phase2OperationalScreenshotAnalyzer CreateAnalyzer(
        GameDataCatalog gameData,
        IPhase2IconRecognizer iconRecognizer,
        IOfflineOcr? numericOcr = null) => new(
            new EmptyCharacterRecognizer(),
            [],
            iconRecognizer,
            [],
            new EmptyOcr(),
            gameData,
            numericOcr ?? new EmptyOcr());

    private static GameDataCatalog LoadGameData() =>
        GameDataCatalogLoader.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));

    private static CaptureFrame EmptyFrame() => new(
        1920,
        1080,
        1920 * 4,
        new byte[1920 * 1080 * 4],
        new PixelRect(0, 0, 1920, 1080),
        CapturedAt);

    private static CaptureFrame FrameWithNodePattern(bool invert)
    {
        const int width = 1920;
        const int height = 1080;
        var pixels = new byte[width * height * 4];
        var region = Phase2RecognitionRegions.PreparationNodeValue
            .ToPixels(width, height);
        for (var sampleY = 0; sampleY < 8; sampleY++)
        {
            var y = region.Y + Math.Min(
                region.Height - 1,
                (int)Math.Round(sampleY * (region.Height - 1) / 7d));
            for (var sampleX = 0; sampleX < 8; sampleX++)
            {
                var x = region.X + Math.Min(
                    region.Width - 1,
                    (int)Math.Round(sampleX * (region.Width - 1) / 7d));
                var high = ((sampleX + sampleY) & 1) == (invert ? 1 : 0);
                var value = high ? (byte)240 : (byte)16;
                var offset = y * width * 4 + x * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            CapturedAt);
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    private sealed class EmptyOcr : IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrTextResult(string.Empty, []));
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

    private sealed class PreparationNodeOcr(string nodeText) : IOfflineOcr
    {
        public bool IsAvailable => true;

        public string NodeText { get; set; } = nodeText;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken)
        {
            var expected = Phase2RecognitionRegions.PreparationNodeValue
                .ToPixels(frame.Width, frame.Height);
            return region == expected
                ? ValueTask.FromResult(new OcrTextResult(NodeText, [NodeText]))
                : ValueTask.FromResult(new OcrTextResult(string.Empty, []));
        }
    }

    private sealed class GrowingStrategyRecognizer(
        string environmentId,
        IReadOnlyList<string> strategyIds) : IPhase2IconRecognizer
    {
        public int StrategyScans { get; private set; }

        public IReadOnlyList<Phase2IconRecognition> Recognize(
            CaptureFrame frame,
            string category,
            IReadOnlyList<NormalizedRect> slots,
            IReadOnlyList<Phase2IconTemplateDefinition> templates)
        {
            if (category == "investment-strategy")
            {
                StrategyScans++;
            }

            return slots.Select((slot, index) =>
            {
                string? id = category switch
                {
                    "investment-environment" when index == 0 => environmentId,
                    "investment-strategy" when index < Math.Min(
                        StrategyScans,
                        strategyIds.Count) => strategyIds[index],
                    _ => null
                };
                return new Phase2IconRecognition(
                    index,
                    slot.ToPixels(frame.Width, frame.Height),
                    id,
                    id is null ? 0 : 0.95,
                    id is not null,
                    id is null ? [] : [id],
                    []);
            }).ToArray();
        }
    }
}
