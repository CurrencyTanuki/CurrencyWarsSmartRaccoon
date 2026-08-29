using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;
using System.Reflection;

namespace CurrencyWarsAssistant.Tests;

// 诊断：011854 缇宝 Front slot1 (card 827,329,122,140) 的装备带非蓝段检测
// 原始段 + MergeAdjacentSegments 后段 —— 定位"追逐星辰种类 conf0.00 / 3件被他并成1段"。
public sealed class TibaoSegmentDiagProbe
{
    [Fact]
    public void Run()
    {
        var path = @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png";
        var frame = CaptureFrameLoader.LoadFile(path);

        // 缇宝 Front slot1 参考卡 (PreparationCharacterSlots1920[1])
        var card = new PixelRect(827, 329, 122, 140);
        var bandPx = Phase2RecognitionRegions.CharacterEquipmentIconBand(card)
            .ToPixels(frame.Width, frame.Height);

        // 反射调用 private LocateEquipmentSegments / MergeAdjacentSegments / CharacterEquipmentIconBand
        var t = typeof(Phase2OperationalScreenshotAnalyzer);
        var locate = t.GetMethod("LocateEquipmentSegments",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var merge = t.GetMethod("MergeAdjacentSegments",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var raw = (IReadOnlyList<PixelRect>)locate.Invoke(null, new object[] { frame, bandPx })!;
        var merged = (IReadOnlyList<PixelRect>)merge.Invoke(null, new object[] { raw })!;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==== 011854 缇宝 Front slot1 装备带段诊断 ====");
        sb.AppendLine($"card=({card})  band=({bandPx}) frame=({frame.Width}x{frame.Height})");
        sb.AppendLine($"原始段数={raw.Count}: " + string.Join(";", raw.Select(s => $"x{s.X}+{s.Width}y{s.Y}+{s.Height}")));
        sb.AppendLine($"合并段数={merged.Count}: " + string.Join(";", merged.Select(s => $"x{s.X}+{s.Width}y{s.Y}+{s.Height}")));

        System.Console.WriteLine(sb);
        var outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag");
        System.IO.Directory.CreateDirectory(outDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "SEGDIAG.txt"), sb.ToString());
    }
}
