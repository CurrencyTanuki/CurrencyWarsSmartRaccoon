using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 2c 采集会话生命周期测试：GrailRunLoop 每轮自起独立采集（独立 runId、AutomaticReroll 入口、
/// 放弃局截图清理），局末取消传播到采集会话（审查阻断项 #2 的修复验证）。
/// </summary>
public sealed class GrailCollectionSessionTests
{
    /// <summary>假采集器：记录 runId 与取消令牌，挂起直到取消。</summary>
    private sealed class FakeCollectionService : IPhase2LiveCollectionService
    {
        public event EventHandler<LiveCollectionUpdate>? Updated { add { } remove { } }

        public List<string> StartedRunIds { get; } = [];

        public List<LiveCollectionStartOptions> StartedOptions { get; } = [];

        public List<CancellationToken> CapturedTokens { get; } = [];

        public Task RunAsync(nint gameWindowHandle, AdvisorSelection selection, CancellationToken cancellationToken)
            => RunAsync(gameWindowHandle, selection, new LiveCollectionStartOptions(), cancellationToken);

        public async Task RunAsync(
            nint gameWindowHandle,
            AdvisorSelection selection,
            LiveCollectionStartOptions options,
            CancellationToken cancellationToken)
        {
            StartedRunIds.Add(options.RunId ?? string.Empty);
            StartedOptions.Add(options);
            CapturedTokens.Add(cancellationToken);
            var tcs = new TaskCompletionSource();
            await using var registration = cancellationToken.Register(() => tcs.TrySetResult());
            await tcs.Task;
        }
    }

    private static Task<OpeningRerollLoopResult> FailingOpeningLoop(
        nint handle, OpeningFilterSet filters, OpeningRerollLoopOptions options, CancellationToken ct) =>
        Task.FromResult(new OpeningRerollLoopResult(
            OpeningRerollLoopState.NavigationFailed, 1, null, null, null, null,
            "测试：导航失败"));

    [Fact]
    public async Task Session_UsesGrailRunIdPrefix_AutoRerollEntry_AndCancelPropagates()
    {
        // 识别流无帧（假采集器不发事件）→ 1-3 组装恒 null → opening 失败走到局末 → 取消采集
        var fake = new FakeCollectionService();
        var loop = new GrailRunLoop(
            FailingOpeningLoop,
            new GrailOperationExecutor(null!, null!, null!, null!, new GrailRunStateHolder()),
            new GrailRunStateHolder(),
            new GrailRecognitionListener(fake),
            EmptyGameData(),
            fake);

        var outcome = await loop.RunAsync(
            1234,
            GrailUserGoal.Single,
            GrailRunLoop.BuildViableEnvironmentFilter(),
            new OpeningRerollLoopOptions { MaximumRounds = 1 },
            new GrailLoopOptions { MaxRounds = 1, TickDelayMs = 1 },
            CancellationToken.None);

        var started = Assert.Single(fake.StartedRunIds);
        Assert.StartsWith("run-", started);
        Assert.Contains("grail-r1", started);
        Assert.Equal(RunEntryMode.AutomaticReroll, fake.StartedOptions[0].EntryMode);
        Assert.True(fake.StartedOptions[0].DeleteScreenshotsOnCompletion);
        Assert.True(fake.CapturedTokens[0].IsCancellationRequested);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void Filter_RequiresOnly067And019()
    {
        var filter = GrailRunLoop.BuildViableEnvironmentFilter();
        Assert.Equal(2, filter.InvestmentEnvironments.Count);
        Assert.All(filter.InvestmentEnvironments, item => Assert.Equal(OpeningFilterState.Require, item.State));
    }

    private static GameDataCatalog EmptyGameData() => new([], [], [], [], []);
}
