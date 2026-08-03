using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

var titleMode = args.Length >= 3 &&
                string.Equals(args[0], "--title", StringComparison.Ordinal);
var outputPath = titleMode
    ? Path.GetFullPath(args[2])
    : args.Length > 0
        ? Path.GetFullPath(args[0])
    : Path.GetFullPath("live-game-capture.png");
GameWindowInfo? window;
if (titleMode)
{
    window = WindowLookup.FindByTitle(args[1]);
}
else
{
    var windows = new GameWindowService().FindCandidates();
    if (windows.Count != 1)
    {
        Console.Error.WriteLine(
            $"Expected exactly one game window, found {windows.Count}.");
        return 2;
    }

    window = windows[0];
}

if (window is null)
{
    Console.Error.WriteLine("The requested window was not found.");
    return 2;
}

Console.WriteLine(
    $"Window={window.Title}; Client={window.ClientArea.Width}x{window.ClientArea.Height}");
using var capture = new WindowsGraphicsGameCapture();
var frame = await capture.CaptureAsync(window, CancellationToken.None);
Directory.CreateDirectory(
    Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
frame.SavePng(outputPath);
Console.WriteLine(
    $"Capture={frame.Width}x{frame.Height}; Output={outputPath}");
return 0;

internal static class WindowLookup
{
    public static GameWindowInfo? FindByTitle(string title)
    {
        var handle = FindWindow(null, title);
        if (handle == 0 ||
            !GetClientRect(handle, out var client) ||
            client.Right <= client.Left ||
            client.Bottom <= client.Top)
        {
            return null;
        }

        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin))
        {
            return null;
        }

        GetWindowThreadProcessId(handle, out var processId);
        var process = Process.GetProcessById((int)processId);
        return new GameWindowInfo(
            handle,
            processId,
            process.ProcessName,
            title,
            new PixelRect(
                origin.X,
                origin.Y,
                client.Right - client.Left,
                client.Bottom - client.Top));
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW",
        CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(
        string? className,
        string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(
        nint window,
        out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(
        nint window,
        ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
