using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>整局循环配置。</summary>
public sealed record GrailLoopOptions
{
    /// <summary>整局安全阀：最多刷多少局（0=不限——三星五费条件苛刻，可能上百局；仅用户停止终止）。</summary>
    public int MaxRounds { get; init; } = 0;

    /// <summary>备战页 ID（1-1/1-2/1-3 布局全局一致；商店/上场动作的页门禁用）。</summary>
    public string PreparationPageId { get; init; } = "preparation_generic";

    /// <summary>循环 tick 间隔（毫秒）。</summary>
    public int TickDelayMs { get; init; } = 600;
}

/// <summary>整局循环结局。</summary>
public sealed record GrailLoopOutcome(bool Succeeded, int RoundsPlayed, string Message);

/// <summary>
/// 「1-3 三星五费」整局刷取循环（定稿决策树的编排壳）：
/// 每轮 = 重刷开局（复用 OpeningRerollLoopCoordinator，067/019 过滤+命杯配置）→
/// 监听器驱动的 1-3 运营循环（弹框响应 + 决策 tick + 执行）→ 达成收工 / 山穷水尽重开。
/// </summary>
public sealed class GrailRunLoop(
    Func<nint, OpeningFilterSet, OpeningRerollLoopOptions, CancellationToken, Task<OpeningRerollLoopResult>> openingLoop,
    GrailOperationExecutor executor,
    GrailRunStateHolder stateHolder,
    GrailRecognitionListener listener,
    GameDataCatalog gameData,
    IPhase2LiveCollectionService collectionService)
{
    /// <summary>滚动录屏（可选；成功局保留、失败局删除）。语义与旧 IRoundRecorder 一致。</summary>
    public interface IRoundRecorder
    {
        Task StartAsync(string roundId, CancellationToken cancellationToken = default);

        Task FinishAsync(bool success, string? outputDirectory, CancellationToken cancellationToken = default);
    }

    /// <summary>录像输出目录（成功局 MP4 保留位置）。</summary>
    public string RecordingOutputDirectory { get; set; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Recordings");

    /// <summary>可选录屏器（编排层组装时注入）。</summary>
    public IRoundRecorder? RoundRecorder { get; set; }
    public async Task<GrailLoopOutcome> RunAsync(
        nint windowHandle,
        GrailUserGoal goal,
        OpeningFilterSet environmentFilter,
        OpeningRerollLoopOptions openingOptions,
        GrailLoopOptions options,
        CancellationToken cancellationToken)
    {
        listener.Subscribe();
        try
        {
            for (var round = 1; options.MaxRounds <= 0 || round <= options.MaxRounds; round++)
            {
                // ① 重刷开局（环境过滤器只收 067/019；命中后进 1-1/1-2/1-3）
                if (RoundRecorder is not null)
                {
                    await RoundRecorder.StartAsync($"grail-round-{round}", cancellationToken);
                }

                // W1：1-1/1-2 也会强制弹祈愿——opening 期间挂"轻量"弹框泵
                //（直接探测+应答，不依赖重型识别管线：重刷导航期没有命杯成员，管线纯烧 CPU 拖慢导航）
                using var openingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var dialogPump = PumpDialogsAsync(windowHandle, openingCts.Token);

                GrailLoopOutcome? outcome = null;
                var opening = await openingLoop(
                    windowHandle, environmentFilter, openingOptions, cancellationToken);
                // 弹框泵只覆盖 opening 阶段（1-1/1-2 的档位弹框）；返回后必须取消，
                // 否则与 1-3 循环的弹框轮询并发应答同一弹框 → WishesResponded 双计数
                openingCts.Cancel();
                try { await dialogPump; } catch (OperationCanceledException) { }
                if (!opening.Succeeded)
                {
                    // opening 内部已负责"未命中→重开"的重刷循环；返回失败即硬失败
                    //（用户停止/导航失败/被动监测放弃）——如实终止整个流程，由用户决定是否重试
                    return new GrailLoopOutcome(false, round, $"开局阶段终止：{opening.Message}");
                }

                // 命中后自起独立采集会话（1-3 快照与弹框依赖它；重刷导航期不跑，省 CPU）
                var runId = $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-grail-r{round}-{Guid.NewGuid():N}";
                var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var collectionTask = collectionService.RunAsync(
                    windowHandle,
                    new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
                    new LiveCollectionStartOptions(
                        runId,
                        RunEntryMode.AutomaticReroll,
                        DeleteScreenshotsOnCompletion: true),
                    sessionCts.Token);
                try
                {
                    // 等识别管线起帧（事件驱动；预热超 15s 兜底）
                    await listener.WaitForFirstAnalysisAsync(TimeSpan.FromSeconds(15), cancellationToken);

                    // ② 1-3 运营循环直至判定通过或山穷水尽
                    outcome = await RunPreparationLoopAsync(
                        windowHandle, goal, options, round, cancellationToken);
                    if (outcome.Succeeded)
                    {
                        return outcome;
                    }
                }
                finally
                {
                    // 会话取消兜底：任何路径（异常/取消/失败/成功）都不泄漏
                    sessionCts.Cancel();
                    try { await collectionTask; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 采集器收尾错误不击穿主循环（记录性吞掉）
                    }
                    sessionCts.Dispose();
                }
            }

            return new GrailLoopOutcome(false, options.MaxRounds, $"已刷 {options.MaxRounds} 局仍未达成。");
        }
        finally
        {
            listener.Unsubscribe();
        }
    }

    private async Task<GrailLoopOutcome> RunPreparationLoopAsync(
        nint windowHandle,
        GrailUserGoal goal,
        GrailLoopOptions options,
        int round,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // 弹框优先：祈愿响应是唯一能翻转判定的事件
            if (listener.IsWishDialogOpen)
            {
                await executor.ExecuteWishDialogAsync(windowHandle, cancellationToken);
                await Task.Delay(900, cancellationToken); // 确认后等识别跟上
            }

            var snapshot = AssembleLatest(goal);
            if (snapshot is null)
            {
                await Task.Delay(options.TickDelayMs, cancellationToken);
                continue;
            }

            executor.LatestSnapshot = snapshot;

            var verdict = GrailFinalJudge.Judge(snapshot);
            if (verdict.Kind == GrailVerdictKind.Success)
            {
                return new GrailLoopOutcome(true, round, verdict.Reason);
            }

            var operation = GrailLoopOperationDecider.DecideNext(snapshot);
            switch (operation.Kind)
            {
                case GrailLoopOperationKind.FinishSuccess:
                    return new GrailLoopOutcome(true, round, operation.Reason);

                case GrailLoopOperationKind.ExhaustedReroll:
                    return new GrailLoopOutcome(false, round, $"第 {round} 局山穷水尽：{operation.Reason}");

                case GrailLoopOperationKind.GiveUpFiveBond:
                    stateHolder.GiveUpFiveBond();
                    break;

                case GrailLoopOperationKind.BuyXpThenDeployMember:
                    await executor.ExecuteBuyXpAsync(windowHandle, cancellationToken);
                    await executor.ExecuteShopPassAsync(
                        windowHandle, snapshot, options.PreparationPageId, cancellationToken);
                    break;

                case GrailLoopOperationKind.SellForXpGold:
                case GrailLoopOperationKind.SellForGold:
                    await executor.ExecuteSellForGoldAsync(
                        windowHandle, snapshot, operation.TargetGold,
                        options.PreparationPageId, cancellationToken);
                    break;

                case GrailLoopOperationKind.ShopPass:
                default:
                    await executor.ExecuteShopPassAsync(
                        windowHandle, snapshot, options.PreparationPageId, cancellationToken);
                    break;
            }

            await Task.Delay(options.TickDelayMs, cancellationToken);
        }

        return new GrailLoopOutcome(false, round, "已取消。");
    }

    /// <summary>opening 阶段的弹框泵：轮询边沿标志并响应祈愿（弹框阻塞游戏输入，必须有人应答）。</summary>
    private async Task PumpDialogsAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (listener.IsWishDialogOpen)
            {
                await executor.ExecuteWishDialogAsync(windowHandle, cancellationToken);
            }

            await Task.Delay(800, cancellationToken);
        }
    }

    private GrailRunSnapshot? AssembleLatest(GrailUserGoal goal)
    {
        var analysis = listener.LatestAnalysis;
        if (analysis?.OperationalState is not { } state)
        {
            return null;
        }

        // 页面门禁（审计#8）：商店/战斗/结算帧的阵容被强制 Unknown，
        // 用它们组装会产出"空阵容快照"驱动错误决策——只有备战页帧才参与
        var pageId = analysis.Snapshot.PageId.Value;
        if (string.IsNullOrEmpty(pageId) || !pageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return GrailSnapshotAssembler.Assemble(
            state,
            analysis.Snapshot,
            gameData,
            stateHolder,
            goal,
            DateTimeOffset.Now,
            staleAfter: TimeSpan.FromSeconds(15));
    }

    /// <summary>构造可接受的开局过滤器（只收 067 英雄登场 / 019 命运圣杯邀请；018 已剔除）。</summary>
    public static OpeningFilterSet BuildViableEnvironmentFilter()
    {
        return new OpeningFilterSet
        {
            InvestmentEnvironments = new OpeningItemFilter[]
            {
                new("investment_environment_067", "英雄登场", OpeningFilterState.Require),
                new("investment_environment_019", "命运圣杯邀请", OpeningFilterState.Require),
            },
        };
    }
}
