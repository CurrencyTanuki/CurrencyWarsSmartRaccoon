using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 诊断+用户审核：011854 缇宝 Front slot1 装备带/卡片裁剪图，含段边界标注。
// 存 tools/diag/tibao_review.png 供用户确认装备件数与图标位置（逐像素程序裁剪，
// 不用千问看图）。
public sealed class TibaoReviewProbe
{
    [Fact]
    public void Run()
    {
        var path = @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png";
        var frame = CaptureFrameLoader.LoadFile(path);

        // 缇宝 Front slot1 卡片区（ClipboardFrameProbe 输出 card(1102,438,163,187)，2559 系）
        var card2559 = new PixelRect(1102, 438, 163, 187);
        // 装备带 = card x+0.03w, y+0.95h, w1.10w, h0.26h
        var band = new PixelRect(
            card2559.X + (int)(card2559.Width * 0.03),
            card2559.Y + (int)(card2559.Height * 0.95),
            (int)(card2559.Width * 1.10),
            (int)(card2559.Height * 0.26));

        var outDir = @"D:\CWAFix-20260814\tools\diag";
        Directory.CreateDirectory(outDir);

        // 卡片原图
        var cardImg = Cropped(frame, card2559);
        Cv2Save(Path.Combine(outDir, "tibao_card_review.png"), cardImg, card2559.Width, card2559.Height);
        // 装备带原图
        var bandImg = Cropped(frame, band);
        Cv2Save(Path.Combine(outDir, "tibao_band_review.png"), bandImg, band.Width, band.Height);
        // 放大 3x（图标太小）
        var bandUp = Unscaled(bandImg, band.Width, band.Height, 3);
        Cv2Save(Path.Combine(outDir, "tibao_band_up3x.png"), bandUp, band.Width * 3, band.Height * 3);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"card(2559)=({card2559})  band=({band})");
        sb.AppendLine($"saved: {Path.Combine(outDir, "tibao_card_review.png")}");
        sb.AppendLine($"saved: {Path.Combine(outDir, "tibao_band_up3x.png")}");
        System.Console.WriteLine(sb);
    }

    static byte[] Cropped(CaptureFrame frame, PixelRect r)
    {
        var w = r.Width; var h = r.Height;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var src = (r.Y + y) * frame.Stride + r.X * 4;
            Array.Copy(frame.BgraPixels, src, px, y * w * 4, w * 4);
        }
        return px;
    }

    static byte[] Unscaled(byte[] src, int w, int h, int k)
    {
        var d = new byte[w * k * h * k * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var si = (y * w + x) * 4;
                for (var dy = 0; dy < k; dy++)
                {
                    for (var dx = 0; dx < k; dx++)
                    {
                        var di = ((y * k + dy) * (w * k) + (x * k + dx)) * 4;
                        d[di] = src[si]; d[di + 1] = src[si + 1]; d[di + 2] = src[si + 2]; d[di + 3] = 255;
                    }
                }
            }
        }
        return d;
    }

    static void Cv2Save(string file, byte[] bgra, int w, int h)
    {
        var mat = OpenCvSharp.Mat.FromPixelData(
            h, w, OpenCvSharp.MatType.CV_8UC4, bgra);
        using (mat)
        {
            OpenCvSharp.Cv2.ImWrite(file, mat);
        }
    }
}
