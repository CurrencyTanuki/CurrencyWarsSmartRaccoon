using System.ComponentModel;
using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;

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

        // 云游戏（浏览器 GPU 合成）下 BitBlt 截屏幕 DC 不可靠：无论窗口
        // 是否在前台，GDI 表面都拿不到 GPU 合成内容（黑/深灰坏帧，或被
        // 遮挡时截到别的窗口）。直接走 PrintWindow(PW_RENDERFULLCONTENT)
        // 从窗口自身渲染内容截取，不依赖屏幕可见性。
        if (window.SourceKind == GameWindowSourceKind.CloudBrowser)
        {
            var cloudFallback = TryCaptureWithPrintWindow(window, area);
            if (cloudFallback is not null)
            {
                _ = ReleaseDC(0, screenDc);
                return ValueTask.FromResult(cloudFallback);
            }
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

            // 本地客户端：BitBlt 正常。若截到黑/深灰坏帧（云游戏窗口被误选、
            // 显卡合成异常等），回退 PrintWindow(PW_RENDERFULLCONTENT) 从
            // 窗口自身渲染内容重新截取。云游戏源已在上方优先走 PrintWindow，
            // 此处不再重复回退。
            if (window.SourceKind != GameWindowSourceKind.CloudBrowser &&
                IsEffectivelyBlack(pixels, area.Width, area.Height))
            {
                var fallback = TryCaptureWithPrintWindow(window, area);
                if (fallback is not null)
                {
                    return ValueTask.FromResult(fallback);
                }
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

    /// <summary>
    /// 判定帧是否为云游戏坏帧（黑/深灰）。只采样中心 60% 区域——
    /// 云游戏坏帧特征为中间深灰 RGB(32,32,32)、边缘残留 UI，阈值 60
    /// 能覆盖 32 而不误伤正常画面边缘。
    /// </summary>
    internal static bool IsEffectivelyBlack(
        byte[] pixels,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
        {
            return true;
        }

        var x0 = width * 2 / 10;
        var x1 = width * 8 / 10;
        var y0 = height * 2 / 10;
        var y1 = height * 8 / 10;
        if (x1 <= x0 || y1 <= y0)
        {
            return true;
        }

        const int blackThreshold = 60;
        var stride = checked(width * 4);
        long sum = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var rowBase = y * stride;
            for (var x = x0; x < x1; x++)
            {
                var offset = rowBase + x * 4;
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                sum += (red + green + blue) / 3;
                count++;
            }
        }

        if (count == 0)
        {
            return true;
        }

        var average = sum / (double)count;
        return average <= blackThreshold;
    }

    /// <summary>
    /// 用 PrintWindow(PW_RENDERFULLCONTENT) 从窗口自身渲染内容重新截取，
    /// 解决云游戏（浏览器 GPU 合成）下 BitBlt 黑屏。返回与 BitBlt 相同
    /// 语义的帧（尺寸与客户区一致）；失败返回 null 时保留原 BitBlt 结果。
    /// </summary>
    private static CaptureFrame? TryCaptureWithPrintWindow(
        GameWindowInfo window,
        PixelRect clientArea)
    {
        if (window.Handle == 0)
        {
            return null;
        }

        var windowRect = NativeWindowMethods.GetWindowScreenRect(window.Handle);
        var clientScreen = NativeWindowMethods.GetClientScreenRect(window.Handle);
        if (windowRect is not { } windowArea ||
            clientScreen is not { } clientScreenArea ||
            windowArea.IsEmpty ||
            clientScreenArea.IsEmpty)
        {
            return null;
        }

        var windowDc = GetDC(window.Handle);
        if (windowDc == 0)
        {
            return null;
        }

        var memoryDc = CreateCompatibleDC(windowDc);
        var bitmap = CreateCompatibleBitmap(
            windowDc,
            windowArea.Width,
            windowArea.Height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            // PW_RENDERFULLCONTENT = 2：强制渲染整个窗口内容（含 GPU 合成）。
            if (!PrintWindow(window.Handle, memoryDc, 2))
            {
                return null;
            }

            var windowStride = checked(windowArea.Width * 4);
            var windowPixels =
                new byte[checked(windowStride * windowArea.Height)];
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = windowArea.Width,
                    Height = -windowArea.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };

            var copied = GetDIBits(
                memoryDc,
                bitmap,
                0,
                (uint)windowArea.Height,
                windowPixels,
                ref bitmapInfo,
                DibRgbColors);
            if (copied != windowArea.Height)
            {
                return null;
            }

            // 从整窗像素中裁剪出客户区（PrintWindow 输出含标题栏/边框，
            // 而上层按客户区坐标换算，必须保持帧尺寸与 ClientArea 一致）。
            var offsetX = clientScreenArea.X - windowArea.X;
            var offsetY = clientScreenArea.Y - windowArea.Y;
            if (offsetX < 0 ||
                offsetY < 0 ||
                offsetX + clientScreenArea.Width > windowArea.Width ||
                offsetY + clientScreenArea.Height > windowArea.Height)
            {
                return null;
            }

            var clientStride = checked(clientScreenArea.Width * 4);
            var clientPixels =
                new byte[checked(clientStride * clientScreenArea.Height)];
            for (var row = 0; row < clientScreenArea.Height; row++)
            {
                System.Buffer.BlockCopy(
                    windowPixels,
                    checked((offsetY + row) * windowStride + offsetX * 4),
                    clientPixels,
                    row * clientStride,
                    clientStride);
            }

            return new CaptureFrame(
                clientScreenArea.Width,
                clientScreenArea.Height,
                clientStride,
                clientPixels,
                clientArea,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(window.Handle, windowDc);
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(
        nint window,
        nint deviceContext,
        uint flags);

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
