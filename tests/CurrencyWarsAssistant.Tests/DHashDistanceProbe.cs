using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// dHash 距离诊断（精确复刻产品算法）：实机装备段 vs 157 模板的组合距离
/// top-N，对照 MaximumReliableEquipmentDHashDistance=150 阈值。
/// </summary>
public sealed class DHashDistanceProbe(ITestOutputHelper output)
{
    [Fact]
    public void DiagnoseDistances()
    {
        var assetDir = @"D:\CWAFix-20260814\data\runtime\1.0.0\4.4\equipment\assets\currency_wars_equipment_icons";
        var cases = new (string Label, string Frame, int X, int Y, int W, int H, string? Truth)[]
        {
            ("F3-生命之环(069)", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-132957.787880-000004.png", 1736, 983, 55, 46, "currency_wars_equipment_069"),
            ("F4-幸运星(043)", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-133831.744167-000005.png", 960, 616, 52, 48, "currency_wars_equipment_043"),
            ("F4-幸运星产品槽位", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-133831.744167-000005.png", 959, 615, 54, 48, "currency_wars_equipment_043"),
            ("F4-乱破装1", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-133831.744167-000005.png", 1487, 983, 52, 46, null),
            ("F4-乱破装2", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-133831.744167-000005.png", 1543, 983, 54, 46, "currency_wars_equipment_042"),
            ("F4-乱破装2左扩", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-133831.744167-000005.png", 1531, 981, 66, 50, "currency_wars_equipment_042"),
            ("F4-乱破装2右扩", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-133831.744167-000005.png", 1541, 981, 62, 50, "currency_wars_equipment_042"),
            ("F4-乱破装3", @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260816-133831.744167-000005.png", 1601, 983, 55, 46, null),
        };

        foreach (var (label, framePath, x, y, w, h, truth) in cases)
        {
            var frame = Cv2.ImRead(framePath, ImreadModes.Color);
            if (frame.Empty())
            {
                output.WriteLine($"{label}: frame missing");
                continue;
            }

            using var crop = new Mat(frame, new Rect(x, y, w, h));
            // 实验：段放大到 128x128（与模板同尺寸）再 dHash——实机小段
            //（54x46）直接缩 9x8 信息不足，放大后保留更多图案细节。
            using var crop128 = new Mat();
            Cv2.Resize(crop, crop128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
            var fullHash = ComputeDHash(crop);
            var fullHash128 = ComputeDHash(crop128);
            using var center = CenterCrop(crop, 0.6);
            var centerHash = ComputeDHash(center);
            var fullHist = ComputeRgbHistogram(crop);
            var centerHist = ComputeRgbHistogram(center);

            var results = new List<(string Id, double Distance)>();
            var results128 = new List<(string Id, double Distance)>();
            foreach (var file in Directory.GetFiles(assetDir, "*.png"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                using var tpl = Cv2.ImRead(file, ImreadModes.Color);
                using var tpl128 = new Mat();
                Cv2.Resize(tpl, tpl128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
                var distance = HammingDistance(fullHash, ComputeDHash(tpl)) +
                               HammingDistance(centerHash, ComputeDHash(CenterCrop(tpl, 0.6))) +
                               (int)Math.Round(
                                   (1 - HistogramSimilarity(fullHist, ComputeRgbHistogram(tpl))) * 100 +
                                   (1 - HistogramSimilarity(centerHist, ComputeRgbHistogram(CenterCrop(tpl, 0.6)))) * 200);
                results.Add((id, distance));
                using var tpl128Center = CenterCrop(tpl128, 0.6);
                var distance128 = HammingDistance(fullHash128, ComputeDHash(tpl128)) +
                                  HammingDistance(
                                      ComputeDHash(CenterCrop(crop128, 0.6)),
                                      ComputeDHash(tpl128Center)) +
                                  (int)Math.Round(
                                      (1 - HistogramSimilarity(ComputeRgbHistogram(crop128), ComputeRgbHistogram(tpl128))) * 100 +
                                      (1 - HistogramSimilarity(ComputeRgbHistogram(CenterCrop(crop128, 0.6)), ComputeRgbHistogram(tpl128Center))) * 200);
                results128.Add((id, distance128));
            }

            // 滑窗实验：±8（产品现状）vs ±3——每个 dx 重算与全部模板
            // 的最小距离，模拟产品滑窗行为（128 预处理版）。
            var slideResults = new List<(int Dx, string Id, double Dist)>();
            for (var dx = -8; dx <= 8; dx++)
            {
                var tx = x + dx;
                if (tx < 0 || tx + w > frame.Width)
                {
                    continue;
                }

                using var cropS = new Mat(frame, new Rect(tx, y, w, h));
                using var cropS128 = new Mat();
                Cv2.Resize(cropS, cropS128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
                var fh = ComputeDHash(cropS128);
                var ch = ComputeDHash(CenterCrop(cropS128, 0.6));
                foreach (var file in Directory.GetFiles(assetDir, "*.png"))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    using var tpl = Cv2.ImRead(file, ImreadModes.Color);
                    using var tpl128 = new Mat();
                    Cv2.Resize(tpl, tpl128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
                    var d = HammingDistance(fh, ComputeDHash(tpl128)) +
                            HammingDistance(ch, ComputeDHash(CenterCrop(tpl128, 0.6))) +
                            (int)Math.Round(
                                (1 - HistogramSimilarity(ComputeRgbHistogram(cropS128), ComputeRgbHistogram(tpl128))) * 100 +
                                (1 - HistogramSimilarity(ComputeRgbHistogram(CenterCrop(cropS128, 0.6)), ComputeRgbHistogram(CenterCrop(tpl128, 0.6)))) * 200);
                    slideResults.Add((dx, id, d));
                }
            }

            foreach (var dx in new[] { -8, -3, 0, 3, 8 })
            {
                var atDx = slideResults.Where(r => r.Dx == dx)
                    .OrderBy(r => r.Dist).Take(3).ToArray();
                if (atDx.Length == 0)
                {
                    continue;
                }

                var marks = string.Join(", ", atDx.Select(r => $"{r.Id[^3..]}:{r.Dist:F0}" +
                    (r.Id == truth ? "*" : "")));
                output.WriteLine($"  dx={dx}: {marks}");
            }

            var bestOverall = slideResults.OrderBy(r => r.Dist).First();
            output.WriteLine(
                $"  全局最优 dx={bestOverall.Dx} id={bestOverall.Id} dist={bestOverall.Dist:F0}" +
                $"{(bestOverall.Id == truth ? " <<<真值" : "")}");

            var ranked = results.OrderBy(r => r.Distance).ToArray();
            var ranked128 = results128.OrderBy(r => r.Distance).ToArray();
            output.WriteLine($"=== {label} 原始距离 ===");
            foreach (var (id, d) in ranked.Take(4))
            {
                output.WriteLine($"  {d,7:F1}  {id}");
            }

            output.WriteLine($"=== {label} 128放大距离 ===");
            foreach (var (id, d) in ranked128.Take(4))
            {
                var mark = id == truth ? " <<<真值" : "";
                output.WriteLine($"  {d,7:F1}  {id}{mark}");
            }

            if (truth is not null)
            {
                var rank = Array.FindIndex(ranked128, r => r.Id == truth) + 1;
                output.WriteLine($"  真值(128)排名 {rank}/{ranked128.Length} 距离 {ranked128[rank - 1].Distance:F1} " +
                                 $"（阈值150 内={(ranked128[rank - 1].Distance <= 150 ? "是" : "否")}）");
            }

            // pHash 对比：段/槽位 vs 039 与真值模板的 pHash 距离
            using var pcrop = new Mat(frame, new Rect(x, y, w, h));
            using var pcrop128 = new Mat();
            Cv2.Resize(pcrop, pcrop128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
            var pFull = ComputePHash(pcrop128);
            var pCenter = ComputePHash(CenterCrop(pcrop128, 0.6));
            foreach (var pid in new[] { "currency_wars_equipment_039", "currency_wars_equipment_043", "currency_wars_equipment_069", "currency_wars_equipment_077" })
            {
                var pf = Path.Combine(assetDir, pid + ".png");
                if (!File.Exists(pf))
                {
                    continue;
                }

                using var ptpl = Cv2.ImRead(pf, ImreadModes.Color);
                using var ptpl128 = new Mat();
                Cv2.Resize(ptpl, ptpl128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
                var pd = HammingDistance(pFull, ComputePHash(ptpl128)) +
                         HammingDistance(pCenter, ComputePHash(CenterCrop(ptpl128, 0.6)));
                output.WriteLine($"  pHash {pid[^3..]}: {pd}" + (pid == truth ? " <<<真值" : ""));
            }
        }
    }

    private static ulong ComputeDHash(Mat source)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(9, 8), 0, 0, InterpolationFlags.Area);
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (small.At<byte>(y, x + 1) > small.At<byte>(y, x))
                {
                    hash |= 1UL << (y * 8 + x);
                }
            }
        }

        return hash;
    }

    private static int HammingDistance(ulong left, ulong right) =>
        System.Numerics.BitOperations.PopCount(left ^ right);

    private static double[] ComputeRgbHistogram(Mat source)
    {
        const int bins = 16;
        var histogram = new double[bins * 3];
        for (var y = 0; y < source.Rows; y++)
        {
            for (var x = 0; x < source.Cols; x++)
            {
                var pixel = source.At<Vec3b>(y, x);
                histogram[Math.Min(bins - 1, pixel.Item0 * bins / 256)]++;
                histogram[bins + Math.Min(bins - 1, pixel.Item1 * bins / 256)]++;
                histogram[bins * 2 + Math.Min(bins - 1, pixel.Item2 * bins / 256)]++;
            }
        }

        return histogram;
    }

    private static Mat CenterCrop(Mat source, double fraction)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * fraction));
        var height = Math.Max(1, (int)Math.Round(source.Height * fraction));
        var x = (source.Width - width) / 2;
        var y = (source.Height - height) / 2;
        return new Mat(source, new Rect(x, y, width, height));
    }

    private static ulong ComputePHash(Mat source)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(32, 32), 0, 0, InterpolationFlags.Cubic);
        using var floatMat = new Mat();
        small.ConvertTo(floatMat, MatType.CV_32F);
        using var dct = new Mat();
        Cv2.Dct(floatMat, dct);
        var vals = new double[64];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                vals[y * 8 + x] = dct.At<float>(y, x);
            }
        }

        var median = vals.OrderBy(v => v).ElementAt(31);
        ulong hash = 0;
        for (var i = 0; i < 64; i++)
        {
            if (vals[i] > median)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    private static double HistogramSimilarity(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        return leftNorm <= double.Epsilon || rightNorm <= double.Epsilon
            ? 0
            : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
