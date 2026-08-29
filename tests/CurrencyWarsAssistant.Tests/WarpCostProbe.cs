using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 量化 BackRowPerspectiveGeometry.WarpBackRow 对 132307 帧的单次耗时，
// 定位 formation 2.8s 里 warp 占比。
public sealed class WarpCostProbe
{
    [Fact]
    public void Run()
    {
        var root = @"D:\CWAFix-20260814";
        var frame = CaptureFrameLoader.LoadFile(
            System.IO.Path.Combine(root, "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "phase2-2026-07-28", "132307.png"));
        for (var i = 0; i < 4; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (warped, slots) = BackRowPerspectiveGeometry.WarpBackRow(frame);
            sw.Stop();
            System.Console.WriteLine($"[warp] iter{i}: {sw.Elapsed.TotalMilliseconds:F1} ms  warped={warped.Width}x{warped.Height} slots={slots.Count}");
        }
    }
}
