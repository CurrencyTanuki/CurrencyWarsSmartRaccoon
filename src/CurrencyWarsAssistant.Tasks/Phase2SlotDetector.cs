using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 备战页卡牌槽位自适应修正器（用户 2026-08-07 要求：兼容 16:9 全分辨率/
/// UI 缩放，位置对得上就能识别）。
/// 原理：固定坐标（1920 参考系）已给出每行卡牌的 y 位置与数量，但
/// 不同分辨率/UI 缩放下卡牌 x 可能整体偏移（视频 1080p 实测前台卡牌
/// x 520-860 vs 固定 681-1234）。本类用"卡牌底部金星"在每行 y 带内
/// 检测卡牌实际 x 位置，修正固定槽位的 x；y 保持固定（16:9 等比缩放
/// 已覆盖）。检测失败时由调用方回退固定坐标。
/// </summary>
public sealed class Phase2SlotDetector
{
    /// <summary>金星像素阈值（金色 = R 高 G 中 B 低）。</summary>
    private const int GoldMinR = 200;
    private const int GoldMinG = 140;
    private const int GoldMaxB = 130;

    /// <summary>
    /// 修正备战页槽位 x 位置。返回与输入等长的修正后槽位列表；
    /// 某行检测不到金星时该行槽位保持原样（回退固定）。
    /// </summary>
    /// <summary>
    /// 后台格子数检测（用户 2026-08-07 方案：预置 6/7/8/9 四套坐标，
    /// 先识别格子数再匹配）。原理：后台格子以屏幕对称轴 x=960 为中心
    /// 等距 145px（已用参考图验证 000035/000036=7格、3-7帧=6格）。
    /// 检测后台行格子中心（列方差峰值聚类），与 4 套模型匹配，
    /// 取平均偏差最小的格数。检测不足时回退 6 格。
    /// </summary>
    public static int DetectBackSlotCount(CaptureFrame frame)
    {
        // 1) 后台行（y 600-745 参考系）列方差检测格子中心
        var y0 = (int)(frame.Height * 600 / 1080d) - 30;
        var y1 = (int)(frame.Height * 745 / 1080d) + 30;
        var colVar = new double[frame.Width];
        for (var x = 0; x < frame.Width; x++)
        {
            long sum = 0;
            long sumSq = 0;
            var count = 0;
            for (var y = y0; y < y1 && y < frame.Height; y++)
            {
                var offset = y * frame.Stride + x * 4;
                var lum = (frame.BgraPixels[offset] * 29 +
                           frame.BgraPixels[offset + 1] * 150 +
                           frame.BgraPixels[offset + 2] * 77) >> 8;
                sum += lum;
                sumSq += lum * lum;
                count++;
            }

            if (count > 0)
            {
                var mean = sum / (double)count;
                colVar[x] = Math.Max(0, sumSq / (double)count - mean * mean);
            }
        }

        // 2) 平滑 + 阈值聚类格子中心
        var smoothed = new double[frame.Width];
        for (var x = 2; x < frame.Width - 2; x++)
        {
            smoothed[x] = (colVar[x - 2] + colVar[x - 1] + colVar[x] +
                           colVar[x + 1] + colVar[x + 2]) / 5;
        }

        var maxVar = smoothed.Max();
        var threshold = maxVar * 0.22;
        var centers = new List<int>();
        var xPos = 0;
        while (xPos < frame.Width)
        {
            if (smoothed[xPos] < threshold)
            {
                xPos++;
                continue;
            }

            var start = xPos;
            var end = xPos;
            while (xPos < frame.Width && smoothed[xPos] >= threshold * 0.5)
            {
                if (smoothed[xPos] > smoothed[end])
                {
                    end = xPos;
                }

                xPos++;
            }

            var width = xPos - start;
            var centerX = (int)Math.Round(end * 1920d / frame.Width);
            // 过滤边缘噪声（x 6%-94% 帧宽外：左侧羁绊列表/右侧按钮等
            // 高方差区域不是后台格子——python 验证版已过滤，C# 漏了）
            // 后台格子 x 范围：6-9 格模型覆盖 380-1540（1920 参考系）。
            // 过滤范围外噪声（左羁绊列表 x<350、右侧按钮 x>1650）——
            // python 验证版 3-7帧=6格/000035=7格 均在此范围。
            if (width > 40 && width < 300 &&
                centerX > 350 && centerX < 1650)
            {
                centers.Add(centerX);
            }
        }

        if (centers.Count < 2)
        {
            return 6; // 检测不足回退
        }

        Console.WriteLine($"[DetectBackSlotCount] centers={string.Join(",", centers)}");

        // 3) 与 6/7/8/9 格模型匹配（960 对称、等距 145）：
        //    统计"模型格与最近检测中心距离 < 60px"的匹配数，
        //    取匹配数最多的格数（并列取总偏差最小）——噪声中心
        //    （不落任何模型格）不参与计分，避免拉偏。
        var bestCount = 6;
        var bestMatches = -1;
        var bestError = double.MaxValue;
        // centers 已在上面换算成 1920×1080 参考系坐标
        //（centerX = end * 1920 / frame.Width），模型也必须是 1920
        // 参考系——两者坐标系必须一致。此前 2026-08-15 误加 scaleX
        // 缩放（把模型换回帧坐标），centers(1920) 与 model(帧) 反向
        // 错配，6/7/8/9 格区分全靠噪声碰运气（用户实机大量误判）。
        const int tolerance = 60;
        // 槽间距恒定（用户 2026-08-15 确认 6/7/8/9 槽间距相同）：
        // 由 9 槽标定锚点推出 dTop = (960 - 404.5) / 4 = 138.875，
        // 替代过时的硬编码 145（2026-08-07 旧值）。
        const double anchorTopCenter = 404.5;
        var dTop = (960 - anchorTopCenter) / 4d;
        for (var n = 6; n <= 9; n++)
        {
            var model = Enumerable.Range(0, n)
                .Select(i => (int)Math.Round(960 + (i - (n - 1) / 2d) * dTop))
                .ToArray();
            var matches = 0;
            var totalError = 0d;
            foreach (var m in model)
            {
                var nearest = centers.Count == 0
                    ? int.MaxValue
                    : centers.Min(c => Math.Abs(c - m));
                if (nearest <= tolerance)
                {
                    matches++;
                    totalError += nearest;
                }
            }

            if (matches > bestMatches ||
                (matches == bestMatches && totalError < bestError))
            {
                bestMatches = matches;
                bestError = totalError;
                bestCount = n;
            }
        }

        return bestCount;
    }

