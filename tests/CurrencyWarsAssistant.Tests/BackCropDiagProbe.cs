using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace CurrencyWarsAssistant.Tests;

/// <summary>后台区域诊断：把 run 帧 5/6/7/8/9 格后台框画到原图上存 PNG，供用户看卡牌位置。</summary>
public sealed class BackCropDiagProbe
{
    [Fact]
    public void DumpBackCrops()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var screens = Path.Combine(appData, "CurrencyWarsSmartRaccoon", "runs",
            "run-20260818-163242", "screenshots");
        var outDir = Path.Combine(Path.GetTempPath(), "cwbackdiag");
        Directory.CreateDirectory(outDir);
        foreach (var shot in new[] { "20260818-084639619.png", "20260818-084211147.png" })
        {
            var frame = CaptureFrameLoader.LoadFile(Path.Combine(screens, shot));
            using var raw = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
            Marshal.Copy(frame.BgraPixels, 0, raw.Data, frame.BgraPixels.Length);
            foreach (var count in new[] { 5, 6, 7, 8, 9 })
            {
                using var canvas = raw.Clone();
                foreach (var r in Phase2RecognitionRegions.BackCharacterSlots1920(count))
                {
                    Cv2.Rectangle(canvas,
                        new OpenCvSharp.Rect(r.X, r.Y, r.Width, r.Height),
                        new Scalar(0, 255, 0), 3);
                }
                Cv2.ImWrite(Path.Combine(outDir, $"{shot.Split('.')[0]}_back{count}.png"), canvas);
            }
        }
        System.IO.File.WriteAllText(Path.Combine(outDir, "DONE.txt"), "ok");
    }
}
