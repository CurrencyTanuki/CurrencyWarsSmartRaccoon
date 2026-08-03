using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public sealed class TemplateDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string File { get; init; }
    public NormalizedRect SearchRegion { get; init; } = new(0, 0, 1, 1);
    public double Threshold { get; init; } = 0.9;
    public bool Grayscale { get; init; }
    public bool EdgeDetection { get; init; }

    /// <summary>
    /// 是否使用遮罩匹配：模板背景涂 MaskColor（默认纯绿 0,255,0），
    /// 匹配时背景不参与计算，只识别图标本身——背景颜色变化不影响分数。
    /// 移植自 BetterGI（babalae/better-genshin-impact）的 UseMask 方案。
    /// </summary>
    public bool UseMask { get; init; }

    /// <summary>
    /// 遮罩背景色的 ARGB 值（UseMask=true 时生效）。默认 0xFF00FF00（纯绿）。
    /// 模板中该颜色的像素视为背景，匹配时不参与计算。
    /// </summary>
    public uint MaskColorArgb { get; init; } = 0xFF00FF00;

    public System.Drawing.Color MaskColor => System.Drawing.Color.FromArgb(
        (int)(MaskColorArgb & 0xFF),
        (int)((MaskColorArgb >> 8) & 0xFF),
        (int)((MaskColorArgb >> 16) & 0xFF));
}

public sealed record TemplateMatchResult(
    string Id,
    string DisplayName,
    double Confidence,
    PixelRect ClientBounds);

public interface ITemplateMatcher
{
    TemplateMatchResult? Find(CaptureFrame frame, TemplateDefinition definition);
    TemplateMatchResult? Probe(CaptureFrame frame, TemplateDefinition definition);
}

public interface IBatchTemplateMatcher : ITemplateMatcher
{
    IReadOnlyList<TemplateMatchResult?> ProbeMany(
        CaptureFrame frame,
        IReadOnlyList<TemplateDefinition> definitions);
}

public sealed class OpenCvTemplateMatcher : IBatchTemplateMatcher, IDisposable
{
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;
    public const double SupportedAspectRatioTolerance = 0.01;

    public TemplateMatchResult? Find(CaptureFrame frame, TemplateDefinition definition)
    {
        var result = Probe(frame, definition);
        return result is not null && result.Confidence >= definition.Threshold
            ? result
            : null;
    }

    private readonly ConcurrentDictionary<string, Lazy<Mat>> templateCache =
        new(StringComparer.OrdinalIgnoreCase);
    private int disposed;

    /// <summary>
    /// Decodes and prepares page anchors before the first captured frame is
    /// classified. This moves one-time file and OpenCV initialization work out
    /// of the realtime recognition budget without changing match thresholds.
    /// </summary>
    public Task WarmUpAsync(
        IReadOnlyList<TemplateDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        return Task.Run(
            () => Parallel.ForEach(
                definitions
                    .GroupBy(
                        item =>
                            $"{item.File}|g={item.Grayscale}|e={item.EdgeDetection}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4
                },
                definition =>
                {
                    if (File.Exists(definition.File))
                    {
                        _ = GetPreparedTemplate(definition);
                    }
                }),
            cancellationToken);
    }

    public TemplateMatchResult? Probe(
        CaptureFrame frame,
        TemplateDefinition definition) => ProbeMany(frame, [definition])[0];

