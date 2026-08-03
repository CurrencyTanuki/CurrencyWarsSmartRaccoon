using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Automation;

public sealed class Win32InputController : IInputController
{
    private readonly IGameWindowService _windowService;
    private readonly IGameForegroundGuard _foregroundGuard;
    private readonly IWin32InputBackend _backend;

    public Win32InputController(
        IGameWindowService windowService,
        IGameForegroundGuard foregroundGuard)
        : this(
            windowService,
            foregroundGuard,
            new NativeWin32InputBackend())
    {
    }

    internal Win32InputController(
        IGameWindowService windowService,
        IGameForegroundGuard foregroundGuard,
        IWin32InputBackend backend)
    {
        _windowService = windowService;
        _foregroundGuard = foregroundGuard;
        _backend = backend;
    }

    public async Task<ActionResult> ClickAsync(
        ClickTarget target,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var validation = await PrepareTargetAsync(target, policy, cancellationToken);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var refreshed = _windowService.Refresh(target.Window.Handle);
        if (refreshed is null)
        {
            return ActionResult.Failure("点击前游戏窗口已失效。");
        }

        var clientPoint = target.ClientBounds.Center;
        var screenPoint = CoordinateMapper.ClientToScreen(
            refreshed,
            clientPoint);
        var attempts = policy.VerifyPointerArrivalBeforeClick
            ? Math.Clamp(policy.MaximumPointerPlacementAttempts, 1, 3)
            : 1;
        var moveSendCount = 0u;
        PixelPoint? cursorAfterMove = null;
        var placementAttempts = 0;
        for (; placementAttempts < attempts; placementAttempts++)
        {
            var currentMoveSendCount = _backend.MoveMouse(screenPoint);
            if (currentMoveSendCount == 0)
            {
                // 后台诊断（不显示给用户）：记录传入坐标与失败错误码，
                // 用于定位"模拟鼠标移动失败"的真实原因。
                RecordInputDiagnostic(
                    "MoveMouseFailed",
                    $"screenPoint=({screenPoint.X},{screenPoint.Y}) " +
                    $"winError={Marshal.GetLastWin32Error()} " +
                    $"clientArea={refreshed.ClientArea}");
            }

            EnsureSent(currentMoveSendCount, "模拟鼠标移动失败。");
            moveSendCount += currentMoveSendCount;
            if (policy.PointerSettleDelay > TimeSpan.Zero)
            {
                await Task.Delay(
                    policy.PointerSettleDelay,
                    cancellationToken);
            }

            cursorAfterMove = _backend.GetCursorPosition();
            if (!policy.VerifyPointerArrivalBeforeClick ||
                (cursorAfterMove is not null &&
                 IsWithinTolerance(
                     cursorAfterMove.Value,
                     screenPoint,
                     policy.PointerArrivalTolerance)))
            {
                break;
            }
        }

        var foregroundBeforeSend =
            _backend.GetForegroundWindow() == refreshed.Handle;
        var windowAtTarget = _backend.WindowFromPoint(screenPoint);
        var diagnostic = new InputActionDiagnostic(
            "Win32.SendInput/absolute-virtual-desktop",
            refreshed.Handle,
            refreshed.ClientArea,
            target.ClientBounds,
            clientPoint,
            screenPoint,
            cursorAfterMove,
            foregroundBeforeSend,
            windowAtTarget,
            Math.Min(placementAttempts + 1, attempts),
            moveSendCount,
            0,
            0,
            policy.PointerSettleDelay,
            policy.MouseButtonHoldDelay);
        if (policy.VerifyPointerArrivalBeforeClick &&
            (cursorAfterMove is null ||
             !IsWithinTolerance(
                 cursorAfterMove.Value,
                 screenPoint,
                 policy.PointerArrivalTolerance)))
        {
            return ActionResult.Failure(
                "鼠标未到达目标坐标，已阻止点击。",
                diagnostic);
        }

        if (policy.VerifyForegroundBeforeClick && !foregroundBeforeSend)
        {
            return ActionResult.Failure(
                "点击发送前游戏窗口失去前台焦点，已阻止点击。",
                diagnostic);
        }

        var mouseDownSendCount = _backend.SendLeftDown();
        EnsureSent(mouseDownSendCount, "模拟鼠标按下失败。");
        uint mouseUpSendCount;
        try
        {
            if (policy.MouseButtonHoldDelay > TimeSpan.Zero)
            {
                await Task.Delay(
                    policy.MouseButtonHoldDelay,
                    cancellationToken);
            }
        }
        finally
        {
            mouseUpSendCount = _backend.SendLeftUp();
            EnsureSent(mouseUpSendCount, "模拟鼠标抬起失败。");
        }

        diagnostic = diagnostic with
        {
            MouseDownSendCount = mouseDownSendCount,
            MouseUpSendCount = mouseUpSendCount
        };
        await Task.Delay(policy.AfterActionDelay, cancellationToken);
        return ActionResult.Success(
            $"已点击：{target.DisplayName}" +
            $"（光标 {cursorAfterMove?.X ?? -1},{cursorAfterMove?.Y ?? -1}；" +
            $"目标窗口句柄 0x{windowAtTarget:X}，期望 0x{refreshed.Handle:X}）",
            diagnostic);
    }

