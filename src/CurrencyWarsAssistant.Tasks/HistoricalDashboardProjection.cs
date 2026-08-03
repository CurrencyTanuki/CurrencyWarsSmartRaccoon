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
                    changed |= ObserveDetails(node, analysis);
                    if (!string.Equals(
                            state.PageId,
                            "reward_shop",
                            StringComparison.Ordinal))
                    {
                        // 补给选择页（reward_shop）不是备战页：其金币不能作为
                        // 备战数据写入节点，也不回填上一节点的 EndingGold。
                        changed |= ObservePreparation(node, state, analysis.Snapshot);
                        changed |= BackfillPreviousNodeEndingGold(
                            node,
                            analysis.Snapshot);
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

    private static bool ObserveDetails(
        MutableNode node,
        ScreenshotAnalysisResult analysis)
    {
        var state = analysis.OperationalState;
        node.LatestSnapshot = MergeSnapshot(
            node.LatestSnapshot,
            analysis.Snapshot);
        node.LatestState = MergeState(node.LatestState, state);
        if (state?.PageFamily == Phase2PageFamily.Preparation)
        {
            node.LatestPreparationState = MergeState(
                node.LatestPreparationState,
                state);
        }

        node.LatestAnalysis = analysis;
        node.UpdatedAt = analysis.Snapshot.AsOf;
        return true;
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
            .Where(node => !node.IsRewardNode)
            .Select(node => node.FinalDamage));
        var theoryScale = SelectDamageScale(nodes
            .Where(node => !node.IsRewardNode)
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

    private sealed class MutableNode(string runId, string nodeId)
    {
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
