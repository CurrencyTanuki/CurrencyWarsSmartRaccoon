namespace CurrencyWarsAssistant.Tests;

public sealed class UiRedesignContractTests
{
    [Fact]
    public void MainUiKeepsCoreEntrypointsAndRemovesRepeatedInstructions()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        Assert.Contains("开始刷开局", xaml, StringComparison.Ordinal);
        Assert.Contains("开始记录", ReadAppFile("SituationAnalysisViewModel.cs"));
        Assert.Contains(
            "SituationAnalysis.StartCollectionCommand",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("识别当前画面", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("进入开局页", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SituationAnalysis.AnalyzeCurrentWindowCommand",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("NavigateCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("未完成对局", xaml, StringComparison.Ordinal);
        Assert.Contains("节点历史", xaml, StringComparison.Ordinal);
        Assert.Contains("运行日志", xaml, StringComparison.Ordinal);
        Assert.Contains("挑战总结", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ObserveCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "点击循环：不限制 → 必出 → 刷掉 → 不限制",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningFilterListsUseMultiColumnWrapLayout()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        Assert.Contains("<WrapPanel", xaml);
        Assert.DoesNotContain(
            "VirtualizingStackPanel Orientation=\"Vertical\"",
            xaml);
        Assert.DoesNotContain("VirtualizationMode\" Value=\"Recycling", xaml);
        Assert.DoesNotContain("ScrollViewer.CanContentScroll\" Value=\"True", xaml);
    }

    [Fact]
    public void StartupTimingStopsBeforeTheNonBlockingNoticeWindow()
    {
        var source = ReadAppFile("App.xaml.cs");
        var startupCompleted = source.IndexOf(
            "\"StartupCompleted\"",
            StringComparison.Ordinal);
        var noticeDialog = source.IndexOf(
            "new StartupNoticeWindow",
            StringComparison.Ordinal);

        Assert.True(startupCompleted >= 0 && noticeDialog > startupCompleted);
        Assert.Contains("Dispatcher.Yield(DispatcherPriority.Loaded)", source);
    }

    [Fact]
    public void StartupNoticeContinueClosesNonModalWindowWithoutDialogResult()
    {
        var notice = ReadAppFile("StartupNoticeWindow.xaml.cs");

        Assert.Contains(
            "private void OnContinueClick",
            notice,
            StringComparison.Ordinal);
        Assert.Contains("Close();", notice, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DialogResult =",
            notice,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupKeepsTheShellResponsiveAndRejectsRepeatedLaunches()
    {
        var app = ReadAppFile("App.xaml.cs");
        var startupWindow = ReadAppFile("StartupWindow.cs");

        Assert.Contains("TryAcquireSingleInstance()", app);
        Assert.Contains("SingleInstanceActivationName", app);
        Assert.Contains("StartSingleInstanceActivationListener()", app);
        Assert.Contains("phase2IconTemplatesTask", app);
        Assert.Contains("navigationTask", app);
        Assert.Contains("notice.Show()", app);
        Assert.DoesNotContain("}.ShowDialog();", app);
        Assert.Contains("Topmost = true", startupWindow);
    }

    [Fact]
    public void OpeningFilterEditorIsVisibleImmediatelyWithoutManualLoad()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var source = ReadAppFile("MainWindow.xaml.cs");

        var sectionStart = xaml.IndexOf(
            "x:Name=\"OpeningFiltersSection\"",
            StringComparison.Ordinal);
        Assert.True(sectionStart >= 0);
        var sectionEnd = xaml.IndexOf(
            "</Border>",
            sectionStart,
            StringComparison.Ordinal);
        var section = xaml[sectionStart..sectionEnd];

        Assert.DoesNotContain("Visibility=\"Collapsed\"", section);
        Assert.DoesNotContain("LoadOpeningFiltersButton", xaml);
        Assert.DoesNotContain("OpeningFiltersPlaceholder", xaml);
        Assert.DoesNotContain("OnLoadOpeningFiltersClick", source);
    }

    [Fact]
    public void DashboardUsesRequestedLayoutAndHiddenScrollbars()
    {
        var xaml = ReadAppFile("OperationPanelWindow.xaml");

        Assert.DoesNotContain(
            "仅显示当前界面无法直接回看的最终统计",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Height=\"620\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "HorizontalScrollBarVisibility=\"Hidden\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerticalScrollBarVisibility=\"Hidden\"",
            xaml,
            StringComparison.Ordinal);

        var theoryIndex = xaml.IndexOf(
            "Text=\"阵容理论出伤极限\"",
            StringComparison.Ordinal);
        var actionIndex = xaml.IndexOf(
            "Text=\"剩余行动值\"",
            StringComparison.Ordinal);
        Assert.True(theoryIndex >= 0 && actionIndex > theoryIndex);
    }

    [Fact]
    public void DashboardAutoScrollsAllChartsOnlyWhenLatestNodeChanges()
    {
        var xaml = ReadAppFile("OperationPanelWindow.xaml");
        var source = ReadAppFile("OperationPanelWindow.xaml.cs");

        Assert.Contains("x:Name=\"DamageChartScroller\"", xaml);
        Assert.Contains("x:Name=\"TheoryChartScroller\"", xaml);
        Assert.Contains("x:Name=\"GoldChartScroller\"", xaml);
        Assert.Contains("x:Name=\"ActionChartScroller\"", xaml);
        Assert.Contains("_lastAutoScrolledNodeId", source);
        Assert.Contains("DamageChartScroller.ScrollToRightEnd()", source);
        Assert.Contains("TheoryChartScroller.ScrollToRightEnd()", source);
        Assert.Contains("GoldChartScroller.ScrollToRightEnd()", source);
        Assert.Contains("ActionChartScroller.ScrollToRightEnd()", source);
    }

    [Fact]
    public void SystemFunctionsLiveInSettings()
    {
        var xaml = ReadAppFile("SettingsWindow.xaml");

        Assert.Contains("截图与识别", xaml, StringComparison.Ordinal);
        Assert.Contains("EnableDiagnosticLogging", xaml, StringComparison.Ordinal);
        Assert.Contains("CaptureCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ObserveCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("LogFilePath", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding LogFilePath, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("DeleteScreenshotsAfterRunCompletion", xaml);
        Assert.Contains("默认关闭", xaml);
    }

    [Fact]
    public void DashboardClickThroughKeepsCheckboxAndDetailButtonClickable()
    {
        var source = ReadAppFile("OperationPanelWindow.xaml.cs");

        var checkboxHitTest = source.IndexOf(
            "ContainsScreenPoint(ClickThroughToggle, screenPoint)",
            StringComparison.Ordinal);
        var transparentBranch = source.IndexOf(
            "if (_viewModel.IsLogOverlayClickThrough)",
            StringComparison.Ordinal);
        Assert.True(checkboxHitTest >= 0 && checkboxHitTest < transparentBranch);
        Assert.Contains("return HtClient;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PanelBorder.IsHitTestVisible =",
            source,
            StringComparison.Ordinal);
        Assert.Contains("WsExTransparent", source, StringComparison.Ordinal);
        Assert.Contains(
            "_toggleTarget?.PositionOver(ClickThroughToggle)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_toggleTarget?.Hide()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_detailsTarget?.PositionOver(DetailedHistoryButton)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_detailsTarget?.Hide()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DetailedHistoryUsesOutlineButtonAndRealtimeCollectedFields()
    {
        var overlay = ReadAppFile("OperationPanelWindow.xaml");
        var detail = ReadAppFile("DetailedHistoryWindow.xaml");
        var presentation = ReadAppFile("HistoricalDetailViewModels.cs");

        Assert.Contains("Content=\"详细历史\"", overlay);
        Assert.Contains("Background=\"Transparent\"", overlay);
        Assert.Contains("BorderBrush=\"#80F4F6F8\"", overlay);
        Assert.Contains("对局历史详细信息", detail);
        Assert.Contains("ReportBrowser", detail);
        Assert.Contains("x:Name=\"TitleDragSurface\"", detail);
        Assert.Contains("x:Name=\"CloseButton\"", detail);
        Assert.Contains("KeyDown=\"OnWindowKeyDown\"", detail);
        Assert.DoesNotContain(
            "x:Name=\"TitleBar\" MouseLeftButtonDown",
            detail);
        Assert.Contains("<Style TargetType=\"ScrollBar\">", detail);
        Assert.Contains("Microsoft YaHei UI", detail);
        Assert.Contains("AccentText", detail);
        Assert.Contains("历史阵容与角色装备", presentation);
        Assert.Contains("装备栏与局内资源", presentation);
        Assert.Contains("识别状态、原始 OCR 与诊断", presentation);
        var detailCode = ReadAppFile("DetailedHistoryWindow.xaml.cs");
        var overlayCode = ReadAppFile("OperationPanelWindow.xaml.cs");
        Assert.Contains("e.Key != Key.Escape", detailCode);
        Assert.Contains(
            "_detailedHistoryWindow is not { IsVisible: true }",
            overlayCode);
    }

    [Fact]
    public void LogOverlayUsesOsPassThroughAndExactVisibleGrip()
    {
        var source = ReadAppFile("LogOverlayWindow.xaml.cs");
        var xaml = ReadAppFile("LogOverlayWindow.xaml");
        var mainWindow = ReadAppFile("MainWindow.xaml.cs");

        Assert.Contains("WsExTransparent", source, StringComparison.Ordinal);
        Assert.Contains(
            "_dragTarget?.PositionOver(DragGrip)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Width=\"34\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"9\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "var top = workArea.Top + 14;",
            mainWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayLogsAppendAtBottomAndDiscardOldestFirst()
    {
        var source = ReadAppFile("MainViewModel.cs");

        Assert.Contains("LogLines.Add(message)", source, StringComparison.Ordinal);
        Assert.Contains(
            "OverlayLogLines.Add(new OverlayLogLine(message, foreground))",
            source,
            StringComparison.Ordinal);
        Assert.Contains("LogLines.RemoveAt(0)", source, StringComparison.Ordinal);
        Assert.Contains(
            "OverlayLogLines.RemoveAt(0)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecognitionTestDoesNotEnterRerollDecisionPipeline()
    {
        var source = ReadAppFile("MainViewModel.cs");
        var start = source.IndexOf(
            "private async Task RecognizeCurrentWindowAsync()",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private void OnOpeningProgressChanged",
            start,
            StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("_situationAnalyzer.AnalyzeAsync", method);
        Assert.DoesNotContain("BuildFilterSet", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RunFilterAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("blacklist", method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWindowDoesNotOpenDashboardDuringInitialRender()
    {
        var source = ReadAppFile("MainWindow.xaml.cs");
        var renderStart = source.IndexOf(
            "private async void OnContentRendered",
            StringComparison.Ordinal);
        var nextMethod = source.IndexOf(
            "private void OnResumeRequested",
            renderStart,
            StringComparison.Ordinal);
        var renderBody = source[renderStart..nextMethod];

        Assert.DoesNotContain("ShowOverlays", renderBody, StringComparison.Ordinal);
        Assert.Contains("PositionOperationPanel", source, StringComparison.Ordinal);
        Assert.Contains("PositionLogOverlay", source, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string fileName)
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
            fileName));
    }
}
