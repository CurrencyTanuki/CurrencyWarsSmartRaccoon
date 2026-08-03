using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CurrencyWarsAssistant.Vision;

public sealed class GdiGameCapture : IGameCapture
{
    private const int SourceCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint DibRgbColors = 0;

    public ValueTask<CaptureFrame> CaptureAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var area = window.ClientArea;
        if (area.IsEmpty)
        {
            throw new InvalidOperationException("游戏客户区尺寸无效。");
        }

        var screenDc = GetDC(0);
        if (screenDc == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取屏幕设备上下文。");
        }

        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, area.Width, area.Height);
        var previous = SelectObject(memoryDc, bitmap);

        try
        {
            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    area.Width,
                    area.Height,
                    screenDc,
                    area.X,
                    area.Y,
                    SourceCopy | CaptureBlt))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "截取游戏画面失败。");
            }

            var stride = checked(area.Width * 4);
            var pixels = new byte[checked(stride * area.Height)];
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = area.Width,
                    Height = -area.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };

            var copied = GetDIBits(
                memoryDc,
                bitmap,
                0,
                (uint)area.Height,
                pixels,
                ref bitmapInfo,
                DibRgbColors);
            if (copied != area.Height)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "读取截图像素失败。");
            }

            return ValueTask.FromResult(new CaptureFrame(
                area.Width,
                area.Height,
                stride,
                pixels,
                area,
                DateTimeOffset.UtcNow));
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(0, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleBitmap(nint deviceContext, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destination,
        int xDestination,
        int yDestination,
        int width,
        int height,
        nint source,
        int xSource,
        int ySource,
        int operation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfo bitmapInfo,
        uint usage);
}
