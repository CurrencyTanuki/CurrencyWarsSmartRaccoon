using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// C 步诊断：04 帧（clipboard-20260816-132957.787880-000004.png）
/// 三月七（后台，识别成 2 星、正确应为 1 星）——全后台槽星带连通域 dump，
/// 定位多余的星核来自哪（warp 帧 vs 原帧）。
/// </summary>
public sealed class March7StarCoreDiagProbe(ITestOutputHelper output)
{
    [Fact]
    public void DumpMarch7BackStarCores()
    {
        var path = @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-132957.787880-000004.png";
        if (!File.Exists(path))
        {
            output.WriteLine("missing frame");
            return;
        }

        var frame = CaptureFrameLoader.LoadFile(path);
        output.WriteLine($"FRAME {frame.Width}x{frame.Height}");

        const int backCount = 6;
        // 生产 warp 路径
        var (warped, warpedRects) = BackRowPerspectiveGeometry.WarpBackRow(frame, backCount);
        output.WriteLine($"WARPED {warped.Width}x{warped.Height} 背槽数={backCount}");
        output.WriteLine("=== warp 帧 各背槽星带 ===");
        for (var i = 0; i < backCount; i++)
        {
            DumpCores(warped, warpedRects[i], $"warp Back rel={i}");
        }

        // 原帧对比
        var origSlots = Phase2RecognitionRegions.BackCharacterSlots1920(backCount);
        output.WriteLine("=== 原帧 各背槽星带 ===");
        for (var i = 0; i < backCount; i++)
        {
            DumpCores(frame, origSlots[i], $"orig Back rel={i}");
        }
    }

    private void DumpCores(CaptureFrame frame, PixelRect slot, string label)
    {
        // 复刻 CountStarCoresFromFrame(BackRight)：星带 X0.30-0.70 Y0.65-0.82
        var scaleX = frame.Width / 1920d;
        var scaleY = frame.Height / 1080d;
        var px0 = (int)Math.Round((slot.X + slot.Width * 0.30) * scaleX);
        var px1 = (int)Math.Round((slot.X + slot.Width * 0.70) * scaleX);
        var py0 = (int)Math.Round((slot.Y + slot.Height * 0.65) * scaleY);
        var py1 = (int)Math.Round((slot.Y + slot.Height * 0.82) * scaleY);
        px0 = Math.Max(0, px0);
        py0 = Math.Max(0, py0);
        px1 = Math.Min(frame.Width, px1);
        py1 = Math.Min(frame.Height, py1);
        var width = px1 - px0;
        var height = py1 - py0;
        if (width < 8 || height < 8)
        {
            output.WriteLine($"{label}: 星带太小 slot={slot}");
            return;
        }

        output.WriteLine($"{label}: slot=({slot.X},{slot.Y},{slot.Width}x{slot.Height}) 星带=({px0},{py0})-({px1},{py1})");

        var all = new List<(double X, double Y, int C, int W, int H, double Aspect)>();
        var visited = new bool[height, width];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = (py0 + y) * frame.Stride + px0 * 4;
            for (var x = 0; x < width; x++)
            {
                if (visited[y, x])
                {
                    continue;
                }

                var offset = rowOffset + x * 4;
                var g = frame.BgraPixels[offset + 1];
                var r = frame.BgraPixels[offset + 2];
                if (r <= 245 || g <= 225)
                {
                    continue;
                }

                var queue = new Queue<(int Y, int X)>();
                queue.Enqueue((y, x));
                visited[y, x] = true;
                var sumX = 0d;
                var sumY = 0d;
                var count = 0;
                var minX = int.MaxValue;
                var minY = int.MaxValue;
                var maxX = int.MinValue;
                var maxY = int.MinValue;
                while (queue.Count > 0)
                {
                    var (cy, cx) = queue.Dequeue();
                    sumX += cx;
                    sumY += cy;
                    count++;
                    minX = Math.Min(minX, cx);
                    minY = Math.Min(minY, cy);
                    maxX = Math.Max(maxX, cx);
                    maxY = Math.Max(maxY, cy);
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dy == 0 && dx == 0)
                            {
                                continue;
                            }

                            var ny = cy + dy;
                            var nx = cx + dx;
                            if (ny < 0 || ny >= height ||
                                nx < 0 || nx >= width ||
                                visited[ny, nx])
                            {
                                continue;
                            }

                            var no = (py0 + ny) * frame.Stride + (px0 + nx) * 4;
                            if (frame.BgraPixels[no + 1] <= 225 ||
                                frame.BgraPixels[no + 2] <= 245)
                            {
                                continue;
                            }

                            visited[ny, nx] = true;
                            queue.Enqueue((ny, nx));
                        }
                    }
                }

                var componentWidth = maxX - minX + 1;
                var componentHeight = maxY - minY + 1;
                var aspect = (double)Math.Max(componentWidth, componentHeight) /
                    Math.Max(1, Math.Min(componentWidth, componentHeight));
                if (count >= 3 && componentWidth >= 3 && componentHeight >= 3)
                {
                    all.Add((sumX / count + px0, sumY / count + py0, count, componentWidth, componentHeight, aspect));
                }
            }
        }

        output.WriteLine($"  连通域 {all.Count} 个:");
        foreach (var c in all.OrderBy(c => c.X))
        {
            output.WriteLine($"    中心=({c.X:F0},{c.Y:F0}) px={c.C} 尺寸={c.W}x{c.H} 宽高比={c.Aspect:F2}" +
                (c.Aspect <= 3d ? " [过形状] " : " [长条-被形状过滤] "));
        }

        // 聚类复刻 11px
        var merged = new List<(double X, double Y)>();
        foreach (var core in all.Where(c => c.Aspect <= 3d).OrderBy(c => c.X))
        {
            var index = -1;
            for (var i = 0; i < merged.Count; i++)
            {
                if (Math.Abs(merged[i].X - core.X) < 11 &&
                    Math.Abs(merged[i].Y - core.Y) < 11)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                merged.Add((core.X, core.Y));
            }
        }

        output.WriteLine($"  聚类(过形状, 11px) {merged.Count} 颗: {string.Join(" | ", merged.Select(m => $"({m.X:F0},{m.Y:F0})"))}");
    }
}