    public static IReadOnlyList<PixelRect> AlignSlotColumns(
        CaptureFrame frame,
        IReadOnlyList<PixelRect> referenceSlots)
    {
        if (referenceSlots.Count == 0)
        {
            return referenceSlots;
        }

        // reference 是 1920x1080 参考系坐标（analyzer 传入），簇检测在
        // 帧像素空间（2K/其他分辨率）——先缩放到帧空间（2026-08-07
        // 修复：3-7 帧 2K 时行 y 399 而非 531，坐标空间不一致导致
        // 行带检测到预览条而非卡牌星级）。
        var scaleX = frame.Width / 1920d;
        var scaleY = frame.Height / 1080d;
        var scaledSlots = referenceSlots
            .Select(slot => new PixelRect(
                (int)Math.Round(slot.X * scaleX),
                (int)Math.Round(slot.Y * scaleY),
                Math.Max(1, (int)Math.Round(slot.Width * scaleX)),
                Math.Max(1, (int)Math.Round(slot.Height * scaleY))))
            .ToArray();

        // 按 y 中心分组（同一行的槽位）
        var rows = scaledSlots
            .GroupBy(slot => slot.Y + slot.Height / 2)
            .OrderBy(group => group.Key)
            .ToList();

        var result = new List<PixelRect>(referenceSlots.Count);
        foreach (var row in rows)
        {
            var slotsInRow = row.OrderBy(s => s.X).ToArray();
            // y 带放宽 ±25% 槽高：星级在卡牌底部，但不同对局星级位置
            // 有漂移（000036 前台银狼星级 y 439 vs 槽位 y 438——接近，
            // 但 000032/000035 有更大偏移，放宽覆盖）
            var yTop = slotsInRow.Min(s => s.Y) -
                       (int)(slotsInRow[0].Height * 0.25);
            var yBottom = slotsInRow.Max(s => s.Y + s.Height) +
                          (int)(slotsInRow[0].Height * 0.25);
            if (yTop < 0)
            {
                yTop = 0;
            }

            // 行带内金星簇（固定坐标行的 y 锚定：预览条 y 66-189 不在
            // 任何固定行带内，自然排除——2026-08-07 上次误判的根因）
            var clusters = DetectGoldClustersInRow(
                frame,
                yTop,
                Math.Min(yBottom, frame.Height - 1));
            Console.WriteLine(
                $"[AlignSlotColumns] row y={row.Key} band={yTop}-{Math.Min(yBottom, frame.Height - 1)} " +
                $"clusters={clusters.Count}: [{string.Join(",", clusters.Select(c => $"{c.Center}w{c.Width}"))}]");
            if (clusters.Count == 0)
            {
                result.AddRange(slotsInRow);
                continue;
            }

            // 按序映射 + 边缘噪声过滤（2026-08-07 终版）：
            // - 过滤 x 5%-95% 帧宽外的噪声簇（3-7 帧左侧 95w87 噪声）
            // - 簇按 x 升序与槽位按 x 升序一一映射（000036 银狼 639 正确
            //   对应 Front#0，而非"最近"的 984 三月七）
            // - 簇数 >= 槽位数*0.6 才启用（防少量噪声簇误映射）
            var usable = clusters
                .Where(c => c.Center > frame.Width * 0.05 &&
                            c.Center < frame.Width * 0.95)
                .OrderBy(c => c.Center)
                .ToArray();
            if (usable.Length >= Math.Max(2, slotsInRow.Length * 3 / 5))
            {
                for (var i = 0; i < slotsInRow.Length; i++)
                {
                    var slot = slotsInRow[i];
                    if (i < usable.Length)
                    {
                        var cluster = usable[i];
                        var slotCenterX = slot.X + slot.Width / 2;
                        var shift = cluster.Center - slotCenterX;
                        if (Math.Abs(shift) <= slot.Width * 4)
                        {
                            result.Add(slot with
                            {
                                X = Math.Max(0, slot.X + shift)
                            });
                            continue;
                        }
                    }

                    result.Add(slot);
                }
            }
            else
            {
                result.AddRange(slotsInRow);
            }

        }

        return result;
    }

