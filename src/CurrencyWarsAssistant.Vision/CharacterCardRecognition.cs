using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public sealed record CharacterCardTemplateDefinition(
    string CharacterId,
    string DisplayName,
    string File,
    CharacterCardTemplateKind Kind = CharacterCardTemplateKind.Character);

public enum CharacterCardTemplateKind
{
    Character,
    SpecialOccupied
}

public enum CharacterCardSlotState
{
    Empty,
    Recognized,
    SpecialOccupied,
    Uncertain
}

public sealed record CharacterCardSlotRecognition(
    int SlotIndex,
    PixelRect ReferenceBounds,
    CharacterCardSlotState State,
    string? CharacterId,
    string? DisplayName,
    double Confidence,
    double RunnerUpConfidence,
    double VisualStandardDeviation,
    string? RunnerUpCharacterId = null,
    string? RunnerUpDisplayName = null,
    string? MatchedTemplateId = null,
    int? StarLevel = null,
    double StarConfidence = 0);

public readonly record struct CharacterCardRecognitionOptions(
    double HorizontalTemplateScale = 1,
    double VerticalTemplateScale = 1)
{
    public CharacterCardRecognitionOptions(double uniformTemplateScale)
        : this(uniformTemplateScale, uniformTemplateScale)
    {
    }

    public static CharacterCardRecognitionOptions Standard => new(1, 1);

    public static CharacterCardRecognitionOptions RewardShopCompact =>
        new(0.80, 0.80);
}

public interface ICharacterCardRecognizer
{
    IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots);

    IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots,
        CharacterCardRecognitionOptions options) =>
        Recognize(frame, templates, referenceSlots);
}

