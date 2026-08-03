using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

public sealed class RewardVisualDetector
{
    private static readonly PixelRect MineRegion =
        new(1270, 165, 390, 390);
    private static readonly PixelRect AutoBattleIconRegion =
        new(1740, 32, 40, 30);
    private const int AutoBattleEnabledGoldPixelThreshold = 150;
    private const int AutoBattleEnabledConnectedPixelThreshold = 100;
    private const int AutoBattleMaximumIconGoldPixels = 600;
    private const int AutoBattleDisabledNeutralPixelThreshold = 70;

    public IReadOnlyList<PixelPoint> FindMineBalls(CaptureFrame frame)
    {
        using var normalized = Normalize(frame);
        using var roi = new Mat(
            normalized,
            new Rect(
                MineRegion.X,
                MineRegion.Y,
                MineRegion.Width,
                MineRegion.Height));
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(7, 7), 1.6);
        var circles = Cv2.HoughCircles(
            gray,
            HoughModes.Gradient,
            1.2,
            34,
            110,
            24,
            12,
            42);
        return circles
            .Select(circle => new PixelPoint(
                MineRegion.X + (int)Math.Round(circle.Center.X),
                MineRegion.Y + (int)Math.Round(circle.Center.Y)))
            .Where(point =>
                HasMineLikeColour(
                    normalized,
                    point))
            .Distinct()
            .ToArray();
    }

    public AutoBattleVisualReading ReadAutoBattleState(CaptureFrame frame)
    {
        using var normalized = Normalize(frame);
        using var roi = new Mat(
            normalized,
            new Rect(
                AutoBattleIconRegion.X,
                AutoBattleIconRegion.Y,
                AutoBattleIconRegion.Width,
                AutoBattleIconRegion.Height));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        using var enabledGoldMask = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(14, 105, 150),
            new Scalar(32, 255, 255),
            enabledGoldMask);
        var goldPixels = Cv2.CountNonZero(enabledGoldMask);
        var largestGoldComponent = LargestConnectedComponent(enabledGoldMask);
        using var disabledNeutralMask = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(0, 0, 105),
            new Scalar(179, 70, 245),
            disabledNeutralMask);
        var neutralPixels = Cv2.CountNonZero(disabledNeutralMask);
        var state = goldPixels >= AutoBattleEnabledGoldPixelThreshold &&
                    goldPixels <= AutoBattleMaximumIconGoldPixels &&
                    largestGoldComponent >=
                        AutoBattleEnabledConnectedPixelThreshold
            ? AutoBattleVisualState.Enabled
            : neutralPixels >= AutoBattleDisabledNeutralPixelThreshold
                ? AutoBattleVisualState.Disabled
                : AutoBattleVisualState.Unknown;
        var confidence = state switch
        {
            AutoBattleVisualState.Enabled => Math.Min(
                1d,
                Math.Min(
                    goldPixels /
                    (double)AutoBattleEnabledGoldPixelThreshold,
                    largestGoldComponent /
                    (double)AutoBattleEnabledConnectedPixelThreshold)),
            AutoBattleVisualState.Disabled => Math.Min(
                1d,
                neutralPixels /
                (double)AutoBattleDisabledNeutralPixelThreshold),
            _ => 0d
        };
        return new AutoBattleVisualReading(
            state,
            goldPixels,
            neutralPixels,
            largestGoldComponent,
            AutoBattleEnabledGoldPixelThreshold,
            confidence);
    }

    public bool IsAutoBattleEnabled(CaptureFrame frame) =>
        ReadAutoBattleState(frame).State == AutoBattleVisualState.Enabled;

    private static int LargestConnectedComponent(Mat mask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var components = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S);
        var largest = 0;
        for (var label = 1; label < components; label++)
        {
            largest = Math.Max(
                largest,
                stats.At<int>(label, (int)ConnectedComponentsTypes.Area));
        }

        return largest;
    }

    private static bool HasMineLikeColour(Mat image, PixelPoint center)
    {
        var radius = 10;
        var rect = new Rect(
            Math.Max(0, center.X - radius),
            Math.Max(0, center.Y - radius),
            Math.Min(radius * 2, image.Width - Math.Max(0, center.X - radius)),
            Math.Min(radius * 2, image.Height - Math.Max(0, center.Y - radius)));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        using var sample = new Mat(image, rect);
        var mean = Cv2.Mean(sample);
        var blueDominant =
            mean.Val0 >= 105 &&
            mean.Val0 >= mean.Val1 * 1.05 &&
            mean.Val0 >= mean.Val2 * 1.10;
        var brightNeutral =
            mean.Val0 >= 110 &&
            Math.Abs(mean.Val0 - mean.Val1) <= 35 &&
            Math.Abs(mean.Val1 - mean.Val2) <= 35;
        return blueDominant || brightNeutral;
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
        if (frame.Width == 1920 && frame.Height == 1080)
        {
            return bgr;
        }

        var normalized = new Mat();
        Cv2.Resize(
            bgr,
            normalized,
            new Size(1920, 1080),
            interpolation: InterpolationFlags.Area);
        bgr.Dispose();
        return normalized;
    }
}

public readonly record struct AutoBattleVisualReading(
    AutoBattleVisualState State,
    int GoldPixels,
    int NeutralPixels,
    int LargestGoldComponent,
    int RequiredGoldPixels,
    double Confidence);
