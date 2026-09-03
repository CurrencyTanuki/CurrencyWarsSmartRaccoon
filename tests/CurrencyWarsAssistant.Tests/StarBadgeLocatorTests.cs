using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Runtime.InteropServices;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 星徽定位器夹具测试（1.2.26，坑 35）：用 2026-09-03 实拍 1080P 面板截图验证
/// "金色像素→位置先验→模板判别"能把命运圣杯星徽与同为金色的幸运星区分开。
/// 夹具：Fixtures/star_badge_panel_20260903_1080.png（星徽在面板最底行 y≈588，幸运星 y≈425）。
/// </summary>
public sealed class StarBadgeLocatorTests
{
    private static string FixturePath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "CurrencyWarsAssistant.Tests", "Fixtures", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private static Mat LoadImage(string fileName)
    {
        // OpenCV ImRead 不支持非 ASCII 路径（工作区含中文目录），改字节流解码。
        var bytes = File.ReadAllBytes(FixturePath(fileName));
        var mat = Cv2.ImDecode(bytes, ImreadModes.Color);
        Assert.False(mat.Empty(), $"图像读取失败：{fileName}");
        return mat;
    }

    private static CaptureFrame LoadFixture(string fileName)
    {
        var mat = LoadImage(fileName);
        Cv2.CvtColor(mat, mat, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[mat.Rows * mat.Cols * 4];
        Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            mat.Cols,
            mat.Rows,
            (int)mat.Step(),
            pixels,
            new PixelRect(0, 0, mat.Cols, mat.Rows),
            DateTimeOffset.Now);
    }

    private static Mat LoadTemplate(string fileName)
    {
        var mat = LoadImage(fileName);
        Assert.False(mat.Empty(), $"模板读取失败：{fileName}");
        return mat;
    }

    [Fact]
    public void Diagnose_MorningPanel_PeakScores()
    {
        var frame = LoadFixture("star_badge_panel_20260903_morning_1080.png");
        using var template = LoadTemplate("template_badge.png");
        var scaleX = frame.Width / 1920.0;
        var scaleY = frame.Height / 1080.0;
        var sx0 = (int)(1755 * scaleX);
        var sy0 = (int)(108 * scaleY);
        var stripW = (int)(165 * scaleX);
        var stripH = (int)(620 * scaleY);
        using var frameMat = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        if (frame.Stride == frame.Width * 4)
        {
            System.Runtime.InteropServices.Marshal.Copy(frame.BgraPixels, 0, frameMat.Data, frame.BgraPixels.Length);
        }
        using var bgr = new Mat();
        Cv2.CvtColor(frameMat, bgr, ColorConversionCodes.BGRA2BGR);
        using var strip = new Mat(bgr, new Rect(sx0, sy0, stripW, stripH));
        var lines = new List<string>();
        foreach (var scale in new[] { scaleY * 0.9, scaleY, scaleY * 1.1 })
        {
            var tw = (int)Math.Round(template.Cols * scale);
            var th = (int)Math.Round(template.Rows * scale);
            if (tw < 8 || th < 8 || tw > stripW || th > stripH) continue;
            using var templ = new Mat();
            Cv2.Resize(template, templ, new Size(tw, th));
            using var result = new Mat();
            Cv2.MatchTemplate(strip, templ, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var max, out _, out var maxLoc);
            lines.Add($"scale={scale:F2} max={max:F3} center=({sx0 + maxLoc.X + tw / 2},{sy0 + maxLoc.Y + th / 2})");
        }

        var fixDir = Path.GetDirectoryName(FixturePath("star_badge_panel_20260903_1080.png"))!;
        File.WriteAllLines(Path.Combine(fixDir, "_morning_diag.txt"), lines);
        Assert.True(lines.Count > 0, "无诊断输出");
    }

    [Fact]
    public void LocatesBadge_MorningPanel_PurpleBackground()
    {
        // 2026-09-03 晨间夹具：紫粉背景（模板为暗蓝底实拍）下星徽真实得分 0.629，须过 0.60 门槛。
        var frame = LoadFixture("star_badge_panel_20260903_morning_1080.png");
        using var template = LoadTemplate("template_badge.png");

        var found = StarBadgeLocator.TryLocate(frame, template, out var center, out var score, out var diagnostics);

        Assert.True(found, $"晨间面板应定位到星徽。诊断：{diagnostics}");
        Assert.InRange(center.X, 1770, 1920);
        Assert.InRange(center.Y, 250, 450);
    }

    [Fact]
    public void LocatesBadge_AtBottomOfPanel_NotLuckyStar()
    {
        var frame = LoadFixture("star_badge_panel_20260903_1080.png");
        using var template = LoadTemplate("template_badge.png");

        var found = StarBadgeLocator.TryLocate(frame, template, out var center, out var score, out var diagnostics);

        if (!found)
        {
            Assert.Fail($"定位失败。诊断：{diagnostics}");
        }

        Assert.True(score >= 0.55, $"模板分应≥0.55，实际 {score:F2}");
        Assert.InRange(center.X, 1770, 1920);
        Assert.True(center.Y > 520, $"星徽应在面板底部（y>520），实际 ({center.X},{center.Y})");
        Assert.True(Math.Abs(center.Y - 425) > 80, $"不得命中幸运星位置（y≈425），实际 ({center.X},{center.Y})");
    }

    [Fact]
    public void LuckyStarTemplate_MatchesLuckyStarPosition_NotBadge()
    {
        var frame = LoadFixture("star_badge_panel_20260903_1080.png");
        using var luckyStarTemplate = LoadTemplate("template_luckystar.png");

        var found = StarBadgeLocator.TryLocate(frame, luckyStarTemplate, out var center, out _);

        // 模板判别应把两者分开：幸运星模板命中的是幸运星位置（y≈425），绝不可跑到星徽位置（y≈588）。
        if (found)
        {
            Assert.True(Math.Abs(center.Y - 425) < 80, $"应命中幸运星位置，实际 ({center.X},{center.Y})");
            Assert.True(Math.Abs(center.Y - 588) > 80, "不得误判到星徽位置");
        }
    }
}
