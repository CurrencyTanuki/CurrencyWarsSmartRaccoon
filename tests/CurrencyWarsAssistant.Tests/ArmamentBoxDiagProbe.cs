using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// D 步诊断：05 帧（clipboard-20260816-133831.744167-000005.png）后台/备战席
/// 武装箱格子识别——把 Bench 0 / Bench 5 两个被识成 unknown 的格子与三种
/// 武装箱模板（equipment_156 特权 / 157 简易 / 158 进阶）做多特征匹配，
/// 验证程序能否区分武装箱类型并确定二者各是哪一种。
/// </summary>
public sealed class ArmamentBoxDiagProbe(ITestOutputHelper output)
{
    [Fact]
    public void DumpSpecialItemCatalog()
    {
        var dataDir = @"D:\CWAFix-20260814\data\4.4";
        var templates = CurrencyWarsAssistant.Vision.Phase2IconTemplateCatalog
            .Load(dataDir);
        output.WriteLine($"=== catalog 里 special-item 类模板（共 {templates.Count}）===");
        foreach (var t in templates.Where(t => t.Category == "special-item"))
        {
            output.WriteLine(
                $"  id={t.Id} cat={t.Category} conf={t.MinimumConfidence:F2} " +
                $"mode={t.ComparisonMode} candidates=[{string.Join(",", t.CandidateIds ?? [])}]");
        }
    }

