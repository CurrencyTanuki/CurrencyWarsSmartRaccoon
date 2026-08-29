using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Vision;

public enum GameSourcePreference
{
    Automatic,
    LocalClient,
    CloudBrowser,
    AnyWindow
}

public enum GameWindowSourceKind
{
    LocalClient,
    CloudBrowser,
    ManualWindow
}

public enum GameWindowBindingState
{
    Ready,
    RequiresCalibration,
    Invalid
}

public sealed record GameWindowInfo(
    nint Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    PixelRect ClientArea,
    GameWindowSourceKind SourceKind = GameWindowSourceKind.LocalClient,
    PixelRect? HostClientAreaOverride = null,
    GameWindowBindingState BindingState = GameWindowBindingState.Ready,
    string BindingMessage = "")
{
    public bool IsValid => Handle != 0 && !ClientArea.IsEmpty;
    public bool IsReadyForAutomation =>
        IsValid && BindingState == GameWindowBindingState.Ready;
    public PixelRect HostClientArea =>
        HostClientAreaOverride ?? ClientArea;
    public string SourceDisplayName => SourceKind switch
    {
        GameWindowSourceKind.LocalClient => "本地客户端",
        GameWindowSourceKind.CloudBrowser => "云游戏浏览器",
        _ => "手动窗口"
    };
    public string BindingStatusDisplay => BindingState switch
    {
        GameWindowBindingState.Ready =>
            $"{ClientArea.Width}×{ClientArea.Height} · 已就绪",
        GameWindowBindingState.RequiresCalibration =>
            "需要定位16:9游戏画面",
        _ => string.IsNullOrWhiteSpace(BindingMessage)
            ? "当前窗口不可用"
            : BindingMessage
    };

    public override string ToString() =>
        $"[{SourceDisplayName}] {Title} · {ProcessName} · {BindingStatusDisplay}";
}

public interface IGameWindowService
{
    IReadOnlyList<GameWindowInfo> FindCandidates();

    IReadOnlyList<GameWindowInfo> FindCandidates(
        GameSourcePreference preference) =>
        FindCandidates();

    GameWindowInfo? Refresh(nint handle);

    GameWindowInfo? BindGameArea(
        nint handle,
        PixelRect gameArea,
        GameWindowSourceKind sourceKind) =>
        Refresh(handle);

    void ClearGameAreaBinding(nint handle)
    {
    }

    bool IsForeground(GameWindowInfo window);
    bool BringToForeground(GameWindowInfo window);
}
