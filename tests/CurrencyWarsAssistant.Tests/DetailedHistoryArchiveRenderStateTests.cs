using System.Reflection;
using CurrencyWarsAssistant.App;
using Xunit.Sdk;

namespace CurrencyWarsAssistant.Tests;

public sealed class DetailedHistoryArchiveRenderStateTests
{
    [Fact]
    public void InFlightArchiveCannotStartTwice()
    {
        var state = CreateState();

        Assert.True(TryBegin(state, "run-a"));
        Assert.False(TryBegin(state, "run-a"));
    }

    [Fact]
    public void FailedArchiveCanRetryOnNextRefresh()
    {
        var state = CreateState();

        Assert.True(TryBegin(state, "run-a"));
        Complete(state, "run-a", succeeded: false);

        Assert.True(TryBegin(state, "run-a"));
    }

    [Fact]
    public void SuccessfulArchiveIsDeduplicatedUntilSourceChanges()
    {
        var state = CreateState();

        Assert.True(TryBegin(state, "run-a"));
        Complete(state, "run-a", succeeded: true);
        Assert.False(TryBegin(state, "run-a"));

        Reset(state);
        Assert.True(TryBegin(state, "run-a"));
    }

    [Fact]
    public void WindowDoesNotTreatStaleNodesAsActiveAndHidesStaleWebView()
    {
        var source = ReadDetailedHistoryWindowSource();

        Assert.DoesNotContain(
            "_viewModel.RealtimeNodeEntries.Count > 0",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReportBrowser.Visibility = Visibility.Collapsed",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await viewModel.RefreshCompletedRunsAsync();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_refreshTimer.Stop();",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeRefreshSkipsUnchangedContentAndConcurrentTick()
    {
        var state = CreateRealtimeState();

        Assert.True(TryBegin(state, "hash-a"));
        Assert.False(TryBegin(state, "hash-a"));
        Complete(state, "hash-a", succeeded: true);
        Assert.False(TryBegin(state, "hash-a"));
        Assert.True(TryBegin(state, "hash-b"));
    }

    [Fact]
    public void RealtimeRefreshKeepsVisibleWebViewWhileRegenerating()
    {
        var source = ReadDetailedHistoryWindowSource();
        var realtimeStart = source.IndexOf(
            "Kind: DetailedHistoryReportSourceKind.Realtime",
            StringComparison.Ordinal);
        var archiveStart = source.IndexOf(
            "Kind: DetailedHistoryReportSourceKind.Archive",
            realtimeStart,
            StringComparison.Ordinal);
        Assert.True(realtimeStart >= 0 && archiveStart > realtimeStart);

        var realtimeBranch = source[realtimeStart..archiveStart];
        Assert.DoesNotContain(
            "ReportBrowser.Visibility = Visibility.Collapsed",
            realtimeBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReportBrowser.CoreWebView2.Reload()",
            source,
            StringComparison.Ordinal);
    }

    private static object CreateState()
    {
        var type = typeof(MainViewModel).Assembly.GetType(
            "CurrencyWarsAssistant.App.DetailedHistoryArchiveRenderState")
            ?? throw new XunitException(
                "Detailed-history archive render state is missing.");
        return Activator.CreateInstance(type)
            ?? throw new XunitException(
                "Detailed-history archive render state could not be created.");
    }

    private static object CreateRealtimeState()
    {
        var type = typeof(MainViewModel).Assembly.GetType(
            "CurrencyWarsAssistant.App.DetailedHistoryRealtimeRenderState")
            ?? throw new XunitException(
                "Detailed-history realtime render state is missing.");
        return Activator.CreateInstance(type)
            ?? throw new XunitException(
                "Detailed-history realtime render state could not be created.");
    }

    private static bool TryBegin(object state, string runId) =>
        Invoke(state, "TryBegin", [runId]) as bool?
        ?? throw new XunitException("TryBegin returned no result.");

    private static void Complete(object state, string runId, bool succeeded) =>
        _ = Invoke(state, "Complete", [runId, succeeded]);

    private static void Reset(object state) =>
        _ = Invoke(state, "Reset", []);

    private static object? Invoke(
        object state,
        string methodName,
        object?[] arguments)
    {
        var method = state.GetType().GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new XunitException(
                $"Detailed-history archive state has no {methodName} method.");
        return method.Invoke(state, arguments);
    }

    private static string ReadDetailedHistoryWindowSource()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        return File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CurrencyWarsAssistant.App",
            "DetailedHistoryWindow.xaml.cs"));
    }
}
