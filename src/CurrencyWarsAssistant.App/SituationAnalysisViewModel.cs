using System.IO;
using System.ComponentModel;
using System.Windows;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Microsoft.Win32;

namespace CurrencyWarsAssistant.App;

public sealed class SituationAnalysisViewModel : ObservableObject, IDisposable
{
    private readonly ISituationScreenshotAnalyzer _analyzer;
    private readonly IPhase2LiveCollectionService _collector;
    private readonly IGameWindowService _windowService;
    private readonly IGameCapture _capture;
    private readonly MainViewModel _mainViewModel;
    private readonly UiTaskEventSink _eventSink;
    private string _selectedFile = "";
    private string _status = "选择一张 16:9 游戏截图，或对当前游戏窗口进行只读分析。";
    private string _pageSummary = "尚未分析";
    private string _stateSummary = "尚未分析";
    private string _routeSummary = "尚未分析";
    private string _recommendationSummary = "尚未分析";
    private string _evidenceSummary = "尚未分析";
    private string _warningsSummary = "尚未分析";
    private string _rawJson = "";
    private bool _isBusy;
    private bool _isCollecting;
    private CancellationTokenSource? _collectionCancellation;
    private CancellationTokenSource? _analysisCancellation;
    private readonly object _activeTaskSync = new();
    private Task? _activeAnalysisTask;
    private Task? _activeCollectionTask;
    private long _analysisGeneration;
    private long _collectionGeneration;
    private bool _shutdownRequested;
    private RunCheckpointSummary? _resumeCheckpoint;
    private string _collectionStartLabel = "开始记录";
    private string _resumeSummary = "";

    public SituationAnalysisViewModel(
        ISituationScreenshotAnalyzer analyzer,
        IPhase2LiveCollectionService collector,
        IGameWindowService windowService,
        IGameCapture capture,
        MainViewModel mainViewModel,
        UiTaskEventSink eventSink)
    {
        _analyzer = analyzer;
        _collector = collector;
        _windowService = windowService;
        _capture = capture;
        _mainViewModel = mainViewModel;
        _eventSink = eventSink;
        _collector.Updated += OnCollectorUpdated;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;

        ChooseFileCommand = new AsyncRelayCommand(
            () => TrackAnalysisAsync(ChooseAndAnalyzeAsync),
            CanAnalyze);
        AnalyzeFileCommand = new AsyncRelayCommand(
            () => TrackAnalysisAsync(AnalyzeSelectedFileAsync),
            CanAnalyzeFile);
        StartCollectionCommand = new AsyncRelayCommand(
            () => TrackCollectionAsync(StartCollectionAsync),
            CanStartCollection);
        StopCollectionCommand = new RelayCommand(
            StopCollection,
            () => IsCollecting);
    }

    public string SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                NotifyCommands();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string PageSummary
    {
        get => _pageSummary;
        private set => SetProperty(ref _pageSummary, value);
    }

    public string StateSummary
    {
        get => _stateSummary;
        private set => SetProperty(ref _stateSummary, value);
    }

    public string RouteSummary
    {
        get => _routeSummary;
        private set => SetProperty(ref _routeSummary, value);
    }

    public string RecommendationSummary
    {
        get => _recommendationSummary;
        private set => SetProperty(ref _recommendationSummary, value);
    }

    public string EvidenceSummary
    {
        get => _evidenceSummary;
        private set => SetProperty(ref _evidenceSummary, value);
    }

    public string WarningsSummary
    {
        get => _warningsSummary;
        private set => SetProperty(ref _warningsSummary, value);
    }

    public string RawJson
    {
        get => _rawJson;
        private set => SetProperty(ref _rawJson, value);
    }

    public bool IsCollecting
    {
        get => _isCollecting;
        private set
        {
            if (SetProperty(ref _isCollecting, value))
            {
                NotifyCommands();
            }
        }
    }

    public string CollectionStartLabel
    {
        get => _collectionStartLabel;
        private set => SetProperty(ref _collectionStartLabel, value);
    }

