namespace CurrencyWarsAssistant.Core;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public PixelPoint Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public PixelRect ToPixels(int width, int height)
    {
        static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

        var x = Clamp((int)Math.Round(X * width), 0, width);
        var y = Clamp((int)Math.Round(Y * height), 0, height);
        var right = Clamp((int)Math.Round((X + Width) * width), x, width);
        var bottom = Clamp((int)Math.Round((Y + Height) * height), y, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }
}
