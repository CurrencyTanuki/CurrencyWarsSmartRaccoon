namespace CurrencyWarsAssistant.Tests;

public sealed class ApplicationShutdownContractTests
{
    [Fact]
    public void MainWindowCloseUsesBoundedAsynchronousShutdown()
    {
        var app = ReadAppFile("App.xaml");
        var window = ReadAppFile("MainWindow.xaml.cs");
        var mainViewModel = ReadAppFile("MainViewModel.cs");
        var situationViewModel = ReadAppFile("SituationAnalysisViewModel.cs");

        Assert.Contains("ShutdownMode=\"OnMainWindowClose\"", app);
        Assert.Contains("Closing += OnClosing", window);
        Assert.Contains("e.Cancel = true", window);
        Assert.Contains("TimeSpan.FromSeconds(3)", window);
        Assert.Contains("await Task.WhenAll", window);
        Assert.Contains(".WaitAsync(shutdownDeadline.Token)", window);
        Assert.Contains("catch (OperationCanceledException)", window);
        Assert.Contains("Dispatcher.BeginInvoke", window);
        Assert.Contains("ShutdownSecondCloseQueued", window);
        Assert.DoesNotContain(
            "_allowClose = true;\n            Close();",
            window.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("RequestShutdownStop", mainViewModel);
        Assert.Contains("WaitForIdleAsync", mainViewModel);
        Assert.Contains("RequestShutdownStop", situationViewModel);
        Assert.Contains("WaitForIdleAsync", situationViewModel);
        Assert.Contains("Mouse.Capture(null)", window);
        Assert.Contains("RemoveHook(WindowMessageHook)", window);
        Assert.DoesNotContain("Environment.Exit", window);
        Assert.DoesNotContain(".Wait(", window);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", window);
        Assert.DoesNotContain("Task.WaitAll", window);
    }

    [Fact]
    public void ShutdownCleanupIsIdempotentAndReleasesOverlayWindows()
    {
        var window = ReadAppFile("MainWindow.xaml.cs");

        Assert.Contains("if (_nativeResourcesReleased)", window);
        Assert.Contains("_logOverlay?.Close()", window);
        Assert.Contains("_operationPanel?.Close()", window);
        Assert.Contains("ownedWindow.Close()", window);
        Assert.Contains("UnregisterHotKey", window);
    }

    [Fact]
    public async Task SharedShutdownDeadlineBoundsANonCooperativeTask()
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));
        var neverCompletes = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.WhenAll(neverCompletes.Task)
                .WaitAsync(deadline.Token));
        stopwatch.Stop();

        Assert.InRange(stopwatch.ElapsedMilliseconds, 50, 2_000);
    }

    private static string ReadAppFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "CurrencyWarsAssistant.App",
            fileName));
}
