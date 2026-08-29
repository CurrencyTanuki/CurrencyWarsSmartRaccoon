using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public interface IPassiveRecoveryMonitor
{
    Task<string> WaitForSafeEntryPageAsync(
        nint windowHandle,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only recovery wait. It never sends keyboard or mouse input and only
/// hands control back to navigation after two consecutive safe entry frames.
/// </summary>
public sealed class PassiveRecoveryMonitor(
    IGameCapture capture,
    IGamePageClassifier pageClassifier,
    IGameForegroundGuard foregroundGuard,
    ITaskEventSink eventSink) : IPassiveRecoveryMonitor
{
    private static readonly HashSet<string> SafeEntryPageIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "currency_wars_home",
            "normal_hud"
        };

    public async Task<string> WaitForSafeEntryPageAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        string? previousPageId = null;
        var stableCount = 0;
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Warning,
            "PassiveRecoveryMonitoringStarted",
            "自动恢复未完成，已进入只读页面监测；不会发送键鼠输入。"));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? pageId;
            try
            {
                var window = await foregroundGuard.WaitUntilForegroundAsync(
                    windowHandle,
                    cancellationToken);
                var frame = await capture.CaptureAsync(window, cancellationToken);
                pageId = pageClassifier.Classify(frame)?.PageId;
                if (pageId is null && CurrencyWarsHomeEvidence.IsMatch(frame))
                {
                    pageId = "currency_wars_home";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                previousPageId = null;
                stableCount = 0;
                eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Warning,
                    "PassiveRecoveryObservationFailed",
                    $"只读页面监测单次失败，将继续观察：{exception.Message}"));
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
                continue;
            }

            if (pageId is not null && SafeEntryPageIds.Contains(pageId))
            {
                stableCount = string.Equals(
                    previousPageId,
                    pageId,
                    StringComparison.OrdinalIgnoreCase)
                    ? stableCount + 1
                    : 1;
                previousPageId = pageId;
                if (stableCount >= 2)
                {
                    eventSink.Publish(new TaskEvent(
                        DateTimeOffset.Now,
                        TaskEventLevel.Information,
                        "PassiveRecoveryEntryDetected",
                        $"连续确认安全入口页 {pageId}，恢复自动导航。"));
                    return pageId;
                }
            }
            else
            {
                previousPageId = null;
                stableCount = 0;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }
}
