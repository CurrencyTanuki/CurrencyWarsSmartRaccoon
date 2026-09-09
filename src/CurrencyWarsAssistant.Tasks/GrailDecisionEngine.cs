using System.Diagnostics;
using CurrencyWarsAssistant.Advisor;
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
    Action<string>? requestStreamRevive = null,
    Func<bool>? isStreamStale = null,
    IModalGuard? modalGuard = null,
    Action<string, string>? publishEvent = null)
{
    private readonly Stopwatch _runClock = Stopwatch.StartNew();

    private GrailUserGoal _goal = GrailUserGoal.Single;
    // 1.2.119（审计簇 E）：最近一次弃局的真实原因——外层弃局回执/事件用它，
    // 消灭"快照持续不可得"被混标为"R3/失败"的标签污染（审计 10:35 局等）。
    private string? _lastAbandonReason;
    // 1.2.119（审计簇 D1）：S3 段最后快照的血量缓存——四.13a 判定数据源（M7 时刻
    // 无 I10 可读）。每次快照可用时刷新。
    private int? _lastKnownTeamHealth;

    /// <summary>P-10（1.2.69）：A9 弃局连续失败计数——退避与清场模式判据。</summary>
    private int _abandonStreak;

    /// <summary>决策层内部统计（汇报用）。</summary>
    public int RunsCompleted { get; private set; }
    public int RunsAbandoned { get; private set; }

    /// <summary>1.2.89 盘面变异时刻：M5 购买（执行器内部自动上场）/A1 部署成功即刷新。
    /// 部署段选槽前据此判定快照是否早于盘面变化（陈旧占用表=拖到已占槽互换，16:57 实锤）。</summary>
    private DateTimeOffset _lastBoardMutationAt = DateTimeOffset.MinValue;

    /// <summary>当前节点锚点（1.2.89）：M8 到达=1-1；M1 落地 reward_shop=1-2、
    /// investment_strategy=1-3。银河学者规则按节点生效（用户令：仅 1-1 且凑 2 才上、
    /// 1-3 必下必卖）。</summary>
    private string _currentNode = "1-1";

    /// <summary>
    /// 1.2.90 上场台账（用户令状态机，X11 星徽同款"识别∪账本并集"模式）：
    /// 名字→前台槽位（0 基真实槽位，无 -1 哨兵值）。
    /// 写入点：A1 像素验证成功（=权威）；M5 买到且执行器自动上场成功（经回执
    /// DeployedFrontSlots 回带真实前台落槽；上场失败/后台兜底上场/昔涟不写）。
    /// 删除点：M8 新局清空、A2/A3 卖出复核通过后。
    /// 消费点：槽位选择取"账本∪识别占用"并集（识别漏读已上场单位时账本兜底，
    /// 20:41 实锤：A1 像素验证 OK 后 I10 连续读前台空，三连同一槽位互换两人）；
    /// 前置门 hasFront 判定含账本（防识别漏读误判 Dead）；bond/学者/填段的
    /// 候选若已在账本=已上场，跳过不再部署。
    /// 识别永不推翻账本（用户口径：上场了就是有人，不能因为识别没读到就当没人）。
    /// </summary>
    private readonly Dictionary<string, int> _frontLedger = new(StringComparer.Ordinal);

    /// <summary>
    /// P1-3（09-08 通宵：S2/S3 M1 两败 ×5 实锤）：识别流停滞期禁硬启战斗——战斗
    /// 状态机依赖帧流观察页面推进，停滞时启动必败。检测到 stale → 请求复活并等
    /// 至多 60 秒；超时照常尝试（失败走既有弃局路径，不另开失败语义）。
    /// </summary>
    private async Task EnsureStreamReadyForBattleAsync(CancellationToken ct)
    {
        if (isStreamStale is null || requestStreamRevive is null)
        {
            return;
        }

        var stale = false;
        try
        {
            stale = isStreamStale();
        }
        catch
        {
            return; // 帧龄探测失败按"流健康"处理（既有口径）
        }

        if (!stale)
        {
            return;
        }

        requestStreamRevive("M1 前识别流停滞——先复活识别会话再出战。");
        for (var waited = 0; waited < 60; waited += 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            try
            {
                if (!isStreamStale())
                {
                    return;
                }
            }
            catch
            {
                // 探测失败继续等（复活后判定恢复即退出）
            }
        }
    }

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
        // 1.2.90 台账（审查 P1 修复）：M5 买到且执行器自动上场成功→按真实落槽登记台账；
        // 1.2.89 变异戳收窄（审查 P3）：仅盘面真的变化（买到/A1 成功）才打戳，零购买
        // 的 M5 不再触发部署段的强制重读+3s 动画等待。
        if (result.Error is null && command.Kind is GrailCommandKind.A1 or GrailCommandKind.M5)
        {
            var boardMutated = false;
            if (command.Kind == GrailCommandKind.A1)
            {
                boardMutated = true;
            }
            else if (result.Payload is GrailShopPassFact shopPass)
            {
                boardMutated = shopPass.BoughtCharacter;
                foreach (var (deployedName, deployedSlot) in shopPass.DeployedFrontSlots
                             ?? new Dictionary<string, int>())
                {
                    _frontLedger[deployedName] = deployedSlot;
                }
            }

            if (boardMutated)
            {
                _lastBoardMutationAt = DateTimeOffset.Now;
            }
        }

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
        // 1.2.95 备案清理（1.2.70 P3 销账）：I2 货架/I3 备战席的载荷是 GrailCharacterFact
        // 列表——引擎目前不发这两条，一旦将来发出（对账需要）落 _ 兜底会打"OK："空尾巴。
        IReadOnlyList<GrailCharacterFact> facts => string.Join(
            ",",
            facts.Select(f => $"{f.Slot}={f.Name ?? "?"}({f.Cost?.ToString() ?? "?"}费)")),
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
    /// 1.2.66 冗余审计：前密后疏退避（1.2.66 时 8 档 24s；1.2.116 实测定稿 5 档 5s，见下）（原 8×5s=40s
    /// 的 60%，交叉复核 F8 澄清：并非"相当"）——换取转场 1-3 秒完成时首次重试即命中，
    /// 识别冻结期的兜底由外层 SnapshotWithRetry 调用方的重试预算承接。</summary>
    // 1.2.112（用户令压缩等干净帧：商店关闭动画 <1s，26 秒停顿=慢节奏白等）：
    // 早期密集重试（0.5s 起步）；流救援仍在第 1 次失败触发。
    // 1.2.116（用户令实测定稿）：录像逐帧实测收店视觉尾巴 <1s（94.0s 帧仍有店/95.0s 帧
    // 已净，见 handoff B.3）→ 梯改 [0.5,0.5,1,1,2]，总窗 15→5s；识别冻结期长尾由
    // 调用方（60s 追帧/流救援）承接，不在梯内堆叠。
    private static readonly double[] SnapshotRetryBackoffSeconds = [0.5, 0.5, 1, 1, 2];

    private async Task<GrailRunSnapshot?> SnapshotWithRetryAsync(nint window, CancellationToken ct)
    {
        for (var attempt = 0; attempt < SnapshotRetryBackoffSeconds.Length; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(SnapshotRetryBackoffSeconds[attempt - 1]), ct);
            }

            // 1.2.94 提速（提速方案 #2）：首败即救援识别流——不等退避窗口走完
            // 才在长尾入口救援。委托异常吞掉；15s 冷却与单飞由测试台侧把守。
            if (attempt == 1 && requestStreamRevive is not null)
            {
                try
                {
                    requestStreamRevive("快照首败");
                }
                catch
                {
                    // 救援失败不阻断退避重试。
                }
            }

            var snapshot = await SnapshotAsync(window, ct);
            if (snapshot is not null)
            {
                // P2-4（对抗审查）：null=本局血量未知，绝不沿用陈旧缓存回填（F13）。
                if (snapshot.TeamHealth is > 0)
                {
                    _lastKnownTeamHealth = snapshot.TeamHealth;
                }
                return snapshot;
            }
        }

        return null;
    }

    /// <summary>
    /// P1-3（1.2.73 实测根因修复）：识别流追帧长尾——SnapshotWithRetryAsync（1.2.116 起 5 档
    /// ≈5s）全败后，若 M8/导航流刚用实时分类确认过页面（调用方自行判定），识别流的
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

    /// <summary>
    /// M8 在途识别流看门狗（1.2.89，16:31 命中局实锤）：M8 导航靠自己的单帧捕获，
    /// 但"上一局未结算守卫"需要识别流页面序列——流在 M8 途中冻结会让到达守卫不可
    /// 判定，M8 诚实报失败，引擎再基于冻结流的陈旧 I1 误判处置（白弃命中局）。
    /// 本包装在 M8 在途期间每 5 秒查一次流活性（isStreamStale 委托；1.2.118 判据
    /// 修订=流活动脉冲 30s 停走才算冻结——原帧龄口径被坑50 弹框抑制冻结，弹框期
    /// 必然误报，审计 #5 通宵 6 次幻影重启根因），冻结即请测试台重启识别会话
    /// （1.2.88 救援通道，绕启发式 300s 节流；15s 冷却防单飞排队刷屏）。
    /// 守卫有帧可看后，命中局不再因流冻结被烧。
    /// </summary>
    private async Task<GrailCommandResult> SendM8WithStreamWatchdogAsync(
        nint window, CancellationToken ct)
    {
        var m8Task = SendAsync("M8", new GrailCommand(GrailCommandKind.M8), window, ct);
        var lastReviveRequestAt = DateTimeOffset.MinValue;
        while (!m8Task.IsCompleted)
        {
            var completed = await Task.WhenAny(m8Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
            if (completed == m8Task)
            {
                break;
            }

            if (isStreamStale is null || requestStreamRevive is null)
            {
                continue;
            }

            var stale = false;
            try
            {
                stale = isStreamStale();
            }
            catch
            {
                // 帧龄探测失败按"流健康"处理，不影响 M8 本体。
            }

            if (stale && DateTimeOffset.Now - lastReviveRequestAt >= TimeSpan.FromSeconds(15))
            {
                lastReviveRequestAt = DateTimeOffset.Now;
                emit("[决策层] M8 在途检测到识别流冻结——请求重启识别会话（守卫需要帧序列）。");
                try
                {
                    requestStreamRevive("M8 在途看门狗");
                }
                catch
                {
                    // 救援委托异常不阻断 M8 本体。
                }
            }
        }

        return await m8Task;
    }

    /// <summary>
    /// 1.2.119（审计簇 E）：快照持续不可得的恢复终态机——长尾 60s 耗尽后**不再直接
    /// 弃局**（审计 10:35 局：68 秒识别追赶被误标"R3/失败"弃掉 019 命中局）。本方法
    /// 总预算 ≈5 分钟：每轮=统一弹框守卫（簇 C，实拍应答可能的阻塞模态）→5s 退避窗
    /// →60s 追帧长尾。任一轮恢复即返回快照；三轮耗尽返回 null，调用方带真实标签
    /// （"快照持续不可得"）降级弃局并响亮留痕——**绝不再混标 R3**。
    /// </summary>
    private async Task<GrailRunSnapshot?> TryRecoverSnapshotAsync(
        nint window,
        CancellationToken ct)
    {
        const int maximumRecoveryRounds = 3;
        for (var round = 1; round <= maximumRecoveryRounds; round++)
        {
            emit($"[决策层] 快照恢复第 {round}/{maximumRecoveryRounds} 轮：" +
                 "先弹框守卫（应答可能的阻塞模态）再重新追帧。");
            if (modalGuard is not null)
            {
                try
                {
                    if (await modalGuard.DismissBlockingModalIfUpAsync(window, ct))
                    {
                        emit("[决策层] 弹框守卫已应答一个阻塞模态。");
                    }
                }
                catch (Exception guardError) when (guardError is not OperationCanceledException)
                {
                    emit($"[决策层] 弹框守卫异常（不阻断恢复）：{guardError.Message}");
                }
            }

            var recovered = await SnapshotWithRetryAsync(window, ct)
                ?? await SnapshotWithRetrySlowTailAsync(window, ct);
            if (recovered is not null)
            {
                emit($"[决策层] 快照在第 {round} 轮恢复——继续原流程。");
                // P2-4（对抗审查）：同上，null 不回填陈旧值。
                if (recovered.TeamHealth is > 0)
                {
                    _lastKnownTeamHealth = recovered.TeamHealth;
                }
                return recovered;
            }
        }

        emit("[决策层] 快照恢复预算（3 轮 ≈5 分钟）耗尽——仍不可得。");
        return null;
    }

    private async Task<GrailPageFact?> PageAsync(nint window, CancellationToken ct)
    {
        var result = await SendAsync("I1", new GrailCommand(GrailCommandKind.I1), window, ct);
        return result.Error is null ? result.Payload as GrailPageFact : null;
    }

    /// <summary>等待祈愿弹框并应答。1.2.66 冗余审计：例行检查（未部署成员的轮次）
    /// 无弹框概率极高，单查一次立即返回；仅部署命杯成员后升档弹框会延迟弹出
    /// （实测），传 maxProbes=8 轮询（间隔 1.5 秒，窗口 ≈12 秒）。
    /// 1.2.106 confirmWithDetector：部署/买到成员后的调用点（弹框必出）在 I1 报
    /// 无弹框时追加 M3 检测器终判——I1 快速分类器在弹框浮层下会把页面报成底层
    /// preparation_generic（16:53 局实弹漏检→带弹框出战→误弃命中局；1.2.102 确认器
    /// 同款病理），而 M3 内部 WishTrialSelectionAutomation 检测可靠且不在屏时不点击、
    /// 诚实返回 Responded=False 无副作用。</summary>
    private async Task<bool> AnswerWishIfUpAsync(
        nint window, CancellationToken ct, int maxProbes, double probeIntervalSeconds = 1.5,
        bool confirmWithDetector = false)
    {
        for (var attempt = 0; attempt < maxProbes; attempt++)
        {
            var page = await PageAsync(window, ct);
            if (page is { WishDialogOpen: true })
            {
                var m3 = await SendAsync("M3", new GrailCommand(GrailCommandKind.M3), window, ct);
                return m3.Error is null;
            }

            if (confirmWithDetector)
            {
                var detector = await SendAsync("M3", new GrailCommand(GrailCommandKind.M3), window, ct);
                if (detector.Error is null
                    && detector.Payload is GrailWishOutcomeFact answered
                    && answered.Responded)
                {
                    emit("[决策层] I1 未报祈愿但 M3 检测器确认弹框在屏并已应答（快速分类器漏检兜底）。");
                    return true;
                }
            }

            if (attempt < maxProbes - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(probeIntervalSeconds), ct);
            }
        }

        return false;
    }

    /// <summary>确认升档祈愿已应答（弹框在屏必答；不在屏按已答/延迟处理）。
    /// maxProbes 缺省 1=例行单查；部署命杯成员后的调用点传 8+检测器终判（1.2.106）。</summary>
    private Task EnsureWishAnsweredAsync(
        nint window, CancellationToken ct, int maxProbes = 1, bool confirmWithDetector = false) =>
        AnswerWishIfUpAsync(window, ct, maxProbes, confirmWithDetector: confirmWithDetector);

    /// <summary>M5 回执是否买到非昔涟成员（昔涟只买不上场、不升档）——
    /// 执行器内部上场的成员同样触发升档弹框，外层须补轮询捕获延迟弹出。</summary>
    private static bool BoughtNonXilianMember(GrailCommandResult? shopResult) =>
        shopResult?.Payload is GrailShopPassFact fact &&
        fact.BoughtCharacterNames?.Any(name => !string.Equals(
            name, GrailRunSnapshot.XilianName, StringComparison.Ordinal)) == true;

    /// <summary>M5 买入的非昔涟成员数（P2-F 档位估算用上界：内部上场可能被跳过，
    /// 高估只多开窗不漏检；DeployedFrontSlots 只记前台，后台兜底上场靠此上界覆盖）。</summary>
    private static int CountM5BoughtNonXilian(GrailCommandResult? shopResult) =>
        shopResult?.Payload is GrailShopPassFact fact && fact.BoughtCharacterNames is not null
            ? fact.BoughtCharacterNames.Count(name => !string.Equals(
                name, GrailRunSnapshot.XilianName, StringComparison.Ordinal))
            : 0;

    /// <summary>
    /// 命运圣杯羁绊档位序（0 起）：2/3/4/5 人各激活一档、每档一次祈愿共 4 次
    /// （GrailRunSnapshot.WishesResponded 口径）。1 人无档，5 人封顶。
    /// </summary>
    internal static int WishTierOf(int bondMemberCount) => bondMemberCount switch
    {
        >= 5 => 4,
        >= 4 => 3,
        >= 3 => 2,
        >= 2 => 1,
        _ => 0
    };

    /// <summary>
    /// P2-F（2026-09-09 修复批）：部署/购买/装配后的祈愿 8 次窗门控判定。
    /// 开窗条件=**当前估算档位 &gt; 已应答档数**（存在已激活未应答的档位→弹框在屏或
    /// 延迟弹出；1.2.106 的"部署 2 名跨档而探针仅 1 次"病理在此分支下全盖）。
    /// 估算=基线快照羁绊数 + 基线后的已知增量（部署段 bondDelta + 装配 +1 +
    /// 可选的 M5 买入数上界）——高估只多开窗不漏检（安全方向）。台账不可信
    /// （基线缺失/识别异常注记/基线早于最近盘面变异）→ 保守回退开窗。
    /// <paramref name="applyRecentMutationDoor"/>：P2-2（审查修复）活门只在**外层**
    /// 门控开启——外层基线与 M5 内部上场之间的变异未逐笔记账（识别滞后时基线
    /// 帧可能早于 M5 变异而 CapturedAt 墙钟晚于它，双判据防时序恒真绕过）；
    /// 部署环内的变异=本环 A1 部署本身，已由 bondDelta 逐笔计入，活门会把自己
    /// 当未知变异恒触发保守窗（门控细化失效），故环内不启用。
    /// 三兜底（M1 失败应答重试/A9 前应答/opening 泵）不裁撤。
    /// </summary>
    private bool IsWishWindowRequired(
        GrailRunSnapshot? tierBaseline,
        int bondDeltaSinceBaseline,
        bool badgeAssembled,
        bool includeM5Delta,
        GrailCommandResult? m5Result,
        bool applyRecentMutationDoor)
    {
        if (tierBaseline is null || !string.IsNullOrEmpty(tierBaseline.AnomalyNotes))
        {
            return true; // 台账不可信：保守回退 8 次窗
        }

        // P2-2（2026-09-09 审查修复）：CapturedAt 是组装墙钟非帧时刻，仅时间戳比对
        // 会被"基线取在变异之后"的时序恒真绕过（识别滞后 19s+ 是本库反复实锤病理）；
        // 外层叠加"最近 10s 内发生过盘面变异→基线可能拍在变异前"的活门，保守开窗。
        if (applyRecentMutationDoor
            && ((tierBaseline.CapturedAt is { } capturedAt
                    && capturedAt < _lastBoardMutationAt)
                || DateTimeOffset.Now - _lastBoardMutationAt < TimeSpan.FromSeconds(10)))
        {
            return true;
        }

        var estimated = tierBaseline.BondMemberCount
            + Math.Max(0, bondDeltaSinceBaseline)
            + (badgeAssembled ? 1 : 0)
            + (includeM5Delta ? CountM5BoughtNonXilian(m5Result) : 0);
        var responded = stateHolder.PeekEventState().WishesResponded;
        return WishTierOf(estimated) > responded;
    }

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
    private async Task<(bool DeployedAny, int DeployedBondDelta, GrailRunSnapshot? LatestSnapshot)>
        DeployBondMembersAsync(
            nint window, GrailRunSnapshot? existingSnapshot, CancellationToken ct)
    {
        var deployedAny = false;
        // P2-F（2026-09-09 修复批）：本段实际部署的命杯成员数（档位门控增量；
        // 学者下场/补位、杂兵填充不改羁绊计数，不计入）。
        var deployedBondDelta = 0;
        // 1.2.89（用户令第 1 问题；审查 P2 加固）：快照早于盘面变异（M5 买到/A1 部署）
        // 即不可用于选槽——陈旧占用表会让新部署拖到已占槽=把刚上场的命杯成员换下
        // （16:57 阮•梅顶掉远坂凛实锤）。CapturedAt 是组装墙钟不是帧时刻，故叠加
        // "变异 10s 内一律忽略传入快照强制现读"的活门；变异刚发生时先等部署动画（X13）。
        if (existingSnapshot is not null
            && ((existingSnapshot.CapturedAt is { } capturedAt
                    && capturedAt < _lastBoardMutationAt)
                || DateTimeOffset.Now - _lastBoardMutationAt < TimeSpan.FromSeconds(10)))
        {
            existingSnapshot = null;
        }

        var sinceMutation = DateTimeOffset.Now - _lastBoardMutationAt;
        if (sinceMutation >= TimeSpan.Zero && sinceMutation < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(TimeSpan.FromSeconds(3) - sinceMutation, ct);
        }

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
        // P2-F：档位基线=部署段首份有效快照（null=不可信，环内探针保守开窗）。
        var tierBaseline = snapshot;
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

                // 1.2.90 台账：已在账本=已上场（M5 内部自动上场/此前 A1），跳过不再部署。
                if (_frontLedger.ContainsKey(candidateName))
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

            // 1.2.90：槽位空闲=识别∪台账并集判定（识别漏读已上场单位时账本兜底）。
            var slot = FirstFreeFrontSlot(snapshot);
            if (slot is null)
            {
                break;
            }

            var deploy = await SendAsync(
                $"A1 {pendingBench} 前台 {slot.Value + 1}",
                new GrailCommand(GrailCommandKind.A1,
                    new GrailDeployArgs(pendingBench, PreparationLane.Front, slot.Value)),
                window, ct);
            if (deploy.Error is not null)
            {
                // 1.2.119（审计簇 D2，3-1/3-2 实锤，rule 四.5）：4 号位部署失败=
                // 人口不足的实证信号（局 3：凛 8 连败全在 4 号位，金 16 够买没买）。
                // 金≥买经验总价（含诅咒涨价）且本局未买过→买经验升人口后重试一次；
                // 金不足或仍失败=如实交外层对账（绝不无限重试）。
                if (slot.Value == 3
                    && string.Equals(_currentNode, "1-3", StringComparison.Ordinal)
                    && !stateHolder.XpBoughtThisRun
                    && snapshot.Gold >= snapshot.XpPurchaseTotalCost)
                {
                    // P2-3（对抗审查）：节点门=1-3 显式前置（FIX_PLAN 八.P1-1"仅 1-3，
                    // 1-1/1-2 绝不触发"）——1-2 半帧误报空 4 号位时绝不动资金；人口复验
                    // 由闩锁+节点等效承担（本局未买过经验且在 1-3 ⇒ 人口=4 自然值）。
                    emit($"[决策层] BuyXpForDeploy：前台4号位部署失败且金={snapshot.Gold}≥买经验价" +
                         $"（{snapshot.XpPurchaseTotalCost}）——判定人口不足，买经验升人口（rule 四.5）后重试部署。");
                    publishEvent?.Invoke(
                        "BuyXpForDeploy",
                        $"1-3 前台4号位部署失败 金={snapshot.Gold}≥价{snapshot.XpPurchaseTotalCost}——买经验升人口");
                    var buyXp = await SendAsync(
                        "A6",
                        new GrailCommand(GrailCommandKind.A6),
                        window, ct);
                    if (buyXp.Error is null)
                    {
                        stateHolder.MarkXpBoughtThisRun();
                        var retry = await SendAsync(
                            $"A1 {pendingBench} 前台 {slot.Value + 1}",
                            new GrailCommand(GrailCommandKind.A1,
                                new GrailDeployArgs(pendingBench, PreparationLane.Front, slot.Value)),
                            window, ct);
                        if (retry.Error is null)
                        {
                            _frontLedger[pendingBench] = slot.Value;
                            deployedAny = true;
                            deployedBondDelta++;
                            // P2-F：跨档才开 8 次探针窗（基线+已部署数落到新档位）。
                            var retryCrossed = IsWishWindowRequired(
                                tierBaseline, deployedBondDelta, false,
                                includeM5Delta: false, m5Result: null,
                                applyRecentMutationDoor: false);
                            await EnsureWishAnsweredAsync(window, ct,
                                maxProbes: retryCrossed ? 8 : 1,
                                confirmWithDetector: retryCrossed);
                            snapshot = await SnapshotWithRetryAsync(window, ct);
                            snapshotFresh = snapshot is not null;
                            if (snapshot is not null)
                            {
                                // P3-1（2026-09-09 审查备案采纳）：基线随部署后重读刷新、
                                // 增量清零——门控保持精确，段内第二名起不再恒走保守窗。
                                tierBaseline = snapshot;
                                deployedBondDelta = 0;
                            }
                            continue;
                        }
                        emit("[决策层] 买经验后重试部署仍失败——交外层对账。");
                    }
                    else
                    {
                        emit("[决策层] 买经验输入失败——交外层对账。");
                    }
                }

                snapshotFresh = false;
                break; // 识别不到该名（识别缺陷）→ 交外层对账，绝不盲拖
            }

            _frontLedger[pendingBench] = slot.Value;
            deployedAny = true;
            deployedBondDelta++;
            // P2-F：跨档才开 8 次探针窗+检测器终判（未跨档=弹框必不来，单查即回，
            // 1-1 首名部署 0→1 不跨档——原 8 次窗是每名部署 ~12 秒纯等待主源）。
            var deployCrossed = IsWishWindowRequired(
                tierBaseline, deployedBondDelta, false,
                includeM5Delta: false, m5Result: null,
                applyRecentMutationDoor: false);
            await EnsureWishAnsweredAsync(window, ct,
                maxProbes: deployCrossed ? 8 : 1,
                confirmWithDetector: deployCrossed);
            snapshot = await SnapshotWithRetryAsync(window, ct); // 部署后重读找下一个候选
            snapshotFresh = snapshot is not null;
            if (snapshot is not null)
            {
                // P3-1（2026-09-09 审查备案采纳）：同买经验重试路径——基线刷新+增量清零。
                tierBaseline = snapshot;
                deployedBondDelta = 0;
            }
        }

        // 1.2.89 学者规则重写（用户令 2026-09-05，第 1/2 问题）：
        // ①仅 1-1 生效（学者羁绊需经过至少两个节点才能完成，1-2/1-3 上学者无意义）；
        // ②仅当备战席同时存在两名不同银河学者才上场（凑 2 羁绊；单学者绝不上）；
        // ③只能上到空槽——绝不替换已上场单位（16:57 阮•梅顶掉远坂凛实锤根因之一：
        //   陈旧快照占用表）；
        // ④1-3 反向操作：场上银河学者（非星徽携带者）必须下场并卖掉（省金币）。
        if (!snapshotFresh)
        {
            snapshot = await SnapshotWithRetryAsync(window, ct);
            snapshotFresh = snapshot is not null;
        }

        var scholarNames = new HashSet<string>(
            gameData.CurrencyWarsCharacters
                .Where(character => character.BondNames.Any(
                    bond => bond is not null && bond.Contains("银河学者", StringComparison.Ordinal)))
                .Select(character => character.Name),
            StringComparer.OrdinalIgnoreCase);

        if (snapshot is not null && _currentNode == "1-3")
        {
            var frontScholars = snapshot.DeployedCharacterDetails
                .Where(detail => !detail.Contains("[星徽]", StringComparison.Ordinal)
                                 && scholarNames.Contains(PureName(detail.Split(':')[^1])))
                .ToList();
            foreach (var detail in frontScholars.Take(2))
            {
                var slotHead = detail.Split(':')[0];
                if (!int.TryParse(slotHead.Replace("F", string.Empty, StringComparison.Ordinal),
                        out var frontSlot) || frontSlot < 1)
                {
                    continue;
                }

                // A2 期望名贯穿（P1-C 后续批 2026-09-09）：下发点全量传台账认为的
                // 占用人名——操作层拖前身份比对防模型漂移误卖（坑38 纪律）。
                var scholarExpectedName = PureName(detail.Split(':')[^1]);
                emit($"[决策层] 1-3 学者下场：出售场上 {detail}（学者规则：1-3 必下必卖省金币）。");
                var sell = await SendAsync(
                    $"A2 前台 {frontSlot}",
                    new GrailCommand(GrailCommandKind.A2,
                        new GrailPositionArgs(PreparationLane.Front, frontSlot - 1,
                            scholarExpectedName)),
                    window, ct);
                if (sell.Error is not null)
                {
                    emit($"[决策层] 1-3 学者出售失败：{sell.Error}——交外层对账。");
                    break;
                }

                // 1.2.90 台账（复审 P3-1 修正）：卖出后等 1s 沉淀再重读，且按**被卖学者
                // 名字**确认离场（不按"任意学者消失"——场上双学者时卖第 1 只不该误判反证）。
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                var scholarRereadAfterSell = await SnapshotWithRetryAsync(window, ct);
                var soldScholarName = PureName(detail.Split(':')[^1]);
                if (scholarRereadAfterSell is null
                    || scholarRereadAfterSell.DeployedCharacterDetails.Any(detail =>
                        string.Equals(
                            PureName(detail.Split(':')[^1]),
                            soldScholarName,
                            StringComparison.Ordinal)))
                {
                    emit("[决策层] 学者出售后重读仍见该学者——反证即停，交外层对账。");
                    snapshot = scholarRereadAfterSell ?? snapshot;
                    snapshotFresh = scholarRereadAfterSell is not null;
                    break;
                }

                _frontLedger.Remove(soldScholarName);
                deployedAny = true;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                snapshot = await SnapshotWithRetryAsync(window, ct);
                snapshotFresh = snapshot is not null;
                if (snapshot is null)
                {
                    return (deployedAny, deployedBondDelta, null);
                }
            }
        }

        if (snapshot is null || snapshot.OccupiedFrontSlots.Count >= 4)
        {
            return (deployedAny, deployedBondDelta, snapshotFresh ? snapshot : null);
        }

        if (_currentNode != "1-1")
        {
            return (deployedAny, deployedBondDelta, snapshotFresh ? snapshot : null);
        }

        // 1.2.90 审查 P2 修正：凑 2 计数=场上学者总数（台账∪识别，星徽携带者不算——
        // 携带者已经是命杯成员另有口径），而非只数备战席。场景"学者 A 已在场+备战席
        // 学者 B"→可上数=2-1=1→部署 B 凑成双学者羁绊（此前永不成对=买第二学者的钱白花）。
        var onFieldScholarCount = Math.Max(
            _frontLedger.Keys.Count(name => scholarNames.Contains(name)),
            snapshot.DeployedCharacterDetails.Count(detail =>
                !detail.Contains("[星徽]", StringComparison.Ordinal)
                && scholarNames.Contains(PureName(detail.Split(':')[^1]))));
        var benchScholarPairs = snapshot.BenchCharacterDetails
            .Select(item => (SlotHead: item.Split(':')[0], Name: PureName(item.Split(':')[^1])))
            .Where(pair => scholarNames.Contains(pair.Name))
            .Where(pair => !_frontLedger.ContainsKey(pair.Name)) // 已在场的不重复部署
            .GroupBy(pair => pair.Name)
            .Select(group => group.First())
            .ToList();
        var deployableScholars = Math.Min(Math.Max(0, 2 - onFieldScholarCount), benchScholarPairs.Count);
        // 1.2.108（17:24 067 局+17:29 019 局实弹）：此处原为提前 return——会让下方
        // "填满人口"块永远执行不到：备战席无学者的命中局（067 赠体全非白名单/019 赠体
        // 只有星徽），命杯白名单又没货→前台空→判死弃掉命中局（17:29 局连到手星徽一起
        // 浪费）。学者无可上时不再返回，直接落到填充块用杂兵填前台。
        if (deployableScholars > 0)
        {
            foreach (var pair in benchScholarPairs.Take(deployableScholars))
            {
            if (snapshot.OccupiedFrontSlots.Count >= 4)
            {
                break;
            }

            if (!int.TryParse(pair.SlotHead, out var scholarBenchSlot) || scholarBenchSlot < 0)
            {
                continue;
            }

            var scholarSlot = FirstFreeFrontSlot(snapshot);
            if (scholarSlot is null)
            {
                break;
            }

            var scholarDeploy = await SendAsync(
                $"A1 {pair.Name} 前台 {scholarSlot.Value + 1}",
                new GrailCommand(GrailCommandKind.A1,
                    new GrailDeployArgs(pair.Name, PreparationLane.Front, scholarSlot.Value)),
                window, ct);
            if (scholarDeploy.Error is not null)
            {
                emit($"[决策层] 学者补位「{pair.Name}」失败：{scholarDeploy.Error}——停止本轮学者部署。");
                break;
            }

            _frontLedger[pair.Name] = scholarSlot.Value;
            emit($"[决策层] 学者补位：{pair.Name} 已部署到前台 {scholarSlot.Value + 1} 号位。");
            deployedAny = true;
            // 学者非圣杯成员，上场不触发圣杯升档弹框——单查即可。
            await EnsureWishAnsweredAsync(window, ct);
            var scholarReread = await SnapshotWithRetryAsync(window, ct);
            if (scholarReread is not null)
            {
                snapshot = scholarReread;
                snapshotFresh = true;
            }
            else
            {
                snapshotFresh = false;
                break;
            }
            }
        }

        // 1.2.89 填满人口（用户令"优先要补满三个人"）：命杯/学者就位后，剩余空槽用
        // 备战席非保护单位补满（1-1/1-2 人口 3）——空着上场就是白打。保护排除：
        // 命杯成员（bond 环已处理）、纯 5 费（067 赠体锁定）、银河学者（等待凑 2）。
        if (snapshot is not null && _currentNode != "1-3")
        {
            var fillAttempts = 0;
            while (snapshot.OccupiedFrontSlots.Count < 3
                   && fillAttempts < 3
                   && !ct.IsCancellationRequested)
            {
                fillAttempts++;
                var fill = snapshot.BenchCharacterDetails
                    .Select(item => (SlotHead: item.Split(':')[0], Name: PureName(item.Split(':')[^1])))
                    .Where(pair => !scholarNames.Contains(pair.Name))
                    .Where(pair => !executor.GrailBondMemberNames.Contains(pair.Name))
                    .Where(pair => !_frontLedger.ContainsKey(pair.Name)) // 1.2.90：已在场不重复
                    .Select(pair => (Pair: pair, Candidate: ResolveProtectedCharacter(pair.Name)))
                    .Where(pair => pair.Candidate is null
                                   || !GrailOperationExecutor.IsPureFiveCostCharacter(pair.Candidate))
                    .Select(pair => pair.Pair)
                    .FirstOrDefault();
                if (fill == default || fill.Name is null)
                {
                    break;
                }

                if (!int.TryParse(fill.SlotHead, out var fillBenchSlot) || fillBenchSlot < 0)
                {
                    continue; // 1.2.89 审查 P3：单个明细解析失败跳过该条，继续尝试其他单位
                }

                var fillSlot = FirstFreeFrontSlot(snapshot);
                if (fillSlot is null || fillSlot.Value >= 3)
                {
                    break; // 1-1/1-2 人口 3：只填前 3 槽
                }

                var fillDeploy = await SendAsync(
                    $"A1 {fill.Name} 前台 {fillSlot.Value + 1}",
                    new GrailCommand(GrailCommandKind.A1,
                        new GrailDeployArgs(fill.Name, PreparationLane.Front, fillSlot.Value)),
                    window, ct);
                if (fillDeploy.Error is not null)
                {
                    break; // 识别不到/拖拽失败——交对账，不硬塞
                }

                _frontLedger[fill.Name] = fillSlot.Value;
                deployedAny = true;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                var fillReread = await SnapshotWithRetryAsync(window, ct);
                if (fillReread is null)
                {
                    snapshotFresh = false;
                    break;
                }

                snapshot = fillReread;
                snapshotFresh = true;
            }
        }

        return (deployedAny, deployedBondDelta, snapshotFresh ? snapshot : null);
    }

    /// <summary>1.2.90：槽位空闲判定=识别占用∪台账占用都不含该槽（识别∪账本并集）。</summary>
    private bool FrontSlotFree(GrailRunSnapshot snapshot, int slot)
    {
        if (snapshot.OccupiedFrontSlots.Contains(slot))
        {
            return false;
        }

        foreach (var occupiedSlot in _frontLedger.Values)
        {
            if (occupiedSlot == slot)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>1.2.90：第一个"识别∪台账"均空闲的前台槽位；无则 null。</summary>
    private int? FirstFreeFrontSlot(GrailRunSnapshot snapshot)
    {
        for (var slot = 0; slot < 4; slot++)
        {
            if (FrontSlotFree(snapshot, slot))
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Unknown 页处置（审查修正版）：仅当页面真正 Unknown（识别表外阻塞态，如位面图/
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
            // A4 期望名贯穿+幂等预查输入（2026-09-09 P1-A）：前台 1 号位的台账占用人名
            // 传给操作层——A4 预查用它与实时读佐证"该槽已带徽"，杜绝 G15 实锤的
            // 带徽重拖（游戏拒绝横幅"无法穿戴相同羁绊的星徽"）。
            var front1Occupant = _frontLedger.FirstOrDefault(kv => kv.Value == 0).Key;
            var a4 = await SendAsync(
                string.IsNullOrEmpty(front1Occupant) ? "A4 前台 1" : $"A4 前台 1 {front1Occupant}",
                new GrailCommand(GrailCommandKind.A4,
                    new GrailPositionArgs(PreparationLane.Front, 0, front1Occupant)),
                window, ct);
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
            // 1.2.89（用户令 R3 口径）：同名多处=本轮快照身份存疑，先重读一次新鲜快照
            // 复核——读干净就照卖（可卖单位存在时绝不据此宣布山穷水尽，16:53 实锤：
            // 有可卖单位却因拒卖被 R3 判死）；仍异常才拒卖交对账。
            emit("[决策层] 卖人前发现同名多处异常注（识别身份存疑）——重读新鲜快照复核。");
            var anomalyReread = await SnapshotWithRetryAsync(window, ct);
            if (anomalyReread is null
                || anomalyReread.AnomalyNotes.Contains("同名多处", StringComparison.Ordinal))
            {
                emit("[决策层] 复核仍同名多处（或读不到快照）——本轮拒绝卖出，交对账。");
                return 0;
            }

            snapshot = anomalyReread;
        }

        // 1.2.105（15:02 局实弹：S4 前置快照漏读 2 名场上单位→cap 算小→清场漏卖，
        // 用户令"上过场的角色也要卖出"）：cap 不再取自单一快照——卖出后的 I10 复核
        // 本身就是新鲜快照，循环条件改用实时 SellableBeyondKeepLineCount，前置漏读
        // 在下一轮自愈；cap=0 终判前必须连续两帧一致（防漏读帧提前收工）。
        // 保留线（未携带星徂数）语义由 SellableBeyondKeepLineCount 继续承担。
        // 可卖数>0 却选不出目标=识别不一致帧：重读复核最多 2 次；绝对上限 13 条
        // （备战席 9+前台 4）防异常振荡。1.2.58 反证即停语义不变。
        var sold = 0;
        var inconsistentRereads = 0;
        var emptyCapConfirmed = false;
        while (sold < 13 && !ct.IsCancellationRequested)
        {
            if (snapshot.SellableBeyondKeepLineCount <= 0)
            {
                if (emptyCapConfirmed)
                {
                    // 1.2.109（19:44 局实弹：S4 清场只卖了 1 人——场上 3 名杂兵在识别
                    // 半帧里不可见，cap=0 两帧一致仍漏卖）：台账对账（1.2.90 口径识别∪
                    // 账本）——台账记着上场而快照看不见的槽位，按台账位置直接 A2 卖
                    // （数据级保护：命杯/纯5费/星徽携带者跳过）。卖出后复核名字消失。
                    var carriers = executor.PeekBadgeCarrierNames();
                    var pendingSlots = executor.PeekBadgePendingSlotKeys();
                    var ledgerSellable = _frontLedger
                        .Where(kv => !carriers.Contains(kv.Key))
                        .Where(kv => !pendingSlots.Contains(
                            GrailSnapshotAssembler.BadgeLedgerSlotKey(FormationZone.Front, kv.Value)))
                        .Select(kv => (kv.Key, kv.Value))
                        .Where(pair => ResolveProtectedCharacter(pair.Key) is { } c
                                       && !GrailOperationExecutor.IsPureFiveCostCharacter(c)
                                       && !c.BondNames.Any(b =>
                                           b is not null && b.Contains("命运圣杯", StringComparison.Ordinal)))
                        .ToList();
                    if (ledgerSellable.Count == 0)
                    {
                        break;
                    }

                    emit($"[决策层] 清场：可卖数=0 但上场台账有 {ledgerSellable.Count} 名快照不可见单位——按台账槽位对账卖出。");
                    var ledgerSoldAny = false;
                    foreach (var (name, slot) in ledgerSellable)
                    {
                        // 1.2.109 审查 P1-1：A2 只认位置，卖出前必须双源确认该槽位
                        // **实际占用人**可卖（压缩位移/挤压可能让保护成员站进台账槽位）；
                        // 身份不可识别=不可证安全，跳过该条。卖出后槽位实测变空才算数
                        //（堵"名字缺席即通过"的空真复核）。
                        var occupant = await executor.PeekFrontSlotCharacterAsync(
                            window, slot, "preparation_generic", ct);
                        if (occupant is null)
                        {
                            emit($"[决策层] 台账对账：前台 {slot + 1} 号位占用人身份不可证（空/不可识别）——跳过「{name}」。");
                            continue;
                        }

                        var occupantData = ResolveProtectedCharacter(occupant);
                        if (occupantData is null
                            || GrailOperationExecutor.IsPureFiveCostCharacter(occupantData)
                            || occupantData.BondNames.Any(b =>
                                b is not null && b.Contains("命运圣杯", StringComparison.Ordinal))
                            || carriers.Contains(occupant))
                        {
                            emit($"[决策层] 台账对账：前台 {slot + 1} 号位实际占用人「{occupant}」受保护——跳过（台账名「{name}」可能已位移）。");
                            continue;
                        }

                        var a2 = await SendAsync(
                            $"A2 前台 {slot + 1}（台账对账：{occupant}）",
                            new GrailCommand(GrailCommandKind.A2,
                                // A2 期望名贯穿（2026-09-09）：实时读到的占用人名=最强佐证。
                                new GrailPositionArgs(PreparationLane.Front, slot, occupant)),
                            window, ct);
                        if (a2.Error is not null)
                        {
                            emit($"[决策层] 台账对账卖出「{occupant}」回执失败：{a2.Error}——跳过该槽。");
                            continue;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        if (await executor.PeekFrontSlotCharacterAsync(
                                window, slot, "preparation_generic", ct) is not null)
                        {
                            emit($"[决策层] 台账对账卖出「{occupant}」后槽位仍有卡——反证即停，交对账。");
                            break;
                        }

                        _frontLedger.Remove(name);
                        ledgerSoldAny = true;
                        sold++;
                        emit($"[决策层] 台账对账卖出「{occupant}」复核通过（槽位实测变空）。");
                    }

                    if (!ledgerSoldAny)
                    {
                        break;
                    }

                    var afterLedger = await SnapshotWithRetryAsync(window, ct);
                    if (afterLedger is not null)
                    {
                        snapshot = afterLedger;
                    }

                    emptyCapConfirmed = false;
                    continue;
                }

                // cap=0 可能来自漏读帧（15:02 局根因）——终判前重读一次确认。
                emptyCapConfirmed = true;
                var confirm = await SnapshotWithRetryAsync(window, ct);
                if (confirm is null)
                {
                    emit("[决策层] 清场：可卖数=0 终判前重读快照失败——停手交对账。");
                    break;
                }

                snapshot = confirm;
                continue;
            }

            emptyCapConfirmed = false;
            var target = SelectNextSellableTarget(snapshot);
            if (target is null)
            {
                inconsistentRereads++;
                if (inconsistentRereads > 2)
                {
                    emit("[决策层] 清场：连续 3 帧可卖数与明细不一致——停手交对账。");
                    break;
                }

                var reread = await SnapshotWithRetryAsync(window, ct);
                if (reread is null)
                {
                    emit("[决策层] 清场：重读快照失败——停手交对账。");
                    break;
                }

                snapshot = reread;
                continue;
            }

            var sell = target.Kind == SellTargetKind.Bench
                ? await SendAsync($"A3 {target.SlotNumber + 1}",
                    new GrailCommand(GrailCommandKind.A3,
                        new GrailBenchSlotArgs(target.SlotNumber)), window, ct)
                : await SendAsync($"A2 {(target.IsBack ? "后台" : "前台")} {target.SlotNumber}",
                    new GrailCommand(GrailCommandKind.A2,
                        new GrailPositionArgs(
                            target.IsBack ? PreparationLane.Back : PreparationLane.Front,
                            target.SlotNumber - 1,
                            target.Name)),
                    window, ct);
            if (sell.Error is not null)
            {
                emit($"[决策层] 卖出「{target.Name}」回执失败：{sell.Error}——反证即停。");
                break;
            }

            sold++;
            inconsistentRereads = 0; // 卖出成功=不一致帧已过去，重计"连续"次数（审查 P3-2）
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
                var slotDisplay = target.Kind == SellTargetKind.Bench
                    ? target.SlotNumber + 1  // A3 文本按 1 基显示，复核文案同基防"卖4查3"歧义
                    : target.SlotNumber;
                emit($"[决策层] 卖出「{target.Name}」复核未通过（{target.Kind} " +
                     $"{slotDisplay} 号位状态与预期不符，可能为卖出未生效或同名位移）——反证即停，交对账。");
                break;
            }

            // 1.2.90 审查 P2：台账清除必须在卖出生效复核**之后**——A2/A3 的 OK 只代表
            // 输入成功（rule 三.10/四.10），复核反证时单位仍在场，台账提前删=互换窗口。
            _frontLedger.Remove(target.Name);
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
            // 1.2.90 台账：识别∪账本并集判定前台有人——识别漏读已上场单位时账本兜底
            // （20:41 实锤：A1 像素验证 OK 后 I10 连续读前台空）。
            var hasFront = _frontLedger.Count > 0
                || fresh.DeployedCharacterDetails.Any(detail =>
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

    /// <summary>
    /// 凑息（1.2.116，用户拍板"1-2 出战前凑 10 金"）：金币&lt;10 时卖备战席冗余卡到 ≥10，
    /// 使 1-3 开局多得 1 金利息（10 金=+1）。保护线与清场同源（TryParseSellable：
    /// 命杯/纯5费/星徽佩戴者绝不卖）+材料线（InterestTopUpPlanner：同名 ≥2 张=升星
    /// 材料链不卖）+上限 2 张；每卖后重读金币，回执失败或金币未增=反证即停（坑 38 纪律）；
    /// 凑不到 10 或无候选=静默放行出战（1 金收益不值得卡流程，更不值得不可逆误卖）。
    /// 仅 1-2 出战前调用（S3）；1-1 出战前不凑（用户口径只覆盖 1-2→1-3）。
    /// </summary>
    private async Task TopUpInterestGoldBeforeBattleAsync(
        nint window,
        GrailRunSnapshot snapshot,
        CancellationToken ct)
    {
        const int interestTargetGold = 10;
        const int maxSales = 2;
        if (snapshot.Gold >= interestTargetGold)
        {
            return;
        }

        emit($"[决策层] 凑息：金={snapshot.Gold} < 10——尝试卖备战席冗余卡凑利息线。");
        var nameCounts = InterestTopUpPlanner.CountNames(
            snapshot.BenchCharacterDetails, snapshot.DeployedCharacterDetails);
        var working = snapshot;
        var sold = 0;
        while (working.Gold < interestTargetGold && sold < maxSales)
        {
            SellTarget? target = null;
            foreach (var detail in working.BenchCharacterDetails)
            {
                if (!TryParseSellable(detail, bench: true, out var candidate)
                    || candidate is null
                    || !InterestTopUpPlanner.IsInterestSellable(detail, nameCounts))
                {
                    continue;
                }

                // 1.2.116 审查 P1-1b（卖价感知）：卖价=卡最低费用（既有口径 Costs.Min）。
                // 金+卖价仍 <10 → 卖了也凑不齐=白损战力换 0 利息，跳过该候选；
                // 费用解析不出=按 0 处理=永不达标（保守不卖，顺带覆盖 Gold=0 假帧）。
                var character = ResolveProtectedCharacter(candidate.Name);
                var saleValue = InterestTopUpPlanner.SaleValueOf(character?.Costs);
                if (!InterestTopUpPlanner.ReachesTarget(working.Gold, saleValue, interestTargetGold))
                {
                    continue;
                }

                target = candidate;
                break;
            }

            if (target is null)
            {
                emit("[决策层] 凑息：保护线/材料线/达标线过滤后无可卖冗余——静默放行出战。");
                return;
            }

            var sell = await SendAsync(
                $"A3 {target.SlotNumber + 1}（凑息:{target.Name}）",
                new GrailCommand(GrailCommandKind.A3, new GrailBenchSlotArgs(target.SlotNumber)),
                window,
                ct);
            if (sell.Error is not null)
            {
                emit($"[决策层] 凑息卖出「{target.Name}」回执失败：{sell.Error}——反证即停，放行出战。");
                return;
            }

            sold++;
            var reread = await SnapshotWithRetryAsync(window, ct);
            if (reread is null)
            {
                emit("[决策层] 凑息：卖出后重读快照失败——停手放行出战（防动画期盲卖）。");
                return;
            }

            // 1.2.116 审查 P2-1：与清场 VerifySaleApplied 对齐——金币实增不能证明卖的是
            // 目标槽那张（识别半帧错位时可能误卖材料卡），目标槽同名消失才算数。
            if (!VerifySaleApplied(reread, target))
            {
                emit($"[决策层] 凑息：卖出「{target.Name}」后目标槽同名仍在——反证即停，放行出战。");
                return;
            }

            if (reread.Gold <= working.Gold)
            {
                emit($"[决策层] 凑息：卖出「{target.Name}」后金币未增（{working.Gold}→{reread.Gold}）——反证即停。");
                return;
            }

            working = reread;
        }

        emit($"[决策层] 凑息完成：金={working.Gold}（卖出 {sold} 张）——出战。");
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
        // P2-4（对抗审查）：血量缓存随局复位——原实现跨局沿用上一局缓存，
        // 本局快照血量 null 时会拿陈旧值继续判 ≤86（F13 口径=null=未知不弃）。
        _lastKnownTeamHealth = null;
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

            // 坑50（1.2.114，审查 P1-1 改法 a）：入口遇盛会之星升档选择框（22:49 停机后
            // 弹框遗留屏上的重启场景）——preparation 分支看不到它、M8 导航被模态挡死。
            // 不在引擎层复制点击（两套点位=漂移事故）：直接发 A9，恢复例程会先按页 ID
            // 应答弹框（任选角色+确认选择）再走 Esc 弃局；A9 失败则 M8 开局弹框泵
            //（同样 gala 感知）兜底二次消除。
            if (entryPage is { IsStale: false, PageId: CurrencyWarsRejectedOpeningRecovery.GalaBondPopupPageId })
            {
                emit("[决策层] 入口检测到盛会之星升档选择框——发 A9 走弃局链（先应答弹框再弃局）后重判页。");
                var entryAbandon = await SendAsync(
                    "A9", new GrailCommand(GrailCommandKind.A9), window, ct);
                if (entryAbandon.Error is not null)
                {
                    emit("[决策层] 入口 A9 失败（" + entryAbandon.Error + "）——依赖 M8 开局弹框泵兜底消除。");
                }

                entryPage = await PageAsync(window, ct);
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
                m8 = await SendM8WithStreamWatchdogAsync(window, ct);
                var fact = m8.Payload as GrailOpeningFact;
                if (m8.Error is null && fact is { Succeeded: true })
                {
                    arrived = true;
                    _abandonStreak = 0; // F1（1.2.70 交叉复核）：M8 到达=弃局链路健康，清零退避计数
                    hit067 = string.Equals(fact.MatchedEnvironmentName, "英雄登场", StringComparison.Ordinal);
                    // 1.2.89 节点锚点+新局边界：M8 到达=1-1 开始。
                    _currentNode = "1-1";
                    executor.AllowGalaxyScholarPurchase = true; // 1.2.102：学者购买仅 1-1
                    _frontLedger.Clear();
                    _lastBoardMutationAt = DateTimeOffset.MinValue;
                }
                else if (m8.Error is not null)
                {
                    // 1.2.89（16:31 命中局实锤）：识别流不可用签名→先救援识别会话并等帧
                    // 新鲜后再判页。此前流冻结时 PageAsync 读到陈旧帧把 1-1 误判成"主界面"，
                    // 引擎据此跳过 A9 重发 M8 三连败，把刚到手的命中局弃掉。
                    if ((m8.Error ?? string.Empty).Contains("无新鲜帧", StringComparison.Ordinal))
                    {
                        emit("[决策层] M8 失败签名=识别流不可用——救援识别会话并等帧新鲜后重判。");
                        try
                        {
                            requestStreamRevive?.Invoke("M8 守卫不可判定");
                        }
                        catch
                        {
                            // 救援失败不阻断（启发式复活仍在兜底）。
                        }

                        var freshAfterRescue = false;
                        for (var wait = 0; wait < 12 && !freshAfterRescue && !ct.IsCancellationRequested; wait++)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), ct);
                            freshAfterRescue = await SnapshotAsync(window, ct) is not null;
                        }

                        emit(freshAfterRescue
                            ? "[决策层] 识别流已恢复——以新鲜读数重判页面。"
                            : "[决策层] 识别流 60s 未恢复——按原失败路径处理。");
                    }

                    // 1.2.89（用户令第 4 问题）：弃局前先应答祈愿弹框——祈愿是模态，
                    // Esc 会被它吞掉（17:04 实锤：A9 三轮 Esc 全被祈愿吞→弃局死锁）。
                    await EnsureWishAnsweredAsync(window, ct, maxProbes: 2);

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
                // 1.2.89（用户令第 4 问题）：同上——A9 前先应答祈愿弹框，防 Esc 被吞死锁。
                await EnsureWishAnsweredAsync(window, ct, maxProbes: 2);
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
                // 1.2.119（审计簇 E）：弃局标签拆分——真实原因替代混标的"R3/失败"
                //（审计 10:35 局：快照不可得被标 R3 弃掉命中局）。A9 Payload 携带
                // 原因，弃局链 RecoveryAbandonStarted 留痕。
                var abandonReason = _lastAbandonReason ?? "R3:山穷水尽（金<刷新价∧可卖=0）";
                emit($"[决策层] 本局判定结束（{abandonReason}）——弃局重开下一局。");
                await SendAsync(
                    "A9",
                    new GrailCommand(GrailCommandKind.A9, abandonReason),
                    window,
                    ct);
                _lastAbandonReason = null;
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
        // P1-B（2026-09-09 修复批）：备战段入口先过统一弹框守卫——G15 局实锤角色详情
        // 残留框挂 90% 局时长且快照可得（恢复终态机永不触发），入口守卫是唯一清扫点；
        // 守卫自带认页（无模态零点击），异常不阻断备战段。
        if (modalGuard is not null)
        {
            try
            {
                if (await modalGuard.DismissBlockingModalIfUpAsync(window, ct))
                {
                    emit("[决策层] 备战段入口：弹框守卫清扫了一个残留弹框/面板。");
                }
            }
            catch (Exception guardError) when (guardError is not OperationCanceledException)
            {
                emit($"[决策层] 备战段入口弹框守卫异常（不阻断）：{guardError.Message}");
            }
        }

        // ---- S2：1-1（人口 3；067 局禁 A4）----
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        // P1-3（1.2.73 实测根因修复）：M8 刚用导航器实时分类确认到达备战页，但识别流
        // LatestAnalysis 滞后（实测进 1-1 后 19s+ 无新分析帧）→ I10 门禁连续全败 →
        // 好局被判 Interrupted 弃掉（02:18 命中局实锤）。加追帧长尾：识别管线恢复即自愈。
        var snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            emit("[决策层] 快照 5s 窗口全败——M8 刚确认到达备战页，判定为识别流滞后，进入 60s 追帧长尾。");
            snapshot = await SnapshotWithRetrySlowTailAsync(window, ct);
        }

        if (snapshot is null)
        {
            snapshot = await TryRecoverSnapshotAsync(window, ct);
            if (snapshot is null)
            {
                _lastAbandonReason = "快照持续不可得（恢复预算耗尽）";
                return PreparationOutcome.Interrupted;
            }
        }

        // P2-F（2026-09-09 修复批）：S2 祈愿档位基线=本快照（M5 之前），M5 买入的
        // 内部上场在基线之后发生——下方门控按"基线羁绊数+已知增量"估算跨档。
        var s2TierBaseline = snapshot;

        var m5Result = await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        // 1.2.66：M5 可能买了角色（盘面已变）→ 部署段须现读快照（传 null）。
        var (deployed, deployedBondDelta, _) = await DeployBondMembersAsync(window, null, ct);
        var assembled = await AssembleBadgeIfAvailableAsync(window, ct, hit067);
        // P2-F（2026-09-09 修复批，细化 1.2.106 门控）：8 次窗+检测器终判只在
        // "羁绊档位已激活但尚未应答"时开启（祈愿=每档一次共 4 档，rule 四.7）——
        // 未激活档位必无弹框，12 秒窗全数浪费（16a 决策速度）；基线快照缺失/
        // 异常注记/陈旧=台账不可信，保守回退 8 次窗。1.2.106 的漏检病理（部署
        // 2 名成员跨档而探针仅 1 次）在"跨档→开窗"分支下仍然全盖。
        var wishExpected = IsWishWindowRequired(
            s2TierBaseline, deployedBondDelta, assembled,
            includeM5Delta: true, m5Result,
            applyRecentMutationDoor: true);
        await EnsureWishAnsweredAsync(window, ct,
            maxProbes: wishExpected ? 8 : 1,
            confirmWithDetector: wishExpected);
        // P2-1（1.2.71 运行时审计）：justActed 纳入"执行器内部上场"——M5 买到成员的
        // 上场动画同样会让立即 I10 读到空前台（19:17 误弃好局同款），前置门按动画期处理。
        var frontCheck = await EnsureFrontHasUnitAsync(
            window, snapshot,
            deployed || assembled || BoughtNonXilianMember(m5Result), ct);
        if (frontCheck is null)
        {
            // 1.2.105（15:54 局实弹：067 命中局开局快照连续 3 帧漏读备战席→部署段零
            // 候选→前台空判死，误弃命中局；对照 15:47 同环境开局备战席有 3 名可部署
            // 单位且数值读数完全相同）。判死前重读一次：读到备战席单位=漏读实锤→
            // 补跑一次部署段再终判。不对称风险与 justActed 放行同款：误放行有
            // M1 弹窗+A9 兜底（可逆），误弃局不可逆。
            var deathReread = await SnapshotWithRetryAsync(window, ct);
            if (deathReread is not null && deathReread.BenchCharacterDetails.Count > 0)
            {
                emit("[决策层] 判死前重读发现备战席有单位（此前为漏读帧）——补跑一次部署段。");
                var (redeployed, _, _) = await DeployBondMembersAsync(window, deathReread, ct);
                frontCheck = await EnsureFrontHasUnitAsync(
                    window, deathReread, redeployed, ct);
            }
        }

        if (frontCheck is null)
        {
            // 1.2.58（架构审查 2-3）：M1 前置门——场上无人时出战必被"前台区域
            // 无角色"弹窗拦下，自 heal 循环烧时间。空场局判 Dead 走弃局重开。
            _lastAbandonReason = "空场判死:出战前置门前台无人"; // P2-2 弃局标签真实化
            return PreparationOutcome.Dead;
        }

        // P1-3（09-08 通宵：S2 M1 两败 ×5 实锤）：识别流停滞期禁硬启战斗——
        // 战斗状态机依赖帧流观察推进,停滞时启动必败。先复活并等至多 60 秒。
        await EnsureStreamReadyForBattleAsync(ct);
        var m1 = await SendAsync("M1 preparation_generic reward_shop",
            new GrailCommand(GrailCommandKind.M1,
                new GrailBattleArgs("preparation_generic", "reward_shop")), window, ct);
        if (m1.Error is not null)
        {
            // 1.2.106（16:53 局实弹兜底层）：祈愿弹框在屏时出战必失败——先应答弹框
            // （M3 检测器不在屏时不点击、无副作用）再重试一次出战，仍失败才判死弃局。
            var wishRetry = await SendAsync("M3", new GrailCommand(GrailCommandKind.M3), window, ct);
            if (wishRetry.Error is null
                && wishRetry.Payload is GrailWishOutcomeFact answered
                && answered.Responded)
            {
                emit("[决策层] M1 失败时祈愿弹框在屏并已应答——重试一次出战。");
                m1 = await SendAsync("M1 preparation_generic reward_shop",
                    new GrailCommand(GrailCommandKind.M1,
                        new GrailBattleArgs("preparation_generic", "reward_shop")), window, ct);
            }
        }

        if (m1.Error is not null)
        {
            // P3-G（2026-09-08 下午批：两败局全程 血=? 对账盲区）：判死前强制读血——
            // 血量是"还剩多少败北空间"的唯一依据，读不到也要在弃局标签里显式声明盲区。
            var healthCheck = await SnapshotWithRetryAsync(window, ct);
            if (healthCheck?.TeamHealth is { } knownHealth)
            {
                _lastKnownTeamHealth = knownHealth;
                _lastAbandonReason = $"出战失败:S2 M1 两败（含祈愿应答重试，判死时血={knownHealth}）";
            }
            else
            {
                _lastAbandonReason = "出战失败:S2 M1 两败（含祈愿应答重试，判死时血量不可读）";
            }

            return PreparationOutcome.Dead; // 战斗未推进：外层弃局重开
        }

        _currentNode = "1-2"; // 1.2.89 节点锚点：M1 落地 reward_shop=进入 1-2
        executor.AllowGalaxyScholarPurchase = false; // 1.2.102：学者购买仅 1-1（S3 商店 pass 前置同步）

        // ---- S3：1-2（人口 3，进场先商店）----
        m5Result = await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        await SendAsync("M2", new GrailCommand(GrailCommandKind.M2), window, ct);
        snapshot = await SnapshotWithRetryAsync(window, ct);
        if (snapshot is null)
        {
            // P1-3 补全（1.2.74 实测）：02:40 命中局在 1-2 进场后 I10 连续 8 次全败
            //（M5 买到黑塔+商店/备战切换的识别流滞后）→ 好局又被 Interrupted 弃掉。
            // 与 S2 同款追帧长尾。
            emit("[决策层] S3 快照 5s 窗口全败——判定为识别流滞后，进入 60s 追帧长尾。");
            snapshot = await SnapshotWithRetrySlowTailAsync(window, ct);
        }

        if (snapshot is null)
        {
            snapshot = await TryRecoverSnapshotAsync(window, ct);
            if (snapshot is null)
            {
                _lastAbandonReason = "快照持续不可得（恢复预算耗尽）";
                return PreparationOutcome.Interrupted;
            }
        }

        // 1.2.66：此快照后无任何操作 → 复用给部署段（省一次背靠背 I10）。
        // P2-F：S3 档位基线=本快照（M5 之后取），M5 增量已含在基线内不再计入。
        int deployedBondDeltaS3 = 0;
        (deployed, deployedBondDeltaS3, _) = await DeployBondMembersAsync(window, snapshot, ct);
        assembled = await AssembleBadgeIfAvailableAsync(window, ct, hit067); // 1-2 新得徽补装（审查 P3）
        // P2-F（2026-09-09 修复批）：与 S2 同款跨档门控（基线含 M5 效果）。
        var wishExpectedS3 = IsWishWindowRequired(
            snapshot, deployedBondDeltaS3, assembled,
            includeM5Delta: false, m5Result: null,
            applyRecentMutationDoor: true);
        await EnsureWishAnsweredAsync(window, ct,
            maxProbes: wishExpectedS3 ? 8 : 1,
            confirmWithDetector: wishExpectedS3);
        // P2-1（1.2.71 运行时审计）：justActed 纳入"执行器内部上场"——M5 买到成员的
        // 上场动画同样会让立即 I10 读到空前台（19:17 误弃好局同款），前置门按动画期处理。
        if (await EnsureFrontHasUnitAsync(
                window, snapshot,
                deployed || assembled || BoughtNonXilianMember(m5Result), ct) is null)
        {
            return PreparationOutcome.Dead;
        }

        // 凑息（1.2.116 用户拍板）：1-2 出战前金币<10 → 卖备战席冗余卡凑 ≥10，
        // 1-3 开局多得 1 金利息。在部署/弹框应答之后、M1 之前执行（金币已定型）。
        await TopUpInterestGoldBeforeBattleAsync(window, snapshot, ct);

        // P1-3（09-08 通宵）：同 S2——识别流停滞期禁硬启战斗。
        await EnsureStreamReadyForBattleAsync(ct);
        m1 = await SendAsync("M1 preparation_generic investment_strategy",
            new GrailCommand(GrailCommandKind.M1,
                new GrailBattleArgs("preparation_generic", "investment_strategy")), window, ct);
        if (m1.Error is not null)
        {
            // 1.2.106（审查 P2 当场修）：与 S2 同款兜底——祈愿弹框在屏时出战必失败，
            // 先应答（M3 检测器不在屏时零副作用）再重试一次，仍失败才判死弃局。
            var wishRetryS3 = await SendAsync("M3", new GrailCommand(GrailCommandKind.M3), window, ct);
            if (wishRetryS3.Error is null
                && wishRetryS3.Payload is GrailWishOutcomeFact answeredS3
                && answeredS3.Responded)
            {
                emit("[决策层] S3 M1 失败时祈愿弹框在屏并已应答——重试一次出战。");
                m1 = await SendAsync("M1 preparation_generic investment_strategy",
                    new GrailCommand(GrailCommandKind.M1,
                        new GrailBattleArgs("preparation_generic", "investment_strategy")), window, ct);
            }
        }

        if (m1.Error is not null)
        {
            // P3-G（同 S2）：判死前强制读血，血量进弃局标签（对账不再缺失）。
            var healthCheckS3 = await SnapshotWithRetryAsync(window, ct);
            if (healthCheckS3?.TeamHealth is { } knownHealthS3)
            {
                _lastKnownTeamHealth = knownHealthS3;
                _lastAbandonReason = $"出战失败:S3 M1 两败（含祈愿应答重试，判死时血={knownHealthS3}）";
            }
            else
            {
                _lastAbandonReason = "出战失败:S3 M1 两败（含祈愿应答重试，判死时血量不可读）";
            }

            return PreparationOutcome.Dead; // 战斗未推进：外层弃局重开
        }

        _currentNode = "1-3"; // 1.2.89 节点锚点：M1 落地 investment_strategy=进入 1-3
        executor.AllowGalaxyScholarPurchase = false; // 1.2.102：学者购买仅 1-1

        // ---- S4：投资策略（禁选阿哈大悦已内置于 M7）----
        var m7 = await SendAsync("M7", new GrailCommand(GrailCommandKind.M7), window, ct);
        // 1.2.66：M7 回执已含"验离页"（选中+确认+离页验证），500ms 页面稳定余量足够
        //（原固定 2 秒无验证判据支撑）。
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        await EnsureWishAnsweredAsync(window, ct);

        // 1.2.119（审计簇 D1/方案 FIX_PLAN 八.P1-2，rule 四.13a）：策略弃局判定——
        // 三条件"且"：goal=全员 ∧ 1-2 血≤86（S3 最后快照缓存）∧ 选定策略≠二极管276。
        // **单人模式永不触发**（rule 四.13a 原文；勘误：审计原判 5 例"该弃未弃"系
        // 漏核目标模式不变量，单人下不弃=合规）。数据源=M7 回执 SelectedStrategyId
        //（M7 返回即判，不等快照——消灭审计局 1 的 90 秒白花）；血量 null=F13 防御
        // 不弃留痕。触发=事件 StrategyAbandonDecided+A9（reason 带血/策略，簇 A 新链）。
        if (_goal == GrailUserGoal.All
            && m7.Error is null
            && m7.Payload is RewardStageAutomationResult m7Result)
        {
            var selectedStrategyId = m7Result.SelectedStrategyId;
            if (string.IsNullOrEmpty(selectedStrategyId))
            {
                // P1-2（对抗审查）：SoftFallbackLeftmost 已带 ID，但识别降级路径 ID 未知——
                // 原实现在此整段静默跳过零留痕。防御不弃+显式留痕。
                emit("[决策层] StrategyAbandonDecided 未触发：M7 回执未带选定策略 ID" +
                     "（识别降级/兜底路径）——策略未知，防御不弃，留痕。");
            }
            else if (!string.Equals(
                selectedStrategyId,
                GrailInvestmentStrategyDecider.DiodeId,
                StringComparison.OrdinalIgnoreCase))
            {
                var healthAtS3 = _lastKnownTeamHealth;
                if (healthAtS3 is > 0 and <= 86)
                {
                    // P2-1（对抗审查）：本分支只置真实原因并返回 Interrupted，A9 由外层
                    // 单点发送——原实现自发 A9 后外层再发一条默认 R3 标签的第二条 A9
                    //（双重弃局+第二条落在主界面上 Esc）。FIX_PLAN 八.P1-2 数据源贯通。
                    _lastAbandonReason =
                        $"策略弃局:血{healthAtS3}≤86/策略{selectedStrategyId}";
                    publishEvent?.Invoke(
                        "StrategyAbandonDecided",
                        $"goal=全员 血={healthAtS3}≤86 策略={selectedStrategyId}≠二极管——弃局重刷");
                    emit($"[决策层] 策略弃局判定成立（goal=全员 血={healthAtS3}≤86 " +
                         $"策略={selectedStrategyId}≠二极管）——立即弃局重刷。");
                    return PreparationOutcome.Interrupted; // 外层按 _lastAbandonReason 发 A9（不重入 S5）
                }

                emit($"[决策层] 策略弃局未触发：血量={healthAtS3?.ToString() ?? "未知"}" +
                     "（>86 或不可知，F13 防御口径不弃，已留痕）。");
            }
        }

        // ---- S4 顺序（用户 2026-09-06 第 4 次重申强制令，最终口径）：
        // 选投资策略 → 游戏强制弹出的商店**必须扫描并购买**（绝不直接关掉不扫）→
        // 开金矿（必须开完）→ 卖角色清场 → M5 圣杯循环。
        // 1.2.108（18:05 局实弹：弹出店货架被槽位识别半帧误读——吉尔伽美什在架上
        // 却识别成别的名单→零购买；金矿 5 球检测后仍有漏开）：裸 M5 买到 0 且货架
        // 含未识别槽位=识别半帧实锤，重发一次裸 M5 重扫（有界一次，不刷新）。 ----
        var poppedShop = await SendAsync("M5", new GrailCommand(GrailCommandKind.M5), window, ct);
        // 1.2.108 审查 P1 修正：货架名单里不存在"未识别"字样（识别失败槽位在 Fact
        // 组装时被整体剔除，全空时 ShelfCharacterNames=null）——半帧信号=槽位数<5。
        if (poppedShop.Error is null
            && poppedShop.Payload is GrailShopPassFact poppedFact
            && (poppedFact.BoughtCharacterNames?.Count ?? 0) == 0
            && (poppedFact.ShelfCharacterNames?.Count ?? 0) < 5)
        {
            emit("[决策层] 弹出商店首轮识别不完整（货架槽位数<5）且零购买——静置后重扫一次。");
            await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
            await SendAsync("M5 rescan", new GrailCommand(GrailCommandKind.M5), window, ct);
        }

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
                    snapshot = await TryRecoverSnapshotAsync(window, ct);
                    if (snapshot is null)
                    {
                        _lastAbandonReason = "快照持续不可得（恢复预算耗尽）";
                        return PreparationOutcome.Interrupted;
                    }
                }
            }
        }

        // 1.2.102（用户第四次重申，最终口径）：一到 1-3 就全员清场——除命杯成员/
        // 星徽佩戴者/五费外全部卖掉一个不留（不再"金<8 才卖"）；保留线=未携带星徽数
        // （拿到命运圣杯星徽→少卖一个作装配载体，A4 装配段消费该候选）。
        await SellRedundantsAsync(window, snapshot, ct);

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
                        snapshot = await TryRecoverSnapshotAsync(window, ct);
                        if (snapshot is null)
                        {
                            _lastAbandonReason = "快照持续不可得（恢复预算耗尽）";
                            return PreparationOutcome.Interrupted;
                        }
                    }
                }
            }

            // 1.2.66：复用上方快照（收店救援分支若发生点击，快照已在救援内重读更新）；
            // 买到非昔涟成员且部署段未部署时外层补轮询（4×3s）捕获延迟弹出的升档框，
            // 其余轮次单查——原无差别 6×3s 轮询是每轮 ~20 秒纯等待的主源。
            // 1.2.68：部署段带回的最新快照直接覆盖 snapshot——S7 终局判定/卖人立即
            // 反映部署结果，不再晚一轮（命中收工时曾多跑一整轮 M5）。
            // P2-F（2026-09-09 修复批）：跨档门控与本环部署前快照（M5 之后取，M5 增量
            // 已在内）+部署段成员数联判——存在已激活未应答档位才开 8 次窗（延迟弹出的
            // 升档框仍被"档位>已应答"条件覆盖），其余单查。
            var s5TierBaseline = snapshot;
            var (_, loopBondDelta, latestFromDeploy) =
                await DeployBondMembersAsync(window, snapshot, ct);
            snapshot = latestFromDeploy ?? snapshot;
            var loopWishWindow = IsWishWindowRequired(
                s5TierBaseline, loopBondDelta, badgeAssembled: false,
                includeM5Delta: false, m5Result: null,
                applyRecentMutationDoor: true);
            await EnsureWishAnsweredAsync(window, ct,
                maxProbes: loopWishWindow ? 8 : 1,
                confirmWithDetector: loopWishWindow);

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
                    // 1.2.89（用户令第 5 问题 R3 口径）：真山穷水尽=可卖单位为零。
                    // 快照仍报有可卖（身份异常拒卖等）时绝不判死——复核后再卖一轮；
                    // 仍卖不动才认输（有界，防死循环）。
                    // 1.2.116 审查 P2-2：梯压至 5s 后，识别流中等滞后（实测 7-19s）原本能在
                    // 15s 窗内自愈放弃 R3 的部分会落进全败——判死前必须长尾承接（误弃不可逆）。
                    var freshBeforeSurrender = await SnapshotWithRetryAsync(window, ct)
                        ?? await SnapshotWithRetrySlowTailAsync(window, ct);
                    if (freshBeforeSurrender is not null
                        && freshBeforeSurrender.SellableBeyondKeepLineCount > 0)
                    {
                        emit("[决策层] 金不足但快照确认仍有可卖单位——复核后再卖一轮，不判 R3。");
                        var retried = await SellRedundantsAsync(window, freshBeforeSurrender, ct);
                        snapshot = await SnapshotWithRetryAsync(window, ct) ?? freshBeforeSurrender;
                        sold = retried;
                    }

                    if (snapshot.Gold < snapshot.RefreshGoldCost && sold == 0)
                    {
                        if (freshBeforeSurrender is null)
                        {
                            // 审查 P2-2（09-10 夜审）：判死前复核帧不可得（识别流滞后/
                            // 帧龄门拦截）时，snapshot.Gold 可能是滞后读数——误弃不可
                            // 逆，本轮防御跳过判死，运营循环下一轮重查（有界 30 轮兜底）。
                            emit("[决策层] R3 候选但判死前复核帧不可得（识别滞后）——本轮防御跳过判死。");
                        }
                        else
                        {
                            // 审查 F3：手头有新鲜复核帧时以其金币为准（snapshot 可能是
                            // 2368 行回填的滞后快照，判死依据不持反证新鲜帧）。
                            var goldForVerdict = freshBeforeSurrender.Gold;
                            if (goldForVerdict < snapshot.RefreshGoldCost && sold == 0)
                            {
                                emit("[决策层] R3：金币耗尽且无可卖（新鲜复核帧裁定）——弃局重开。");
                                return PreparationOutcome.Dead;
                            }

                            emit("[决策层] 新鲜复核帧显示金币仍足（金=" + goldForVerdict + "）——不判 R3，继续运营。");
                        }
                    }
                }
            }
        }

        _lastAbandonReason = "运营轮上限（异常兜底）"; // P2-2 弃局标签真实化
        return PreparationOutcome.Dead; // 运营轮上限（异常兜底）
    }
}