    public IReadOnlyList<TemplateMatchResult?> ProbeMany(
        CaptureFrame frame,
        IReadOnlyList<TemplateDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definitions);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);
        if (definitions.Count == 0)
        {
            return [];
        }

        foreach (var definition in definitions)
        {
            if (!Path.IsPathFullyQualified(definition.File))
            {
                throw new ArgumentException(
                    $"模板路径必须是绝对路径：{definition.File}",
                    nameof(definitions));
            }
        }

        if (!HasSupportedAspectRatio(frame.Width, frame.Height))
        {
            return Enumerable.Repeat<TemplateMatchResult?>(null, definitions.Count)
                .ToArray();
        }

        using var bgra = Mat.FromPixelData(
            frame.Height,
            frame.Width,
            MatType.CV_8UC4,
            frame.BgraPixels,
            frame.Stride);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        using var normalized = new Mat();
        if (frame.Width == ReferenceWidth && frame.Height == ReferenceHeight)
        {
            bgr.CopyTo(normalized);
        }
        else
        {
            Cv2.Resize(
                bgr,
                normalized,
                new Size(ReferenceWidth, ReferenceHeight),
                interpolation: InterpolationFlags.Area);
        }

        using var grayscaleNormalized = new Mat();
        using var edgeNormalized = new Mat();
        var needsGrayscale = definitions.Any(item =>
            item.Grayscale || item.EdgeDetection);
        var needsEdges = definitions.Any(item => item.EdgeDetection);
        if (needsGrayscale)
        {
            Cv2.CvtColor(
                normalized,
                grayscaleNormalized,
                ColorConversionCodes.BGR2GRAY);
        }
        if (needsEdges)
        {
            Cv2.Canny(grayscaleNormalized, edgeNormalized, 80, 180);
        }

        var results = new TemplateMatchResult?[definitions.Count];
        for (var index = 0; index < definitions.Count; index++)
        {
            results[index] = ProbeNormalized(
                frame,
                normalized,
                grayscaleNormalized,
                edgeNormalized,
                definitions[index]);
        }

        return results;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var item in templateCache.Values)
        {
            if (item.IsValueCreated)
            {
                item.Value.Dispose();
            }
        }
        templateCache.Clear();
    }

    private TemplateMatchResult? ProbeNormalized(
        CaptureFrame frame,
        Mat normalized,
        Mat grayscaleNormalized,
        Mat edgeNormalized,
        TemplateDefinition definition)
    {
        if (!File.Exists(definition.File))
        {
            return null;
        }

        var search = definition.SearchRegion.ToPixels(
            ReferenceWidth,
            ReferenceHeight);
        if (search.IsEmpty)
        {
            return null;
        }

        var preparedTemplate = GetPreparedTemplate(definition);
        if (preparedTemplate.Empty())
        {
            return null;
        }

        var source = definition.EdgeDetection
            ? edgeNormalized
            : definition.Grayscale
                ? grayscaleNormalized
                : normalized;
        using var searchImage = new Mat(
            source,
            new Rect(search.X, search.Y, search.Width, search.Height));
        if (preparedTemplate.Width > searchImage.Width ||
            preparedTemplate.Height > searchImage.Height)
        {
            return null;
        }

        using var scores = new Mat();
        if (definition.UseMask)
        {
            // 遮罩匹配（BetterGI 方案）：模板中 MaskColor 背景不参与计算，
            // 只匹配图标本身——背景颜色变化不影响分数。
            using var mask = CreateMask(preparedTemplate, definition.MaskColor);
            Cv2.MatchTemplate(
                searchImage,
                preparedTemplate,
                scores,
                TemplateMatchModes.CCorrNormed,
                mask);
        }
        else
        {
            Cv2.MatchTemplate(
                searchImage,
                preparedTemplate,
                scores,
                TemplateMatchModes.CCoeffNormed);
        }

        Cv2.MinMaxLoc(scores, out _, out var maxValue, out _, out var maxLocation);
        return new TemplateMatchResult(
            definition.Id,
            definition.DisplayName,
            maxValue,
            MapToSourceFrame(
                new PixelRect(
                    search.X + maxLocation.X,
                    search.Y + maxLocation.Y,
                    preparedTemplate.Width,
                    preparedTemplate.Height),
                frame.Width,
                frame.Height));
    }

    private Mat GetPreparedTemplate(TemplateDefinition definition)
    {
        var key =
            $"{definition.File}|g={definition.Grayscale}|e={definition.EdgeDetection}" +
            $"|m={definition.UseMask}|mc={definition.MaskColorArgb:X8}";
        return templateCache.GetOrAdd(
            key,
            _ => new Lazy<Mat>(
                () => LoadPreparedTemplate(definition),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static Mat LoadPreparedTemplate(TemplateDefinition definition)
    {
        using var color = Cv2.ImRead(definition.File, ImreadModes.Color);
        if (color.Empty())
        {
            return new Mat();
        }

        if (!definition.Grayscale && !definition.EdgeDetection)
        {
            return color.Clone();
        }

        using var grayscale = new Mat();
        Cv2.CvtColor(color, grayscale, ColorConversionCodes.BGR2GRAY);
        if (!definition.EdgeDetection)
        {
            return grayscale.Clone();
        }

        var edges = new Mat();
        Cv2.Canny(grayscale, edges, 80, 180);
        return edges;
    }

    /// <summary>
    /// 生成遮罩（BetterGI 方案）：模板中背景色（默认纯绿 0,255,0）置 0，
    /// 其余（图标）置 255。匹配时背景不参与计算。
    /// </summary>
    private static Mat CreateMask(Mat template, System.Drawing.Color maskColor)
    {
        var mask = new Mat();
        var scalar = new Scalar(maskColor.B, maskColor.G, maskColor.R);
        Cv2.InRange(template, scalar, scalar, mask);
        Cv2.BitwiseNot(mask, mask);
        return mask;
    }

    public static bool HasSupportedAspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var actual = (double)width / height;
        var expected = (double)ReferenceWidth / ReferenceHeight;
        return Math.Abs(actual - expected) <= SupportedAspectRatioTolerance;
    }

    private static PixelRect MapToSourceFrame(
        PixelRect referenceBounds,
        int sourceWidth,
        int sourceHeight)
    {
        var scaleX = (double)sourceWidth / ReferenceWidth;
        var scaleY = (double)sourceHeight / ReferenceHeight;
        var left = (int)Math.Round(referenceBounds.X * scaleX);
        var top = (int)Math.Round(referenceBounds.Y * scaleY);
        var right = (int)Math.Round(referenceBounds.Right * scaleX);
        var bottom = (int)Math.Round(referenceBounds.Bottom * scaleY);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
