using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// GdiGameCapture 云游戏坏帧检测与 PrintWindow 回退的判定逻辑测试。
/// 特征依据 docs/HANDOFF_20260804.md：云游戏坏帧为中间深灰 RGB(32,32,32)、
/// 边缘残留 UI；原阈值 24 判不出（32>24），放宽到 60 后能触发回退。
/// </summary>
public sealed class GdiGameCaptureBlackFrameTests
{
    [Fact]
    public void SolidDeepGrayFrameIsBlack()
    {
        // 云游戏坏帧：中心深灰 RGB(32,32,32)。
        var pixels = CreateFrame(
            width: 320,
            height: 180,
            r: 32,
            g: 32,
            b: 32);

        Assert.True(GdiGameCapture.IsEffectivelyBlack(
            pixels,
            width: 320,
            height: 180));
    }

    [Fact]
    public void SolidBlackFrameIsBlack()
    {
        var pixels = CreateFrame(
            width: 320,
            height: 180,
            r: 0,
            g: 0,
            b: 0);

        Assert.True(GdiGameCapture.IsEffectivelyBlack(
            pixels,
            width: 320,
            height: 180));
    }

    [Fact]
    public void NormalBrightFrameIsNotBlack()
    {
        // 正常游戏画面：中心区域平均亮度远超 60。
        var pixels = CreateFrame(
            width: 320,
            height: 180,
            r: 128,
            g: 128,
            b: 128);

        Assert.False(GdiGameCapture.IsEffectivelyBlack(
            pixels,
            width: 320,
            height: 180));
    }

    [Fact]
    public void EdgeUiWithDarkCenterIsBlack()
    {
        // 云游戏坏帧特征：中心深灰、边缘残留 UI（亮色）。
        var pixels = CreateFrame(
            width: 320,
            height: 180,
            r: 32,
            g: 32,
            b: 32);
        PaintBorder(
            pixels,
            width: 320,
            height: 180,
            r: 200,
            g: 200,
            b: 200);

        Assert.True(GdiGameCapture.IsEffectivelyBlack(
            pixels,
            width: 320,
            height: 180));
    }

    [Fact]
    public void ThresholdBoundaryAtSixtyIsBlack()
    {
        // 阈值边界：平均 60 判为黑（<= 60）。
        var pixels = CreateFrame(
            width: 320,
            height: 180,
            r: 60,
            g: 60,
            b: 60);

        Assert.True(GdiGameCapture.IsEffectivelyBlack(
            pixels,
            width: 320,
            height: 180));
    }

    [Fact]
    public void CenterSixtyPercentOnlyIsSampled()
    {
        // 只采样中心 60%：中心暗、边缘很亮（UI）仍判黑；
        // 中心亮、边缘暗则判非黑——验证采样区域是中心而非全帧。
        var darkCenter = CreateFrame(
            width: 320,
            height: 180,
            r: 32,
            g: 32,
            b: 32);
        PaintBorder(
            darkCenter,
            width: 320,
            height: 180,
            r: 255,
            g: 255,
            b: 255);
        Assert.True(GdiGameCapture.IsEffectivelyBlack(
            darkCenter,
            width: 320,
            height: 180));

        var brightCenter = CreateFrame(
            width: 320,
            height: 180,
            r: 200,
            g: 200,
            b: 200);
        PaintBorder(
            brightCenter,
            width: 320,
            height: 180,
            r: 0,
            g: 0,
            b: 0);
        Assert.False(GdiGameCapture.IsEffectivelyBlack(
            brightCenter,
            width: 320,
            height: 180));
    }

    private static byte[] CreateFrame(
        int width,
        int height,
        byte r,
        byte g,
        byte b)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static void PaintBorder(
        byte[] pixels,
        int width,
        int height,
        byte r,
        byte g,
        byte b)
    {
        const int borderThickness = 20;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var onBorder =
                    x < borderThickness ||
                    y < borderThickness ||
                    x >= width - borderThickness ||
                    y >= height - borderThickness;
                if (!onBorder)
                {
                    continue;
                }

                var offset = (y * width + x) * 4;
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }
    }
}
