using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class UnknownPageEscapeRecoveryTests
{
    [Fact]
    public async Task RecoverySendsOneEscapeAndDoesNotClick()
    {
        var window = new GameWindowInfo(
            (nint)42,
            1,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(100, 100, 1920, 1080));
        var input = new RecordingInputController();
        var recovery = new UnknownPageEscapeRecovery(
            new FixedWindowService(window),
            input,
            new NullTaskEventSink());

        var result = await recovery.RecoverAsync(
            window.Handle,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([InputKey.Escape], input.PressedKeys);
        Assert.Equal(0, input.ClickCount);
    }

    private sealed class FixedWindowService(GameWindowInfo window)
        : IGameWindowService
    {
        public IReadOnlyList<GameWindowInfo> FindCandidates() => [window];

        public GameWindowInfo? Refresh(nint handle) =>
            handle == window.Handle ? window : null;

        public bool IsForeground(GameWindowInfo value) => true;

        public bool BringToForeground(GameWindowInfo value) => true;
    }

    private sealed class RecordingInputController : IInputController
    {
        public List<InputKey> PressedKeys { get; } = [];
        public int ClickCount { get; private set; }

        public Task<ActionResult> ClickAsync(
            ClickTarget target,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            ClickCount++;
            return Task.FromResult(ActionResult.Success("clicked"));
        }

        public Task<ActionResult> DragAsync(
            ClickTarget source,
            PixelPoint targetClientPoint,
            TimeSpan duration,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Success("dragged"));

        public Task<ActionResult> PressKeyAsync(
            GameWindowInfo window,
            InputKey key,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            PressedKeys.Add(key);
            return Task.FromResult(ActionResult.Success("pressed"));
        }

        public Task<ActionResult> ClickWithModifierAsync(
            ClickTarget target,
            InputKey modifier,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionResult.Success("modifier-clicked"));
    }

    private sealed class NullTaskEventSink : ITaskEventSink
    {
        public void Publish(TaskEvent taskEvent)
        {
        }
    }
}
