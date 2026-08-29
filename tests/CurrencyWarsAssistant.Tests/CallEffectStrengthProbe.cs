using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 诊断：复刻 MeasureCallEffectStrength（应援检测）对 010639/011854 两帧
// 霍霍位（后台 abs4，6 格 [0]）量化左右蓝棒比，验证霍霍位是因蓝色能量
// 地块特效被误判为应援、而正常帧不误判。
public sealed class CallEffectStrengthProbe
{
    static readonly string[] INPUT_PATHS =
    {
        @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-010639.995866-000008.png",
        @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png",
    };

    // 6 格后台霍霍位 abs4 = BackCharacterSlots1920(6)[0] = (533,600,130,145) 参考系
    static readonly PixelRect HuohuoSlotRef = new(533, 600, 130, 145);

    [Fact]
    public void Run()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var path in INPUT_PATHS)
        {
            var frame = CaptureFrameLoader.LoadFile(path);
            var bounds = ScaleReferenceBounds(frame, HuohuoSlotRef);
            var (left, right) = MeasureCallEffectStrength(frame, bounds);
            sb.AppendLine($"{System.IO.Path.GetFileName(path)} ({frame.Width}x{frame.Height})");
            sb.AppendLine($"  霍霍位原帧bounds=({bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height})");
            sb.AppendLine($"  左蓝棒比={left:F3}  右蓝棒比={right:F3}  Det={Math.Min(left, right):F3} (>=0.10 判应援)");
        }
        var outTxt = sb.ToString();
        System.Console.WriteLine(outTxt);
        var outDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cwbackdiag");
        System.IO.Directory.CreateDirectory(outDir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "CALLEFFECT.txt"), outTxt);
    }

    // 与 MeasureCallEffectStrength 一致：左右两侧 20% 宽、卡高 40%-95% 区域，亮蓝棒判定
    static (double Left, double Right) MeasureCallEffectStrength(CaptureFrame frame, PixelRect bounds)
    {
        var left = new PixelRect(
            bounds.X,
            bounds.Y + (int)(bounds.Height * 0.40),
            System.Math.Max(2, (int)(bounds.Width * 0.20)),
            (int)(bounds.Height * 0.55));
        var right = new PixelRect(
            bounds.Right - System.Math.Max(2, (int)(bounds.Width * 0.20)),
            bounds.Y + (int)(bounds.Height * 0.40),
            System.Math.Max(2, (int)(bounds.Width * 0.20)),
            (int)(bounds.Height * 0.55));
        var leftRatio = ColoredPixelRatio(frame, left, static (b, g, r) => b > 140 && b >= r + 40 && (r + g + b) / 3 > 110);
        var rightRatio = ColoredPixelRatio(frame, right, static (b, g, r) => b > 140 && b >= r + 40 && (r + g + b) / 3 > 110);
        return (leftRatio, rightRatio);
    }

    static PixelRect ScaleReferenceBounds(CaptureFrame frame, PixelRect rb) => new(
        (int)System.Math.Round(rb.X * frame.Width / 1920d),
        (int)System.Math.Round(rb.Y * frame.Height / 1080d),
        System.Math.Max(1, (int)System.Math.Round(rb.Width * frame.Width / 1920d)),
        System.Math.Max(1, (int)System.Math.Round(rb.Height * frame.Height / 1080d)));

    static double ColoredPixelRatio(CaptureFrame frame, PixelRect region, Func<byte, byte, byte, bool> predicate)
    {
        var total = 0;
        var hit = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            if (y < 0 || y >= frame.Height) continue;
            for (var x = region.X; x < region.Right; x++)
            {
                if (x < 0 || x >= frame.Width) continue;
                total++;
                var offset = y * frame.Stride + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                if (predicate(blue, green, red)) hit++;
            }
        }
        return total == 0 ? 0 : (double)hit / total;
    }
}
