using OpenCvSharp;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 后台备战席斜视平面几何（用户 2026-08-15 实机确认的模型）：
/// 卡牌是 3D 斜视平面上的 2D 面片，屏幕投影为横向平行四边形——
/// 上边中心间距 &lt; 下边中心间距，且左右槽位上下边错位（越靠边越明显）。
/// 本类按用户 2K 标定的最左槽 4 点线性插值出全部槽位的平行四边形 4 点，
/// 并提供全局单应矩阵（同平面共享一个投影变换）把后台行拉正。
/// </summary>
internal static class BackRowPerspectiveGeometry
{
    // 用户标定的最左槽 4 点（2560×1440 实机截图，2026-08-15，
    // 换算到 1920×1080 参考系 /1.333）：
    // 左上 (343,600)、右上 (466,600)、左下 (319,735)、右下 (446,735)。
    // 中间槽（索引 4）中心 = 画面中轴 960。其余槽位线性插值。
    // 用 1920 参考系（而非 2K 原始值）是刻意的：角色识别器把帧归一化到
    // 1920×1080、模板固定 111×127——warped 卡牌必须与前台卡牌同尺寸
    // （约 123×135），否则模板只覆盖卡牌一部分导致匹配分数不足。
    private const double AnchorTopLeftX = 343;
    private const double AnchorTopRightX = 466;
    private const double AnchorBottomLeftX = 319;
    private const double AnchorBottomRightX = 446;
    private const double AnchorTopY = 600;
    private const double AnchorBottomY = 735;
    private const double AnchorWidth = 1920;
    private const double AnchorHeight = 1080;

    public static double ScaleX(double frameWidth) => frameWidth / AnchorWidth;
    public static double ScaleY(double frameHeight) => frameHeight / AnchorHeight;

    /// <summary>
    /// 9 个后台槽位的平行四边形 4 点（帧坐标，顺序 TL/TR/BL/BR）。
    /// </summary>
    public static IReadOnlyList<(double X1, double Y1, double X2, double Y2,
        double X3, double Y3, double X4, double Y4)> ComputeSlotQuads(
        int frameWidth,
        int frameHeight,
        int slotCount = 9)
    {
        var sx = ScaleX(frameWidth);
        var sy = ScaleY(frameHeight);
        var topLeftX = AnchorTopLeftX * sx;
        var topRightX = AnchorTopRightX * sx;
        var bottomLeftX = AnchorBottomLeftX * sx;
        var bottomRightX = AnchorBottomRightX * sx;
        var topY = AnchorTopY * sy;
        var bottomY = AnchorBottomY * sy;

        var topCenter = (topLeftX + topRightX) / 2;
        var bottomCenter = (bottomLeftX + bottomRightX) / 2;
        var midX = frameWidth / 2d;
        // 槽间距恒定（用户 2026-08-15 确认：6/7/8/9 槽间距相同，可
        // 直接从 6 槽推到 9 槽）。间距由 9 槽标定锚点推出：
        // 9 槽中间槽 = 画面中轴 960 → dTop = (960 - 锚点中心)/4。
        // 各槽数以 960 对称铺开（6 槽最左更靠右、9 槽最左更靠左）。
        var dTop = (midX - topCenter) / 4d;
        var dBottom = (midX - bottomCenter) / 4d;
        var topHalfW = (topRightX - topLeftX) / 2;
        var bottomHalfW = (bottomRightX - bottomLeftX) / 2;

        var quads = new List<(double, double, double, double, double, double, double, double)>(slotCount);
        for (var i = 0; i < slotCount; i++)
        {
            var cTop = midX + (i - (slotCount - 1) / 2d) * dTop;
            var cBottom = midX + (i - (slotCount - 1) / 2d) * dBottom;
            quads.Add((
                cTop - topHalfW, topY,
                cTop + topHalfW, topY,
                cBottom - bottomHalfW, bottomY,
                cBottom + bottomHalfW, bottomY));
        }

        return quads;
    }

