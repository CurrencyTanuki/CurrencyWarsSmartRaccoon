using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

// 诊断：011854 缇宝 Front slot1 装备带，用彩色连通域(LocateEquipmentIcons)计数，
// 对比非蓝段(LocatEquipmentSegments)。判断追逐星尘(蓝色系)能否被彩色连通域检出为第3件。
public sealed class TibaoConnectivityProbe
{
    [Fact]
    public void Run()
    {
        var path = @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png";
        var frame = CaptureFrameLoader.LoadFile(path);
        var band = new PixelRect(1107, 616, 179, 48);

        // LocateEquipmentIcons 对带内彩色连通域
        var icons = Phase2OperationalScreenshotAnalyzer.DebugLocateEquipmentIcons(frame, band);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==== 011854 缇宝带 彩色连通域(LocateEquipmentIcons) ====");
        sb.AppendLine($"band=({band})  图标数={icons.Count}");
        foreach (var i in icons.OrderBy(i => i.X))
        {
            sb.AppendLine($"  icon x={i.X}+{i.Width} y={i.Y}+{i.Height} 中心x={i.X + i.Width / 2.0:F0}");
        }
        System.Console.WriteLine(sb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "CONN.txt"), sb.ToString());
    }
}
