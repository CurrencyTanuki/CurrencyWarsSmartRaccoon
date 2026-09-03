using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 宏层适配器（M 类）：决策层下发一条宏、收一个事实结果；识别层与操作层在宏内部配合
/// （经 GrailOperationExecutor 与既有组件编排，组件内部原有的识别重试保持不变）。
/// 宏只回事实——收工停机/弃局重刷判定统一归决策层 GrailFinalJudge（v4.3.1「宏只回事实」）。
/// 尚无独立底层入口的宏（M2 晶矿、M6 环境选择）显式 Fail 并注明承载位置，绝不凭空编造。
/// </summary>
public sealed class GrailMacroCommands(
    GrailOperationExecutor executor,
    RewardStageAutomationController rewardStage,
    GrailRunStateHolder stateHolder,
    GrailRecognitionListener listener,
    Func<nint, OpeningFilterSet, OpeningRerollLoopOptions, CancellationToken, Task<OpeningRerollLoopResult>> openingLoop)
    : IGrailCommandHandler
{
    public async Task<GrailCommandResult> HandleAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        return command.Kind switch
        {
            GrailCommandKind.M1 => await RunBattleAsync(command, context, cancellationToken),
            GrailCommandKind.M3 => await RespondWishAsync(context, cancellationToken),
            GrailCommandKind.M4 => await OpenLettersAsync(context, cancellationToken),
            GrailCommandKind.M5 => await ShopPassAsync(command, context, cancellationToken),
            GrailCommandKind.M7 => await SelectStrategyAsync(command, context, cancellationToken),
            GrailCommandKind.M8 => await RunOpeningAsync(command, context, cancellationToken),
            GrailCommandKind.M2 => await OpenMineBallsAsync(context, cancellationToken),
            GrailCommandKind.M6 => GrailCommandResult.Fail(command.Kind,
                "投资环境选择由 M8 的 opening 过滤重刷承载（067/019 过滤写死在过滤器），无独立环境页动作。"),
            _ => GrailCommandResult.Fail(command.Kind, $"宏层不受理指令 {command.Kind}。"),
        };
    }

    private async Task<GrailCommandResult> RunBattleAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        if (command.Payload is not GrailBattleArgs args
            || string.IsNullOrWhiteSpace(args.PreparationPageId)
            || string.IsNullOrWhiteSpace(args.ExpectedPostBattlePageId))
        {
            return GrailCommandResult.Fail(command.Kind, "缺少 GrailBattleArgs(备战页 ID + 预期落地页 ID)。");
        }

        // 战斗预算沿用既有写死口径：默认 3 分钟（067/019 非「过热环境」，无 5 分钟档）。
        var advanced = await rewardStage.AdvanceBattleToPageAsync(
            context.WindowHandle,
            args.PreparationPageId,
            args.ExpectedPostBattlePageId,
            RewardBattleTimingPolicy.DefaultBattleBudget,
            allowIncompleteLineupConfirmation: true,
            cancellationToken);
        return advanced
            ? GrailCommandResult.Ok(command.Kind, args.ExpectedPostBattlePageId)
            : GrailCommandResult.Fail(command.Kind, "未推进到预期落地页（战斗状态机返回失败；处置归决策层）。");
    }

    private async Task<GrailCommandResult> RespondWishAsync(
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 弹框识别/选侧/点确认全部在执行器与 WishTrialSelectionAutomation 内部完成；
        // 用户模式经组合根注入 executor.Goal（M3 不从 context 另读，两处须由组合根保证一致）。
        // 宏只回「是否应答 + 持有器事件态」事实，收工判定归决策层。
        var responded = await executor.ExecuteWishDialogAsync(context.WindowHandle, cancellationToken);
        var (wishes, obtained, opened, miracle, _, cauldron, _, _) = stateHolder.PeekEventState();
        return GrailCommandResult.Ok(
            GrailCommandKind.M3,
            new GrailWishOutcomeFact(responded, wishes, obtained, opened, miracle, cauldron));
    }

    private async Task<GrailCommandResult> OpenLettersAsync(
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 逐本打开聘用书（含按 F 回对局与兜底选人公理，全在执行器内部）。
        await executor.OpenLettersAsync(context.WindowHandle, context.Goal, cancellationToken);
        var (_, obtained, opened, _, _, _, _, _) = stateHolder.PeekEventState();
        return GrailCommandResult.Ok(GrailCommandKind.M4, new GrailLettersFact(obtained, opened));
    }

    /// <summary>M2 独立开晶矿（N13）：检测并点击当前页面全部晶矿球，返回开启数量。</summary>
    private async Task<GrailCommandResult> OpenMineBallsAsync(
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        var opened = await rewardStage.OpenMineBallsStandaloneAsync(
            context.WindowHandle,
            cancellationToken);
        return GrailCommandResult.Ok(
            GrailCommandKind.M2,
            opened > 0 ? $"已开启晶矿球 {opened} 个。" : "当前页面没有可开启的晶矿球。");
    }

    private async Task<GrailCommandResult> ShopPassAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 动态白名单（2026-09-02）：命杯+昔涟+条件银河学者（BuildShopPurchaseNames），
        // 同名只买一次由快照已拥有集合把关。返回货架全名单供决策层核对购买判定。
        // 带"圣杯"参数=1-3 N14 循环语义（2026-09-03 用户修正：白名单仅命杯+昔涟、
        // 无目标刷新、买到才关店→上场→重开、真实空槽上场、购买后验证）。
        var grailLoopMode = command.Payload is GrailShopPassArgs args && args.GrailLoopMode;
        // 1-3 循环模式硬守卫：空快照（识别未跑/未刷新）会让占用槽位/已购名单全空，
        // 上场退化成"拖到有人槽=互换"风险、已购角色被重买；金币读数未知则本地金币账无从谈起。
        // 拒绝执行，要求先发 I10。
        if (grailLoopMode
            && (executor.LatestSnapshot is null || stateHolder.PeekGold().Value is null))
        {
            return GrailCommandResult.Fail(
                GrailCommandKind.M5,
                "1-3 循环语义要求先发 I10 识别（当前无识别快照或金币读数未知）。");
        }

        var snapshot = executor.LatestSnapshot ?? executor.BuildMinimalSnapshot();
        var bought = await executor.ExecuteShopPassAsync(
            context.WindowHandle,
            snapshot,
            context.PreparationPageId,
            cancellationToken,
            grailLoopMode: grailLoopMode);
        var (holderGold, _) = stateHolder.PeekGold();
        var gold = executor.LastShopPassGold >= 0 ? executor.LastShopPassGold : holderGold ?? 0;
        var shelf = executor.LastShopPassShelfNames;
        return GrailCommandResult.Ok(
            GrailCommandKind.M5,
            new GrailShopPassFact(bought, gold, shelf, executor.LastShopPassBoughtNames));
    }

    private async Task<GrailCommandResult> SelectStrategyAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 优先级写死在宏内（v4.3.1）：彩238 > 都是这家伙的错333 > 金051 > 二极管276；
        // 全未中选最左（软门槛）与三连刷都在组件内部。
        // 决策树 N11（用户拍板）：策略全未命中→按正常流程选最左一张推进，不允许因挑策略弃局。
        // 该开关原只在 M8 长流程里设置，M7 指令路径须显式打开（实测漏开曾回"需退出重刷"）。
        rewardStage.SoftInvestmentStrategyRequirementEnabled = true;
        var preferred = command.Payload is GrailStrategyArgs args && args.PreferredStrategyIds.Count > 0
            ? args.PreferredStrategyIds
            : DefaultStrategyIds;
        var result = await rewardStage.SelectInvestmentStrategyAsync(
            context.WindowHandle,
            preferred,
            cancellationToken);
        return GrailCommandResult.Ok(command.Kind, result);
    }

    /// <summary>投资策略写死优先级（与 MainWindow「刷三星五费」组合一致）。</summary>
    private static IReadOnlySet<string> DefaultStrategyIds { get; } =
        new HashSet<string>(
            [
                InvestmentStrategyPicker.PurchaseSpecialistColor,
                GrailInvestmentStrategyDecider.ItIsHisFaultId,
                InvestmentStrategyPicker.PurchaseSpecialistGold,
                GrailInvestmentStrategyDecider.DiodeId,
            ],
            StringComparer.OrdinalIgnoreCase);

    private async Task<GrailCommandResult> RunOpeningAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 新对局边界：上一局遗留的进程级上场进度必须作废——否则本局会从上一局
        // 用过的槽位继续摆人（2026-09-02 跨局污染实测事故）。
        executor.ResetDeploymentProgressForNewMatch();
        // M8 语义（用户拍板 2026-09-02 白天再定停靠点）：一条命令运行到底——重刷直到
        // 命中 067/019，命中后立刻选中进局，**进入 1-1 备战席立刻停**（不布阵、不进奖励关）。
        // 绝不停在环境选择页等下一条指令；布阵/1-1/1-2/策略由决策层逐条指令接手。
        // RewardStage 配置为预留：StopAtPreparationEntry=true 时永远走不到奖励关控制器。
        var options = new OpeningRerollLoopOptions
        {
            DeployMatchedOpening = true,
            StopAtPreparationEntry = true,
            CompleteRewardStages = false,
            FastReroll = FastRerollMode.Fast,
            BenchSaleMode = PreparationBenchSaleMode.None,
            RewardStage = new RewardStageAutomationOptions
            {
                EnableEarlyStrongFormationPurchase = false,
                EnableGalaxyScholarRewardStrategy = true,
                SoftInvestmentStrategyRequirement = true,
                AutoPurchaseCharacterNames = new HashSet<string>(
                    ["远坂凛", "吉尔伽美什", "Saber"],
                    StringComparer.OrdinalIgnoreCase),
                RetainedCharacterNames = new HashSet<string>(
                    ["远坂凛", "吉尔伽美什", "Saber", GrailRunSnapshot.ArcherName],
                    StringComparer.OrdinalIgnoreCase),
                PreferredInvestmentStrategyIds = DefaultStrategyIds,
            },
        };

        // W1 弹框泵（与生产 GrailRunLoop 同口径，且依赖识别流在跑→泵不盲）：
        // 1-1/1-2 升档强弹祈愿，阻塞游戏输入，必须有人应答；应答后执行器自动开聘用书（F11a）。
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = PumpDialogsAsync(context.WindowHandle, pumpCts.Token);
        try
        {
            var result = await openingLoop(
                context.WindowHandle,
                GrailRunLoop.BuildViableEnvironmentFilter(),
                options,
                cancellationToken);

            // 回报具体命中的环境（用户要求：返回是两个投资环境中的哪一个）。
            // 主源=导航器记录的选中环境；评估条件兜底。
            string? environmentId = result.Navigation?.SelectedInvestmentEnvironmentId;
            string? environmentName = environmentId switch
            {
                "investment_environment_067" => "英雄登场",
                "investment_environment_019" => "命运圣杯邀请",
                _ => null,
            };
            if (environmentId is null && result.Evaluation is { } evaluation)
            {
                var matchedEnvironment = evaluation.MatchedConditions.FirstOrDefault(item =>
                    item.Id.StartsWith("investment_environment_", StringComparison.OrdinalIgnoreCase));
                environmentId = matchedEnvironment?.Id;
                environmentName ??= matchedEnvironment?.DisplayName;
            }

            return GrailCommandResult.Ok(
                GrailCommandKind.M8,
                new GrailOpeningFact(result.Succeeded, result.Message, environmentId, environmentName));
        }
        finally
        {
            pumpCts.Cancel();
            try { await pump; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>opening 期弹框泵：轮询祈愿弹框并应答（弹框期间其余输入全被游戏挡住）。</summary>
    private async Task PumpDialogsAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (listener.IsWishDialogOpen)
            {
                try
                {
                    await executor.ExecuteWishDialogAsync(windowHandle, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 泵故障不外泄（外泄会杀死 M8）：吞掉后继续轮询
                }
            }

            await Task.Delay(800, cancellationToken);
        }
    }
}
