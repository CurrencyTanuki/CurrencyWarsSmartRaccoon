using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Tests;

public sealed class UserLogPolicyTests
{
    [Theory]
    [InlineData(TaskEventLevel.Debug, "Phase2FrameDiagnostic", false)]
    [InlineData(TaskEventLevel.Information, "WaitingForPage", false)]
    [InlineData(TaskEventLevel.Information, "Phase2CollectionUpdated", false)]
    [InlineData(TaskEventLevel.Information, "Phase2CollectionMilestone", true)]
    [InlineData(TaskEventLevel.Information, "Navigating", true)]
    [InlineData(TaskEventLevel.Information, "GameFocusPaused", false)]
    [InlineData(TaskEventLevel.Warning, "GameFocusPaused", false)]
    [InlineData(TaskEventLevel.Warning, "RecognitionIncomplete", true)]
    [InlineData(TaskEventLevel.Error, "UnhandledUiException", true)]
    public void VisibilitySeparatesUserMilestonesFromDiagnostics(
        TaskEventLevel level,
        string code,
        bool expected)
    {
        var taskEvent = Event(level, code, "message");

        Assert.Equal(expected, UserLogPolicy.IsUserVisible(taskEvent));
    }

    [Fact]
    public void DuplicateUnchangedMessagesAreThrottledWithoutHidingFirstEvent()
    {
        var policy = new UserLogPolicy();
        var first = Event(
            TaskEventLevel.Information,
            "Navigating",
            "进入开局识别",
            DateTimeOffset.Parse("2026-07-31T10:00:00+08:00"));

        Assert.True(policy.ShouldPublish(first));
        Assert.False(policy.ShouldPublish(first with
        {
            Timestamp = first.Timestamp.AddSeconds(1)
        }));
        Assert.True(policy.ShouldPublish(first with
        {
            Timestamp = first.Timestamp.AddSeconds(9)
        }));
    }

    [Fact]
    public void HiddenUiDiagnosticsRemainInDiagnosticFile()
    {
        var root = TemporaryDirectory();
        try
        {
            var visible = new List<TaskEvent>();
            string logPath;
            using (var sink = new UiTaskEventSink(root))
            {
                logPath = sink.LogFilePath;
                sink.EventPublished += (_, taskEvent) => visible.Add(taskEvent);
                sink.Publish(Event(
                    TaskEventLevel.Information,
                    "Phase2CollectionUpdated",
                    "internal frame confirmation"));
            }

            Assert.Empty(visible);
            Assert.Contains(
                "internal frame confirmation",
                File.ReadAllText(logPath),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DisablingDiagnosticsStillRetainsWarningsButDropsHiddenNoise()
    {
        var root = TemporaryDirectory();
        try
        {
            string logPath;
            using (var sink = new UiTaskEventSink(root)
                   {
                       DiagnosticLoggingEnabled = false
                   })
            {
                logPath = sink.LogFilePath;
                sink.Publish(Event(
                    TaskEventLevel.Information,
                    "Phase2CollectionUpdated",
                    "hidden-noise"));
                sink.Publish(Event(
                    TaskEventLevel.Warning,
                    "RecognitionIncomplete",
                    "actionable-warning"));
            }

            var log = File.ReadAllText(logPath);
            Assert.DoesNotContain("hidden-noise", log, StringComparison.Ordinal);
            Assert.Contains("actionable-warning", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ErrorDisplayUsesOneBoundedLineAndActionableAdvice()
    {
        var formatted = UserLogMessageFormatter.Format(Event(
            TaskEventLevel.Error,
            "Failure",
            "first line\r\nstack trace line"));

        Assert.DoesNotContain("stack trace", formatted, StringComparison.Ordinal);
        Assert.Contains("保留诊断日志", formatted, StringComparison.Ordinal);
    }

    private static TaskEvent Event(
        TaskEventLevel level,
        string code,
        string message,
        DateTimeOffset? timestamp = null) => new(
            timestamp ?? DateTimeOffset.Now,
            level,
            code,
            message);

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
