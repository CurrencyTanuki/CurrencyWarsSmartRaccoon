using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

// 诊断：011854 缇宝 Front slot1，固定3件坐标 0.03/0.39/0.75 下，对比默认槽宽与
// 窄槽宽(逐图标)的 dHash 命中。目标：3 件（胜利之旗/追逐星尘/胜利之旗）全命中。
public sealed class TibaoNarrowProbe
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

        var card = new PixelRect(827, 329, 122, 140);
        // 图标中心（reference→pixel via 1920 base; 2559 frame）
        double sx = frame.Width / 1920d, sy = frame.Height / 1080d;
        // 3 图标中心（胜利之旗≈x1129, 追逐星尘≈x1182, 胜利之旗≈x1239）@2559
        var allSb = new System.Text.StringBuilder();
        double[] starts = { 0.03, 0.39, 0.75 };
        foreach (var wFrac in new[] { 0.33, 0.30, 0.26 })
        {
            allSb.AppendLine($"==== 槽宽={wFrac} ====");
            for (var i = 0; i < 3; i++)
            {
                var slotW = card.Width * wFrac;
                var xStart = card.X + card.Width * starts[i];
                var yC = card.Y + card.Height * 1.08;
                var slot = new NormalizedRect(
                    xStart / 1920d,
                    (yC - card.Height * 0.26 / 2) / 1080d,
                    slotW / 1920d,
                    card.Height * 0.26 / 1080d);
                var res = iconRecognizer.RecognizeEquipmentSlotByDHash(frame, "advanced-equipment", slot, iconTemplates);
                allSb.AppendLine($"  槽{i} x={(int)(xStart * sx)}+{(int)(slotW * sx)}  -> id={res.TemplateId ?? "-"} conf={res.Confidence:F3} known={res.IsKnown}");
            }
        }
        System.Console.WriteLine(allSb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "NARROW.txt"), allSb.ToString());
    }
}
