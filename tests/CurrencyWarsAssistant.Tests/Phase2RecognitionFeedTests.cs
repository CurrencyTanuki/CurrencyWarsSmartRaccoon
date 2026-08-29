using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 统一识别流（步骤 3）契约测试：
/// pipeline 多播 + feed 服务的核心行为——新识别推进版本、heartbeat 不推进、
/// 等待下一帧识别（刷开局操作后验证用）、超时返回 null、Attach/Detach。
/// </summary>
public sealed class Phase2RecognitionFeedTests
{
    [Fact]
    public void NewAnalysisAdvancesVersionAndNotifiesSubscribers()
    {
        var feed = new Phase2RecognitionFeed();
        ScreenshotAnalysisResult? received = null;
        long receivedVersion = 0;
        feed.Updated += (_, e) =>
        {
            received = e.Analysis;
            receivedVersion = e.Version;
        };

        var analysis = Analysis("a1");
        feed.Ingest(Update(analysis, isHeartbeat: false));

        Assert.Equal(1, feed.LatestVersion);
        Assert.Same(analysis, feed.LatestAnalysis);
        Assert.Same(analysis, received);
        Assert.Equal(1, receivedVersion);
    }

    [Fact]
    public void HeartbeatDoesNotAdvanceVersionOrReplaceLatest()
    {
        var feed = new Phase2RecognitionFeed();
        var first = Analysis("a1");
        feed.Ingest(Update(first, isHeartbeat: false));
        Assert.Equal(1, feed.LatestVersion);

        // heartbeat 是最近识别的重发：不得推进版本（刷开局不会把旧内容当新结果）
        feed.Ingest(Update(first, isHeartbeat: true));

        Assert.Equal(1, feed.LatestVersion);
        Assert.Same(first, feed.LatestAnalysis);
    }

    [Fact]
    public async Task WaitForNextReturnsWhenNewAnalysisArrives()
    {
        var feed = new Phase2RecognitionFeed();
        var waitTask = feed.WaitForNextAnalysisAsync(
            afterVersion: 0,
            timeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var analysis = Analysis("a2");
        feed.Ingest(Update(analysis, isHeartbeat: false));

        var result = await waitTask;
        Assert.Same(analysis, result);
    }

    [Fact]
    public async Task WaitForNextTimesOutAndReturnsNull()
    {
        var feed = new Phase2RecognitionFeed();
        var result = await feed.WaitForNextAnalysisAsync(
            afterVersion: 0,
            timeout: TimeSpan.FromMilliseconds(80),
            CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FrameStreamAdvancesVersionAndNotifies()
    {
        var feed = new Phase2RecognitionFeed();
        var frame = Frame();
        var waitTask = feed.WaitForNextFrameAsync(
            afterVersion: 0,
            timeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        feed.IngestFrame(frame);

        Assert.Equal(1, feed.LatestFrameVersion);
        Assert.Same(frame, feed.LatestFrame);
        var result = await waitTask;
        Assert.Same(frame, result);
    }

    [Fact]
    public async Task WaitForNextFrameTimesOutAndReturnsNull()
    {
        var feed = new Phase2RecognitionFeed();
        var result = await feed.WaitForNextFrameAsync(
            afterVersion: 0,
            timeout: TimeSpan.FromMilliseconds(60),
            CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task FrameVersionSurvivesDetachAndReattach()
    {
        // 单例 feed 跨对局：版本单调递增、Attach 不重置；
        // 调用方（RewardStageAutomation）必须以 LatestFrameVersion 作基线，
        // 否则 WaitForNextFrameAsync(0) 会立即拿到上一局残留帧。
        var feed = new Phase2RecognitionFeed();
        var pipeline = new Phase2RealtimeRecognitionPipeline(
            new FakeWindowService(),
            new FakeCapture(),
            new FakeAnalyzer(),
            fastPageClassifier: null);

        feed.Attach(pipeline);
        feed.IngestFrame(Frame());
        Assert.Equal(1, feed.LatestFrameVersion);

        feed.Detach();
        feed.Attach(pipeline);
        Assert.Equal(1, feed.LatestFrameVersion);
        Assert.NotNull(feed.LatestFrame);

        // 调用方基线语义：以 LatestFrameVersion 为起点等待新帧
        var waitTask = feed.WaitForNextFrameAsync(
            afterVersion: feed.LatestFrameVersion,
            timeout: TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var next = Frame();
        feed.IngestFrame(next);
        Assert.Same(next, await waitTask);
    }

    [Fact]
    public void AttachAndDetachDoNotThrow()
    {
        var feed = new Phase2RecognitionFeed();
        var pipeline = new Phase2RealtimeRecognitionPipeline(
            new FakeWindowService(),
            new FakeCapture(),
            new FakeAnalyzer(),
            fastPageClassifier: null);

        feed.Attach(pipeline);
        feed.Attach(pipeline); // 重复 Attach：应先 Detach 旧订阅
        feed.Detach();
        feed.Detach(); // 重复 Detach 安全
    }

    private static Phase2RealtimePipelineUpdate Update(
        ScreenshotAnalysisResult analysis,
        bool isHeartbeat) => new(
        Frame: null,
        ScreenshotName: isHeartbeat ? null : "test.png",
        Analysis: analysis,
        IsHeartbeat: isHeartbeat,
        IsRevalidated: false,
        IsCritical: false,
        AnalysisAge: TimeSpan.Zero,
        Error: null);

    private static ScreenshotAnalysisResult Analysis(string id) => new()
    {
        AnalysisId = id,
        Snapshot = new RunSnapshot
        {
            RunId = "feed-test",
            AsOf = DateTimeOffset.UtcNow
        }
    };

    private static CaptureFrame Frame() => new(
        16,
        16,
        64,
        new byte[16 * 16 * 4],
        new PixelRect(0, 0, 16, 16),
        DateTimeOffset.UtcNow);

    private sealed class FakeCapture : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWindowService : IGameWindowService
    {
        public IReadOnlyList<GameWindowInfo> FindCandidates() => [];
        public GameWindowInfo? Refresh(nint handle) => null;
        public bool IsForeground(GameWindowInfo window) => true;
        public bool BringToForeground(GameWindowInfo window) => true;
    }

    private sealed class FakeAnalyzer : ISituationScreenshotAnalyzer
    {
        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId,
            Phase2IncrementalSelection? incrementalSelection,
            int? recognitionGeneration) =>
            AnalyzeAsync(
                frame,
                evidenceSourceId,
                selection,
                cancellationToken,
                runId);

        public Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId = null) =>
            Task.FromResult<ScreenshotAnalysisResult?>(null)!;
    }
}