    [Fact]
    public void ProductRecognizeArmamentBench05()
    {
        var dataDir = @"D:\CWAFix-20260814\data\4.4";
        var catalog = CurrencyWarsAssistant.Vision.Phase2IconTemplateCatalog
            .Load(dataDir);
        var targets = new[]
        {
            "currency_wars_equipment_156",
            "currency_wars_equipment_157",
            "currency_wars_equipment_158",
            "special_item_020",
            "special_item_021",
            "special_item_022",
        };
        var tpl = catalog
            .Where(t => t.Category == "special-item")
            .Where(t => targets.Contains(t.Id))
            .Select(t => t with { Category = "bench-special-item" })
            .ToArray();
        output.WriteLine($"product templates = {tpl.Length}");
        foreach (var t in tpl)
        {
            output.WriteLine($"  {t.Id} mode={t.ComparisonMode} conf={t.MinimumConfidence} src={t.SourceConfidence}");
        }

        var framePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "PageReplay",
            "armament_box_bench_05_user_marked.png");
        if (!File.Exists(framePath))
        {
            output.WriteLine("skip: 005 帧 fixture 不存在");
            return;
        }
        var frame = CaptureFrameLoader.LoadFile(framePath);
        using var recognizer = new OpenCvPhase2IconRecognizer();
        // BenchCharacterSlots1920: 0 & 5
        var slots1920 = new[] { (0, 383, 844, 114, 137), (5, 1005, 844, 116, 137) };
        var sx = frame.Width / 1920d;
        var sy = frame.Height / 1080d;
        foreach (var (idx, x, y, w, h) in slots1920)
        {
            var full = new NormalizedRect(
                x / 1920d, y / 1080d, w / 1920d, h / 1080d);
            output.WriteLine($"=== Bench{idx} 整格 ===");
            DumpProduct(recognizer, frame, full, tpl);
            var center = new NormalizedRect(
                full.X + full.Width * 0.20,
                full.Y + full.Height * 0.20,
                full.Width * 0.60,
                full.Height * 0.60);
            output.WriteLine($"=== Bench{idx} 中心0.6 ===");
            DumpProduct(recognizer, frame, center, tpl);
        }
    }

    private void DumpProduct(
        IPhase2IconRecognizer recognizer,
        CaptureFrame frame,
        NormalizedRect region,
        IReadOnlyList<Phase2IconTemplateDefinition> templates)
    {
        var result = recognizer.Recognize(frame, "bench-special-item", [region], templates)[0];
        output.WriteLine(
            $"  known={result.IsKnown} conf={result.Confidence:F3} id={result.TemplateId}");
        var ranked = (result.RankedCandidates ?? [])
            .OrderByDescending(c => c.Confidence)
            .Take(4);
        foreach (var c in ranked)
        {
            output.WriteLine($"    {c.TemplateId}: {c.Confidence:F3} (exact={c.ResolvesExactIdentity})");
        }
    }

    [Fact]
    public void Match05FrameArmamentBoxes()
    {
        var framePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "PageReplay",
            "armament_box_bench_05_user_marked.png");
        if (!File.Exists(framePath))
        {
            output.WriteLine("missing frame");
            return;
        }

        var icons = @"D:\CWAFix-20260814\data\runtime\1.0.0\4.4\equipment\assets\currency_wars_equipment_icons";
        var frame = Cv2.ImRead(framePath, ImreadModes.Color);
        if (frame.Empty())
        {
            output.WriteLine("frame empty");
            return;
        }

        output.WriteLine($"FRAME {frame.Width}x{frame.Height}");
        var scaleX = frame.Width / 1920d;
        var scaleY = frame.Height / 1080d;

        // BenchCharacterSlots1920：0=(383,844,114,137) 5=(1005,844,116,137)
        var slots = new (string Label, int X, int Y, int W, int H)[]
        {
            ("Bench0", 383, 844, 114, 137),
            ("Bench5", 1005, 844, 116, 137),
        };

        var tplFiles = new (string Id, string Name, string File)[]
        {
            ("eq156", "特权武装箱", "currency_wars_equipment_156.png"),
            ("eq157", "简易武装箱", "currency_wars_equipment_157.png"),
            ("eq158", "进阶武装箱", "currency_wars_equipment_158.png"),
        };
        var templates = tplFiles
            .Select(t => (t.Id, t.Name, Img: Cv2.ImRead(
                Path.Combine(icons, t.File), ImreadModes.Color)))
            .Where(t => !t.Img.Empty())
            .ToArray();

        foreach (var (label, x, y, w, h) in slots)
        {
            var px = (int)Math.Round(x * scaleX);
            var py = (int)Math.Round(y * scaleY);
            var pw = (int)Math.Round(w * scaleX);
            var ph = (int)Math.Round(h * scaleY);
            using var crop = new Mat(frame, new Rect(px, py, pw, ph));
            using var crop256 = new Mat();
            Cv2.Resize(crop, crop256, new Size(256, 256), 0, 0, InterpolationFlags.Cubic);
            output.WriteLine($"=== {label} 格 crop({px},{py},{pw}x{ph}) ===");

            foreach (var (id, name, img) in templates)
            {
                var d = CompositeDistance(crop256, img);
                output.WriteLine($"  {name}({id}) composite={d}");
            }

            // 尝试只在格子内 0.6 中心匹配（武装箱图标可能居中）
            output.WriteLine($"  -- 中心 0.6 crop --");
            using var center = CenterCrop(crop256, 0.6);
            foreach (var (id, name, img) in templates)
            {
                using var ic = CenterCrop(img, 0.6);
                var d = CompositeDistance(center, ic);
                output.WriteLine($"  {name}({id}) composite-center={d}");
            }
        }
    }

    private static double CompositeDistance(Mat a, Mat b)
    {
        using var a128 = new Mat();
        using var b128 = new Mat();
        Cv2.Resize(a, a128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
        Cv2.Resize(b, b128, new Size(128, 128), 0, 0, InterpolationFlags.Cubic);
        var dhash = (double)Hamming(ComputeDHash(a128), ComputeDHash(b128));
        var hist = 1 - HistogramSimilarity(ComputeRgbHistogram(a128), ComputeRgbHistogram(b128));
        return dhash + Math.Round(hist * 100);
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

    private static int Hamming(ulong left, ulong right) =>
        System.Numerics.BitOperations.PopCount(left ^ right);

    private static double[] ComputeRgbHistogram(Mat source)
    {
        const int bins = 16;
        var histogram = new double[bins * 3];
        for (var y = 0; y < source.Rows; y++)
        {
            for (var x = 0; x < source.Cols; x++)
            {
                var p = source.At<Vec3b>(y, x);
                histogram[Math.Min(bins - 1, p.Item0 * bins / 256)]++;
                histogram[bins + Math.Min(bins - 1, p.Item1 * bins / 256)]++;
                histogram[bins * 2 + Math.Min(bins - 1, p.Item2 * bins / 256)]++;
            }
        }

        return histogram;
    }

    private static double HistogramSimilarity(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var dot = 0d;
        var ln = 0d;
        var rn = 0d;
        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            ln += left[i] * left[i];
            rn += right[i] * right[i];
        }

        return ln <= 1e-9 || rn <= 1e-9 ? 0 : dot / Math.Sqrt(ln * rn);
    }

    private static Mat CenterCrop(Mat source, double fraction)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * fraction));
        var height = Math.Max(1, (int)Math.Round(source.Height * fraction));
        var x = (source.Width - width) / 2;
        var y = (source.Height - height) / 2;
        return new Mat(source, new Rect(x, y, width, height));
    }
}
