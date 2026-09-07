using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// I1 页面事实帧龄测试（2026-09-02 实测事故回归）：
/// 识别流冻结时 LatestAnalysis 停在最后一帧——I1 必须把帧龄/陈旧标记一并
/// 返回，绝不把几分钟前的旧帧伪装成现状（本次事故：冻结 6 分钟后 I1 仍报
/// challenge_failed，被误判成"游戏自动开打"）。
/// </summary>
public class GrailRecognitionPageFactTests
{
    private sealed class FakeCollectionService : IPhase2LiveCollectionService
    {
        public event EventHandler<LiveCollectionUpdate>? Updated;

        public void Raise(ScreenshotAnalysisResult analysis) =>
            Updated?.Invoke(this, new LiveCollectionUpdate("run-test", 1, analysis, string.Empty));

        /// <summary>模拟 null Analysis 的纯文本更新（服务层心跳外的错误/里程碑消息）。</summary>
        public void RaiseMessage(string message) =>
            Updated?.Invoke(this, new LiveCollectionUpdate("run-test", 1, null, message, IsError: true));

        public Task RunAsync(
            nint gameWindowHandle,
            AdvisorSelection selection,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ScreenshotAnalysisResult Analysis(DateTimeOffset asOf, string pageId) => new()
    {
        AnalysisId = $"analysis-{asOf:HHmmss}",
        Snapshot = new RunSnapshot
        {
            RunId = "run-test",
            AsOf = asOf,
            PageId = Observation<string>.Known(pageId, 0.9),
        },
    };

    private static async Task<GrailPageFact> ReadPageAsync(
        FakeCollectionService service,
        ScreenshotAnalysisResult analysis)
    {
        var listener = new GrailRecognitionListener(service);
        listener.Subscribe();
        service.Raise(analysis);
        var commands = new GrailRecognitionCommands(
            listener, new GrailRunStateHolder(), null!, null!, null!, null!);
        var result = await commands.HandleAsync(
            new GrailCommand(GrailCommandKind.I1),
            new GrailCommandContext(1, "preparation_generic", GrailUserGoal.Single),
            CancellationToken.None);
        return Assert.IsType<GrailPageFact>(result.Payload);
    }

    [Fact]
    public async Task I1_Marks_Frozen_Frame_Stale()
    {
        // 事故场景：帧停在 6 分钟前（识别流已冻结）。
        var fact = await ReadPageAsync(
            new FakeCollectionService(),
            Analysis(DateTimeOffset.Now.AddMinutes(-6), "challenge_failed"));

        Assert.Equal("challenge_failed", fact.PageId);
        Assert.NotNull(fact.CapturedAt);
        Assert.True(fact.IsStale);
    }

    [Fact]
    public async Task I1_Marks_Fresh_Frame_Current()
    {
        var fact = await ReadPageAsync(
            new FakeCollectionService(),
            Analysis(DateTimeOffset.Now.AddSeconds(-2), "preparation_generic"));

        Assert.Equal("preparation_generic", fact.PageId);
        Assert.NotNull(fact.CapturedAt);
        Assert.False(fact.IsStale);
    }

    [Fact]
    public async Task I1_Marks_Missing_Frame_Stale()
    {
        // 无任何帧（识别会话未启动/未产出首帧）同样视为陈旧。
        var commands = new GrailRecognitionCommands(
            new GrailRecognitionListener(new FakeCollectionService()),
            new GrailRunStateHolder(), null!, null!, null!, null!);

        var result = await commands.HandleAsync(
            new GrailCommand(GrailCommandKind.I1),
            new GrailCommandContext(1, "preparation_generic", GrailUserGoal.Single),
            CancellationToken.None);

        var fact = Assert.IsType<GrailPageFact>(result.Payload);
        Assert.Null(fact.PageId);
        Assert.Null(fact.CapturedAt);
        Assert.True(fact.IsStale);
    }

    [Fact]
    public void StreamPulse_Refreshes_On_Dialog_Frames_While_Suppressing_Analysis()
    {
        // 审计 #5（1.2.118）回归：盛会弹框在屏期 LatestAnalysis 抑制（坑50），但
        // LastUpdateAt 必须照常刷新——冻结判据看活动脉冲，弹框期不得误报冻结。
        var service = new FakeCollectionService();
        var listener = new GrailRecognitionListener(service);
        listener.Subscribe();

        var baseline = Analysis(DateTimeOffset.Now.AddMinutes(-2), "preparation_generic");
        service.Raise(baseline);
        Assert.Equal(baseline, listener.LatestAnalysis);

        var dialogFrame = Analysis(DateTimeOffset.Now, "gala_star_bond_selection");
        service.Raise(dialogFrame);

        // 抑制语义保持：弹框帧不得进入 LatestAnalysis（决策层仍持弹框前备战帧）。
        Assert.Equal(baseline, listener.LatestAnalysis);
        // 活动脉冲照常刷新：事件在流动=流活着。
        Assert.NotNull(listener.LastUpdateAt);
        Assert.True(
            DateTimeOffset.Now - listener.LastUpdateAt!.Value < TimeSpan.FromSeconds(5),
            "弹框帧到达后 LastUpdateAt 应为刚刷新的活动脉冲");
    }

    [Fact]
    public void StreamPulse_Refreshes_On_Null_Analysis_Message()
    {
        // 服务层纯文本消息（错误/里程碑，Analysis=null）同样算活动脉冲——失败帧
        // 即时产出场景（失焦抓屏失败等）不构成冻结。
        var service = new FakeCollectionService();
        var listener = new GrailRecognitionListener(service);
        listener.Subscribe();

        service.RaiseMessage("识别流看门狗：失败帧即时上报");

        Assert.Null(listener.LatestAnalysis);
        Assert.NotNull(listener.LastUpdateAt);
    }

    [Fact]
    public void StreamPulse_Null_Before_Any_Event()
    {
        // 从未收到任何事件（会话未启动/管线挂死且零消息）=无活动脉冲，冻结判据成立。
        var listener = new GrailRecognitionListener(new FakeCollectionService());
        listener.Subscribe();
        Assert.Null(listener.LastUpdateAt);
    }
}
