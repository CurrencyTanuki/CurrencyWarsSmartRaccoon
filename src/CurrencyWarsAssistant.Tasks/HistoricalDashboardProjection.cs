using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

public enum HistoricalDamageScale
{
    Linear,
    Logarithmic
}

public sealed record HistoricalNodeDashboardEntry(
    string RunId,
    string NodeId,
    long? FinalDamage,
    int? RemainingActionValue,
    int? GoldSpentSincePreviousNode,
    int? GoldDeltaSincePreviousNode,
    int? GoldReward,
    DateTimeOffset UpdatedAt,
    bool IsComplete,
    NodeClearStatus ClearStatus = NodeClearStatus.Unknown,
    int? HealthDelta = null,
    int? AbsoluteGold = null,
    long? TheoreticalDamage = null,
    TheoreticalDamageQuality TheoreticalDamageQuality =
        TheoreticalDamageQuality.Unknown,
    bool IsRewardNode = false,
    bool HealthDepleted = false);

public sealed record HistoricalDashboardSnapshot(
    string? RunId,
    IReadOnlyList<HistoricalNodeDashboardEntry> Nodes,
    HistoricalDamageScale DamageScale,
    HistoricalDamageScale TheoryScale = HistoricalDamageScale.Linear)
{
    public IReadOnlyList<HistoricalNodeDetailEntry> DetailNodes { get; init; } = [];
}

public sealed record HistoricalNodeDetailEntry(
    string RunId,
    string NodeId,
    RunSnapshot? LatestSnapshot,
    Phase2OperationalState? LatestState,
    Phase2OperationalState? LatestPreparationState,
    FinalNodeBattleState? FinalBattle,
    ScreenshotAnalysisResult? LatestAnalysis,
    DateTimeOffset UpdatedAt,
    string? PreparationAnalysisFile = null,
    string? FinalBattleFile = null);

public interface IHistoricalDashboardProjection
{
    event EventHandler<HistoricalDashboardSnapshot>? Changed;

    HistoricalDashboardSnapshot Current { get; }

    void Observe(string runId, ScreenshotAnalysisResult analysis);
}

