using OpenCvSharp;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 用用户手标四角做「正交投影（透视矫正）」拉正的几何工具（2026-08-21）。
/// 每张卡用其 4 斜角构建单应拉成正面矩形，再按序排进 16:9 画布供识别。
/// 前台(4卡)与后台(每档 count 卡)共用此方法；坐标取 CalibrationSlots。
/// </summary>
internal static class CalibratedPerspective
{
    /// <summary>
    /// 把一张卡的斜四角拉正成指定目标宽高的正面矩形。
    /// quad = 4 角 [[TLx,TLy],[TRx,TRy],[BLx,BLy],[BRx,BRy]]（帧坐标系）。
    /// 传入 sourceReuse 可复用源 Mat（避免每卡重复复制整帧）。
    /// </summary>
    public static Mat WarpOneQuad(
        byte[]? sourceBgra, int srcW, int srcH, int[][] quad, int dstW, int dstH, Mat? sourceReuse = null)
    {
        if (sourceBgra is null || quad is not { Length: 4 })
        {
            throw new ArgumentException("source/quad 无效");
        }
        // quad 是 1920×1080 参考系四角；单应源点换算到帧坐标。
        var fsx = srcW / 1920d;
        var fsy = srcH / 1080d;
        var src = new Point2f[]
        {
            new((float)(quad[0][0] * fsx), (float)(quad[0][1] * fsy)),
            new((float)(quad[1][0] * fsx), (float)(quad[1][1] * fsy)),
            new((float)(quad[2][0] * fsx), (float)(quad[2][1] * fsy)),
            new((float)(quad[3][0] * fsx), (float)(quad[3][1] * fsy))
        };
        var dst = new Point2f[]
        {
            new(0, 0),
            new(dstW, 0),
            new(0, dstH),
            new(dstW, dstH)
        };
        using var homography = Cv2.GetPerspectiveTransform(src, dst);
        var source = sourceReuse;
        var ownsSource = source is null;
        if (source is null)
        {
            source = new Mat(srcH, srcW, MatType.CV_8UC4);
            System.Runtime.InteropServices.Marshal.Copy(sourceBgra, 0, source.Data, sourceBgra.Length);
        }
        try
        {
            var outMat = new Mat();
            Cv2.WarpPerspective(
                source, outMat, homography, new OpenCvSharp.Size(dstW, dstH),
                InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            return outMat;
        }
        finally
        {
            if (ownsSource)
            {
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// 把一排卡的斜四角（帧坐标）各自拉成正矩形，按序水平排进 16:9 画布。
    /// 返回：画布 captureFrame、每张卡在画布内轴对齐矩形（长度 = quads 数）。
    /// 注意：每张卡拉正到「它自身四角的 AABB 尺寸」，保持原始正视比例，
    /// 不做统一缩放的模板失配（不同卡/槽实机尺寸不一，统一尺寸会造成 conf 掉到
    /// 阈值下 → unknown 回归）。cardW 仅作画布内槽位间距基准，gap = 卡间距。
    /// </summary>
    public static (CaptureFrame Frame, IReadOnlyList<PixelRect> Rects)
        WarpRow(CaptureFrame frame, IReadOnlyList<int[][]> quads, int cardW, int cardH, int gap = 0)
    {
        var canvasW = 1920;
        var canvasH = 1080;
        var n = quads.Count;
        var rects = new List<PixelRect>(n);
        using var canvas = new Mat(canvasH, canvasW, MatType.CV_8UC4, Scalar.All(0));
        using (var source = new Mat(
            frame.Height, frame.Width, MatType.CV_8UC4))
        {
            System.Runtime.InteropServices.Marshal.Copy(
                frame.BgraPixels, 0, source.Data, frame.BgraPixels.Length);
            var totalW = n * (cardW + gap) - (n > 0 ? gap : 0);
            var curX = Math.Max(0, (canvasW - totalW) / 2);
            for (var i = 0; i < n; i++)
            {
                using var card = WarpOneQuad(
                    frame.BgraPixels, frame.Width, frame.Height, quads[i], cardW, cardH, source);
                if (card is null) continue;
                var dy = (canvasH - cardH) / 2;
                using var roi = canvas[dy, dy + cardH, curX, curX + cardW];
                card.CopyTo(roi);
                rects.Add(new PixelRect(curX, dy, cardW, cardH));
                curX += cardW + gap;
            }
        }

        var pixels = new byte[checked(canvasW * 4 * canvasH)];
        for (var row = 0; row < canvasH; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(canvas.Ptr(row), pixels, row * canvasW * 4, canvasW * 4);
        }
        var warpedFrame = new CaptureFrame(
            canvasW, canvasH, canvasW * 4, pixels,
            new PixelRect(0, 0, canvasW, canvasH), frame.CapturedAt);
        return (warpedFrame, rects);
    }
}
