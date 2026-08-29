using System.Runtime.InteropServices;
using System.Text;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Vision;

internal static class NativeWindowMethods
{
    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hWnd, ref Point point);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    internal static string GetWindowTitle(nint handle)
    {
        var length = GetWindowTextLengthW(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowTextW(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    internal static PixelRect? GetClientScreenRect(nint handle)
    {
        if (!GetClientRect(handle, out var client))
        {
            return null;
        }

        var topLeft = new Point { X = client.Left, Y = client.Top };
        var bottomRight = new Point { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(handle, ref topLeft) || !ClientToScreen(handle, ref bottomRight))
        {
            return null;
        }

        var rect = new PixelRect(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
        return rect.IsEmpty ? null : rect;
    }

    internal static PixelRect? GetWindowScreenRect(nint handle)
    {
        if (!GetWindowRect(handle, out var window))
        {
            return null;
        }

        var rect = new PixelRect(
            window.Left,
            window.Top,
            window.Right - window.Left,
            window.Bottom - window.Top);
        return rect.IsEmpty ? null : rect;
    }

    internal static bool ForceForegroundWindow(nint handle)
    {
        const int restoreWindow = 9;
        _ = ShowWindow(handle, restoreWindow);

        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var foregroundThread = foreground == 0
            ? 0
            : GetWindowThreadProcessId(foreground, out _);

        var attachedCurrent = false;
        var attachedForeground = false;
        try
        {
            if (currentThread != targetThread)
            {
                attachedCurrent = AttachThreadInput(currentThread, targetThread, true);
            }

            if (foregroundThread != 0 &&
                foregroundThread != targetThread &&
                foregroundThread != currentThread)
            {
                attachedForeground =
                    AttachThreadInput(foregroundThread, targetThread, true);
            }

            _ = BringWindowToTop(handle);
            var activated = SetForegroundWindow(handle);
            _ = SetFocus(handle);
            return activated || GetForegroundWindow() == handle;
        }
        finally
        {
            if (attachedForeground)
            {
                _ = AttachThreadInput(foregroundThread, targetThread, false);
            }

            if (attachedCurrent)
            {
                _ = AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }
}
