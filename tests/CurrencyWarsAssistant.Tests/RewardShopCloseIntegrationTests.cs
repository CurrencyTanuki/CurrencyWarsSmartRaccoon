using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

public sealed class RewardShopCloseIntegrationTests
{
    [Fact]
    public async Task DisabledRefreshShopIsRecognizedClickedAndVerifiedClosed()
    {
        var shopFrame = LoadFrame(
            "reward_shop_after_two_purchases_2048x1152.png");
        var preparationFrame = LoadFrame(
            "preparation_1_1_after_shop_batch_2048x1152.png");
        var capture = new TransitionCapture(shopFrame, preparationFrame);
        var input = new ShopCloseInput(capture);
        var events = new RecordingEventSink();
        var window = new GameWindowInfo(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(0, 0, 2048, 1152));
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        var classifier = new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
        var data = GameDataCatalogLoader.Load(
            Path.Combine(RepositoryRoot, "data", "4.4"));
        var ocr = new EmptyOcr();
        var controller = new RewardStageAutomationController(
            capture,
            classifier,
            new RewardShopReader(ocr, data),
            new RewardShopPurchasePlanner(),
            new InvestmentStrategyPageReader(ocr, data),
            new RewardVisualDetector(),
            input,
            new ImmediateForegroundGuard(window),
            new UnusedPreparationCompletionController(),
            new UnusedSettlementRecovery(),
            events);

        var closed = await controller.CloseShopAsync(
            window.Handle,
            "preparation_generic",
            CancellationToken.None);

        Assert.True(closed);
        Assert.Equal(["收起商店"], input.Clicks);
        Assert.Empty(input.Keys);
        Assert.DoesNotContain(
            events.Events,
            item => item.Code is "CloseRewardShopTransitionTimedOut" or
                "CloseRewardShopPageMismatch");
    }

    [Fact]
    public async Task SlowFirstPostClickClassificationDoesNotToggleClosedShopAgain()
    {
        var shopFrame = LoadFrame(
            "reward_shop_after_two_purchases_2048x1152.png");
        var preparationFrame = LoadFrame(
            "preparation_1_1_after_shop_batch_2048x1152.png");
        var capture = new TransitionCapture(shopFrame, preparationFrame);
        var input = new ShopCloseInput(capture);
        var events = new RecordingEventSink();
        var window = new GameWindowInfo(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(0, 0, 2048, 1152));
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        var classifier = new DelayedFirstPageClassifier(
            new TemplateGamePageClassifier(
                new OpenCvTemplateMatcher(),
                config.Pages),
            "preparation_generic",
            TimeSpan.FromMilliseconds(5200));
        var data = GameDataCatalogLoader.Load(
            Path.Combine(RepositoryRoot, "data", "4.4"));
        var ocr = new EmptyOcr();
        var controller = new RewardStageAutomationController(
            capture,
            classifier,
            new RewardShopReader(ocr, data),
            new RewardShopPurchasePlanner(),
            new InvestmentStrategyPageReader(ocr, data),
            new RewardVisualDetector(),
            input,
            new ImmediateForegroundGuard(window),
            new UnusedPreparationCompletionController(),
            new UnusedSettlementRecovery(),
            events);

        var closed = await controller.CloseShopAsync(
            window.Handle,
            "preparation_generic",
            CancellationToken.None);

        Assert.True(closed);
        Assert.Equal(["收起商店"], input.Clicks);
        Assert.Empty(input.Keys);
    }