    public string ResumeSummary
    {
        get => _resumeSummary;
        private set => SetProperty(ref _resumeSummary, value);
    }

    public AsyncRelayCommand ChooseFileCommand { get; }
    public AsyncRelayCommand AnalyzeFileCommand { get; }
    public AsyncRelayCommand StartCollectionCommand { get; }
    public RelayCommand StopCollectionCommand { get; }

    public void PrepareResume(RunCheckpointSummary checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _resumeCheckpoint = checkpoint;
        CollectionStartLabel = "继续记录";
        ResumeSummary =
            $"将续录 {checkpoint.Checkpoint.CreatedAtUtc.ToLocalTime():MM-dd HH:mm} " +
            $"开始的对局；断点节点：{checkpoint.Checkpoint.LastConfirmedNodeId ?? "未确认"}。";
        Status = "开始前会先读取当前游戏节点，避免把更早节点或另一局误并入旧记录。";
        NotifyCommands();
    }

    public void StartPreparedResume() => StartCollectionCommand.Execute(null);

    public void RequestShutdownStop()
    {
        lock (_activeTaskSync)
        {
            _shutdownRequested = true;
        }

        _analysisCancellation?.Cancel();
        _collectionCancellation?.Cancel();
        NotifyCommands();
    }

    public async Task<bool> WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task[] activeTasks;
        lock (_activeTaskSync)
        {
            activeTasks = new[] { _activeAnalysisTask, _activeCollectionTask }
                .Where(task => task is { IsCompleted: false })
                .Cast<Task>()
                .ToArray();
        }

        if (activeTasks.Length == 0)
        {
            return true;
        }

        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(activeTasks).WaitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
            when (timeoutCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private Task TrackAnalysisAsync(Func<Task> operation)
    {
        lock (_activeTaskSync)
        {
            if (_shutdownRequested)
            {
                return Task.CompletedTask;
            }

            if (_activeAnalysisTask is { IsCompleted: false })
            {
                return _activeAnalysisTask;
            }

            var generation = ++_analysisGeneration;
            _activeAnalysisTask = TrackTaskCoreAsync(
                operation,
                generation,
                isCollection: false);
            return _activeAnalysisTask;
        }
    }

    private Task TrackCollectionAsync(Func<Task> operation)
    {
        lock (_activeTaskSync)
        {
            if (_shutdownRequested)
            {
                return Task.CompletedTask;
            }

            if (_activeCollectionTask is { IsCompleted: false })
            {
                return _activeCollectionTask;
            }

            var generation = ++_collectionGeneration;
            _activeCollectionTask = TrackTaskCoreAsync(
                operation,
                generation,
                isCollection: true);
            return _activeCollectionTask;
        }
    }

    private async Task TrackTaskCoreAsync(
        Func<Task> operation,
        long generation,
        bool isCollection)
    {
        try
        {
            await operation();
        }
        finally
        {
            lock (_activeTaskSync)
            {
                if (isCollection && _collectionGeneration == generation)
                {
                    _activeCollectionTask = null;
                }
                else if (!isCollection && _analysisGeneration == generation)
                {
                    _activeAnalysisTask = null;
                }
            }
        }
    }

    public void Dispose()
    {
        _collector.Updated -= OnCollectorUpdated;
        _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        RequestShutdownStop();
        _analysisCancellation?.Dispose();
        _collectionCancellation?.Dispose();
    }

    private async Task ChooseAndAnalyzeAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 16:9 游戏截图",
            Filter = "游戏截图 (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectedFile = dialog.FileName;
        await AnalyzeSelectedFileAsync();
    }

    private async Task AnalyzeSelectedFileAsync()
    {
        if (!File.Exists(SelectedFile))
        {
            Status = "截图文件不存在。";
            return;
        }

        var file = new FileInfo(SelectedFile);
        if (file.Length is <= 0 or > 8L * 1024 * 1024)
        {
            Status = "截图必须大于 0 字节且不超过 8 MB。";
            return;
        }

        Publish(
            TaskEventLevel.Information,
            "Phase2ScreenshotLoadStarted",
            $"正在读取截图：{file.Name}；大小={file.Length} bytes。");
        try
        {
            var frame = CaptureFrameLoader.LoadFile(file.FullName);
            await AnalyzeAsync(frame, $"file:{file.Name}");
        }
        catch (Exception exception)
        {
            Status = $"截图读取失败：{exception.Message}";
            Publish(
                TaskEventLevel.Warning,
                "Phase2ScreenshotLoadFailed",
                $"阶段=decode；文件={file.Name}；异常={exception.GetType().Name}；" +
                $"消息={exception.Message}");
        }
    }

