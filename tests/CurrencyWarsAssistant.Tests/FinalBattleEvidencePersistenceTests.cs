using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class FinalBattleEvidencePersistenceTests
{
    [Fact]
    public async Task FinalNodeBattleReferencesOnlyBattleScreenshotsThatExist()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        const string runId = "run-final-evidence-contract";
        try
        {
            var store = new LocalRunStore(root);
            var capture = new MarkerSequenceCapture();
            var windowService = new StaticWindowService();
            var service = new Phase2LiveCollectionService(
                windowService,
                capture,
                new MarkerSituationAnalyzer(),
                store,
                new MarkerFastPageClassifier());
            var finalized = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.Updated += (_, update) =>
            {
                if (string.Equals(
                        update.Analysis?.OperationalState?.FinalBattle.Value?.NodeId,
                        "1-1",
                        StringComparison.Ordinal))
                {
                    finalized.TrySetResult();
                }
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var runTask = service.RunAsync(
                windowService.Window.Handle,
                new AdvisorSelection(AdvisorMode.Auto, "test", "4.4"),
                new LiveCollectionStartOptions(runId),
                timeout.Token);

            await finalized.Task.WaitAsync(timeout.Token);
            timeout.Cancel();
            await runTask;

            var runDirectory = store.GetRunDirectory(runId);
            var finalFile = Path.Combine(
                runDirectory,
                "nodes",
                "node-1-1-final.json");
            Assert.True(File.Exists(finalFile), finalFile);

            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(finalFile));
            var prefix = $"run:{runId}/screenshots/";
            var sourceIds = new List<string>();
            CollectSourceIds(document.RootElement, sourceIds);
            var battleReferences = sourceIds
                .Where(source => source.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.NotEmpty(battleReferences);

            var referencedNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var sourceId in battleReferences)
            {
                var fileName = sourceId[prefix.Length..];
                Assert.Equal(Path.GetFileName(fileName), fileName);
                Assert.True(referencedNames.Add(fileName));
                var path = Path.Combine(runDirectory, "screenshots", fileName);
                Assert.True(File.Exists(path), sourceId);
                var persisted = CaptureFrameLoader.LoadFile(path);
                Assert.Equal(MarkerSequenceCapture.BattleMarker, persisted.BgraPixels[0]);
            }

            var persistedBattleNames = Directory
                .EnumerateFiles(Path.Combine(runDirectory, "screenshots"), "*.png")
                .Where(path =>
                    CaptureFrameLoader.LoadFile(path).BgraPixels[0] ==
                    MarkerSequenceCapture.BattleMarker)
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(
                referencedNames.OrderBy(item => item, StringComparer.OrdinalIgnoreCase),
                persistedBattleNames.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(
                capture.BattleScreenshotNames,
                fileName => !persistedBattleNames.Contains(fileName));
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
    public async Task EvidencePngFailurePreventsFinalAnalysisAndNodeJson()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        const string runId = "run-final-evidence-write-failure";
        try
        {
            var store = new LocalRunStore(root);
            var screenshotDirectory = Path.Combine(
                store.GetRunDirectory(runId),
                "screenshots");
            Directory.CreateDirectory(screenshotDirectory);
            var startedAt = DateTimeOffset.Parse(
                "2026-08-09T13:31:08.000+08:00");
            for (var index = 3; index < 9; index++)
            {
                Directory.CreateDirectory(Path.Combine(
                    screenshotDirectory,
                    $"{startedAt.AddMilliseconds(index * 400L):yyyyMMdd-HHmmssfff}.png"));
            }
            var capture = new MarkerSequenceCapture();
            var windowService = new StaticWindowService();
            var service = new Phase2LiveCollectionService(
                windowService,
                capture,
                new MarkerSituationAnalyzer(),
                store,
                new MarkerFastPageClassifier());
            var failure = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.Updated += (_, update) =>
            {
                if (update.IsError)
                {
                    failure.TrySetResult();
                }
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var runTask = service.RunAsync(
                windowService.Window.Handle,
                new AdvisorSelection(AdvisorMode.Auto, "test", "4.4"),
                new LiveCollectionStartOptions(runId),
                timeout.Token);

            await failure.Task.WaitAsync(timeout.Token);
            timeout.Cancel();
            await runTask;

            var runDirectory = store.GetRunDirectory(runId);
            Assert.False(File.Exists(Path.Combine(
                runDirectory,
                "nodes",
                "node-1-1-final.json")));
            foreach (var path in Directory.EnumerateFiles(
                         runDirectory,
                         "analysis-*.json"))
            {
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(path));
                if (document.RootElement.TryGetProperty(
                        "operationalState",
                        out var operational) &&
                    operational.TryGetProperty("finalBattle", out var finalBattle))
                {
                    Assert.NotEqual(
                        "known",
                        finalBattle.GetProperty("status").GetString());
                }
            }
            Assert.Empty(Directory.EnumerateFiles(
                screenshotDirectory,
                "*.tmp"));
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
    public async Task EvidencePngFailureIsRetriedWithoutConsumingTrackerFinal()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        const string runId = "run-final-evidence-retry";
        try
        {
            var store = new LocalRunStore(root);
            var screenshotDirectory = Path.Combine(
                store.GetRunDirectory(runId),
                "screenshots");
            Directory.CreateDirectory(screenshotDirectory);
            var startedAt = DateTimeOffset.Parse(
                "2026-08-09T13:31:08.000+08:00");
            for (var index = 3; index < 9; index++)
            {
                Directory.CreateDirectory(Path.Combine(
                    screenshotDirectory,
                    $"{startedAt.AddMilliseconds(index * 400L):yyyyMMdd-HHmmssfff}.png"));
            }
            var nodeFinalPath = Path.Combine(
                store.GetRunDirectory(runId),
                "nodes",
                "node-1-1-final.json");
            Directory.CreateDirectory(nodeFinalPath);

            var capture = new MarkerSequenceCapture();
            var windowService = new StaticWindowService();
            var service = new Phase2LiveCollectionService(
                windowService,
                capture,
                new MarkerSituationAnalyzer(),
                store,
                new MarkerFastPageClassifier());
            var persistenceFailures = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var finalized = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var failureCount = 0;
            service.Updated += (_, update) =>
            {
                if (update.IsError)
                {
                    var currentFailure = Interlocked.Increment(ref failureCount);
                    if (currentFailure == 1)
                    {
                        foreach (var blockedPath in Directory.EnumerateDirectories(
                                     screenshotDirectory,
                                     "*.png"))
                        {
                            Directory.Delete(blockedPath);
                        }
                    }
                    else if (currentFailure == 2)
                    {
                        Directory.Delete(nodeFinalPath);
                        persistenceFailures.TrySetResult();
                    }
                }

                if (string.Equals(
                        update.Analysis?.OperationalState?.FinalBattle.Value?.NodeId,
                        "1-1",
                        StringComparison.Ordinal))
                {
                    finalized.TrySetResult();
                }
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var runTask = service.RunAsync(
                windowService.Window.Handle,
                new AdvisorSelection(AdvisorMode.Auto, "test", "4.4"),
                new LiveCollectionStartOptions(runId),
                timeout.Token);

            await persistenceFailures.Task.WaitAsync(timeout.Token);
            await finalized.Task.WaitAsync(timeout.Token);
            timeout.Cancel();
            await runTask;

            Assert.Equal(2, failureCount);
            Assert.True(File.Exists(nodeFinalPath));
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
    public async Task NewRunBoundaryPersistsSimultaneousFinalOnlyUnderPreviousRun()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        const string previousRunId = "run-boundary-evidence-contract";
        try
        {
            var store = new LocalRunStore(root);
            var capture = new NewRunSequenceCapture();
            var windowService = new StaticWindowService();
            var service = new Phase2LiveCollectionService(
                windowService,
                capture,
                new NewRunSequenceAnalyzer(),
                store,
                new NewRunFastPageClassifier());
            var resetObserved = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.Updated += (_, update) =>
            {
                if (!string.Equals(
                        update.RunId,
                        previousRunId,
                        StringComparison.Ordinal) &&
                    update.Analysis?.Snapshot.Stage.Value == "1-1")
                {
                    resetObserved.TrySetResult(update.RunId);
                }
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var runTask = service.RunAsync(
                windowService.Window.Handle,
                new AdvisorSelection(AdvisorMode.Auto, "test", "4.4"),
                new LiveCollectionStartOptions(previousRunId),
                timeout.Token);

            var resetRunId = await resetObserved.Task.WaitAsync(timeout.Token);
            timeout.Cancel();
            await runTask;

            var previousDirectory = store.GetRunDirectory(previousRunId);
            var previousFinal = Path.Combine(
                previousDirectory,
                "nodes",
                "node-2-2-final.json");
            Assert.True(File.Exists(previousFinal), previousFinal);
            using (var document = JsonDocument.Parse(
                       await File.ReadAllTextAsync(previousFinal)))
            {
                var sourceIds = new List<string>();
                CollectSourceIds(document.RootElement, sourceIds);
                var referenced = sourceIds
                    .Where(source => source.StartsWith(
                        $"run:{previousRunId}/screenshots/",
                        StringComparison.Ordinal))
                    .ToArray();
                Assert.NotEmpty(referenced);
                Assert.All(referenced, source =>
                {
                    var fileName = source[(source.LastIndexOf('/') + 1)..];
                    var path = Path.Combine(
                        previousDirectory,
                        "screenshots",
                        fileName);
                    Assert.True(File.Exists(path), source);
                    Assert.Equal(
                        NewRunSequenceCapture.SecondBattleMarker,
                        CaptureFrameLoader.LoadFile(path).BgraPixels[0]);
                });
            }

            var resetDirectory = store.GetRunDirectory(resetRunId);
            Assert.False(File.Exists(Path.Combine(
                resetDirectory,
                "nodes",
                "node-2-2-final.json")));
            var resetAnalyses = Directory
                .EnumerateFiles(resetDirectory, "analysis-*.json")
                .ToArray();
            Assert.NotEmpty(resetAnalyses);
            foreach (var path in resetAnalyses)
            {
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(path));
                if (document.RootElement.TryGetProperty(
                        "operationalState",
                        out var operational) &&
                    operational.TryGetProperty("finalBattle", out var finalBattle))
                {
                    Assert.Equal(
                        "unknown",
                        finalBattle.GetProperty("status").GetString());
                    Assert.False(finalBattle.TryGetProperty("value", out var value) &&
                                 value.ValueKind is not JsonValueKind.Null);
                }
            }
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalFinalizationPersistsEvidenceBeforeArchiveAndHonorsCleanup(
        bool deleteScreenshotsOnCompletion)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        var runId = deleteScreenshotsOnCompletion
            ? "run-terminal-cleanup"
            : "run-terminal-preserve";
        try
        {
            var store = new LocalRunStore(root);
            var capture = new TerminalSequenceCapture();
            var windowService = new StaticWindowService();
            var service = new Phase2LiveCollectionService(
                windowService,
                capture,
                new NewRunSequenceAnalyzer(),
                store,
                new NewRunFastPageClassifier());
            var archived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.Updated += (_, update) =>
            {
                if (update.Analysis?.Snapshot.PageId.Value == "challenge_failed" &&
                    update.Analysis.OperationalState?.FinalBattle.Value is not null)
                {
                    archived.TrySetResult();
                }
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var runTask = service.RunAsync(
                windowService.Window.Handle,
                new AdvisorSelection(AdvisorMode.Auto, "test", "4.4"),
                new LiveCollectionStartOptions(
                    runId,
                    DeleteScreenshotsOnCompletion:
                        deleteScreenshotsOnCompletion),
                timeout.Token);

            await archived.Task.WaitAsync(timeout.Token);
            timeout.Cancel();
            await runTask;

            var runDirectory = store.GetRunDirectory(runId);
            Assert.True(File.Exists(Path.Combine(
                runDirectory,
                "completed-run.v1.json")));
            var finalPath = Path.Combine(
                runDirectory,
                "nodes",
                "node-2-1-final.json");
            Assert.True(File.Exists(finalPath), finalPath);
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(finalPath));
            var sources = new List<string>();
            CollectSourceIds(document.RootElement, sources);
            var screenshotSources = sources
                .Where(source => source.StartsWith(
                    $"run:{runId}/screenshots/",
                    StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.NotEmpty(screenshotSources);

            if (deleteScreenshotsOnCompletion)
            {
                Assert.False(Directory.Exists(Path.Combine(
                    runDirectory,
                    "screenshots")));
            }
            else
            {
                Assert.All(screenshotSources, source =>
                {
                    var fileName = source[(source.LastIndexOf('/') + 1)..];
                    var path = Path.Combine(
                        runDirectory,
                        "screenshots",
                        fileName);
                    Assert.True(File.Exists(path), source);
                    Assert.Equal(
                        TerminalSequenceCapture.BattleMarker,
                        CaptureFrameLoader.LoadFile(path).BgraPixels[0]);
                });
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CollectSourceIds(
        JsonElement element,
        ICollection<string> destination)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("sourceId") &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is { } sourceId)
                    {
                        destination.Add(sourceId);
                    }

                    CollectSourceIds(property.Value, destination);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectSourceIds(item, destination);
                }

                break;
        }
    }

    private sealed class MarkerSequenceCapture : IGameCapture
    {
        public const byte BattleMarker = 20;
        private static readonly DateTimeOffset StartedAt =
            DateTimeOffset.Parse("2026-08-09T13:31:08.000+08:00");
        private int index = -1;

        public List<string> BattleScreenshotNames { get; } = [];

        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref index);
            var marker = current switch
            {
                < 3 => (byte)10,
                < 9 => BattleMarker,
                _ => (byte)30
            };
            var capturedAt = StartedAt.AddMilliseconds(current * 400L);
            if (marker == BattleMarker)
            {
                BattleScreenshotNames.Add($"{capturedAt:yyyyMMdd-HHmmssfff}.png");
            }

            return ValueTask.FromResult(CreateFrame(marker, capturedAt));
        }

        private static CaptureFrame CreateFrame(
            byte marker,
            DateTimeOffset capturedAt)
        {
            const int width = 160;
            const int height = 90;
            var fill = marker switch
            {
                10 => (byte)60,
                BattleMarker => (byte)140,
                _ => (byte)220
            };
            var pixels = new byte[width * height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = fill;
                pixels[offset + 1] = fill;
                pixels[offset + 2] = fill;
                pixels[offset + 3] = 255;
            }

            pixels[0] = marker;
            return new CaptureFrame(
                width,
                height,
                width * 4,
                pixels,
                new PixelRect(0, 0, width, height),
                capturedAt);
        }
    }

    private sealed class MarkerFastPageClassifier : IPhase2FastPageClassifier
    {
        public Phase2FastPageObservation Classify(CaptureFrame frame) =>
            frame.BgraPixels[0] == MarkerSequenceCapture.BattleMarker
                ? new Phase2FastPageObservation(
                    true,
                    Phase2PageFamily.Battle,
                    "battle_generic")
                : new Phase2FastPageObservation(
                    true,
                    Phase2PageFamily.Preparation,
                    "preparation_generic");
    }

    private sealed class NewRunSequenceCapture : IGameCapture
    {
        public const byte SecondBattleMarker = 41;
        private static readonly DateTimeOffset StartedAt =
            DateTimeOffset.Parse("2026-08-09T14:00:00.000+08:00");
        private int index = -1;

        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref index);
            var marker = current switch
            {
                < 4 => (byte)11,
                < 11 => (byte)21,
                < 16 => (byte)31,
                < 23 => SecondBattleMarker,
                _ => (byte)51
            };
            return ValueTask.FromResult(CreateFrame(
                marker,
                StartedAt.AddMilliseconds(current * 450L)));
        }

        private static CaptureFrame CreateFrame(
            byte marker,
            DateTimeOffset capturedAt)
        {
            const int width = 160;
            const int height = 90;
            var pixels = new byte[width * height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = marker;
                pixels[offset + 1] = marker;
                pixels[offset + 2] = marker;
                pixels[offset + 3] = 255;
            }

            return new CaptureFrame(
                width,
                height,
                width * 4,
                pixels,
                new PixelRect(0, 0, width, height),
                capturedAt);
        }
    }

    private sealed class TerminalSequenceCapture : IGameCapture
    {
        public const byte BattleMarker = 21;
        private static readonly DateTimeOffset StartedAt =
            DateTimeOffset.Parse("2026-08-09T14:30:00.000+08:00");
        private int index = -1;

        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Interlocked.Increment(ref index);
            var marker = current switch
            {
                < 4 => (byte)11,
                < 11 => BattleMarker,
                _ => (byte)61
            };
            const int width = 160;
            const int height = 90;
            var pixels = new byte[width * height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = marker;
                pixels[offset + 1] = marker;
                pixels[offset + 2] = marker;
                pixels[offset + 3] = 255;
            }

            return ValueTask.FromResult(new CaptureFrame(
                width,
                height,
                width * 4,
                pixels,
                new PixelRect(0, 0, width, height),
                StartedAt.AddMilliseconds(current * 450L)));
        }
    }

    private sealed class NewRunFastPageClassifier : IPhase2FastPageClassifier
    {
        public Phase2FastPageObservation Classify(CaptureFrame frame) =>
            frame.BgraPixels[0] == 61
                ? new Phase2FastPageObservation(
                    true,
                    Phase2PageFamily.BattleSettlement,
                    "challenge_failed")
                : frame.BgraPixels[0] is 21 or NewRunSequenceCapture.SecondBattleMarker
                ? new Phase2FastPageObservation(
                    true,
                    Phase2PageFamily.Battle,
                    "battle_generic")
                : new Phase2FastPageObservation(
                    true,
                    Phase2PageFamily.Preparation,
                    "preparation_generic");
    }

    private sealed class NewRunSequenceAnalyzer : ISituationScreenshotAnalyzer
    {
        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId,
            Phase2IncrementalSelection? incrementalSelection,
            int? recognitionGeneration) =>
            AnalyzeAsync(
                frame,
                evidenceSourceId,
                selection,
                cancellationToken,
                runId);

        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = frame.BgraPixels[0];
            var battle = marker is 21 or NewRunSequenceCapture.SecondBattleMarker;
            var terminal = marker == 61;
            var nodeId = marker switch
            {
                11 or 21 => "2-1",
                31 or NewRunSequenceCapture.SecondBattleMarker => "2-2",
                _ => "1-1"
            };
            var pageId = terminal
                ? "challenge_failed"
                : battle
                    ? "battle_generic"
                    : "preparation_generic";
            var evidence = new EvidenceReference(
                evidenceSourceId,
                "screenshot:full-frame",
                CapturedAt: frame.CapturedAt,
                Confidence: 0.95);
            var state = new Phase2OperationalState
            {
                PageFamily = terminal
                    ? Phase2PageFamily.BattleSettlement
                    : battle
                        ? Phase2PageFamily.Battle
                        : Phase2PageFamily.Preparation,
                PageId = pageId,
                NodeId = Observation<string>.Known(
                    nodeId,
                    0.95,
                    [evidence],
                    frame.CapturedAt),
                Health = Observation<int>.Known(
                    80,
                    0.95,
                    [evidence],
                    frame.CapturedAt)
            };
            if (battle)
            {
                var damage = new CharacterDamageState(
                    1,
                    "currency_wars_character_01",
                    marker == NewRunSequenceCapture.SecondBattleMarker
                        ? 222_000
                        : 111_000,
                    "damage",
                    0.95,
                    0.95,
                    new RelativeRegion(0.6, 0.5, 0.04, 0.06),
                    new RelativeRegion(0.65, 0.5, 0.10, 0.06),
                    evidence);
                state = state with
                {
                    BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
                        [damage],
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    BattleScreenDamageCandidate = Observation<long>.Known(
                        damage.Damage,
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    RemainingActionValue = Observation<RemainingActionValueState>.Known(
                        RemainingActionValueState.Create(0, 50),
                        0.95,
                        [evidence],
                        frame.CapturedAt)
                };
            }

            return Task.FromResult(new ScreenshotAnalysisResult
            {
                AnalysisId = $"new-run-{frame.CapturedAt:HHmmssfff}",
                Snapshot = new RunSnapshot
                {
                    RunId = runId ?? "new-run-sequence",
                    AsOf = frame.CapturedAt,
                    PageId = Observation<string>.Known(
                        pageId,
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    Stage = Observation<string>.Known(
                        nodeId,
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    Health = state.Health
                },
                OperationalState = state
            });
        }
    }

    private sealed class MarkerSituationAnalyzer : ISituationScreenshotAnalyzer
    {
        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId,
            Phase2IncrementalSelection? incrementalSelection,
            int? recognitionGeneration) =>
            AnalyzeAsync(
                frame,
                evidenceSourceId,
                selection,
                cancellationToken,
                runId);

        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var battle = frame.BgraPixels[0] == MarkerSequenceCapture.BattleMarker;
            var successor = frame.BgraPixels[0] == 30;
            var pageId = battle ? "battle_generic" : "preparation_generic";
            var nodeId = successor ? "1-2" : "1-1";
            var evidence = new EvidenceReference(
                evidenceSourceId,
                "screenshot:full-frame",
                $"{frame.Width}x{frame.Height}",
                frame.CapturedAt,
                0.95);
            var state = new Phase2OperationalState
            {
                PageFamily = battle
                    ? Phase2PageFamily.Battle
                    : Phase2PageFamily.Preparation,
                PageId = pageId,
                NodeId = Observation<string>.Known(
                    nodeId,
                    0.95,
                    [evidence],
                    frame.CapturedAt),
                Health = Observation<int>.Known(
                    successor ? 82 : 80,
                    0.95,
                    [evidence],
                    frame.CapturedAt)
            };
            if (battle)
            {
                var damage = new CharacterDamageState(
                    1,
                    "currency_wars_character_01",
                    40_545,
                    "4.1万",
                    0.95,
                    0.95,
                    new RelativeRegion(0.6, 0.5, 0.04, 0.06),
                    new RelativeRegion(0.65, 0.5, 0.10, 0.06),
                    evidence);
                state = state with
                {
                    BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
                        [damage],
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    BattleScreenDamageCandidate = Observation<long>.Known(
                        damage.Damage,
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    RemainingActionValue = Observation<RemainingActionValueState>.Known(
                        RemainingActionValueState.Create(1, 88),
                        0.95,
                        [evidence],
                        frame.CapturedAt)
                };
            }

            return Task.FromResult(new ScreenshotAnalysisResult
            {
                AnalysisId = $"marker-{frame.CapturedAt:HHmmssfff}",
                Snapshot = new RunSnapshot
                {
                    RunId = runId ?? "marker-run",
                    AsOf = frame.CapturedAt,
                    PageId = Observation<string>.Known(
                        pageId,
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    Stage = Observation<string>.Known(
                        nodeId,
                        0.95,
                        [evidence],
                        frame.CapturedAt),
                    Health = state.Health
                },
                OperationalState = state
            });
        }
    }

    private sealed class StaticWindowService : IGameWindowService
    {
        public GameWindowInfo Window { get; } = new(
            1,
            1,
            "StarRail",
            "StarRail",
            new PixelRect(0, 0, 160, 90));

        public IReadOnlyList<GameWindowInfo> FindCandidates() => [Window];

        public GameWindowInfo? Refresh(nint handle) =>
            handle == Window.Handle ? Window : null;

        public bool IsForeground(GameWindowInfo window) => true;

        public bool BringToForeground(GameWindowInfo window) => true;
    }
}
