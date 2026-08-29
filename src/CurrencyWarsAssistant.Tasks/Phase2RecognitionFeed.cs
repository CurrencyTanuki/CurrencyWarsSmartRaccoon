using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 统一识别流推送单元：一次新识别（非 heartbeat 重发）的完整结果。
/// </summary>
public sealed record Phase2FeedUpdate(
    ScreenshotAnalysisResult Analysis,
    long Version,
    DateTimeOffset ObservedAt,
    bool IsCritical);

/// <summary>
/// 统一识别流（用户架构：一个摄像机 + 一个识别器，识别结果分发给下游）。
/// pipeline（Phase2RealtimeRecognitionPipeline）是唯一的"摄像机+识别器"循环，
/// 本 feed 是其对外门面：订阅者（记录员/刷开局）通过它共享同一份识别结果，
/// 并可等待"下一帧新识别"（刷开局操作后验证用）。
/// 记录员启动对局时 Attach(pipeline)，对局结束 Detach。
/// </summary>
public interface IPhase2RecognitionFeed
{
    /// <summary>新识别结果（非 heartbeat 重发）。</summary>
    event EventHandler<Phase2FeedUpdate>? Updated;

    ScreenshotAnalysisResult? LatestAnalysis { get; }

    long LatestVersion { get; }

    /// <summary>记录员当前对局是否已挂载识别流（刷开局据此决定取 feed 帧还是回退）。</summary>
    bool IsAttached { get; }

    /// <summary>最新原始帧（pipeline 每次截图更新，供刷开局对帧做专用识别）。</summary>
    CaptureFrame? LatestFrame { get; }

    long LatestFrameVersion { get; }

    /// <summary>
    /// 等待版本号大于 afterVersion 的新识别；超时返回 null（供刷开局
    /// "操作后等待下一帧识别结果"使用）。
    /// </summary>
    Task<ScreenshotAnalysisResult?> WaitForNextAnalysisAsync(
        long afterVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// 等待版本号大于 afterVersion 的新原始帧；超时返回 null
    /// （供刷开局"操作后等待下一帧画面"使用，约 100ms 一帧）。
    /// </summary>
    Task<CaptureFrame?> WaitForNextFrameAsync(
        long afterVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class Phase2RecognitionFeed : IPhase2RecognitionFeed
{
    private readonly object gate = new();
    private readonly List<TaskCompletionSource<ScreenshotAnalysisResult?>> waiters = [];
    private readonly List<TaskCompletionSource<CaptureFrame?>> frameWaiters = [];
    private ScreenshotAnalysisResult? latest;
    private long version;
    private CaptureFrame? latestFrame;
    private long frameVersion;
    private IDisposable? subscription;
    private IDisposable? frameSubscription;

    public event EventHandler<Phase2FeedUpdate>? Updated;

    public ScreenshotAnalysisResult? LatestAnalysis
    {
        get
        {
            lock (gate)
            {
                return latest;
            }
        }
    }

    public long LatestVersion
    {
        get
        {
            lock (gate)
            {
                return version;
            }
        }
    }

    public CaptureFrame? LatestFrame
    {
        get
        {
            lock (gate)
            {
                return latestFrame;
            }
        }
    }

    public long LatestFrameVersion
    {
        get
        {
            lock (gate)
            {
                return frameVersion;
            }
        }
    }

    public bool IsAttached
    {
        get
        {
            lock (gate)
            {
                return subscription is not null;
            }
        }
    }

    /// <summary>
    /// 把当前对局的识别流挂到本 feed（记录员启动 pipeline 时调用）。
    /// 注意：Attach 不重置 latest/version（feed 是单例，跨对局保留）；
    /// 调用方应先用 LatestVersion 作基线，再 WaitForNextAnalysisAsync。
    /// </summary>
    internal void Attach(Phase2RealtimeRecognitionPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        lock (gate)
        {
            DetachLocked();
            subscription = pipeline.Subscribe(Ingest);
            frameSubscription = pipeline.SubscribeFrames(IngestFrame);
        }
    }

    internal void Detach()
    {
        lock (gate)
        {
            DetachLocked();
        }
    }

    public Task<ScreenshotAnalysisResult?> WaitForNextAnalysisAsync(
        long afterVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<ScreenshotAnalysisResult?> waiter;
        lock (gate)
        {
            if (version > afterVersion)
            {
                return Task.FromResult(latest);
            }

            waiter = new TaskCompletionSource<ScreenshotAnalysisResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add(waiter);
        }

        // 超时（或取消令牌触发 Task.Delay 取消）后返回 null；
        // 调用方如需区分"取消"与"超时"，检查传入的 cancellationToken。
        _ = Task.Delay(timeout, cancellationToken)
            .ContinueWith(
                task => waiter.TrySetResult(null),
                TaskScheduler.Default);
        return waiter.Task;
    }

    public Task<CaptureFrame?> WaitForNextFrameAsync(
        long afterVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<CaptureFrame?> waiter;
        lock (gate)
        {
            if (frameVersion > afterVersion)
            {
                return Task.FromResult(latestFrame);
            }

            waiter = new TaskCompletionSource<CaptureFrame?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            frameWaiters.Add(waiter);
        }

        _ = Task.Delay(timeout, cancellationToken)
            .ContinueWith(
                task => waiter.TrySetResult(null),
                TaskScheduler.Default);
        return waiter.Task;
    }

    internal void IngestFrame(CaptureFrame frame)
    {
        TaskCompletionSource<CaptureFrame?>[] pending;
        lock (gate)
        {
            latestFrame = frame;
            frameVersion++;
            pending = frameWaiters.ToArray();
            frameWaiters.Clear();
        }

        foreach (var waiter in pending)
        {
            waiter.TrySetResult(frame);
        }
    }

    internal void Ingest(Phase2RealtimePipelineUpdate update)
    {
        // 只收新识别：heartbeat 是最近一次识别的重发（内容相同），
        // 不应推进版本号（否则刷开局会把旧内容当"新结果"消费）。
        if (update.IsHeartbeat || update.Analysis is null)
        {
            return;
        }

        TaskCompletionSource<ScreenshotAnalysisResult?>[] pending;
        Phase2FeedUpdate feedUpdate;
        lock (gate)
        {
            latest = update.Analysis;
            version++;
            feedUpdate = new Phase2FeedUpdate(
                update.Analysis,
                version,
                update.Analysis.Snapshot.AsOf,
                update.IsCritical);
            pending = waiters.ToArray();
            waiters.Clear();
        }

        foreach (var waiter in pending)
        {
            waiter.TrySetResult(update.Analysis);
        }

        Updated?.Invoke(this, feedUpdate);
    }

    private void DetachLocked()
    {
        subscription?.Dispose();
        subscription = null;
        frameSubscription?.Dispose();
        frameSubscription = null;
    }
}
