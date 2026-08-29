using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 星核诊断：000032 爻光（真值 2 星，warp 路径识别 3 星）——
/// 对比 warp 帧槽位与原帧槽位的星核连通域/聚类，定位多检的星来自哪。
/// </summary>
public sealed class StarCoreDiagProbe(ITestOutputHelper output)
{
    [Fact]
    public void DiagnoseYaoGuangStarCores()
    {
        var path = @"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\PageReplay\user_ref_000032_prep22.png";
        if (!File.Exists(path))
        {
            output.WriteLine("missing");
            return;
        }

        var frame = CaptureFrameLoader.LoadFile(path);
        var (warped, slots) = BackRowPerspectiveGeometry.WarpBackRow(frame, 7);
        var warpedSlot = slots[3]; // 第 4 格 = 爻光（slot2=千冶刃、slot3=开拓者）
        var origSlots = Phase2RecognitionRegions.BackCharacterSlots1920(7);
        var origSlot = origSlots[3]; // 第 4 格 = 爻光

        output.WriteLine("=== warp 帧爻光槽位星核 ===");
        DumpStarCores(warped, warpedSlot, "warp");
        output.WriteLine("=== 原帧爻光槽位星核 ===");
        DumpStarCores(frame, origSlot, "orig");
    }

    private void DumpStarCores(CaptureFrame frame, PixelRect slot, string label)
    {
        // 复刻 CountStarCoresFromFrame：槽内 X0.30-0.70 Y0.65-0.82
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
        output.WriteLine($"  slot=({slot.X},{slot.Y},{slot.Width}x{slot.Height}) 星带=({px0},{py0})-({px1},{py1})");

        var cores = new List<(double X, double Y, int C, int W, int H)>();
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
                if (count >= 3 && componentWidth >= 3 && componentHeight >= 3)
                {
                    cores.Add((sumX / count, sumY / count, count, componentWidth, componentHeight));
                }
            }
        }

        output.WriteLine($"  连通域 {cores.Count} 个:");
        foreach (var c in cores)
        {
            output.WriteLine(
                $"    中心=({c.X + px0:F0},{c.Y + py0:F0}) px={c.C} 尺寸={c.W}x{c.H}");
        }

        // 聚类（复刻 11px）
        var merged = new List<(double X, double Y)>();
        foreach (var core in cores.OrderBy(c => c.X))
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
            else
            {
                merged[index] = ((merged[index].X + core.X) / 2, (merged[index].Y + core.Y) / 2);
            }
        }

        output.WriteLine($"  聚类 {merged.Count} 颗: {string.Join(" | ", merged.Select(m => $"({m.X + px0:F0},{m.Y + py0:F0})"))}");
    }
}
