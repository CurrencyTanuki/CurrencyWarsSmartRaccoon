using System.Security.Cryptography;
using System.Text;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

public sealed record Phase2TrackingUpdate(
    Phase2OperationalState Current,
    bool PersistentStateConfirmed,
    FinalNodeBattleState? FinalizedBattle,
    string Message,
    string? Diagnostic = null,
    bool PageChanged = false,
    bool NewRunBoundaryConfirmed = false);

public sealed class Phase2OperationalStateTracker(int confirmationFrames = 2)
{
    private const string WalterCharacterId = "currency_wars_character_20";
    private const int MaximumImmediateActionDrop = 40;
    private const int MaximumConfirmedActionDrift = 10;
    private const int ActionDropRecoveryWindow = 4;
    private const int MaximumSettlementCollectionFrames = 12;
    private readonly int _confirmationFrames = confirmationFrames >= 2
        ? confirmationFrames
        : throw new ArgumentOutOfRangeException(nameof(confirmationFrames));
    // 动画干扰降频器（用户 2026-08-07 周期制方案）：备战页部分槽位
    // 因动画（放置/特效/打call）持续识别失败。策略：连续失败 >=3 个周期
    //（周期=强刷全量识别间隔）后，该槽位进入降频模式——合并时保留旧
    // Known、抑制 Uncertainty 噪声，每 5 帧放行一次尝试；动画结束后
    // 很快恢复正常（不放弃、不停止）。
    private const int SlotFailureStreakThreshold = 3;
    private const int SlotRetryGateFrames = 5;
    private readonly Dictionary<string, int> _slotFailureStreak = [];
    private readonly Dictionary<string, int> _slotRetryGate = [];
    private string? _pendingFingerprint;
    private int _pendingCount;
    private Phase2PageFamily _confirmedPage;
    /// <summary>
    /// 战斗开始去抖：终结技/过场会暂时隐藏战斗锚点，页面在 Battle 与
    /// Unknown 之间抖动，10 秒内不重复确认战斗开始（日志曾反复触发 8 次）。
    /// </summary>
    private static readonly TimeSpan BattleStartDebounce = TimeSpan.FromSeconds(10);
    private DateTimeOffset _lastBattleConfirmedAt;
    private Phase2PageFamily _pendingPage;
    private int _pendingPageCount;
    private Phase2OperationalState? _latestBattleDamageFrame;
    private Phase2OperationalState? _latestBattleActionFrame;
    private Phase2OperationalState? _pendingBattleActionFrame;
    private Phase2OperationalState? _pendingRoundBoundaryActionFrame;
    private Phase2OperationalState? _actionDropRollbackAnchor;
    private Phase2OperationalState? _pendingActionRecoveryFrame;
    private int _actionDropRecoveryFramesRemaining;
    private Phase2OperationalState? _latestBattleContextFrame;
    private string? _activeBattleNode;
    private bool _activeBattleHasWalter;
    private int? _activeBattleWalterStarLevel;
    private int _acceptedBattleActionIncrease;
    private int _activeBattleReliableActionSamples;
    private bool _activeBattleIsRewardNode;
    private Phase2OperationalState? _lastConfirmedState;
    /// <summary>最近备战帧的阵容（瓦尔特识别兜底：战斗帧 formation 为空、
    /// _lastConfirmedState 已被战斗帧覆盖时，瓦尔特仍能从本节点备战阵容识别——
    /// 用户规则：瓦尔特在前台才算）。</summary>
    private IReadOnlyList<FormationCharacterState>? _latestPreparationFormation;
    /// <summary>
    /// 最近一次"连续确认"的备战节点及其帧快照——奖励关/无战斗节点封存
    /// 兜底：节点 N 的备战被确认后，若观察到后继节点 S（TryGetPreviousNode(S)==N）
    /// 且期间没有任何战斗/结算证据，则用此帧降级封存 N，保证奖励关
    /// 不因无战斗页而从历史消失（用户合同："奖励节点即使没有普通战斗页，
    /// 也不能因此永久丢失"）。
    /// </summary>
    private string? _confirmedPreparationNode;
    private Phase2OperationalState? _confirmedPreparationFrame;
    private string? _pendingPreparationNode;
    private int _pendingPreparationNodeCount;
    private string? _lastFinalizedNode;
    private Phase2OperationalState? _bestSettlementFrame;
    private int _settlementFramesObserved;
    private string? _pendingSettlementFingerprint;
    private int _pendingSettlementCount;
    private readonly Dictionary<int, int> _settlementGoldVotes = [];
    private string? _pendingHealthKey;
    private int? _pendingHealthValue;
    private int _pendingHealthCount;
    private readonly Dictionary<string, int> _confirmedPreparationHealth =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _provisionalPreparationHealth =
        new(StringComparer.OrdinalIgnoreCase);
    private int? _activeBattlePreHealth;
    private int? _activeBattlePostHealth;
    private double _activeBattlePostHealthConfidence;
    private bool _activeBattlePostHealthFromSettlement;
    private bool _suspectedNewRunPreparationSeen;
    private bool _suspectedNewRunBattleSeen;
    private bool _suspectedNewRunSettlementSeen;
    private bool _suspectedNewRunBoundaryReady;
    private readonly Dictionary<string, string> _temporaryIds =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _temporaryCounters =
        new(StringComparer.Ordinal);

