using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class PreparationBoardControllerInitialBenchTests
{
    [Fact]
    public async Task ProductionControllerAcceptsThreeToFiveLeadingCardsAndPreservesPhysicalContiguity()
    {
        foreach (var recognizedCount in new[] { 3, 4, 5 })
        {
            var layout = Enumerable.Range(0, 9)
                .Select(index => index < recognizedCount
                    ? Slot(index, $"character_{index}")
                    : Empty(index))
                .ToArray();
            var run = await RunAsync(layout);

            Assert.Equal(
                PreparationBoardStatus.NoEligibleCharacter,
                run.Result.Status);
            Assert.Equal(recognizedCount, run.Result.Bench.Count);
            Assert.Equal(2, run.RecognizerCalls);
            Assert.DoesNotContain(
                run.Events,
                item => item.Code == "PreparationRecognitionDegraded");
        }

        var specialRun = await RunAsync(
        [
            Special(0),
            Special(1),
            Slot(2, "character_2"),
            Slot(3, "character_3"),
            Slot(4, "character_4"),
            Empty(5),
            Empty(6),
            Empty(7),
            Empty(8)
        ]);
        Assert.Equal(3, specialRun.Result.Bench.Count);
        Assert.Equal([2, 3, 4], specialRun.Result.Bench
            .Select(item => item.BenchSlot));
        Assert.Equal(2, specialRun.RecognizerCalls);

        var gapRun = await RunAsync(
        [
            Slot(0, "character_0"),
            Slot(1, "character_1"),
            Empty(2),
            Slot(3, "character_3"),
            Empty(4),
            Empty(5),
            Empty(6),
            Empty(7),
            Empty(8)
        ]);
        Assert.Empty(gapRun.Result.Bench);
        Assert.Equal(10, gapRun.RecognizerCalls);
        Assert.Contains(
            gapRun.Events,
            item => item.Code == "PreparationRecognitionDegraded");
    }

    private static async Task<RunResult> RunAsync(
        IReadOnlyList<CharacterCardSlotRecognition> layout)
    {
        var characters = Enumerable.Range(0, 9)
            .Select(index => new CurrencyWarsCharacterData(
                $"character_{index}",
                $"角色{index}",
                "前台",
                [1],
                false))
            .ToArray();
        var events = new RecordingEventSink();
        var recognizer = new StaticCharacterRecognizer(layout);
        var window = new GameWindowInfo(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(0, 0, 1920, 1080));
        var controller = new PreparationBoardController(
            new StaticCapture(),
            recognizer,
            [],
            new UnusedGoldRecognizer(),
            [],
            new GameDataCatalog([], [], [], [], characters),
            new InitialRewardFormationPlanner(),
            new PreparationBenchSalePlanner(),
            new UnusedInputController(),
            new ImmediateForegroundGuard(window),
            new PreparationPageClassifier(),
            new UnusedOcr(),
            events);

        var result = await controller.PrepareAsync(
            window.Handle,
            new PreparationBoardOptions
            {
                EligibleCharacterNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
            },
            CancellationToken.None);
        return new RunResult(result, recognizer.Calls, events.Events);
    }

    private static CharacterCardSlotRecognition Slot(
        int index,
        string characterId) =>
        new(
            index,
            new PixelRect(index * 120, 840, 110, 130),
            CharacterCardSlotState.Recognized,
            characterId,
            characterId,
            0.99,
            0,
            50);

    private static CharacterCardSlotRecognition Empty(int index) =>
        new(
            index,
            new PixelRect(index * 120, 840, 110, 130),
            CharacterCardSlotState.Empty,
            null,
            null,
            0,
            0,
            0);

    private static CharacterCardSlotRecognition Special(int index) =>
        new(
            index,
            new PixelRect(index * 120, 840, 110, 130),
            CharacterCardSlotState.SpecialOccupied,
            null,
            "特权武装箱",
            0.99,
            0,
            50);

    private sealed record RunResult(
        PreparationBoardResult Result,
        int RecognizerCalls,
        IReadOnlyList<TaskEvent> Events);

    private sealed class StaticCapture : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CaptureFrame(
                1,
                1,
                4,
                new byte[4],
                window.ClientArea,
                DateTimeOffset.Now));
    }

    private sealed class StaticCharacterRecognizer(
        IReadOnlyList<CharacterCardSlotRecognition> layout) :
        ICharacterCardRecognizer
    {
        public int Calls { get; private set; }

        public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            IReadOnlyList<PixelRect> referenceSlots)
        {
            Calls++;
            return layout;
        }
    }

    private sealed class PreparationPageClassifier : IGamePageClassifier
    {
        public PageClassificationResult? Classify(CaptureFrame frame) =>
            new("preparation_1_1", "1-1备战", 0.99, []);
    }

    private sealed class ImmediateForegroundGuard(GameWindowInfo window) :
        IGameForegroundGuard
    {
        public TimeSpan TotalPausedDuration => TimeSpan.Zero;

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            nint windowHandle,
            CancellationToken cancellationToken) => Task.FromResult(window);

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            GameWindowInfo current,
            CancellationToken cancellationToken) => Task.FromResult(window);
    }

    private sealed class RecordingEventSink : ITaskEventSink
    {
        public List<TaskEvent> Events { get; } = [];

        public void Publish(TaskEvent taskEvent) => Events.Add(taskEvent);
    }

    private sealed class UnusedGoldRecognizer : IGoldDigitRecognizer
    {
        public GoldDigitRecognition Recognize(
            CaptureFrame frame,
            IReadOnlyList<GoldDigitTemplateDefinition> templates,
            PixelRect referenceRegion) => throw new NotSupportedException();
    }

    private sealed class UnusedOcr : IOfflineOcr
    {
        public bool IsAvailable => false;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedInputController : IInputController
    {
        public Task<ActionResult> ClickAsync(
            ClickTarget target,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ActionResult> DragAsync(
            ClickTarget source,
            PixelPoint targetClientPoint,
            TimeSpan duration,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ActionResult> PressKeyAsync(
            GameWindowInfo window,
            InputKey key,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ActionResult> ClickWithModifierAsync(
            ClickTarget target,
            InputKey modifier,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
