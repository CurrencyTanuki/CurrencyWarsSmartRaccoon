using System.Diagnostics;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Vision;

public sealed class GameWindowService : IGameWindowService
{
    private static readonly HashSet<string> BrowserProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome",
            "msedge",
            "firefox",
            "brave",
            "opera",
            "vivaldi",
            "360chrome",
            "qqbrowser"
        };
    private static readonly HashSet<string> KnownProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "StarRail",
            "StarRailBase"
        };
    private static readonly string[] KnownTitleParts =
    [
        "崩坏：星穹铁道",
        "崩坏:星穹铁道",
        "Honkai: Star Rail"
    ];
    private static readonly string[] CloudTitleParts =
    [
        "云·星穹铁道",
        "云・星穹铁道",
        "云星穹铁道",
        "Honkai: Star Rail",
        "miHoYo",
        "米哈游"
    ];
    private readonly object _bindingLock = new();
    private readonly Dictionary<nint, WindowBinding> _bindings = [];

    public IReadOnlyList<GameWindowInfo> FindCandidates() =>
        FindCandidates(GameSourcePreference.LocalClient);

    public IReadOnlyList<GameWindowInfo> FindCandidates(
        GameSourcePreference preference)
    {
        var results = new List<GameWindowInfo>();
        NativeWindowMethods.EnumWindows((handle, _) =>
        {
            var window = ReadWindow(handle);
            if (window is not null && ShouldInclude(window, preference))
            {
                results.Add(ApplyBinding(window));
            }

            return true;
        }, 0);

        return results
            .DistinctBy(window => window.Handle)
            .OrderBy(window => WindowSourceRank(window.SourceKind))
            .ThenByDescending(window =>
                window.ClientArea.Width * window.ClientArea.Height)
            .ToArray();
    }

    public GameWindowInfo? Refresh(nint handle)
    {
        var window = ReadWindow(handle);
        return window is null ? null : ApplyBinding(window);
    }

    public GameWindowInfo? BindGameArea(
        nint handle,
        PixelRect gameArea,
        GameWindowSourceKind sourceKind)
    {
        var window = ReadWindow(handle);
        if (window is null || gameArea.IsEmpty)
        {
            return null;
        }

        var host = window.ClientArea;
        if (gameArea.X < host.X ||
            gameArea.Y < host.Y ||
            gameArea.Right > host.Right ||
            gameArea.Bottom > host.Bottom)
        {
            return window with
            {
                SourceKind = sourceKind,
                BindingState = GameWindowBindingState.Invalid,
                BindingMessage = "所选游戏区域超出窗口客户区。"
            };
        }

        if (!GameAspectRatio.IsSixteenByNine(
                gameArea.Width,
                gameArea.Height))
        {
            return window with
            {
                SourceKind = sourceKind,
                BindingState = GameWindowBindingState.Invalid,
                BindingMessage = GameAspectRatio.InvalidAspectRatioMessage
            };
        }

        var relative = new NormalizedRect(
            (gameArea.X - host.X) / (double)host.Width,
            (gameArea.Y - host.Y) / (double)host.Height,
            gameArea.Width / (double)host.Width,
            gameArea.Height / (double)host.Height);
        lock (_bindingLock)
        {
            _bindings[handle] = new WindowBinding(
                sourceKind,
                relative,
                window.ProcessId,
                window.ProcessName,
                window.Title,
                host.Width,
                host.Height);
        }

        return ApplyBinding(window);
    }

    public void ClearGameAreaBinding(nint handle)
    {
        lock (_bindingLock)
        {
            _bindings.Remove(handle);
        }
    }

    public bool IsForeground(GameWindowInfo window) =>
        window.IsReadyForAutomation &&
        NativeWindowMethods.GetForegroundWindow() == window.Handle;

    public bool BringToForeground(GameWindowInfo window) =>
        window.IsValid &&
        NativeWindowMethods.ForceForegroundWindow(window.Handle);

    private static GameWindowInfo? ReadWindow(nint handle)
    {
        if (handle == 0 ||
            !NativeWindowMethods.IsWindowVisible(handle) ||
            NativeWindowMethods.IsIconic(handle))
        {
            return null;
        }

        var clientArea = NativeWindowMethods.GetClientScreenRect(handle);
        if (clientArea is null)
        {
            return null;
        }

        _ = NativeWindowMethods.GetWindowThreadProcessId(handle, out var processId);
        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return new GameWindowInfo(
            handle,
            processId,
            processName,
            NativeWindowMethods.GetWindowTitle(handle),
            clientArea.Value);
    }

    private GameWindowInfo ApplyBinding(GameWindowInfo window)
    {
        WindowBinding? binding;
        lock (_bindingLock)
        {
            binding = _bindings.GetValueOrDefault(window.Handle);
        }

        if (binding is not null)
        {
            if (binding.ProcessId != window.ProcessId ||
                !string.Equals(
                    binding.ProcessName,
                    window.ProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                ClearGameAreaBinding(window.Handle);
                return window with
                {
                    SourceKind = GuessSourceKind(window),
                    BindingState = GameWindowBindingState.Invalid,
                    BindingMessage = "窗口句柄已被其他程序复用，请重新选择游戏窗口。"
                };
            }

            if (binding.SourceKind is not GameWindowSourceKind.LocalClient &&
                !string.Equals(
                    binding.BoundTitle,
                    window.Title,
                    StringComparison.Ordinal))
            {
                return window with
                {
                    SourceKind = binding.SourceKind,
                    HostClientAreaOverride = window.ClientArea,
                    BindingState = GameWindowBindingState.Invalid,
                    BindingMessage =
                        "浏览器页面或标签页已经变化，已停止输入；请重新选择并定位游戏画面。"
                };
            }

            var relativeArea = binding.RelativeGameArea.ToPixels(
                window.ClientArea.Width,
                window.ClientArea.Height);
            var hostScaleX =
                window.ClientArea.Width / (double)binding.HostWidth;
            var hostScaleY =
                window.ClientArea.Height / (double)binding.HostHeight;
            if (Math.Abs(hostScaleX - hostScaleY) > 0.01)
            {
                return window with
                {
                    SourceKind = binding.SourceKind,
                    HostClientAreaOverride = window.ClientArea,
                    BindingState = GameWindowBindingState.Invalid,
                    BindingMessage =
                        "浏览器客户区比例已变化，请重新定位16:9游戏画面。"
                };
            }

            var gameArea = relativeArea with
            {
                X = checked(window.ClientArea.X + relativeArea.X),
                Y = checked(window.ClientArea.Y + relativeArea.Y)
            };
            if (!GameAspectRatio.IsSixteenByNine(
                    gameArea.Width,
                    gameArea.Height))
            {
                return window with
                {
                    SourceKind = binding.SourceKind,
                    HostClientAreaOverride = window.ClientArea,
                    BindingState = GameWindowBindingState.Invalid,
                    BindingMessage = GameAspectRatio.InvalidAspectRatioMessage
                };
            }

            return window with
            {
                ClientArea = gameArea,
                SourceKind = binding.SourceKind,
                HostClientAreaOverride = window.ClientArea,
                BindingState = GameWindowBindingState.Ready,
                BindingMessage = ""
            };
        }

        var sourceKind = GuessSourceKind(window);
        if (sourceKind == GameWindowSourceKind.LocalClient)
        {
            return window;
        }

        return window with
        {
            SourceKind = sourceKind,
            BindingState = GameWindowBindingState.RequiresCalibration,
            BindingMessage = "请定位浏览器中的16:9游戏画面。"
        };
    }

    private static bool ShouldInclude(
        GameWindowInfo window,
        GameSourcePreference preference)
    {
        var isLocal = IsKnownLocalClient(window);
        var isBrowser = BrowserProcessNames.Contains(window.ProcessName);
        var isLikelyCloud = isBrowser && CloudTitleParts.Any(part =>
            window.Title.Contains(part, StringComparison.OrdinalIgnoreCase));
        return preference switch
        {
            GameSourcePreference.LocalClient => isLocal,
            GameSourcePreference.CloudBrowser => isBrowser,
            GameSourcePreference.AnyWindow =>
                !string.IsNullOrWhiteSpace(window.Title) &&
                window.ClientArea.Width >= 320 &&
                window.ClientArea.Height >= 180,
            _ => isLocal || isLikelyCloud
        };
    }

    private static bool IsKnownLocalClient(GameWindowInfo window) =>
        KnownProcessNames.Contains(window.ProcessName) ||
        KnownTitleParts.Any(part =>
            window.Title.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static GameWindowSourceKind GuessSourceKind(GameWindowInfo window)
    {
        if (IsKnownLocalClient(window))
        {
            return GameWindowSourceKind.LocalClient;
        }

        return BrowserProcessNames.Contains(window.ProcessName)
            ? GameWindowSourceKind.CloudBrowser
            : GameWindowSourceKind.ManualWindow;
    }

    private static int WindowSourceRank(GameWindowSourceKind sourceKind) =>
        sourceKind switch
        {
            GameWindowSourceKind.LocalClient => 0,
            GameWindowSourceKind.CloudBrowser => 1,
            _ => 2
        };

    private sealed record WindowBinding(
        GameWindowSourceKind SourceKind,
        NormalizedRect RelativeGameArea,
        uint ProcessId,
        string ProcessName,
        string BoundTitle,
        int HostWidth,
        int HostHeight);
}
