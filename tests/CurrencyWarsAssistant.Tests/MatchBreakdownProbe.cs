using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CurrencyWarsAssistant.Tests;

/// <summary>复现产品 Match 打分公式，对 warp 槽0 卡面对多个角色模板逐项分解，
/// 定位"完全不像的(开拓者)为何与霍霍同分0.499"。</summary>
public sealed class MatchBreakdownProbe
{
    const double Grace = 0.90, PenWeight = 0.50;

    // 2026-09-05：取证帧源已随磁盘卫生清理（runs/run-20260818-163242），探针无数据必挂；
    // 恢复数据或改指新取证目录时移除本 Skip。
    [Fact(Skip = "2026-09-05 取证帧已清理（run-20260818-163242）：诊断探针数据缺席时跳过")]
    public void Breakdown()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var screens = Path.Combine(appData, "CurrencyWarsSmartRaccoon", "runs",
            "run-20260818-163242", "screenshots");
        var outDir = Path.Combine(Path.GetTempPath(), "cwbackdiag");
        var repo = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var tplDir = Path.Combine(repo, "data", "4.4", "character-card-templates");

        var frame = CaptureFrameLoader.LoadFile(Path.Combine(screens, "20260818-084639619.png"));
        dynamic pair = Wrap((object)frame, 6);
        using var warp = ToBgr(pair.Item1);
        // 槽0 = (0,0,128,140)
        using var search = new Mat(warp, new OpenCvSharp.Rect(0, 0, 128, 140));

        var targets = new[]
        {
            "currency_wars_character_69",      // 霍霍(真值)
            "currency_wars_character_trailblazer", // 开拓者(误判)
            "currency_wars_character_03",
            "currency_wars_character_05",
            "currency_wars_character_23",
        };

        var lines = new List<string>();
        foreach (var id in targets)
        {
            var file = System.IO.Directory.GetFiles(tplDir, $"{id}__*.png").FirstOrDefault();
            if (file is null) { lines.Add($"{id}: 无模板"); continue; }
            var (m, d, c2, tot) = Compute(search, file);
            lines.Add($"{id}: max={m:F3} detail={d:F3} colorSim={c2:F3} penalty={PenWeight * Math.Max(0, Grace - c2):F3} TOTAL={tot:F3}");
        }
        System.IO.File.WriteAllLines(Path.Combine(outDir, "MATCH_BREAKDOWN.txt"), lines);
        System.IO.File.WriteAllText(Path.Combine(outDir, "MB_DONE.txt"), "ok");
    }

    static (double Max, double Detail, double ColorSim, double Total) Compute(Mat search, string tplPath)
    {
        using var template = Cv2.ImRead(tplPath, ImreadModes.Color);
        if (template.Width != 111 || template.Height != 127)
        {
            var r = new Mat();
            Cv2.Resize(template, r, new OpenCvSharp.Size(111, 127), interpolation: InterpolationFlags.Area);
            return Calc(search, r);
        }
        return Calc(search, template);
    }

    static (double Max, double Detail, double ColorSim, double Total) Calc(Mat search, Mat template)
    {
        using var scores = new Mat();
        Cv2.MatchTemplate(search, template, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out var maximum, out _, out var loc);
        var detail = new OpenCvSharp.Rect(5, 5, template.Width - 25 - 5, template.Height - 15 - 5);
        using var tDetail = new Mat(template, detail);
        using var sDetail = new Mat(search, new OpenCvSharp.Rect(loc.X + detail.X, loc.Y + detail.Y, detail.Width, detail.Height));
        using var ds = new Mat();
        Cv2.MatchTemplate(sDetail, tDetail, ds, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(ds, out _, out var detailMax, out _, out _);
        using var sAligned = new Mat(search, new OpenCvSharp.Rect(loc.X + detail.X, loc.Y + detail.Y, detail.Width, detail.Height));
        using var diff = new Mat();
        Cv2.Absdiff(sAligned, tDetail, diff);
        var mean = Cv2.Mean(diff);
        var colorSim = 1 - (mean.Val0 + mean.Val1 + mean.Val2) / (3d * 255d);
        var penalty = PenWeight * Math.Max(0, Grace - colorSim);
        var total = maximum * 0.55 + detailMax * 0.45 - penalty;
        return (maximum, detailMax, colorSim, total);
    }

    static object Wrap(object frame, int slotCount)
    {
        var asm = Assembly.Load("CurrencyWarsAssistant.Tasks");
        var geom = asm.GetType("CurrencyWarsAssistant.Tasks.BackRowPerspectiveGeometry")!;
        var method = geom.GetMethod("WarpBackRow", BindingFlags.Public | BindingFlags.Static)!;
        return method.Invoke(null, new[] { frame, slotCount })!;
    }

    static Mat ToBgr(dynamic frame)
    {
        var w = (int)frame.Width;
        var h = (int)frame.Height;
        var pixels = (byte[])frame.BgraPixels;
        using var bgra = new Mat(h, w, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, bgra.Data, pixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }
}
