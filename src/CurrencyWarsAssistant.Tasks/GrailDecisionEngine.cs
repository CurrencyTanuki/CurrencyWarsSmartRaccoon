using System.Diagnostics;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 决策层引擎 v3（2026-09-04，四轮对抗审查修复版）：S1~S8 状态机经指令分发器驱动。
/// 依据=DECISION_LAYER_BLUEPRINT_20260903.md + 决策树 + rule.md。
/// 纪律：宏只回事实；变更类指令发前过前置门；分级超时自愈；识别读数覆盖状态机；
/// 节点歧义防御（重启后一律弃局重开）；卖人保护名单三重校验（bond/5费/星徽携带者+保留线+异常注拒采）；
/// 页面门禁：盲点击仅在页面 Unknown 时允许，已知页一律走 A9/正规指令。
/// 单人收工=奇迹代偿已选 或（有 5 费+无限之釜）；全员收工=奇迹代偿+昔涟在场；
/// R3 判死→A9+M8 重开（测试局长期授权）。
/// </summary>
public sealed class GrailDecisionEngine(
    GrailCommandDispatcher dispatcher,
    GrailRunStateHolder stateHolder,
    GrailOperationExecutor executor,
    GameDataCatalog gameData,
    Action<string> emit,
    Func<nint, int, int, CancellationToken, Task<bool>>? genericClick = null,
    Func<nint, CancellationToken, Task<bool>>? pressInteractKey = null,
    Func<nint, CancellationToken, Task<bool>>? retreatFromBattleView = null)
{
    private readonly Stopwatch _runClock = Stopwatch.StartNew();

    private GrailUserGoal _goal = GrailUserGoal.Single;

    /// <summary>决策层内部统计（汇报用）。</summary>
    public int RunsCompleted { get; private set; }
    public int RunsAbandoned { get; private set; }

    private async Task<GrailCommandResult> SendAsync(
        string commandText, GrailCommand command, nint window, CancellationToken ct)
    {
        var context = new GrailCommandContext(window, "preparation_generic", _goal);
        GrailCommandResult result;
        try
        {
            // 分级超时（04:4x 实测：45 秒一刀切会谋杀 M8 的合法长重刷）：
            // M8=35 分钟（其内部上限+余量）；M1=6 分钟（战斗预算+落地余量）；
            // M3/M5=5 分钟；M2=3 分钟；A9=3 分钟（弃局序列最长合法 ≈125 秒+余量）；I/A=60 秒。
            var timeout = command.Kind switch
            {
                GrailCommandKind.M8 => TimeSpan.FromMinutes(35),
                GrailCommandKind.M1 => TimeSpan.FromMinutes(6),
                GrailCommandKind.M5 => TimeSpan.FromMinutes(5),
                GrailCommandKind.M3 => TimeSpan.FromMinutes(5),
                GrailCommandKind.M2 => TimeSpan.FromMinutes(3),
                GrailCommandKind.A9 => TimeSpan.FromMinutes(3),
                _ => TimeSpan.FromSeconds(60),
            };
            using var orphanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                result = await dispatcher.DispatchAsync(command, context, orphanCts.Token)
                    .WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                orphanCts.Cancel();
                emit($"[决策层/{_runClock.Elapsed:mm\\:ss}] {commandText} ⇒ 失败：指令执行超过 {timeout.TotalSeconds:F0} 秒未返回（疑似卡死），已取消并跳过。");
                return GrailCommandResult.Fail(command.Kind, $"指令执行超时（{timeout.TotalSeconds:F0} 秒）未返回。");
            }
        }
        catch (InvalidOperationException)
        {
            // 前台守卫绑定失效等环境异常（审查 P2）：如实上报为失败事实，不让整循环裸崩。
            emit($"[决策层/{_runClock.Elapsed:mm\\:ss}] {commandText} ⇒ 失败：环境异常（窗口绑定失效/失焦超时）。");
            return GrailCommandResult.Fail(command.Kind, "环境异常：窗口绑定失效或失焦超时。");
        }

        emit($"[决策层/{_runClock.Elapsed:mm\\:ss}] {commandText} ⇒ {(result.Error is null ? "OK" : "失败")}：{result.Error ?? Describe(result.Payload)}");
        return result;
    }

    private static string Describe(object? payload) => payload switch
    {
        GrailOpeningFact o => $"命中={o.MatchedEnvironmentName ?? "—"} {o.Message}",
        GrailShopPassFact s => $"买到={string.Join(",", s.BoughtCharacterNames ?? [])} 金={s.GoldAfter}",
        GrailWishOutcomeFact w => $"应答={w.Responded} 累计={w.WishesResponded}",
        GrailRunSnapshot s => $"羁绊={s.BondMemberCount} 金={s.Gold} 血={s.TeamHealth?.ToString() ?? "?"}",
        GrailPageFact p => $"页面={p.PageId ?? "未知"}",
        GrailSellResult r => $"卖出={r.SoldCount} 金={r.EstimatedGold}",
        _ => string.Empty,
    };

    private async Task<GrailRunSnapshot?> SnapshotAsync(nint window, CancellationToken ct)
    {
        var result = await SendAsync("I10", new GrailCommand(GrailCommandKind.I10), window, ct);
        if (result.Error is not null || result.Payload is not GrailRunSnapshot snapshot)
        {
            return null;
        }

        // 引擎自刷新执行器快照（补审 P1 修复）：M5 圣杯前置门依赖它——
        // 引擎内部指令不经过文件通道的 RefreshLatestSnapshot，必须自给自足。
        executor.LatestSnapshot = snapshot;
        return snapshot;
    }

    /// <summary>带退避的快照读取：转场/识别冻结期单帧失败是常态（实测教训），重试至多 8×5 秒。</summary>
    private async Task<GrailRunSnapshot?> SnapshotWithRetryAsync(nint window, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await SnapshotAsync(window, ct);
            if (snapshot is not null)
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        return null;
    }

    private async Task<GrailPageFact?> PageAsync(nint window, CancellationToken ct)
    {
        var result = await SendAsync("I1", new GrailCommand(GrailCommandKind.I1), window, ct);
        return result.Error is null ? result.Payload as GrailPageFact : null;
    }

    /// <summary>等待祈愿弹框并应答（延迟弹出已实测，最多等 90 秒）；无弹框返回 false。</summary>
    private async Task<bool> AnswerWishIfUpAsync(nint window, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var page = await PageAsync(window, ct);
            if (page is { WishDialogOpen: true })
            {
                var m3 = await SendAsync("M3", new GrailCommand(GrailCommandKind.M3), window, ct);
                return m3.Error is null;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return false;
    }

    /// <summary>确认升档祈愿已应答（弹框在屏必答；不在屏按已答/延迟处理）。</summary>
    private async Task EnsureWishAnsweredAsync(nint window, CancellationToken ct) =>
        await AnswerWishIfUpAsync(window, ct);

    /// <summary>剥掉明细名上的 [星徽]/[装备] 后缀，还原纯角色名。</summary>
    private static string PureName(string detailName)
    {
        var open = detailName.IndexOf('[');
        return open > 0 ? detailName[..open] : detailName;
    }

    /// <summary>把识别到的、未上场的命杯成员部署到空位（A1 显式前台槽，逐个 I10 复核）。</summary>
    private async Task DeployBondMembersAsync(nint window, CancellationToken ct)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var snapshot = await SnapshotWithRetryAsync(window, ct);
            if (snapshot is null)
            {
                return;
            }

            var bondNames = executor.GrailBondMemberNames;
            var pendingBench = snapshot.BenchCharacterDetails
                .Select(item => PureName(item.Split(':')[^1]))
                .FirstOrDefault(name => bondNames.Contains(name));
            if (pendingBench is null || snapshot.OccupiedFrontSlots.Count >= 4)
            {
                return; // 无可部署或前台满（后台部署归 N17a 之后流程）
            }

            var slot = Enumerable.Range(0, 4).FirstOrDefault(i => !snapshot.OccupiedFrontSlots.Contains(i));
            var deploy = await SendAsync(
                $"A1 {pendingBench} 前台 {slot + 1}",
                new GrailCommand(GrailCommandKind.A1,
                    new GrailDeployArgs(pendingBench, PreparationLane.Front, slot)),
                window, ct);
            if (deploy.Error is not null)
            {
                return; // 识别不到该名（识别缺陷）→ 交外层对账，绝不盲拖
            }

            await EnsureWishAnsweredAsync(window, ct);
        }
    }

    /// <summary>
    /// Unknown 页处置（审查修正版）：仅当页面真正 Unknown（识别表外阻塞态，如位面图/
    /// 积分弹窗变体）时尝试 plane_progress 续进热点 (960,720) 与世界内交互键；
    /// 已知页绝不盲点（坑39/审查 P1：(960,540) 会误选投资环境卡、(1290,615) 落在备战页角色卡）。
    /// </summary>
    private async Task DismissUnknownPageAsync(nint window, CancellationToken ct)
    {
        var page = await PageAsync(window, ct);
        if (page is null || !string.IsNullOrWhiteSpace(page.PageId))
        {
            return; // 有页面 ID=非未知态，交由对应流程处理
        }

        if (genericClick is not null)
        {
            await genericClick(window, 960, 720, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (pressInteractKey is not null)
        {
            await pressInteractKey(window, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>A4：仅当非 067 局且物品栏有未携带星徽（I7）时装配到 1 号位角色。</summary>
    private async Task AssembleBadgeIfAvailableAsync(nint window, CancellationToken ct, bool is067Run)
    {
        if (is067Run)
        {
            return; // 067 局禁发 A4
        }

        var i7 = await SendAsync("I7", new GrailCommand(GrailCommandKind.I7), window, ct);
        if (i7.Payload is GrailBadgeFact badge && badge.Uncarried > 0)
        {
            await SendAsync("A4 前台 1",
                new GrailCommand(GrailCommandKind.A4,
                    new GrailPositionArgs(PreparationLane.Front, 0)), window, ct);
        }
    }

    /// <summary>
    /// S1A/S1B：卖光冗余凑金币。保护名单三重校验（审查 P1 修复）：
    /// ①官方数据 bond/纯 5 费；②明细 [星徽] 标记（识别∪账本并集产物）；
    /// ③数据查不到的名字（识别误名）绝不卖——宁少卖不误卖。
    /// 保留线封顶=快照 SellableBeyondKeepLineCount；AnomalyNotes 含同名多处=拒采整轮。
    /// </summary>
    private async Task<int> SellRedundantsAsync(nint window, GrailRunSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.AnomalyNotes.Contains("同名多处", StringComparison.Ordinal))
        {
            emit("[决策层] 卖人前发现同名多处异常注（识别身份存疑）——本轮拒绝卖出，交对账。");
            return 0;
        }

        var cap = snapshot.SellableBeyondKeepLineCount;
        var sold = 0;

        // 备战席：I10 明细的槽号即绝对位置（实测不压缩）。
        foreach (var detail in snapshot.BenchCharacterDetails)
        {
            if (sold >= cap)
            {
                break;
            }

            var separator = detail.IndexOf(':');
            if (separator <= 0 || !int.TryParse(detail.AsSpan(0, separator), out var slotIndex))
            {
                continue;
            }

            var name = PureName(detail[(separator + 1)..]);
            var character = ResolveProtectedCharacter(name);
            if (character is null)
            {
                emit($"[决策层] 卖人跳过「{name}」：数据查不到该名（识别误名保护）。");
                continue;
            }

            var sell = await SendAsync($"A3 {slotIndex + 1}",
                new GrailCommand(GrailCommandKind.A3, new GrailBenchSlotArgs(slotIndex)), window, ct);
            if (sell.Error is null)
            {
                sold++;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        // 场上冗余：F/B 槽固定（审查 P2：后台槽同样要卖，否则 R3 误判无可卖）。
        foreach (var detail in snapshot.DeployedCharacterDetails)
        {
            var head = detail.Split(':')[0];
            var isBack = head.StartsWith('B');
            if ((!head.StartsWith('F') && !isBack) || !int.TryParse(head.AsSpan(1), out var slotNumber))
            {
                continue;
            }

            var name = PureName(detail.Split(':')[^1]);
            var character = ResolveProtectedCharacter(name);
            if (character is null)
            {
                continue;
            }

            var isFive = GrailOperationExecutor.IsPureFiveCostCharacter(character);
            var isBond = character.BondNames.Any(b =>
                b is not null && b.Contains("命运圣杯", StringComparison.Ordinal));
            var isCarrier = detail.Contains("[星徽]", StringComparison.Ordinal);
            if (isBond || isFive || isCarrier)
            {
                continue;
            }

            var sell = await SendAsync($"A2 {(isBack ? "后台" : "前台")} {slotNumber}",
                new GrailCommand(GrailCommandKind.A2,
                    new GrailPositionArgs(isBack ? PreparationLane.Back : PreparationLane.Front, slotNumber - 1)),
                window, ct);
            if (sell.Error is null)
            {
                sold++;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        return sold;
    }

    private CurrencyWarsCharacterData? ResolveProtectedCharacter(string name) =>
        gameData.CurrencyWarsCharacters.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 主循环：M8 重开 → 备战运营 → 终局判定 → R3 重开。
    /// 节点歧义防御：入口处若已有对局在备战页（引擎重启后无法确知节点）→ 先弃局再刷。
    /// </summary>
    public async Task RunAsync(nint window, GrailUserGoal goal, CancellationToken ct)
    {
        _goal = goal;
        stateHolder.Reset();
        executor.ResetDeploymentProgressForNewMatch();
        emit("[决策层] 启动：目标=" + (goal == GrailUserGoal.All ? "全员" : "单人"));

        while (!ct.IsCancellationRequested)
        {
            var entry = await SnapshotWithRetryAsync(window, ct);
            if (entry is not null)
            {
                emit("[决策层] 检测到备战页已有对局（节点歧义）——先弃局重开，绝不在未知节点操作。");
                await SendAsync("A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            GrailCommandResult? m8 = null;
            var arrived = false;
            var hit067 = false;
            for (var attempt = 0; attempt < 3 && !arrived && !ct.IsCancellationRequested; attempt++)
            {
                m8 = await SendAsync("M8", new GrailCommand(GrailCommandKind.M8), window, ct);
                var fact = m8.Payload as GrailOpeningFact;
                if (m8.Error is null && fact is { Succeeded: true })
                {
                    arrived = true;
                    hit067 = string.Equals(fact.MatchedEnvironmentName, "英雄登场", StringComparison.Ordinal);
                }
                else if (m8.Error is not null)
                {
                    // 守卫拦截/导航失败：先解除 Unknown 阻塞页（仅页面未知时），
                    // 再走世界内撤退链路（货币战争交互→战视图→Esc→暂停页→撤退），
                    // 最后 A9 兜底。已知页（备战/商店/战斗）不盲点。
                    await DismissUnknownPageAsync(window, ct);
                    emit("[决策层] M8 未成（守卫或导航）——执行世界内撤退链路后 A9 清场。");
                    if (retreatFromBattleView is not null)
                    {
                        try
                        {
                            await retreatFromBattleView(window, ct).WaitAsync(
                                TimeSpan.FromMinutes(3), ct);
                        }
                        catch (TimeoutException)
                        {
                            emit("[决策层] 撤退链路超时（3 分钟）——继续 A9 兜底。");
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception retreatError)
                        {
                            emit($"[决策层] 撤退链路异常（不致命）：{retreatError.Message}");
                        }
                    }
                    await SendAsync("A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                else if (fact?.Message.Contains("已到达首次备战页面", StringComparison.Ordinal) == true)
                {
                    // 已在 1-1 备战页（环境未命中轮的停靠事实）——视作到达，交运营循环评估。
                    arrived = true;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }

            if (!arrived)
            {
                emit("[决策层] M8 三次尝试未到达备战席——A9 后重开外层循环。");
                await SendAsync("A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            var outcome = await RunPreparationCycleAsync(window, hit067, ct);
            if (outcome == PreparationOutcome.Win)
            {
                RunsCompleted++;
                emit("[决策层] ★ 收工条件达成——停机（等待新指令）。");
                return;
            }

            RunsAbandoned++;
            if (!ct.IsCancellationRequested)
            {
                emit("[决策层] 本局判定结束（R3/失败）——弃局重开下一局。");
                await SendAsync("A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
    }

    private enum PreparationOutcome
    {
        Win,
        Dead,
        Interrupted,
    }

    /// <summary>
    /// 备战运营循环（已知节点：M8 到达确认后的 1-1 起点）：
    /// S2 1-1 → S3 1-2 → S4 策略+晶矿+卖冗余 → S5 M5 圣杯循环+祈愿 → S7 终局/R3。
    /// </summary>
    private async Task<PreparationOutcome> RunPreparationCycleAsync(
        nint window, bool hit067, CancellationToken ct)
    {
        // ---- S2：1-1（人口 3；067 局禁 A4）----
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        var snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            return PreparationOutcome.Interrupted;
        }

        await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        await DeployBondMembersAsync(window, ct);
        await AssembleBadgeIfAvailableAsync(window, ct, hit067);
        await EnsureWishAnsweredAsync(window, ct);
        var m1 = await SendAsync("M1 preparation_generic reward_shop",
            new GrailCommand(GrailCommandKind.M1,
                new GrailBattleArgs("preparation_generic", "reward_shop")), window, ct);
        if (m1.Error is not null)
        {
            return PreparationOutcome.Dead; // 战斗未推进：外层弃局重开
        }

        // ---- S3：1-2（人口 3，进场先商店）----
        await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            return PreparationOutcome.Interrupted;
        }

        await DeployBondMembersAsync(window, ct);
        await AssembleBadgeIfAvailableAsync(window, ct, hit067); // 1-2 新得徽补装（审查 P3）
        await EnsureWishAnsweredAsync(window, ct);
        m1 = await SendAsync("M1 preparation_generic investment_strategy",
            new GrailCommand(GrailCommandKind.M1,
                new GrailBattleArgs("preparation_generic", "investment_strategy")), window, ct);
        if (m1.Error is not null)
        {
            return PreparationOutcome.Dead; // 战斗未推进：外层弃局重开
        }

        // ---- S4：投资策略（禁选阿哈大悦已内置于 M7）----
        await SendAsync("M7", new GrailCommand(GrailCommandKind.M7), window, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await EnsureWishAnsweredAsync(window, ct);

        // ---- S4 顺序（2026-09-03 终版）：策略后弹店=M5 裸收起（货架有命杯则机会性买下，
        // 2026-09-03 实测获用户默认）→ 晶矿 → 卖冗余 → M5 圣杯循环 ----
        await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            // 快照持续失败=页面可能被识别表外的弹窗阻塞：先解除再试一次（审查 P2）。
            await DismissUnknownPageAsync(window, ct);
            snapshot = await SnapshotWithRetryAsync(window, ct);
            if (snapshot is null)
            {
                return PreparationOutcome.Interrupted;
            }
        }

        if (snapshot.Gold < 8)
        {
            await SellRedundantsAsync(window, snapshot, ct);
        }

        // ---- S5/S7：运营循环 ----
        for (var opsRound = 0; opsRound < 30 && !ct.IsCancellationRequested; opsRound++)
        {
            await SendAsync("M5 圣杯",
                new GrailCommand(GrailCommandKind.M5,
                    new GrailShopPassArgs(GrailLoopMode: true)), window, ct);
            snapshot = await SnapshotWithRetryAsync(window, ct);
            if (snapshot is null)
            {
                return PreparationOutcome.Interrupted;
            }

            await DeployBondMembersAsync(window, ct);
            await EnsureWishAnsweredAsync(window, ct);

            // ---- S7 终局判定（按目标模式分流，审查 P2：不得用单人口径判全员）----
            var (_, _, _, miracle, miracleAtHealth, cauldron, _, _) = stateHolder.PeekEventState();
            if (_goal == GrailUserGoal.Single)
            {
                if (miracle || (cauldron && snapshot.HasFiveCostBody))
                {
                    emit("[决策层] ★ 收工条件达成（单人）——停机。");
                    return PreparationOutcome.Win;
                }
            }
            else
            {
                if (miracle && snapshot.XilianOnField)
                {
                    emit("[决策层] ★ 收工条件达成（全员：奇迹代偿+昔涟在场）——停机。");
                    return PreparationOutcome.Win;
                }
            }

            // R3 候选：金不足刷新价 且 卖光冗余后仍不足（审查 P3：用动态刷新价）
            if (snapshot.Gold < snapshot.RefreshGoldCost)
            {
                var sold = await SellRedundantsAsync(window, snapshot, ct);
                snapshot = await SnapshotWithRetryAsync(window, ct) ?? snapshot;
                if (snapshot.Gold < snapshot.RefreshGoldCost && sold == 0)
                {
                    emit("[决策层] R3：金币耗尽且无可卖——弃局重开。");
                    return PreparationOutcome.Dead;
                }
            }
        }

        return PreparationOutcome.Dead; // 运营轮上限（异常兜底）
    }
}
