using System.Diagnostics;
using System.Security.Cryptography;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class PpOcrOfflineOcrTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    private static readonly string ModelPath = Path.Combine(
        RepositoryRoot,
        "data",
        "ocr",
        "rapidocr",
        "PP-OCRv6_rec_small.onnx");

    private static readonly string FixtureDirectory = Path.Combine(
        RepositoryRoot,
        "tests",
        "CurrencyWarsAssistant.Tests",
        "Fixtures",
        "OcrEngine");

    [Theory]
    [InlineData("action-76-a.png", "76")]
    [InlineData("action-76-b.png", "76")]
    [InlineData("action-79-a.png", "79")]
    [InlineData("preparation-node-1-7.png", "1-7")]
    [InlineData("preparation-stage-label.png", "备战阶段")]
    [InlineData("settlement-damage-13249.8w.png", "13249.8万")]
    [InlineData("settlement-gold-9.png", "9")]
    public async Task RecognitionOnlyModelReadsRealGameUiCrops(
        string fileName,
        string expected)
    {
        using var ocr = new PpOcrOfflineOcr(ModelPath, maximumConcurrency: 2);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            fileName));
        var region = new PixelRect(0, 0, frame.Width, frame.Height);

        await ocr.RecognizeAsync(frame, region, CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();
        var result = await ocr.RecognizeAsync(
            frame,
            region,
            CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(expected, result.Text);
        Assert.Equal("ppocr-recognition-only", result.Provider);
        Assert.True(result.Confidence >= 0.95, $"confidence={result.Confidence}");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Warm recognition took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task SharedSessionHandlesParallelUiRegionsWithoutCrossTalk()
    {
        using var ocr = new PpOcrOfflineOcr(ModelPath, maximumConcurrency: 4);
        var samples = new[]
        {
            ("action-76-a.png", "76"),
            ("action-79-a.png", "79"),
            ("preparation-stage-label.png", "备战阶段"),
            ("settlement-damage-13249.8w.png", "13249.8万")
        };

        var work = Enumerable.Range(0, 4)
            .SelectMany(_ => samples)
            .Select(async sample =>
            {
                var frame = CaptureFrameLoader.LoadFile(Path.Combine(
                    FixtureDirectory,
                    sample.Item1));
                var result = await ocr.RecognizeAsync(
                    frame,
                    new PixelRect(0, 0, frame.Width, frame.Height),
                    CancellationToken.None);
                return (sample.Item2, result.Text);
            });

        var results = await Task.WhenAll(work);

        Assert.All(results, result => Assert.Equal(result.Item1, result.Text));
    }

    [Fact]
    public async Task ExplicitWarmUpKeepsFirstRealCropInsideRealtimeBudget()
    {
        using var ocr = new PpOcrOfflineOcr(ModelPath, maximumConcurrency: 2);
        await ocr.WarmUpAsync(CancellationToken.None);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            FixtureDirectory,
            "action-79-a.png"));

        var stopwatch = Stopwatch.StartNew();
        var result = await ocr.RecognizeAsync(
            frame,
            new PixelRect(0, 0, frame.Width, frame.Height),
            CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal("79", result.Text);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"First post-warm-up recognition took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task MissingModelUsesFallbackWithoutPretendingPrimarySucceeded()
    {
        using var missing = new PpOcrOfflineOcr(
            Path.Combine(RepositoryRoot, "missing-ocr-model.onnx"));
        var fallback = new StubOcr(new OcrTextResult("42", ["42"])
        {
            Provider = "test-fallback"
        });
        var combined = new ConfidenceFallbackOfflineOcr(missing, fallback);

        var result = await combined.RecognizeAsync(
            SinglePixelFrame(),
            new PixelRect(0, 0, 1, 1),
            CancellationToken.None);

        Assert.Equal("42", result.Text);
        Assert.Equal("test-fallback", result.Provider);
    }

    [Fact]
    public async Task LowConfidenceConflictPreservesBothReadings()
    {
        var primary = new StubOcr(new OcrTextResult("79", ["79"])
        {
            Confidence = 0.25,
            Provider = "primary"
        });
        var fallback = new StubOcr(new OcrTextResult("19", ["19"])
        {
            Provider = "fallback"
        });
        var combined = new ConfidenceFallbackOfflineOcr(
            primary,
            fallback,
            minimumPrimaryConfidence: 0.55);

        var result = await combined.RecognizeAsync(
            SinglePixelFrame(),
            new PixelRect(0, 0, 1, 1),
            CancellationToken.None);

        Assert.Equal("79", result.Text);
        Assert.Contains("79", result.Lines);
        Assert.Contains("19", result.Lines);
        Assert.Equal("primary+fallback", result.Provider);
    }

    [Fact]
    public void PackagedModelHashMatchesReviewedAsset()
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ModelPath)))
            .ToLowerInvariant();

        Assert.Equal(
            "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884",
            hash);
    }

    private static CaptureFrame SinglePixelFrame() => new(
        1,
        1,
        4,
        [0, 0, 0, 255],
        new PixelRect(0, 0, 1, 1),
        DateTimeOffset.UtcNow);

    private sealed class StubOcr(OcrTextResult result) : IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(result);
    }
}
