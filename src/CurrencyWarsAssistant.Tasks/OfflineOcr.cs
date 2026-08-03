using System.IO;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CurrencyWarsAssistant.Tasks;

public sealed record OcrTextResult(string Text, IReadOnlyList<string> Lines)
{
    public double? Confidence { get; init; }

    public string? Provider { get; init; }
}

public interface IOfflineOcr
{
    bool IsAvailable { get; }

    ValueTask<OcrTextResult> RecognizeAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken);
}

public interface IAdaptiveOfflineOcr : IOfflineOcr
{
    ValueTask<OcrTextResult> RecognizeRobustAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken);
}

public enum OfflineOcrRecognitionMode
{
    Robust,
    Fast
}

/// <summary>
/// Uses the Simplified Chinese OCR model installed with Windows. No image or
/// recognition data leaves the computer.
/// </summary>
public sealed class WindowsOfflineOcr : IAdaptiveOfflineOcr
{
    private const int MaximumInputDimension = 2400;
    private readonly Lazy<OcrEngine?>[] engines;
    private readonly ConcurrentQueue<int> availableEngineLanes = new();
    private readonly SemaphoreSlim recognitionLanes;
    private readonly OfflineOcrRecognitionMode recognitionMode;

    public WindowsOfflineOcr(
        string languageTag = "zh-Hans",
        OfflineOcrRecognitionMode recognitionMode =
            OfflineOcrRecognitionMode.Robust,
        int maximumConcurrency = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageTag);
        if (maximumConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        this.recognitionMode = recognitionMode;
        engines = Enumerable.Range(0, maximumConcurrency)
            .Select(_ => new Lazy<OcrEngine?>(
                () => CreateEngine(languageTag),
                LazyThreadSafetyMode.ExecutionAndPublication))
            .ToArray();
        recognitionLanes = new SemaphoreSlim(
            maximumConcurrency,
            maximumConcurrency);
        for (var lane = 0; lane < maximumConcurrency; lane++)
        {
            availableEngineLanes.Enqueue(lane);
        }
    }

    public bool IsAvailable => engines[0].Value is not null;

    private static OcrEngine? CreateEngine(string languageTag)
    {
        var language = new Language(languageTag);
        return OcrEngine.IsLanguageSupported(language)
            ? OcrEngine.TryCreateFromLanguage(language)
            : null;
    }

    public async ValueTask<OcrTextResult> RecognizeAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken) =>
        await RecognizeAsync(
            frame,
            region,
            recognitionMode,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<OcrTextResult> RecognizeRobustAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken) =>
        await RecognizeAsync(
            frame,
            region,
            OfflineOcrRecognitionMode.Robust,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<OcrTextResult> RecognizeAsync(
        CaptureFrame frame,
        PixelRect region,
        OfflineOcrRecognitionMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "Windows 中文 OCR 不可用。请在 Windows 语言设置中安装“中文（简体）”语言包及基本键入组件。");
        }

        var boundedRegion = Bound(region, frame.Width, frame.Height);
        if (boundedRegion.IsEmpty)
        {
            return new OcrTextResult(string.Empty, []);
        }

        var variants = EncodeRegionVariants(
            frame,
            boundedRegion,
            mode);
        var recognizedTexts = new List<string>();

        await recognitionLanes.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!availableEngineLanes.TryDequeue(out var engineLane))
        {
            recognitionLanes.Release();
            throw new InvalidOperationException("OCR engine lane accounting failed.");
        }

        try
        {
            var activeEngine = engines[engineLane].Value ??
                throw new InvalidOperationException("OCR engine is unavailable.");
            foreach (var pngBytes in variants)
            {
                var result = await RecognizePngAsync(
                        pngBytes,
                        activeEngine,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    recognizedTexts.Add(result.Text.Trim());
                }

                recognizedTexts.AddRange(
                    result.Lines
                        .Select(line => line.Text.Trim())
                        .Where(line => line.Length > 0));
            }
        }
        finally
        {
            availableEngineLanes.Enqueue(engineLane);
            recognitionLanes.Release();
        }

        var distinctTexts = recognizedTexts
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new OcrTextResult(
            distinctTexts.FirstOrDefault() ?? string.Empty,
            distinctTexts);
    }

    private async Task<OcrResult> RecognizePngAsync(
        byte[] pngBytes,
        OcrEngine activeEngine,
        CancellationToken cancellationToken)
    {
        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        randomAccessStream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder
            .CreateAsync(randomAccessStream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        using var softwareBitmap = await decoder
            .GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        return await activeEngine
            .RecognizeAsync(softwareBitmap)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<byte[]> EncodeRegionVariants(
        CaptureFrame frame,
        PixelRect region,
        OfflineOcrRecognitionMode recognitionMode)
    {
        var source = frame.ToBitmapSource();
        var cropped = new CroppedBitmap(
            source,
            new Int32Rect(region.X, region.Y, region.Width, region.Height));

        var longestDimension = Math.Max(region.Width, region.Height);
        var scale = Math.Clamp(
            MaximumInputDimension / (double)longestDimension,
            1,
            3);
        BitmapSource prepared = scale > 1
            ? new TransformedBitmap(cropped, new ScaleTransform(scale, scale))
            : cropped;
        prepared.Freeze();

        const int recognitionPadding = 28;
        var original = EncodePng(AddRecognitionPadding(
            prepared,
            recognitionPadding,
            Brushes.Black));
        if (recognitionMode == OfflineOcrRecognitionMode.Fast)
        {
            return [original];
        }

        var firstThreshold = CreateHighContrastTextBitmap(prepared, 150);
        var secondThreshold = CreateHighContrastTextBitmap(prepared, 182);
        return
        [
            original,
            EncodePng(AddRecognitionPadding(
                firstThreshold,
                recognitionPadding,
                Brushes.White)),
            EncodePng(AddRecognitionPadding(
                secondThreshold,
                recognitionPadding,
                Brushes.White))
        ];
    }

    private static BitmapSource AddRecognitionPadding(
        BitmapSource source,
        int padding,
        Brush background)
    {
        var width = checked(source.PixelWidth + padding * 2);
        var height = checked(source.PixelHeight + padding * 2);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                background,
                null,
                new Rect(0, 0, width, height));
            drawing.DrawImage(
                source,
                new Rect(
                    padding,
                    padding,
                    source.PixelWidth,
                    source.PixelHeight));
        }

        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateHighContrastTextBitmap(
        BitmapSource source,
        byte threshold)
    {
        var gray = new FormatConvertedBitmap(
            source,
            PixelFormats.Gray8,
            null,
            0);
        gray.Freeze();
        var stride = gray.PixelWidth;
        var pixels = new byte[checked(stride * gray.PixelHeight)];
        gray.CopyPixels(pixels, stride, 0);

        // The game labels are bright glyphs over a dark coloured panel.
        // Convert them to dark text on a clean white background, which both
        // Windows OCR and future OCR providers handle more consistently.
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = pixels[index] >= threshold
                ? (byte)0
                : (byte)255;
        }

        var bitmap = BitmapSource.Create(
            gray.PixelWidth,
            gray.PixelHeight,
            96,
            96,
            PixelFormats.Gray8,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static PixelRect Bound(PixelRect region, int width, int height)
    {
        var x = Math.Clamp(region.X, 0, width);
        var y = Math.Clamp(region.Y, 0, height);
        var right = Math.Clamp(region.Right, x, width);
        var bottom = Math.Clamp(region.Bottom, y, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }
}
