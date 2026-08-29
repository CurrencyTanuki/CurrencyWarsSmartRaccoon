using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>整局循环配置。</summary>
public sealed record GrailLoopOptions
{
    /// <summary>整局安全阀：最多刷多少局。</summary>
    public int MaxRounds { get; init; } = 20;

    /// <summary>备战页 ID（1-1/1-2/1-3 布局全局一致；商店/上场动作的页门禁用）。</summary>
    public string PreparationPageId { get; init; } = "preparation_1_3";

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
    OpeningRerollLoopCoordinator openingCoordinator,
    GrailOperationExecutor executor,
    GrailRunStateHolder stateHolder,
    GrailRecognitionListener listener,
    GameDataCatalog gameData)
{
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
            for (var round = 1; round <= options.MaxRounds; round++)
            {
                // ① 重刷开局（环境过滤器只收 067/019；命中后进 1-1/1-2/1-3）
                var opening = await openingCoordinator.RunAsync(
                    windowHandle, environmentFilter, openingOptions, cancellationToken);
                if (!opening.Succeeded)
                {
                    continue; // 导航失败等：下一轮重试
                }

                // ② 1-3 运营循环直至判定通过或山穷水尽
                var outcome = await RunPreparationLoopAsync(
                    windowHandle, goal, options, round, cancellationToken);
                if (outcome.Succeeded)
                {
                    return outcome;
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

    private GrailRunSnapshot? AssembleLatest(GrailUserGoal goal)
    {
        if (listener.LatestAnalysis?.OperationalState is not { } state)
        {
            return null;
        }

        var analysis = listener.LatestAnalysis;
        return GrailSnapshotAssembler.Assemble(
            state,
            analysis.Snapshot,
            gameData,
            stateHolder,
            goal,
            DateTimeOffset.Now,
            staleAfter: TimeSpan.FromSeconds(15));
    }
}