    /// <summary>
    /// 后台行全局单应：把斜视平面（9 槽整体四边形）拉正为轴对齐矩形。
    /// 源 4 点 = 最左槽左上、最右槽右上、最左槽左下、最右槽右下。
    /// </summary>
    public static Mat ComputeRowHomography(int frameWidth, int frameHeight, int slotCount = 9)
    {
        var quads = ComputeSlotQuads(frameWidth, frameHeight, slotCount);
        var first = quads[0];
        var last = quads[^1];
        var src = new Point2f[]
        {
            new((float)first.Item1, (float)first.Item2),
            new((float)last.Item3, (float)last.Item4),
            new((float)first.Item5, (float)first.Item6),
            new((float)last.Item7, (float)last.Item8)
        };
        // 目标：轴对齐矩形（宽=源四边形平均宽度，高=卡牌高）
        var dstW = (float)((last.Item3 - first.Item1) * 1.02);
        var dstH = (float)((first.Item6 - first.Item2) * 1.10);
        var dst = new Point2f[]
        {
            new(0, 0),
            new(dstW, 0),
            new(0, dstH),
            new(dstW, dstH)
        };
        return Cv2.GetPerspectiveTransform(src, dst);
    }

    /// <summary>
    /// 把后台行裁出并按全局单应拉正（斜视平面 → 正视矩形带），
    /// 返回 warped 帧与 9 个轴对齐槽位矩形（warped 坐标系）。
    /// 角色/装备/星级识别在 warped 帧上按这些矩形工作。
    /// </summary>
    public static (CaptureFrame Warped, IReadOnlyList<PixelRect> Rects)
        WarpBackRow(CaptureFrame frame, int slotCount = 9)
    {
        // 全部几何在 1920×1080 参考系完成（识别器归一化到 1920×1080、
        // 模板固定 111×127——warped 卡牌必须与前台同尺寸约 123×135）。
        // 帧坐标只用于单应源点（参考坐标 × 帧/参考比例）。
        var quadsRef = ComputeSlotQuads(1920, 1080, slotCount);
        var sx = frame.Width / 1920d;
        var sy = frame.Height / 1080d;
        var rowWidth = quadsRef[^1].Item3 - quadsRef[0].Item1;
        var rowHeight = quadsRef[0].Item6 - quadsRef[0].Item2;
        var dstW = checked((int)Math.Round(rowWidth * 1.04));
        // dst 高度必须保持源平面宽高比（卡牌原始比例）——垂直拉伸会
        // 使模板匹配分数大幅下降（实测 0.59 &lt; 阈值导致后台识别全失败）。
        var dstH = checked((int)Math.Round(dstW * rowHeight / rowWidth));

        // 单应：源 = 最左槽左上/最右槽右上/最左槽左下/最右槽右下
        //（参考坐标换算到帧坐标）
        var src = new Point2f[]
        {
            new((float)(quadsRef[0].Item1 * sx), (float)(quadsRef[0].Item2 * sy)),
            new((float)(quadsRef[^1].Item3 * sx), (float)(quadsRef[^1].Item4 * sy)),
            new((float)(quadsRef[0].Item5 * sx), (float)(quadsRef[0].Item6 * sy)),
            new((float)(quadsRef[^1].Item7 * sx), (float)(quadsRef[^1].Item8 * sy))
        };
        var dst = new Point2f[]
        {
            new(0, 0),
            new(dstW, 0),
            new(0, dstH),
            new(dstW, dstH)
        };
        using var homography = Cv2.GetPerspectiveTransform(src, dst);

        using var source = new Mat(
            frame.Height,
            frame.Width,
            MatType.CV_8UC4);
        System.Runtime.InteropServices.Marshal.Copy(
            frame.BgraPixels,
            0,
            source.Data,
            frame.BgraPixels.Length);
        using var warped = new Mat();
        Cv2.WarpPerspective(
            source,
            warped,
            homography,
            new OpenCvSharp.Size(dstW, dstH),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.All(0));

        // 角色识别器要求帧为 16:9（HasSupportedAspectRatio），否则全部
        // 槽位直接返回 Uncertain——warped 窄条 (1712×187) 会被拒绝。
        // 把 warped 行贴进 16:9 黑色画布（最小宽度 1920 参考），
        // 槽位矩形保持画布内绝对坐标。
        var canvasW = 1920;
        var canvasH = 1080;
        using var canvas = new Mat(
            canvasH,
            canvasW,
            MatType.CV_8UC4,
            Scalar.All(0));
        using var roi = canvas[0, dstH, 0, dstW];
        warped.CopyTo(roi);

        var pixels = new byte[checked(canvasW * 4 * canvasH)];
        for (var row = 0; row < canvasH; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                canvas.Ptr(row),
                pixels,
                row * canvasW * 4,
                canvasW * 4);
        }

