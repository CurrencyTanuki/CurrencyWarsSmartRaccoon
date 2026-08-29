using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

// 双路合并验证：段边界slot + 固定3件slot 各自 dHash conf≥0.90，按 x 中心合并去重。
// 目标：缇宝(固定中080) 与 阿格莱雅(段中051) 都不漏，0件/1件角色不误检，无重复。
public sealed class TibaoDualProbe
{
    [Fact]
    public void Run()
    {
        var path = @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png";
        var frame = CaptureFrameLoader.LoadFile(path);
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repo, "data", "4.4");
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var iconTemplates = Phase2IconTemplateCatalog.Load(dataDirectory);

        var prep = Phase2RecognitionRegions.PreparationCharacterSlots1920;
        var back = Phase2RecognitionRegions.BackCharacterSlots1920(6);
        var roles = new (string name, int zone, PixelRect card)[]
        {
            ("阿格莱雅F0", 0, prep[0]),
            ("缇宝F1", 0, prep[1]),
            ("星期日F2", 0, prep[2]),
            ("风堇F3", 0, prep[3]),
            ("藿藿B4", 1, back[0]),
            ("千冶刃B5", 1, back[1]),
        };

        var sb = new System.Text.StringBuilder();
        foreach (var (name, isBack, card) in roles)
        {
            // 段边界 slot（段检测在带内）
            var bandPx = Phase2RecognitionRegions.CharacterEquipmentIconBand(card).ToPixels(frame.Width, frame.Height);
            var segList = LocateSegs(frame, bandPx);
            var segSlots = segList.Select(s => new NormalizedRect(s.X / (double)frame.Width, s.Y / (double)frame.Height, Math.Clamp(s.Width + 2, 1, frame.Width) / (double)frame.Width, Math.Clamp(s.Height + 2, 1, frame.Height) / (double)frame.Height));

            // 固定3件坐标
            double[] starts = isBack == 1 ? new[] { 0.03, 0.34, 0.68 } : new[] { 0.03, 0.39, 0.72 };
            var fixedSlots = Enumerable.Range(0, 3).Select(i =>
            {
                var slotW = card.Width * 0.26;
                var xStart = card.X + card.Width * starts[i];
                var yC = card.Y + card.Height * 1.08;
                return new NormalizedRect(xStart / 1920d, (yC - card.Height * 0.26 / 2) / 1080d, slotW / 1920d, card.Height * 0.26 / 1080d);
            });

            // 双路匹配
            var hits = new List<(double cx, string id, double conf)>();
            foreach (var s in segSlots.Concat(fixedSlots))
            {
                var res = iconRecognizer.RecognizeEquipmentSlotByDHash(frame, "advanced-equipment", s, iconTemplates);
                if (res.IsKnown && res.Confidence >= 0.90)
                {
                    var px = s.ToPixels(frame.Width, frame.Height);
                    hits.Add((px.X + px.Width / 2.0, res.TemplateId!, res.Confidence));
                }
            }
            // 按 x 中心去重合并（相邻 <30px 合并）
            var merged = new List<(string id, double conf)>();
            foreach (var h in hits.OrderBy(h => h.cx))
            {
                if (merged.Count == 0 || h.cx - LastCx(hits, h.cx) > 30) { /* noop */ }
            }
            // 简单去重：按 cx 聚合，同 cx 邻域保留 conf 最高
            var ordered = hits.OrderBy(h => h.cx).ToList();
            var finalHits = new List<(double cx, string id, double conf)>();
            foreach (var h in ordered)
            {
                var last = finalHits.LastOrDefault();
                if (last.id != null && Math.Abs(h.cx - last.cx) <= 30)
                {
                    // 相邻，保留 conf 高者
                    if (h.conf > last.conf) finalHits[finalHits.Count - 1] = (h.cx, h.id, h.conf);
                }
                else finalHits.Add(h);
            }
            sb.AppendLine($"{name} -> " + string.Join(" | ", finalHits.Select(h => $"{h.id}:{h.conf:F2}@x{h.cx:F0}")));
        }
        System.Console.WriteLine(sb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "DUAL.txt"), sb.ToString());
    }

    static double LastCx(List<(double cx, string id, double conf)> l, double cx) => 0;

    static IReadOnlyList<PixelRect> LocateSegs(CaptureFrame frame, PixelRect band)
    {
        // 复制 LocateEquipmentSegments 的非蓝段逻辑
        var width = band.Width; var height = band.Height;
        var nonBlue = new int[width];
        for (var y = 0; y < height; y++)
        {
            var ro = (band.Y + y) * frame.Stride + band.X * 4;
            for (var x = 0; x < width; x++)
            {
                var o = ro + x * 4;
                var b = frame.BgraPixels[o]; var g = frame.BgraPixels[o + 1]; var r = frame.BgraPixels[o + 2];
                if (!(b > r + 25 && b > g + 10 && b > 70)) nonBlue[x]++;
            }
        }
        double thr = System.Math.Max(2, height * 0.30);
        var segs = new List<PixelRect>();
        var inSeg = false; var start = 0;
        for (var x = 0; x < width; x++)
        {
            var nb = nonBlue[x] >= thr;
            if (!inSeg && nb) { start = x; inSeg = true; }
            else if (inSeg && !nb)
            {
                if (x - start >= 8) segs.Add(new PixelRect(band.X + start, band.Y, x - start, height));
                inSeg = false;
            }
        }
        if (inSeg && width - start >= 8) segs.Add(new PixelRect(band.X + start, band.Y, width - start, height));
        return segs;
    }
}
