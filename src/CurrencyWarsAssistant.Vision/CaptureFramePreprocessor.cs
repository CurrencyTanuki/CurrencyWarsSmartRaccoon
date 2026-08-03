using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public static class CaptureFramePreprocessor
{
    public static CaptureFrame CreateMaskedCrop(
        CaptureFrame frame,
        PixelRect region,
        IReadOnlyList<PixelRect> masks)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(masks);

        var x = Math.Clamp(region.X, 0, frame.Width);
        var y = Math.Clamp(region.Y, 0, frame.Height);
        var right = Math.Clamp(region.Right, x, frame.Width);
        var bottom = Math.Clamp(region.Bottom, y, frame.Height);
        var bounded = new PixelRect(x, y, right - x, bottom - y);
        if (bounded.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        using var source = ToMat(frame);
        using var sourceCrop = new Mat(
            source,
            new Rect(bounded.X, bounded.Y, bounded.Width, bounded.Height));
        using var crop = sourceCrop.Clone();
        foreach (var mask in masks)
        {
            var maskLeft = Math.Max(mask.X, bounded.X);
            var maskTop = Math.Max(mask.Y, bounded.Y);
            var maskRight = Math.Min(mask.Right, bounded.Right);
            var maskBottom = Math.Min(mask.Bottom, bounded.Bottom);
            if (maskRight <= maskLeft || maskBottom <= maskTop)
            {
                continue;
            }

            Cv2.Rectangle(
                crop,
                new Rect(
                    maskLeft - bounded.X,
                    maskTop - bounded.Y,
                    maskRight - maskLeft,
                    maskBottom - maskTop),
                new Scalar(16, 16, 16, 255),
                thickness: -1);
        }

        return ToFrame(crop, frame.CapturedAt);
    }

    public static CaptureFrame CreateEnlargedCrop(
        CaptureFrame frame,
        PixelRect region,
        int scale = 4)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (scale is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var x = Math.Clamp(region.X, 0, frame.Width);
        var y = Math.Clamp(region.Y, 0, frame.Height);
        var right = Math.Clamp(region.Right, x, frame.Width);
        var bottom = Math.Clamp(region.Bottom, y, frame.Height);
        var bounded = new PixelRect(x, y, right - x, bottom - y);
        if (bounded.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        using var source = new Mat(
            frame.Height,
            frame.Width,
            MatType.CV_8UC4);
        Marshal.Copy(frame.BgraPixels, 0, source.Data, frame.BgraPixels.Length);
        using var crop = new Mat(
            source,
            new Rect(bounded.X, bounded.Y, bounded.Width, bounded.Height));
        using var resized = new Mat();
        Cv2.Resize(
            crop,
            resized,
            new Size(bounded.Width * scale, bounded.Height * scale),
            interpolation: InterpolationFlags.Cubic);
        var stride = checked(resized.Width * 4);
        var pixels = new byte[checked(stride * resized.Height)];
        for (var row = 0; row < resized.Height; row++)
        {
            Marshal.Copy(
                resized.Ptr(row),
                pixels,
                checked(row * stride),
                stride);
        }

        return new CaptureFrame(
            resized.Width,
            resized.Height,
            stride,
            pixels,
            new PixelRect(0, 0, resized.Width, resized.Height),
            frame.CapturedAt);
    }

    public static CaptureFrame CreateRepeatedEnlargedCrop(
        CaptureFrame frame,
        PixelRect region,
        int repetitions = 3,
        int scale = 4)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (repetitions is < 2 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(repetitions));
        }

        using var enlarged = ToMat(CreateEnlargedCrop(frame, region, scale));
        var gap = Math.Max(12, enlarged.Height / 4);
        var width = checked(enlarged.Width * repetitions + gap * (repetitions + 1));
        using var repeated = new Mat(
            enlarged.Height + gap * 2,
            width,
            MatType.CV_8UC4,
            new Scalar(16, 16, 16, 255));
        for (var index = 0; index < repetitions; index++)
        {
            using var target = new Mat(
                repeated,
                new Rect(
                    gap + index * (enlarged.Width + gap),
                    gap,
                    enlarged.Width,
                    enlarged.Height));
            enlarged.CopyTo(target);
        }

        return ToFrame(repeated, frame.CapturedAt);
    }

    private static Mat ToMat(CaptureFrame frame)
    {
        var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        Marshal.Copy(frame.BgraPixels, 0, mat.Data, frame.BgraPixels.Length);
        return mat;
    }

    private static CaptureFrame ToFrame(Mat bgra, DateTimeOffset capturedAt)
    {
        var stride = checked(bgra.Width * 4);
        var pixels = new byte[checked(stride * bgra.Height)];
        for (var row = 0; row < bgra.Height; row++)
        {
            Marshal.Copy(
                bgra.Ptr(row),
                pixels,
                checked(row * stride),
                stride);
        }

        return new CaptureFrame(
            bgra.Width,
            bgra.Height,
            stride,
            pixels,
            new PixelRect(0, 0, bgra.Width, bgra.Height),
            capturedAt);
    }
}