        var warpedFrame = new CaptureFrame(
            canvasW,
            canvasH,
            canvasW * 4,
            pixels,
            new PixelRect(0, 0, canvasW, canvasH),
            frame.CapturedAt);
        return (warpedFrame, ComputeWarpedSlotRects(1920, 1080, slotCount));
    }

    /// <summary>
    /// 拉正后的后台行内 9 个槽位的轴对齐矩形（warped 坐标系，
    /// 与 WarpBackRow 的映射完全一致：x 按 warped 行宽缩放因子换算，
    /// y 覆盖整个 warped 行高——卡牌底部/星级带不被裁掉）。
    /// </summary>
    public static IReadOnlyList<PixelRect> ComputeWarpedSlotRects(
        int frameWidth,
        int frameHeight,
        int slotCount = 9)
    {
        var quads = ComputeSlotQuads(frameWidth, frameHeight, slotCount);
        var rowWidth = quads[^1].Item3 - quads[0].Item1;
        var dstW = checked((int)Math.Round(rowWidth * 1.04));
        var sx = ScaleX(frameWidth);
        var cardW = (AnchorTopRightX - AnchorTopLeftX) * sx;
        var scale = dstW / rowWidth;

        var midX = frameWidth / 2d;
        // 槽间距必须用 9 槽锚点中心（固定 404.5 参考系）——不能用
        // quads[0] 中心：6/7/8 槽时 quads[0] 更靠中间，dTop 变小导致
        // 槽位矩形间距 < 卡宽、相邻框重叠（6 槽重叠 38px 实测），
        // 识别器裁剪框混入相邻卡牌 → 错位/漏识别（2026-08-16 诊断）。
        var topCenter = ((AnchorTopLeftX + AnchorTopRightX) / 2d) * ScaleX(frameWidth);
        // 槽间距恒定（与 ComputeSlotQuads 一致）：9 槽锚点推出。
        var dTop = (midX - topCenter) / 4d;
        var dstDTop = dTop * scale;
        var dstCardW = cardW * scale;
        // warped 帧里第 1 张卡左上角在 x=0（WarpBackRow 单应源 =
        // 最左槽左上 → dst (0,0)），槽 i 左上角 = i × dstDTop。
        // 不能用 quads[0] 反推 firstCenter：6/7/8 槽时 quads[0] 不是
        // 锚点槽，会整体左移（6 槽实测左移 217px）。
        var rects = new List<PixelRect>(slotCount);
        for (var i = 0; i < slotCount; i++)
        {
            rects.Add(new PixelRect(
                (int)Math.Round(i * dstDTop),
                0,
                (int)Math.Round(dstCardW),
                dstH(rowWidth, quads)));
        }

        return rects;
    }

    private static int dstH(
        double rowWidth,
        IReadOnlyList<(double X1, double Y1, double X2, double Y2,
            double X3, double Y3, double X4, double Y4)> quads)
    {
        var minY = quads.Min(q => Math.Min(q.Item2, q.Item6));
        var maxY = quads.Max(q => Math.Max(q.Item2, q.Item6));
        var rowHeight = maxY - minY;
        var dstW = checked((int)Math.Round(rowWidth * 1.04));
        // 保持源平面宽高比（见 WarpBackRow 注释）。
        return checked((int)Math.Round(dstW * rowHeight / rowWidth));
    }
}
