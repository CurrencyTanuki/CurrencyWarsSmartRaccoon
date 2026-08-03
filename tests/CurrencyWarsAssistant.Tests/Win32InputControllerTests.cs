using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class Win32InputControllerTests
{
    [Fact]
    public async Task ClickWaitsForPointerAndHoldsButtonBeforeReportingSuccess()
    {
        var window = Window();
        var backend = new RecordingBackend(window.Handle);
        var controller = Controller(window, backend);

        var result = await controller.ClickAsync(
            Target(window),
            Policy(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["move", "cursor", "foreground", "window-at-point", "down", "up"],
            backend.Events);
        var diagnostic = Assert.IsType<InputActionDiagnostic>(result.Diagnostic);
        Assert.Equal(new PixelPoint(410, 160), diagnostic.TargetScreenPoint);
        Assert.Equal(diagnostic.TargetScreenPoint, diagnostic.CursorAfterMove);
        Assert.True(diagnostic.ForegroundBeforeSend);
        Assert.Equal(1u, diagnostic.MoveSendCount);
        Assert.Equal(1u, diagnostic.MouseDownSendCount);
        Assert.Equal(1u, diagnostic.MouseUpSendCount);
        Assert.Equal(TimeSpan.FromMilliseconds(2), diagnostic.PointerSettleDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(2), diagnostic.MouseButtonHoldDelay);
    }

    [Fact]
    public async Task ClickRepositionsPointerOnceBeforeSendingSingleClick()
    {
        var window = Window();
        var backend = new RecordingBackend(window.Handle)
        {
            MissFirstPointerPlacement = true
        };
        var controller = Controller(window, backend);

        var result = await controller.ClickAsync(
            Target(window),
            Policy(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, backend.Events.Count(item => item == "move"));
        Assert.Single(backend.Events.Where(item => item == "down"));
        Assert.Single(backend.Events.Where(item => item == "up"));
        Assert.Equal(2, result.Diagnostic?.PointerPlacementAttempts);
    }

    [Fact]
    public async Task ClickDoesNotPressWhenPointerNeverReachesTarget()
    {
        var window = Window();
        var backend = new RecordingBackend(window.Handle)
        {
            AlwaysMissPointerPlacement = true
        };
        var controller = Controller(window, backend);

        var result = await controller.ClickAsync(
            Target(window),
            Policy(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("down", backend.Events);
        Assert.DoesNotContain("up", backend.Events);
        Assert.Equal(2, result.Diagnostic?.PointerPlacementAttempts);
    }

    [Fact]
    public async Task ClickDoesNotPressWhenFocusChangesAfterPointerMove()
    {
        var window = Window();
        var backend = new RecordingBackend(window.Handle)
        {
            ForegroundHandle = 999
        };
        var controller = Controller(window, backend);

        var result = await controller.ClickAsync(
            Target(window),
            Policy(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("down", backend.Events);
        Assert.DoesNotContain("up", backend.Events);
        Assert.False(result.Diagnostic?.ForegroundBeforeSend);
    }

    [Fact]
    public async Task LegacyClickPolicyKeepsExistingInputBehavior()
    {
        var window = Window();
        var backend = new RecordingBackend(window.Handle)
        {
            AlwaysMissPointerPlacement = true,
            ForegroundHandle = 999
        };
        var controller = Controller(window, backend);

        var result = await controller.ClickAsync(
            Target(window),
            new ActionPolicy { AfterActionDelay = TimeSpan.Zero },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(backend.Events.Where(item => item == "move"));
        Assert.Single(backend.Events.Where(item => item == "down"));
        Assert.Single(backend.Events.Where(item => item == "up"));
    }

    private static Win32InputController Controller(
        GameWindowInfo window,
        IWin32InputBackend backend) =>
        new(
            new FixedWindowService(window),
            new ImmediateForegroundGuard(window),
            backend);

    private static GameWindowInfo Window() =>
        new(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(100, 50, 1280, 720));

    private static ClickTarget Target(GameWindowInfo window) =>
        new(
            "slot-1",
            "购买测试角色",
            window,
            new PixelRect(300, 100, 20, 20));

    private static ActionPolicy Policy() =>
        new()
        {
            PointerSettleDelay = TimeSpan.FromMilliseconds(2),
            MouseButtonHoldDelay = TimeSpan.FromMilliseconds(2),
            MaximumPointerPlacementAttempts = 2,
            VerifyPointerArrivalBeforeClick = true,
            VerifyForegroundBeforeClick = true,
            AfterActionDelay = TimeSpan.Zero
        };

    private sealed class RecordingBackend : IWin32InputBackend
    {
        private readonly nint _gameWindow;
        private PixelPoint _target;
        private int _moves;

        public RecordingBackend(nint gameWindow)
        {
            _gameWindow = gameWindow;
            ForegroundHandle = gameWindow;
        }

        public List<string> Events { get; } = [];
        public bool MissFirstPointerPlacement { get; init; }
        public bool AlwaysMissPointerPlacement { get; init; }
        public nint ForegroundHandle { get; init; }

        public uint MoveMouse(PixelPoint screenPoint)
        {
            Events.Add("move");
            _target = screenPoint;
            _moves++;
            return 1;
        }

        public uint SendLeftDown()
        {
            Events.Add("down");
            return 1;
        }

        public uint SendLeftUp()
        {
            Events.Add("up");
            return 1;
        }

        public uint SendKeyboard(ushort virtualKey, bool keyUp) => 1;

        public PixelPoint? GetCursorPosition()
        {
            Events.Add("cursor");
            return AlwaysMissPointerPlacement ||
                   (MissFirstPointerPlacement && _moves == 1)
                ? new PixelPoint(_target.X + 20, _target.Y + 20)
                : _target;
        }

        public nint GetForegroundWindow()
        {
            Events.Add("foreground");
            return ForegroundHandle;
        }

        public nint WindowFromPoint(PixelPoint screenPoint)
        {
            Events.Add("window-at-point");
            return _gameWindow;
        }
    }

    private sealed class FixedWindowService(GameWindowInfo window)
        : IGameWindowService
    {
        public IReadOnlyList<GameWindowInfo> FindCandidates() => [window];
        public GameWindowInfo? Refresh(nint handle) => window;
        public bool IsForeground(GameWindowInfo current) => true;
        public bool BringToForeground(GameWindowInfo current) => true;
    }

    private sealed class ImmediateForegroundGuard(GameWindowInfo window)
        : IGameForegroundGuard
    {
        public TimeSpan TotalPausedDuration => TimeSpan.Zero;

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            nint windowHandle,
            CancellationToken cancellationToken) =>
            Task.FromResult(window);

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            GameWindowInfo current,
            CancellationToken cancellationToken) =>
            Task.FromResult(window);
    }
}
