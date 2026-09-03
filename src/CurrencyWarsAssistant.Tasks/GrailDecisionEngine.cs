using System.Diagnostics;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 决策层引擎（2026-09-04，用户令"决策层软件化"）：S1~S8 状态机经指令分发器驱动，
/// 自主决策/自主拖动/自主完成全部操作。依据=DECISION_LAYER_BLUEPRINT_20260903.md +
/// 决策树（定稿版+09-03/04 注）+ rule.md 红线。
/// 纪律：宏只回事实；变更类指令发前过前置门；等待必带上限；识别读数直接覆盖状态机预期；
/// 果断弃局（操作不动/连续失败）不改状态机、交重开循环。
/// 单人收工=奇迹代偿已选 或（有 5 费+无限之釜）；R3 判死→A9+M8 重开（测试局长期授权）。
/// 节点歧义防御（04:13 监督教训）：引擎重启后无法确知当前节点 → 一律弃局重开，
/// 绝不在未知节点上执行 M1/商店等节点相关操作。
/// </summary>
public sealed class GrailDecisionEngine(
    GrailCommandDispatcher dispatcher,
    GrailRunStateHolder stateHolder,
    GrailOperationExecutor executor,
    GameDataCatalog gameData,
    Action<string> emit,
    Func<nint, int, int, CancellationToken, Task<bool>>? genericClick = null,
    Func<nint, CancellationToken, Task<bool>>? pressInteractKey = null)
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
        // 分级超时（04:4x 实测教训：45 秒一刀切会谋杀 M8 的合法长重刷）：
        // M8=35 分钟（其内部上限+余量）；M1=6 分钟（战斗预算 3 分钟+落地余量）；
        // M3/M5=5 分钟（M5 圣杯可连刷数分钟）；M2=3 分钟；I/A 及其余=60 秒。
        var timeout = command.Kind switch
        {
            GrailCommandKind.M8 => TimeSpan.FromMinutes(35),
            GrailCommandKind.M1 => TimeSpan.FromMinutes(6),
            GrailCommandKind.M5 => TimeSpan.FromMinutes(5),
            GrailCommandKind.M3 => TimeSpan.FromMinutes(5),
            GrailCommandKind.M2 => TimeSpan.FromMinutes(3),
            _ => TimeSpan.FromSeconds(60),
        };
        using var orphanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // 任何指令挂死都必须超时自愈；超时时同时取消被遗弃的任务（防新旧导航打架）。
            result = await dispatcher.DispatchAsync(command, context, orphanCts.Token)
                .WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            orphanCts.Cancel();
            emit($"[决策层/{_runClock.Elapsed:mm\\:ss}] {commandText} ⇒ 失败：指令执行超过 {timeout.TotalSeconds:F0} 秒未返回（疑似卡死），已取消并跳过。");
            return GrailCommandResult.Fail(command.Kind, $"指令执行超时（{timeout.TotalSeconds:F0} 秒）未返回。");
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
        return result.Error is null ? result.Payload as GrailRunSnapshot : null;
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
                .Select(item => item.Split(':')[^1])
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
    /// 通用弹窗解除（2026-09-04 夜间批次）：卡在未知页时依次尝试——点右上 ✕ (1860,64)、
    /// 点屏幕空白 (960,540)、再点 ✕；每步后 I1 验证，命中已知页立即返回。
    /// </summary>
    private async Task DismissBlockingPopupsAsync(nint window, CancellationToken ct)
    {
        if (genericClick is null)
        {
            return; // 未注入通用点击能力时跳过（交 A9/重开处理）
        }

        (int X, int Y)[] attempts = [(1860, 64), (960, 540), (1860, 64)];
        foreach (var (x, y) in attempts)
        {
            await genericClick(window, x, y, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var page = await PageAsync(window, ct);
            if (page is not null && !string.IsNullOrWhiteSpace(page.PageId))
            {
                return; // 页面已可识别=解除成功
            }
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

    /// <summary>S1A/S1B：卖光冗余凑金币（保留线=已获星徽数由组装器把关；槽位固定不压缩，逐槽判定）。</summary>
    private async Task<int> SellRedundantsAsync(nint window, GrailRunSnapshot snapshot, CancellationToken ct)
    {
        var sold = 0;
        // 备战席：I10 明细的槽号即绝对位置（实测不压缩）。
        foreach (var detail in snapshot.BenchCharacterDetails)
        {
            var separator = detail.IndexOf(':');
            if (separator <= 0 || !int.TryParse(detail.AsSpan(0, separator), out var slotIndex))
            {
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

        // 场上冗余：F 槽固定；保护名单双源=官方数据身份+识别明细 [星徽] 标记。
        foreach (var detail in snapshot.DeployedCharacterDetails)
        {
            var head = detail.Split(':')[0];
            if (!head.StartsWith('F') || !int.TryParse(head.AsSpan(1), out var slotNumber))
            {
                continue;
            }

            var name = detail.Split(':')[^1].Split('[')[0];
            var character = gameData.CurrencyWarsCharacters.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            var isFive = character is not null && GrailOperationExecutor.IsPureFiveCostCharacter(character);
            var isBond = character is not null && character.BondNames.Any(b =>
                b is not null && b.Contains("命运圣杯", StringComparison.Ordinal));
            var isCarrier = detail.Contains("[星徽]", StringComparison.Ordinal);
            if (isBond || isFive || isCarrier)
            {
                continue;
            }

            var sell = await SendAsync($"A2 前台 {slotNumber}",
                new GrailCommand(GrailCommandKind.A2,
                    new GrailPositionArgs(PreparationLane.Front, slotNumber - 1)), window, ct);
            if (sell.Error is null)
            {
                sold++;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        return sold;
    }

    /// <summary>
    /// 主循环：M8 重开 → 1-1/1-2 → 1-3 运营 → 终局判定 → R3 重开。
    /// 单人收工=奇迹代偿已选 或（有 5 费+无限之釜）→ 停机。
    /// 节点歧义防御：入口处若已有对局在备战页（引擎重启后无法确知节点）→ 先弃局再刷，
    /// 绝不在未知节点上执行节点相关操作（M1 灾难防御）。
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
                else if (m8.Error is not null || fact?.Message.Contains("祈愿试炼", StringComparison.Ordinal) == true)
                {
                    // 守卫拦截/残留页面：A9 清场后重发。
                    emit("[决策层] M8 未成（守卫或残留页），A9 清场后重试。");
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
                emit("[决策层] M8 三次尝试未到达备战席——通用弹窗解除 + 世界内交互，再 A9 重开外层循环。");
                await DismissBlockingPopupsAsync(window, ct);
                // 世界内处理（06:4x 实测）：角色可能站在货币战争圆桌旁（交互提示在屏），
                // M8 导航对此无步骤、A9 弃局菜单也打不开——先点击交互提示进入战视图，
                // A9 的 Esc/退出菜单在战视图内才能正常工作。
                if (genericClick is not null)
                {
                    await genericClick(window, 1290, 615, ct);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }

                if (pressInteractKey is not null)
                {
                    await pressInteractKey(window, ct);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                }

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
        await SendAsync("M1 preparation_generic reward_shop",
            new GrailCommand(GrailCommandKind.M1,
                new GrailBattleArgs("preparation_generic", "reward_shop")), window, ct);

        // ---- S3：1-2（人口 3，进场先商店）----
        await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            return PreparationOutcome.Interrupted;
        }

        await DeployBondMembersAsync(window, ct);
        await EnsureWishAnsweredAsync(window, ct);
        var m1 = await SendAsync("M1 preparation_generic investment_strategy",
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

        // ---- S4 顺序（2026-09-03 终版）：晶矿 → 卖冗余 → M5 圣杯循环 ----
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            return PreparationOutcome.Interrupted;
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

            // ---- S7 终局判定（单人）----
            var (_, _, _, miracle, _, cauldron, _, _) = stateHolder.PeekEventState();
            if (miracle || (cauldron && snapshot.HasFiveCostBody))
            {
                emit("[决策层] ★ 收工条件达成（单人）——停机。");
                return PreparationOutcome.Win;
            }

            // R3 候选：金 <2 且卖光冗余后仍 <2（5 档无望且刷新无钱）
            if (snapshot.Gold < 2)
            {
                var sold = await SellRedundantsAsync(window, snapshot, ct);
                snapshot = await SnapshotWithRetryAsync(window, ct) ?? snapshot;
                if (snapshot.Gold < 2 && sold == 0)
                {
                    emit("[决策层] R3：金币耗尽且无可卖——弃局重开。");
                    return PreparationOutcome.Dead;
                }
            }
        }

        return PreparationOutcome.Dead; // 运营轮上限（异常兜底）
    }
}
