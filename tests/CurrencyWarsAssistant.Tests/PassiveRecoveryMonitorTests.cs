using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace CurrencyWarsAssistant.Tests;

public sealed class PassiveRecoveryMonitorTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task UsesHomeContextEvidenceWhenOverlayHidesClassifierAnchor()
    {
        var frame = LoadFrame(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "currency_wars_home_overlay_2048x1152.png"));
        var monitor = new PassiveRecoveryMonitor(
            new RepeatingCapture(frame),
            new SequenceClassifier(null, null),
            new ImmediateForegroundGuard(),
            new SilentEventSink());

        var pageId = await monitor.WaitForSafeEntryPageAsync(
            1,
            CancellationToken.None);

        Assert.Equal("currency_wars_home", pageId);
    }

    [Fact]
    public async Task IgnoresUnknownAndMidRunPagesUntilSafeEntryIsStable()
    {
        var classifier = new SequenceClassifier(
            null,
            "preparation_1_1",
            "currency_wars_home",
            "currency_wars_home");
        var capture = new CountingCapture();
        var monitor = new PassiveRecoveryMonitor(
            capture,
            classifier,
            new ImmediateForegroundGuard(),
            new SilentEventSink());

        var pageId = await monitor.WaitForSafeEntryPageAsync(
            1,
            CancellationToken.None);

        Assert.Equal("currency_wars_home", pageId);
        Assert.Equal(4, capture.Calls);
    }

    private sealed class CountingCapture : IGameCapture
    {
        public int Calls { get; private set; }

        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(new CaptureFrame(
                1, 1, 4, new byte[4], new PixelRect(0, 0, 1, 1),
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class RepeatingCapture(CaptureFrame frame) : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(frame);
    }

    private sealed class SequenceClassifier(params string?[] pageIds)
        : IGamePageClassifier
    {
        private readonly Queue<string?> _pageIds = new(pageIds);

        public PageClassificationResult? Classify(CaptureFrame frame)
        {
            var pageId = _pageIds.Dequeue();
            return pageId is null
                ? null
                : new PageClassificationResult(pageId, pageId, 1, []);
        }
    }

    private sealed class ImmediateForegroundGuard : IGameForegroundGuard
    {
        private static readonly GameWindowInfo Window = new(
            1, 1, "game", "game", new PixelRect(0, 0, 1, 1));

        public TimeSpan TotalPausedDuration => TimeSpan.Zero;

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            nint windowHandle,
            CancellationToken cancellationToken) =>
            Task.FromResult(Window);

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            Task.FromResult(window);
    }

    private sealed class SilentEventSink : ITaskEventSink
    {
        public void Publish(TaskEvent taskEvent)
        {
        }
    }

    private static CaptureFrame LoadFrame(string path)
    {
        using var source = Cv2.ImRead(path, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        var stride = checked(bgra.Width * 4);
        var pixels = new byte[checked(stride * bgra.Height)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Width,
            bgra.Height,
            stride,
            pixels,
            new PixelRect(0, 0, bgra.Width, bgra.Height),
            DateTimeOffset.UtcNow);
    }
}
