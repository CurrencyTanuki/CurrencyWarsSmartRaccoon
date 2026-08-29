using System.Reflection;
using CurrencyWarsAssistant.App;
using Xunit.Sdk;

namespace CurrencyWarsAssistant.Tests;

public sealed class DetailedHistoryReportSourceSelectorTests
{
    [Fact]
    public void NoRealtimeUsesFirstArchiveFromCanonicalOrder()
    {
        var source = Select(
            hasRealtimeActivity: false,
            realtimeDirectory: null,
            ["run-newest", "run-older"]);

        Assert.Equal("Archive", source.Kind);
        Assert.Equal("run-newest", source.Identifier);
    }

    [Fact]
    public void CompletedRealtimeReportTakesPriorityOverArchive()
    {
        var source = Select(
            hasRealtimeActivity: true,
            realtimeDirectory: @"C:\temp\cwt-realtime\run-live",
            ["run-archive"]);

        Assert.Equal("Realtime", source.Kind);
        Assert.Equal(@"C:\temp\cwt-realtime\run-live", source.Identifier);
    }

    [Fact]
    public void PausedRealtimeReportTakesPriorityOverOlderArchive()
    {
        var source = Select(
            hasRealtimeActivity: false,
            realtimeDirectory: @"C:\temp\cwt-realtime\run-paused",
            ["run-archive"]);

        Assert.Equal("Realtime", source.Kind);
        Assert.Equal(@"C:\temp\cwt-realtime\run-paused", source.Identifier);
    }

    [Fact]
    public void ActiveRealtimeWithoutFinalBattleDoesNotShowOldArchive()
    {
        var source = Select(
            hasRealtimeActivity: true,
            realtimeDirectory: null,
            ["run-archive"]);

        Assert.Equal("WaitingForRealtime", source.Kind);
        Assert.Null(source.Identifier);
    }

    [Fact]
    public void NoRealtimeAndNoArchiveReturnsNoData()
    {
        var source = Select(
            hasRealtimeActivity: false,
            realtimeDirectory: null,
            []);

        Assert.Equal("None", source.Kind);
        Assert.Null(source.Identifier);
    }

    [Fact]
    public void SameArchiveRunIsNotRenderedTwice()
    {
        Assert.True(ShouldRenderArchive(null, "run-a"));
        Assert.False(ShouldRenderArchive("run-a", "run-a"));
        Assert.True(ShouldRenderArchive("run-a", "run-b"));
    }

    private static (string Kind, string? Identifier) Select(
        bool hasRealtimeActivity,
        string? realtimeDirectory,
        IReadOnlyList<string> archiveRunIds)
    {
        var selector = GetSelectorType();
        var method = selector.GetMethod(
            "Select",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new XunitException("Detailed-history report selector has no Select method.");
        var result = method.Invoke(
            null,
            [hasRealtimeActivity, realtimeDirectory, archiveRunIds])
            ?? throw new XunitException("Detailed-history report selector returned null.");
        var resultType = result.GetType();
        var kind = resultType.GetProperty("Kind")?.GetValue(result)?.ToString()
            ?? throw new XunitException("Detailed-history report source has no Kind.");
        var identifier = resultType.GetProperty("Identifier")?.GetValue(result) as string;
        return (kind, identifier);
    }

    private static bool ShouldRenderArchive(
        string? renderedArchiveRunId,
        string candidateArchiveRunId)
    {
        var selector = GetSelectorType();
        var method = selector.GetMethod(
            "ShouldRenderArchive",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new XunitException(
                "Detailed-history report selector has no ShouldRenderArchive method.");
        return method.Invoke(
            null,
            [renderedArchiveRunId, candidateArchiveRunId]) as bool?
            ?? throw new XunitException(
                "Detailed-history archive deduplication returned no result.");
    }

    private static Type GetSelectorType() =>
        typeof(MainViewModel).Assembly.GetType(
            "CurrencyWarsAssistant.App.DetailedHistoryReportSourceSelector")
        ?? throw new XunitException(
            "Detailed-history report source selector is missing.");
}
