using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Vision;

/// <summary>
/// 帧沙箱（docs/SANDBOX_FEASIBILITY_20260908.md）的假窗口服务：
/// 不枚举任何真实窗口，恒定返回一个 1920×1080 的假 GameWindowInfo。
/// 前台判定恒真、切前台恒成功——沙箱里"游戏窗口"只是帧序列的逻辑载体。
/// </summary>
public sealed class StubWindowService : IGameWindowService
{
    public const int SandboxClientWidth = 1920;
    public const int SandboxClientHeight = 1080;

    /// <summary>假窗口句柄：任意非 0 值即可，绝不传给任何 Win32 捕获/输入调用。</summary>
    public const int SandboxWindowHandle = 0x53414E44;

    public StubWindowService(string windowTitle = "帧沙箱窗口")
    {
        Window = new GameWindowInfo(
            SandboxWindowHandle,
            ProcessId: 0,
            ProcessName: "FrameSandbox",
            Title: windowTitle,
            ClientArea: new PixelRect(0, 0, SandboxClientWidth, SandboxClientHeight),
            GameWindowSourceKind.ManualWindow,
            BindingState: GameWindowBindingState.Ready,
            BindingMessage: "帧沙箱假窗口");
    }

    /// <summary>沙箱唯一窗口。操作层坐标全部落在其 ClientArea（1920×1080 标准系）内。</summary>
    public GameWindowInfo Window { get; }

    public IReadOnlyList<GameWindowInfo> FindCandidates() => [Window];

    public GameWindowInfo? Refresh(nint handle) =>
        handle == 0 ? null : Window;

    public bool IsForeground(GameWindowInfo window) => true;

    public bool BringToForeground(GameWindowInfo window) => true;
}
