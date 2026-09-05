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
    Func<nint, CancellationToken, Task<bool>>? retreatFromBattleView = null,
    Action<string>? requestStreamRevive = null)
{
    private readonly Stopwatch _runClock = Stopwatch.StartNew();

    private GrailUserGoal _goal = GrailUserGoal.Single;

    /// <summary>P-10（1.2.69）：A9 弃局连续失败计数——退避与清场模式判据。</summary>
    private int _abandonStreak;

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
            // M7=8 分钟（1.2.71 行为审计：策略六轮环合法耗时 ≈6 分钟，原 60s 配额会
            // 静默腰斩策略选择→页面未知→弃好局）。
            var timeout = command.Kind switch
            {
                GrailCommandKind.M8 => TimeSpan.FromMinutes(35),
                GrailCommandKind.M1 => TimeSpan.FromMinutes(6),
                GrailCommandKind.M7 => TimeSpan.FromMinutes(8),
                GrailCommandKind.M5 => TimeSpan.FromMinutes(5),
                GrailCommandKind.M3 => TimeSpan.FromMinutes(5),
                GrailCommandKind.M2 => TimeSpan.FromMinutes(3),
                GrailCommandKind.A9 => TimeSpan.FromMinutes(3),
                _ => TimeSpan.FromSeconds(60),
            };
            // P2-2（1.2.71 运行时审计）：显式 try/finally 先 Cancel 再 Dispose——
            // 原 using 在解栈时先拆注册，DECIDE 停止/关窗后在途指令（M8 最长 30 分钟）
            // 收不到取消，变成"僵尸指令"继续操作游戏。
            var orphanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                // P-22（1.2.70）：指令在途心跳——M8=35 分钟/M1=6 分钟级长指令卡死时
                // 日志不再静默，每 30 秒一条心跳（决策层 emit 进 UI/事件文件双写）。
                result = await AwaitWithHeartbeatAsync(
                    commandText,
                    dispatcher.DispatchAsync(command, context, orphanCts.Token)
                        .WaitAsync(timeout, ct),
                    timeout,
                    ct);
            }
            catch (TimeoutException)
            {
                orphanCts.Cancel();
                emit($"[决策层/{_runClock.Elapsed:mm\\:ss}] {commandText} ⇒ 失败：指令执行超过 {timeout.TotalSeconds:F0} 秒未返回（疑似卡死），已取消并跳过。");
                return GrailCommandResult.Fail(command.Kind, $"指令执行超时（{timeout.TotalSeconds:F0} 秒）未返回。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 用户叫停：保持取消语义，由外层正常收尾。
            }
            catch (Exception dispatchError)
            {
                // P1-2（1.2.71 运行时审计）：兜底包装——引擎"一次异常即死"已被定性为
                // 不可用形态（决策层异常一行后永久退出且无自愈）。非取消异常一律降级
                // 为失败事实：引擎存活，问题凭异常类型+消息在日志可见。
                emit($"[决策层/{_runClock.Elapsed:mm\\:ss}] {commandText} ⇒ 失败：指令执行异常（{dispatchError.GetType().Name}）：{dispatchError.Message}");
                return GrailCommandResult.Fail(command.Kind, $"指令执行异常（{dispatchError.GetType().Name}）：{dispatchError.Message}");
            }
            finally
            {
                orphanCts.Cancel();
                orphanCts.Dispose();
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

    /// <summary>
    /// P-22（1.2.70）：在途指令心跳包装——超时仍由外层 WaitAsync 裁决，本方法只在
    /// 每 30 秒发一条 WaitHeartbeat，让 M5/M8/M1 级长指令的"卡死"与"正常长跑"可区分。
    /// </summary>
    private async Task<GrailCommandResult> AwaitWithHeartbeatAsync(
        string commandText,
        Task<GrailCommandResult> awaited,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = timeout - (DateTimeOffset.Now - started);
            if (remaining <= TimeSpan.Zero)
            {
                // D1（1.2.70 审查 FAIL 项）：超时压线拍——WaitAsync 的 TimeoutException
                // 即将/已经裁决，直接 await awaited 抛出。负值给 Task.Delay 会同步抛
                // ArgumentOutOfRangeException（逃过 SendAsync 的两个 catch，把可自愈
                // 超时变成决策层死亡）。
                return await awaited;
            }

            var beatInterval = remaining < TimeSpan.FromSeconds(30)
                ? remaining
                : TimeSpan.FromSeconds(30);
            var completed = await Task.WhenAny(awaited, Task.Delay(beatInterval, ct));
            if (completed == awaited)
            {
                return await awaited;
            }

            emit($"[决策层心跳] {commandText} 在途已 {(DateTimeOffset.Now - started).TotalSeconds:F0}s" +
                 $"（上限 {timeout.TotalSeconds:F0}s）——继续等待。");
        }
    }

    private static string Describe(object? payload) => payload switch
    {
        GrailOpeningFact o => $"{(o.Succeeded ? "命中" : "未成功")}环境={o.MatchedEnvironmentName ?? "—"} {o.Message}",
        GrailShopPassFact s => $"买到={string.Join(",", s.BoughtCharacterNames ?? [])} 金={s.GoldAfter}",
        GrailWishOutcomeFact w => $"应答={w.Responded} 累计={w.WishesResponded}",
        GrailRunSnapshot s => $"羁绊={s.BondMemberCount} 金={s.Gold} 血={s.TeamHealth?.ToString() ?? "?"}",
        GrailPageFact p => $"页面={p.PageId ?? "未知"}",
        GrailSellResult r => $"卖出={r.SoldCount} 金={r.EstimatedGold}",
        // P-14/P-21（1.2.70）：M7 回执此前在此处无分支（打"OK："空尾巴）、M8 未成功
        // 曾打"命中=—"——对齐 CommandTestWindow.FormatPayload 的诚实口径。
        RewardStageAutomationResult s => $"策略={s.Status}：{s.Message}",
        // 1.2.88：M2 的开启数量与 M1 的落地页都是字符串载荷，此前落进 _ 兜底打成
        // "OK："空尾巴（开没开晶矿从回执不可辨）。
        string s => s,
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

    /// <summary>带退避的快照读取：转场/识别冻结期单帧失败是常态（实测教训）。
    /// 1.2.66 冗余审计：前密后疏退避（1,1,2,2,3,5,5,5 秒），总窗口 24s（原 8×5s=40s
    /// 的 60%，交叉复核 F8 澄清：并非"相当"）——换取转场 1-3 秒完成时首次重试即命中，
    /// 识别冻结期的兜底由外层 SnapshotWithRetry 调用方的重试预算承接。</summary>
    private static readonly int[] SnapshotRetryBackoffSeconds = [1, 1, 2, 2, 3, 5, 5, 5];

    private async Task<GrailRunSnapshot?> SnapshotWithRetryAsync(nint window, CancellationToken ct)
    {
        for (var attempt = 0; attempt < SnapshotRetryBackoffSeconds.Length; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(SnapshotRetryBackoffSeconds[attempt - 1]), ct);
            }

            var snapshot = await SnapshotAsync(window, ct);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return null;
    }

    /// <summary>
    /// P1-3（1.2.73 实测根因修复）：识别流追帧长尾——SnapshotWithRetryAsync（8 次退避
    /// ≈24s）全败后，若 M8/导航流刚用实时分类确认过页面（调用方自行判定），识别流的
    /// LatestAnalysis 可能仍在追赶游戏状态（实测进 1-1 后 19s+ 无新分析帧）。本方法以
    /// 5 秒间隔再追 60 秒；管线恢复即自愈，仍失败如实返回 null（调用方走弃局）。
    /// </summary>
    private async Task<GrailRunSnapshot?> SnapshotWithRetrySlowTailAsync(
        nint window, CancellationToken ct)
    {
        // 1.2.88（命中局实锤：长尾 60s < 启发式复活节流 300s → 好局在长尾耗尽时
        // 被判 R3 弃掉，识别流随后才复活）。X3 药方本来就是"冻结→STOP/START 重连"：
        // 进入长尾立即请测试台重启识别会话，不等 300s 节流。重启只影响帧流不影响
        // 本重试循环；委托失败不阻断（最坏=维持旧行为，启发式复活仍兜底）。
        if (requestStreamRevive is not null)
        {
            try
            {
                requestStreamRevive("追帧长尾");
            }
            catch
            {
                // 救援委托异常不阻断长尾重试。
            }
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            // 1.2.88 审查 P3 加固：实机病理为流在重启后可能 ~5 分钟内再死——长尾中段
            // （第 6 次，t≈25-30s，单飞标志已释放）再请求一次；帧陈旧前置由测试台侧把守。
            if (attempt == 5 && requestStreamRevive is not null)
            {
                try
                {
                    requestStreamRevive("追帧长尾中段");
                }
                catch
                {
                    // 救援委托异常不阻断长尾重试。
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var snapshot = await SnapshotAsync(window, ct);
            if (snapshot is not null)
            {
                emit($"[决策层] 识别流追帧成功（第 {attempt + 1} 次长尾重试）——快照恢复可用。");
                return snapshot;
            }
        }

        return null;
    }

    private async Task<GrailPageFact?> PageAsync(nint window, CancellationToken ct)
    {
        var result = await SendAsync("I1", new GrailCommand(GrailCommandKind.I1), window, ct);
        return result.Error is null ? result.Payload as GrailPageFact : null;
    }

    /// <summary>等待祈愿弹框并应答。1.2.66 冗余审计：例行检查（未部署成员的轮次）
    /// 无弹框概率极高，单查一次立即返回；仅部署命杯成员后升档弹框会延迟弹出
    /// （实测），传 maxProbes=4 轮询（间隔 3 秒，窗口 12 秒）。</summary>
    private async Task<bool> AnswerWishIfUpAsync(
        nint window, CancellationToken ct, int maxProbes, int probeIntervalSeconds = 3)
    {
        for (var attempt = 0; attempt < maxProbes; attempt++)
        {
            var page = await PageAsync(window, ct);
            if (page is { WishDialogOpen: true })
            {
                var m3 = await SendAsync("M3", new GrailCommand(GrailCommandKind.M3), window, ct);
                return m3.Error is null;
            }

            if (attempt < maxProbes - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(probeIntervalSeconds), ct);
            }
        }

        return false;
    }

    /// <summary>确认升档祈愿已应答（弹框在屏必答；不在屏按已答/延迟处理）。
    /// maxProbes 缺省 1=例行单查；部署命杯成员后的调用点传 4。</summary>
    private Task EnsureWishAnsweredAsync(nint window, CancellationToken ct, int maxProbes = 1) =>
        AnswerWishIfUpAsync(window, ct, maxProbes);

    /// <summary>M5 回执是否买到非昔涟成员（昔涟只买不上场、不升档）——
    /// 执行器内部上场的成员同样触发升档弹框，外层须补轮询捕获延迟弹出。</summary>
    private static bool BoughtNonXilianMember(GrailCommandResult? shopResult) =>
        shopResult?.Payload is GrailShopPassFact fact &&
        fact.BoughtCharacterNames?.Any(name => !string.Equals(
            name, GrailRunSnapshot.XilianName, StringComparison.Ordinal)) == true;

    /// <summary>弃局（A9）后的收尾等待（1.2.68 由固定 4-5 秒改为有界轮询）：
    /// A9 回执本身已含"回主页确认"，此处轮询 I1 等结算收尾动画——命中主页
    /// 即早退（常在首次查询即命中，省 3-4 秒）；从未命中也只等有界余量。
    /// 页面状态最终由 M8 入口守卫兜底。</summary>
    private async Task SettleAfterAbandonAsync(nint window, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
            }

            var page = await PageAsync(window, ct);
            if (page is { IsStale: false } &&
                (string.Equals(page.PageId, "currency_wars_home", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(page.PageId, "normal_hud", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    /// <summary>剥掉明细名上的 [星徽]/[装备] 后缀，还原纯角色名。</summary>
    private static string PureName(string detailName)
    {
        var open = detailName.IndexOf('[');
        return open > 0 ? detailName[..open] : detailName;
    }

    /// <summary>
    /// 把识别到的、未上场的命杯成员部署到空位（A1 显式前台槽，逐个 I10 复核），
    /// 随后执行学者补位（1.2.64）。返回（是否部署过任何角色, 可用最新快照）——
    /// 1.2.68：LatestSnapshot 供 S7 终局判定/卖人直接使用（部署后盘面已变，
    /// 旧快照会让收工判定晚一轮、多跑一整轮 M5）；不可用时为 null（调用方沿用原快照）。
    /// 1.2.66 冗余审计：①existingSnapshot 非空且调用方快照后无任何操作时首轮复用，
    /// 消除背靠背双 I10；②候选穷尽/前台满改 break 而非 return——修复学者补位段
    /// 不可达（原逻辑只有连续部署满 3 名 bond 成员才会走到，补位功能形同虚设）。
    /// </summary>
    private async Task<(bool DeployedAny, GrailRunSnapshot? LatestSnapshot)> DeployBondMembersAsync(
        nint window, GrailRunSnapshot? existingSnapshot, CancellationToken ct)
    {
        var deployedAny = false;
        var snapshot = existingSnapshot ?? await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null && existingSnapshot is null)
        {
            // P1-4（1.2.75 实测根因修复）：S2 现读场景下识别流滞后（实测 19s+ 无新帧）
            // 会让部署段放弃→前置门判 Dead→买到的成员坐备战席被整局弃掉（03:37 命中局
            // 远坂凛实锤）。追帧后重试：管线恢复即完成部署。
            emit("[决策层] 部署段快照全败——识别流滞后，进入 60s 追帧后重试部署。");
            snapshot = await SnapshotWithRetrySlowTailAsync(window, ct);
        }
        // 快照是否仍代表当前盘面：部署后重读=true；部署指令失败后=false（盘面可能已变）。
        var snapshotFresh = snapshot is not null;
        for (var pass = 0; pass < 3 && snapshot is not null; pass++)
        {
            var bondNames = executor.GrailBondMemberNames;
            // R67（1.2.86 取证修复）：部署候选排除纯 5 费——067「英雄登场」赠体
            // （2星5费，19 节点内锁定不可上场，游戏拒绝拿起=拖拽源变化恒 0.0）恰好是
            // 命运圣杯成员，会被当可部署候选反复拖拽（1.2.63 实测 5 连败跨 3 周期）。
            // 命杯成员中仅 Archer 为纯 5 费，排除不影响凛/闪/Saber。
            string? pendingBench = null;
            foreach (var candidateName in snapshot.BenchCharacterDetails
                         .Select(item => PureName(item.Split(':')[^1])))
            {
                if (!bondNames.Contains(candidateName))
                {
                    continue;
                }

                var candidate = ResolveProtectedCharacter(candidateName);
                if (candidate is not null && GrailOperationExecutor.IsPureFiveCostCharacter(candidate))
                {
                    emit($"[决策层] 部署候选「{candidateName}」为纯5费（067 赠体锁定不可上场）——排除出部署候选。");
                    continue;
                }

                pendingBench = candidateName;
                break;
            }

            if (pendingBench is null || snapshot.OccupiedFrontSlots.Count >= 4)
            {
                break; // 无可部署或前台满（后台部署归 N17a 之后流程）——仍继续学者补位
            }

            var slot = Enumerable.Range(0, 4).FirstOrDefault(i => !snapshot.OccupiedFrontSlots.Contains(i));
            var deploy = await SendAsync(
                $"A1 {pendingBench} 前台 {slot + 1}",
                new GrailCommand(GrailCommandKind.A1,
                    new GrailDeployArgs(pendingBench, PreparationLane.Front, slot)),
                window, ct);
            if (deploy.Error is not null)
            {
                snapshotFresh = false;
                break; // 识别不到该名（识别缺陷）→ 交外层对账，绝不盲拖
            }

            deployedAny = true;
            await EnsureWishAnsweredAsync(window, ct, maxProbes: 4);
            snapshot = await SnapshotWithRetryAsync(window, ct); // 部署后重读找下一个候选
            snapshotFresh = snapshot is not null;
        }

        // 1.2.63（用户令去冗余+独立分析 P-15 关联）：学者补位——bond 候选部署完毕后，
        // 前台仍有空槽且备战席存在银河学者（凑 2 学者羁绊）时补位上场。
        // 艾丝妲滞留备战席案（19:47 局：M5 买学者先上 F1/F2，命杯互换把学者顶回备战席，
        // 引擎此前的 bond-only 部署不再看她）。
        if (!snapshotFresh)
        {
            snapshot = await SnapshotWithRetryAsync(window, ct);
            snapshotFresh = snapshot is not null;
        }

        if (snapshot is null || snapshot.OccupiedFrontSlots.Count >= 4)
        {
            return (deployedAny, snapshotFresh ? snapshot : null);
        }

        var scholarNames = new HashSet<string>(
            gameData.CurrencyWarsCharacters
                .Where(character => character.BondNames.Any(
                    bond => bond is not null && bond.Contains("银河学者", StringComparison.Ordinal)))
                .Select(character => character.Name),
            StringComparer.OrdinalIgnoreCase);
        var benchScholar = snapshot.BenchCharacterDetails
            .Select(item => PureName(item.Split(':')[^1]))
            .FirstOrDefault(name => scholarNames.Contains(name));
        if (benchScholar is null)
        {
            return (deployedAny, snapshotFresh ? snapshot : null);
        }

        var benchHead = snapshot.BenchCharacterDetails
            .First(detail => scholarNames.Contains(PureName(detail.Split(':')[^1])))
            .Split(':')[0];
        if (!int.TryParse(benchHead, out var scholarBenchSlot) || scholarBenchSlot < 0)
        {
            return (deployedAny, snapshotFresh ? snapshot : null);
        }

        var scholarSlot = Enumerable.Range(0, 4).FirstOrDefault(
            i => !snapshot.OccupiedFrontSlots.Contains(i));
        var scholarDeploy = await SendAsync(
            $"A1 {benchScholar} 前台 {scholarSlot + 1}",
            new GrailCommand(GrailCommandKind.A1,
                new GrailDeployArgs(benchScholar, PreparationLane.Front, scholarSlot)),
            window, ct);
        if (scholarDeploy.Error is null)
        {
            emit($"[决策层] 学者补位：{benchScholar} 已部署到前台 {scholarSlot + 1} 号位。");
            deployedAny = true;
            // 学者非圣杯成员，上场不触发圣杯升档弹框——单查即可。
            await EnsureWishAnsweredAsync(window, ct);
            // 1.2.68：学者上场改变盘面占位——重读一次供快照带出（低频，每局 0-2 次）。
            // 重读失败=无法证明快照新鲜（学者已上场，旧快照占位失真）——按契约置
            // snapshotFresh=false 返回 null，调用方沿用自身快照兜底。
            var scholarReread = await SnapshotWithRetryAsync(window, ct);
            if (scholarReread is not null)
            {
                snapshot = scholarReread;
            }
            else
            {
                snapshotFresh = false;
            }
        }

        return (deployedAny, snapshotFresh ? snapshot : null);
    }

    /// <summary>
    /// Unknown 页处置（审查修正版）：仅当页面真正 Unknown（识别表外阻塞态，如位面图/
    /// 积分弹窗变体）时尝试世界内交互键；已知页绝不盲点（坑39/审查 P1：(960,540) 会误选
    /// 投资环境卡、(1290,615) 落在备战页角色卡）。
    /// 1.2.71 行为审计：删除 (960,720) 中心盲点——战斗过场帧被降级 Unknown 时它就是
    /// 战场中心点击（"异常举动"最短路径）；陈旧帧的 Unknown 不是现状，同样跳过。
    /// </summary>
    private async Task DismissUnknownPageAsync(nint window, CancellationToken ct)
    {
        var page = await PageAsync(window, ct);
        if (page is null ||
            !string.IsNullOrWhiteSpace(page.PageId) ||
            page.IsStale)
        {
            return; // 有页面 ID=非未知态交对应流程；陈旧帧的"Unknown"=无现状绝不操作
        }

        if (pressInteractKey is not null)
        {
            await pressInteractKey(window, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>A4：仅当非 067 局且物品栏有未携带星徽（I7）时装配到 1 号位角色。
    /// 返回是否实际装配（供 M1 前置门的部署动画等待判定——A4 拖拽同样产生动画期）。</summary>
    private async Task<bool> AssembleBadgeIfAvailableAsync(
        nint window, CancellationToken ct, bool is067Run)
    {
        if (is067Run)
        {
            return false; // 067 局禁发 A4
        }

        var i7 = await SendAsync("I7", new GrailCommand(GrailCommandKind.I7), window, ct);
        if (i7.Payload is GrailBadgeFact badge && badge.Uncarried > 0)
        {
            var a4 = await SendAsync("A4 前台 1",
                new GrailCommand(GrailCommandKind.A4,
                    new GrailPositionArgs(PreparationLane.Front, 0)), window, ct);
            return a4.Error is null;
        }

        return false;
    }

    /// <summary>
    /// S1A/S1B：卖光冗余凑金币。保护名单三重校验（审查 P1 修复）：
    /// ①官方数据 bond/纯 5 费；②明细 [星徽] 标记（识别∪账本并集产物）；
    /// ③数据查不到的名字（识别误名）绝不卖——宁少卖不误卖。
    /// 保留线封顶=快照 SellableBeyondKeepLineCount；AnomalyNotes 含同名多处=拒采整轮。
    /// 1.2.58（防错③④/坑38/X5）：备战席与场上循环统一三重校验（备战席此前漏保护，
    /// 命杯/5费/星徽在备战席同样会被卖）；每卖一条→I10 重读复核（该槽已空/该名消失）
    /// →反证即停；逐卖重读快照，禁止按卖出前槽位表连发。
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

        // 1.2.58：逐卖重读循环——每轮从最新快照选一条可卖（备战席优先、场上次之），
        // 卖出后立即 I10 复核；复核反证（目标槽未空/名字未消失/快照拿不到）立即停手。
        while (sold < cap && !ct.IsCancellationRequested)
        {
            var target = SelectNextSellableTarget(snapshot);
            if (target is null)
            {
                break;
            }

            var sell = target.Kind == SellTargetKind.Bench
                ? await SendAsync($"A3 {target.SlotNumber + 1}",
                    new GrailCommand(GrailCommandKind.A3,
                        new GrailBenchSlotArgs(target.SlotNumber)), window, ct)
                : await SendAsync($"A2 {(target.IsBack ? "后台" : "前台")} {target.SlotNumber}",
                    new GrailCommand(GrailCommandKind.A2,
                        new GrailPositionArgs(
                            target.IsBack ? PreparationLane.Back : PreparationLane.Front,
                            target.SlotNumber - 1)),
                    window, ct);
            if (sell.Error is not null)
            {
                emit($"[决策层] 卖出「{target.Name}」回执失败：{sell.Error}——反证即停。");
                break;
            }

            sold++;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            // 防错④：卖出后 I10 复核=硬性收尾。复核失败=反证，立即停手交对账。
            var verify = await SnapshotWithRetryAsync(window, ct);
            if (verify is null)
            {
                emit("[决策层] 卖出后 I10 复核拿不到快照——反证即停，交对账。");
                break;
            }

            if (!VerifySaleApplied(verify, target))
            {
                emit($"[决策层] 卖出「{target.Name}」复核未通过（{target.Kind} " +
                     $"{target.SlotNumber} 号位状态与预期不符）——反证即停，交对账。");
                break;
            }

            snapshot = verify;
        }

        return sold;
    }

    /// <summary>
    /// 1.2.58（架构审查 2-3）：M1 前置门——检查最新快照前台（F 槽）是否有已部署
    /// 角色。需要新鲜盘面时先 I10 重读；拿不到快照或前台无人=返回 null（调用方判 Dead）。
    /// justActed=本轮刚部署/A4 装配过（1.2.66：仅此时先等 3 秒——坑 43 判据不变；
    /// 什么都没操作的轮次直接查，查不到仍走"等 3 秒重读"动画路径兜底）。
    /// </summary>
    private async Task<GrailRunSnapshot?> EnsureFrontHasUnitAsync(
        nint window, GrailRunSnapshot snapshot, bool justActed, CancellationToken ct)
    {
        // 1.2.61（实机 19:17 局复盘）：部署成功后立即 I10 会撞上部署动画+识别滞后
        // （蓝图 X13：部署回执后等 3-5 秒再核对）——刚操作过时首查前等 3 秒，
        // 未找到再等 3 秒重读一次；两次都空才判 Dead。此前零等待曾把部署成功的好局误杀。
        if (justActed)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        for (var readAttempt = 1; readAttempt <= 2; readAttempt++)
        {
            var fresh = await SnapshotWithRetryAsync(window, ct) ?? snapshot;
            var hasFront = fresh.DeployedCharacterDetails.Any(detail =>
                detail.StartsWith('F') || detail.StartsWith("F:"));
            if (hasFront)
            {
                return fresh;
            }

            if (readAttempt == 1)
            {
                emit("[决策层] 前台未读到角色——可能为部署动画/识别滞后，3 秒后重读。");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }

        if (justActed)
        {
            // P1-5（1.2.76→1.2.79 两轮实机迭代）：A1 部署回执 OK（含像素差验证——
            // 拖拽引起目标槽画面大幅变化才回 OK）vs I10 前台空的矛盾=识别流滞后。
            // 实测两连：04:08/04:56 两个命中局黑塔 A1 OK 后前置门 4 次 I10 跨 18s
            // 前台空。追帧终判后仍空时**放行出战**：误放行后果=出战弹窗→M1 失败→
            // A9 自愈（约 1 分钟，可逆）；误弃局后果=好局不可逆损失。不对称风险取放行。
            emit("[决策层] 两次重读前台均无角色但刚部署过（A1 像素验证 OK）——追帧后放行出战，如出战受阻由 M1 失败+A9 自愈。");
            var tail = await SnapshotWithRetrySlowTailAsync(window, ct);
            return tail ?? await SnapshotWithRetryAsync(window, ct) ?? snapshot;
        }

        emit("[决策层] 两次重读前台均无角色——禁止出战（防『前台区域无角色』弹窗）。判 Dead 弃局重开。");
        return null;
    }


    private sealed record SellTarget(
        SellTargetKind Kind,
        int SlotNumber,
        bool IsBack,
        string Name,
        string Detail);

    private enum SellTargetKind
    {
        Bench,
        Field
    }

    /// <summary>从快照选下一条可卖目标：备战席优先（A3），场上次之（A2 前台/后台）。</summary>
    private SellTarget? SelectNextSellableTarget(GrailRunSnapshot snapshot)
    {
        foreach (var detail in snapshot.BenchCharacterDetails)
        {
            if (TryParseSellable(detail, bench: true, out var benchTarget))
            {
                return benchTarget;
            }
        }

        foreach (var detail in snapshot.DeployedCharacterDetails)
        {
            if (TryParseSellable(detail, bench: false, out var fieldTarget))
            {
                return fieldTarget;
            }
        }

        return null;
    }

    private bool TryParseSellable(string detail, bool bench, out SellTarget? target)
    {
        target = null;
        var headSeparator = detail.IndexOf(':');
        if (headSeparator <= 0)
        {
            return false;
        }

        var head = detail[..headSeparator];
        var name = PureName(detail[(headSeparator + 1)..]);
        var character = ResolveProtectedCharacter(name);
        if (character is null)
        {
            emit($"[决策层] 卖人跳过「{name}」：数据查不到该名（识别误名保护）。");
            return false;
        }

        var isFive = GrailOperationExecutor.IsPureFiveCostCharacter(character);
        var isBond = character.BondNames.Any(b =>
            b is not null && b.Contains("命运圣杯", StringComparison.Ordinal));
        var isCarrier = detail.Contains("[星徽]", StringComparison.Ordinal);
        if (isBond || isFive || isCarrier)
        {
            emit($"[决策层] 卖人跳过「{name}」：命杯/纯5费/星徽携带者保护" +
                 (bench ? "（1.2.58：备战席同样受保护）。" : "。"));
            return false;
        }

        if (bench)
        {
            // I10 备战席明细 head=0 基槽号（0~5）；A3 指令同样吃 0 基。
            if (!int.TryParse(head, out var benchSlot) || benchSlot < 0)
            {
                return false;
            }

            target = new SellTarget(
                SellTargetKind.Bench, benchSlot, IsBack: false, name, detail);
            return true;
        }

        var isBack = head.StartsWith('B');
        if ((!head.StartsWith('F') && !isBack) ||
            !int.TryParse(head.AsSpan(1), out var slotNumber) ||
            slotNumber <= 0)
        {
            return false;
        }

        target = new SellTarget(
            SellTargetKind.Field, slotNumber, isBack, name, detail);
        return true;
    }

    /// <summary>卖出复核：目标槽位的最新明细里不再出现同名，且槽位头仍然合法。</summary>
    private static bool VerifySaleApplied(GrailRunSnapshot verify, SellTarget target)
    {
        IEnumerable<string> details = target.Kind == SellTargetKind.Bench
            ? verify.BenchCharacterDetails
            : verify.DeployedCharacterDetails;
        var slotHead = target.Kind == SellTargetKind.Bench
            ? target.SlotNumber.ToString()
            : (target.IsBack ? "B" : "F") + target.SlotNumber;
        foreach (var detail in details)
        {
            if (detail.StartsWith(slotHead + ":", StringComparison.Ordinal) &&
                detail.Contains(target.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private CurrencyWarsCharacterData? ResolveProtectedCharacter(string name) =>
        gameData.CurrencyWarsCharacters.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>P-10 补全（1.2.71 行为审计）：对任意 A9 弃局尝试记账——失败累计
    /// _abandonStreak（与入口清场模式共享计数），达到 5 次高声停机等人工介入；
    /// 成功清零。返回 true=已达停机终态，调用方应立即 return。</summary>
    private async Task<bool> RecordAbandonOutcomeAndCheckShutdownAsync(
        GrailCommandResult abandon, nint window, CancellationToken ct)
    {
        if (abandon.Error is null)
        {
            _abandonStreak = 0;
            return false;
        }

        _abandonStreak++;
        if (_abandonStreak < 5)
        {
            return false;
        }

        emit($"[决策层] !! 连续 {_abandonStreak} 次弃局未成功——疑似存在无法自动解除的阻塞（未识别弹窗/识别失效）。引擎停机等待人工介入：请查看游戏画面手动恢复后重新下发 DECIDE。");
        await SnapshotWithRetryAsync(window, ct);
        return true;
    }

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
            // 1.2.66（独立效率审计 TOP1）：入口判定改 I1 轻量判页——原用 I10 全量快照
            // "有结果"当"在局内"判据，而 I10 有备战页门禁，主界面/弃局落点上 8 次退避
            // 全败=每局边界白等 ~19 秒。preparation_ 前缀才需要弃局（语义更准）；
            // 其余页面直接进 M8（M8 自带续局守卫兜底，GrailMacroCommands 入口守卫）。
            // IsStale 检查（增量复查项3）：识别流冻结时 I1 仍报旧页（实测冻结 6 分钟），
            // 陈旧读数=无现状，绝不据此发 A9——放行进 M8（其守卫自带 10s 新鲜窗）。
            var entryPage = await PageAsync(window, ct);
            // 1.2.83 断线自愈：游戏与服务器断开时弹"请重新登录"模态（1.2.83 入识别表）
            // ——点确认关闭弹窗后高声停机，等人工重新登录（断线态任何操作都无意义）。
            if (entryPage is { IsStale: false, PageId: "disconnect_prompt" })
            {
                emit("[决策层] !! 检测到游戏与服务器断开连接（请重新登录弹窗）——关闭弹窗后停机。请重新登录游戏并回到货币战争主界面，再重新下发 DECIDE。");
                if (genericClick is not null)
                {
                    // 确认按钮与"前台区域无角色"确认点同位（模态按钮标准位 960,699@1920）。
                    await genericClick(window, 960, 699, ct);
                }

                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                await SnapshotWithRetryAsync(window, ct); // 留最终对账快照
                return;
            }

            if (entryPage is { IsStale: false, PageId: not null } &&
                entryPage.PageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase))
            {
                emit("[决策层] 检测到备战页已有对局（节点歧义）——先对账局面留痕，再弃局重开。");
                // P-09（1.2.69）：继承局对账留痕——弃局决策不变（用户拍板：未知节点
                // 绝不操作），但局面先落日志供复盘（1-3 胜局曾被当继承局连弃 2 个）。
                var inheritedSnapshot = await SnapshotWithRetryAsync(window, ct);
                if (inheritedSnapshot is not null)
                {
                    emit("[决策层] 继承局对账：羁绊=" + inheritedSnapshot.BondMemberCount +
                         " 金=" + inheritedSnapshot.Gold +
                         " 血=" + (inheritedSnapshot.TeamHealth?.ToString() ?? "?") +
                         " 上场=" + inheritedSnapshot.DeployedCharacterDetails.Count +
                         " 备战=" + inheritedSnapshot.BenchCharacterDetails.Count + "。");
                }

                // P1-1（1.2.71 运行时审计）：A9 在备战页会被屏上模态（典型=祈愿升档框）
                // 确定性挡死（恢复例程禁止对备战页兜底点击）——弃局前先应答在屏弹框，
                // 否则清场模式会变成"退避→A9→再退避"的永久驻留。
                if (entryPage.WishDialogOpen)
                {
                    emit("[决策层] 祈愿弹框在屏——先应答再弃局。");
                    await EnsureWishAnsweredAsync(window, ct, maxProbes: 2);
                }
                else
                {
                    await EnsureWishAnsweredAsync(window, ct);
                }

                // P-10（1.2.69）：弃局受阻退避（清场模式）——A9 连续失败时疑似弃局
                // 机制受损，拉长退避且绝不进入 M8 导航段，直到弃局成功或用户叫停。
                // P1-1（1.2.71）：连续 5 次仍失败=存在无法自动解除的阻塞——高声停机
                // 等人工介入，绝不无限空转。
                _abandonStreak++;
                if (_abandonStreak >= 5)
                {
                    emit($"[决策层] !! 连续 {_abandonStreak} 次弃局未成功（页面={entryPage.PageId}）——疑似存在无法自动解除的阻塞（未识别弹窗/识别失效）。引擎停机等待人工介入：请查看游戏画面手动恢复后重新下发 DECIDE。");
                    await SnapshotWithRetryAsync(window, ct); // 留一份最终对账快照（I10 落日志）
                    return;
                }

                if (_abandonStreak > 1)
                {
                    var backoff = TimeSpan.FromSeconds(Math.Min(10 * _abandonStreak, 30));
                    emit($"[决策层] 弃局后仍滞留备战页（连续 {_abandonStreak} 次）——疑似弃局受阻，退避 {backoff.TotalSeconds:F0} 秒后重试；期间不发任何导航/操作指令。");
                    await Task.Delay(backoff, ct);
                }

                var abandon = await SendAsync("A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                if (abandon.Error is null)
                {
                    await SettleAfterAbandonAsync(window, ct);
                    // P1-1（1.2.71）：名义成功≠页面离开——复核后仍备战页则保留 streak
                    //（下一轮退避升级），防"A9 回执 OK 但实际没弃掉"的每分钟空转。
                    var afterAbandon = await PageAsync(window, ct);
                    if (afterAbandon is { IsStale: false, PageId: not null } &&
                        afterAbandon.PageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase))
                    {
                        emit("[决策层] A9 回执成功但页面仍在备战页——按失败计，下轮退避重试。");
                    }
                    else
                    {
                        _abandonStreak = 0;
                    }
                }
                else
                {
                    emit("[决策层] A9 弃局失败——清场模式：重试弃局成功前不发 M8。");
                }

                // 弃局成功与否由下一轮入口判页决定——离开备战页才放行 M8。
                continue;
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
                    _abandonStreak = 0; // F1（1.2.70 交叉复核）：M8 到达=弃局链路健康，清零退避计数
                    hit067 = string.Equals(fact.MatchedEnvironmentName, "英雄登场", StringComparison.Ordinal);
                }
                else if (m8.Error is not null)
                {
                    // 守卫拦截/导航失败：先解除 Unknown 阻塞页（仅页面未知时），
                    // 再 A9 清场重发。已知页（备战/商店/战斗）不盲点。
                    await DismissUnknownPageAsync(window, ct);
                    // 坑41 规避③：A9 前判页——主界面（normal_hud/currency_wars_home）
                    // 无局可弃（A9 必失败），跳过 A9 直接重试 M8。
                    var pageBeforeA9 = await PageAsync(window, ct);
                    var pageBeforeId = pageBeforeA9?.PageId;
                    if (string.Equals(pageBeforeId, "normal_hud", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(pageBeforeId, "currency_wars_home", StringComparison.OrdinalIgnoreCase))
                    {
                        emit("[决策层] 页面=主界面（无局可弃）——跳过 A9 直接重试 M8。");
                        continue;
                    }

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
                    var abandon = await SendAsync("A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                    await SettleAfterAbandonAsync(window, ct);
                    // P-10 补全（1.2.71）：M8 失败分支的 A9 同样纳入清场计数——原实现
                    // 只封了入口路径，弃局受损+Unknown 页时此处会无限空转。
                    if (await RecordAbandonOutcomeAndCheckShutdownAsync(abandon, window, ct))
                    {
                        return;
                    }
                }
                else
                {
                    // F2（1.2.70 交叉复核）：原"已到达首次备战页面"分支是死代码——1.2.67 起
                    // 协调器只以 Succeeded=true 报到达，该消息模式不再出现。
                    // M8 重试间隔 2s（1.2.68）：M8 内部导航/守卫自带节奏，长间隔纯空转。
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }

            if (!arrived)
            {
                emit("[决策层] M8 三次尝试未到达备战席——A9 后重开外层循环。");
                var abandon = await SendAsync("A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                await SettleAfterAbandonAsync(window, ct);
                if (await RecordAbandonOutcomeAndCheckShutdownAsync(abandon, window, ct))
                {
                    return;
                }

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
                await SettleAfterAbandonAsync(window, ct);
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
        // P1-3（1.2.73 实测根因修复）：M8 刚用导航器实时分类确认到达备战页，但识别流
        // LatestAnalysis 滞后（实测进 1-1 后 19s+ 无新分析帧）→ I10 门禁连续全败 →
        // 好局被判 Interrupted 弃掉（02:18 命中局实锤）。加追帧长尾：识别管线恢复即自愈。
        var snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            emit("[决策层] 快照 24s 窗口全败——M8 刚确认到达备战页，判定为识别流滞后，进入 60s 追帧长尾。");
            snapshot = await SnapshotWithRetrySlowTailAsync(window, ct);
        }

        if (snapshot is null)
        {
            return PreparationOutcome.Interrupted;
        }

        var m5Result = await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        // 1.2.66：M5 可能买了角色（盘面已变）→ 部署段须现读快照（传 null）。
        var (deployed, _) = await DeployBondMembersAsync(window, null, ct);
        var assembled = await AssembleBadgeIfAvailableAsync(window, ct, hit067);
        // 买到的成员由执行器内部上场且部署段未再部署时，无任何轮询覆盖延迟弹出
        // 的升档框——此时外层补轮询；其余轮次单查。
        await EnsureWishAnsweredAsync(window, ct,
            maxProbes: BoughtNonXilianMember(m5Result) && !deployed ? 4 : 1);
        // P2-1（1.2.71 运行时审计）：justActed 纳入"执行器内部上场"——M5 买到成员的
        // 上场动画同样会让立即 I10 读到空前台（19:17 误弃好局同款），前置门按动画期处理。
        if (await EnsureFrontHasUnitAsync(
                window, snapshot,
                deployed || assembled || BoughtNonXilianMember(m5Result), ct) is null)
        {
            // 1.2.58（架构审查 2-3）：M1 前置门——场上无人时出战必被"前台区域
            // 无角色"弹窗拦下，自 heal 循环烧时间。空场局判 Dead 走弃局重开。
            return PreparationOutcome.Dead;
        }

        var m1 = await SendAsync("M1 preparation_generic reward_shop",
            new GrailCommand(GrailCommandKind.M1,
                new GrailBattleArgs("preparation_generic", "reward_shop")), window, ct);
        if (m1.Error is not null)
        {
            return PreparationOutcome.Dead; // 战斗未推进：外层弃局重开
        }

        // ---- S3：1-2（人口 3，进场先商店）----
        m5Result = await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            // P1-3 补全（1.2.74 实测）：02:40 命中局在 1-2 进场后 I10 连续 8 次全败
            //（M5 买到黑塔+商店/备战切换的识别流滞后）→ 好局又被 Interrupted 弃掉。
            // 与 S2 同款追帧长尾。
            emit("[决策层] S3 快照 24s 窗口全败——判定为识别流滞后，进入 60s 追帧长尾。");
            snapshot = await SnapshotWithRetrySlowTailAsync(window, ct);
        }

        if (snapshot is null)
        {
            return PreparationOutcome.Interrupted;
        }

        // 1.2.66：此快照后无任何操作 → 复用给部署段（省一次背靠背 I10）。
        (deployed, _) = await DeployBondMembersAsync(window, snapshot, ct);
        assembled = await AssembleBadgeIfAvailableAsync(window, ct, hit067); // 1-2 新得徽补装（审查 P3）
        await EnsureWishAnsweredAsync(window, ct,
            maxProbes: BoughtNonXilianMember(m5Result) && !deployed ? 4 : 1);
        // P2-1（1.2.71 运行时审计）：justActed 纳入"执行器内部上场"——M5 买到成员的
        // 上场动画同样会让立即 I10 读到空前台（19:17 误弃好局同款），前置门按动画期处理。
        if (await EnsureFrontHasUnitAsync(
                window, snapshot,
                deployed || assembled || BoughtNonXilianMember(m5Result), ct) is null)
        {
            return PreparationOutcome.Dead;
        }

        m1 = await SendAsync("M1 preparation_generic investment_strategy",
            new GrailCommand(GrailCommandKind.M1,
                new GrailBattleArgs("preparation_generic", "investment_strategy")), window, ct);
        if (m1.Error is not null)
        {
            return PreparationOutcome.Dead; // 战斗未推进：外层弃局重开
        }

        // ---- S4：投资策略（禁选阿哈大悦已内置于 M7）----
        await SendAsync("M7", new GrailCommand(GrailCommandKind.M7), window, ct);
        // 1.2.66：M7 回执已含"验离页"（选中+确认+离页验证），500ms 页面稳定余量足够
        //（原固定 2 秒无验证判据支撑）。
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
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
                // P1-3 补全（1.2.74 实测）：S4 与 S2/S3 同款识别流滞后风险——追帧长尾。
                emit("[决策层] S4 快照两次窗口全败——判定为识别流滞后，进入 60s 追帧长尾。");
                snapshot = await SnapshotWithRetrySlowTailAsync(window, ct);
                if (snapshot is null)
                {
                    return PreparationOutcome.Interrupted;
                }
            }
        }

        if (snapshot.Gold < 8)
        {
            await SellRedundantsAsync(window, snapshot, ct);
        }

        // ---- S5/S7：运营循环 ----
        for (var opsRound = 0; opsRound < 30 && !ct.IsCancellationRequested; opsRound++)
        {
            var shopResult = await SendAsync("M5 圣杯",
                new GrailCommand(GrailCommandKind.M5,
                    new GrailShopPassArgs(GrailLoopMode: true)), window, ct);
            snapshot = await SnapshotWithRetryAsync(window, ct);
            if (snapshot is null)
            {
                // 1.2.58：M5 失败（收店失败等）不再直接弃局——4 命杯好局曾因此被
                // 连带放弃（14:4x 局实况）。先重试一轮 M5 让商店状态自恢复，
                // 快照仍失败才判 Interrupted。
                if (shopResult.Error is not null)
                {
                    emit("[决策层] M5 失败（" + shopResult.Error + "）——重试一轮再判定。");
                    shopResult = await SendAsync("M5 圣杯",
                        new GrailCommand(GrailCommandKind.M5,
                            new GrailShopPassArgs(GrailLoopMode: true)), window, ct);
                }

                snapshot = await SnapshotWithRetryAsync(window, ct);
                if (snapshot is null)
                {
                    // 1.2.63（实机 19:49 局）：快照失败的最常见原因=M5 收店失败后
                    // 面板仍开着（reward_shop 不在 I10 门禁的备战族内，死锁）。
                    // 弃局前先点一次收店开关 (1620,975)@1920 解除面板，再最后重读。
                    emit("[决策层] 快照仍失败——尝试收起商店面板后做最后一次快照。");
                    var pageBeforeRescue = await PageAsync(window, ct);
                    if (pageBeforeRescue?.PageId is "reward_shop")
                    {
                        // 1.2.64（补审 P1-1）：只有证实面板还开着（reward_shop）才点
                        // 收店开关——页面身份未验证时 (1620,975) 是盲点（铁律：新增
                        // 兜底点击必须页面身份分流）。
                        if (genericClick is null)
                        {
                            emit("[决策层] 未注入通用点击能力——无法收店，放弃最后重试。");
                        }
                        else if (await genericClick(window, 1620, 975, ct))
                        {
                            emit("[决策层] 已发送收起商店点击。");
                        }

                        snapshot = await SnapshotWithRetryAsync(window, ct);
                    }

                    if (snapshot is null)
                    {
                        return PreparationOutcome.Interrupted;
                    }
                }
            }

            // 1.2.66：复用上方快照（收店救援分支若发生点击，快照已在救援内重读更新）；
            // 买到非昔涟成员且部署段未部署时外层补轮询（4×3s）捕获延迟弹出的升档框，
            // 其余轮次单查——原无差别 6×3s 轮询是每轮 ~20 秒纯等待的主源。
            // 1.2.68：部署段带回的最新快照直接覆盖 snapshot——S7 终局判定/卖人立即
            // 反映部署结果，不再晚一轮（命中收工时曾多跑一整轮 M5）。
            var (deployedInLoop, latestFromDeploy) = await DeployBondMembersAsync(window, snapshot, ct);
            snapshot = latestFromDeploy ?? snapshot;
            await EnsureWishAnsweredAsync(window, ct,
                maxProbes: BoughtNonXilianMember(shopResult) && !deployedInLoop ? 4 : 1);

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