    private async Task AnalyzeAsync(CaptureFrame frame, string evidenceSource)
    {
        _analysisCancellation?.Dispose();
        var analysisCancellation = new CancellationTokenSource();
        _analysisCancellation = analysisCancellation;
        IsBusy = true;
        Status = "正在使用现有页面识别、角色模板和 OCR 分析……";
        Publish(
            TaskEventLevel.Information,
            "Phase2ScreenshotAnalysisStarted",
            $"来源={evidenceSource}；截图={frame.Width}×{frame.Height}；" +
            $"资源目录={AppContext.BaseDirectory}");
        try
        {
            var result = await _analyzer.AnalyzeAsync(
                frame,
                evidenceSource,
                new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
                analysisCancellation.Token);
            Apply(result);
            Status = "分析完成。低置信度和无法确定的字段已明确列出。";
            var presentation = SituationAnalysisPresentation.Build(result);
            Publish(
                TaskEventLevel.Information,
                "Phase2ScreenshotAnalyzed",
                $"页面={result.Snapshot.PageId.Value ?? "Unknown"}；" +
                $"页面类型={result.OperationalState?.PageFamily.ToString() ?? "Unknown"}；" +
                $"已确认字段={presentation.KnownFieldCount}；" +
                $"未知字段={presentation.UnknownFieldCount}；" +
                $"路线候选={result.RouteCandidates.Count}；" +
                $"建议={result.Recommendations.Count}。" );
        }
        catch (OperationCanceledException)
        {
            Status = "截图分析已停止。";
        }
        catch (Exception exception)
        {
            Status = $"分析失败：{exception.Message}";
            Publish(
                TaskEventLevel.Warning,
                "Phase2ScreenshotAnalysisFailed",
                $"阶段=recognition；来源={evidenceSource}；" +
                $"异常={exception.GetType().Name}；消息={exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, analysisCancellation))
            {
                _analysisCancellation = null;
            }
            analysisCancellation.Dispose();
            IsBusy = false;
        }
    }

    private async Task StartCollectionAsync()
    {
        Status = "已接收开始指令，正在连接游戏窗口……";
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Render);
        var window = ResolveGameWindow();
        if (window is null)
        {
            Status = "没有发现可用的《崩坏：星穹铁道》窗口；已自动重新查找。";
            Publish(
                TaskEventLevel.Warning,
                "Phase2LiveWindowUnavailable",
                "启动即时识别前自动重新发现窗口后仍未找到游戏窗口。");
            return;
        }

        _collectionCancellation?.Dispose();
        _collectionCancellation = new CancellationTokenSource();
        _mainViewModel.BeginPassiveCollection(_collectionCancellation);
        IsCollecting = true;
        Status = "即时识别已启动；正在后台截图和更新状态。";
        Publish(
            TaskEventLevel.Information,
            "Phase2LiveCollectionStarted",
            $"窗口={window.Title}；客户区=" +
            $"{window.ClientArea.Width}×{window.ClientArea.Height}。");
        try
        {
            var startOptions = await ResolveStartOptionsAsync(
                window,
                _collectionCancellation.Token);
            if (startOptions is null)
            {
                Status = "已取消续录；原断点记录保持不变。";
                return;
            }

            startOptions = startOptions with
            {
                DeleteScreenshotsOnCompletion =
                    _mainViewModel.DeleteScreenshotsAfterRunCompletion
            };

            await _collector.RunAsync(
                window.Handle,
                new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
                startOptions,
                _collectionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "即时识别已暂停。";
            Publish(
                TaskEventLevel.Information,
                "Phase2LiveCollectionStopped",
                "用户暂停了即时截图与分析。");
        }
        catch (Exception exception)
        {
            Status = $"即时识别已安全停止：{exception.Message}";
            Publish(
                TaskEventLevel.Warning,
                "Phase2LiveCollectionFailed",
                $"阶段=live-collection；异常={exception.GetType().Name}；" +
                $"消息={exception.Message}");
        }
        finally
        {
            if (_collectionCancellation is { } cancellation)
            {
                _mainViewModel.EndPassiveCollection(cancellation);
            }
            IsCollecting = false;
            await _mainViewModel.RefreshIncompleteRunsAsync();
        }
    }

    private async Task<LiveCollectionStartOptions?> ResolveStartOptionsAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        var selectedCheckpoint = _resumeCheckpoint;
        if (selectedCheckpoint is null)
        {
            return new LiveCollectionStartOptions(
                EntryMode: RunEntryMode.DirectRecording);
        }

        Status = "正在读取当前节点并核对断点……";
        var frame = await _capture.CaptureAsync(window, cancellationToken);
        var analysis = await _analyzer.AnalyzeAsync(
            frame,
            $"resume-preflight:{selectedCheckpoint.Checkpoint.RunId}",
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            cancellationToken,
            selectedCheckpoint.Checkpoint.RunId);
        Apply(analysis);

        var observedCheckpoint = RunCheckpointFactory.FromAnalysis(
            RunCheckpointFactory.CreateInitial(
                selectedCheckpoint.Checkpoint.RunId,
                RunEntryMode.Resumed,
                frame.CapturedAt),
            analysis,
            0,
            RunCheckpointLifecycleStatus.Active,
            frame.CapturedAt);
        var decision = RunResumePolicy.Decide(
            selectedCheckpoint.Checkpoint,
            new RunResumeObservation(
                observedCheckpoint.LastConfirmedNodeId,
                observedCheckpoint.IdentityEvidence,
                frame.CapturedAt));

        LiveCollectionStartOptions options;
        switch (decision.Kind)
        {
            case RunResumeDecisionKind.ContinueExisting:
                options = new LiveCollectionStartOptions(
                    selectedCheckpoint.Checkpoint.RunId,
                    RunEntryMode.Resumed,
                    decision.MissingNodeIds);
                Status = decision.MissingNodeIds.Count == 0
                    ? "断点核对通过，正在继续原对局。"
                    : $"断点核对通过；跳过的 {decision.MissingNodeIds.Count} 个节点将保持空白。";
                break;
            case RunResumeDecisionKind.CreateNewRun:
                options = new LiveCollectionStartOptions(
                    EntryMode: RunEntryMode.DirectRecording);
                Status = "当前节点早于断点，已保留旧记录并新建对局。";
                break;
            default:
                var reasons = string.Join(
                    Environment.NewLine,
                    decision.Reasons.Select(reason => $"• {reason}"));
                var answer = MessageBox.Show(
                    $"无法可靠确认当前画面是否属于所选对局。\n\n{reasons}\n\n" +
                    "选择“是”继续写入原记录；选择“否”保留原记录并新建对局；" +
                    "选择“取消”暂不记录。",
                    "确认续录方式",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.Cancel)
                {
                    return null;
                }

                options = answer == MessageBoxResult.Yes
                    ? new LiveCollectionStartOptions(
                        selectedCheckpoint.Checkpoint.RunId,
                        RunEntryMode.Resumed,
                        selectedCheckpoint.Checkpoint.MissingNodeIds)
                    : new LiveCollectionStartOptions(
                        EntryMode: RunEntryMode.DirectRecording);
                Status = answer == MessageBoxResult.Yes
                    ? "已按用户选择继续原对局。"
                    : "已按用户选择保留旧记录并新建对局。";
                break;
        }

        Publish(
            TaskEventLevel.Information,
            "RunResumeDecision",
            $"断点={selectedCheckpoint.Checkpoint.RunId}；" +
            $"结果={decision.Kind}；当前节点={observedCheckpoint.LastConfirmedNodeId ?? "Unknown"}；" +
            $"空白节点={string.Join(',', options.EffectiveMissingNodeIds)}");
        _resumeCheckpoint = null;
        CollectionStartLabel = "开始记录";
        ResumeSummary = "";
        return options;
    }

