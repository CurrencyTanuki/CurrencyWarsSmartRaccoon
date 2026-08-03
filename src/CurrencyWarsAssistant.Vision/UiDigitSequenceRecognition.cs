using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public sealed record UiDigitGlyphRecognition(
    int Digit,
    double Confidence,
    double RunnerUpConfidence,
    PixelRect Region);

public sealed record UiDigitSequenceRecognition(
    int? Value,
    double Confidence,
    double RunnerUpConfidence,
    IReadOnlyList<UiDigitGlyphRecognition> Glyphs,
    string FailureReason)
{
    public bool IsRecognized => Value is not null;
}

public enum UiDigitForegroundStyle
{
    BrightOnDark,
    GoldSaturated,
    DarkOnLight
}

/// <summary>
/// Recognizes the small, fixed game UI digit font after a caller has already
/// localized the numeric field. This is deliberately not a general OCR path:
/// it only uses category-local templates and returns unknown on ambiguity.
/// </summary>
public sealed partial class OpenCvUiDigitSequenceRecognizer
{
    private const int NormalizedWidth = 32;
    private const int NormalizedHeight = 48;
    private const double MinimumConfidence = 0.58;
    private const double MinimumLeadOverRunnerUp = 0.035;
    private readonly ConcurrentDictionary<string, byte[]> _normalizedTemplates =
        new(StringComparer.OrdinalIgnoreCase);

    public UiDigitSequenceRecognition Recognize(
        CaptureFrame frame,
        PixelRect region,
        IReadOnlyList<Phase2IconTemplateDefinition> templates,
        int minimumValue,
        int maximumValue,
        UiDigitForegroundStyle foregroundStyle =
            UiDigitForegroundStyle.BrightOnDark)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(templates);
        if (region.IsEmpty ||
            region.X < 0 ||
            region.Y < 0 ||
            region.Right > frame.Width ||
            region.Bottom > frame.Height ||
            minimumValue > maximumValue)
        {
            return Unknown("The localized numeric region is invalid.");
        }

