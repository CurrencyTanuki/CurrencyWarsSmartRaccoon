using System.Text.Json;
using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2DatasetCaptureTests
{
    [Fact]
    public void CommandParsesBoundedCaptureOptions()
    {
        var command = Phase2DatasetCaptureCommand.Parse(
        [
            Phase2DatasetCaptureCommand.Switch,
            "--output",
            "dataset-output",
            "--duration-seconds",
            "30",
            "--fps",
            "5.5",
            "--encoder-workers",
            "4"
        ]);

        Assert.NotNull(command);
        Assert.Equal(TimeSpan.FromSeconds(30), command.Duration);
        Assert.Equal(5.5, command.FramesPerSecond);
        Assert.Equal(4, command.EncoderWorkers);
        Assert.True(Path.IsPathFullyQualified(command.OutputDirectory));
    }

    [Theory]
    [InlineData("3.9")]
    [InlineData("6.1")]
    public void CommandRejectsFrequencyOutsideFourToSixFps(string fps)
    {
        Assert.Throws<ArgumentException>(() =>
            Phase2DatasetCaptureCommand.Parse(
            [
                Phase2DatasetCaptureCommand.Switch,
                "--output",
                "dataset-output",
                "--fps",
                fps
            ]));
    }

    [Fact]
    public async Task DatasetCaptureSavesAContinuousFiveFpsStreamAndManifest()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var service = new Phase2DatasetCaptureService(
                new StaticWindowService(),
                new SyntheticCapture());
            var report = await service.CaptureAsync(
                new Phase2DatasetCaptureCommand(
                    root,
                    TimeSpan.FromSeconds(1),
                    5,
                    EncoderWorkers: 2),
                CancellationToken.None);

            Assert.InRange(report.SuccessfulFrames, 5, 6);
            Assert.Equal(0, report.FailedFrames);
            Assert.InRange(report.ActualFramesPerSecond, 4, 6);
            Assert.False(report.SendsInput);
            Assert.False(report.ReadsGameMemory);
            var files = Directory.GetFiles(
                Path.Combine(root, "frames"),
                "*.png");
            Assert.Equal(report.SuccessfulFrames, files.Length);
            var manifestLines = await File.ReadAllLinesAsync(
                Path.Combine(root, "frames.jsonl"));
            Assert.Equal(report.SuccessfulFrames, manifestLines.Length);
            Assert.All(manifestLines, line =>
                Assert.Equal(JsonValueKind.Object,
                    JsonDocument.Parse(line).RootElement.ValueKind));
            Assert.True(File.Exists(Path.Combine(root, "capture-report.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StaticWindowService : IGameWindowService
    {
        private static readonly GameWindowInfo Window = new(
            1,
            1,
            "StarRail",
            "Honkai: Star Rail",
            new PixelRect(0, 0, 320, 180));

        public IReadOnlyList<GameWindowInfo> FindCandidates() => [Window];

        public GameWindowInfo? Refresh(nint handle) =>
            handle == Window.Handle ? Window : null;

        public bool IsForeground(GameWindowInfo window) => true;

        public bool BringToForeground(GameWindowInfo window) => true;
    }

    private sealed class SyntheticCapture : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = new byte[window.ClientArea.Width *
                                  window.ClientArea.Height * 4];
            for (var offset = 3; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = 255;
            }

            return ValueTask.FromResult(new CaptureFrame(
                window.ClientArea.Width,
                window.ClientArea.Height,
                window.ClientArea.Width * 4,
                pixels,
                window.ClientArea,
                DateTimeOffset.UtcNow));
        }
    }
}
