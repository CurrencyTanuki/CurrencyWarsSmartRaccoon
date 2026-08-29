using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.App;

public sealed class UserLogPolicy
{
    private static readonly IReadOnlySet<string> DiagnosticOnlyCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GameFocusPaused",
            "GameFocusResumed"
        };

    private static readonly IReadOnlySet<string> VisibleInformationCodes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Navigating",
            "Evaluating",
            "Recovering",
            "Matched",
            "WaitingForRecovery",
            "RecoveryStarted",
            "OpeningRecognized",
            "ReachedPreparation",
            "RewardAutomationStarted",
            "EmergencyStopRequested",
            "CaptureSucceeded",
            "Phase2ScreenshotAnalyzed",
            "Phase2LiveCollectionStarted",
            "Phase2LiveCollectionStopped",
            "Phase2CollectionMilestone",
            "RunResumeDecision",
            "UnifiedRunLifecycleMilestone",
            "RunCompleted"
        };
    private static readonly TimeSpan DuplicateInformationWindow =
        TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DuplicateWarningWindow =
        TimeSpan.FromSeconds(4);
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastPublished =
        new(StringComparer.Ordinal);

    public bool ShouldPublish(TaskEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        if (!IsUserVisible(taskEvent))
        {
            return false;
        }

        if (taskEvent.Level == TaskEventLevel.Error)
        {
            return true;
        }

        var key = $"{taskEvent.Level}|{taskEvent.Code}|{taskEvent.Message}";
        var window = taskEvent.Level == TaskEventLevel.Warning
            ? DuplicateWarningWindow
            : DuplicateInformationWindow;
        lock (gate)
        {
            if (lastPublished.TryGetValue(key, out var previous) &&
                taskEvent.Timestamp - previous < window)
            {
                return false;
            }

            lastPublished[key] = taskEvent.Timestamp;
            if (lastPublished.Count > 256)
            {
                var threshold = taskEvent.Timestamp - TimeSpan.FromMinutes(5);
                foreach (var stale in lastPublished
                             .Where(item => item.Value < threshold)
                             .Select(item => item.Key)
                             .ToArray())
                {
                    lastPublished.Remove(stale);
                }
            }

            return true;
        }
    }

    public static bool IsUserVisible(TaskEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        if (DiagnosticOnlyCodes.Contains(taskEvent.Code))
        {
            return false;
        }

        if (taskEvent.Level is TaskEventLevel.Error or TaskEventLevel.Warning)
        {
            return true;
        }

        if (taskEvent.Level == TaskEventLevel.Debug)
        {
            return false;
        }

        return VisibleInformationCodes.Contains(taskEvent.Code) ||
               taskEvent.Code.EndsWith("Succeeded", StringComparison.OrdinalIgnoreCase) ||
               taskEvent.Code.EndsWith("Completed", StringComparison.OrdinalIgnoreCase);
    }
}

public static class UserLogMessageFormatter
{
    private const int MaximumDisplayLength = 360;

    public static string Format(TaskEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        var firstLine = taskEvent.Message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? "未提供详细信息。";
        if (firstLine.Length > MaximumDisplayLength)
        {
            firstLine = firstLine[..MaximumDisplayLength] + "…";
        }

        return taskEvent.Level == TaskEventLevel.Error
            ? firstLine + " 请保留诊断日志并在停止操作后检查游戏页面。"
            : firstLine;
    }
}
