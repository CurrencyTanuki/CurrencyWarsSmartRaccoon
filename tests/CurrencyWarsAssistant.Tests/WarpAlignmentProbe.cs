using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// warp 对齐诊断：对 6/7/8/9 槽帧各跑 WarpBackRow，用非黑像素列投影
/// 找 warped 帧里卡牌的实际位置，与 warpedBackSlots 矩形对比，
/// 并把 warped 帧+槽位框画图存 artifacts/diag 供用户审核。
/// </summary>
public sealed class WarpAlignmentProbe(ITestOutputHelper output)
{
    [Fact]
    public void DiagnoseWarpAlignmentForAllSlotCounts()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CurrencyWarsSmartRaccoon", "debug-evidence", "20260812-084051", "frames");
        if (!Directory.Exists(dir))
        {
            output.WriteLine("no dir");
            return;
        }

        // 用户逐格标定的备战页帧（金标准）。原批量样本
        //（00003/00009/00041/00045）经千问核实是战斗/终结技动画帧，
        // 非备战页——已废弃。
        var samples = new Dictionary<string, string>
        {
            ["7slot-000032"] = "D:/CWAFix-20260814/tests/CurrencyWarsAssistant.Tests/Fixtures/PageReplay/user_ref_000032_prep22.png",
            ["6slot-00023"] = "C:/Users/zzz81/AppData/Local/CurrencyWarsSmartRaccoon/debug-evidence/20260812-084051/frames/00023-full-004127236-20260812-004127236_png.png",
            ["7slot-01433"] = "C:/Users/zzz81/AppData/Local/CurrencyWarsSmartRaccoon/debug-evidence/20260812-084051/frames/01433-full-011041300-20260812-011041300_png.png",
        };
        var outDir = @"D:\CWAFix-20260814\artifacts\diag";
        Directory.CreateDirectory(outDir);

        foreach (var (label, name) in samples)
        {
            var path = name;
            if (!File.Exists(path))
            {
                output.WriteLine($"{label}: 缺帧 {path}");
                continue;
            }

            var frame = CaptureFrameLoader.LoadFile(path);
            var (warped, slots) = BackRowPerspectiveGeometry.WarpBackRow(frame, int.Parse(label[..1]));
            output.WriteLine($"=== {label}: {Path.GetFileName(name)} ===");
            output.WriteLine($"  warped {warped.Width}x{warped.Height}");

            // 非黑像素列投影 → 卡牌实际 x 范围
            var stride = warped.Width * 4;
            var colNonBlack = new int[warped.Width];
            for (var y = 0; y < warped.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < warped.Width; x++)
                {
                    var i = row + x * 4;
                    var lum = (warped.BgraPixels[i] * 29 +
                               warped.BgraPixels[i + 1] * 150 +
                               warped.BgraPixels[i + 2] * 77) >> 8;
                    if (lum > 30)
                    {
                        colNonBlack[x]++;
                    }
                }
            }

            var cardRanges = new List<(int L, int R)>();
            var inCard = false;
            var start = 0;
            for (var x = 0; x < warped.Width; x++)
            {
                if (colNonBlack[x] > 5 && !inCard)
                {
                    start = x;
                    inCard = true;
                }
                else if (colNonBlack[x] <= 5 && inCard)
                {
                    if (x - start > 20)
                    {
                        cardRanges.Add((start, x));
                    }
                    inCard = false;
                }
            }
            if (inCard && warped.Width - start > 20)
            {
                cardRanges.Add((start, warped.Width - 1));
            }

            output.WriteLine($"  卡牌实际x范围（投影）: {string.Join(", ", cardRanges.Select(r => $"{r.L}-{r.R}"))}");
            for (var s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                var overlap = cardRanges.Any(r =>
                    slot.X < r.R && slot.Right > r.L);
                output.WriteLine(
                    $"  slot{s} rect=({slot.X},{slot.Y},{slot.Width}x{slot.Height}) 卡牌重叠={overlap}");
            }

            // 画图：warped 帧 + 槽位框（绿=重叠卡牌、红=无卡牌重叠）
            using var mat = new Mat(
                warped.Height,
                warped.Width,
                MatType.CV_8UC4);
            System.Runtime.InteropServices.Marshal.Copy(
                warped.BgraPixels,
                0,
                mat.Data,
                warped.BgraPixels.Length);
            for (var s = 0; s < slots.Count; s++)
            {
                var slot = slots[s];
                var hasCard = cardRanges.Any(r => slot.X < r.R && slot.Right > r.L);
                Cv2.Rectangle(
                    mat,
                    new Rect(slot.X, slot.Y, slot.Width, slot.Height),
                    hasCard ? Scalar.Lime : Scalar.Red,
                    2);
                Cv2.PutText(
                    mat,
                    s.ToString(),
                    new Point(slot.X + 4, slot.Y + 22),
                    HersheyFonts.HersheyPlain,
                    1.4,
                    Scalar.Yellow,
                    2);
            }

            var outPath = Path.Combine(outDir, $"warp-align-{label}-{Path.GetFileNameWithoutExtension(name)}.png");
            Cv2.ImWrite(outPath, mat);
            output.WriteLine($"  标注图: {outPath}");
        }
    }
}