        var digitTemplates = templates
            .Where(item => string.Equals(
                item.Category,
                "action-value-digit",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => (
                Template: item,
                Digit: ParseDigit(item.Id)))
            .Where(item => item.Digit is not null)
            .Select(item => (item.Template, Digit: item.Digit!.Value))
            .ToArray();
        if (digitTemplates.Select(item => item.Digit).Distinct().Count() != 10)
        {
            return Unknown("The action-value digit template set is incomplete.");
        }

        using var source = ToBgr(frame);
        var compactField = foregroundStyle !=
                               UiDigitForegroundStyle.BrightOnDark ||
                           region.Width <= 80 && region.Height <= 55;
        var localX = (int)Math.Round(
            region.Width * (compactField ? 0.04 : 0.14));
        var localY = (int)Math.Round(
            region.Height * (compactField ? 0 : 0.10));
        var localizedRegion = new PixelRect(
            region.X + localX,
            region.Y + localY,
            Math.Max(
                8,
                (int)Math.Round(region.Width *
                                (compactField ? 0.92 : 0.70))),
            Math.Max(
                12,
                (int)Math.Round(region.Height *
                                (compactField ? 1.0 : 0.76))));
        using var crop = new Mat(
            source,
            new Rect(
                localizedRegion.X,
                localizedRegion.Y,
                localizedRegion.Width,
                localizedRegion.Height));
        using var mask = BuildForegroundMask(
            crop,
            removeLongHorizontalLines: false,
            foregroundStyle);
        var sliding = RecognizeBySlidingTemplates(
            mask,
            localizedRegion,
            digitTemplates,
            minimumValue,
            maximumValue);
        if (sliding.IsRecognized)
        {
            return sliding;
        }

        var projectedBounds = FindProjectedGlyphBounds(mask);
        var bounds = (projectedBounds.Count is >= 1 and <= 3
                ? projectedBounds
                : FindGlyphBounds(mask))
            .Where(item => item.X >= localizedRegion.Width * 0.04 &&
                           item.Right <= localizedRegion.Width * 0.96)
            .OrderByDescending(item => item.Width * item.Height)
            .Take(3)
            .OrderBy(item => item.X)
            .ToArray();
        var segmentationSummary =
            $"projected={string.Join(';', projectedBounds.Select(item => $"{item.X},{item.Y},{item.Width},{item.Height}"))}; " +
            $"selected={string.Join(';', bounds.Select(item => $"{item.X},{item.Y},{item.Width},{item.Height}"))}";
        if (bounds.Length is < 1 or > 3)
        {
            return Unknown("No unambiguous digit components were found.");
        }

        var glyphResults = new List<UiDigitGlyphRecognition>(bounds.Length);
        var value = 0;
        var confidence = 1d;
        var runnerUpConfidence = 0d;
        foreach (var glyphBounds in bounds)
        {
            using var glyph = new Mat(mask, glyphBounds);
            using var normalized = NormalizeGlyph(glyph);
            var normalizedBytes = CopyBytes(normalized);
            var ranked = digitTemplates
                .Select(item => (
                    item.Digit,
                    Confidence: Match(
                        normalizedBytes,
                        LoadTemplate(item.Template.FilePath))))
                .GroupBy(item => item.Digit)
                .Select(group => group.OrderByDescending(item => item.Confidence)
                    .First())
                .OrderByDescending(item => item.Confidence)
                .Take(2)
                .ToArray();
            if (ranked.Length < 2)
            {
                return Unknown("The digit template ranking was incomplete.");
            }

            var best = ranked[0];
            var runnerUp = ranked[1].Confidence;
            glyphResults.Add(new UiDigitGlyphRecognition(
                best.Digit,
                best.Confidence,
                runnerUp,
                new PixelRect(
                    localizedRegion.X + glyphBounds.X,
                    localizedRegion.Y + glyphBounds.Y,
                    glyphBounds.Width,
                    glyphBounds.Height)));
            if (best.Confidence < MinimumConfidence ||
                best.Confidence - runnerUp < MinimumLeadOverRunnerUp)
            {
                return new UiDigitSequenceRecognition(
                    null,
                    best.Confidence,
                    runnerUp,
                    glyphResults,
                    "At least one digit was below the category-local confidence or margin requirement. " +
                    segmentationSummary + "; sliding=" + sliding.FailureReason);
            }

            value = checked(value * 10 + best.Digit);
            confidence = Math.Min(confidence, best.Confidence);
            runnerUpConfidence = Math.Max(runnerUpConfidence, runnerUp);
        }

        return value >= minimumValue && value <= maximumValue
            ? new UiDigitSequenceRecognition(
                value,
                confidence,
                runnerUpConfidence,
                glyphResults,
                string.Empty)
            : new UiDigitSequenceRecognition(
                null,
                confidence,
                runnerUpConfidence,
                glyphResults,
                $"The recognized value {value.ToString(CultureInfo.InvariantCulture)} is outside the allowed range.");
    }

