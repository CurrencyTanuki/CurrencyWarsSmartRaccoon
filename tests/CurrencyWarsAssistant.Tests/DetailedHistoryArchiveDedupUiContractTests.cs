namespace CurrencyWarsAssistant.Tests;

public sealed class DetailedHistoryArchiveDedupUiContractTests
{
    [Fact]
    public void ArchiveLoadingStateChangesOnlyAfterRenderActuallyStarts()
    {
        var source = ReadDetailedHistoryWindowSource();
        var tryBegin = source.IndexOf(
            "if (!_archiveRenderState.TryBegin(archiveRunId))",
            StringComparison.Ordinal);
        var generate = source.IndexOf(
            "htmlPath = await ReportHtmlRenderer.GenerateAsync(archiveRunId);",
            tryBegin,
            StringComparison.Ordinal);

        Assert.True(tryBegin >= 0);
        Assert.True(generate > tryBegin);
        var archiveStart = source[tryBegin..generate];
        Assert.Contains(
            "ReportLoading.Visibility = Visibility.Visible",
            archiveStart,
            StringComparison.Ordinal);
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
