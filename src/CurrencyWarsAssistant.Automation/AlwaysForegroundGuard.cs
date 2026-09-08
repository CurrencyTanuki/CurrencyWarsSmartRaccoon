using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Automation;

/// <summary>
/// 帧沙箱（docs/SANDBOX_FEASIBILITY_20260908.md）的恒真前台守卫：
/// rule 二.2 的前台守卫在沙箱里必须永远放行——假窗口没有真实前台状态，
/// 任何等待都是无限阻塞。
/// </summary>
public sealed class AlwaysForegroundGuard : IGameForegroundGuard
{
    private readonly IGameWindowService _windowService;

    public AlwaysForegroundGuard(IGameWindowService windowService)
    {
        ArgumentNullException.ThrowIfNull(windowService);
        _windowService = windowService;
    }

    public TimeSpan TotalPausedDuration => TimeSpan.Zero;

    public Task<GameWindowInfo> WaitUntilForegroundAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(window);
    }

    public Task<GameWindowInfo> WaitUntilForegroundAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = _windowService.Refresh(windowHandle) ??
                     _windowService.FindCandidates().FirstOrDefault() ??
                     throw new InvalidOperationException(
                         "帧沙箱窗口服务没有可用窗口。");
        return Task.FromResult(window);
    }
}