    internal static IReadOnlyList<(int Center, int Width)> DetectGoldClustersInRow(
        CaptureFrame frame,
        int yTop,
        int yBottom)
    {
        var clusters = new List<(int, int)>();
        var active = new bool[frame.Width];
        for (var y = yTop; y <= yBottom; y++)
        {
            var rowOffset = y * frame.Stride;
            for (var x = 0; x < frame.Width; x++)
            {
                if (active[x])
                {
                    continue;
                }

                var offset = rowOffset + x * 4;
                var b = frame.BgraPixels[offset];
                var g = frame.BgraPixels[offset + 1];
                var r = frame.BgraPixels[offset + 2];
                if (r >= GoldMinR && g >= GoldMinG && b <= GoldMaxB &&
                    r - b >= 70)
                {
                    active[x] = true;
                }
            }
        }

        var cx = 0;
        while (cx < active.Length)
        {
            if (!active[cx])
            {
                cx++;
                continue;
            }

            var start = cx;
            var end = cx;
            while (cx < active.Length &&
                   (active[cx] || cx - end <= 25))
            {
                if (active[cx])
                {
                    end = cx;
                }

                cx++;
            }

            var width = end - start;
            if (width >= 15 && width <= 200)
            {
                clusters.Add(((start + end) / 2, width));
            }
        }

        return clusters;
    }
}
