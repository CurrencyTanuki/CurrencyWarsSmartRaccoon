using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

// 诊断：对 011854 整帧各场上角色跑「3件固定坐标逐槽 dHash + conf过滤」，
// 评估用户担心的误检/漏检：1件(星期日/藿藿)、0件(风堇/千冶刃)、3件(阿格莱雅/缇宝)。
public sealed class TibaoWholeFrameProbe
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

        // 011854 各场上角色 reference（Front=Preparation[0..3]; Back=BackCharSlots(6)[0..2]）
        var prep = Phase2RecognitionRegions.PreparationCharacterSlots1920;
        var back = Phase2RecognitionRegions.BackCharacterSlots1920(6);
        var roles = new (string name, int zone, PixelRect card)[]
        {
            ("阿格莱雅Front0", 0, prep[0]),
            ("缇宝Front1", 0, prep[1]),
            ("星期日Front2", 0, prep[2]),
            ("风堇Front3", 0, prep[3]),
            ("藿藿Back4", 1, back[0]),
            ("千冶刃Back5", 1, back[1]),
        };

        var sb = new System.Text.StringBuilder();
        foreach (var (name, isBack, card) in roles)
        {
            // 3件固定坐标
            double[] starts = isBack == 1
                ? new[] { 0.03, 0.34, 0.68 }
                : new[] { 0.03, 0.39, 0.72 };
            var hits = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var slotW = card.Width * 0.26;
                var xStart = card.X + card.Width * starts[i];
                var yC = card.Y + card.Height * 1.08;
                var slot = new NormalizedRect(
                    xStart / 1920d,
                    (yC - card.Height * 0.26 / 2) / 1080d,
                    slotW / 1920d,
                    card.Height * 0.26 / 1080d);
                var res = iconRecognizer.RecognizeEquipmentSlotByDHash(frame, "advanced-equipment", slot, iconTemplates);
                if (res.IsKnown && res.Confidence >= 0.90)
                    hits.Add($"{res.TemplateId}:{res.Confidence:F2}");
                else
                    hits.Add($"-");
            }
            sb.AppendLine($"{name}  card=({card.X},{card.Y},{card.Width}x{card.Height}) -> {string.Join(" | ", hits)}");
        }
        System.Console.WriteLine(sb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "WHOLE.txt"), sb.ToString());
    }
}
