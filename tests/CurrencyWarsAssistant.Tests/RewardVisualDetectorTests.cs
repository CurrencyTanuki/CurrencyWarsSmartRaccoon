using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

public sealed class RewardVisualDetectorTests
{
    [Fact]
    public void DetectsFourMineBallsOnPreparationReplay()
    {
        var frame = LoadFrame("preparation_1_2.jpg");

        var mines = new RewardVisualDetector().FindMineBalls(frame);

        Assert.Equal(4, mines.Count);
        Assert.All(mines, point =>
        {
            Assert.InRange(point.X, 1270, 1660);
            Assert.InRange(point.Y, 165, 555);
        });
    }

    [Fact]
    public void DistinguishesGrayDisabledFromGoldEnabledAutoBattleToggle()
    {
        var frame = LoadFrame("battle_1_1.jpg");
        var detector = new RewardVisualDetector();

        var replay = detector.ReadAutoBattleState(frame);
        Assert.NotEqual(AutoBattleVisualState.Enabled, replay.State);
        Assert.True(replay.GoldPixels < replay.RequiredGoldPixels);

        var disabledOnOrangeBackground = SyntheticBattleFrame(
            iconColor: (B: (byte)140, G: (byte)140, R: (byte)140),
            backgroundColor: (B: (byte)70, G: (byte)150, R: (byte)220));
        Assert.False(detector.IsAutoBattleEnabled(disabledOnOrangeBackground));

        var activeIcon = SyntheticBattleFrame(
            iconColor: (B: (byte)20, G: (byte)190, R: (byte)245),
            backgroundColor: (B: (byte)30, G: (byte)30, R: (byte)30));
        Assert.True(detector.IsAutoBattleEnabled(activeIcon));
    }

    [Fact]
    public void YellowBattleEffectsDoNotEnableGrayAutoBattleToggle()
    {
        var frame = LoadFrame(
            "reward_battle_yellow_effect_auto_disabled_2559x1439.png");

        var reading = new RewardVisualDetector().ReadAutoBattleState(frame);

        Assert.NotEqual(AutoBattleVisualState.Enabled, reading.State);
    }

    private static CaptureFrame SyntheticBattleFrame(
        (byte B, byte G, byte R) iconColor,
        (byte B, byte G, byte R) backgroundColor)
    {
        const int width = 1920;
        const int height = 1080;
        var pixels = new byte[width * height * 4];
        for (var y = 20; y < 95; y++)
        {
            for (var x = 1715; x < 1820; x++)
            {
                SetPixel(
                    pixels,
                    width,
                    x,
                    y,
                    backgroundColor.B,
                    backgroundColor.G,
                    backgroundColor.R);
            }
        }

        for (var y = 40; y < 58; y++)
        {
            for (var x = 1745; x < 1775; x++)
            {
                SetPixel(
                    pixels,
                    width,
                    x,
                    y,
                    iconColor.B,
                    iconColor.G,
                    iconColor.R);
            }
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }

    private static void SetPixel(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte blue,
        byte green,
        byte red)
    {
        var index = (y * width + x) * 4;
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
        pixels[index + 3] = 255;
    }

    private static CaptureFrame LoadFrame(string file)
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
        var path = Path.Combine(
            repositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file);
        using var bgr = Cv2.ImRead(path, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Cols,
            bgra.Rows,
            checked(bgra.Cols * 4),
            pixels,
            new PixelRect(0, 0, bgra.Cols, bgra.Rows),
            DateTimeOffset.UtcNow);
    }

}
