using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Vision;

public static class CoordinateMapper
{
    public static PixelPoint ClientToScreen(GameWindowInfo window, PixelPoint clientPoint) =>
        new(
            checked(window.ClientArea.X + clientPoint.X),
            checked(window.ClientArea.Y + clientPoint.Y));

    public static PixelPoint NormalizedToScreen(
        GameWindowInfo window,
        double normalizedX,
        double normalizedY)
    {
        var clientX = (int)Math.Round(normalizedX * window.ClientArea.Width);
        var clientY = (int)Math.Round(normalizedY * window.ClientArea.Height);
        return ClientToScreen(window, new PixelPoint(clientX, clientY));
    }
}
