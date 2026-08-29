namespace CurrencyWarsAssistant.Vision;

public static class GameAspectRatio
{
    public const string InvalidAspectRatioMessage =
        "当前游戏画面不是16:9，请调整云游戏画面比例，可尝试在显示设置中更改屏幕分辨率。";

    public static bool IsSixteenByNine(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // A one-pixel error is the largest error that integer layout and
        // DPI rounding may introduce. Larger deviations are rejected.
        var expectedHeight = width * 9d / 16d;
        return Math.Abs(height - expectedHeight) <= 1d;
    }
}
