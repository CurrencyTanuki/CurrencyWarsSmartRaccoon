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
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
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
    // 「1-3 三星五费」运行支持（0 = 未运行；非 0 = 正在刷）。
    private CancellationTokenSource? _threeStarFiveCostCts;
    private Task? _threeStarFiveCostTask;
    private GrailRecognitionListener? _threeStarFiveCostListener;
    // 注入的服务（供现场组装 FateGrailRunLoop）。
    private readonly IGameWindowService _gameWindowService;
    private readonly RewardStageAutomationController _rewardController;
    private readonly WishTrialSelectionAutomation _trialSelection;
    private readonly GameDataCatalog _gameData;
    private readonly IPhase2LiveCollectionService _liveCollection;
    private readonly OpeningRerollLoopCoordinator _openingCoordinator;
    private readonly IRunAbandoner _runAbandoner;
    private readonly PreparationBoardController _preparationBoard;
    private readonly TrialRecruitSelectionAutomation _trialRecruit;
    private readonly IGameCapture _capture;
    // 「刷三星五费」录像输出目录（点击「打开录像文件夹」按钮即打开这里）。
    private static readonly string RecordingOutputDirectory =
        Path.Combine(AppContext.BaseDirectory, "Recordings");

    public MainWindow(
        MainViewModel viewModel,
        SituationAnalysisViewModel situationAnalysis,
        LocalRunStore runStore,
        UiTaskEventSink eventSink,
        IGameWindowService gameWindowService,
        RewardStageAutomationController rewardController,
        WishTrialSelectionAutomation trialSelection,
        GameDataCatalog gameData,
        IPhase2LiveCollectionService liveCollection,
        OpeningRerollLoopCoordinator openingCoordinator,
        IRunAbandoner runAbandoner,
        PreparationBoardController preparationBoard,
        TrialRecruitSelectionAutomation trialRecruit,
        IGameCapture capture)
    {
        _viewModel = viewModel;
        _situationAnalysis = situationAnalysis;
        _runStore = runStore;
        _eventSink = eventSink;
        _gameWindowService = gameWindowService;
        _rewardController = rewardController;
        _trialSelection = trialSelection;
        _gameData = gameData;
        _liveCollection = liveCollection;
        _openingCoordinator = openingCoordinator;
        _runAbandoner = runAbandoner;
        _preparationBoard = preparationBoard;
        _trialRecruit = trialRecruit;
        _capture = capture;
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
        InputKillSwitch.Armed = true;
        _threeStarFiveCostCts?.Cancel();
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

    /// <summary>导航到主界面"刷三星五费"隔离功能区。</summary>
    private void OnThreeStarFiveCostClick(object sender, RoutedEventArgs e) =>
        ThreeStarFiveCostSection.BringIntoView();

    /// <summary>
    /// 「刷三星五费」开始：定位游戏窗口 → 组装整局循环 → 后台运行（真实点屏）。
    /// 目标模式取自 <see cref="ThreeStarFiveCostTargetCombo"/>（0=单人，1=全员）。
    /// </summary>
    private void OnStartThreeStarFiveCostClick(object sender, RoutedEventArgs e)
    {
        if (_threeStarFiveCostTask is { IsCompleted: false })
        {
            return; // 已在运行
        }

        var candidates = _gameWindowService.FindCandidates();
        var gameWindow = candidates.FirstOrDefault(window => window.IsReadyForAutomation);
        if (gameWindow is null)
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Error,
                "ThreeStarFiveCostNoGameWindow",
                "未找到可自动化的游戏窗口，无法启动「刷三星五费」。"));
            return;
        }

        if (_viewModel.IsPassiveCollectionRunning || _viewModel.IsRunning)
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Error,
                "ThreeStarFiveCostBusy",
                "识别流或自动化已在运行（开始记录/刷开局），请先停止再启动「刷三星五费」。"));
            return;
        }

        _viewModel.BeginGrailRun();
        InputKillSwitch.Armed = false; // 新任务启动解除急停闸

        var goal = ThreeStarFiveCostTargetCombo.SelectedIndex == 1
            ? GrailUserGoal.All
            : GrailUserGoal.Single;

        // 滚动录屏：勾选启用时先定位 ffmpeg；未装则不启动并自动打开下载页（用户拍板）。
        FateGrailRecordingOptions? recording = null;
        if (ThreeStarFiveCostRecordCheckBox.IsChecked == true)
        {
            var locator = FfmpegLocator.Locate();
            if (!locator.Found)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = locator.DownloadUrl,
                    UseShellExecute = true,
                }); }
                catch (Exception) { }
                _eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Warning,
                    "ThreeStarFiveCostNoFfmpeg",
                    "未检测到 ffmpeg，已为你打开下载页；安装后请重新点「刷三星五费」。"));
                return;
            }

            recording = new FateGrailRecordingOptions(
                _capture,
                FateGrailRecordingQuality.FromLevel((FateGrailRecordingQualityLevel)ThreeStarFiveCostQualityCombo.SelectedIndex),
                locator.ExecutablePath!,
                Path.Combine(Path.GetTempPath(), "GrailRecordingTemp"),
                RecordingOutputDirectory);
        }

        try
        {
            var stateHolder = new GrailRunStateHolder();
            var listener = new GrailRecognitionListener(_liveCollection);
            _threeStarFiveCostListener = listener;
            var executor = new GrailOperationExecutor(
                _rewardController, _preparationBoard, _trialSelection, _trialRecruit, stateHolder)
            {
                Goal = goal,
            };
            GrailRollingRecorder? assembledRecorder = null;
            var loop = new GrailRunLoop(_openingCoordinator.RunAsync, executor, stateHolder, listener, _gameData, _liveCollection, _runAbandoner);
            if (recording is not null)
            {
                assembledRecorder = new GrailRollingRecorder(
                    recording.Capture,
                    gameWindow,
                    recording.Quality,
                    recording.FfmpegPath,
                    recording.TempDirectory);
                loop.RoundRecorder = assembledRecorder;
                loop.RecordingOutputDirectory = recording.OutputDirectory;
            }

            _threeStarFiveCostCts = new CancellationTokenSource();
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "ThreeStarFiveCostStarted",
                $"「刷三星五费」开始（目标：{(goal == GrailUserGoal.All ? "全员" : "单人")}）。"));

            var filters = GrailRunLoop.BuildViableEnvironmentFilter();
            // 三星五费：命中的可推进环境（067/018/019）必须真正进入 1-1 并打完
            // 1-1/1-2 奖励关，才能到 1-3 决策；否则只停在投资环境页/打完 1-1 就停，
            // 走不到三星五费决策（刷到环境就停、刷到 1-1 就停的病根）。
            var options = new OpeningRerollLoopOptions
            {
                DeployMatchedOpening = true,
                CompleteRewardStages = true,
                // 三轮（用户拍板）：启用快速状态机——拖拽走裸拖+单帧兜底复查（缩放/竞态已修）
                FastReroll = FastRerollMode.Fast,
                // N1：命杯成员绝不自动卖出（商店只买命运圣杯羁绊成员）——其余配置见 RewardStage（商店只买命运圣杯羁绊成员）。
                BenchSaleMode = PreparationBenchSaleMode.None,
                // 投资策略偏好：二极管276（补血）+ 采购专员（抬5费刷出概率），由 Rewards 阶段选。
                RewardStage = new RewardStageAutomationOptions
                {
                    // N1：三仙舟+2DOT 购买预设禁用；银河学者（猫猫糕）策略启用；
                    // 1-1/1-2 商店买入命杯成员（同名只买一次），禁卖命杯与星徽携带者
                    EnableEarlyStrongFormationPurchase = false,
                    // 银河学者（猫猫糕）：沿用旧代码成熟实现（仅 1-1 生效，1-2 不生效）
                    EnableGalaxyScholarRewardStrategy = true,
                    // N11 软门槛：策略未命中选最左推进，绝不为此弃局（保住 067/昔涟等好开局）
                    SoftInvestmentStrategyRequirement = true,
                    AutoPurchaseCharacterNames = new HashSet<string>(["远坂凛", "吉尔伽美什", "Saber"]),
                    RetainedCharacterNames = new HashSet<string>(["远坂凛", "吉尔伽美什", "Saber", "Archer"]),
                    PreferredInvestmentStrategyIds = new HashSet<string>(
                        new[]
                        {
                            InvestmentStrategyPicker.PurchaseSpecialistColor,
                            GrailInvestmentStrategyDecider.ItIsHisFaultId,
                            InvestmentStrategyPicker.PurchaseSpecialistGold,
                            GrailInvestmentStrategyDecider.DiodeId,
                        },
                        System.StringComparer.OrdinalIgnoreCase),
                },
            };
            var cts = _threeStarFiveCostCts;
            var snapshotSource = listener;
            _threeStarFiveCostTask = Task.Run(async () =>
            {
                try
                {
                    var result = await loop.RunAsync(
                        gameWindow.Handle,
                        goal,
                        filters,
                        options,
                        new GrailLoopOptions { MaxRounds = 0 },
                        cts.Token);
                    _eventSink.Publish(new TaskEvent(
                        DateTimeOffset.Now,
                        result.Succeeded ? TaskEventLevel.Information : TaskEventLevel.Warning,
                        "ThreeStarFiveCostFinished",
                        result.Succeeded
                            ? $"「刷三星五费」达成：{result.Message}"
                            : $"「刷三星五费」未达成（已刷 {result.RoundsPlayed} 局）：{result.Message}"));
                }
                catch (OperationCanceledException)
                {
                    _eventSink.Publish(new TaskEvent(
                        DateTimeOffset.Now,
                        TaskEventLevel.Information,
                        "ThreeStarFiveCostStopped",
                        "「刷三星五费」已停止。"));
                }
                catch (Exception exception)
                {
                    _eventSink.Publish(new TaskEvent(
                        DateTimeOffset.Now,
                        TaskEventLevel.Error,
                        "ThreeStarFiveCostCrashed",
                        $"「刷三星五费」循环异常终止：{exception.Message}"));
                }
                finally
                {
                    // 任务结束（达成/上限/取消/异常）都退订识别流并释放互斥标志与急停闸
                    //（闸随任务解除：grail 已停，普通功能输入应恢复）
                    snapshotSource.Unsubscribe();
                    _viewModel.EndGrailRun();

                    // 录屏收尾：以 None 令牌执行（成功转正/失败删除/停止收尾），不因取消跳过
                    if (assembledRecorder is not null)
                    {
                        try
                        {
                            await assembledRecorder.FinishAsync(
                                false, RecordingOutputDirectory, CancellationToken.None);
                        }
                        catch { /* 停止路径收尾失败不掩盖主流程结果 */ }
                    }

                    InputKillSwitch.Armed = false;
                }
            }, cts.Token);
        }
        catch (Exception exception)
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Error,
                "ThreeStarFiveCostStartFailed",
                $"「刷三星五费」启动失败：{exception.Message}"));
        }
    }

    /// <summary>「停止」：取消当前「刷三星五费」运行，并置输入急停闸（停止=立即拒绝一切模拟输入）。</summary>
    private void OnStopThreeStarFiveCostClick(object sender, RoutedEventArgs e)
    {
        InputKillSwitch.Armed = true;
        _threeStarFiveCostCts?.Cancel();
        _threeStarFiveCostListener?.Unsubscribe();
        _threeStarFiveCostListener = null;
        _eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            "ThreeStarFiveCostStopped",
            "「刷三星五费」已请求停止。"));
    }

    /// <summary>「打开录像文件夹」：打开三星五费成功录像所在目录（不存在则先创建）。</summary>
    private void OnOpenRecordingFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RecordingOutputDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = RecordingOutputDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Error,
                "OpenRecordingFolderFailed",
                $"打开录像文件夹失败：{exception.Message}"));
        }
    }

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
            // 刷开局/记录开始前，强制开启日志窗 + 节点历史记录窗的鼠标穿透
            // （两者共用 IsLogOverlayClickThrough），避免鼠标误点到节点历史区域
            // 导致自动停止。属性变化会触发 OperationPanelWindow 立即应用 WsExTransparent。
            if (!_viewModel.IsLogOverlayClickThrough)
            {
                _viewModel.IsLogOverlayClickThrough = true;
            }

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
        // 云游戏浏览器（Edge）切前台会从全屏还原/缩小导致窗口乱动，
        // 后台截屏由 GdiGameCapture 的 PrintWindow 回退兜底，无需切前台。
        if (rawWindow.SourceKind != GameWindowSourceKind.CloudBrowser)
        {
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
        }
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
            // 紧急停止必须覆盖三星五费：置急停闸 + 取消其令牌（否则热键停止后 grail 继续点屏）
            InputKillSwitch.Armed = true;
            _threeStarFiveCostCts?.Cancel();
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