    public string? ActiveBattleNode => _activeBattleNode;
    public string? LastFinalizedNode => _lastFinalizedNode;
    internal IReadOnlyList<string> ActiveBattleEvidenceSourceIds =>
        new[]
            {
                _latestBattleDamageFrame,
                _latestBattleActionFrame,
                _pendingBattleActionFrame,
                _pendingRoundBoundaryActionFrame,
                _actionDropRollbackAnchor,
                _pendingActionRecoveryFrame,
                _latestBattleContextFrame,
                _bestSettlementFrame
            }
            .Where(state => state is not null)
            .SelectMany(state =>
                Phase2FinalEvidenceSourceCollector.Collect(state!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public FinalNodeBattleState? CompleteFailedRun()
    {
        var final = CompletePendingBattle();
        if (final is null)
        {
            return null;
        }

        var damage = final.SelectedDamage ?? final.TotalDamage;
        return final with
        {
            RemainingActionValue = RemainingActionValueState.Create(0, 0),
            PostBattleHealth = null,
            HealthDelta = null,
            HealthDepleted = true,
            ClearStatus = NodeClearStatus.NotPerfect,
            TheoreticalDamageLimit = damage,
            TheoreticalDamageQuality = damage.HasValue
                ? TheoreticalDamageQuality.ActionExhausted
                : TheoreticalDamageQuality.Unknown,
            TheoreticalDamageRule = damage.HasValue
                ? "challenge-failed page: health was depleted and action ended; limit equals final damage"
                : "challenge-failed page confirmed defeat, but final damage is unavailable",
            Uncertainty = final.FinalUncertainty
                .Append("Challenge-failed page confirms health depletion; exact health delta is unavailable and was not inferred.")
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    public FinalNodeBattleState? CompletePendingBattle(
        Observation<int>? completionHealth = null)
    {
        if (completionHealth?.Status == ObservationStatus.Known &&
            completionHealth.Value is >= 0 and <= 100 &&
            (!_activeBattlePostHealthFromSettlement ||
             completionHealth.Confidence >= _activeBattlePostHealthConfidence))
        {
            _activeBattlePostHealth = completionHealth.Value;
            _activeBattlePostHealthConfidence = completionHealth.Confidence;
            _activeBattlePostHealthFromSettlement = true;
        }

        var context = _latestBattleContextFrame ??
                      _latestBattleDamageFrame ??
                      _latestBattleActionFrame;
        if (context is null ||
            new[]
                {
                    _latestBattleDamageFrame,
                    _latestBattleActionFrame,
                    context,
                    _bestSettlementFrame
                }
                .All(item =>
                    item?.NodeId.Status != ObservationStatus.Known ||
                    string.IsNullOrWhiteSpace(item.NodeId.Value)))
        {
            return null;
        }

        var battleFrame = CombineBattleFrames(
            _latestBattleDamageFrame,
            _latestBattleActionFrame,
            context);
        var finalized = FinalizeBattle(
            battleFrame,
            _bestSettlementFrame,
            _activeBattlePreHealth,
            _activeBattlePostHealth,
            _activeBattleHasWalter,
            _activeBattleWalterStarLevel,
            _acceptedBattleActionIncrease,
            _activeBattleReliableActionSamples,
            _activeBattleIsRewardNode);
        if (string.Equals(finalized.NodeId, _lastFinalizedNode, StringComparison.Ordinal))
        {
            finalized = null;
        }
        else
        {
            _lastFinalizedNode = finalized.NodeId;
        }

        _latestBattleDamageFrame = null;
        _latestBattleActionFrame = null;
        _pendingBattleActionFrame = null;
        _pendingRoundBoundaryActionFrame = null;
        ClearActionDropRecovery();
        _latestBattleContextFrame = null;
        _activeBattleNode = null;
        // 节点已完成：清空最近备战阵容，防止瓦尔特跨节点残留
        //（节点 A 前台瓦尔特 → 节点 B 备战 formation 全空时误标瓦尔特）。
        _latestPreparationFormation = null;
        _activeBattleHasWalter = false;
        _activeBattleWalterStarLevel = null;
        _acceptedBattleActionIncrease = 0;
        _activeBattleReliableActionSamples = 0;
        _activeBattleIsRewardNode = false;
        _activeBattlePreHealth = null;
        _activeBattlePostHealth = null;
        _activeBattlePostHealthConfidence = 0;
        _activeBattlePostHealthFromSettlement = false;
        ClearSettlementCollection();
        return finalized;
    }

    public Phase2TrackingUpdate Observe(
        Phase2OperationalState frame,
        Observation<int>? playerHealth = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (IsRewardShopOverlay(frame))
        {
            // 商店弹窗页用紧凑布局坐标识别阵容（RewardShopCharacterSlots1920），
            // 其槽位坐标与备战页不同。若让商店帧的 formation 进入任何合并路径
            // （含 CarryForwardStale/MergeConfirmedFields 的 previous==null 短路、
            // AssignStableTemporaryIds 的临时 ID 分配），紧凑坐标槽位会混入后续
            // 备战帧合并结果 → 阵容/装备永远 Unknown（实测 023158）。
            // 在入口最先置空：紧凑坐标槽位绝不可能进入状态或临时 ID 表。
            frame = frame with
            {
                Formation = Observation<IReadOnlyList<FormationCharacterState>>.Unknown(
                    "reward shop overlay obscures the formation")
            };
        }

        frame = AssignStableTemporaryIds(frame);
        if (frame.PageFamily is Phase2PageFamily.Unknown or
            Phase2PageFamily.Transition)
        {
            // 字段指纹确认必须清零（当前帧字段不可信），但页面确认进度
            // （_pendingPage/_pendingPageCount）必须保留：单帧识别失败
            // （如备战页动画瞬间模板 miss 判 __classifier-miss__）不应把
            // 已积累的页面确认计数清零，否则"重新进入对局识别恢复慢"——
            // 备战页第一帧确认后一帧 Unknown 就从头再来，延迟十几秒
            // （实测 09:41:23 已识别备战 → 09:41:27 Unknown → 09:41:41 才确认）。
            // 页面确认只在同页面连续帧递增、不同页面帧会重置，故保留安全。
            _pendingFingerprint = null;
            _pendingCount = 0;
            return new Phase2TrackingUpdate(
                CarryForwardStale(frame, _lastConfirmedState),
                false,
                null,
                "当前帧无法可靠识别或处于过场；已安全跳过，等待后续画面恢复。");
        }

        ObserveHealth(frame, playerHealth);
        ObserveSuspectedNewRun(frame);
        // 节点未知的战斗帧（如"竞争对手生成中"过场被模板误匹配为战斗页）
        // 不确认"战斗开始"，避免假"未知节点 战斗开始"与错误的战斗状态，
        // 否则后续备战快速切换时会因上一状态是战斗而漏抓关键帧。
        var effectivePage =
            frame.PageFamily == Phase2PageFamily.Battle &&
            frame.NodeId.Status != ObservationStatus.Known
                ? Phase2PageFamily.Unknown
                : frame.PageFamily;
        var pageChanged = ObservePage(effectivePage);
        if (frame.PageFamily == Phase2PageFamily.Battle)
        {
            ObserveBattleFrame(frame);
        }
        else if (frame.PageFamily == Phase2PageFamily.BattleSettlement)
        {
            ObserveSettlementFrame(frame);
        }

        // 备战帧：保存最近备战阵容（战斗帧瓦尔特识别的兜底来源；
        // UpdateActiveBattleWalter 只在战斗帧调用，备战帧需在此独立保存）。
        if (frame.PageFamily == Phase2PageFamily.Preparation &&
            frame.Formation.Value is { Count: > 0 })
        {
            _latestPreparationFormation = frame.Formation.Value;
        }

        // 节点级备战确认（与字段指纹无关——阵容在备战期逐帧变化，
        // PersistentFingerprint 的 2 帧确认几乎不可能达成）：同一节点 ID
        // 连续 2 帧 Known 即视为"该节点备战已被确认"，供第五 exit
        // （节点推进封存兜底）使用。离开备战页时计数清零，防止跨节点
        // 残留（如 1-2 单帧备战不满足 2 帧，不会把 1-1 的确认覆盖掉）。
        if (frame.PageFamily == Phase2PageFamily.Preparation &&
            frame.NodeId.Status == ObservationStatus.Known &&
            !string.IsNullOrWhiteSpace(frame.NodeId.Value))
        {
            if (string.Equals(
                    frame.NodeId.Value,
                    _pendingPreparationNode,
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingPreparationNodeCount++;
            }
            else
            {
                _pendingPreparationNode = frame.NodeId.Value;
                _pendingPreparationNodeCount = 1;
            }

            if (_pendingPreparationNodeCount >= _confirmationFrames)
            {
                _confirmedPreparationNode = frame.NodeId.Value;
                _confirmedPreparationFrame = frame;
            }
        }
        else if (frame.PageFamily != Phase2PageFamily.Preparation)
        {
            _pendingPreparationNode = null;
            _pendingPreparationNodeCount = 0;
        }

        var fingerprint = PersistentFingerprint(frame);
        if (string.Equals(fingerprint, _pendingFingerprint, StringComparison.Ordinal))
        {
            _pendingCount++;
        }
        else
        {
            _pendingFingerprint = fingerprint;
            _pendingCount = 1;
        }

        var confirmed = _pendingCount >= _confirmationFrames;
        // 重开边界：1-1 备战连续确认即分段（玩家重开，旧局立即封存，
        // 新数据不与旧局混合）。不依赖第一战结算。
        var newRunBoundaryConfirmed =
            confirmed &&
            _suspectedNewRunBoundaryReady &&
            frame.PageFamily == Phase2PageFamily.Preparation &&
            frame.NodeId.Status == ObservationStatus.Known &&
            string.Equals(
                frame.NodeId.Value,
                "1-1",
                StringComparison.OrdinalIgnoreCase);
        if (newRunBoundaryConfirmed)
        {
            _suspectedNewRunBoundaryReady = false;
            _suspectedNewRunPreparationSeen = false;
            _suspectedNewRunBattleSeen = false;
            _suspectedNewRunSettlementSeen = false;
        }

        FinalNodeBattleState? finalized = null;
        int? finalizedRawAction = null;
        var hasPendingBattle =
            _latestBattleDamageFrame is not null ||
            _latestBattleActionFrame is not null ||
            _latestBattleContextFrame is not null ||
            _bestSettlementFrame is not null;
        var hasFinalizableNode = new[]
            {
                _latestBattleDamageFrame,
                _latestBattleActionFrame,
                _latestBattleContextFrame,
                _bestSettlementFrame
            }
            .Any(item =>
                item?.NodeId.Status == ObservationStatus.Known &&
                !string.IsNullOrWhiteSpace(item.NodeId.Value));
        var stableSettlementReady =
            frame.PageFamily == Phase2PageFamily.BattleSettlement &&
            _bestSettlementFrame is not null &&
            IsSettlementSummaryReady(frame) &&
            _pendingSettlementCount >= _confirmationFrames;
        var settlementBudgetExhausted =
            frame.PageFamily == Phase2PageFamily.BattleSettlement &&
            _bestSettlementFrame is not null &&
            _settlementFramesObserved >= MaximumSettlementCollectionFrames;
        var confirmedNonBattleExit =
            _pendingPageCount >= _confirmationFrames &&
            frame.PageFamily is not (
                Phase2PageFamily.Battle or
                Phase2PageFamily.BattleSettlement) &&
            _confirmedPage != Phase2PageFamily.Battle &&
            // 问题5修复：商店页/策略页被 MapPage 归为 Preparation，但它们是
            // 弹窗（可能遮住节点锚点），不是真正的备战页退出——不应触发
            // "非战斗退出"封存（否则 2-2 战斗未开始就被误封存）。
            !IsTransientOverlayPage(frame.PageId);
        var resolvedFinalNode = ResolvePendingBattleNode(frame);
        var reliableSettlementSuccessorExit =
            _bestSettlementFrame is not null &&
            IsImmediateSuccessorPreparation(frame, resolvedFinalNode);
        // 第五 exit（奖励关/无战斗节点兜底）：没有任何战斗/结算证据，但
        // ① 曾连续确认过节点 N 的备战，② 当前帧已推进到后继节点 S
        // （NodeId Known、置信>=0.65、TryGetPreviousNode(S)==N），
        // 则用 N 的最后确认备战帧降级封存 N。伤害无证据保持未记录；
        // 奖励关（1-1/1-2/1-8/2-6/3-6）按用户 2026-08-11 拍板硬规则
        // （必然过关、血量Δ+2、完美通关）。跳过/未观察的中间节点不补造。
        var advancedNodeId = frame.NodeId.Status == ObservationStatus.Known &&
                             frame.NodeId.Confidence >= 0.65
            ? frame.NodeId.Value
            : null;
        FinalNodeBattleState? advancedFinalized = null;
        if (!hasPendingBattle &&
            frame.PageFamily is Phase2PageFamily.Preparation or
                Phase2PageFamily.Battle or
                Phase2PageFamily.BattleSettlement &&
            _confirmedPreparationNode is { } advanceCandidateNode &&
            IsRewardNodeId(advanceCandidateNode) &&
            !string.IsNullOrWhiteSpace(advancedNodeId) &&
            RunResumePolicy.TryGetPreviousNode(
                advancedNodeId,
                out var expectedPreviousNode) &&
            _confirmedPreparationNode is { } confirmedPreparationNode &&
            string.Equals(
                expectedPreviousNode,
                confirmedPreparationNode,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                confirmedPreparationNode,
                _lastFinalizedNode,
                StringComparison.OrdinalIgnoreCase) &&
            _confirmedPreparationFrame is { } confirmedPreparationFrame)
        {
            var isRewardAdvance = IsRewardNodeId(confirmedPreparationNode);
            var advancedPreHealth = ResolvePreBattleHealth(confirmedPreparationNode);
            var advancedPostHealth = isRewardAdvance && advancedPreHealth.HasValue
                ? Math.Min(100, advancedPreHealth.Value + 2)
                : (int?)null;
            var advancedFrame = CombineBattleFrames(
                null,
                null,
                BindFinalizedNode(
                    confirmedPreparationFrame,
                    confirmedPreparationNode,
                    frame));
            advancedFinalized = FinalizeBattle(
                advancedFrame,
                null,
                advancedPreHealth,
                advancedPostHealth,
                false,
                null,
                0,
                0,
                false);
            if (!string.Equals(
                    advancedFinalized.NodeId,
                    _lastFinalizedNode,
                    StringComparison.OrdinalIgnoreCase))
            {
                _lastFinalizedNode = advancedFinalized.NodeId;
            }
            else
            {
                advancedFinalized = null;
            }

            _confirmedPreparationNode = null;
            _confirmedPreparationFrame = null;
            _latestPreparationFormation = null;
        }

        if (hasPendingBattle &&
            hasFinalizableNode &&
            (stableSettlementReady ||
             settlementBudgetExhausted ||
             reliableSettlementSuccessorExit ||
             confirmedNonBattleExit))
        {
            var battleFrame = CombineBattleFrames(
                _latestBattleDamageFrame,
                _latestBattleActionFrame,
                _latestBattleContextFrame ?? _bestSettlementFrame!);
            battleFrame = BindFinalizedNode(
                battleFrame,
                resolvedFinalNode,
                frame);
            var finalizedPreHealth = ResolvePreBattleHealth(resolvedFinalNode);
            finalizedRawAction = battleFrame.RemainingActionValue.Status ==
                                 ObservationStatus.Known
                ? battleFrame.RemainingActionValue.Value?.TotalActionValue
                : null;
            finalized = FinalizeBattle(
                battleFrame,
                _bestSettlementFrame,
                finalizedPreHealth,
                _activeBattlePostHealth,
                _activeBattleHasWalter,
                _activeBattleWalterStarLevel,
                _acceptedBattleActionIncrease,
                _activeBattleReliableActionSamples,
                _activeBattleIsRewardNode);
            if (string.Equals(finalized.NodeId, _lastFinalizedNode, StringComparison.Ordinal))
            {
                finalized = null;
            }
            else
            {
                _lastFinalizedNode = finalized.NodeId;
            }

            // 节点已完成：清空最近备战阵容，防止瓦尔特跨节点残留。
            _latestPreparationFormation = null;

            // 兜底：1-1 备战帧被快速跳过时，仍等 1-1 第一战结算后确认。
            if (finalized is not null &&
                string.Equals(finalized.NodeId, "1-1", StringComparison.OrdinalIgnoreCase) &&
                _suspectedNewRunPreparationSeen &&
                _suspectedNewRunBattleSeen &&
                _suspectedNewRunSettlementSeen)
            {
                newRunBoundaryConfirmed = true;
            }

            if (newRunBoundaryConfirmed)
            {
                _suspectedNewRunBoundaryReady = false;
                _suspectedNewRunPreparationSeen = false;
                _suspectedNewRunBattleSeen = false;
                _suspectedNewRunSettlementSeen = false;
            }

            _latestBattleDamageFrame = null;
            _latestBattleActionFrame = null;
            _pendingBattleActionFrame = null;
            _pendingRoundBoundaryActionFrame = null;
            ClearActionDropRecovery();
            _latestBattleContextFrame = null;
            _activeBattleNode = null;
            _activeBattleHasWalter = false;
            _activeBattleWalterStarLevel = null;
            _acceptedBattleActionIncrease = 0;
            _activeBattleReliableActionSamples = 0;
            _activeBattleIsRewardNode = false;
            _activeBattlePreHealth = null;
            _activeBattlePostHealth = null;
            _activeBattlePostHealthConfidence = 0;
            _activeBattlePostHealthFromSettlement = false;
            ClearSettlementCollection();
        }

        if (confirmed)
        {
            // 动画干扰降频（用户 2026-08-07）：仅备战页统计失败槽位，
            // 连续失败达阈值后降频（保留旧 Known + 抑制噪声 + 每 5 帧放行）。
            if (frame.PageFamily is Phase2PageFamily.Preparation &&
                (frame.Formation.Value is not null ||
                 frame.Formation.Status != ObservationStatus.Known))
            {
                UpdateSlotFailureThrottle(frame.Formation);
            }

            _lastConfirmedState = MergeConfirmedFields(_lastConfirmedState, frame);
        }

        var visibleState = CarryForwardStale(frame, _lastConfirmedState);
        var effectiveFinalized = finalized ?? advancedFinalized;
        var update = new Phase2TrackingUpdate(
            visibleState,
            confirmed,
            effectiveFinalized,
            effectiveFinalized is not null
                ? $"节点 {effectiveFinalized.NodeId} 的最后完整战斗帧已封存。"
                : pageChanged
                    ? PageTransitionMessage(
                        frame.PageFamily,
                        frame.NodeId.Value ?? _activeBattleNode)
                : confirmed
                    ? "状态已通过连续帧确认。"
                    : $"等待第 {_pendingCount + 1}/{_confirmationFrames} 帧确认。",
            PageChanged: pageChanged,
            NewRunBoundaryConfirmed: newRunBoundaryConfirmed);
        return update with
        {
            Diagnostic = effectiveFinalized?.HealthDelta is < 0
                ? $"Node {effectiveFinalized.NodeId}: health {effectiveFinalized.PreBattleHealth}->{effectiveFinalized.PostBattleHealth}; " +
                  $"raw terminal action {(finalizedRawAction?.ToString() ?? "unknown")} was corrected to 0."
                : null
        };
    }

    private void ObserveSuspectedNewRun(Phase2OperationalState frame)
    {
        // 重开判定：最后封存节点 rank>1 且回到备战页。
        // 节点号可能因快速切换/识别未稳定而未知（如新局 1-1 备战首帧），
        // 未知时仍标记重开边界（1-1 是唯一可能的回退节点），
        // 避免"重开没反应"：旧局不封存、新数据混入旧局。
        var nodeIsOneOne =
            frame.NodeId.Status == ObservationStatus.Known &&
            string.Equals(
                frame.NodeId.Value,
                "1-1",
                StringComparison.OrdinalIgnoreCase);
        if (!RunResumePolicy.TryGetNodeRank(_lastFinalizedNode, out var finalizedRank) ||
            finalizedRank <= 1 ||
            frame.PageFamily != Phase2PageFamily.Preparation ||
            (!nodeIsOneOne && frame.NodeId.Status == ObservationStatus.Known))
        {
            return;
        }

        if (frame.PageFamily == Phase2PageFamily.Preparation)
        {
            // 玩家重开：节点回到 1-1 备战即代表上一局结束、新对局开始。
            // 立即标记边界，不等第一战结算（旧逻辑仅在备战帧丢失时兜底）。
            _suspectedNewRunBoundaryReady = true;
            _suspectedNewRunPreparationSeen = true;
            _suspectedNewRunBattleSeen = false;
            _suspectedNewRunSettlementSeen = false;
        }
        else if (frame.PageFamily == Phase2PageFamily.Battle &&
                 _suspectedNewRunPreparationSeen)
        {
            _suspectedNewRunBattleSeen = true;
        }
        else if (frame.PageFamily == Phase2PageFamily.BattleSettlement &&
                 _suspectedNewRunBattleSeen)
        {
            _suspectedNewRunSettlementSeen = true;
        }
    }

    private string? ResolvePendingBattleNode(Phase2OperationalState boundaryFrame)
    {
        var candidate = _activeBattleNode ??
                        KnownNode(_latestBattleContextFrame) ??
                        KnownNode(_latestBattleDamageFrame) ??
                        KnownNode(_latestBattleActionFrame) ??
                        KnownNode(_bestSettlementFrame);
        if (boundaryFrame.PageFamily != Phase2PageFamily.Preparation ||
            boundaryFrame.NodeId.Status != ObservationStatus.Known ||
            string.IsNullOrWhiteSpace(boundaryFrame.NodeId.Value) ||
            !RunResumePolicy.TryGetPreviousNode(
                boundaryFrame.NodeId.Value,
                out var expectedPrevious))
        {
            return candidate;
        }

        if (string.Equals(
                candidate,
                expectedPrevious,
                StringComparison.OrdinalIgnoreCase))
        {
            return expectedPrevious;
        }

        var expectedFollowsLastFinalized =
            RunResumePolicy.TryGetNodeRank(_lastFinalizedNode, out var lastRank) &&
            RunResumePolicy.TryGetNodeRank(expectedPrevious, out var expectedRank) &&
            expectedRank == lastRank + 1;
        if (expectedFollowsLastFinalized &&
            (string.IsNullOrWhiteSpace(candidate) ||
             string.Equals(
                 candidate,
                 boundaryFrame.NodeId.Value,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return expectedPrevious;
        }

        return candidate;
    }

    private static string? KnownNode(Phase2OperationalState? frame) =>
        frame?.NodeId.Status == ObservationStatus.Known &&
        !string.IsNullOrWhiteSpace(frame.NodeId.Value)
            ? frame.NodeId.Value
            : null;

    /// <summary>
    /// 是否为临时弹窗页（商店/策略选择/出战人数不足等）：它们被归为
    /// Preparation 或 Unknown 家族，但不是真正的备战页退出——不应触发
    /// "非战斗退出"封存（问题5：商店打开遮住节点锚点导致 2-2 战斗未开始
    /// 就被误封存；incomplete_lineup_prompt 同理）。
    /// </summary>
    private static bool IsTransientOverlayPage(string? pageId) => pageId is
        "reward_shop" or "investment_strategy" or
        "incomplete_lineup_prompt" or "companion_selection";

    private static bool IsImmediateSuccessorPreparation(
        Phase2OperationalState frame,
        string? pendingNode) =>
        frame.PageFamily == Phase2PageFamily.Preparation &&
        frame.NodeId.Status == ObservationStatus.Known &&
        frame.NodeId.Confidence >= 0.65 &&
        !string.IsNullOrWhiteSpace(pendingNode) &&
        RunResumePolicy.TryGetPreviousNode(frame.NodeId.Value, out var previous) &&
        string.Equals(previous, pendingNode, StringComparison.OrdinalIgnoreCase);

    private static Phase2OperationalState BindFinalizedNode(
        Phase2OperationalState battleFrame,
        string? resolvedNode,
        Phase2OperationalState boundaryFrame)
    {
        if (string.IsNullOrWhiteSpace(resolvedNode) ||
            string.Equals(
                battleFrame.NodeId.Value,
                resolvedNode,
                StringComparison.OrdinalIgnoreCase))
        {
            return battleFrame;
        }

        var evidence = battleFrame.NodeId.Evidence
            .Concat(boundaryFrame.NodeId.Evidence)
            .Append(new EvidenceReference(
                "phase2-node-sequence",
                "derived:successor-preparation",
                $"Preparation {boundaryFrame.NodeId.Value} confirms prior battle {resolvedNode}.",
                boundaryFrame.NodeId.ObservedAt,
                boundaryFrame.NodeId.Confidence))
            .Distinct()
            .ToArray();
        return battleFrame with
        {
            NodeId = Observation<string>.Known(
                resolvedNode,
                Math.Clamp(boundaryFrame.NodeId.Confidence * 0.9, 0.65, 0.90),
                evidence,
                boundaryFrame.NodeId.ObservedAt)
        };
    }

    private int? ResolvePreBattleHealth(string? resolvedNode)
    {
        if (_activeBattlePreHealth.HasValue ||
            string.IsNullOrWhiteSpace(resolvedNode))
        {
            return _activeBattlePreHealth;
        }

        if (_confirmedPreparationHealth.TryGetValue(
                resolvedNode,
                out var confirmed))
        {
            return confirmed;
        }

        return _provisionalPreparationHealth.TryGetValue(
            resolvedNode,
            out var provisional)
            ? provisional
            : null;
    }

    private static string PageTransitionMessage(
        Phase2PageFamily page,
        string? nodeId)
    {
        var node = string.IsNullOrWhiteSpace(nodeId) ? "未知节点" : nodeId;
        return page switch
        {
            Phase2PageFamily.Preparation => $"进入备战节点 {node}。",
            Phase2PageFamily.Supply => $"进入补给节点 {node}。",
            Phase2PageFamily.Battle => $"节点 {node} 战斗开始。",
            Phase2PageFamily.BattleSettlement => $"节点 {node} 进入战斗结算。",
            _ => $"页面状态已切换为 {page}。"
        };
    }

    private void ObserveHealth(
        Phase2OperationalState frame,
        Observation<int>? playerHealth)
    {
        // 补给选择页（reward_shop）不是备战页：其血量不能当作备战血量采集，
        // 否则会污染上一节点回填与下一节点 pre-battle 血量。
        if (string.Equals(frame.PageId, "reward_shop", StringComparison.Ordinal))
        {
            return;
        }

        if (playerHealth is null ||
            playerHealth.Status != ObservationStatus.Known ||
            playerHealth.Value is < 0 or > 100 ||
            frame.NodeId.Status != ObservationStatus.Known ||
            string.IsNullOrWhiteSpace(frame.NodeId.Value) ||
            frame.PageFamily is not (
                Phase2PageFamily.Preparation or
                Phase2PageFamily.BattleSettlement))
        {
            return;
        }

        var node = frame.NodeId.Value;
        var value = playerHealth.Value;
        if (frame.PageFamily == Phase2PageFamily.BattleSettlement)
        {
            // The yellow challenge-success page is the authoritative post-battle
            // source. It can exist for only one captured frame, so a semantically
            // valid 0..100 reading is locked immediately and cannot later be
            // replaced by a preparation fallback of equal or lower quality.
            if (playerHealth.Confidence >= 0.70 &&
                (!_activeBattlePostHealthFromSettlement ||
                 playerHealth.Confidence > _activeBattlePostHealthConfidence))
            {
                _activeBattlePostHealth = value;
                _activeBattlePostHealthConfidence = playerHealth.Confidence;
                _activeBattlePostHealthFromSettlement = true;
            }

            return;
        }

        if (playerHealth.Confidence >= 0.85)
        {
            _provisionalPreparationHealth[node] = value;
            var pendingNode = ResolvePendingBattleNode(frame);
            if (!_activeBattlePostHealthFromSettlement &&
                IsImmediateSuccessorPreparation(frame, pendingNode))
            {
                _activeBattlePostHealth = value;
                _activeBattlePostHealthConfidence = playerHealth.Confidence;
            }
        }

        var key = $"{frame.PageFamily}|{node}|{value}";
        if (string.Equals(key, _pendingHealthKey, StringComparison.Ordinal))
        {
            _pendingHealthCount++;
        }
        else
        {
            _pendingHealthKey = key;
            _pendingHealthValue = value;
            _pendingHealthCount = 1;
        }

        if (_pendingHealthCount < _confirmationFrames ||
            _pendingHealthValue is null)
        {
            return;
        }

        if (frame.PageFamily == Phase2PageFamily.Preparation)
        {
            _confirmedPreparationHealth[node] = _pendingHealthValue.Value;
            if (!string.IsNullOrWhiteSpace(_activeBattleNode) &&
                !string.Equals(_activeBattleNode, node, StringComparison.OrdinalIgnoreCase) &&
                !_activeBattlePostHealthFromSettlement)
            {
                _activeBattlePostHealth = _pendingHealthValue.Value;
                _activeBattlePostHealthConfidence = playerHealth.Confidence;
            }

            return;
        }

        // Battle-settlement observations return above. Keep this branch empty so
        // future page-family additions cannot silently weaken source priority.
    }

    private Phase2OperationalState AssignStableTemporaryIds(
        Phase2OperationalState frame)
    {
        var nodeScope = frame.NodeId.Value ?? "unknown-node";
        var damage = frame.BattleDamage.Value?.Select(item =>
        {
            if (item.CanDriveDecisions)
            {
                _temporaryIds.Remove(
                    $"{nodeScope}|character-damage|{item.Rank}");
                return item;
            }

            var id = GetTemporaryId(
                nodeScope,
                "character",
                $"damage-row-{item.Rank}");
            return item with { CharacterId = id, TemporaryId = id };
        }).ToArray();
        var formation = frame.Formation.Value?.Select(item =>
        {
            // 横向特殊单位拥有可靠身份，但按规则不能驱动阵容决策。
            // 保留 special_unit_* 身份供记录/展示使用；不要因为
            // CanDriveDecisions=false 把它降级成 unknown 临时对象。
            if (item.CanDriveDecisions ||
                (item.Zone == FormationZone.Special &&
                 item.CharacterId.StartsWith(
                     "special_unit_",
                     StringComparison.Ordinal)))
            {
                return item;
            }

            var id = GetTemporaryId(
                nodeScope,
                "formation-unit",
                $"{item.Zone}-{item.SlotIndex}");
            return item with { CharacterId = id, TemporaryId = id };
        }).ToArray();
        var synergyDamage = frame.BattleSynergyDamage.Value?.Select(item =>
        {
            if (item.CanDriveDecisions)
            {
                _temporaryIds.Remove(
                    $"{nodeScope}|synergy-damage|{item.Rank}");
                return item;
            }

            var id = GetTemporaryId(
                nodeScope,
                "synergy",
                $"damage-row-{item.Rank}");
            return item with { SynergyId = id, TemporaryId = id };
        }).ToArray();
        var unresolvedDamage = frame.BattleUnresolvedDamage.Value?.Select(item =>
        {
            var id = GetTemporaryId(
                nodeScope,
                item.SourceKind == BattleDamageSourceKind.SpecialUnit
                    ? "special-unit"
                    : "damage-source",
                $"damage-row-{item.Rank}");
            return item with { TemporaryId = id };
        }).ToArray();
        var settlementDamage = frame.SettlementDamage.Value?.Select(item =>
        {
            if (item.CanDriveDecisions)
            {
                return item;
            }

            var id = GetTemporaryId(
                nodeScope,
                "character",
                $"settlement-damage-row-{item.Rank}");
            return item with { CharacterId = id, TemporaryId = id };
        }).ToArray();
        var pending = frame.PendingIcons.Select(item =>
        {
            if (!string.IsNullOrWhiteSpace(item.TemporaryId))
            {
                var objectType = item.RecognizedFields?.GetValueOrDefault("sourceType") ??
                                 item.Category.ToString();
                var id = GetTemporaryId(
                    nodeScope,
                    objectType,
                    item.SlotKey);
                return item with { TemporaryId = id };
            }

            return item;
        }).ToArray();

        return frame with
        {
            Formation = ReplacePartialValue(frame.Formation, formation),
            BattleDamage = ReplacePartialValue(frame.BattleDamage, damage),
            BattleSynergyDamage = ReplacePartialValue(
                frame.BattleSynergyDamage,
                synergyDamage),
            BattleUnresolvedDamage = ReplacePartialValue(
                frame.BattleUnresolvedDamage,
                unresolvedDamage),
            SettlementDamage = ReplacePartialValue(
                frame.SettlementDamage,
                settlementDamage),
            PendingIcons = pending
        };
    }

    private string GetTemporaryId(
        string nodeScope,
        string objectType,
        string slotKey)
    {
        var key = $"{nodeScope}|{objectType}|{slotKey}";
        if (_temporaryIds.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var normalizedType = objectType switch
        {
            "character" => "unknown-character",
            "synergy" => "unknown-synergy",
            "special-unit" => "unknown-special-unit",
            "damage-source" => "unknown-damage-source",
            "formation-unit" => "unknown-formation-unit",
            _ => "unknown-object"
        };
        var next = _temporaryCounters.GetValueOrDefault(normalizedType) + 1;
        _temporaryCounters[normalizedType] = next;
        var id = $"{normalizedType}-{next}";
        _temporaryIds[key] = id;
        return id;
    }

    private static Observation<IReadOnlyList<T>> ReplacePartialValue<T>(
        Observation<IReadOnlyList<T>> observation,
        IReadOnlyList<T>? value) =>
        value is null
            ? observation
            : observation with { Value = value };

    private bool ObservePage(Phase2PageFamily page)
    {
        if (page == _pendingPage)
        {
            _pendingPageCount++;
        }
        else
        {
            _pendingPage = page;
            _pendingPageCount = 1;
        }

        if (_pendingPageCount < _confirmationFrames || _confirmedPage == page)
        {
            return false;
        }

        // 战斗开始去抖：Battle 与 Unknown 之间抖动时不重复触发战斗开始。
        if (page == Phase2PageFamily.Battle &&
            DateTimeOffset.UtcNow - _lastBattleConfirmedAt < BattleStartDebounce)
        {
            _pendingPage = _confirmedPage;
            _pendingPageCount = 1;
            return false;
        }

        _confirmedPage = page;
        if (page == Phase2PageFamily.Battle)
        {
            _lastBattleConfirmedAt = DateTimeOffset.UtcNow;
        }
        return true;
    }

    /// <summary>
    /// 商店弹窗页（reward_shop）用紧凑布局坐标识别阵容（RewardShopCharacterSlots1920），
    /// 其槽位坐标与备战页（PreparationCharacterSlots1920）不同。若把商店帧的
    /// formation 按 (Zone, SlotIndex) 与备战帧合并，会把紧凑坐标的槽位混入备战
    /// 数据、坐标错位且 Uncertain 保留，导致阵容/装备永远 Unknown（实测 023158）。
    /// 商店帧不参与 formation 合并：保留备战页最后可靠值并标记过期，
    /// 备战帧恢复后正常合并（用户架构：记录员在备战页采集阵容）。
    /// </summary>
    private static bool IsRewardShopOverlay(Phase2OperationalState state) =>
        string.Equals(
            state.PageId,
            "reward_shop",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 商店帧的 formation 不进入状态：保留备战页最后可靠值（标 stale），
    /// 若无可靠值则置空 Unknown——紧凑坐标槽位绝不被后续备战帧合并继承。
    /// </summary>
    private static Observation<IReadOnlyList<FormationCharacterState>>
        IsolateFromRewardShopFormation(
            Observation<IReadOnlyList<FormationCharacterState>> previous) =>
        previous.Status is ObservationStatus.Known or ObservationStatus.Stale &&
        previous.Value is { Count: > 0 }
            ? RunCheckpointFactory.KeepLastReliable(
                previous,
                Observation<IReadOnlyList<FormationCharacterState>>.Unknown(
                    "reward shop overlay"))
            : Observation<IReadOnlyList<FormationCharacterState>>.Unknown(
                "reward shop overlay obscures the formation");

    private static Phase2OperationalState CarryForwardStale(
        Phase2OperationalState current,
        Phase2OperationalState? previous)
    {
        if (previous is null)
        {
            return current;
        }

        return current with
        {
            NodeId = Carry(current.NodeId, previous.NodeId, "节点"),
            EnemyDifficulty = Carry(
                current.EnemyDifficulty,
                previous.EnemyDifficulty,
                "敌人难度"),
            StoreLevel = Carry(
                current.StoreLevel,
                previous.StoreLevel,
                "商店等级"),
            Interest = Carry(current.Interest, previous.Interest, "利息"),
            CumulativeSpend = Carry(
                current.CumulativeSpend,
                previous.CumulativeSpend,
                "累计消费"),
            PlayerProgress = Carry(
                current.PlayerProgress,
                previous.PlayerProgress,
                "玩家进度"),
            Formation = IsRewardShopOverlay(current)
                ? IsolateFromRewardShopFormation(previous.Formation)
                : RunCheckpointFactory.MergeFormationObservations(
                    previous.Formation,
                    current.Formation,
                    markCurrentUnavailableAsStale: true),
            ActiveSynergies = Carry(
                current.ActiveSynergies,
                previous.ActiveSynergies,
                "已激活羁绊"),
            DismantleToolCount = Carry(
                current.DismantleToolCount,
                previous.DismantleToolCount,
                "拆解工具"),
            SimpleEquipmentIds = Carry(
                current.SimpleEquipmentIds,
                previous.SimpleEquipmentIds,
                "简易装备"),
            SpecialItemIds = Carry(
                current.SpecialItemIds,
                previous.SpecialItemIds,
                "特殊物品"),
            InventorySlots = RunCheckpointFactory.MergeInventoryObservations(
                previous.InventorySlots,
                current.InventorySlots,
                markCurrentUnavailableAsStale: true),
            NegativeAffixIds = MergeStableFixedList(
                previous.NegativeAffixIds,
                current.NegativeAffixIds,
                Phase2RecognitionRegions.NegativeAffixSlots.Count,
                "negative affixes"),
            InvestmentEnvironmentId = MergeStableScalar(
                previous.InvestmentEnvironmentId,
                current.InvestmentEnvironmentId,
                "investment environment"),
            InvestmentStrategyIds = MergeMonotonicList(
                previous.InvestmentStrategyIds,
                current.InvestmentStrategyIds)
        };
    }

    /// <summary>
    /// 动画干扰降频统计（用户 2026-08-07 周期制方案）：
    /// 备战页每个槽位若当前帧识别失败（Unknown/低置信），累计连续失败
    /// 帧数；达到 SlotFailureStreakThreshold（3 个周期）后进入降频——
    /// 后续帧该槽位不再追加 Uncertainty（保留旧 Known），直到每隔
    /// SlotRetryGateFrames（5 帧）放行一次重新尝试。动画结束后连续成功
    /// 帧清零 streak，立即恢复高频识别。
    /// </summary>
    private void UpdateSlotFailureThrottle(
        Observation<IReadOnlyList<FormationCharacterState>> formation)
    {
        var slots = formation.Value ?? [];
        if (slots.Count == 0)
        {
            return;
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var key = $"{slot.Zone}:{slot.SlotIndex}";
            seenKeys.Add(key);
            var failed = slot.CharacterId is null ||
                         slot.CharacterId.StartsWith(
                             "unknown-formation-unit",
                             StringComparison.Ordinal);
            if (!failed)
            {
                // 识别成功：清零 streak，恢复高频
                _slotFailureStreak.Remove(key);
                _slotRetryGate.Remove(key);
                continue;
            }

            var streak = _slotFailureStreak.GetValueOrDefault(key) + 1;
            _slotFailureStreak[key] = streak;
            if (streak >= SlotFailureStreakThreshold &&
                !_slotRetryGate.ContainsKey(key))
            {
                // 进入降频：每 SlotRetryGateFrames 帧放行一次尝试。
                // 放行帧仍失败 → streak 继续累计（gate 重新生效）；
                // 放行帧成功 → streak 清零（上面的成功分支处理）。
                _slotRetryGate[key] = SlotRetryGateFrames;
            }
            else if (_slotRetryGate.TryGetValue(key, out var remaining))
            {
                remaining--;
                if (remaining <= 0)
                {
                    // 放行该槽位一帧（允许其新值进入合并），gate 移除后
                    // 若仍失败将重新累计并再次降频。
                    _slotRetryGate.Remove(key);
                }
                else
                {
                    _slotRetryGate[key] = remaining;
                }
            }
        }

        // 清理已不存在的槽位（阵容变化）
        foreach (var key in _slotFailureStreak.Keys
                     .Where(key => !seenKeys.Contains(key))
                     .ToArray())
        {
            _slotFailureStreak.Remove(key);
            _slotRetryGate.Remove(key);
        }
    }

    private static Phase2OperationalState MergeConfirmedFields(
        Phase2OperationalState? previous,
        Phase2OperationalState current)
    {
        if (previous is null)
        {
            return current;
        }

        return current with
        {
            NodeId = Merge(previous.NodeId, current.NodeId),
            EnemyDifficulty = Merge(previous.EnemyDifficulty, current.EnemyDifficulty),
            StoreLevel = Merge(previous.StoreLevel, current.StoreLevel),
            Interest = Merge(previous.Interest, current.Interest),
            PlayerProgress = Merge(previous.PlayerProgress, current.PlayerProgress),
            Formation = IsRewardShopOverlay(current)
                ? IsolateFromRewardShopFormation(previous.Formation)
                : RunCheckpointFactory.MergeFormationObservations(
                    previous.Formation,
                    current.Formation),
            ActiveSynergies = Merge(previous.ActiveSynergies, current.ActiveSynergies),
            DismantleToolCount = Merge(previous.DismantleToolCount, current.DismantleToolCount),
            SimpleEquipmentIds = Merge(previous.SimpleEquipmentIds, current.SimpleEquipmentIds),
            SpecialItemIds = Merge(previous.SpecialItemIds, current.SpecialItemIds),
            InventorySlots = RunCheckpointFactory.MergeInventoryObservations(
                previous.InventorySlots,
                current.InventorySlots),
            NegativeAffixIds = MergeStableFixedList(
                previous.NegativeAffixIds,
                current.NegativeAffixIds,
                Phase2RecognitionRegions.NegativeAffixSlots.Count,
                "negative affixes"),
            InvestmentEnvironmentId = MergeStableScalar(
                previous.InvestmentEnvironmentId,
                current.InvestmentEnvironmentId,
                "investment environment"),
            InvestmentStrategyIds = MergeMonotonicList(
                previous.InvestmentStrategyIds,
                current.InvestmentStrategyIds)
        };
    }

    private static Observation<IReadOnlyList<string>> MergeMonotonicList(
        Observation<IReadOnlyList<string>> previous,
        Observation<IReadOnlyList<string>> current)
    {
        var values = (previous.Value ?? [])
            .Concat(current.Value ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evidence = previous.Evidence
            .Concat(current.Evidence)
            .Distinct()
            .ToArray();
        if (current.Status == ObservationStatus.Known)
        {
            return Observation<IReadOnlyList<string>>.Known(
                values,
                current.Confidence,
                evidence,
                current.ObservedAt);
        }

        var uncertainty = current.Uncertainty
            .Concat(previous.Status == ObservationStatus.Known
                ? ["The previously confirmed strategies remain valid, but the current node scan is incomplete and may contain additional strategies."]
                : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (uncertainty.Length == 0)
        {
            uncertainty = ["The strategy set is incomplete; confirmed partial identities were retained."];
        }

        return new Observation<IReadOnlyList<string>>
        {
            Status = current.Status,
            Value = values,
            Confidence = 0,
            Evidence = evidence,
            Uncertainty = uncertainty,
            ObservedAt = current.ObservedAt ?? previous.ObservedAt
        };
    }

    private static Observation<IReadOnlyList<string>> MergeStableFixedList(
        Observation<IReadOnlyList<string>> previous,
        Observation<IReadOnlyList<string>> current,
        int requiredCount,
        string field)
    {
        var currentValues = (current.Value ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (current.Status == ObservationStatus.Known &&
            currentValues.Length == requiredCount)
        {
            return current with { Value = currentValues };
        }

        var previousValues = (previous.Value ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (previous.Status == ObservationStatus.Known &&
            previousValues.Length == requiredCount)
        {
            return previous with { Value = previousValues };
        }

        var values = previousValues
            .Concat(currentValues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var status = current.Status == ObservationStatus.Conflict ||
                     previous.Status == ObservationStatus.Conflict
            ? ObservationStatus.Conflict
            : ObservationStatus.Unknown;
        return new Observation<IReadOnlyList<string>>
        {
            Status = status,
            Value = values,
            Confidence = 0,
            Evidence = previous.Evidence
                .Concat(current.Evidence)
                .Distinct()
                .ToArray(),
            Uncertainty = previous.Uncertainty
                .Concat(current.Uncertainty)
                .Append(
                    $"{field} recognized {values.Length}/{requiredCount}; partial identities remain non-authoritative.")
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ObservedAt = current.ObservedAt ?? previous.ObservedAt
        };
    }

    private static Observation<string> MergeStableScalar(
        Observation<string> previous,
        Observation<string> current,
        string field)
    {
        if (string.IsNullOrWhiteSpace(previous.Value) ||
            previous.Status is not (
                ObservationStatus.Known or ObservationStatus.Conflict))
        {
            return current;
        }

        if (current.Status != ObservationStatus.Known ||
            string.IsNullOrWhiteSpace(current.Value))
        {
            return previous;
        }

        if (string.Equals(
                previous.Value,
                current.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            return current with
            {
                Evidence = previous.Evidence
                    .Concat(current.Evidence)
                    .Distinct()
                    .ToArray()
            };
        }

        return new Observation<string>
        {
            Status = ObservationStatus.Conflict,
            Value = previous.Value,
            Confidence = 0,
            Evidence = previous.Evidence
                .Concat(current.Evidence)
                .Distinct()
                .ToArray(),
            Uncertainty =
            [
                $"Confirmed {field} '{previous.Value}' conflicts with later recognition '{current.Value}'; the original identity was retained."
            ],
            ObservedAt = current.ObservedAt ?? previous.ObservedAt
        };
    }

    private static Observation<T> Carry<T>(
        Observation<T> current,
        Observation<T> previous,
        string field)
    {
        if (current.Status == ObservationStatus.Known ||
            previous.Value is null ||
            previous.Status is not (ObservationStatus.Known or ObservationStatus.Stale))
        {
            return current;
        }

        var observedAt = previous.ObservedAt ??
            previous.Evidence
                .Select(item => item.CapturedAt)
                .Where(item => item.HasValue)
                .Max() ??
            DateTimeOffset.MinValue;
        return Observation<T>.Stale(
            previous.Value,
            $"{field}在当前帧暂不可见；沿用最近一次可靠值，可能已过期。",
            previous.Evidence,
            observedAt);
    }

    private static Observation<T> Merge<T>(
        Observation<T> previous,
        Observation<T> current) =>
        current.Status == ObservationStatus.Known
            ? current
            : previous;

    private void ObserveBattleFrame(Phase2OperationalState frame)
    {
        var node = frame.NodeId.Status == ObservationStatus.Known
            ? frame.NodeId.Value
            : null;
        if (!string.IsNullOrWhiteSpace(node) &&
            !string.Equals(node, _activeBattleNode, StringComparison.Ordinal))
        {
            _activeBattleNode = node;
            _latestBattleDamageFrame = null;
            _latestBattleActionFrame = null;
            _pendingBattleActionFrame = null;
            _pendingRoundBoundaryActionFrame = null;
            ClearActionDropRecovery();
            _latestBattleContextFrame = null;
            _activeBattleHasWalter = false;
            _activeBattleWalterStarLevel = null;
            _acceptedBattleActionIncrease = 0;
            _activeBattleReliableActionSamples = 0;
            _activeBattleIsRewardNode = false;
            _activeBattlePreHealth = node is not null &&
                _confirmedPreparationHealth.TryGetValue(
                    node,
                    out var confirmedHealth)
                ? confirmedHealth
                : node is not null &&
                  _provisionalPreparationHealth.TryGetValue(
                      node,
                      out var provisionalHealth)
                    ? provisionalHealth
                    // 商店自动弹出会遮挡备战页血量区域（每次节点备战结束
                    // 自动弹商店，用户一直开着则血量一直被遮）→ preHealth
                    // 缺失。回退到上一节点结算血量（postHealth）继承——
                    // 节点间血量通常不变（除非购买回血），优于 null。
                    : node is not null &&
                      _activeBattlePostHealth is { } inheritedHealth
                        ? inheritedHealth
                        : null;
            // 继承值使用后清空，避免污染后续节点。
            if (_activeBattlePreHealth is { } preHealth &&
                _activeBattlePostHealth == preHealth)
            {
                _activeBattlePostHealth = null;
            }
            _activeBattlePostHealthConfidence = 0;
            _activeBattlePostHealthFromSettlement = false;
            ClearSettlementCollection();
            UpdateActiveBattleWalter(frame);
        }

        UpdateActiveBattleWalter(frame);
        // 奖励关只认 reward_battle 系页面；battle_generic 是普通战斗页，
        // 若计入会把 1-3/1-4/1-6/1-7 等普通节点全部误标成奖励关
        //（用户 2026-08-06 实测：node-1-1~1-7 全部 isRewardNode=True）。
        _activeBattleIsRewardNode |= frame.PageId is
            "reward_battle" or "reward_battle_pause";

        if (!string.IsNullOrWhiteSpace(node))
        {
            if (_latestBattleContextFrame is null ||
                ObservedAt(frame) >= ObservedAt(_latestBattleContextFrame))
            {
                _latestBattleContextFrame = frame;
            }
        }

        if (frame.RemainingActionValue.Status == ObservationStatus.Known &&
            frame.RemainingActionValue.Value is not null)
        {
            ObserveActionFrame(frame);
        }

        if (HasBattleDamageEvidence(frame))
        {
            _latestBattleDamageFrame = MergeBattleEvidence(
                _latestBattleDamageFrame,
                frame);
        }
    }

    private void ObserveSettlementFrame(Phase2OperationalState frame)
    {
        _settlementFramesObserved++;
        if (frame.SettlementGoldReward.Status == ObservationStatus.Known &&
            frame.SettlementGoldReward.Value is >= 0 and <= 100 &&
            HasAnchoredRewardGold(frame))
        {
            var value = frame.SettlementGoldReward.Value;
            _settlementGoldVotes[value] =
                _settlementGoldVotes.GetValueOrDefault(value) + 1;
        }
        _bestSettlementFrame = MergeSettlementEvidence(
            _bestSettlementFrame,
            frame,
            _settlementGoldVotes);

        var fingerprint = SettlementFingerprint(frame);
        if (string.Equals(
                fingerprint,
                _pendingSettlementFingerprint,
                StringComparison.Ordinal))
        {
            _pendingSettlementCount++;
        }
        else
        {
            _pendingSettlementFingerprint = fingerprint;
            _pendingSettlementCount = 1;
        }
    }

    private static Phase2OperationalState MergeSettlementEvidence(
        Phase2OperationalState? previous,
        Phase2OperationalState current,
        IReadOnlyDictionary<int, int> goldVotes)
    {
        if (previous is null)
        {
            return current;
        }

        return current with
        {
            NodeId = PreferObservation(previous.NodeId, current.NodeId),
            SettlementDamage = PreferDamageRows(
                previous.SettlementDamage,
                current.SettlementDamage),
            SettlementScreenDamageCandidate = PreferCumulativeValue(
                previous.SettlementScreenDamageCandidate,
                current.SettlementScreenDamageCandidate),
            SettlementGoldReward = PreferSettlementGold(
                previous.SettlementGoldReward,
                current.SettlementGoldReward,
                goldVotes),
            PendingIcons = MergePendingIcons(
                previous.PendingIcons,
                current.PendingIcons),
            PartialFields = MergePartialFields(
                previous.PartialFields,
                current.PartialFields),
            RecognitionTrace = previous.RecognitionTrace
                .Concat(current.RecognitionTrace)
                .Distinct()
                .ToArray(),
            Diagnostics = previous.Diagnostics
                .Concat(current.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static Phase2OperationalState MergeBattleEvidence(
        Phase2OperationalState? previous,
        Phase2OperationalState current)
    {
        if (previous is null)
        {
            return current;
        }

        return current with
        {
            NodeId = PreferObservation(previous.NodeId, current.NodeId),
            BattleDamage = PreferDamageRows(
                previous.BattleDamage,
                current.BattleDamage),
            BattleSynergyDamage = PreferSynergyRows(
                previous.BattleSynergyDamage,
                current.BattleSynergyDamage),
            BattleUnresolvedDamage = PreferUnresolvedRows(
                previous.BattleUnresolvedDamage,
                current.BattleUnresolvedDamage),
            BattleScreenDamageCandidate = PreferCumulativeValue(
                previous.BattleScreenDamageCandidate,
                current.BattleScreenDamageCandidate),
            PendingIcons = MergePendingIcons(
                previous.PendingIcons,
                current.PendingIcons),
            PartialFields = MergePartialFields(
                previous.PartialFields,
                current.PartialFields),
            RecognitionTrace = previous.RecognitionTrace
                .Concat(current.RecognitionTrace)
                .Distinct()
                .ToArray(),
            Diagnostics = previous.Diagnostics
                .Concat(current.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static Observation<T> PreferObservation<T>(
        Observation<T> previous,
        Observation<T> current)
    {
        if (previous.Status == ObservationStatus.Known &&
            current.Status != ObservationStatus.Known)
        {
            return previous;
        }

        if (current.Status == ObservationStatus.Known &&
            previous.Status != ObservationStatus.Known)
        {
            return current;
        }

        if (previous.Status == ObservationStatus.Known &&
            current.Status == ObservationStatus.Known)
        {
            return current.Confidence > previous.Confidence ||
                   current.Confidence == previous.Confidence &&
                   current.ObservedAt >= previous.ObservedAt
                ? current
                : previous;
        }

        return current.Value is not null && previous.Value is null
            ? current
            : previous;
    }

    private static Observation<long> PreferCumulativeValue(
        Observation<long> previous,
        Observation<long> current)
    {
        if (previous.Status != ObservationStatus.Known &&
            current.Status != ObservationStatus.Known &&
            previous.Value is long stablePrevious &&
            current.Value is long stableCurrent &&
            stablePrevious > 0 &&
            stablePrevious == stableCurrent &&
            previous.ObservedAt != current.ObservedAt &&
            !HasAmbiguousDamageScale(previous) &&
            !HasAmbiguousDamageScale(current))
        {
            return new Observation<long>
            {
                Status = ObservationStatus.Known,
                Value = stableCurrent,
                Confidence = 0.62,
                Evidence = previous.Evidence
                    .Concat(current.Evidence)
                    .Distinct()
                    .ToArray(),
                Uncertainty = previous.Uncertainty
                    .Concat(current.Uncertainty)
                    .Append("节点总伤害由两帧相同的残缺候选稳定确认。")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                ObservedAt = current.ObservedAt
            };
        }

        if (previous.Status == ObservationStatus.Known &&
            current.Status == ObservationStatus.Known &&
            previous.Value is long previousValue &&
            current.Value is long currentValue)
        {
            if (currentValue != previousValue)
            {
                return currentValue > previousValue ? current : previous;
            }
        }

        return PreferObservation(previous, current);
    }

    private static bool HasAmbiguousDamageScale(Observation<long> observation) =>
        observation.Uncertainty.Any(item =>
            item.Contains("伤害单位未识别", StringComparison.Ordinal));

    private static Observation<int> PreferSettlementGold(
        Observation<int> previous,
        Observation<int> current,
        IReadOnlyDictionary<int, int> votes)
    {
        var previousIsValid = previous.Status == ObservationStatus.Known &&
                              previous.Value is >= 0 and <= 100;
        var currentIsValid = current.Status == ObservationStatus.Known &&
                             current.Value is >= 0 and <= 100;
        if (previousIsValid != currentIsValid)
        {
            return currentIsValid ? current : previous;
        }

        if (previous.Status == ObservationStatus.Known &&
            current.Status == ObservationStatus.Known &&
            previous.Value != current.Value)
        {
            var previousVotes = votes.GetValueOrDefault(previous.Value);
            var currentVotes = votes.GetValueOrDefault(current.Value);
            return currentVotes > previousVotes ? current : previous;
        }

        return PreferObservation(previous, current);
    }

    private static Observation<IReadOnlyList<CharacterDamageState>> PreferDamageRows(
        Observation<IReadOnlyList<CharacterDamageState>> previous,
        Observation<IReadOnlyList<CharacterDamageState>> current) =>
        PreferRowObservation(previous, current, rows => rows.Sum(item => item.Damage));

    private static Observation<IReadOnlyList<SynergyDamageState>> PreferSynergyRows(
        Observation<IReadOnlyList<SynergyDamageState>> previous,
        Observation<IReadOnlyList<SynergyDamageState>> current) =>
        PreferRowObservation(previous, current, rows => rows.Sum(item => item.Damage));

    private static Observation<IReadOnlyList<UnresolvedDamageSourceState>>
        PreferUnresolvedRows(
            Observation<IReadOnlyList<UnresolvedDamageSourceState>> previous,
            Observation<IReadOnlyList<UnresolvedDamageSourceState>> current) =>
        PreferRowObservation(previous, current, rows => rows.Sum(item => item.Damage));

    private static Observation<IReadOnlyList<T>> PreferRowObservation<T>(
        Observation<IReadOnlyList<T>> previous,
        Observation<IReadOnlyList<T>> current,
        Func<IReadOnlyList<T>, long> total)
    {
        // Partial/unknown OCR rows still carry useful values. Compare those
        // values before the generic confidence preference so a later, larger
        // cumulative damage reading cannot be replaced by an older partial
        // frame merely because both observations remain unresolved.
        if (previous.Value is { } previousRows &&
            current.Value is { } currentRows)
        {
            var previousTotal = total(previousRows);
            var currentTotal = total(currentRows);
            if (currentTotal != previousTotal)
            {
                return currentTotal > previousTotal ? current : previous;
            }

            if (currentRows.Count != previousRows.Count)
            {
                return currentRows.Count > previousRows.Count ? current : previous;
            }
        }

        return PreferObservation(previous, current);
    }

    private static IReadOnlyList<PendingIconObservation> MergePendingIcons(
        IReadOnlyList<PendingIconObservation> previous,
        IReadOnlyList<PendingIconObservation> current) => previous
        .Concat(current)
        .GroupBy(item => (item.Category, item.SlotKey))
        .Select(group => group
            .OrderByDescending(item => item.CanDriveDecisions)
            .ThenByDescending(item => item.Confidence)
            .First())
        .ToArray();

    private static IReadOnlyList<Phase2PartialFieldObservation> MergePartialFields(
        IReadOnlyList<Phase2PartialFieldObservation> previous,
        IReadOnlyList<Phase2PartialFieldObservation> current) => previous
        .Concat(current)
        .GroupBy(item => (item.Field, item.TemporaryId))
        .Select(group => group.OrderByDescending(item => item.Confidence).First())
        .ToArray();

    private static bool ShouldReplaceSettlement(
        Phase2OperationalState previous,
        Phase2OperationalState current)
    {
        var previousQuality = SettlementEvidenceQuality(previous);
        var currentQuality = SettlementEvidenceQuality(current);
        return currentQuality > previousQuality ||
               (currentQuality == previousQuality &&
                SettlementObservedAt(current) >= SettlementObservedAt(previous));
    }

    private static int SettlementEvidenceQuality(Phase2OperationalState frame)
    {
        var rows = frame.SettlementDamage.Value?.Count ?? 0;
        return
            (frame.SettlementScreenDamageCandidate.Status == ObservationStatus.Known
                ? 100
                : frame.SettlementScreenDamageCandidate.Confidence > 0
                    ? 20
                    : 0) +
            Math.Min(rows, 3) * 20 +
            (frame.SettlementGoldReward.Status == ObservationStatus.Known
                ? 30
                : 0) +
            (frame.NodeId.Status == ObservationStatus.Known ? 5 : 0);
    }

    private static bool IsSettlementSummaryReady(Phase2OperationalState frame) =>
        // 结算金币奖励（GoldReward）识别已按用户 2026-08-06 拍板废弃（恒
        // Unknown），此前要求 SettlementGoldReward.Known 使该条件永久为假，
        // 导致普通战斗节点的"稳定结算"封存路径被杀死（0.2.855 实机已
        // 证实）。废弃字段不得参与封存判定。
        frame.SettlementScreenDamageCandidate.Status == ObservationStatus.Known &&
        frame.SettlementDamage.Value is { Count: 3 };

    private static DateTimeOffset SettlementObservedAt(
        Phase2OperationalState frame) =>
        new DateTimeOffset?[]
        {
            frame.SettlementScreenDamageCandidate.ObservedAt,
            frame.SettlementGoldReward.ObservedAt,
            frame.NodeId.ObservedAt
        }
        .Concat(frame.SettlementDamage.Evidence.Select(item => item.CapturedAt))
        .Where(item => item.HasValue)
        .Select(item => item!.Value)
        .DefaultIfEmpty(DateTimeOffset.MinValue)
        .Max();

    private static string SettlementFingerprint(Phase2OperationalState frame)
    {
        var material = string.Join(
            "|",
            SemanticValue(frame.NodeId),
            SemanticDamage(frame.SettlementDamage),
            SemanticValue(frame.SettlementScreenDamageCandidate),
            SemanticValue(frame.SettlementGoldReward));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private void ClearSettlementCollection()
    {
        _bestSettlementFrame = null;
        _settlementFramesObserved = 0;
        _pendingSettlementFingerprint = null;
        _pendingSettlementCount = 0;
        _settlementGoldVotes.Clear();
    }

    private void ObserveActionFrame(Phase2OperationalState current)
    {
        var currentValue = current.RemainingActionValue.Value!;
        if (currentValue.CurrentRoundActionValue == 100)
        {
            // The game briefly renders "N轮 100" both at a legitimate round
            // boundary and, more importantly, during the terminal animation.
            // It is therefore useful transition evidence but never a safe
            // final-action sample. Keep it out of the terminal candidate; a
            // subsequent non-100 frame can still establish the new timeline.
            _pendingRoundBoundaryActionFrame = current;
            _pendingBattleActionFrame = null;
            return;
        }

        _activeBattleReliableActionSamples++;
        _pendingRoundBoundaryActionFrame = null;
        if (_latestBattleActionFrame?.RemainingActionValue.Value is null)
        {
            _latestBattleActionFrame = current;
            _pendingBattleActionFrame = null;
            ClearActionDropRecovery();
            return;
        }

        if (ObservedAt(current) < ObservedAt(_latestBattleActionFrame))
        {
            return;
        }

        var trusted = _latestBattleActionFrame.RemainingActionValue.Value;
        var candidate = currentValue;
        if (candidate.TotalActionValue > trusted.TotalActionValue)
        {
            if (TryRecoverFromFalseLargeDrop(current, candidate))
            {
                return;
            }

            ObserveActionIncrease(current, trusted, candidate);
            return;
        }

        var drop = trusted.TotalActionValue - candidate.TotalActionValue;
        if (drop <= MaximumImmediateActionDrop)
        {
            _latestBattleActionFrame = current;
            _pendingBattleActionFrame = null;
            AdvanceActionDropRecoveryWindow();
            return;
        }

        // A single effect-heavy frame can lose a leading digit (76 -> 6).
        // Large drops are committed only after a second nearby reading. At
        // 4-6 FPS a genuine transition supplies that evidence quickly, while
        // an isolated OCR spike cannot poison the terminal candidate.
        var pending = _pendingBattleActionFrame?.RemainingActionValue.Value;
        if (pending is not null &&
            Math.Abs(
                pending.TotalActionValue -
                candidate.TotalActionValue) <= MaximumConfirmedActionDrift)
        {
            _actionDropRollbackAnchor = _latestBattleActionFrame;
            _pendingActionRecoveryFrame = null;
            _actionDropRecoveryFramesRemaining = ActionDropRecoveryWindow;
            _latestBattleActionFrame = current;
            _pendingBattleActionFrame = null;
            return;
        }

        _pendingBattleActionFrame = current;
    }

    private bool TryRecoverFromFalseLargeDrop(
        Phase2OperationalState current,
        RemainingActionValueState candidate)
    {
        var anchor = _actionDropRollbackAnchor?.RemainingActionValue.Value;
        if (anchor is null)
        {
            return false;
        }

        if (Math.Abs(
                anchor.TotalActionValue -
                candidate.TotalActionValue) > MaximumConfirmedActionDrift)
        {
            AdvanceActionDropRecoveryWindow();
            return false;
        }

        var pending = _pendingActionRecoveryFrame?.RemainingActionValue.Value;
        if (pending is not null &&
            Math.Abs(
                pending.TotalActionValue -
                candidate.TotalActionValue) <= MaximumConfirmedActionDrift)
        {
            _latestBattleActionFrame = current;
            _pendingBattleActionFrame = null;
            ClearActionDropRecovery();
            return true;
        }

        _pendingActionRecoveryFrame = current;
        return true;
    }

    private void AdvanceActionDropRecoveryWindow()
    {
        if (_actionDropRollbackAnchor is null)
        {
            return;
        }

        _actionDropRecoveryFramesRemaining--;
        if (_actionDropRecoveryFramesRemaining <= 0)
        {
            ClearActionDropRecovery();
        }
    }

    private void ClearActionDropRecovery()
    {
        _actionDropRollbackAnchor = null;
        _pendingActionRecoveryFrame = null;
        _actionDropRecoveryFramesRemaining = 0;
    }

    private void ObserveActionIncrease(
        Phase2OperationalState current,
        RemainingActionValueState trusted,
        RemainingActionValueState candidate)
    {
        // A battle countdown may increase only when an active, reliably
        // identified Walter is present. His Currency Wars ultimate delays the
        // battle-end countdown by 20 at one/two stars or 100 at three stars.
        // Enemy action delay is a separate effect and is intentionally not
        // considered here.
        if (!_activeBattleHasWalter)
        {
            _pendingBattleActionFrame = null;
            return;
        }

        var increase = candidate.TotalActionValue - trusted.TotalActionValue;
        var maximumSingleIncrease = _activeBattleWalterStarLevel == 3
            ? 100
            : _activeBattleWalterStarLevel is 1 or 2
                ? 20
                : 100;
        var maximumBattleIncrease = _activeBattleWalterStarLevel == 3
            ? 999
            : _activeBattleWalterStarLevel is 1 or 2
                ? 100
                : 999;
        if (increase > maximumSingleIncrease ||
            _acceptedBattleActionIncrease + increase > maximumBattleIncrease)
        {
            _pendingBattleActionFrame = null;
            return;
        }

        // OCR can occasionally turn 79 into 99. Even with Walter present, an
        // increase is accepted only after a nearby consecutive reading. This
        // preserves the game mechanic without allowing one noisy frame to
        // overwrite the last trustworthy countdown.
        var pending = _pendingBattleActionFrame?.RemainingActionValue.Value;
        if (pending is not null &&
            Math.Abs(
                pending.TotalActionValue -
                candidate.TotalActionValue) <= MaximumConfirmedActionDrift)
        {
            _acceptedBattleActionIncrease += increase;
            _latestBattleActionFrame = current;
            _pendingBattleActionFrame = null;
            ClearActionDropRecovery();
            return;
        }

        _pendingBattleActionFrame = current;
    }

    private void UpdateActiveBattleWalter(Phase2OperationalState frame)
    {
        // 瓦尔特识别必须放宽到"残缺阵容"：formation 整体 Unknown（个别槽位
        // 匹配失败）但瓦尔特槽位可能已识别到（实测 3-7：阵容 Unknown 导致
        // 瓦尔特从未被识别 → 行动上限未加成 → 行动值 163 ≥ 120 拒绝计算
        // 理论极限）。只要 value 里有瓦尔特（前台），就按用户规则
        // （1/2星 +100、3星 +999）提高上限。
        // 注意：本方法只在战斗帧调用（ObserveBattleFrame）；备战帧的
        // formation 由 Observe 主流程保存到 _latestPreparationFormation。
        // 不用 _lastConfirmedState.Formation 兜底——MergeFormationObservations
        // 跨节点保留 formation（932 行 previousValues 作 base），会导致上一节点
        // 的瓦尔特泄漏到下一节点（实测 3-7 瓦尔特 → 3-8 误标）。
        var formation = frame.Formation.Value is { Count: > 0 }
            ? frame.Formation.Value
            : _latestPreparationFormation;
        var walter = formation?.FirstOrDefault(item =>
            // 瓦尔特必须是前台角色（放后台无法释放技能，用户 2026-08-06 确认）。
            item.Zone == FormationZone.Front &&
            item.CanDriveDecisions &&
            (string.Equals(
                 item.CharacterId,
                 WalterCharacterId,
                 StringComparison.Ordinal) ||
             string.Equals(item.CharacterId, "瓦尔特", StringComparison.Ordinal)));
        if (walter is null)
        {
            return;
        }

        _activeBattleHasWalter = true;
        if (walter.StarLevel is >= 1 and <= 3)
        {
            _activeBattleWalterStarLevel = walter.StarLevel;
        }
    }

    private static bool ShouldReplaceDamage(
        Phase2OperationalState? previous,
        Phase2OperationalState current)
    {
        if (previous is null)
        {
            return true;
        }

        if (ObservedAt(current) < ObservedAt(previous))
        {
            return false;
        }

        var previousTotal = RecordedBattleDamage(previous);
        var currentTotal = RecordedBattleDamage(current);
        if (!previousTotal.HasValue)
        {
            return true;
        }

        // Damage is cumulative within a battle. Do not let a later occluded or
        // partially OCR'd panel replace a higher or equally-valued but more
        // complete candidate.
        if (!currentTotal.HasValue || currentTotal.Value < previousTotal.Value)
        {
            return false;
        }

        return currentTotal.Value > previousTotal.Value ||
               DamageEvidenceQuality(current) >= DamageEvidenceQuality(previous);
    }

    private static int DamageEvidenceQuality(Phase2OperationalState frame) =>
        (frame.BattleDamage.Status == ObservationStatus.Known ? 20 : 0) +
        (frame.BattleSynergyDamage.Status == ObservationStatus.Known ? 10 : 0) +
        (frame.BattleUnresolvedDamage.Status == ObservationStatus.Known ? 10 : 0) +
        (frame.BattleScreenDamageCandidate.Status == ObservationStatus.Known ? 10 : 0) +
        (frame.BattleDamage.Value?.Count ?? 0) * 2 +
        (frame.BattleSynergyDamage.Value?.Count ?? 0) * 2 +
        (frame.BattleUnresolvedDamage.Value?.Count ?? 0);

    private static bool HasBattleDamageEvidence(Phase2OperationalState frame) =>
        RecordedBattleDamage(frame).HasValue ||
        frame.PendingIcons.Any(item =>
            item.RecognizedFields?.ContainsKey("damage") == true);

    private static long? RecordedBattleDamage(Phase2OperationalState frame)
    {
        if (frame.BattleScreenDamageCandidate.Value is long candidate)
        {
            return candidate;
        }

        var values = (frame.BattleDamage.Value ?? [])
            .Select(item => item.Damage)
            .Concat((frame.BattleSynergyDamage.Value ?? []).Select(item => item.Damage))
            .Concat((frame.BattleUnresolvedDamage.Value ?? []).Select(item => item.Damage))
            .ToArray();
        return values.Length == 0 ? null : values.Sum();
    }

    private static DateTimeOffset ObservedAt(Phase2OperationalState frame) =>
        new DateTimeOffset?[]
        {
            frame.BattleScreenDamageCandidate.ObservedAt,
            frame.RemainingActionValue.ObservedAt,
            frame.NodeId.ObservedAt
        }
        .Concat(frame.BattleDamage.Evidence.Select(item => item.CapturedAt))
        .Concat(frame.BattleSynergyDamage.Evidence.Select(item => item.CapturedAt))
        .Concat(frame.BattleUnresolvedDamage.Evidence.Select(item => item.CapturedAt))
        .Where(item => item.HasValue)
        .Select(item => item!.Value)
        .DefaultIfEmpty(DateTimeOffset.MinValue)
        .Max();

    private static Phase2OperationalState CombineBattleFrames(
        Phase2OperationalState? damageFrame,
        Phase2OperationalState? actionFrame,
        Phase2OperationalState contextFrame)
    {
        var combined = damageFrame ?? actionFrame ?? contextFrame;
        var node = new[] { damageFrame, actionFrame, contextFrame }
            .Where(item => item?.NodeId.Status == ObservationStatus.Known &&
                           !string.IsNullOrWhiteSpace(item.NodeId.Value))
            .OrderByDescending(item => ObservedAt(item!))
            .Select(item => item!.NodeId)
            .FirstOrDefault() ?? combined.NodeId;
        var action = actionFrame?.RemainingActionValue ??
                     combined.RemainingActionValue;
        return combined with
        {
            NodeId = node,
            RemainingActionValue = action
        };
    }

    private static bool IsCompleteBattleFrame(Phase2OperationalState frame) =>
        frame.BattleDamage.Status == ObservationStatus.Known &&
        frame.BattleDamage.Value is { Count: > 0 } &&
        frame.BattleSynergyDamage.Status == ObservationStatus.Known &&
        frame.BattleSynergyDamage.Value is not null &&
        frame.BattleUnresolvedDamage.Status == ObservationStatus.Known &&
        frame.BattleUnresolvedDamage.Value is { Count: 0 } &&
        frame.RemainingActionValue.Status == ObservationStatus.Known &&
        frame.RemainingActionValue.Value is not null &&
        frame.NodeId.Status == ObservationStatus.Known &&
        !string.IsNullOrWhiteSpace(frame.NodeId.Value) &&
        frame.BattleDamage.Value.All(item => item.CanDriveDecisions) &&
        frame.BattleSynergyDamage.Value.All(item => item.CanDriveDecisions);

    private static bool IsRewardNodeId(string? nodeId) => nodeId is
        "1-1" or "1-2" or "1-8" or "2-6" or "3-6";

    private static FinalNodeBattleState FinalizeBattle(
        Phase2OperationalState frame,
        Phase2OperationalState? settlement,
        int? preBattleHealth,
        int? postBattleHealth,
        bool hasWalter,
        int? walterStarLevel,
        int confirmedActionIncrease,
        int reliableActionSamples,
        bool isRewardNode)
    {
        var damage = frame.BattleDamage.Value ?? [];
        var synergyDamage = frame.BattleSynergyDamage.Value ?? [];
        var unresolvedDamage = frame.BattleUnresolvedDamage.Value ?? [];
        var settlementDamage = settlement?.SettlementDamage.Value ?? [];
        var evidence = damage.Select(item => item.Evidence)
            .Concat(synergyDamage.Select(item => item.Evidence))
            .Concat(unresolvedDamage.Select(item => item.Evidence))
            .Concat(frame.PendingIcons.Select(item => item.Evidence))
            .Concat(settlementDamage.Select(item => item.Evidence))
            .Concat(settlement?.PendingIcons.Select(item => item.Evidence) ?? [])
            .Concat(frame.NodeId.Evidence)
            .Concat(frame.RemainingActionValue.Evidence)
            .Concat(settlement?.NodeId.Evidence ?? [])
            .FirstOrDefault() ??
            new EvidenceReference(
                "phase2-degraded-finalization",
                "derived:node-transition",
                "No row-level damage evidence was available; the incomplete " +
                "record was finalized from the confirmed page transition.",
                frame.RemainingActionValue.ObservedAt ??
                frame.NodeId.ObservedAt ??
                DateTimeOffset.UtcNow,
                0);
        var battleCandidate = frame.BattleScreenDamageCandidate.Value;
        var settlementCandidate = settlement?.SettlementScreenDamageCandidate.Value;
        long? validBattleCandidate = frame.BattleScreenDamageCandidate.Status ==
                                     ObservationStatus.Known
            ? battleCandidate
            : null;
        long? validSettlementCandidate = settlement?.SettlementScreenDamageCandidate.Status ==
                                         ObservationStatus.Known
            ? settlementCandidate
            : null;
        var (selected, selectedSource) = SelectDamage(
            validBattleCandidate,
            validSettlementCandidate);
        var selectedIdentitiesComplete = selectedSource switch
        {
            FinalDamageSelectionSource.BattleLastFrame =>
                IsCompleteBattleFrame(frame),
            FinalDamageSelectionSource.SettlementTopThree =>
                settlementDamage.Count == 3 &&
                settlementDamage.All(item => item.CanDriveDecisions),
            _ => false
        };
        // 结算金币奖励（GoldReward）识别已废弃（用户 2026-08-06 确认删除）。
        int? goldReward = null;
        // 奖励关硬性规则（用户 2026-08-11 拍板）：奖励关 5 关
        // （1-1/1-2/1-8/2-6/3-6）必然过关——血量Δ固定 +2、完美通关，
        // 无需识别（秒杀局伤害 0、血量Δ未记录不影响判定）。
        // 用户 2026-08-11 拍板：奖励关 5 关（1-1/1-2/1-8/2-6/3-6）必然
        // 过关——无论封存走哪条路径（正常战斗帧路径或第五 exit 降级路径），
        // 均按"完美通关、IsComplete"判定（血量Δ按拍板规则 +2；不伪造伤害）。
        var isRewardNodeByNodeId = IsRewardNodeId(frame.NodeId.Value);
        var rewardPostBattleHealth = isRewardNodeByNodeId &&
                                     preBattleHealth.HasValue
            ? Math.Min(100, preBattleHealth.Value + 2)
            : postBattleHealth;
        var rawHealthDelta = preBattleHealth.HasValue &&
                             rewardPostBattleHealth.HasValue
            ? rewardPostBattleHealth.Value - preBattleHealth.Value
            : isRewardNodeByNodeId
                ? 2
                : (int?)null;
        var healthDelta = rawHealthDelta;
        var rawRemainingAction = frame.RemainingActionValue.Status ==
                                 ObservationStatus.Known
            ? frame.RemainingActionValue.Value
            : null;
        var remainingAction = healthDelta is < 0
            ? RemainingActionValueState.Create(0, 0)
            : rawRemainingAction;
        // 结算金币奖励已废弃后，settlement 不再参与完成判定：
        // 战斗帧完整 + 剩余行动值可读即为完成（否则正常通关节点恒 false）。
        // 奖励关按用户拍板必然过关，无战斗证据也视为完整（伤害仍不伪造）。
        var complete = isRewardNodeByNodeId ||
                       selected.HasValue &&
                       selectedIdentitiesComplete &&
                       remainingAction is not null;
        var clearStatus =
            isRewardNodeByNodeId ||
            isRewardNode ||
            healthDelta == 2 ||
            (postBattleHealth == 100 &&
             healthDelta is not < 0) ||
            healthDelta is null &&
            remainingAction?.TotalActionValue > 10
                ? NodeClearStatus.Perfect
                : healthDelta < 0 ||
                  remainingAction?.TotalActionValue == 0
                    ? NodeClearStatus.NotPerfect
                    : healthDelta >= 0 &&
                      complete &&
                      remainingAction?.TotalActionValue > 0
                        ? NodeClearStatus.Perfect
                        : NodeClearStatus.Unknown;
        var theoretical = TheoreticalDamageCalculator.Calculate(
            frame.NodeId.Value!,
            selected,
            remainingAction,
            clearStatus,
            hasWalter,
            walterStarLevel,
            confirmedActionIncrease,
            reliableActionSamples);
        var uncertainty = frame.BattleDamage.Uncertainty
            .Concat(frame.BattleSynergyDamage.Uncertainty)
            .Concat(frame.BattleUnresolvedDamage.Uncertainty)
            .Concat(frame.BattleScreenDamageCandidate.Uncertainty)
            .Concat(settlement?.SettlementDamage.Uncertainty ?? [])
            .Concat(settlement?.SettlementScreenDamageCandidate.Uncertainty ?? [])
            .Concat(settlement?.SettlementGoldReward.Uncertainty ?? [])
            .Concat(frame.PendingIcons
                .Where(item => item.RecognizedFields?.ContainsKey("damage") == true)
                .Select(item => item.Status))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new FinalNodeBattleState(
            frame.NodeId.Value!,
            damage,
            selected,
            remainingAction,
            damage.Select(item => item.Evidence.CapturedAt)
                .Concat(synergyDamage.Select(item => item.Evidence.CapturedAt))
                .Concat(unresolvedDamage.Select(item => item.Evidence.CapturedAt))
                .Concat(frame.PendingIcons.Select(item => item.Evidence.CapturedAt))
                .Concat(settlementDamage.Select(item => item.Evidence.CapturedAt))
                .Concat(settlement?.PendingIcons.Select(
                    item => item.Evidence.CapturedAt) ?? [])
                .Where(item => item.HasValue)
                .Max() ?? DateTimeOffset.UtcNow,
            evidence,
            synergyDamage,
            complete,
            complete,
            uncertainty,
            frame.PendingIcons
                .Where(item => item.RecognizedFields?.ContainsKey("damage") == true)
                .ToArray(),
            frame.PartialFields,
            unresolvedDamage,
            battleCandidate,
            settlementCandidate,
            selected,
            selectedSource,
            settlementDamage,
            goldReward,
            preBattleHealth,
            rewardPostBattleHealth,
            healthDelta,
            clearStatus,
            theoretical.Value,
            theoretical.BaseMaximumActionValue,
            theoretical.ConfirmedActionIncrease,
            theoretical.EffectiveMaximumActionValue,
            theoretical.Quality,
            theoretical.Rule,
            isRewardNodeByNodeId || isRewardNode);
    }

    private static bool HasAnchoredRewardGold(Phase2OperationalState settlement)
    {
        if (settlement.SettlementGoldReward.Value is not (>= 0 and <= 100) ||
            settlement.SettlementGoldReward.Confidence < 0.65)
        {
            return false;
        }

        var explicitlyAnchored = string.Equals(
                settlement.PageId,
                "challenge_success",
                StringComparison.OrdinalIgnoreCase) &&
            settlement.SettlementGoldReward.Evidence.Any(item =>
                item.Locator.StartsWith(
                    "ocr:settlement-gold-reward",
                    StringComparison.OrdinalIgnoreCase));
        if (explicitlyAnchored)
        {
            return true;
        }

        // Older collectors did not persist PageId/evidence on this field, but
        // the complete three-row settlement summary is itself a strong page
        // anchor. The tracker still requires two identical settlement frames
        // before finalization, while out-of-range animation OCR such as 841 is
        // rejected above.
        return settlement.PageFamily == Phase2PageFamily.BattleSettlement &&
               settlement.SettlementScreenDamageCandidate.Status ==
               ObservationStatus.Known &&
               settlement.SettlementDamage.Value is { Count: 3 };
    }

    internal static (long? Value, FinalDamageSelectionSource Source) SelectDamage(
        long? battleCandidate,
        long? settlementCandidate)
    {
        if (!battleCandidate.HasValue && !settlementCandidate.HasValue)
        {
            return (null, FinalDamageSelectionSource.Unavailable);
        }

        if (!settlementCandidate.HasValue ||
            battleCandidate >= settlementCandidate)
        {
            return (battleCandidate, FinalDamageSelectionSource.BattleLastFrame);
        }

        return (settlementCandidate, FinalDamageSelectionSource.SettlementTopThree);
    }

    private static string PersistentFingerprint(Phase2OperationalState frame)
    {
        var material = string.Join(
            "|",
            frame.PageFamily,
            SemanticValue(frame.NodeId),
            SemanticValue(frame.EnemyDifficulty),
            SemanticValue(frame.StoreLevel),
            SemanticValue(frame.Interest),
            SemanticValue(frame.CumulativeSpend),
            SemanticValue(frame.PlayerProgress),
            SemanticFormation(frame.Formation),
            SemanticSynergies(frame.ActiveSynergies),
            SemanticStringList(frame.SimpleEquipmentIds),
            SemanticStringList(frame.SpecialItemIds),
            SemanticInventory(frame.InventorySlots),
            SemanticValue(frame.DismantleToolCount),
            SemanticStringList(frame.NegativeAffixIds),
            SemanticValue(frame.InvestmentEnvironmentId),
            SemanticStringList(frame.InvestmentStrategyIds),
            SemanticValue(frame.BattleScreenDamageCandidate),
            SemanticDamage(frame.SettlementDamage),
            SemanticValue(frame.SettlementScreenDamageCandidate),
            SemanticValue(frame.SettlementGoldReward));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string SemanticValue<T>(Observation<T> observation) =>
        observation.Status == ObservationStatus.Known
            ? AdvisorJson.Serialize(observation.Value, indented: false)
            : observation.Status.ToString();

    private static string SemanticFormation(
        Observation<IReadOnlyList<FormationCharacterState>> observation) =>
        AdvisorJson.Serialize(
            new
            {
                observation.Status,
                Characters = (observation.Value ?? [])
                    .OrderBy(item => item.Zone)
                    .ThenBy(item => item.SlotIndex)
                    .Select(item => new
                    {
                        item.Zone,
                        item.SlotIndex,
                        item.CharacterId,
                        item.StarLevel,
                        item.Standing,
                        EquipmentIds = item.EquipmentIds
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray(),
                        item.CanDriveDecisions,
                        CandidateCharacterIds = (item.CandidateCharacterIds ?? [])
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray(),
                        EquipmentSlots = item.FinalEquipmentSlots
                            .OrderBy(slot => slot.SlotIndex)
                            .Select(slot => new
                            {
                                slot.SlotIndex,
                                slot.Occupancy,
                                slot.EquipmentId,
                                slot.IsPrivileged,
                                CandidateEquipmentIds =
                                    slot.CandidateEquipmentIds
                                        .OrderBy(
                                            id => id,
                                            StringComparer.Ordinal)
                                        .ToArray(),
                                slot.CanDriveDecisions
                            })
                            .ToArray()
                    })
                    .ToArray()
            },
            indented: false);

    private static string SemanticInventory(
        Observation<IReadOnlyList<InventorySlotState>> observation) =>
        AdvisorJson.Serialize(
            new
            {
                observation.Status,
                Slots = (observation.Value ?? [])
                    .OrderBy(item => item.SlotIndex)
                    .Select(item => new
                    {
                        item.SlotIndex,
                        item.Occupancy,
                        item.ItemKind,
                        item.ItemId,
                        CandidateItemIds = item.CandidateItemIds
                            .OrderBy(id => id, StringComparer.Ordinal)
                            .ToArray(),
                        item.CanDriveDecisions
                    })
                    .ToArray()
            },
            indented: false);

    private static string SemanticSynergies(
        Observation<IReadOnlyList<ActiveSynergyState>> observation) =>
        observation.Status == ObservationStatus.Known
            ? AdvisorJson.Serialize(
                (observation.Value ?? [])
                    .OrderBy(item => item.SlotKey, StringComparer.Ordinal)
                    .Select(item => new
                    {
                        item.SlotKey,
                        item.SynergyId,
                        item.ActiveCount,
                        item.NextThreshold
                    })
                    .ToArray(),
                indented: false)
            : observation.Status.ToString();

    private static string SemanticStringList(
        Observation<IReadOnlyList<string>> observation) =>
        AdvisorJson.Serialize(
            new
            {
                observation.Status,
                Values = (observation.Value ?? [])
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
            },
            indented: false);

    private static string SemanticDamage(
        Observation<IReadOnlyList<CharacterDamageState>> observation) =>
        observation.Status == ObservationStatus.Known
            ? AdvisorJson.Serialize(
                (observation.Value ?? [])
                    .OrderBy(item => item.Rank)
                    .Select(item => new
                    {
                        item.Rank,
                        item.CharacterId,
                        item.Damage,
                        item.CanDriveDecisions
                    })
                    .ToArray(),
                indented: false)
            : observation.Status.ToString();
}