    [Fact]
    public async Task StableShopAfterFailedVerificationAllowsOneSafeRetry()
    {
        var shopFrame = LoadFrame(
            "reward_shop_after_two_purchases_2048x1152.png");
        var preparationFrame = LoadFrame(
            "preparation_1_1_after_shop_batch_2048x1152.png");
        var capture = new TransitionCapture(shopFrame, preparationFrame);
        var input = new ShopCloseInput(capture, closeOnClick: 2);
        var events = new RecordingEventSink();
        var window = new GameWindowInfo(
            123,
            456,
            "StarRail",
            "Honkai: Star Rail",
            new PixelRect(0, 0, 2048, 1152));
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        var classifier = new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
        var data = GameDataCatalogLoader.Load(
            Path.Combine(RepositoryRoot, "data", "4.4"));
        var ocr = new EmptyOcr();
        var controller = new RewardStageAutomationController(
            capture,
            classifier,
            new RewardShopReader(ocr, data),
            new RewardShopPurchasePlanner(),
            new InvestmentStrategyPageReader(ocr, data),
            new RewardVisualDetector(),
            input,
            new ImmediateForegroundGuard(window),
            new UnusedPreparationCompletionController(),
            new UnusedSettlementRecovery(),
            events);

        var closed = await controller.CloseShopAsync(
            window.Handle,
            "preparation_generic",
            CancellationToken.None);

        Assert.True(closed);
        Assert.Equal(["收起商店", "收起商店"], input.Clicks);
        Assert.Contains(
            events.Events,
            item => item.Code == "CloseRewardShopRetry");
        Assert.DoesNotContain(
            events.Events,
            item => item.Code == "CloseRewardShopRetryStopped");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("home")]
    public async Task UnknownOrOtherPageAfterVerificationFailureStopsWithoutRetry(
        string? postClickPageId)
    {
        var frame = LoadFrame(
            "reward_shop_after_two_purchases_2048x1152.png");
        var capture = new TransitionCapture(frame, frame);
        var input = new ShopCloseInput(capture);
        var events = new RecordingEventSink();
        var window = new GameWindowInfo(
            123,
            456,
            "StarRail",
            "Honkai: Star Rail",
            new PixelRect(0, 0, 2048, 1152));
        var classifier = new PostClickPageClassifier(postClickPageId);
        var data = GameDataCatalogLoader.Load(
            Path.Combine(RepositoryRoot, "data", "4.4"));
        var ocr = new EmptyOcr();
        var controller = new RewardStageAutomationController(
            capture,
            classifier,
            new RewardShopReader(ocr, data),
            new RewardShopPurchasePlanner(),
            new InvestmentStrategyPageReader(ocr, data),
            new RewardVisualDetector(),
            input,
            new ImmediateForegroundGuard(window),
            new UnusedPreparationCompletionController(),
            new UnusedSettlementRecovery(),
            events);

        var closed = await controller.CloseShopAsync(
            window.Handle,
            "preparation_generic",
            CancellationToken.None);

        Assert.False(closed);
        Assert.Equal(["收起商店"], input.Clicks);
        Assert.Contains(
            events.Events,
            item => item.Code == "CloseRewardShopRetryStopped");
        Assert.DoesNotContain(
            events.Events,
            item => item.Code == "CloseRewardShopRetry");
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

    private static CaptureFrame LoadFrame(string file)
    {
        using var bgr = Cv2.ImRead(
            Path.Combine(FixtureDirectory, file),
            ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Cols,
            bgra.Rows,
            checked(bgra.Cols * 4),
            pixels,
            new PixelRect(0, 0, bgra.Cols, bgra.Rows),
            DateTimeOffset.UtcNow);
    }

    private sealed class TransitionCapture(
        CaptureFrame shop,
        CaptureFrame preparation) : IGameCapture
    {
        public bool ShopClosed { get; set; }

        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ShopClosed ? preparation : shop);
        }
    }

    private sealed class DelayedFirstPageClassifier(
        IAutomationPageClassifier inner,
        string pageId,
        TimeSpan delay) : IAutomationPageClassifier
    {
        private int _delayApplied;

        public PageClassificationResult? Classify(CaptureFrame frame)
        {
            var result = inner.Classify(frame);
            if (result is not null &&
                string.Equals(
                    result.PageId,
                    pageId,
                    StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _delayApplied, 1) == 0)
            {
                // Models a synchronous OpenCV classification that starts before
                // the deadline and finishes after it under CPU contention.
                Thread.Sleep(delay);
            }

            return result;
        }
    }

    private sealed class PostClickPageClassifier(string? postClickPageId)
        : IAutomationPageClassifier
    {
        private int _classifications;

        public PageClassificationResult? Classify(CaptureFrame frame)
        {
            var classification = Interlocked.Increment(ref _classifications);
            if (classification <= 2)
            {
                return CreatePage("reward_shop");
            }

            if (classification == 3)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(5200));
            }

            return postClickPageId is null
                ? null
                : CreatePage(postClickPageId);
        }

        private static PageClassificationResult CreatePage(string pageId) =>
            new(pageId, pageId, 1, []);
    }

    private sealed class ShopCloseInput(
        TransitionCapture capture,
        int closeOnClick = 1)
        : IInputController
    {
        private int _closeClicks;

        public List<string> Clicks { get; } = [];
        public List<InputKey> Keys { get; } = [];

        public Task<ActionResult> ClickAsync(
            ClickTarget target,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Clicks.Add(target.Id);
            if (target.Id == "收起商店")
            {
                _closeClicks++;
                capture.ShopClosed = _closeClicks >= closeOnClick;
            }

            return Task.FromResult(ActionResult.Success(target.DisplayName));
        }

        public Task<ActionResult> PressKeyAsync(
            GameWindowInfo window,
            InputKey key,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            Keys.Add(key);
            return Task.FromResult(ActionResult.Success(key.ToString()));
        }

        public Task<ActionResult> DragAsync(
            ClickTarget source,
            PixelPoint targetClientPoint,
            TimeSpan duration,
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

    private sealed class ImmediateForegroundGuard(GameWindowInfo window)
        : IGameForegroundGuard
    {
        public TimeSpan TotalPausedDuration => TimeSpan.Zero;

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            nint windowHandle,
            CancellationToken cancellationToken) => Task.FromResult(window);

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            GameWindowInfo current,
            CancellationToken cancellationToken) => Task.FromResult(window);
    }

    private sealed class EmptyOcr : IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrTextResult(string.Empty, []));
    }

    private sealed class UnusedPreparationCompletionController
        : IPreparationBoardCompletionController
    {
        public Task<IReadOnlyList<RecognizedBenchCharacter>?>
            ReadStableBenchCharactersAsync(
                nint windowHandle,
                string expectedPreparationPageId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PreparationMineCapacityResult> EnsureMineCapacityAsync(
            nint windowHandle,
            IReadOnlyList<PreparationPlacement> existingPlacements,
            PreparationBoardOptions options,
            string expectedPreparationPageId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PreparationBoardResult> CompleteAfterShopAsync(
            nint windowHandle,
            IReadOnlyList<PreparationPlacement> existingPlacements,
            PreparationBoardOptions options,
            string expectedPreparationPageId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedSettlementRecovery : IAbandonSettlementRecovery
    {
        public Task<RejectedOpeningRecoveryResult>
            CompleteFromAbandonSettlementPromptAsync(
                nint windowHandle,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingEventSink : ITaskEventSink
    {
        public List<TaskEvent> Events { get; } = [];

        public void Publish(TaskEvent taskEvent) => Events.Add(taskEvent);
    }
}
