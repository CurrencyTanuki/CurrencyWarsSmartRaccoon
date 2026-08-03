using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Automation;

internal interface IWin32InputBackend
{
    uint MoveMouse(PixelPoint screenPoint);
    uint SendLeftDown();
    uint SendLeftUp();
    uint SendKeyboard(ushort virtualKey, bool keyUp);
    PixelPoint? GetCursorPosition();
    nint GetForegroundWindow();
    nint WindowFromPoint(PixelPoint screenPoint);
}

internal sealed class NativeWin32InputBackend : IWin32InputBackend
{
    public uint MoveMouse(PixelPoint screenPoint)
    {
        // SendInput 绝对坐标优先：旧版 build04（第一阶段）用 SendInput
        // 在你机器上实测刷开局成功。SetCursorPos 在软件进程内实测返回
        // 失败（0.2.772-0.2.774 诊断证明），因此作为降级方案而非首选。
        var virtualX = GetSystemMetrics(SystemMetric.XVirtualScreen);
        var virtualY = GetSystemMetrics(SystemMetric.YVirtualScreen);
        var virtualWidth = GetSystemMetrics(SystemMetric.CxVirtualScreen);
        var virtualHeight = GetSystemMetrics(SystemMetric.CyVirtualScreen);
        if (virtualWidth <= 1 || virtualHeight <= 1)
        {
            return 0u;
        }

        var absoluteX = (int)Math.Round(
            (screenPoint.X - virtualX) * 65535d / (virtualWidth - 1));
        var absoluteY = (int)Math.Round(
            (screenPoint.Y - virtualY) * 65535d / (virtualHeight - 1));
        var sent = SendMouse(
            MouseEventFlags.Move |
            MouseEventFlags.Absolute |
            MouseEventFlags.VirtualDesk,
            absoluteX,
            absoluteY);
        if (sent == 1)
        {
            return 1u;
        }

        // SendInput 失败时降级 SetCursorPos（独立进程实测可用）。
        return NativeSetCursorPos(screenPoint.X, screenPoint.Y) ? 1u : 0u;
    }

    public uint SendLeftDown() => SendMouse(MouseEventFlags.LeftDown);

    public uint SendLeftUp() => SendMouse(MouseEventFlags.LeftUp);

    public uint SendKeyboard(ushort virtualKey, bool keyUp)
    {
        var input = new Input
        {
            Type = InputType.Keyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp
                        ? KeyboardEventFlags.KeyUp
                        : KeyboardEventFlags.None
                }
            }
        };

        return SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    public PixelPoint? GetCursorPosition() =>
        GetCursorPos(out var point)
            ? new PixelPoint(point.X, point.Y)
            : null;

    public nint GetForegroundWindow() => NativeGetForegroundWindow();

    public nint WindowFromPoint(PixelPoint screenPoint) =>
        NativeWindowFromPoint(new NativePoint(screenPoint.X, screenPoint.Y));

    private static uint SendMouse(
        MouseEventFlags flags,
        int x = 0,
        int y = 0)
    {
        var input = new Input
        {
            Type = InputType.Mouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    X = x,
                    Y = y,
                    Flags = flags
                }
            }
        };

        return SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private enum InputType : uint
    {
        Mouse = 0,
        Keyboard = 1
    }

    [Flags]
    private enum MouseEventFlags : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        VirtualDesk = 0x4000,
        Absolute = 0x8000
    }

    [Flags]
    private enum KeyboardEventFlags : uint
    {
        None = 0,
        KeyUp = 0x0002
    }

    private enum SystemMetric
    {
        XVirtualScreen = 76,
        YVirtualScreen = 77,
        CxVirtualScreen = 78,
        CyVirtualScreen = 79
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal InputType Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal MouseEventFlags Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal KeyboardEventFlags Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] Input[] inputs,
        int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric metric);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint NativeGetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint NativeWindowFromPoint(NativePoint point);

    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetCursorPos(int x, int y);
}