/// <summary>
/// Builds the compact, current-run history shown by the operation overlay.
/// It consumes the already completed phase-two analysis and never captures or
/// recognizes an additional frame.
/// </summary>
public sealed class HistoricalDashboardProjection :
    IHistoricalDashboardProjection
{
    private const double LogarithmicScaleRatioThreshold = 100;
    private readonly object _gate = new();
    private readonly Dictionary<string, MutableNode> _nodes =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _runId;
    private string? _lastResolvedNodeId;
    private HistoricalDashboardSnapshot _current =
        new(null, [], HistoricalDamageScale.Linear);

    public event EventHandler<HistoricalDashboardSnapshot>? Changed;

    public HistoricalDashboardSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Observe(string runId, ScreenshotAnalysisResult analysis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(analysis);

        HistoricalDashboardSnapshot? changedSnapshot = null;
        lock (_gate)
        {
            var changed = EnsureRun(runId);
            var state = analysis.OperationalState;
            if (state is null)
            {
                if (changed)
                {
                    changedSnapshot = RebuildSnapshot();
                }
            }
            else
            {
                var finalBattle = state.FinalBattle.Value;
                if (finalBattle is not null &&
                    !string.IsNullOrWhiteSpace(finalBattle.NodeId))
                {
                    var finalizedNode = GetOrCreateNode(runId, finalBattle.NodeId);
                    changed |= ObserveFinalBattle(finalizedNode, finalBattle);
                }

                var nodeId = ResolveNodeId(state, analysis.Snapshot);
                if (!string.IsNullOrWhiteSpace(nodeId))
                {
                    var node = GetOrCreateNode(runId, nodeId);
                    var isCanonicalPreparation =
                        state.PageFamily == Phase2PageFamily.Preparation &&
                        !string.Equals(
                            state.PageId,
                            "reward_shop",
                            StringComparison.Ordinal);
                    if (isCanonicalPreparation)
                    {
                        var stableAnalysis = ObserveDetails(node, analysis);
                        changed = true;
                        changed |= ObservePreparation(
                            node,
                            stableAnalysis.OperationalState!,
                            stableAnalysis.Snapshot);
                        changed |= BackfillPreviousNodeEndingGold(
                            node,
                            stableAnalysis.Snapshot);
                    }

                    changed |= RecalculateEconomyDeltas();
                }

                if (changed)
                {
                    changedSnapshot = RebuildSnapshot();
                }
            }
        }

        if (changedSnapshot is not null)
        {
            Changed?.Invoke(this, changedSnapshot);
        }
    }

    public static HistoricalDamageScale SelectDamageScale(
        IEnumerable<long?> damageValues)
    {
        var positive = damageValues
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .ToArray();
        if (positive.Length < 2)
        {
            return HistoricalDamageScale.Linear;
        }

        var minimum = positive.Min();
        var maximum = positive.Max();
        return maximum / (double)minimum >= LogarithmicScaleRatioThreshold
            ? HistoricalDamageScale.Logarithmic
            : HistoricalDamageScale.Linear;
    }

    public static double NormalizeDamage(
        long? damage,
        IEnumerable<long?> damageValues,
        HistoricalDamageScale scale)
    {
        if (damage is not > 0)
        {
            return 0;
        }

        var maximum = damageValues
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (maximum <= 0)
        {
            return 0;
        }

        var normalized = scale == HistoricalDamageScale.Logarithmic
            ? Math.Log10(1 + (double)damage.Value) /
              Math.Log10(1 + (double)maximum)
            : damage.Value / (double)maximum;
        return Math.Clamp(normalized, 0, 1);
    }

    private bool EnsureRun(string runId)
    {
        if (string.Equals(_runId, runId, StringComparison.Ordinal))
        {
            return false;
        }

        _runId = runId;
        _nodes.Clear();
        _lastResolvedNodeId = null;
        return true;
    }

    private MutableNode GetOrCreateNode(string runId, string nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var existing))
        {
            return existing;
        }

        var created = new MutableNode(runId, nodeId);
        _nodes.Add(nodeId, created);
        return created;
    }

    private static bool ObservePreparation(
        MutableNode node,
        Phase2OperationalState state,
        RunSnapshot snapshot)
    {
        if (state.PageFamily != Phase2PageFamily.Preparation)
        {
            return false;
        }

        var changed = false;
        if (snapshot.Economy.Status == ObservationStatus.Known &&
            node.PreBattleGold != snapshot.Economy.Value)
        {
            node.PreBattleGold = snapshot.Economy.Value;
            changed = true;
        }

        int? cumulativeSpend = state.CumulativeSpend.Status ==
                               ObservationStatus.Known
            ? (int?)state.CumulativeSpend.Value
            : snapshot.CumulativeSpend.Status == ObservationStatus.Known
                ? (int?)snapshot.CumulativeSpend.Value
                : null;
        if (cumulativeSpend is not null &&
            node.CumulativeSpend != cumulativeSpend)
        {
            node.CumulativeSpend = cumulativeSpend;
            changed = true;
        }

        if (changed)
        {
            node.UpdatedAt = snapshot.AsOf;
        }

        return changed;
    }

    private bool BackfillPreviousNodeEndingGold(
        MutableNode current,
        RunSnapshot snapshot)
    {
        if (snapshot.Economy.Status != ObservationStatus.Known ||
            !RunResumePolicy.TryGetNodeRank(current.NodeId, out var currentRank))
        {
            return false;
        }

        var previous = _nodes.Values.FirstOrDefault(candidate =>
            candidate.IsFinalized &&
            RunResumePolicy.TryGetNodeRank(candidate.NodeId, out var candidateRank) &&
            candidateRank == currentRank - 1);
        if (previous is null || previous.EndingGold == snapshot.Economy.Value)
        {
            return false;
        }

        previous.EndingGold = snapshot.Economy.Value;
        previous.UpdatedAt = snapshot.AsOf;
        return true;
    }

    private static bool ObserveFinalBattle(
        MutableNode node,
        FinalNodeBattleState? battle)
    {
        if (battle is null ||
            !string.Equals(
                node.NodeId,
                battle.NodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var damage = battle.SelectedDamage ?? battle.TotalDamage;
        var remainingAction = battle.RemainingActionValue?.TotalActionValue;
        var complete = battle.IsComplete &&
                       damage is not null &&
                       remainingAction is not null;
        var detailChanged = node.FinalBattle != battle;
        if (node.FinalDamage == damage &&
            node.RemainingActionValue == remainingAction &&
            node.GoldReward == battle.GoldReward &&
            node.ClearStatus == battle.ClearStatus &&
            node.HealthDelta == battle.HealthDelta &&
            node.TheoreticalDamage == battle.TheoreticalDamageLimit &&
            node.TheoreticalDamageQuality == battle.TheoreticalDamageQuality &&
            node.IsRewardNode == battle.IsRewardNode &&
            node.HealthDepleted == battle.HealthDepleted &&
            node.IsComplete == complete &&
            node.IsFinalized &&
            !detailChanged)
        {
            return false;
        }

        node.FinalDamage = damage;
        node.RemainingActionValue = remainingAction;
        node.GoldReward = battle.GoldReward;
        node.ClearStatus = battle.ClearStatus;
        node.HealthDelta = battle.HealthDelta;
        node.TheoreticalDamage = battle.TheoreticalDamageLimit;
        node.TheoreticalDamageQuality = battle.TheoreticalDamageQuality;
        node.IsRewardNode = battle.IsRewardNode;
        node.HealthDepleted = battle.HealthDepleted;
        node.IsComplete = complete;
        node.IsFinalized = true;
        node.FinalBattle = battle;
        node.UpdatedAt = battle.CapturedAt;
        return true;
    }

    private static ScreenshotAnalysisResult ObserveDetails(
        MutableNode node,
        ScreenshotAnalysisResult analysis)
    {
        var snapshot = node.StabilizeSnapshot(analysis.Snapshot);
        var state = node.StabilizeState(analysis.OperationalState, snapshot);
        node.LatestSnapshot = MergeSnapshot(
            node.LatestSnapshot,
            snapshot);
        node.LatestState = MergeState(node.LatestState, state);
        if (state is not null)
        {
            node.LatestPreparationState = MergeState(
                node.LatestPreparationState,
                state);
        }

        var stableAnalysis = analysis with
        {
            Snapshot = snapshot,
            OperationalState = state
        };
        node.LatestAnalysis = stableAnalysis;
        node.UpdatedAt = snapshot.AsOf;
        return stableAnalysis;
    }

    private static RunSnapshot MergeSnapshot(
        RunSnapshot? previous,
        RunSnapshot current)
    {
        if (previous is null)
        {
            return current;
        }

        return current with
        {
            PageId = Prefer(previous.PageId, current.PageId),
            Stage = Prefer(previous.Stage, current.Stage),
            Economy = Prefer(previous.Economy, current.Economy),
            CumulativeSpend = Prefer(
                previous.CumulativeSpend,
                current.CumulativeSpend),
            Health = Prefer(previous.Health, current.Health),
            StoreLevel = Prefer(previous.StoreLevel, current.StoreLevel),
            ActionPoints = Prefer(previous.ActionPoints, current.ActionPoints),
            CurrentNodeDamage = Prefer(
                previous.CurrentNodeDamage,
                current.CurrentNodeDamage),
            BoardCharacterIds = Prefer(
                previous.BoardCharacterIds,
                current.BoardCharacterIds),
            BenchCharacterIds = Prefer(
                previous.BenchCharacterIds,
                current.BenchCharacterIds),
            ShopCharacterIds = Prefer(
                previous.ShopCharacterIds,
                current.ShopCharacterIds),
            LineupIds = Prefer(previous.LineupIds, current.LineupIds),
            SynergyIds = Prefer(previous.SynergyIds, current.SynergyIds),
            InvestmentEnvironmentId = Prefer(
                previous.InvestmentEnvironmentId,
                current.InvestmentEnvironmentId),
            InvestmentStrategyIds = MergeMonotonicStrategySet(
                previous.InvestmentStrategyIds,
                current.InvestmentStrategyIds),
            EquipmentIds = Prefer(previous.EquipmentIds, current.EquipmentIds),
            SpecialItemIds = Prefer(
                previous.SpecialItemIds,
                current.SpecialItemIds),
            InventorySlots = RunCheckpointFactory.MergeInventoryObservations(
                previous.InventorySlots,
                current.InventorySlots,
                markCurrentUnavailableAsStale: true),
            ExpertAdvisorIds = Prefer(
                previous.ExpertAdvisorIds,
                current.ExpertAdvisorIds),
            EnemyIds = Prefer(previous.EnemyIds, current.EnemyIds),
            Nodes = current.Nodes.Count > 0 ? current.Nodes : previous.Nodes,
            AppliedEventIds = previous.AppliedEventIds
                .Concat(current.AppliedEventIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = previous.Diagnostics
                .Concat(current.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static Phase2OperationalState? MergeState(
        Phase2OperationalState? previous,
        Phase2OperationalState? current)
    {
        if (current is null)
        {
            return previous;
        }

        if (previous is null)
        {
            return current;
        }

        return current with
        {
            NodeId = Prefer(previous.NodeId, current.NodeId),
            EnemyDifficulty = Prefer(
                previous.EnemyDifficulty,
                current.EnemyDifficulty),
            Interest = Prefer(previous.Interest, current.Interest),
            CumulativeSpend = Prefer(
                previous.CumulativeSpend,
                current.CumulativeSpend),
            PlayerProgress = Prefer(
                previous.PlayerProgress,
                current.PlayerProgress),
            StoreLevel = Prefer(previous.StoreLevel, current.StoreLevel),
            Formation = Prefer(previous.Formation, current.Formation),
            ActiveSynergies = Prefer(
                previous.ActiveSynergies,
                current.ActiveSynergies),
            DismantleToolCount = Prefer(
                previous.DismantleToolCount,
                current.DismantleToolCount),
            SimpleEquipmentIds = Prefer(
                previous.SimpleEquipmentIds,
                current.SimpleEquipmentIds),
            SpecialItemIds = Prefer(
                previous.SpecialItemIds,
                current.SpecialItemIds),
            InventorySlots = RunCheckpointFactory.MergeInventoryObservations(
                previous.InventorySlots,
                current.InventorySlots,
                markCurrentUnavailableAsStale: true),
            NegativeAffixIds = Prefer(
                previous.NegativeAffixIds,
                current.NegativeAffixIds),
            InvestmentEnvironmentId = Prefer(
                previous.InvestmentEnvironmentId,
                current.InvestmentEnvironmentId),
            InvestmentStrategyIds = MergeMonotonicStrategySet(
                previous.InvestmentStrategyIds,
                current.InvestmentStrategyIds),
            BattleDamage = Prefer(previous.BattleDamage, current.BattleDamage),
            BattleSynergyDamage = Prefer(
                previous.BattleSynergyDamage,
                current.BattleSynergyDamage),
            BattleUnresolvedDamage = Prefer(
                previous.BattleUnresolvedDamage,
                current.BattleUnresolvedDamage),
            BattleScreenDamageCandidate = Prefer(
                previous.BattleScreenDamageCandidate,
                current.BattleScreenDamageCandidate),
            SettlementDamage = Prefer(
                previous.SettlementDamage,
                current.SettlementDamage),
            SettlementScreenDamageCandidate = Prefer(
                previous.SettlementScreenDamageCandidate,
                current.SettlementScreenDamageCandidate),
            SettlementGoldReward = Prefer(
                previous.SettlementGoldReward,
                current.SettlementGoldReward),
            RemainingActionValue = Prefer(
                previous.RemainingActionValue,
                current.RemainingActionValue),
            FinalBattle = Prefer(previous.FinalBattle, current.FinalBattle),
            NamedContent = previous.NamedContent
                .Concat(current.NamedContent)
                .GroupBy(item => (item.Kind, item.SlotKey))
                .Select(group => group.OrderByDescending(item => item.Confidence).First())
                .ToArray(),
            PendingIcons = previous.PendingIcons
                .Concat(current.PendingIcons)
                .GroupBy(item => (item.Category, item.SlotKey))
                .Select(group => group.OrderByDescending(item => item.Confidence).First())
                .ToArray(),
            PartialFields = previous.PartialFields
                .Concat(current.PartialFields)
                .GroupBy(item => (item.Field, item.TemporaryId))
                .Select(group => group.OrderByDescending(item => item.Confidence).First())
                .ToArray(),
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

    private static Observation<T> Prefer<T>(
        Observation<T> previous,
        Observation<T> current)
    {
        if (current.Status == ObservationStatus.Known)
        {
            return current;
        }

        if (previous.Status == ObservationStatus.Known)
        {
            return previous;
        }

        return current.Value is not null || previous.Value is null
            ? current
            : previous;
    }

    private static Observation<IReadOnlyList<string>>
        MergeMonotonicStrategySet(
            Observation<IReadOnlyList<string>> previous,
            Observation<IReadOnlyList<string>> current)
    {
        if (current.Value is null || current.Value.Count == 0)
        {
            return Prefer(previous, current);
        }

        var merged = (previous.Value ?? [])
            .Concat(current.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (current.Status == ObservationStatus.Known)
        {
            return Observation<IReadOnlyList<string>>.Known(
                merged,
                Math.Max(previous.Confidence, current.Confidence),
                previous.Evidence.Concat(current.Evidence).Distinct(),
                current.ObservedAt ?? previous.ObservedAt);
        }

        return current with
        {
            Value = merged,
            Evidence = previous.Evidence.Concat(current.Evidence)
                .Distinct()
                .ToArray(),
            Uncertainty = previous.Uncertainty.Concat(current.Uncertainty)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ObservedAt = current.ObservedAt ?? previous.ObservedAt
        };
    }

    private bool RecalculateEconomyDeltas()
    {
        var changed = false;
        MutableNode? previous = null;
        foreach (var node in _nodes.Values.OrderBy(item => item.NodeId, NodeIdComparer.Instance))
        {
            int? spent = null;
            int? delta = null;
            if (previous is null)
            {
                // Cumulative spend starts at zero for a new run, so the first
                // reliable preparation snapshot is already the first node's
                // spend total. Starting gold is environment-dependent and is
                // therefore not inferred.
                spent = node.CumulativeSpend;
            }
            else
            {
                if (node.CumulativeSpend is not null &&
                    previous.CumulativeSpend is not null &&
                    node.CumulativeSpend >= previous.CumulativeSpend)
                {
                    spent = node.CumulativeSpend - previous.CumulativeSpend;
                }

                if (node.PreBattleGold is not null &&
                    previous.PreBattleGold is not null)
                {
                    delta = node.PreBattleGold - previous.PreBattleGold;
                }
            }

            if (node.GoldSpentSincePreviousNode != spent ||
                node.GoldDeltaSincePreviousNode != delta)
            {
                node.GoldSpentSincePreviousNode = spent;
                node.GoldDeltaSincePreviousNode = delta;
                changed = true;
            }

            previous = node;
        }

        return changed;
    }

    private HistoricalDashboardSnapshot RebuildSnapshot()
    {
        var nodes = _nodes.Values
            .Where(node => node.IsFinalized)
            .OrderBy(node => node.NodeId, NodeIdComparer.Instance)
            .Select(node => new HistoricalNodeDashboardEntry(
                node.RunId,
                node.NodeId,
                node.FinalDamage,
                node.RemainingActionValue,
                node.GoldSpentSincePreviousNode,
                node.GoldDeltaSincePreviousNode,
                node.GoldReward,
                node.UpdatedAt,
                node.IsComplete,
                node.ClearStatus,
                node.HealthDelta,
                node.EndingGold,
                node.TheoreticalDamage,
                node.TheoreticalDamageQuality,
                node.IsRewardNode,
                node.HealthDepleted))
            .ToArray();
        var scale = SelectDamageScale(nodes
            .Where(node => !IsRewardNodeId(node.NodeId))
            .Select(node => node.FinalDamage));
        var theoryScale = SelectDamageScale(nodes
            .Where(node => !IsRewardNodeId(node.NodeId))
            .Select(node => node.TheoreticalDamage));
        _current = new HistoricalDashboardSnapshot(
            _runId,
            nodes,
            scale,
            theoryScale)
        {
            DetailNodes = _nodes.Values
                .OrderBy(node => node.NodeId, NodeIdComparer.Instance)
                .Select(node => new HistoricalNodeDetailEntry(
                    node.RunId,
                    node.NodeId,
                    node.LatestSnapshot,
                    node.LatestState,
                    node.LatestPreparationState,
                    node.FinalBattle,
                    node.LatestAnalysis,
                    node.UpdatedAt))
                .ToArray()
        };
        return _current;
    }

    private string? ResolveNodeId(
        Phase2OperationalState state,
        RunSnapshot snapshot)
    {
        var candidate = state.NodeId.Status == ObservationStatus.Known
            ? state.NodeId.Value
            : snapshot.Stage.Status == ObservationStatus.Known
                ? snapshot.Stage.Value
                : null;
        if (IsCanonicalNodeId(candidate))
        {
            _lastResolvedNodeId = candidate;
        }

        return _lastResolvedNodeId;
    }

    private static bool IsCanonicalNodeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf('-');
        return separator == 1 &&
               value[0] is >= '1' and <= '3' &&
               separator < value.Length - 1 &&
               value[(separator + 1)..].All(char.IsDigit);
    }

    /// <summary>
    /// 奖励节点判定（用户 2026-08-05 规则）：1-8、2-6、3-6 是奖励关。
    /// 与图表侧 DashboardTrendChart.IsRewardNodeId 保持完全一致；
    /// 不要用 IsRewardNode 字段（那是页面类型 reward_battle/battle_generic
    /// = 1-1/1-2 奖励关页面，语义不同，会把几乎全部节点误判为奖励，
    /// 导致 SelectDamageScale 只剩 1 个正数值而强制线性——实机
    /// run-20260805-023158 最终伤害图被拉平即此根因）。
    /// </summary>
    private static bool IsRewardNodeId(string? nodeId) =>
        nodeId is "1-8" or "2-6" or "3-6";

    private sealed class MutableNode(string runId, string nodeId)
    {
        private readonly ObservationConsensus<int> _economy = new();
        private readonly ObservationConsensus<int> _health = new();
        private readonly ObservationConsensus<int> _storeLevel = new();
        private readonly ObservationConsensus<int> _cumulativeSpend = new();
        private readonly ObservationConsensus<int> _enemyDifficulty = new();
        private readonly ObservationConsensus<int> _interest = new();
        private readonly ObservationConsensus<int> _population = new();
        private readonly ObservationConsensus<int> _dismantleToolCount = new();

        public string RunId { get; } = runId;
        public string NodeId { get; } = nodeId;
        public int? PreBattleGold { get; set; }
        public int? EndingGold { get; set; }
        public int? CumulativeSpend { get; set; }
        public long? FinalDamage { get; set; }
        public int? RemainingActionValue { get; set; }
        public int? GoldSpentSincePreviousNode { get; set; }
        public int? GoldDeltaSincePreviousNode { get; set; }
        public int? GoldReward { get; set; }
        public NodeClearStatus ClearStatus { get; set; } = NodeClearStatus.Unknown;
        public int? HealthDelta { get; set; }
        public long? TheoreticalDamage { get; set; }
        public TheoreticalDamageQuality TheoreticalDamageQuality { get; set; } =
            TheoreticalDamageQuality.Unknown;
        public bool IsRewardNode { get; set; }
        public bool HealthDepleted { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public bool IsFinalized { get; set; }
        public bool IsComplete { get; set; }
        public RunSnapshot? LatestSnapshot { get; set; }
        public Phase2OperationalState? LatestState { get; set; }
        public Phase2OperationalState? LatestPreparationState { get; set; }
        public FinalNodeBattleState? FinalBattle { get; set; }
        public ScreenshotAnalysisResult? LatestAnalysis { get; set; }

        public RunSnapshot StabilizeSnapshot(RunSnapshot snapshot) => snapshot with
        {
            Economy = _economy.Observe(snapshot.Economy),
            Health = _health.Observe(snapshot.Health),
            StoreLevel = _storeLevel.Observe(snapshot.StoreLevel),
            CumulativeSpend = _cumulativeSpend.Observe(snapshot.CumulativeSpend),
            ShopCharacterIds = Observation<IReadOnlyList<string>>.Unknown(
                "商店角色是临时决策数据，不写入节点历史",
                observedAt: snapshot.AsOf),
            ExpertAdvisorIds = Observation<IReadOnlyList<string>>.Unknown(
                "专家不属于节点历史字段",
                observedAt: snapshot.AsOf),
            EnemyIds = Observation<IReadOnlyList<string>>.Unknown(
                "敌人不属于节点历史字段",
                observedAt: snapshot.AsOf)
        };

        public Phase2OperationalState? StabilizeState(
            Phase2OperationalState? state,
            RunSnapshot snapshot)
        {
            if (state is null)
            {
                return null;
            }

            return state with
            {
                Health = snapshot.Health,
                StoreLevel = snapshot.StoreLevel,
                CumulativeSpend = snapshot.CumulativeSpend,
                EnemyDifficulty = _enemyDifficulty.Observe(state.EnemyDifficulty),
                Interest = _interest.Observe(state.Interest),
                Population = _population.Observe(state.Population),
                DismantleToolCount = _dismantleToolCount.Observe(
                    state.DismantleToolCount)
            };
        }
    }

    private sealed class ObservationConsensus<T>
    {
        private const double ImmediateConfidence = 0.85;
        private const int RequiredMatchingFrames = 2;

        private Observation<T>? _stable;
        private Observation<T>? _candidate;
        private int _candidateCount;

        public Observation<T> Observe(Observation<T> current)
        {
            if (current.Status != ObservationStatus.Known || current.Value is null)
            {
                return _stable ?? current;
            }

            if (current.Confidence >= ImmediateConfidence)
            {
                _stable = current;
                _candidate = null;
                _candidateCount = 0;
                return current;
            }

            if (_stable is { } stable &&
                stable.Value is not null &&
                EqualityComparer<T>.Default.Equals(stable.Value, current.Value))
            {
                _stable = MergeEvidence(stable, current);
                _candidate = null;
                _candidateCount = 0;
                return _stable;
            }

            if (_candidate is { } candidate &&
                candidate.Value is not null &&
                EqualityComparer<T>.Default.Equals(candidate.Value, current.Value))
            {
                _candidate = MergeEvidence(candidate, current);
                _candidateCount++;
            }
            else
            {
                _candidate = current;
                _candidateCount = 1;
            }

            if (_candidateCount >= RequiredMatchingFrames)
            {
                _stable = _candidate;
                _candidate = null;
                _candidateCount = 0;
                return _stable;
            }

            return _stable ?? Observation<T>.Unknown(
                "等待连续帧确认",
                current.Evidence,
                current.ObservedAt);
        }

        private static Observation<T> MergeEvidence(
            Observation<T> previous,
            Observation<T> current) => current with
            {
                Confidence = Math.Max(previous.Confidence, current.Confidence),
                Evidence = previous.Evidence
                    .Concat(current.Evidence)
                    .Distinct()
                    .ToArray()
            };
    }

    private sealed class NodeIdComparer : IComparer<string>
    {
        public static NodeIdComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftParsed = TryParse(left, out var leftParts);
            var rightParsed = TryParse(right, out var rightParts);
            if (leftParsed && rightParsed)
            {
                var plane = leftParts.Plane.CompareTo(rightParts.Plane);
                return plane != 0
                    ? plane
                    : leftParts.Node.CompareTo(rightParts.Node);
            }

            if (leftParsed != rightParsed)
            {
                return leftParsed ? -1 : 1;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static bool TryParse(
            string value,
            out (int Plane, int Node) result)
        {
            var parts = value.Split('-', 2);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var plane) &&
                int.TryParse(parts[1], out var node))
            {
                result = (plane, node);
                return true;
            }

            result = default;
            return false;
        }
    }
}