    private UiDigitSequenceRecognition RecognizeBySlidingTemplates(
        Mat mask,
        PixelRect sourceRegion,
        IReadOnlyList<(Phase2IconTemplateDefinition Template, int Digit)> templates,
        int minimumValue,
        int maximumValue)
    {
        var candidates = new List<SlidingDigitCandidate>();
        foreach (var (template, digit) in templates)
        {
            var bytes = LoadTemplate(template.FilePath);
            using var normalized = new Mat(
                NormalizedHeight,
                NormalizedWidth,
                MatType.CV_8UC1);
            Marshal.Copy(bytes, 0, normalized.Data, bytes.Length);
            foreach (var height in new[] { 24, 28, 32, 36, 40 })
            {
                var width = Math.Max(
                    8,
                    (int)Math.Round(height * NormalizedWidth /
                                    (double)NormalizedHeight));
                if (width > mask.Width || height > mask.Height)
                {
                    continue;
                }

                using var resized = new Mat();
                Cv2.Resize(
                    normalized,
                    resized,
                    new Size(width, height),
                    interpolation: InterpolationFlags.Area);
                using var scores = new Mat();
                Cv2.MatchTemplate(
                    mask,
                    resized,
                    scores,
                    TemplateMatchModes.CCoeffNormed);
                for (var peakIndex = 0; peakIndex < 3; peakIndex++)
                {
                    Cv2.MinMaxLoc(
                        scores,
                        out _,
                        out var maximum,
                        out _,
                        out var location);
                    if (maximum < 0.40)
                    {
                        break;
                    }

                    var centerX = location.X + width / 2d;
                    if (centerX >= mask.Width * 0.08 &&
                        centerX <= mask.Width * 0.86)
                    {
                        candidates.Add(new SlidingDigitCandidate(
                            digit,
                            maximum,
                            TopologyAdjustment(
                                mask,
                                new Rect(location.X, location.Y, width, height),
                                digit),
                            new Rect(location.X, location.Y, width, height)));
                    }

                    var suppression = new Rect(
                        Math.Max(0, location.X - width / 2),
                        Math.Max(0, location.Y - height / 3),
                        Math.Min(
                            scores.Width - Math.Max(0, location.X - width / 2),
                            width * 2),
                        Math.Min(
                            scores.Height - Math.Max(0, location.Y - height / 3),
                            height + height * 2 / 3));
                    if (suppression.Width > 0 && suppression.Height > 0)
                    {
                        using var suppressed = new Mat(scores, suppression);
                        suppressed.SetTo(new Scalar(-1));
                    }
                }
            }
        }

        var clusters = new List<List<SlidingDigitCandidate>>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Confidence))
        {
            var centerX = candidate.Region.X + candidate.Region.Width / 2d;
            var centerY = candidate.Region.Y + candidate.Region.Height / 2d;
            var cluster = clusters.FirstOrDefault(group =>
            {
                var anchor = group[0];
                var anchorX = anchor.Region.X + anchor.Region.Width / 2d;
                var anchorY = anchor.Region.Y + anchor.Region.Height / 2d;
                var horizontalTolerance = Math.Max(
                    8,
                    Math.Min(
                        candidate.Region.Width,
                        anchor.Region.Width) * 0.60);
                var verticalTolerance = Math.Max(
                    8,
                    Math.Min(
                        candidate.Region.Height,
                        anchor.Region.Height) * 0.45);
                return Math.Abs(centerX - anchorX) <= horizontalTolerance &&
                       Math.Abs(centerY - anchorY) <= verticalTolerance;
            });
            if (cluster is null)
            {
                clusters.Add([candidate]);
            }
            else
            {
                cluster.Add(candidate);
            }
        }

        var rankedClusters = clusters.Select(group =>
            {
                var byDigit = group
                    .GroupBy(item => item.Digit)
                    .Select(digitGroup => digitGroup
                        .OrderByDescending(item => item.EffectiveConfidence)
                        .First())
                    .OrderByDescending(item => item.EffectiveConfidence)
                    .ToArray();
                return (
                    Best: byDigit[0],
                    RunnerUp: byDigit.Length > 1
                        ? byDigit[1].EffectiveConfidence
                        : 0d);
            })
            .Where(item => item.Best.EffectiveConfidence >= 0.48 &&
                           item.Best.EffectiveConfidence - item.RunnerUp >= 0.035)
            .OrderByDescending(item => item.Best.EffectiveConfidence)
            .ToArray();
        if (rankedClusters.Length == 0)
        {
            return Unknown("Sliding digit matching found no confident candidates.");
        }

        var anchor = rankedClusters[0].Best;
        var anchorBottom = anchor.Region.Bottom;
        var selected = rankedClusters
            .Where(item => Math.Abs(
                    item.Best.Region.Bottom - anchorBottom) <= 6)
            .OrderByDescending(item => item.Best.Confidence)
            .Take(3)
            .OrderBy(item => item.Best.Region.X)
            .ToArray();
        if (selected.Length is < 1 or > 3)
        {
            return Unknown("Sliding digit candidates did not form a valid sequence.");
        }

        var value = 0;
        var glyphs = new List<UiDigitGlyphRecognition>(selected.Length);
        var confidence = 1d;
        var runnerUpConfidence = 0d;
        foreach (var item in selected)
        {
            value = checked(value * 10 + item.Best.Digit);
            confidence = Math.Min(
                confidence,
                item.Best.EffectiveConfidence);
            runnerUpConfidence = Math.Max(
                runnerUpConfidence,
                item.RunnerUp);
            glyphs.Add(new UiDigitGlyphRecognition(
                item.Best.Digit,
                item.Best.EffectiveConfidence,
                item.RunnerUp,
                new PixelRect(
                    sourceRegion.X + item.Best.Region.X,
                    sourceRegion.Y + item.Best.Region.Y,
                    item.Best.Region.Width,
                    item.Best.Region.Height)));
        }

        return value >= minimumValue && value <= maximumValue
            ? new UiDigitSequenceRecognition(
                value,
                confidence,
                runnerUpConfidence,
                glyphs,
                string.Empty)
            : Unknown(
                $"Sliding digit sequence {value.ToString(CultureInfo.InvariantCulture)} is outside the allowed range.");
    }

    private byte[] LoadTemplate(string path) =>
        _normalizedTemplates.GetOrAdd(
            path,
            static file =>
            {
                using var source = Cv2.ImRead(file, ImreadModes.Color);
                if (source.Empty())
                {
                    throw new InvalidDataException(
                        $"Unable to decode UI digit template: {file}");
                }

                using var mask = BuildForegroundMask(
                    source,
                    removeLongHorizontalLines: false);
                var bounds = FindTemplateGlyphBounds(mask);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    throw new InvalidDataException(
                        $"UI digit template does not contain a glyph: {file}");
                }

                using var glyph = new Mat(mask, bounds);
                using var normalized = NormalizeGlyph(glyph);
                return CopyBytes(normalized);
            });

    private static IReadOnlyList<Rect> FindProjectedGlyphBounds(Mat mask)
    {
        var minimumColumnPixels = Math.Max(3, mask.Height / 12);
        var startX = Math.Max(0, (int)Math.Round(mask.Width * 0.08));
        var endX = Math.Min(
            mask.Width,
            (int)Math.Round(mask.Width * 0.86));
        var active = new bool[mask.Width];
        for (var x = startX; x < endX; x++)
        {
            var count = 0;
            for (var y = 0; y < mask.Height; y++)
            {
                if (mask.At<byte>(y, x) != 0)
                {
                    count++;
                }
            }

            active[x] = count >= minimumColumnPixels;
        }

        var results = new List<Rect>();
        var cursor = startX;
        while (cursor < endX)
        {
            while (cursor < endX && !active[cursor])
            {
                cursor++;
            }

            if (cursor >= endX)
            {
                break;
            }

            var left = cursor;
            while (cursor < endX && active[cursor])
            {
                cursor++;
            }

            var right = cursor;
            if (right - left < 3 || right - left > mask.Width * 0.30)
            {
                continue;
            }

            var top = mask.Height;
            var bottom = -1;
            for (var y = 0; y < mask.Height; y++)
            {
                for (var x = left; x < right; x++)
                {
                    if (mask.At<byte>(y, x) == 0)
                    {
                        continue;
                    }

                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }

            if (bottom >= top && bottom - top + 1 >= mask.Height * 0.25)
            {
                results.Add(new Rect(
                    left,
                    top,
                    right - left,
                    bottom - top + 1));
            }
        }

        return results;
    }

    private static Rect FindTemplateGlyphBounds(Mat mask)
    {
        using var labels = new Mat();
        using var statistics = new Mat();
        using var centroids = new Mat();
        var componentCount = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            statistics,
            centroids,
            PixelConnectivity.Connectivity8);
        return Enumerable.Range(1, componentCount - 1)
            .Select(component => new Rect(
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Left),
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Top),
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Width),
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Height)))
            .Where(item => item.Width >= 2 &&
                           item.Height >= Math.Max(8, mask.Height / 3))
            .OrderByDescending(item => item.Width * item.Height)
            .FirstOrDefault();
    }

    private static IReadOnlyList<Rect> FindGlyphBounds(Mat mask)
    {
        using var labels = new Mat();
        using var statistics = new Mat();
        using var centroids = new Mat();
        var componentCount = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            statistics,
            centroids,
            PixelConnectivity.Connectivity8);
        var minimumHeight = Math.Max(10, (int)Math.Round(mask.Height * 0.28));
        var maximumHeight = Math.Max(
            minimumHeight,
            (int)Math.Round(mask.Height * 0.85));
        var maximumWidth = Math.Max(12, (int)Math.Round(mask.Width * 0.34));
        return Enumerable.Range(1, componentCount - 1)
            .Select(component => new Rect(
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Left),
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Top),
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Width),
                statistics.At<int>(component, (int)ConnectedComponentsTypes.Height)))
            .Where(item => item.Width is >= 3 &&
                           item.Width <= maximumWidth &&
                           item.Height >= minimumHeight &&
                           item.Height <= maximumHeight &&
                           item.Width * item.Height >= 30)
            .ToArray();
    }

    private static Mat BuildForegroundMask(
        Mat source,
        bool removeLongHorizontalLines,
        UiDigitForegroundStyle foregroundStyle =
            UiDigitForegroundStyle.BrightOnDark)
    {
        var mask = new Mat();
        if (foregroundStyle == UiDigitForegroundStyle.GoldSaturated)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(
                hsv,
                new Scalar(7, 55, 95),
                new Scalar(42, 255, 255),
                mask);
        }
        else if (foregroundStyle == UiDigitForegroundStyle.DarkOnLight)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(
                hsv,
                new Scalar(0, 0, 0),
                new Scalar(180, 90, 165),
                mask);
        }
        else
        {
            using var grayscale = new Mat();
            Cv2.CvtColor(source, grayscale, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(
                grayscale,
                mask,
                165,
                byte.MaxValue,
                ThresholdTypes.Binary);
        }
        if (removeLongHorizontalLines)
        {
            using var horizontal = new Mat();
            using var horizontalKernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new Size(Math.Max(18, source.Width / 5), 1));
            Cv2.MorphologyEx(
                mask,
                horizontal,
                MorphTypes.Open,
                horizontalKernel);
            Cv2.Subtract(mask, horizontal, mask);
        }

        using var closeKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(2, 2));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
        return mask;
    }

    private static Mat NormalizeGlyph(Mat glyph)
    {
        var output = Mat.Zeros(
                NormalizedHeight,
                NormalizedWidth,
                MatType.CV_8UC1)
            .ToMat();
        var scale = Math.Min(24d / glyph.Width, 40d / glyph.Height);
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

    private static double Match(byte[] glyph, byte[] template)
    {
        var intersection = 0;
        var glyphForeground = 0;
        var templateForeground = 0;
        for (var index = 0; index < glyph.Length; index++)
        {
            var glyphSet = glyph[index] >= 128;
            var templateSet = template[index] >= 128;
            if (glyphSet)
            {
                glyphForeground++;
            }

            if (templateSet)
            {
                templateForeground++;
            }

            if (glyphSet && templateSet)
            {
                intersection++;
            }
        }

        var denominator = glyphForeground + templateForeground;
        return denominator == 0 ? 0 : 2d * intersection / denominator;
    }

    private static double TopologyAdjustment(Mat mask, Rect region, int digit)
    {
        if (digit is not 0 and not 8)
        {
            return 0;
        }

        using var candidate = new Mat(mask, region);
        Cv2.FindContours(
            candidate,
            out Point[][] contours,
            out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp,
            ContourApproximationModes.ApproxSimple);
        var minimumHoleArea = Math.Max(3, region.Width * region.Height * 0.008);
        var holes = Enumerable.Range(0, contours.Length)
            .Count(index => hierarchy[index].Parent >= 0 &&
                            Math.Abs(Cv2.ContourArea(contours[index])) >=
                            minimumHoleArea);
        var expected = digit == 8 ? 2 : 1;
        return holes == expected
            ? 0.06
            : holes is 1 or 2
                ? -0.06
                : 0;
    }

    private static byte[] CopyBytes(Mat source)
    {
        var bytes = new byte[NormalizedWidth * NormalizedHeight];
        Marshal.Copy(source.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static Mat ToBgr(CaptureFrame frame)
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
        return bgr;
    }

    private static int? ParseDigit(string id)
    {
        var match = DigitTemplatePattern().Match(id);
        return match.Success
            ? int.Parse(match.Groups["digit"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private static UiDigitSequenceRecognition Unknown(string reason) =>
        new(null, 0, 0, [], reason);

    private sealed record SlidingDigitCandidate(
        int Digit,
        double Confidence,
        double TopologyAdjustment,
        Rect Region)
    {
        public double EffectiveConfidence => Math.Clamp(
            Confidence + TopologyAdjustment,
            0,
            1);
    }

    [GeneratedRegex(
        "^digit_(?<digit>[0-9])(?:__|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DigitTemplatePattern();
}
