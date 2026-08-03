using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.App;

public partial class MainWindow : Window
{
    private const int StopHotKeyId = 0x4357;
    private const int ToggleLogClickThroughHotKeyId = 0x4358;
    private const int WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VirtualKeyF12 = 0x7B;
    private const uint VirtualKeyF11 = 0x7A;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private readonly MainViewModel _viewModel;
    private readonly SituationAnalysisViewModel _situationAnalysis;
    private readonly LocalRunStore _runStore;
    private readonly UiTaskEventSink _eventSink;
    private nint _handle;
    private LogOverlayWindow? _logOverlay;
    private OperationPanelWindow? _operationPanel;
    private CompletedRunsWindow? _completedRunsWindow;
    private bool _overlaysActivated;
    private bool _logOverlayPositioned;
    private bool _operationPanelPositioned;
    private HwndSource? _source;
    private bool _shutdownRequested;
    private bool _allowClose;
    private bool _nativeResourcesReleased;

    public MainWindow(
        MainViewModel viewModel,
        SituationAnalysisViewModel situationAnalysis,
        LocalRunStore runStore,
        UiTaskEventSink eventSink)
    {
        _viewModel = viewModel;
        _situationAnalysis = situationAnalysis;
        _runStore = runStore;
        _eventSink = eventSink;
        InitializeComponent();
        DataContext = _viewModel;
        SourceInitialized += OnSourceInitialized;
        _viewModel.AssistanceActivated += OnAssistanceActivated;
        _viewModel.ResumeRequested += OnResumeRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public SituationAnalysisViewModel SituationAnalysis =>
        _situationAnalysis;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessageHook);
        if (!RegisterHotKey(
                _handle,
                StopHotKeyId,
                ModControl | ModShift,
                VirtualKeyF12))
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "HotKeyRegistrationFailed",
                "紧急停止热键 Ctrl+Shift+F12 注册失败（可能被其他程序占用），该功能不可用。"));
        }

        if (!RegisterHotKey(
                _handle,
                ToggleLogClickThroughHotKeyId,
                ModControl | ModShift,
                VirtualKeyF11))
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "HotKeyRegistrationFailed",
                "日志穿透热键 Ctrl+Shift+F11 注册失败（可能被其他程序占用）。"));
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownRequested)
        {
            return;
        }

        _shutdownRequested = true;
        IsEnabled = false;
        PublishShutdownStage("ShutdownCloseEntered", "主窗口关闭流程已进入。");
        _viewModel.RequestShutdownStop();
        _situationAnalysis.RequestShutdownStop();
        PublishShutdownStage(
            "ShutdownCancellationSent",
            "已停止接收新任务并发送后台取消请求。");

        try
        {
            using var shutdownDeadline = new CancellationTokenSource(
                ShutdownTimeout);
            var idleResults = await Task.WhenAll(
                    _viewModel.WaitForIdleAsync(
                        ShutdownTimeout,
                        shutdownDeadline.Token),
                    _situationAnalysis.WaitForIdleAsync(
                        ShutdownTimeout,
                        shutdownDeadline.Token))
                .WaitAsync(shutdownDeadline.Token);
            if (idleResults.Any(result => !result))
            {
                PublishShutdownStage(
                    "ShutdownTasksTimedOut",
                    "后台任务未能在共享的 3 秒期限内全部结束。");
                _eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Warning,
                    "ShutdownDrainTimedOut",
                    "后台任务未能在 3 秒内完全结束；已保留现有断点并继续退出。"));
            }
            else
            {
                PublishShutdownStage(
                    "ShutdownTasksFinished",
                    "后台任务已在退出期限内结束。");
            }
        }
        catch (OperationCanceledException)
        {
            PublishShutdownStage(
                "ShutdownTasksTimedOut",
                "后台任务未能在共享的 3 秒期限内全部结束。");
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "ShutdownDrainTimedOut",
                "后台任务未能在 3 秒内完全结束；已保留现有断点并继续退出。"));
        }
        catch (Exception exception)
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "ShutdownDrainFailed",
                $"退出前停止后台任务失败：{exception.GetType().Name}: {exception.Message}"));
        }
        finally
        {
            _viewModel.SaveUserSettings();
            ReleaseWindowResources();
            PublishShutdownStage(
                "ShutdownAuxiliaryWindowsClosed",
                "辅助窗口、钩子和热键已释放。");
            _allowClose = true;
            PublishShutdownStage(
                "ShutdownSecondCloseQueued",
                "已排队执行最终窗口关闭。");
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(Close));
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PublishShutdownStage("ShutdownWindowClosed", "主窗口已关闭。");
        _viewModel.AssistanceActivated -= OnAssistanceActivated;
        _viewModel.ResumeRequested -= OnResumeRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ContentRendered -= OnContentRendered;
        Closing -= OnClosing;
        ReleaseWindowResources();
        // ShutdownMode=OnExplicitShutdown 下，窗口关闭不会自动结束进程，
        // 必须显式 Shutdown 才能保证退出后进程完全消失（真实退出测试要求）。
        Application.Current.Shutdown(0);
    }

    private void PublishShutdownStage(string code, string message) =>
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            code,
            message));

    private void ReleaseWindowResources()
    {
        if (_nativeResourcesReleased)
        {
            return;
        }

        _nativeResourcesReleased = true;
        Mouse.Capture(null);
        _logOverlay?.Close();
        _logOverlay = null;
        _operationPanel?.Close();
        _operationPanel = null;
        foreach (Window ownedWindow in OwnedWindows.Cast<Window>().ToArray())
        {
            ownedWindow.Close();
        }

        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        if (_handle != 0)
        {
            _ = UnregisterHotKey(_handle, StopHotKeyId);
            _ = UnregisterHotKey(_handle, ToggleLogClickThroughHotKeyId);
            _handle = 0;
        }
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        try
        {
            await _viewModel.RefreshIncompleteRunsAsync();
        }
        catch (Exception exception)
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Error,
                "InitialDataRefreshFailed",
                $"初始化数据刷新失败：{exception.GetType().Name}: {exception.Message}"));
        }
    }

    private void OnResumeRequested(object? sender, RunResumeRequestedEventArgs e)
    {
        _situationAnalysis.PrepareResume(e.Checkpoint);
        _situationAnalysis.StartPreparedResume();
        _overlaysActivated = true;
        ShowOverlays(activate: false);
    }

    private void OnShowLogWindowClick(object sender, RoutedEventArgs e)
    {
        _overlaysActivated = true;
        if (_logOverlay?.IsVisible == true)
        {
            _logOverlay.Hide();
            return;
        }

        ApplyLogOverlayVisibility();
    }

    private void OnHomeNavigationClick(object sender, RoutedEventArgs e) =>
        MainContentScroller.ScrollToTop();

    private void OnRunsNavigationClick(object sender, RoutedEventArgs e) =>
        IncompleteRunsSection.BringIntoView();

    /// <summary>
    /// 主界面"对局历史记录"页签：直接打开"历史对局"窗口
    /// （左侧 8 个存档列表 + 右侧 HTML 对局报告），一步直达；
    /// 单例——已存在时只激活不重复新建。
    /// </summary>
    private void OnRunHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_completedRunsWindow is { IsLoaded: true })
        {
            _completedRunsWindow.Show();
            _completedRunsWindow.Activate();
            return;
        }

        _completedRunsWindow = new CompletedRunsWindow(_viewModel) { Owner = this };
        _completedRunsWindow.Closed += (_, _) => _completedRunsWindow = null;
        _completedRunsWindow.Show();
    }

    private void OnDashboardNavigationClick(object sender, RoutedEventArgs e)
    {
        _overlaysActivated = true;
        if (_operationPanel?.IsVisible == true)
        {
            _operationPanel.Hide();
            return;
        }

        ShowOperationPanel(activate: true);
    }

    private void OnAssistanceActivated(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() =>
        {
            _overlaysActivated = true;
            ShowOverlays(activate: false);
        });

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_viewModel)
        {
            Owner = this
        };
        _ = settings.ShowDialog();
        ApplyLogOverlayVisibility();
    }

    private async void OnCalibrateGameAreaClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = _viewModel.SelectedWindow;
        if (selected is null)
        {
            _ = MessageBox.Show(
                this,
                "请先选择一个游戏窗口。",
                "定位游戏画面",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var rawWindow = selected with
        {
            ClientArea = selected.HostClientArea,
            HostClientAreaOverride = null,
            BindingState = GameWindowBindingState.Ready,
            BindingMessage = ""
        };
        if (!_viewModel.BringWindowToForeground(rawWindow))
        {
            _ = MessageBox.Show(
                this,
                "无法将所选窗口切到前台，请手动打开该窗口后重试。",
                "定位游戏画面",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await Task.Delay(250);
        var calibration = new GameAreaCalibrationWindow(
            rawWindow,
            _viewModel)
        {
            Owner = this
        };
        await calibration.LoadPreviewAsync();
        _ = calibration.ShowDialog();
    }

    private void OnRealtimeRecognitionStarted(
        object sender,
        RoutedEventArgs e)
    {
        _overlaysActivated = true;
        _ = Dispatcher.BeginInvoke(
            () => ShowOverlays(activate: false),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnStopAllClick(object sender, RoutedEventArgs e) =>
        _viewModel.RequestStop();

    private async void OnOpenLatestChallengeReportClick(
        object sender,
        RoutedEventArgs e)
    {
        var reportPath = Directory.Exists(_runStore.RootDirectory)
            ? Directory
                .EnumerateFiles(
                    _runStore.RootDirectory,
                    "challenge-summary.html",
                    SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName
            : null;
        // 尚未生成报告时，尝试用最新已完成对局现场生成，避免
        // “还没有生成挑战总结”提示后无法直接查看。
        if (reportPath is null)
        {
            reportPath = await _viewModel
                .TryGenerateLatestChallengeReportAsync()
                .ConfigureAwait(true);
        }

        if (reportPath is null)
        {
            _ = MessageBox.Show(
                this,
                "还没有生成挑战总结。完成一局并封存数据后，报告会自动出现在该对局的 reports 目录。",
                "挑战总结（实验版）",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = reportPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
            when (exception is Win32Exception or IOException)
        {
            _ = MessageBox.Show(
                this,
                $"无法打开挑战总结：{exception.Message}\n\n文件仍保留在：{reportPath}",
                "挑战总结（实验版）",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowLogOverlay))
        {
            Dispatcher.Invoke(ApplyLogOverlayVisibility);
        }
    }

    private void OnAddCombinationRuleClick(object sender, RoutedEventArgs e)
    {
        var editor = new CombinationRuleEditorWindow(_viewModel)
        {
            Owner = this
        };
        _ = editor.ShowDialog();
    }

    private void OnAddRerollProfileClick(
        object sender,
        RoutedEventArgs e) =>
        ShowRerollProfileEditor(null);

    private void OnEditRerollProfileClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: RerollProfileViewModel profile
            })
        {
            ShowRerollProfileEditor(profile);
        }
    }

    private void OnDeleteRerollProfileClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: RerollProfileViewModel profile
            })
        {
            _viewModel.DeleteRerollProfile(profile);
        }
    }

    private void ShowRerollProfileEditor(
        RerollProfileViewModel? existing)
    {
        var editor = new RerollProfileEditorWindow(
            _viewModel.CreateRerollProfileEditor(existing))
        {
            Owner = this
        };
        if (editor.ShowDialog() == true && editor.Result is not null)
        {
            _viewModel.AddOrReplaceRerollProfile(editor.Result);
        }
    }

    private void ShowOverlays(bool activate)
    {
        ShowOperationPanel(activate);
        ApplyLogOverlayVisibility();
    }

    private void ShowOperationPanel(bool activate)
    {
        if (_operationPanel is null)
        {
            _operationPanel = new OperationPanelWindow(_viewModel);
            _operationPanel.Closed += (_, _) =>
            {
                _operationPanel = null;
                _operationPanelPositioned = false;
            };
        }

        if (!_operationPanel.IsVisible)
        {
            _operationPanel.Show();
        }
        PositionOperationPanel();
        _operationPanel.EnsureTopmost();

        if (activate && !_viewModel.IsLogOverlayClickThrough)
        {
            _operationPanel.Activate();
        }
    }

    private void ApplyLogOverlayVisibility()
    {
        if (!_overlaysActivated || !_viewModel.ShowLogOverlay)
        {
            _logOverlay?.Hide();
            return;
        }

        if (_logOverlay is null)
        {
            _logOverlay = new LogOverlayWindow(_viewModel);
            _logOverlay.Closed += (_, _) =>
            {
                _logOverlay = null;
                _logOverlayPositioned = false;
            };
        }

        if (!_logOverlay.IsVisible)
        {
            _logOverlay.Show();
        }

        PositionLogOverlay();
        _logOverlay.EnsureTopmost();
    }

    private void PositionOperationPanel()
    {
        if (_operationPanel is null || _operationPanelPositioned)
        {
            return;
        }

        _operationPanel.UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        var width = _operationPanel.ActualWidth > 0
            ? _operationPanel.ActualWidth
            : _operationPanel.Width;
        _operationPanel.Left = Math.Max(
            workArea.Left + 20,
            workArea.Right - width - 24);
        _operationPanel.Top = workArea.Top + 20;
        _operationPanelPositioned = true;
    }

    private void PositionLogOverlay()
    {
        if (_logOverlay is null || _logOverlayPositioned)
        {
            return;
        }

        _logOverlay.UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        var logWidth = _logOverlay.ActualWidth > 0
            ? _logOverlay.ActualWidth
            : _logOverlay.Width;
        var logHeight = _logOverlay.ActualHeight > 0
            ? _logOverlay.ActualHeight
            : _logOverlay.MinHeight;
        var left = workArea.Left + 24;
        // 用户确认：日志悬浮窗默认放在屏幕左上角贴近顶部（截图 y≈10）。
        // 历史节点面板在右上角，两者默认不重叠。
        var top = workArea.Top + 14;

        if (_operationPanel?.IsVisible == true)
        {
            var panelWidth = _operationPanel.ActualWidth > 0
                ? _operationPanel.ActualWidth
                : _operationPanel.Width;
            var panelHeight = _operationPanel.ActualHeight > 0
                ? _operationPanel.ActualHeight
                : _operationPanel.Height;
            var panelBounds = new Rect(
                _operationPanel.Left,
                _operationPanel.Top,
                panelWidth,
                panelHeight);
            var logBounds = new Rect(left, top, logWidth, logHeight);
            if (panelBounds.IntersectsWith(logBounds))
            {
                top = Math.Max(
                    workArea.Top + 20,
                    workArea.Bottom - logHeight - 24);
            }
        }

        _logOverlay.Left = left;
        _logOverlay.Top = top;
        _logOverlayPositioned = true;
    }

    private nint WindowMessageHook(
        nint handle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message == WmHotKey && wordParameter == StopHotKeyId)
        {
            _viewModel.RequestStop();
            handled = true;
        }
        else if (message == WmHotKey &&
                 wordParameter == ToggleLogClickThroughHotKeyId)
        {
            _viewModel.IsLogOverlayClickThrough =
                !_viewModel.IsLogOverlayClickThrough;
            handled = true;
        }

        return 0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint window,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
