using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 诊断：011854 缇宝 Front slot1 装备带(y616-664, x1107-1286)逐列非蓝像素分布，
// 定位 3 件图标（胜利之旗/追逐星尘/胜利之旗）各自 x 峰。
public sealed class TibaoColumnProbe
{
    [Fact]
    public void Run()
    {
        var path = @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png";
        var frame = CaptureFrameLoader.LoadFile(path);
        var band = new PixelRect(1107, 616, 179, 48); // 段诊断实测带

        var sb = new System.Text.StringBuilder();
        double threshold = 48 * 0.30; // LocateEquipmentSegments 阈值

        // 每列非蓝像素数
        var nonBlue = new int[band.Width];
        for (var y = 0; y < band.Height; y++)
        {
            var rowOff = (band.Y + y) * frame.Stride + band.X * 4;
            for (var x = 0; x < band.Width; x++)
            {
                var o = rowOff + x * 4;
                var b = frame.BgraPixels[o]; var g = frame.BgraPixels[o + 1]; var r = frame.BgraPixels[o + 2];
                var isBlue = b > r + 25 && b > g + 10 && b > 70;
                if (!isBlue) nonBlue[x]++;
            }
        }

        // 输出列分布（每 5 列一组摘要 + 超阈值列）
        sb.AppendLine("column:ratio(over threshold " + threshold.ToString("F1") + " => icon)");
        var inIcon = false;
        var iconStart = 0;
        for (var x = 0; x < band.Width; x++)
        {
            var over = nonBlue[x] >= threshold;
            var mark = over ? "#" : ".";
            if (!inIcon && over) { iconStart = x; inIcon = true; }
            else if (inIcon && !over)
            {
                sb.AppendLine($"  icon x={band.X + iconStart}..{band.X + x - 1} (width={x - iconStart})");
                inIcon = false;
            }
        }
        if (inIcon) sb.AppendLine($"  icon x={band.X + iconStart}..{band.X + band.Width - 1} (width={band.Width - iconStart})");

        // 合并相邻（gap<25）
        sb.AppendLine("原始图标段: " + sb.ToString().Split('\n').Where(l => l.Contains("icon x=")).ToArray().Length);
        System.Console.WriteLine(sb.ToString());
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag", "COLUMN.txt"), sb.ToString());
    }
}
