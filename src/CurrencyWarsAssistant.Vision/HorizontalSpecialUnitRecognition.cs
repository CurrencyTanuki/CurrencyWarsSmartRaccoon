using System.IO;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Vision;

public sealed record HorizontalSpecialUnitRecognition(
    string SpecialUnitId,
    string DisplayName,
    PixelRect ReferenceBounds,
    double Confidence);

/// <summary>
/// Recognizes horizontal special-unit panels independently from the ordinary
/// front, back, and bench character-card recognizer.
/// </summary>
public interface IHorizontalSpecialUnitRecognizer
{
    IReadOnlyList<HorizontalSpecialUnitRecognition> Recognize(CaptureFrame frame);
}

/// <summary>
/// Recognizes the fixed horizontal Peipei panel shown to the left of the
/// front-row cards. The identity template is combined with a local panel-color
/// gate so the small artwork cannot match an unrelated card portrait.
/// </summary>
public sealed class OpenCvHorizontalSpecialUnitRecognizer :
    IHorizontalSpecialUnitRecognizer,
    IDisposable
{
    public const string PeipeiId = "special_unit_peipei";
    public const string PeipeiDisplayName = "佩佩";

    private const double MinimumTemplateConfidence = 0.75;
    private const double MinimumPanelPinkRatio = 0.20;
    private static readonly PixelRect PeipeiPanelReferenceBounds =
        new(340, 295, 240, 90);
    private static readonly NormalizedRect PeipeiTemplateSearchRegion = new(
        390d / OpenCvTemplateMatcher.ReferenceWidth,
        305d / OpenCvTemplateMatcher.ReferenceHeight,
        90d / OpenCvTemplateMatcher.ReferenceWidth,
        55d / OpenCvTemplateMatcher.ReferenceHeight);

    private readonly OpenCvTemplateMatcher _templateMatcher = new();
    private readonly TemplateDefinition _peipeiTemplate;

    public OpenCvHorizontalSpecialUnitRecognizer(string peipeiTemplateFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peipeiTemplateFile);
        var fullPath = Path.GetFullPath(peipeiTemplateFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "横向佩佩特殊单位模板不存在。",
                fullPath);
        }

        _peipeiTemplate = new TemplateDefinition
        {
            Id = PeipeiId,
            DisplayName = PeipeiDisplayName,
            File = fullPath,
            SearchRegion = PeipeiTemplateSearchRegion,
            Threshold = MinimumTemplateConfidence
        };
    }

    public IReadOnlyList<HorizontalSpecialUnitRecognition> Recognize(
        CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!OpenCvTemplateMatcher.HasSupportedAspectRatio(
                frame.Width,
                frame.Height))
        {
            return [];
        }

        var match = _templateMatcher.Probe(frame, _peipeiTemplate);
        if (match is null ||
            match.Confidence < MinimumTemplateConfidence ||
            MeasurePanelPinkRatio(frame) < MinimumPanelPinkRatio)
        {
            return [];
        }

        return
        [
            new HorizontalSpecialUnitRecognition(
                PeipeiId,
                PeipeiDisplayName,
                PeipeiPanelReferenceBounds,
                match.Confidence)
        ];
    }

    public void Dispose() => _templateMatcher.Dispose();

    private static double MeasurePanelPinkRatio(CaptureFrame frame)
    {
        var region = MapReferenceRectToFrame(
            PeipeiPanelReferenceBounds,
            frame.Width,
            frame.Height);
        if (region.IsEmpty)
        {
            return 0;
        }

        var pinkPixels = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var rowOffset = y * frame.Stride;
            for (var x = region.X; x < region.Right; x++)
            {
                var offset = rowOffset + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                if (red > 110 &&
                    red * 100 > green * 120 &&
                    red * 100 > blue * 105)
                {
                    pinkPixels++;
                }
            }
        }

        return pinkPixels / (double)(region.Width * region.Height);
    }

    private static PixelRect MapReferenceRectToFrame(
        PixelRect referenceBounds,
        int frameWidth,
        int frameHeight)
    {
        var scaleX = frameWidth / (double)OpenCvTemplateMatcher.ReferenceWidth;
        var scaleY = frameHeight / (double)OpenCvTemplateMatcher.ReferenceHeight;
        var left = (int)Math.Round(referenceBounds.X * scaleX);
        var top = (int)Math.Round(referenceBounds.Y * scaleY);
        var right = (int)Math.Round(referenceBounds.Right * scaleX);
        var bottom = (int)Math.Round(referenceBounds.Bottom * scaleY);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
