using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 命运圣杯星徽定位器（1.2.26，用户 2026-09-03 定稿）：
/// ①金色像素掩码 → 网格连通簇；
/// ②位置先验——星徽必在物品栏最后出现=最底行/行内最左（游戏排列先上到下、再右到左，用户实测铁律），候选按此排序；
/// ③模板判别——候选簇裁片与星徽实拍模板做归一化相关匹配，只有星徽得分最高才拖。
/// 颜色只能判"金色"，判不了"是星徽"（坑 35：幸运星同为金色曾被误拖）。
/// 探测区/阈值一律 1920×1080 基准（与识别管线同一技术标准，用户指定）。
/// </summary>
internal static class StarBadgeLocator
{
    /// <summary>物品栏全面板探测区（1920×1080 基准）。</summary>
    private static readonly PixelRect PanelStrip = new(1755, 108, 165, 620); // 顶边 108：覆盖物品顶部（审查 P3-D）

    private const int ClusterCellPixels = 18;
    private const double TemplateScoreThreshold = 0.60;   // 接受门槛（坑 35 后定标：实拍星徽 0.629、跨物品干扰 ≤0.46；2026-09-03 晨间紫粉背景实测标定）
    private const double PeakCollectThreshold = 0.60;     // 诊断可见的峰收集线

    private static readonly Lazy<Mat?> ShippedTemplateLazy = new(LoadShippedTemplateCore);

    private static Mat? _shippedTemplate;

    /// <summary>定位失败时导出探测区图像的目录（ASCII 路径；null=不导出）。供离线校准。</summary>
    internal static string? DiagnosticsDumpDirectory;

    /// <summary>便捷入口：使用随包发布的实拍模板（Assets/Recognition/star_badge_template.png）。</summary>
    public static bool TryLocate(CaptureFrame frame, out PixelPoint center, out double score)
    {
        var template = LoadShippedTemplate();
        if (template is null)
        {
            center = default;
            score = 0;
            return false;
        }

        return TryLocate(frame, template, out center, out score);
    }

    public static bool TryLocate(CaptureFrame frame, Mat badgeTemplate, out PixelPoint center, out double score) =>
        TryLocate(frame, badgeTemplate, out center, out score, out _);

    public static bool TryLocate(CaptureFrame frame, Mat badgeTemplate, out PixelPoint center, out double score, out string diagnostics)
    {
        center = default;
        score = 0;
        var diag = new System.Text.StringBuilder();
        if (frame.BgraPixels.Length < frame.Stride * (long)frame.Height)
        {
            diagnostics = "帧缓冲长度异常";
            return false;
        }

        var scaleX = frame.Width / 1920.0;
        var scaleY = frame.Height / 1080.0;
        var sx0 = Math.Max(0, (int)(PanelStrip.X * scaleX));
        var sy0 = Math.Max(0, (int)(PanelStrip.Y * scaleY));
        var sx1 = Math.Min(frame.Width, (int)((PanelStrip.X + PanelStrip.Width) * scaleX));
        var sy1 = Math.Min(frame.Height, (int)((PanelStrip.Y + PanelStrip.Height) * scaleY));
        var stripW = sx1 - sx0;
        var stripH = sy1 - sy0;
        if (stripW < 20 || stripH < 20)
        {
            diagnostics = "探测区过小（帧尺寸异常）";
            return false;
        }

        using var frameMat = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        if (frame.Stride == frame.Width * 4)
        {
            Marshal.Copy(frame.BgraPixels, 0, frameMat.Data, frame.BgraPixels.Length);
        }
        else
        {
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.BgraPixels, y * frame.Stride, IntPtr.Add(frameMat.Data, y * frame.Width * 4), frame.Width * 4);
            }
        }

        using var bgr = new Mat();
        Cv2.CvtColor(frameMat, bgr, ColorConversionCodes.BGRA2BGR);
        using var strip = new Mat(bgr, new Rect(sx0, sy0, stripW, stripH));

        // 多尺度（模板 1080 基准渲染 × 帧高推导缩放 ±10%）收集全部 ≥阈值峰，跨尺度按位置合并。
        var peaks = new List<(PixelPoint Center, double Score)>();
        var baseW = Math.Max(8, badgeTemplate.Cols);
        var baseH = Math.Max(8, badgeTemplate.Rows);
        foreach (var scale in new[] { scaleY * 0.9, scaleY, scaleY * 1.1 })
        {
            var tw = (int)Math.Round(baseW * scale);
            var th = (int)Math.Round(baseH * scale);
            if (tw < 8 || th < 8 || tw > stripW || th > stripH)
            {
                continue;
            }

            using var templ = new Mat();
            Cv2.Resize(badgeTemplate, templ, new Size(tw, th));
            using var result = new Mat();
            Cv2.MatchTemplate(strip, templ, result, TemplateMatchModes.CCoeffNormed);
            for (var y = 0; y < result.Rows; y++)
            {
                for (var x = 0; x < result.Cols; x++)
                {
                    var v = result.At<float>(y, x);
                    if (v >= PeakCollectThreshold)
                    {
                        peaks.Add((new PixelPoint(sx0 + x + tw / 2, sy0 + y + th / 2), v));
                    }
                }
            }
        }

        // 跨尺度峰合并：30px 内视为同一物品，保留最高分（高分先占位）。
        var merged = new List<(PixelPoint Center, double Score)>();
        foreach (var peak in peaks.OrderByDescending(p => p.Score))
        {
            var near = merged.FindIndex(m =>
                Math.Abs(m.Center.X - peak.Center.X) < 30 && Math.Abs(m.Center.Y - peak.Center.Y) < 30);
            if (near < 0)
            {
                merged.Add(peak);
            }
        }

        diag.AppendLine($"峰 {peaks.Count} 个，合并后 {merged.Count} 个：" +
            string.Join("；", merged.Select(m => $"({m.Center.X},{m.Center.Y}) 分 {m.Score:F3}")));

        // 位置先验（用户实测铁律：星徽必在物品栏最后=最底行/行内最左——游戏排列先上到下再右到左）：
        // 候选按 最底行→行内最左 排序取首个。幸运星等上方金色物品即使分数更高也让位。
        var chosen = merged
            .OrderByDescending(m => m.Center.Y)
            .ThenBy(m => m.Center.X)
            .FirstOrDefault();
        if (chosen.Center == default || chosen.Score < TemplateScoreThreshold)
        {
            DumpStrip(strip);
            diagnostics = diag.ToString();
            return false;
        }

        center = chosen.Center;
        score = chosen.Score;
        diagnostics = diag.ToString();
        return true;
    }

    /// <summary>随包模板懒加载（Lazy 线程安全，审查 P3-E）。模板=2026-09-03 实拍 1080P 裁片。</summary>
    private static void DumpStrip(Mat strip)
    {
        if (DiagnosticsDumpDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DiagnosticsDumpDirectory);
            var path = Path.Combine(DiagnosticsDumpDirectory, $"star_badge_strip_{DateTime.Now:HHmmss}.png");
            Cv2.ImWrite(path, strip);
        }
        catch
        {
            // 诊断导出失败不影响主流程
        }
    }

    public static Mat? LoadShippedTemplate()
    {
        _shippedTemplate = ShippedTemplateLazy.Value;
        return _shippedTemplate;
    }

    private static Mat? LoadShippedTemplateCore()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Recognition", "star_badge_template.png");
        if (!File.Exists(path))
        {
            return null;
        }

        var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat.Empty())
        {
            return null;
        }

        _shippedTemplate = mat;
        return mat;
    }
}
