using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>监听器事件：弹框上沿（含当帧识别结果）。</summary>
public sealed record GrailRecognitionEvent(
    bool IsWishDialogEdge,
    ScreenshotAnalysisResult Analysis);

/// <summary>
/// 「1-3 三星五费」外挂监听器（W1，定稿决策树）：识别流的只读观察者，
/// 弹框边沿检测（前帧非祈愿弹框→本帧是）+ 非弹框帧的识别转发。仿 FateGrailLiveSnapshotSource
/// 的锁+幂等订阅模式；不触碰识别管线本身。
/// </summary>
public sealed class GrailRecognitionListener
{
    private const string WishDialogPageId = "wish_trial_selection";
    // 坑50（1.2.114）：盛会之星羁绊升档选择框页 ID 与识别表/恢复类同源。
    private const string GalaBondPopupPageId =
        CurrencyWarsRejectedOpeningRecovery.GalaBondPopupPageId;
    private readonly IPhase2LiveCollectionService _collectionService;
    private readonly object _gate = new();
    private bool _subscribed;
    private bool _dialogWasOpen;
    private bool _galaPopupOpen;
    private TaskCompletionSource? _firstAnalysisSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GrailRecognitionListener(IPhase2LiveCollectionService collectionService)
    {
        _collectionService = collectionService;
    }

    /// <summary>弹框上沿（每局 4 次触发点）。弹框期间不重复触发（边沿检测去重）。</summary>
    public event EventHandler<GrailRecognitionEvent>? WishDialogOpened;

    /// <summary>每条非弹框帧识别（编排层用于刷新 LatestSnapshot 并跑运营循环 tick）。</summary>
    public event EventHandler<ScreenshotAnalysisResult>? AnalysisUpdated;

    /// <summary>最近一条非弹框帧识别结果（弹框期间保持上一备战帧，供组装）。</summary>
    public ScreenshotAnalysisResult? LatestAnalysis { get; private set; }

    /// <summary>祈愿弹框当前是否在屏上（边沿检测的对外只读视图）。</summary>
    public bool IsWishDialogOpen
    {
        get { lock (_gate) { return _dialogWasOpen; } }
    }

    /// <summary>盛会之星升档选择框当前是否在屏上（坑50，1.2.114；供循环泵应答）。</summary>
    public bool IsGalaBondPopupOpen
    {
        get { lock (_gate) { return _galaPopupOpen; } }
    }

    /// <summary>等待识别流产出首帧（识别预热可能远超固定延迟，事件驱动替代盲等；超时返回 false）。</summary>
    public async Task<bool> WaitForFirstAnalysisAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = _firstAnalysisSignal;
        if (signal is null || signal.Task.IsCompleted)
        {
            return true;
        }

        var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout, cancellationToken));
        _firstAnalysisSignal = null;
        return completed == signal.Task;
    }

    public void Subscribe()
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _dialogWasOpen = false;
            _galaPopupOpen = false;
            _collectionService.Updated += OnUpdated;
            _subscribed = true;
        }
    }

    public void Unsubscribe()
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                return;
            }

            _collectionService.Updated -= OnUpdated;
            _subscribed = false;
        }
    }

    private void OnUpdated(object? sender, LiveCollectionUpdate update)
    {
        var analysis = update.Analysis;
        if (analysis is null)
        {
            return;
        }

        var pageId = analysis.Snapshot.PageId.Value;
        var isDialogNow = string.Equals(pageId, WishDialogPageId, StringComparison.OrdinalIgnoreCase);

        // 坑50（1.2.114）：盛会之星升档选择框与祈愿弹框同性质（模态吞输入）——
        // 在屏期间保持上一备战快照、不转发决策 tick（弹框帧不是备战页事实）。
        var isGalaPopupNow = string.Equals(
            pageId,
            GalaBondPopupPageId,
            StringComparison.OrdinalIgnoreCase);

        List<EventHandler<GrailRecognitionEvent>>? dialogHandlers;
        List<EventHandler<ScreenshotAnalysisResult>>? analysisHandlers;
        lock (_gate)
        {
            dialogHandlers = isDialogNow && !_dialogWasOpen
                ? WishDialogOpened?.GetInvocationList().Cast<EventHandler<GrailRecognitionEvent>>().ToList()
                : null;
            _dialogWasOpen = isDialogNow;
            _galaPopupOpen = isGalaPopupNow;
            if (!isDialogNow && !isGalaPopupNow)
            {
                LatestAnalysis = analysis;
                _firstAnalysisSignal?.TrySetResult();
            }

            analysisHandlers = isDialogNow || isGalaPopupNow
                ? null
                : AnalysisUpdated?.GetInvocationList().Cast<EventHandler<ScreenshotAnalysisResult>>().ToList();
        }

        // 事件在锁外派发（处理器耗时不得放大进采集循环）
        if (dialogHandlers is not null)
        {
            var edge = new GrailRecognitionEvent(IsWishDialogEdge: true, analysis);
            foreach (var handler in dialogHandlers)
            {
                handler(this, edge);
            }
        }

        if (analysisHandlers is not null)
        {
            foreach (var handler in analysisHandlers)
            {
                handler(this, analysis);
            }
        }
    }
}
