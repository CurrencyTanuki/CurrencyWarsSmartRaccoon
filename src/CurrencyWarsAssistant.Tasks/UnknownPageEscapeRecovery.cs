using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed record UnknownPageEscapeRecoveryResult(
    bool Succeeded,
    string Message);

/// <summary>
/// BetterGI-style fallback for pages that are not part of the configured
/// workflow: send one Escape key, then let the page classifier determine the
/// resulting known state. This class never guesses a follow-up click.
/// </summary>
public sealed class UnknownPageEscapeRecovery(
    IGameWindowService windowService,
    IInputController input,
    ITaskEventSink eventSink,
    IGameForegroundGuard? foregroundGuard = null)
{
    public async Task<UnknownPageEscapeRecoveryResult> RecoverAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var window = foregroundGuard is null
            ? windowService.Refresh(windowHandle)
            : await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
        if (window is null)
        {
            return new UnknownPageEscapeRecoveryResult(
                false,
                "未知页面恢复前游戏窗口已失效。");
        }

        Publish(
            "UnknownPageEscapeRequested",
            "当前页面未知，按通用恢复策略发送 Esc，随后重新识别页面。");
        var action = await input.PressKeyAsync(
            window,
            InputKey.Escape,
            new ActionPolicy
            {
                AfterActionDelay = TimeSpan.FromMilliseconds(350)
            },
            cancellationToken);
        if (!action.Succeeded)
        {
            return new UnknownPageEscapeRecoveryResult(false, action.Message);
        }

        Publish(
            "UnknownPageEscapeSent",
            "已发送 Esc，正在重新识别当前页面。");
        return new UnknownPageEscapeRecoveryResult(true, action.Message);
    }

    private void Publish(string code, string message) =>
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            code,
            message));
}