    public async Task<ActionResult> DragAsync(
        ClickTarget source,
        PixelPoint targetClientPoint,
        TimeSpan duration,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var validation = await PrepareTargetAsync(source, policy, cancellationToken);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var refreshed = _windowService.Refresh(source.Window.Handle);
        if (refreshed is null)
        {
            return ActionResult.Failure("拖动前游戏窗口已失效。");
        }

        var start = CoordinateMapper.ClientToScreen(refreshed, source.ClientBounds.Center);
        var end = CoordinateMapper.ClientToScreen(refreshed, targetClientPoint);
        EnsureSent(_backend.MoveMouse(start), "模拟鼠标移动失败。");
        EnsureSent(_backend.SendLeftDown(), "模拟鼠标按下失败。");

        const int steps = 12;
        var stepDelay = TimeSpan.FromTicks(Math.Max(1, duration.Ticks / steps));
        for (var index = 1; index <= steps; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = index / (double)steps;
            EnsureSent(_backend.MoveMouse(new PixelPoint(
                (int)Math.Round(start.X + (end.X - start.X) * progress),
                (int)Math.Round(start.Y + (end.Y - start.Y) * progress))),
                "模拟鼠标移动失败。");
            await Task.Delay(stepDelay, cancellationToken);
        }

        EnsureSent(_backend.SendLeftUp(), "模拟鼠标抬起失败。");
        await Task.Delay(policy.AfterActionDelay, cancellationToken);
        return ActionResult.Success($"已拖动：{source.DisplayName}");
    }

    public async Task<ActionResult> PressKeyAsync(
        GameWindowInfo window,
        InputKey key,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var validation = await PrepareWindowAsync(window, policy, cancellationToken);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var virtualKey = ToVirtualKey(key);

        EnsureSent(
            _backend.SendKeyboard(virtualKey, keyUp: false),
            "模拟键盘按下失败。");
        EnsureSent(
            _backend.SendKeyboard(virtualKey, keyUp: true),
            "模拟键盘抬起失败。");
        await Task.Delay(policy.AfterActionDelay, cancellationToken);
        return ActionResult.Success($"已按下按键：{key}");
    }

    public async Task<ActionResult> ClickWithModifierAsync(
        ClickTarget target,
        InputKey modifier,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var validation = await PrepareTargetAsync(target, policy, cancellationToken);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var refreshed = _windowService.Refresh(target.Window.Handle);
        if (refreshed is null)
        {
            return ActionResult.Failure("组合点击前游戏窗口已失效。");
        }

        var virtualKey = ToVirtualKey(modifier);
        EnsureSent(
            _backend.SendKeyboard(virtualKey, keyUp: false),
            "模拟键盘按下失败。");
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
            EnsureSent(
                _backend.MoveMouse(CoordinateMapper.ClientToScreen(
                    refreshed,
                    target.ClientBounds.Center)),
                "模拟鼠标移动失败。");
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            EnsureSent(_backend.SendLeftDown(), "模拟鼠标按下失败。");
            EnsureSent(_backend.SendLeftUp(), "模拟鼠标抬起失败。");
            await Task.Delay(policy.AfterActionDelay, cancellationToken);
            return ActionResult.Success(
                $"已按住 {modifier} 点击：{target.DisplayName}");
        }
        finally
        {
            EnsureSent(
                _backend.SendKeyboard(virtualKey, keyUp: true),
                "模拟键盘抬起失败。");
        }
    }

    private static ushort ToVirtualKey(InputKey key) =>
        key switch
        {
            InputKey.Escape => 0x1B,
            InputKey.LeftAlt => 0x12,
            InputKey.V => 0x56,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
        };

    private async Task<ActionResult> PrepareTargetAsync(
        ClickTarget target,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var validation = await PrepareWindowAsync(target.Window, policy, cancellationToken);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var current = _windowService.Refresh(target.Window.Handle);
        if (current is null)
        {
            return ActionResult.Failure("操作前游戏窗口已失效。");
        }

        var center = target.ClientBounds.Center;
        if (center.X < 0 ||
            center.Y < 0 ||
            center.X >= current.ClientArea.Width ||
            center.Y >= current.ClientArea.Height)
        {
            return ActionResult.Failure("目标坐标超出游戏客户区，已阻止操作。");
        }

        return ActionResult.Success("操作前检查通过。");
    }

    private async Task<ActionResult> PrepareWindowAsync(
        GameWindowInfo window,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var current = await _foregroundGuard.WaitUntilForegroundAsync(
            window,
            cancellationToken);

        var widthDrift = Math.Abs(current.ClientArea.Width - window.ClientArea.Width);
        var heightDrift = Math.Abs(current.ClientArea.Height - window.ClientArea.Height);
        if (widthDrift > policy.MaximumClientSizeDrift ||
            heightDrift > policy.MaximumClientSizeDrift)
        {
            return ActionResult.Failure("游戏窗口尺寸在识别后发生变化，已阻止操作。");
        }

        return ActionResult.Success("窗口检查通过。");
    }

    private static void EnsureSent(uint sentCount, string errorMessage)
    {
        if (sentCount != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage);
        }
    }

    private static void RecordInputDiagnostic(string kind, string details)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "CurrencyWarsSmartRaccoon",
                "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "input-diagnostics.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Timestamp = DateTimeOffset.Now,
                    Kind = kind,
                    Details = details
                }) + Environment.NewLine);
        }
        catch
        {
            // 诊断记录失败不影响主流程。
        }
    }

    private static bool IsWithinTolerance(
        PixelPoint actual,
        PixelPoint expected,
        int tolerance)
    {
        var allowed = Math.Max(0, tolerance);
        return Math.Abs(actual.X - expected.X) <= allowed &&
               Math.Abs(actual.Y - expected.Y) <= allowed;
    }
}