    private void StopCollection()
    {
        Status = "正在暂停即时识别……";
        _collectionCancellation?.Cancel();
    }

    private void OnCollectorUpdated(object? sender, LiveCollectionUpdate update) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            Status = update.Message;
            if (update.Analysis is not null)
            {
                Apply(update.Analysis);
            }
            Publish(
                update.IsError
                    ? TaskEventLevel.Warning
                    : TaskEventLevel.Information,
                update.IsError
                    ? "Phase2CollectionWarning"
                    : update.IsMilestone
                        ? "Phase2CollectionMilestone"
                        : "Phase2CollectionUpdated",
                update.Message);
        });

    private void Apply(ScreenshotAnalysisResult result)
    {
        var presentation = SituationAnalysisPresentation.Build(result);
        PageSummary = presentation.PageSummary;
        StateSummary = presentation.StateSummary;
        RouteSummary = result.RouteCandidates.Count == 0
            ? "没有可用的攻略候选。"
            : string.Join(
                Environment.NewLine,
                result.RouteCandidates.Take(3).Select(match =>
                    $"{match.ArchetypeName} · {match.Score:F1} 分 · " +
                    $"置信度 {match.Confidence:P0}"));
        RecommendationSummary = result.Recommendations.Count == 0
            ? "没有生成建议。"
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                result.Recommendations.Select(recommendation =>
                    $"{recommendation.Priority}. {recommendation.Action}\n" +
                    $"依据：{string.Join("；", recommendation.Reasons)}\n" +
                    $"风险：{string.Join("；", recommendation.Risks)}\n" +
                    $"置信度：{recommendation.Confidence:P0}"));
        EvidenceSummary = string.Join(
            Environment.NewLine,
            result.Recommendations
                .SelectMany(recommendation => recommendation.Sources)
                .Distinct()
                .Select(source =>
                    $"{source.SourceId} · {source.Locator}"));
        if (string.IsNullOrWhiteSpace(EvidenceSummary))
        {
            EvidenceSummary = "当前没有足以支持具体建议的攻略证据。";
        }
        WarningsSummary = presentation.WarningsSummary;
        RawJson = AdvisorJson.Serialize(result);
    }

    private bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    private bool CanAnalyze() =>
        !_shutdownRequested && !IsBusy && !IsCollecting;

    private bool CanAnalyzeFile() =>
        CanAnalyze() && !string.IsNullOrWhiteSpace(SelectedFile);

    private bool CanStartCollection() =>
        !_shutdownRequested &&
        !IsBusy &&
        !IsCollecting &&
        !_mainViewModel.IsRunning &&
        !_mainViewModel.IsPassiveCollectionRunning;

    private void OnMainViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedWindow) or
            nameof(MainViewModel.IsRunning) or
            nameof(MainViewModel.IsPassiveCollectionRunning))
        {
            NotifyCommands();
        }
    }

    private GameWindowInfo? ResolveGameWindow()
    {
        var selected = _mainViewModel.SelectedWindow;
        var refreshed = selected is null
            ? null
            : _windowService.Refresh(selected.Handle);
        if (refreshed is not null)
        {
            return refreshed;
        }

        _mainViewModel.RefreshWindows();
        selected = _mainViewModel.SelectedWindow;
        return selected is null
            ? null
            : _windowService.Refresh(selected.Handle);
    }

    private void NotifyCommands()
    {
        ChooseFileCommand.NotifyCanExecuteChanged();
        AnalyzeFileCommand.NotifyCanExecuteChanged();
        StartCollectionCommand.NotifyCanExecuteChanged();
        StopCollectionCommand.NotifyCanExecuteChanged();
    }

    private void Publish(TaskEventLevel level, string code, string message) =>
        _eventSink.Publish(new TaskEvent(DateTimeOffset.Now, level, code, message));
}
