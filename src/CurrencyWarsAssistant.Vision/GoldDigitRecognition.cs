using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public sealed record GoldDigitTemplateDefinition(int Digit, string File);

public sealed record GoldDigitRecognition(
    int? Value,
    double Confidence,
    double RunnerUpConfidence)
{
    public bool IsRecognized => Value is not null;
}

public interface IGoldDigitRecognizer
{
    GoldDigitRecognition Recognize(
        CaptureFrame frame,
        IReadOnlyList<GoldDigitTemplateDefinition> templates,
        PixelRect referenceRegion);
}

public sealed class OpenCvGoldDigitRecognizer :
    IGoldDigitRecognizer,
    IDisposable
{
    private const byte DarkDigitThreshold = 140;
    private const double MinimumConfidence = 0.78;
    private const double MinimumLeadOverRunnerUp = 0.20;
    private const int NormalizedWidth = 32;
    private const int NormalizedHeight = 48;
    private readonly ConcurrentDictionary<string, Mat> loadedTemplates =
        new(StringComparer.OrdinalIgnoreCase);

    public GoldDigitRecognition Recognize(
        CaptureFrame frame,
        IReadOnlyList<GoldDigitTemplateDefinition> templates,
        PixelRect referenceRegion)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(templates);
        if (referenceRegion.IsEmpty ||
            !OpenCvTemplateMatcher.HasSupportedAspectRatio(
                frame.Width,
                frame.Height))
        {
            return Unknown();
        }

        using var normalized = Normalize(frame);
        if (referenceRegion.X < 0 ||
            referenceRegion.Y < 0 ||
            referenceRegion.Right > normalized.Width ||
            referenceRegion.Bottom > normalized.Height)
        {
            return Unknown();
        }

        using var region = new Mat(
            normalized,
            new Rect(
                referenceRegion.X,
                referenceRegion.Y,
                referenceRegion.Width,
                referenceRegion.Height));
        using var grayscale = new Mat();
        Cv2.CvtColor(region, grayscale, ColorConversionCodes.BGR2GRAY);
        using var binary = new Mat();
        Cv2.Threshold(
            grayscale,
            binary,
            DarkDigitThreshold,
            byte.MaxValue,
            ThresholdTypes.BinaryInv);
        using var labels = new Mat();
        using var statistics = new Mat();
        using var centroids = new Mat();
        var componentCount = Cv2.ConnectedComponentsWithStats(
            binary,
            labels,
            statistics,
            centroids,
            PixelConnectivity.Connectivity8);
        var glyphBounds = Enumerable.Range(1, componentCount - 1)
            .Select(component => new Rect(
                statistics.At<int>(
                    component,
                    (int)ConnectedComponentsTypes.Left),
                statistics.At<int>(
                    component,
                    (int)ConnectedComponentsTypes.Top),
                statistics.At<int>(
                    component,
                    (int)ConnectedComponentsTypes.Width),
                statistics.At<int>(
                    component,
                    (int)ConnectedComponentsTypes.Height)))
            .Where(bounds =>
                bounds.X >= 18 &&
                bounds.Width is >= 4 and <= 24 &&
                bounds.Height is >= 20 and <= 38)
            .OrderBy(bounds => bounds.X)
            .ToArray();
        if (glyphBounds.Length is < 1 or > 2)
        {
            return Unknown();
        }

        var value = 0;
        var confidence = 1d;
        var runnerUpConfidence = 0d;
        foreach (var bounds in glyphBounds)
        {
            using var glyph = new Mat(binary, bounds);
            using var normalizedGlyph = NormalizeGlyph(glyph);
            var ranked = templates
                .Select(template => (
                    template.Digit,
                    Confidence: Match(
                        normalizedGlyph,
                        LoadTemplate(template.File))))
                .OrderByDescending(item => item.Confidence)
                .Take(2)
                .ToArray();
            if (ranked.Length == 0)
            {
                return Unknown();
            }

            var best = ranked[0];
            var runnerUp = ranked.Length > 1
                ? ranked[1].Confidence
                : 0;
            if (best.Confidence < MinimumConfidence ||
                best.Confidence - runnerUp < MinimumLeadOverRunnerUp)
            {
                return new GoldDigitRecognition(
                    null,
                    best.Confidence,
                    runnerUp);
            }

            value = checked(value * 10 + best.Digit);
            confidence = Math.Min(confidence, best.Confidence);
            runnerUpConfidence = Math.Max(runnerUpConfidence, runnerUp);
        }

        return new GoldDigitRecognition(
            value,
            confidence,
            runnerUpConfidence);
    }

    public void Dispose()
    {
        foreach (var template in loadedTemplates.Values)
        {
            template.Dispose();
        }

        loadedTemplates.Clear();
    }

    private Mat LoadTemplate(string path) =>
        loadedTemplates.GetOrAdd(
            path,
            static file =>
            {
                var template = Cv2.ImRead(file, ImreadModes.Grayscale);
                if (template.Empty() ||
                    template.Width != NormalizedWidth ||
                    template.Height != NormalizedHeight)
                {
                    template.Dispose();
                    throw new InvalidDataException(
                        $"金币数字模板必须为 {NormalizedWidth}x{NormalizedHeight}：{file}");
                }

                return template;
            });

    private static double Match(Mat glyph, Mat template)
    {
        using var scores = new Mat();
        Cv2.MatchTemplate(
            glyph,
            template,
            scores,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out var maximum, out _, out _);
        return maximum;
    }

    private static Mat NormalizeGlyph(Mat glyph)
    {
        var output = Mat.Zeros(
                NormalizedHeight,
                NormalizedWidth,
                MatType.CV_8UC1)
            .ToMat();
        var scale = Math.Min(
            24d / glyph.Width,
            40d / glyph.Height);
        var width = Math.Max(1, (int)Math.Round(glyph.Width * scale));
        var height = Math.Max(1, (int)Math.Round(glyph.Height * scale));
        using var resized = new Mat();
        Cv2.Resize(
            glyph,
            resized,
            new Size(width, height),
            interpolation: InterpolationFlags.Area);
        using var target = new Mat(
            output,
            new Rect(
                (NormalizedWidth - width) / 2,
                (NormalizedHeight - height) / 2,
                width,
                height));
        resized.CopyTo(target);
        return output;
    }

    private static Mat Normalize(CaptureFrame frame)
    {
        using var bgra = new Mat(
            frame.Height,
            frame.Width,
            MatType.CV_8UC4);
        Marshal.Copy(
            frame.BgraPixels,
            0,
            bgra.Data,
            frame.BgraPixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        if (frame.Width == OpenCvTemplateMatcher.ReferenceWidth &&
            frame.Height == OpenCvTemplateMatcher.ReferenceHeight)
        {
            return bgr;
        }

        var normalized = new Mat();
        Cv2.Resize(
            bgr,
            normalized,
            new Size(
                OpenCvTemplateMatcher.ReferenceWidth,
                OpenCvTemplateMatcher.ReferenceHeight),
            interpolation: InterpolationFlags.Area);
        bgr.Dispose();
        return normalized;
    }

    private static GoldDigitRecognition Unknown() => new(null, 0, 0);
}
