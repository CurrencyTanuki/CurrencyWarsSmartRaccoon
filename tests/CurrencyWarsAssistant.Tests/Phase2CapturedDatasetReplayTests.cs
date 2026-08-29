using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2CapturedDatasetReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void OptionalFiveFpsDatasetExercisesProductionFastSelector()
    {
        var datasetDirectory = Environment.GetEnvironmentVariable(
            "CWA_PHASE2_REPLAY_DATASET");
        if (string.IsNullOrWhiteSpace(datasetDirectory))
        {
            output.WriteLine(
                "CWA_PHASE2_REPLAY_DATASET is not set; optional local replay skipped.");
            return;
        }

        var frameDirectory = Directory.Exists(
            Path.Combine(datasetDirectory, "frames"))
            ? Path.Combine(datasetDirectory, "frames")
            : datasetDirectory;
        var files = Directory.EnumerateFiles(frameDirectory, "*.png")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            repositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        var classifier = new Phase2FastPageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
        var selector = new Phase2RealtimeFrameSelector();
        var lastKnownPage = Phase2PageFamily.Unknown;
        var selectedCount = 0;
        var criticalCount = 0;
        var classifierTimes = new List<double>(files.Length);
        var signatureTimes = new List<double>(files.Length);
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        var transitions = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknownFiles = new List<string>();
        var unknownDiagnostics = new Dictionary<
            string,
            IReadOnlyList<PageAnchorDiagnostic>>(StringComparer.Ordinal);
        var selectedFrames = new List<SelectedFrameDiagnostic>();
        string? previousMatchedPageId = null;

        foreach (var path in files)
        {
            var frame = CaptureFrameLoader.LoadFile(path) with
            {
                CapturedAt = ParseCapturedAt(path)
            };
            var classifierStopwatch = Stopwatch.StartNew();
            var hint = classifier.Classify(frame);
            classifierStopwatch.Stop();
            classifierTimes.Add(classifierStopwatch.Elapsed.TotalMilliseconds);
            var pageId = hint.IsMatched
                ? hint.PageId ?? hint.PageFamily.ToString()
                : "unknown";
            pages[pageId] = pages.GetValueOrDefault(pageId) + 1;
            if (!hint.IsMatched)
            {
                var fileName = Path.GetFileName(path);
                unknownFiles.Add(fileName);
                unknownDiagnostics[fileName] = classifier.LastDiagnostics
                    .OrderByDescending(item => item.Confidence - item.Threshold)
                    .Take(5)
                    .ToArray();
            }
            if (hint.IsMatched &&
                previousMatchedPageId is not null &&
                !string.Equals(
                    previousMatchedPageId,
                    hint.PageId,
                    StringComparison.Ordinal))
            {
                var transition = $"{previousMatchedPageId}->{hint.PageId}";
                transitions[transition] = transitions.GetValueOrDefault(transition) + 1;
            }

            if (hint.IsMatched)
            {
                previousMatchedPageId = hint.PageId;
            }
            var signatureStopwatch = Stopwatch.StartNew();
            var selection = selector.Observe(
                frame,
                wasReliable: hint.IsMatched,
                lastKnownPage,
                hint);
            signatureStopwatch.Stop();
            signatureTimes.Add(signatureStopwatch.Elapsed.TotalMilliseconds);
            selectedCount += selection.FramesToRecognize.Count;
            criticalCount += selection.FramesToRecognize.Count(item =>
                item.IsCritical);
            selectedFrames.AddRange(selection.FramesToRecognize.Select(item =>
                new SelectedFrameDiagnostic(
                    item.BufferedFrame.Frame.CapturedAt,
                    item.IsCritical)));
            if (hint.PageFamily != Phase2PageFamily.Unknown)
            {
                lastKnownPage = hint.PageFamily;
            }
        }

        var report = new
        {
            frameCount = files.Length,
            selectedCount,
            criticalCount,
            pageCounts = pages,
            pageTransitions = transitions,
            unknownFiles,
            unknownDiagnostics,
            selectedFrames,
            classifierMilliseconds = Metrics(classifierTimes),
            signatureAndSelectionMilliseconds = Metrics(signatureTimes)
        };
        var json = JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        var reportPath = Environment.GetEnvironmentVariable(
            "CWA_PHASE2_REPLAY_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, json);
        }

        Assert.True(
            report.signatureAndSelectionMilliseconds.P95 < 50,
            "Perceptual signatures and selection must stay well below the 200 ms capture interval.");
        Assert.True(
            selectedCount < files.Length / 2,
            "The bounded selector must not promote most 5 FPS animation frames to full OCR work.");
    }

    private static DateTimeOffset ParseCapturedAt(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var timestamp = stem.Split('-', 2)[1];
        return DateTimeOffset.ParseExact(
            timestamp,
            "yyyyMMdd-HHmmssfff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
    }

    private static ReplayMetrics Metrics(IReadOnlyList<double> values)
    {
        var ordered = values.Order().ToArray();
        return new ReplayMetrics(
            ordered.Average(),
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1]);
    }

    private static double Percentile(IReadOnlyList<double> values, double p) =>
        values[(int)Math.Floor((values.Count - 1) * p)];

    private sealed record ReplayMetrics(
        double Average,
        double P50,
        double P95,
        double P99,
        double Maximum);

    private sealed record SelectedFrameDiagnostic(
        DateTimeOffset CapturedAt,
        bool IsCritical);
}