public sealed class OpenCvCharacterCardRecognizer :
    ICharacterCardRecognizer,
    IDisposable
{
    private const double EmptyVisualStandardDeviation = 18;
    private const double MinimumCharacterConfidence = 0.58;
    private const double MinimumLeadOverRunnerUp = 0.055;
    private const double ColorSimilarityGrace = 0.90;
    private const double ColorMismatchPenaltyWeight = 0.50;
    private const int StandardTemplateWidth = 111;
    private const int StandardTemplateHeight = 127;
    private const int HorizontalSearchPadding = 10;
    private readonly ConcurrentDictionary<string, Mat> _templates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(
        string File,
        int HorizontalScalePermille,
        int VerticalScalePermille), Mat>
        _scaledTemplates = new();
    private readonly ConcurrentDictionary<string, float[]> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly int _candidateLimit;

    public OpenCvCharacterCardRecognizer(int candidateLimit = 32)
    {
        if (candidateLimit < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateLimit),
                "At least two candidates are required for the runner-up gate.");
        }

        _candidateLimit = candidateLimit;
    }

    public int LastExactComparisonCount { get; private set; }
    public int LastDecisiveShortlistCount { get; private set; }

    /// <summary>
    /// Preloads the compact search index and normalized card templates so the
    /// first preparation frame does not pay the one-time disk/decode cost.
    /// </summary>
    public Task WarmUpAsync(
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return Task.Run(
            () => Parallel.ForEach(
                templates
                    .GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4
                },
                template =>
                {
                    _ = _descriptors.GetOrAdd(
                        template.File,
                        _ => CreateCompactDescriptor(
                            LoadTemplate(template.File)));
                }),
            cancellationToken);
    }

    public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots) => Recognize(
        frame,
        templates,
        referenceSlots,
        CharacterCardRecognitionOptions.Standard);

    public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots,
        CharacterCardRecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(referenceSlots);
        if (!OpenCvTemplateMatcher.HasSupportedAspectRatio(
                frame.Width,
                frame.Height))
        {
            return referenceSlots
                .Select((slot, index) => Uncertain(index, slot, 0))
                .ToArray();
        }

        using var normalized = Normalize(frame);
        LastDecisiveShortlistCount = 0;
        var results = new List<CharacterCardSlotRecognition>(
            referenceSlots.Count);
        for (var index = 0; index < referenceSlots.Count; index++)
        {
            var slot = referenceSlots[index];
            if (slot.IsEmpty ||
                slot.X < 0 ||
                slot.Y < 0 ||
                slot.Right > normalized.Width ||
                slot.Bottom > normalized.Height)
            {
                results.Add(Uncertain(index, slot, 0));
                continue;
            }

            using var slotImage = new Mat(
                normalized,
                new Rect(slot.X, slot.Y, slot.Width, slot.Height));
            using var grayscale = new Mat();
            Cv2.CvtColor(
                slotImage,
                grayscale,
                ColorConversionCodes.BGR2GRAY);
            var insetX = Math.Max(4, (int)Math.Round(slot.Width * 0.08));
            var insetTop = Math.Max(4, (int)Math.Round(slot.Height * 0.08));
            var insetBottom = Math.Max(
                4,
                (int)Math.Round(slot.Height * 0.12));
            using var occupancyInterior = new Mat(
                grayscale,
                new Rect(
                    insetX,
                    insetTop,
                    Math.Max(1, grayscale.Width - insetX * 2),
                    Math.Max(1, grayscale.Height - insetTop - insetBottom)));
            Cv2.MeanStdDev(
                occupancyInterior,
                out _,
                out var standardDeviation);
            var visualStandardDeviation = standardDeviation.Val0;
            if (visualStandardDeviation <= EmptyVisualStandardDeviation)
            {
                results.Add(new CharacterCardSlotRecognition(
                    index,
                    slot,
                    CharacterCardSlotState.Empty,
                    null,
                    null,
                    0,
                    0,
                    visualStandardDeviation));
                continue;
            }

            var starRecognition = RecognizeStarLevel(slotImage);

            var searchLeft = Math.Max(0, slot.X - HorizontalSearchPadding);
            var searchRight = Math.Min(
                normalized.Width,
                slot.Right + HorizontalSearchPadding);
            using var searchImage = new Mat(
                normalized,
                new Rect(
                    searchLeft,
                    slot.Y,
                    searchRight - searchLeft,
                    slot.Height));

            var shortlisted = Shortlist(slotImage, templates);
            LastExactComparisonCount = shortlisted.Count;
            var ranked = Rank(searchImage, shortlisted, options);
            if (IsDecisive(ranked))
            {
                LastDecisiveShortlistCount++;
            }
            else if (shortlisted.Count < templates.Count)
            {
                // Uncertain slots are evidence too. Preserve the exhaustive
                // best/runner-up candidates for degraded records while the
                // high-confidence common path stays bounded by the shortlist.
                ranked = Rank(searchImage, templates, options);
                LastExactComparisonCount = templates.Count;
            }
            var best = ranked.FirstOrDefault();
            var runnerUp = ranked.Length > 1
                ? ranked[1].Confidence
                : 0;
            if (best.Definition is not null &&
                best.Confidence >= MinimumCharacterConfidence &&
                best.Confidence - runnerUp >= MinimumLeadOverRunnerUp)
            {
                var isSpecialOccupied =
                    best.Definition.Kind ==
                    CharacterCardTemplateKind.SpecialOccupied;
                results.Add(new CharacterCardSlotRecognition(
                    index,
                    slot,
                    isSpecialOccupied
                        ? CharacterCardSlotState.SpecialOccupied
                        : CharacterCardSlotState.Recognized,
                    isSpecialOccupied
                        ? null
                        : best.Definition.CharacterId,
                    best.Definition.DisplayName,
                    best.Confidence,
                    runnerUp,
                    visualStandardDeviation,
                    ranked.Length > 1
                        ? ranked[1].Definition.CharacterId
                        : null,
                    ranked.Length > 1
                        ? ranked[1].Definition.DisplayName
                        : null,
                    best.Definition.CharacterId,
                    starRecognition.Level,
                    starRecognition.Confidence));
                continue;
            }

            results.Add(new CharacterCardSlotRecognition(
                index,
                slot,
                CharacterCardSlotState.Uncertain,
                best.Definition?.CharacterId,
                best.Definition?.DisplayName,
                best.Confidence,
                runnerUp,
                visualStandardDeviation,
                ranked.Length > 1
                    ? ranked[1].Definition.CharacterId
                    : null,
                ranked.Length > 1
                    ? ranked[1].Definition.DisplayName
                    : null,
                StarLevel: starRecognition.Level,
                StarConfidence: starRecognition.Confidence));
        }

        return results;
    }

    private static (int? Level, double Confidence) RecognizeStarLevel(
        Mat slotImage)
    {
        // Character stars are rendered in a narrow, stable band near the
        // bottom centre of every preparation card. Work inside the already
        // normalized slot so this remains resolution independent and adds no
        // second frame conversion.
        var left = Math.Max(0, (int)Math.Round(slotImage.Width * 0.16));
        var top = Math.Max(0, (int)Math.Round(slotImage.Height * 0.66));
        var width = Math.Min(
            slotImage.Width - left,
            Math.Max(1, (int)Math.Round(slotImage.Width * 0.68)));
        var height = Math.Min(
            slotImage.Height - top,
            Math.Max(1, (int)Math.Round(slotImage.Height * 0.28)));
        if (width < 8 || height < 8)
        {
            return (null, 0);
        }

        using var band = new Mat(slotImage, new Rect(left, top, width, height));
        using var hsv = new Mat();
        using var goldMask = new Mat();
        Cv2.CvtColor(band, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(
            hsv,
            new Scalar(5, 55, 165),
            new Scalar(42, 255, 255),
            goldMask);

        using var projectionMat = new Mat();
        Cv2.Reduce(
            goldMask,
            projectionMat,
            ReduceDimension.Row,
            ReduceTypes.Sum,
            MatType.CV_32SC1.Value);
        var rawProjection = new double[goldMask.Width];
        for (var x = 0; x < goldMask.Width; x++)
        {
            rawProjection[x] = projectionMat.At<int>(0, x) / 255d;
        }

        var projection = new double[rawProjection.Length];
        for (var x = 0; x < rawProjection.Length; x++)
        {
            var start = Math.Max(0, x - 2);
            var end = Math.Min(rawProjection.Length - 1, x + 2);
            var sum = 0d;
            for (var sample = start; sample <= end; sample++)
            {
                sum += rawProjection[sample];
            }

            projection[x] = sum / (end - start + 1);
        }

        var minimumPeak = Math.Max(2.4, goldMask.Height * 0.10);
        var minimumDistance = Math.Max(7, goldMask.Width / 10);
        var peaks = Enumerable.Range(1, Math.Max(0, projection.Length - 2))
            .Where(x => projection[x] >= minimumPeak &&
                        projection[x] >= projection[x - 1] &&
                        projection[x] >= projection[x + 1])
            .OrderByDescending(x => projection[x])
            .ThenBy(x => x)
            .Aggregate(
                new List<int>(),
                (selected, candidate) =>
                {
                    if (selected.All(existing =>
                            Math.Abs(existing - candidate) >= minimumDistance))
                    {
                        selected.Add(candidate);
                    }

                    return selected;
                })
            .Take(3)
            .OrderBy(x => x)
            .ToArray();

        if (peaks.Length is < 1 or > 3)
        {
            return (null, 0);
        }

        var weakestPeak = peaks.Min(x => projection[x]);
        var confidence = Math.Clamp(
            0.55 + (weakestPeak - minimumPeak) /
            Math.Max(1, goldMask.Height) * 0.9,
            0.55,
            0.96);
        return (peaks.Length, confidence);
    }

    public void Dispose()
    {
        foreach (var template in _templates.Values)
        {
            template.Dispose();
        }

        _templates.Clear();
        foreach (var template in _scaledTemplates.Values)
        {
            template.Dispose();
        }

        _scaledTemplates.Clear();
        _descriptors.Clear();
    }

    private IReadOnlyList<CharacterCardTemplateDefinition> Shortlist(
        Mat slotImage,
        IReadOnlyList<CharacterCardTemplateDefinition> templates)
    {
        if (templates.Count <= _candidateLimit)
        {
            return templates;
        }

        var query = CreateCompactDescriptor(slotImage);
        return templates
            .Select(template => (
                Template: template,
                Similarity: DescriptorSimilarity(
                    query,
                    _descriptors.GetOrAdd(
                        template.File,
                        _ => CreateCompactDescriptor(
                            LoadTemplate(template.File))))))
            .OrderByDescending(item => item.Similarity)
            .ThenBy(item => item.Template.CharacterId, StringComparer.Ordinal)
            .Take(_candidateLimit)
            .Select(item => item.Template)
            .ToArray();
    }

    private (CharacterCardTemplateDefinition Definition, double Confidence)[]
        Rank(
            Mat searchImage,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            CharacterCardRecognitionOptions options) =>
        templates
            .Select(template => (
                Definition: template,
                Confidence: Match(
                    searchImage,
                    LoadTemplate(template.File, options))))
            .OrderByDescending(item => item.Confidence)
            .Take(2)
            .ToArray();

    private static bool IsDecisive(
        IReadOnlyList<(
            CharacterCardTemplateDefinition Definition,
            double Confidence)> ranked) =>
        ranked.Count > 0 &&
        ranked[0].Confidence >= MinimumCharacterConfidence &&
        ranked[0].Confidence - (ranked.Count > 1
            ? ranked[1].Confidence
            : 0) >= MinimumLeadOverRunnerUp;

    private static float[] CreateCompactDescriptor(Mat source)
    {
        const int side = 24;
        using var reduced = new Mat();
        Cv2.Resize(
            source,
            reduced,
            new Size(side, side),
            interpolation: InterpolationFlags.Area);
        var pixels = new byte[checked(side * side * reduced.Channels())];
        Marshal.Copy(reduced.Data, pixels, 0, pixels.Length);
        var mean = pixels.Average(value => (double)value);
        var descriptor = new float[pixels.Length];
        var squaredNorm = 0d;
        for (var index = 0; index < pixels.Length; index++)
        {
            var centered = pixels[index] - mean;
            descriptor[index] = (float)centered;
            squaredNorm += centered * centered;
        }

        if (squaredNorm <= double.Epsilon)
        {
            return descriptor;
        }

        var inverseNorm = 1d / Math.Sqrt(squaredNorm);
        for (var index = 0; index < descriptor.Length; index++)
        {
            descriptor[index] = (float)(descriptor[index] * inverseNorm);
        }

        return descriptor;
    }

    private static double DescriptorSimilarity(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        var score = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            score += left[index] * right[index];
        }

        return score;
    }

    private Mat LoadTemplate(string path) =>
        _templates.GetOrAdd(
            path,
            static file =>
            {
                var bytes = File.ReadAllBytes(file);
                var template = Cv2.ImDecode(bytes, ImreadModes.Color);
                if (template.Empty())
                {
                    template.Dispose();
                    throw new InvalidDataException(
                        $"角色卡牌模板无法读取：{file}");
                }

                if (template.Width != StandardTemplateWidth ||
                    template.Height != StandardTemplateHeight)
                {
                    var normalized = new Mat();
                    Cv2.Resize(
                        template,
                        normalized,
                        new Size(
                            StandardTemplateWidth,
                            StandardTemplateHeight),
                        interpolation: InterpolationFlags.Area);
                    template.Dispose();
                    return normalized;
                }

                return template;
            });

    private Mat LoadTemplate(
        string path,
        CharacterCardRecognitionOptions options)
    {
        if (Math.Abs(options.HorizontalTemplateScale - 1) < 0.001 &&
            Math.Abs(options.VerticalTemplateScale - 1) < 0.001)
        {
            return LoadTemplate(path);
        }

        if (options.HorizontalTemplateScale is < 0.5 or > 1.5 ||
            options.VerticalTemplateScale is < 0.5 or > 1.5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Character-card template scale must stay within a safe range.");
        }

        var horizontalScalePermille = (int)Math.Round(
            options.HorizontalTemplateScale * 1000);
        var verticalScalePermille = (int)Math.Round(
            options.VerticalTemplateScale * 1000);
        return _scaledTemplates.GetOrAdd(
            (path, horizontalScalePermille, verticalScalePermille),
            key =>
            {
                var source = LoadTemplate(key.File);
                var scaled = new Mat();
                Cv2.Resize(
                    source,
                    scaled,
                    new Size(
                        Math.Max(1, (int)Math.Round(
                            StandardTemplateWidth *
                            key.HorizontalScalePermille / 1000d)),
                        Math.Max(1, (int)Math.Round(
                            StandardTemplateHeight *
                            key.VerticalScalePermille / 1000d))),
                    interpolation: InterpolationFlags.Area);
                return scaled;
            });
    }

    private static double Match(Mat search, Mat template)
    {
        if (template.Width > search.Width ||
            template.Height > search.Height)
        {
            return 0;
        }

        using var scores = new Mat();
        Cv2.MatchTemplate(
            search,
            template,
            scores,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(
            scores,
            out _,
            out var maximum,
            out _,
            out var maximumLocation);

        var detailBounds = new Rect(
            5,
            5,
            Math.Max(1, template.Width - 25),
            Math.Max(1, template.Height - 15));
        using var templateDetail = new Mat(template, detailBounds);
        using var searchDetail = new Mat(
            search,
            new Rect(
                maximumLocation.X + detailBounds.X,
                maximumLocation.Y + detailBounds.Y,
                detailBounds.Width,
                detailBounds.Height));
        using var detailScore = new Mat();
        Cv2.MatchTemplate(
            searchDetail,
            templateDetail,
            detailScore,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(detailScore, out _, out var detailMaximum, out _, out _);
        using var alignedSearch = new Mat(
            search,
            new Rect(
                maximumLocation.X,
                maximumLocation.Y,
                template.Width,
                template.Height));
        using var absoluteDifference = new Mat();
        Cv2.Absdiff(alignedSearch, template, absoluteDifference);
        var meanDifference = Cv2.Mean(absoluteDifference);
        var normalizedMeanDifference =
            (meanDifference.Val0 +
             meanDifference.Val1 +
             meanDifference.Val2) /
            (3d * byte.MaxValue);
        var absoluteColorSimilarity = 1 - normalizedMeanDifference;
        var colorMismatchPenalty = ColorMismatchPenaltyWeight *
            Math.Max(0, ColorSimilarityGrace - absoluteColorSimilarity);
        return maximum * 0.55 + detailMaximum * 0.45 -
               colorMismatchPenalty;
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

    private static CharacterCardSlotRecognition Uncertain(
        int slotIndex,
        PixelRect slot,
        double visualStandardDeviation) =>
        new(
            slotIndex,
            slot,
            CharacterCardSlotState.Uncertain,
            null,
            null,
            0,
            0,
            visualStandardDeviation);
}
