using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;
using System.Reflection;

namespace CurrencyWarsAssistant.Tests;

// 诊断：011854 缇宝 Front slot1（CardRegion≈1102,438,163,187，2559系）。用户审核确认
// 3 件装备=胜利之旗/追逐星尘/胜利之旗。当前段检测只出 2 段。此探针扩大 y 范围扫描
// 缇宝卡下方全部非蓝段，定位 3 件图标实际 x/y，找出"3件检测成2段"原因。
public sealed class TibaoFullScanProbe
{
    [Fact]
    public void Run()
    {
        var path = @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png";
        var frame = CaptureFrameLoader.LoadFile(path);

        // 缇宝 Front slot1 card 2559 系（ClipboardFrameProbe 输出）
        var card = new PixelRect(1102, 438, 163, 187);
        // 扩大扫描区：卡片下半部到卡片下方（含装备带）
        var scan = new PixelRect(1102, card.Y + (int)(card.Height * 0.55), 340, (int)(card.Height * 0.55) + 40);

        var t = typeof(Phase2OperationalScreenshotAnalyzer);
        var locate = t.GetMethod("LocateEquipmentSegments", BindingFlags.NonPublic | BindingFlags.Static)!;
        var raw = (IReadOnlyList<PixelRect>)locate.Invoke(null, new object[] { frame, scan })!;
        var merge = t.GetMethod("MergeAdjacentSegments", BindingFlags.NonPublic | BindingFlags.Static)!;
        var merged = (IReadOnlyList<PixelRect>)merge.Invoke(null, new object[] { raw })!;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==== 011854 缇宝 Front slot1 卡下方全扫非蓝段 ====");
        sb.AppendLine($"card=({card})  scan=({scan})");
        sb.AppendLine($"原始段数={raw.Count}: " + string.Join(";", raw.Select(s => $"x{s.X}+{s.Width}y{s.Y}+{s.Height}")));
        sb.AppendLine($"合并段数={merged.Count}: " + string.Join(";", merged.Select(s => $"x{s.X}+{s.Width}y{s.Y}+{s.Height}")));
        System.Console.WriteLine(sb);

        // 记录图片
        var outDir = @"D:\CWAFix-20260814\tools\diag";
        Directory.CreateDirectory(outDir);
        SaveBgra(Path.Combine(outDir, "tibao_scan.png"), frame, scan, 2);

        var d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag");
        System.IO.Directory.CreateDirectory(d);
        System.IO.File.WriteAllText(System.IO.Path.Combine(d, "FULLSCAN.txt"), sb.ToString());
    }

    static void SaveBgra(string file, CaptureFrame frame, PixelRect r, int k)
    {
        var w = r.Width; var h = r.Height;
        var d = new byte[w * k * h * k * 4];
        for (var y = 0; y < h; y++)
        {
            var src = (r.Y + y) * frame.Stride + r.X * 4;
            for (var x = 0; x < w; x++)
            {
                var si = src + x * 4;
                for (var dy = 0; dy < k; dy++)
                    for (var dx = 0; dx < k; dx++)
                    {
                        var di = ((y * k + dy) * (w * k) + (x * k + dx)) * 4;
                        d[di] = frame.BgraPixels[si];
                        d[di + 1] = frame.BgraPixels[si + 1];
                        d[di + 2] = frame.BgraPixels[si + 2];
                        d[di + 3] = 255;
                    }
            }
        }
        var mat = OpenCvSharp.Mat.FromPixelData(h * k, w * k, OpenCvSharp.MatType.CV_8UC4, d);
        using (mat) { OpenCvSharp.Cv2.ImWrite(file, mat); }
    }
}
