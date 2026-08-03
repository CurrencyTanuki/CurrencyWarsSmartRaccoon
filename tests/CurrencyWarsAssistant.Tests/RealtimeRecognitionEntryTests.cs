namespace CurrencyWarsAssistant.Tests;

public sealed class RealtimeRecognitionEntryTests
{
    [Fact]
    public void MainWindowKeepsOnlyContinuousRecognitionActions()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        Assert.Contains("SituationAnalysis.StartCollectionCommand", xaml);
        Assert.Contains("SituationAnalysis.CollectionStartLabel", xaml);
        Assert.DoesNotContain("SituationAnalysis.AnalyzeCurrentWindowCommand", xaml);
        Assert.DoesNotContain("NavigateCommand", xaml);
        Assert.DoesNotContain("识别当前画面", xaml);
        Assert.DoesNotContain("进入开局页", xaml);
        Assert.DoesNotContain("OnSituationAnalysisClick", xaml);
    }

    [Fact]
    public void DeprecatedAnalysisWindowIsRemovedFromProductionRegistration()
    {
        var app = ReadAppFile("App.xaml.cs");
        var applicationDirectory = Path.GetDirectoryName(
            AppFile("App.xaml.cs"))!;

        Assert.DoesNotContain("SituationAnalysisWindow", app);
        Assert.False(File.Exists(Path.Combine(
            applicationDirectory,
            "SituationAnalysisWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(
            applicationDirectory,
            "SituationAnalysisWindow.xaml.cs")));
    }

    [Fact]
    public void PassiveCollectionBlocksCompetingAutomationAndSharesStop()
    {
        var main = ReadAppFile("MainViewModel.cs");
        var analysis = ReadAppFile("SituationAnalysisViewModel.cs");

        Assert.Contains("BeginPassiveCollection", analysis);
        Assert.Contains("EndPassiveCollection", analysis);
        Assert.Contains("_passiveCollectionCancellation?.Cancel()", main);
        Assert.Contains("!IsPassiveCollectionRunning", main);
    }

    [Fact]
    public void LongRunningActionsRenderImmediateFeedbackBeforeSynchronousSetup()
    {
        var main = ReadAppFile("MainViewModel.cs");
        var analysis = ReadAppFile("SituationAnalysisViewModel.cs");
        var window = ReadAppFile("MainWindow.xaml.cs");
        var runFilter = main[main.IndexOf(
            "private async Task RunFilterAsync",
            StringComparison.Ordinal)..];
        var startCollection = analysis[analysis.IndexOf(
            "private async Task StartCollectionAsync",
            StringComparison.Ordinal)..];

        Assert.True(
            runFilter.IndexOf("已接收开始指令", StringComparison.Ordinal) <
            runFilter.IndexOf("AssistanceActivated?.Invoke", StringComparison.Ordinal));
        Assert.True(
            startCollection.IndexOf("已接收开始指令", StringComparison.Ordinal) <
            startCollection.IndexOf("ResolveGameWindow()", StringComparison.Ordinal));
        Assert.Contains("DispatcherPriority.Render", main);
        Assert.Contains("DispatcherPriority.Render", analysis);
        Assert.Contains("DispatcherPriority.Background", window);
    }

    private static string ReadAppFile(string fileName) =>
        File.ReadAllText(AppFile(fileName));

    private static string AppFile(string fileName)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        return Path.Combine(
            repositoryRoot,
            "src",
            "CurrencyWarsAssistant.App",
            fileName);
    }
}
