using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

// 诊断：011854 缇宝 Front slot1（reference 827,329,122,140 → 固定3件 0.03/0.39/0.75），
// 对每个固定槽 dHash 匹配，验证 3 件(胜利之旗/追逐星尘/胜利之旗)能否都命中，
// 确认固定位置方案(独立于段检测)可行。
public sealed class TibaoFixed3Probe
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

        // 缇宝 Front slot1 reference (1920/1080 系)
        var card = new PixelRect(827, 329, 122, 140);
        double[] startFractions = { 0.03, 0.39, 0.75 };
        var slots = startFractions.Select(f => new NormalizedRect(
                (card.X + card.Width * f) / 1920d,
                (card.Y + card.Height * 1.08 - card.Height * 0.26 / 2) / 1080d,
                card.Width * 0.33 / 1920d,
                card.Height * 0.26 / 1080d))
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==== 011854 缇宝固定3件槽 dHash 匹配 ====");
        sb.AppendLine($"card=({card})");
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var px = slot.ToPixels(frame.Width, frame.Height);
            var res = iconRecognizer.RecognizeEquipmentSlotByDHash(frame, "advanced-equipment", slot, iconTemplates);
            sb.AppendLine($"  槽{i} x={px.X}+{px.Width}  -> id={res.TemplateId ?? "-"} conf={res.Confidence:F3} known={res.IsKnown}");
        }
        System.Console.WriteLine(sb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "FIXED3.txt"), sb.ToString());
    }
}
