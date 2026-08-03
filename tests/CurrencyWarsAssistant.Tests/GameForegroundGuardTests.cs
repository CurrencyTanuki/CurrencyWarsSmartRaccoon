using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class GameForegroundGuardTests
{
    [Fact]
    public async Task PausesWhileGameIsInBackgroundAndResumesWhenFocusReturns()
    {
        var windowService = new FocusWindowService();
        var eventSink = new RecordingEventSink();
        var guard = new GameForegroundGuard(windowService, eventSink);

        var waitTask = guard.WaitUntilForegroundAsync(
            windowService.Window,
            CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waitTask.IsCompleted);

        windowService.IsGameForeground = true;
        var resumedWindow = await waitTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(windowService.Window.Handle, resumedWindow.Handle);
        Assert.True(guard.TotalPausedDuration > TimeSpan.Zero);
        Assert.Contains(eventSink.Events, value => value.Code == "GameFocusPaused");
        Assert.Contains(eventSink.Events, value => value.Code == "GameFocusResumed");
    }

    [Fact]
    public async Task InvalidCloudBindingStopsInsteadOfWaitingForever()
    {
        var windowService = new FocusWindowService
        {
            WindowOverride = new GameWindowInfo(
                123,
                456,
                "msedge",
                "其他标签页",
                new PixelRect(0, 0, 1920, 1080),
                GameWindowSourceKind.CloudBrowser,
                new PixelRect(0, 0, 1920, 1080),
                GameWindowBindingState.Invalid,
                "浏览器页面或标签页已经变化。")
        };
        var guard = new GameForegroundGuard(
            windowService,
            new RecordingEventSink());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.WaitUntilForegroundAsync(
                windowService.Window,
                CancellationToken.None));

        Assert.Contains("标签页", exception.Message);
    }

    private sealed class FocusWindowService : IGameWindowService
    {
        private readonly GameWindowInfo _defaultWindow = new(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(0, 0, 1920, 1080));

        public GameWindowInfo Window => WindowOverride ?? _defaultWindow;
        public GameWindowInfo? WindowOverride { get; init; }
        public bool IsGameForeground { get; set; }

        public IReadOnlyList<GameWindowInfo> FindCandidates() => [Window];

        public GameWindowInfo? Refresh(nint handle) =>
            handle == Window.Handle ? Window : null;

        public bool IsForeground(GameWindowInfo window) => IsGameForeground;

        public bool BringToForeground(GameWindowInfo window) => false;
    }

    private sealed class RecordingEventSink : ITaskEventSink
    {
        public List<TaskEvent> Events { get; } = [];

        public void Publish(TaskEvent taskEvent) => Events.Add(taskEvent);
    }
}
