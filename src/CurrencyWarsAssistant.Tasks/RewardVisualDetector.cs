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
        var points = new List<PixelPoint>();
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
        points.AddRange(circles
            .Select(circle => new PixelPoint(
                MineRegion.X + (int)Math.Round(circle.Center.X),
                MineRegion.Y + (int)Math.Round(circle.Center.Y)))
            .Where(point =>
                HasMineLikeColour(
                    normalized,
                    point)));

        // 1.2.108（用户实弹目击"非常大的金色球"漏开=历次"金矿没开"根因）：普通检测
        // 三重致盲——半径上限 42 装不下大金球、色彩门只认蓝/中性、固定矿区太窄。
        // 第二趟：扩区+大半径 Hough+HSV 金盘占比校验。
        points.AddRange(FindLargeGoldBalls(normalized));

        return points.Distinct().ToArray();
    }

    /// <summary>大金矿球检测（1.2.108）：1920 基准扩区 (1150,100,760,660)——覆盖普通
    /// 矿区及周边、排除顶栏与右侧羁绊栏；半径 48-130；圆盘内金色像素占比 ≥45% 才认定
    /// （防误检：羁绊金图标/金币 UI 均小且不满足大圆盘）。命中即与普通矿球同路径点击。</summary>
    private static IEnumerable<PixelPoint> FindLargeGoldBalls(Mat normalized)
    {
        var region = new Rect(1150, 100, 760, 660);
        using var roi = new Mat(normalized, region);
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(9, 9), 2.0);
        var circles = Cv2.HoughCircles(
            gray,
            HoughModes.Gradient,
            1.5,
            60,
            100,
            28,
            48,
            130);
        if (circles.Length == 0)
        {
            yield break;
        }

        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        using var goldMask = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(10, 90, 140),
            new Scalar(35, 255, 255),
            goldMask);
        foreach (var circle in circles)
        {
            var radius = Math.Max(1, (int)Math.Round(circle.Radius));
            var cx = (int)Math.Round(circle.Center.X);
            var cy = (int)Math.Round(circle.Center.Y);
            var x0 = Math.Max(0, cx - radius);
            var y0 = Math.Max(0, cy - radius);
            var width = Math.Min(roi.Width - x0, radius * 2);
            var height = Math.Min(roi.Height - y0, radius * 2);
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            using var disk = new Mat(goldMask, new Rect(x0, y0, width, height));
            if (Cv2.CountNonZero(disk) < 0.45 * width * height)
            {
                continue;
            }

            yield return new PixelPoint(region.X + cx, region.Y + cy);
        }
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
