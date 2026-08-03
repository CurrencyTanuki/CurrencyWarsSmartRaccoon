using System.Diagnostics;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Automation;

public interface IGameForegroundGuard
{
    TimeSpan TotalPausedDuration { get; }

    Task<GameWindowInfo> WaitUntilForegroundAsync(
        nint windowHandle,
        CancellationToken cancellationToken);

    Task<GameWindowInfo> WaitUntilForegroundAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken);
}

public sealed class GameForegroundGuard(
    IGameWindowService windowService,
    ITaskEventSink eventSink) : IGameForegroundGuard
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ResumeSettleDelay =
        TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim _waitGate = new(1, 1);
    private readonly Dictionary<nint, GameWindowInfo> _knownWindows = [];
    private readonly object _knownWindowsLock = new();
    private long _totalPausedTicks;

    public TimeSpan TotalPausedDuration =>
        TimeSpan.FromTicks(Interlocked.Read(ref _totalPausedTicks));

    public async Task<GameWindowInfo> WaitUntilForegroundAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        Remember(window);
        return await WaitUntilForegroundAsync(
            window.Handle,
            cancellationToken);
    }

    public async Task<GameWindowInfo> WaitUntilForegroundAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        await _waitGate.WaitAsync(cancellationToken);
        try
        {
            var current = windowService.Refresh(windowHandle) ??
                          Recall(windowHandle);
            ThrowIfBindingInvalid(current);
            if (current is not null && windowService.IsForeground(current))
            {
                Remember(current);
                return current;
            }

            var paused = Stopwatch.StartNew();
            Publish(
                TaskEventLevel.Warning,
                "GameFocusPaused",
                "检测到游戏窗口失去前台焦点，自动化已暂停；重新切回游戏后将从当前步骤继续。");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, cancellationToken);
                current = windowService.Refresh(windowHandle);
                ThrowIfBindingInvalid(current);
                if (current is null || !windowService.IsForeground(current))
                {
                    continue;
                }

                await Task.Delay(ResumeSettleDelay, cancellationToken);
                current = windowService.Refresh(windowHandle);
                ThrowIfBindingInvalid(current);
                if (current is null || !windowService.IsForeground(current))
                {
                    continue;
                }

                paused.Stop();
                Interlocked.Add(
                    ref _totalPausedTicks,
                    paused.Elapsed.Ticks);
                Publish(
                    TaskEventLevel.Information,
                    "GameFocusResumed",
                    $"检测到游戏窗口重新获得焦点，自动化已恢复；本次暂停 {paused.Elapsed.TotalSeconds:F1} 秒。");
                Remember(current);
                return current;
            }
        }
        finally
        {
            _waitGate.Release();
        }
    }

    private void Remember(GameWindowInfo window)
    {
        lock (_knownWindowsLock)
        {
            _knownWindows[window.Handle] = window;
        }
    }

    private GameWindowInfo? Recall(nint windowHandle)
    {
        lock (_knownWindowsLock)
        {
            return _knownWindows.GetValueOrDefault(windowHandle);
        }
    }

    private static void ThrowIfBindingInvalid(GameWindowInfo? window)
    {
        if (window is null ||
            window.BindingState != GameWindowBindingState.Invalid)
        {
            return;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(window.BindingMessage)
                ? "游戏窗口或已定位的游戏画面已经失效，自动输入已停止。"
                : window.BindingMessage);
    }

    private void Publish(
        TaskEventLevel level,
        string code,
        string message) =>
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            level,
            code,
            message));
}
