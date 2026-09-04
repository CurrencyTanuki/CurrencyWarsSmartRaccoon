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
        // 诚实包装（1.2.32 候选，2026-09-03 落地）：战斗组件抛出的异常（如 GPU TDR 崩溃把
        // 游戏窗口打挂后的 COM/DXGI「参数错误」）必须包装成失败事实回传，绝不裸抛给通道层——
        // 决策层拿到的是可解读事实而非神秘异常，页面状态未知须先 I1 核实。
        bool advanced;
        try
        {
            advanced = await rewardStage.AdvanceBattleToPageAsync(
                context.WindowHandle,
                args.PreparationPageId,
                args.ExpectedPostBattlePageId,
                RewardBattleTimingPolicy.DefaultBattleBudget,
                allowIncompleteLineupConfirmation: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // 急停/取消不是故障，交通道层按取消语义回执
        }
        catch (Exception exception)
        {
            return GrailCommandResult.Fail(
                command.Kind,
                $"战斗推进中组件抛出 {exception.GetType().Name}：{exception.Message}" +
                "（常见诱因=游戏窗口失效/GPU 崩溃，当前页面状态未知）——先用 I1 核实游戏状态再接续。");
        }

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

    /// <summary>
    /// 上一局未结算守卫回执·入口拒绝版（2026-09-03 用户拍板，操作层）：M8 下发时最新帧已在
    /// 备战页=当前就在未结算旧局里。M8 拒绝执行、未点击任何东西；重刷请先结算旧局
    /// （宏只回事实，停止决策归决策层）。
    /// </summary>
    internal const string UnsettledRunGuardMessage =
        "检测到游戏未经过敌人概览/投资环境选择页就直接进入了备战页——上一局对局未结算，" +
        "已被游戏自动续局。为防止误结算影响你正在进行的对局，M8 刷开局已立即停止，" +
        "未对该局做任何操作；如要继续刷开局，请先结算/退出这局对局后再发 M8。";

    /// <summary>守卫回执·流程中止版：M8 已点击开局（可能触发续局）后中途发现续局签名并中止——
    /// 未布阵、未进奖励关、未结算该局（审查 P2：不得声称"未做任何操作"）。</summary>
    internal const string UnsettledRunInterruptedMessage =
        "M8 中途检测到未经过敌人概览/投资环境选择页就直接进入了备战页——上一局对局未结算，" +
        "已被游戏自动续局。为防止误结算影响你正在进行的对局，M8 已立即中止：" +
        "未布阵、未进奖励关、未结算该局。如要继续刷开局，请先结算/退出这局对局后再发 M8。";

    /// <summary>守卫回执·识别流不可用版（审查 P1：无新鲜帧时绝不伪造"续局"事实）。</summary>
    internal const string RecognitionUnavailableAfterOpeningMessage =
        "M8 已到达备战席，但识别流全程无新鲜帧，上一局未结算守卫无法判定（守卫需要识别流页面序列）。" +
        "请先确认识别会话在运行（START），再用 I1/I10 核实当前盘面后再继续操作。";

    /// <summary>该页是否为开局页序列的一员（敌人概览/投资环境选择——续局守卫的判据）。</summary>
    private static bool IsRunEntryPage(string? pageId) =>
        pageId is not null
        && (pageId.StartsWith("enemy_overview", StringComparison.OrdinalIgnoreCase)
            || pageId.StartsWith("investment_environment", StringComparison.OrdinalIgnoreCase));

    private async Task<GrailCommandResult> RunOpeningAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 守卫前置检查（先于一切重置——续局判定成立时连状态都不许清，旧局事实必须原样保留）：
        // M8 下发时若最新帧已在备战页且帧新鲜，=当前就在未结算旧局里，立即拒绝。
        var entryAnalysis = listener.LatestAnalysis;
        if (entryAnalysis is not null
            && entryAnalysis.Snapshot.PageId.Value is { } entryPageId
            && entryPageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase)
            && entryAnalysis.Snapshot.AsOf is { } entryAt
            && DateTimeOffset.Now - entryAt <= TimeSpan.FromSeconds(10))
        {
            return GrailCommandResult.Fail(GrailCommandKind.M8, UnsettledRunGuardMessage);
        }

        // 新对局边界：上一局遗留的进程级上场进度必须作废——否则本局会从上一局
        // 用过的槽位继续摆人（2026-09-02 跨局污染实测事故）。
        executor.ResetDeploymentProgressForNewMatch();
        // 持有器跨局复位（规格「跨局重置」条款）：指令测试台路径没有 GrailRunLoop 的
        // 每局 Reset，已购集合/事件态/星徽账本若不清，第 2 局会继承第 1 局的
        // "已拥有"假象（M5 拒买真目标）与祈愿计数（G1 假判死）——M8=对局边界在此统一清零。
        stateHolder.Reset();
        // M8 语义（用户拍板 2026-09-02 白天再定停靠点）：一条命令运行到底——重刷直到
        // 命中 067/019，命中后立刻选中进局，**进入 1-1 备战席立刻停**（不布阵、不进奖励关）。
        // 绝不停在环境选择页等下一条指令；布阵/1-1/1-2/策略由决策层逐条指令接手。
        // RewardStage 配置为预留：StopAtPreparationEntry=true 时永远走不到奖励关控制器。
        // MaximumRuntime=30 分钟（审查 P1 修复）：同时关掉"失败后被动恢复监控自动弃局"兜底
        // （ShouldMonitorAfterFailure 仅在无时限时为真）——兜底弃局会替用户结算未结算旧局，
        // 与续局守卫直接冲突；M8 失败改为事实回决策层处置（等待必带上限，30 分钟=实测余量极大的界）。
        var options = new OpeningRerollLoopOptions
        {
            DeployMatchedOpening = true,
            StopAtPreparationEntry = true,
            CompleteRewardStages = false,
            FastReroll = FastRerollMode.Fast,
            BenchSaleMode = PreparationBenchSaleMode.None,
            MaximumRuntime = TimeSpan.FromMinutes(30),
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
        // 续局守卫状态（创建于 try 内——审查 P3：任何异常路径都不许泄漏轮询任务）。
        // 看门狗：250ms 轮询识别流页面序列；①记录是否见过开局页（敌人概览/投资环境选择）；
        // ②未见开局页却连续 2 帧读到新鲜备战页=续局签名 → 立即取消 openingLoop（抢在
        // 任何兜底弃局之前停止一切操作）；③全程无新鲜帧 → 守卫不可判定（绝不伪造续局事实）。
        var runEntryPagesSeen = false;
        var sawFreshFrame = false;
        var tripped = false;
        var prepStrikes = 0;
        using var guardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pageTraceDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageTrace = Task.Run(async () =>
        {
            while (!pageTraceDone.Task.IsCompleted)
            {
                var analysis = listener.LatestAnalysis;
                if (analysis is not null)
                {
                    if (analysis.Snapshot.AsOf is { } frameAt
                        && DateTimeOffset.Now - frameAt <= TimeSpan.FromSeconds(10))
                    {
                        sawFreshFrame = true;
                    }

                    var page = analysis.Snapshot.PageId.Value;
                    if (IsRunEntryPage(page))
                    {
                        runEntryPagesSeen = true;
                        prepStrikes = 0;
                    }
                    else if (!tripped
                        && page is not null
                        && page.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase)
                        && analysis.Snapshot.AsOf is { } prepFrameAt
                        && DateTimeOffset.Now - prepFrameAt <= TimeSpan.FromSeconds(10))
                    {
                        prepStrikes++;
                        if (prepStrikes >= 2 && !runEntryPagesSeen)
                        {
                            tripped = true;
                            guardCts.Cancel(); // 抢在导航失败兜底/弃局之前停住一切
                        }
                    }
                    else
                    {
                        prepStrikes = 0;
                    }
                }

                try
                {
                    await Task.Delay(250, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
        try
        {
            OpeningRerollLoopResult result;
            try
            {
                result = await openingLoop(
                    context.WindowHandle,
                    GrailRunLoop.BuildViableEnvironmentFilter(),
                    options,
                    guardCts.Token);
            }
            catch (OperationCanceledException) when (tripped)
            {
                // 续局签名已坐实、看门狗主动中止：以失败事实收场（急停取消不经此路，when 条件区分）。
                return GrailCommandResult.Fail(GrailCommandKind.M8, UnsettledRunInterruptedMessage);
            }

            // 守卫兜底判定：循环自然结束（成功停靠）但全程未见开局页——续局签名（竞态兜底）。
            // 三态判定（审查 P1）：识别流全程无新鲜帧时守卫不可判定，绝不伪造"续局"事实。
            // 1.2.58（独立分析 P-04）：快速刷开局盲点连点下页面停留极短，识别流可能
            // 整程没"见过"开局页（竞态误伤刚命中的合格局）——成功停靠时先 I1 复核
            // 当前页，已停靠 preparation_ 备战页本身就是"非续局"的反证，按成功放行。
            if (result.Succeeded && !runEntryPagesSeen)
            {
                var currentPage = ProbeLatestPageId();
                // 补审 P2-1（1.2.69）：族匹配——精确匹配 preparation_generic 时，识别流
                // 若未来产出族内其他 ID（JSON 新增节点级备战页/诊断回退）会让放行形同
                // 虚设；当前识别流实际只产出 preparation_generic（交叉复核 F7 澄清：
                // JSON 现无 preparation_1_1/1_2 定义），本改动是行为等价的防御收紧。
                if (!string.IsNullOrEmpty(currentPage) &&
                    currentPage.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase))
                {
                    // 竞态放行：无 eventSink 可落日志，放行事实由 M8 成功回执
                    // （命中环境名正常返回）与决策层日志共同佐证。
                }
                else
                {
                    return GrailCommandResult.Fail(GrailCommandKind.M8,
                        sawFreshFrame ? UnsettledRunInterruptedMessage : RecognitionUnavailableAfterOpeningMessage);
                }
            }

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
            pageTraceDone.TrySetResult();
            try { await pageTrace; }
            catch (OperationCanceledException) { }
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

    /// <summary>
    /// 1.2.58（独立分析 P-04）：守卫竞态复核——取识别流最新页面 ID（10 秒内新鲜才算，
    /// 否则 null=不可判定）。
    /// </summary>
    private string? ProbeLatestPageId()
    {
        var analysis = listener.LatestAnalysis;
        if (analysis?.Snapshot?.PageId is { } pageId &&
            analysis.Snapshot.AsOf is { } at &&
            DateTimeOffset.Now - at <= TimeSpan.FromSeconds(10))
        {
            return pageId.Value;
        }

        return null;
    }
}
