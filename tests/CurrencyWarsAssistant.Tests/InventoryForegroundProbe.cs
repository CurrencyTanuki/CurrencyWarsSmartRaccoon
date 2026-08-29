using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

// 诊断：fixture 帧 preparation-1-4-user-2026-08-01.png 物品栏槽(InventoryIconSlots右列+左列)
// 逐个计算 stddev / transitionRatio，对照 HasDetailedForeground 阈值(≥14 && ≥0.025)。
public sealed class InventoryForegroundProbe
{
    [Fact]
    public void Run()
    {
        var path = @"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-live-2026-07-29\preparation-1-4-user-2026-08-01.png";
        var frame = CaptureFrameLoader.LoadFile(path);
        var baseSlots = Phase2RecognitionRegions.InventoryIconSlots.ToArray();
        var right = baseSlots;
        var left = baseSlots.Select(r => r with { X = r.X - baseSlots[0].Width }).ToArray();
        var all = left.Concat(right).ToArray();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"帧={frame.Width}x{frame.Height}");
        for (var i = 0; i < all.Length; i++)
        {
            var region = all[i].ToPixels(frame.Width, frame.Height);
            long sum = 0, sq = 0; int trans = 0, cmp = 0;
            for (var y = region.Y; y < region.Bottom; y++)
            {
                int prev = -1;
                for (var x = region.X; x < region.Right; x++)
                {
                    var o = y * frame.Stride + x * 4;
                    var lum = (frame.BgraPixels[o] * 29 + frame.BgraPixels[o + 1] * 150 + frame.BgraPixels[o + 2] * 77) >> 8;
                    sum += lum; sq += lum * lum;
                    if (prev >= 0) { cmp++; if (Math.Abs(lum - prev) >= 25) trans++; }
                    if (y > region.Y)
                    {
                        var up = (frame.BgraPixels[o - frame.Stride] * 29 + frame.BgraPixels[o + 1 - frame.Stride] * 150 + frame.BgraPixels[o + 2 - frame.Stride] * 77) >> 8;
                        cmp++; if (Math.Abs(lum - up) >= 25) trans++;
                    }
                    prev = lum;
                }
            }
            var count = region.Width * region.Height;
            var mean = (double)sum / count;
            var variance = Math.Max(0, (double)sq / count - mean * mean);
            var tr = cmp == 0 ? 0 : trans / (double)cmp;
            var pass = Math.Sqrt(variance) >= 14 && tr >= 0.025;
            sb.AppendLine($"槽{i}({(i < left.Length ? "L" : "R")}) px={region} stddev={Math.Sqrt(variance):F1} trans={tr:F4} → {(pass ? "PASS" : "FAIL")}");
        }
        System.Console.WriteLine(sb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "INVF.txt"), sb.ToString());
    }
}
