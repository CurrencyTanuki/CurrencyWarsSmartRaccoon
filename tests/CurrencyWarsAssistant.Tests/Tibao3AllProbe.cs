using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

// 诊断：011854 缇宝固定3件，窄槽宽 0.26/0.28，搜索第3件起点，找 3 件全命中。
public sealed class Tibao3AllProbe
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

        var sb = new System.Text.StringBuilder();
        foreach (var wFrac in new[] { 0.26, 0.28 })
        {
            double[] s3 = { 0.72, 0.75, 0.78 };
            foreach (var f3 in s3)
            {
                double[] starts = { 0.03, 0.39, f3 };
                sb.AppendLine($"==== 槽宽={wFrac} f3={f3} ====");
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
                    sb.AppendLine($"  槽{i}  -> id={res.TemplateId ?? "-"} conf={res.Confidence:F3}");
                }
            }
        }
        System.Console.WriteLine(sb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "ALL3.txt"), sb.ToString());
    }
}
