using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurrencyWarsAssistant.Advisor;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunCheckpointLifecycleStatus
{
    Candidate,
    Active,
    Paused,
    Completed,
    Abandoned
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunEntryMode
{
    AutomaticReroll,
    DirectRecording,
    Resumed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunCheckpointHealth
{
    Healthy,
    RecoveredFromBackup,
    PartiallyRecovered,
    SynthesizedFromArtifacts
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunResumeDecisionKind
{
    ContinueExisting,
    CreateNewRun,
    RequireUserChoice
}

public sealed record RunDataCompleteness(
    int KnownFieldCount,
    int TrackedFieldCount,
    int RecordedNodeCount,
    int MissingNodeCount)
{
    public double Ratio => TrackedFieldCount <= 0
        ? 0
        : Math.Clamp((double)KnownFieldCount / TrackedFieldCount, 0, 1);
}

public sealed record RunIdentityEvidence
{
    public string? InvestmentEnvironmentId { get; init; }
    public IReadOnlyList<string> InvestmentStrategyIds { get; init; } = [];
    public IReadOnlyList<string> EnemyAffixIds { get; init; } = [];
    public IReadOnlyList<string> EnemyIds { get; init; } = [];
}

public sealed record RunCheckpointRecord
{
    public string SchemaVersion { get; init; } = AdvisorContractVersions.Current;
    public int CheckpointVersion { get; init; } = 1;
    public required string RunId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset LastSavedAtUtc { get; init; }
    public RunCheckpointLifecycleStatus LifecycleStatus { get; init; } =
        RunCheckpointLifecycleStatus.Active;
    public RunEntryMode EntryMode { get; init; } = RunEntryMode.DirectRecording;
    public int ResumeCount { get; init; }
    public int SavedObservationCount { get; init; }
    public string? LastConfirmedNodeId { get; init; }
    public string? LastConfirmedPageId { get; init; }
    public RunSnapshot? LastSnapshot { get; init; }
    public Phase2OperationalState? LastOperationalState { get; init; }
    public IReadOnlyList<string> FinalizedNodeIds { get; init; } = [];
    public IReadOnlyList<string> MissingNodeIds { get; init; } = [];
    public RunDataCompleteness DataCompleteness { get; init; } = new(0, 0, 0, 0);
    public RunIdentityEvidence IdentityEvidence { get; init; } = new();
    public IReadOnlyList<string> Uncertainty { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record RunCheckpointSummary(
    RunCheckpointRecord Checkpoint,
    RunCheckpointHealth Health,
    string CheckpointFile,
    IReadOnlyList<string> Diagnostics);

public sealed record RunResumeObservation(
    string? NodeId,
    RunIdentityEvidence IdentityEvidence,
    DateTimeOffset ObservedAt);

public sealed record RunResumeDecision(
    RunResumeDecisionKind Kind,
    IReadOnlyList<string> MissingNodeIds,
    IReadOnlyList<string> Reasons);

public static class RunResumePolicy
{
    private const int NodesPerPlane = 9;
    private const int MaximumPlane = 3;

    public static RunResumeDecision Decide(
        RunCheckpointRecord checkpoint,
        RunResumeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(observation);

        var identityConflicts = FindIdentityConflicts(
            checkpoint.IdentityEvidence,
            observation.IdentityEvidence);
        if (identityConflicts.Count > 0)
        {
            return new RunResumeDecision(
                RunResumeDecisionKind.RequireUserChoice,
                checkpoint.MissingNodeIds,
                identityConflicts);
        }

        if (!TryGetNodeRank(checkpoint.LastConfirmedNodeId, out var previousRank))
        {
            return new RunResumeDecision(
                RunResumeDecisionKind.RequireUserChoice,
                checkpoint.MissingNodeIds,
                ["The saved run has no comparable last node."]);
        }

        if (!TryGetNodeRank(observation.NodeId, out var observedRank))
        {
            return new RunResumeDecision(
                RunResumeDecisionKind.RequireUserChoice,
                checkpoint.MissingNodeIds,
                ["The current game node could not be confirmed."]);
        }

        if (observedRank < previousRank)
        {
            return new RunResumeDecision(
                RunResumeDecisionKind.CreateNewRun,
                [],
                ["The current game node is earlier than the saved run."]);
        }

        var missing = checkpoint.MissingNodeIds
            .Concat(EnumerateMissingNodes(previousRank, observedRank))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NodeSortKey)
            .ToArray();
        return new RunResumeDecision(
            RunResumeDecisionKind.ContinueExisting,
            missing,
            observedRank == previousRank
                ? ["The current game is at the saved node."]
                : ["The current game is at a later node; skipped nodes remain missing."]);
    }

    public static bool TryGetNodeRank(string? nodeId, out int rank)
    {
        rank = 0;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        var parts = nodeId.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var plane) ||
            !int.TryParse(parts[1], out var index) ||
            plane is < 1 or > MaximumPlane ||
            index is < 1 or > NodesPerPlane)
        {
            return false;
        }

        rank = ((plane - 1) * NodesPerPlane) + index;
        return true;
    }

    public static bool TryGetPreviousNode(string? nodeId, out string previousNode)
    {
        previousNode = string.Empty;
        if (!TryGetNodeRank(nodeId, out var rank) || rank <= 1)
        {
            return false;
        }

        var previousRank = rank - 1;
        while (previousRank > 0 && IsSupplyNodeRank(previousRank))
        {
            previousRank--;
        }

        if (previousRank <= 0)
        {
            return false;
        }

        var plane = ((previousRank - 1) / NodesPerPlane) + 1;
        var index = ((previousRank - 1) % NodesPerPlane) + 1;
        previousNode = $"{plane}-{index}";
        return true;
    }

    private static IReadOnlyList<string> FindIdentityConflicts(
        RunIdentityEvidence saved,
        RunIdentityEvidence observed)
    {
        var conflicts = new List<string>();
        if (!string.IsNullOrWhiteSpace(saved.InvestmentEnvironmentId) &&
            !string.IsNullOrWhiteSpace(observed.InvestmentEnvironmentId) &&
            !string.Equals(
                saved.InvestmentEnvironmentId,
                observed.InvestmentEnvironmentId,
                StringComparison.OrdinalIgnoreCase))
        {
            conflicts.Add("The investment environment conflicts with the saved run.");
        }

        if (HasSetConflict(
                saved.InvestmentStrategyIds,
                observed.InvestmentStrategyIds,
                allowObservedSuperset: true))
        {
            conflicts.Add("The investment strategy set conflicts with the saved run.");
        }

        if (HasSetConflict(saved.EnemyAffixIds, observed.EnemyAffixIds))
        {
            conflicts.Add("The enemy affix set conflicts with the saved run.");
        }

        if (HasSetConflict(saved.EnemyIds, observed.EnemyIds))
        {
            conflicts.Add("The enemy overview conflicts with the saved run.");
        }

        return conflicts;
    }

    private static bool HasSetConflict(
        IReadOnlyList<string> saved,
        IReadOnlyList<string> observed,
        bool allowObservedSuperset = false)
    {
        if (saved.Count == 0 || observed.Count == 0)
        {
            return false;
        }

        var savedSet = saved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observedSet = observed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allowObservedSuperset
            ? !savedSet.IsSubsetOf(observedSet)
            : !savedSet.SetEquals(observedSet);
    }

    private static IEnumerable<string> EnumerateMissingNodes(
        int previousRank,
        int observedRank)
    {
        for (var rank = previousRank + 1; rank < observedRank; rank++)
        {
            if (IsSupplyNodeRank(rank))
            {
                continue;
            }

            var plane = ((rank - 1) / NodesPerPlane) + 1;
            var index = ((rank - 1) % NodesPerPlane) + 1;
            yield return $"{plane}-{index}";
        }
    }

    private static int NodeSortKey(string nodeId) =>
        TryGetNodeRank(nodeId, out var rank) ? rank : int.MaxValue;

    private static bool IsSupplyNodeRank(int rank) =>
        ((rank - 1) % NodesPerPlane) + 1 == 5;
}

public static class RunCheckpointFactory
{
    private const int TrackedFieldCount = 17;

    public static RunCheckpointRecord CreateInitial(
        string runId,
        RunEntryMode entryMode,
        DateTimeOffset createdAt) => new()
    {
        RunId = runId,
        CreatedAtUtc = createdAt,
        LastSavedAtUtc = createdAt,
        LifecycleStatus = RunCheckpointLifecycleStatus.Active,
        EntryMode = entryMode,
        DataCompleteness = new RunDataCompleteness(0, TrackedFieldCount, 0, 0)
    };

    public static RunCheckpointRecord FromAnalysis(
        RunCheckpointRecord current,
        ScreenshotAnalysisResult analysis,
        int savedObservationCount,
        RunCheckpointLifecycleStatus lifecycleStatus,
        DateTimeOffset savedAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(analysis);

        var nodeId = KnownValue(analysis.OperationalState?.NodeId) ??
                     KnownValue(analysis.Snapshot.Stage) ??
                     current.LastConfirmedNodeId;
        var pageId = KnownValue(analysis.Snapshot.PageId) ??
                     analysis.OperationalState?.PageId ??
                     current.LastConfirmedPageId;
        var finalizedNodes = current.FinalizedNodeIds.ToList();
        var finalBattle = analysis.OperationalState?.FinalBattle.Value;
        if (finalBattle is not null &&
            !string.IsNullOrWhiteSpace(finalBattle.NodeId))
        {
            finalizedNodes.Add(finalBattle.NodeId);
        }

        var checkpointSnapshot = MergeCheckpointSnapshot(
            current.LastSnapshot,
            analysis.Snapshot);
        var checkpointOperationalState = MergeCheckpointOperationalState(
            current.LastOperationalState,
            analysis.OperationalState);
        var identity = MergeIdentity(current.IdentityEvidence, analysis);
        var knownFieldCount = CountKnownFields(
            checkpointSnapshot,
            checkpointOperationalState);
        var finalized = finalizedNodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => RunResumePolicy.TryGetNodeRank(value, out var rank)
                ? rank
                : int.MaxValue)
            .ToArray();
        return current with
        {
            LastSavedAtUtc = savedAt,
            LifecycleStatus = lifecycleStatus,
            SavedObservationCount = savedObservationCount,
            LastConfirmedNodeId = nodeId,
            LastConfirmedPageId = pageId,
            LastSnapshot = checkpointSnapshot,
            LastOperationalState = checkpointOperationalState,
            FinalizedNodeIds = finalized,
            DataCompleteness = new RunDataCompleteness(
                knownFieldCount,
                TrackedFieldCount,
                finalized.Length,
                current.MissingNodeIds.Count),
            IdentityEvidence = identity,
            Uncertainty = analysis.Warnings
                .Concat(analysis.UnknownFields.Select(value => $"Unknown field: {value}"))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    internal static RunSnapshot MergeCheckpointSnapshot(
        RunSnapshot? previous,
        RunSnapshot current)
    {
        if (previous is null)
        {
            return current;
        }

        return current with
        {
            PageId = KeepLastReliable(previous.PageId, current.PageId),
            Stage = KeepLastReliable(previous.Stage, current.Stage),
            Economy = KeepLastReliable(previous.Economy, current.Economy),
            CumulativeSpend = KeepLastReliable(
                previous.CumulativeSpend,
                current.CumulativeSpend),
            Health = KeepLastReliable(previous.Health, current.Health),
            ActionPoints = KeepLastReliable(
                previous.ActionPoints,
                current.ActionPoints),
            CurrentNodeDamage = KeepLastReliable(
                previous.CurrentNodeDamage,
                current.CurrentNodeDamage),
            BoardCharacterIds = KeepLastReliable(
                previous.BoardCharacterIds,
                current.BoardCharacterIds),
            BenchCharacterIds = KeepLastReliable(
                previous.BenchCharacterIds,
                current.BenchCharacterIds),
            ShopCharacterIds = KeepLastReliable(
                previous.ShopCharacterIds,
                current.ShopCharacterIds),
            LineupIds = KeepLastReliable(previous.LineupIds, current.LineupIds),
            SynergyIds = KeepLastReliable(previous.SynergyIds, current.SynergyIds),
            InvestmentEnvironmentId = KeepStableIdentity(
                previous.InvestmentEnvironmentId,
                current.InvestmentEnvironmentId),
            InvestmentStrategyIds = KeepMonotonicPartialStringSet(
                previous.InvestmentStrategyIds,
                current.InvestmentStrategyIds),
            EquipmentIds = KeepLastReliable(
                previous.EquipmentIds,
                current.EquipmentIds),
            SpecialItemIds = KeepLastReliable(
                previous.SpecialItemIds,
                current.SpecialItemIds),
            InventorySlots = MergeInventoryObservations(
                previous.InventorySlots,
                current.InventorySlots,
                markCurrentUnavailableAsStale: true),
            ExpertAdvisorIds = Observation<IReadOnlyList<string>>.Unknown(
                "legacy field is no longer collected"),
            EnemyIds = KeepMonotonicStringSet(
                previous.EnemyIds,
                current.EnemyIds),
            Nodes = current.Nodes.Count > 0 ? current.Nodes : previous.Nodes,
            AppliedEventIds = previous.AppliedEventIds
                .Concat(current.AppliedEventIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = previous.Diagnostics
                .Concat(current.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .TakeLast(100)
                .ToArray()
        };
    }

    internal static Phase2OperationalState? MergeCheckpointOperationalState(
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
            NodeId = KeepLastReliable(previous.NodeId, current.NodeId),
            EnemyDifficulty = KeepLastReliable(
                previous.EnemyDifficulty,
                current.EnemyDifficulty),
            StoreLevel = KeepLastReliable(
                previous.StoreLevel,
                current.StoreLevel),
            Interest = KeepLastReliable(previous.Interest, current.Interest),
            CumulativeSpend = KeepLastReliable(
                previous.CumulativeSpend,
                current.CumulativeSpend),
            PlayerProgress = KeepLastReliable(
                previous.PlayerProgress,
                current.PlayerProgress),
            Formation = MergeFormationObservations(
                previous.Formation,
                current.Formation,
                markCurrentUnavailableAsStale: true,
                current.FormationSlotObservations,
                current.FormationSlotObservationsAreAtomic,
                current.FormationSlotObservationsArePartial),
            ActiveSynergies = MergeActiveSynergyObservations(
                previous.ActiveSynergies,
                current.ActiveSynergies),
            DismantleToolCount = KeepLastReliable(
                previous.DismantleToolCount,
                current.DismantleToolCount),
            SimpleEquipmentIds = KeepLastReliable(
                previous.SimpleEquipmentIds,
                current.SimpleEquipmentIds),
            SpecialItemIds = KeepLastReliable(
                previous.SpecialItemIds,
                current.SpecialItemIds),
            InventorySlots = MergeInventoryObservations(
                previous.InventorySlots,
                current.InventorySlots,
                markCurrentUnavailableAsStale: true),
            NegativeAffixIds = KeepMonotonicStringSet(
                previous.NegativeAffixIds,
                current.NegativeAffixIds),
            InvestmentEnvironmentId = KeepStableIdentity(
                previous.InvestmentEnvironmentId,
                current.InvestmentEnvironmentId),
            InvestmentStrategyIds = KeepMonotonicPartialStringSet(
                previous.InvestmentStrategyIds,
                current.InvestmentStrategyIds),
            BattleDamage = KeepLastReliable(
                previous.BattleDamage,
                current.BattleDamage),
            BattleSynergyDamage = KeepLastReliable(
                previous.BattleSynergyDamage,
                current.BattleSynergyDamage),
            BattleUnresolvedDamage = KeepLastReliable(
                previous.BattleUnresolvedDamage,
                current.BattleUnresolvedDamage),
            BattleScreenDamageCandidate = KeepLastReliable(
                previous.BattleScreenDamageCandidate,
                current.BattleScreenDamageCandidate),
            SettlementDamage = KeepLastReliable(
                previous.SettlementDamage,
                current.SettlementDamage),
            SettlementScreenDamageCandidate = KeepLastReliable(
                previous.SettlementScreenDamageCandidate,
                current.SettlementScreenDamageCandidate),
            SettlementGoldReward = KeepLastReliable(
                previous.SettlementGoldReward,
                current.SettlementGoldReward),
            RemainingActionValue = KeepLastReliable(
                previous.RemainingActionValue,
                current.RemainingActionValue),
            FinalBattle = KeepLastReliable(
                previous.FinalBattle,
                current.FinalBattle),
            NamedContent = current.NamedContent.Count > 0
                ? current.NamedContent
                : previous.NamedContent,
            PendingIcons = previous.PendingIcons
                .Concat(current.PendingIcons)
                .DistinctBy(item => (item.Category, item.SlotKey, item.TemporaryId))
                .TakeLast(100)
                .ToArray(),
            PartialFields = previous.PartialFields
                .Concat(current.PartialFields)
                .DistinctBy(item => (item.Field, item.TemporaryId))
                .TakeLast(100)
                .ToArray(),
            RecognitionTrace = Phase2RecognitionTraceRetention.Retain(
                previous.RecognitionTrace.Concat(current.RecognitionTrace)),
            Diagnostics = previous.Diagnostics
                .Concat(current.Diagnostics)
                .Distinct(StringComparer.Ordinal)
                .TakeLast(100)
                .ToArray()
        };
    }

    public static Observation<T> KeepLastReliable<T>(
        Observation<T> previous,
        Observation<T> current)
    {
        if (current.Status == ObservationStatus.Known)
        {
            if (previous.Status == ObservationStatus.Known &&
                previous.Value is not null &&
                current.Confidence + 0.05 < previous.Confidence)
            {
                return new Observation<T>
                {
                    Status = ObservationStatus.Conflict,
                    Value = previous.Value,
                    Confidence = 0,
                    Evidence = previous.Evidence.Concat(current.Evidence)
                        .Distinct()
                        .ToArray(),
                    Uncertainty = previous.Uncertainty
                        .Concat(current.Uncertainty)
                        .Append(
                            "A later lower-confidence value conflicted with the " +
                            "saved reliable value; the reliable value was retained.")
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    ObservedAt = previous.ObservedAt
                };
            }

            return current;
        }

        if (previous.Status is ObservationStatus.Known or ObservationStatus.Stale &&
            previous.Value is not null)
        {
            return new Observation<T>
            {
                Status = ObservationStatus.Stale,
                Value = previous.Value,
                Confidence = 0,
                Evidence = previous.Evidence.Concat(current.Evidence)
                    .Distinct()
                    .ToArray(),
                Uncertainty = current.Uncertainty
                    .Append("当前帧无法确认；断点保留上一帧可靠值并标记为已过期。")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                ObservedAt = previous.ObservedAt
            };
        }

        return current.Value is not null ? current : previous;
    }

    public static Observation<IReadOnlyList<ActiveSynergyState>>
        MergeActiveSynergyObservations(
            Observation<IReadOnlyList<ActiveSynergyState>> previous,
            Observation<IReadOnlyList<ActiveSynergyState>> current)
    {
        if (current.Status != ObservationStatus.Known ||
            current.Value is not { Count: > 0 } currentValues)
        {
            return KeepLastReliable(previous, current);
        }

        var previousById = (previous.Value ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.SynergyId))
            .GroupBy(item => NormalizeSynergyId(item.SynergyId))
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var values = currentValues.Select(item =>
        {
            if (!previousById.TryGetValue(
                    NormalizeSynergyId(item.SynergyId),
                    out var saved))
            {
                return item;
            }

            return item with
            {
                ActiveCount = item.ActiveCount ?? saved.ActiveCount,
                NextThreshold = item.NextThreshold ?? saved.NextThreshold
            };
        }).ToArray();
        return current with { Value = values };
    }

    private static string NormalizeSynergyId(string? value) =>
        value?.StartsWith("bond_", StringComparison.Ordinal) == true
            ? value[5..]
            : value ?? string.Empty;

    public static Observation<IReadOnlyList<FormationCharacterState>>
        MergeFormationObservations(
            Observation<IReadOnlyList<FormationCharacterState>> previous,
            Observation<IReadOnlyList<FormationCharacterState>> current,
            bool markCurrentUnavailableAsStale = false,
            IReadOnlyList<Phase2FormationSlotObservation>? currentSlotObservations = null,
            bool observationsAreAtomic = false,
            bool observationsArePartial = true)
    {
        var previousValues = previous.Value ?? [];
        var currentValues = current.Value ?? [];
        if (currentSlotObservations is { Count: > 0 })
        {
            return MergeObservedFormationSlots(
                previous,
                current,
                currentSlotObservations,
                observationsAreAtomic,
                observationsArePartial,
                markCurrentUnavailableAsStale);
        }
        if (previousValues.Count == 0)
        {
            return current;
        }

        if (currentValues.Count == 0)
        {
            return KeepLastReliable(previous, current);
        }

        var merged = (current.Status == ObservationStatus.Known
                ? Enumerable.Empty<FormationCharacterState>()
                : previousValues)
            .ToDictionary(
                item => (item.Zone, item.SlotIndex),
                item => item);
        foreach (var currentCharacter in currentValues)
        {
            var key = (currentCharacter.Zone, currentCharacter.SlotIndex);
            merged[key] = previousValues.FirstOrDefault(item =>
                              item.Zone == currentCharacter.Zone &&
                              item.SlotIndex == currentCharacter.SlotIndex) is
                          { } previousCharacter
                ? MergeFormationCharacter(previousCharacter, currentCharacter)
                : currentCharacter;
        }

        var values = merged.Values
            .OrderBy(item => item.Zone)
            .ThenBy(item => item.SlotIndex)
            .ToArray();
        var evidence = previous.Evidence
            .Concat(current.Evidence)
            .Distinct()
            .ToArray();
        if (current.Status == ObservationStatus.Known)
        {
            return current with
            {
                Value = values,
                Confidence = Math.Max(previous.Confidence, current.Confidence),
                Evidence = evidence
            };
        }

        if (previous.Status is ObservationStatus.Known or ObservationStatus.Stale)
        {
            if (markCurrentUnavailableAsStale)
            {
                return new Observation<IReadOnlyList<FormationCharacterState>>
                {
                    Status = ObservationStatus.Stale,
                    Value = values,
                    Confidence = 0,
                    Evidence = evidence,
                    Uncertainty = current.Uncertainty
                        .Append(
                            "The current frame did not fully expose the formation; " +
                            "the best per-character and per-slot state was retained.")
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    ObservedAt = previous.ObservedAt
                };
            }

            return previous with
            {
                Value = values,
                Evidence = evidence
            };
        }

        return current with
        {
            Value = values,
            Evidence = evidence,
            Uncertainty = previous.Uncertainty
                .Concat(current.Uncertainty)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static Observation<IReadOnlyList<FormationCharacterState>>
        MergeObservedFormationSlots(
            Observation<IReadOnlyList<FormationCharacterState>> previous,
            Observation<IReadOnlyList<FormationCharacterState>> current,
            IReadOnlyList<Phase2FormationSlotObservation> observations,
            bool observationsAreAtomic,
            bool observationsArePartial,
            bool markCurrentUnavailableAsStale)
    {
        if (observationsAreAtomic && observations.Any(item =>
                item.Occupancy == Phase2FormationSlotOccupancy.Uncertain))
        {
            // A multi-slot change (for example a drag) is one transaction. If
            // any scanned slot is still uncertain, committing the other slots
            // would expose a half-move. Keep the last complete formation even
            // when the partial current observation is itself marked Known.
            return previous with
            {
                Evidence = previous.Evidence
                    .Concat(current.Evidence)
                    .Distinct()
                    .ToArray(),
                Uncertainty = previous.Uncertainty
                    .Concat(current.Uncertainty)
                    .Append(
                        "A fixed-slot formation transaction remains incomplete; " +
                        "the previously committed formation was retained.")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }

        var previousValues = previous.Value ?? [];
        var currentValues = current.Value ?? [];
        var uncertainKeys = observations
            .Where(item =>
                item.Occupancy == Phase2FormationSlotOccupancy.Uncertain)
            .Select(item => (item.Zone, item.SlotIndex))
            .ToHashSet();
        var confirmedEmptyKeys = observations
            .Where(item =>
                item.Occupancy == Phase2FormationSlotOccupancy.Empty)
            .Select(item => (item.Zone, item.SlotIndex))
            .ToHashSet();
        // Partial means that only a subset of the topology was sampled. A full
        // scan can still contain uncertain slots: retain those exact slots, but
        // remove unobserved trailing slots when the back-row topology shrinks.
        var merged = (observationsArePartial
                ? previousValues
                : previousValues.Where(item =>
                    uncertainKeys.Contains((item.Zone, item.SlotIndex))))
            .ToDictionary(
                item => (item.Zone, item.SlotIndex),
                item => item);
        var currentBySlot = currentValues.ToDictionary(
            item => (item.Zone, item.SlotIndex),
            item => item);
        var previousByIdentity = previousValues
            .Where(IsDecisionDrivingOrdinaryCharacter)
            .GroupBy(item => item.CharacterId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        var currentIdentityCounts = currentValues
            .Where(IsDecisionDrivingOrdinaryCharacter)
            .GroupBy(item => item.CharacterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);


        foreach (var observation in observations)
        {
            var key = (observation.Zone, observation.SlotIndex);
            switch (observation.Occupancy)
            {
                case Phase2FormationSlotOccupancy.Empty:
                    merged.Remove(key);
                    break;
                case Phase2FormationSlotOccupancy.Uncertain:
                    break;
                case Phase2FormationSlotOccupancy.Recognized:
                    if (!currentBySlot.TryGetValue(key, out var currentCharacter))
                    {
                        break;
                    }

                    previousByIdentity.TryGetValue(
                        currentCharacter.CharacterId,
                        out var previousIdentity);
                    var hasUniqueIdentity =
                        observationsAreAtomic &&
                        IsDecisionDrivingOrdinaryCharacter(currentCharacter) &&
                        currentIdentityCounts.GetValueOrDefault(
                            currentCharacter.CharacterId) == 1 &&
                        previousIdentity is not null &&
                        confirmedEmptyKeys.Contains(
                            (previousIdentity.Zone, previousIdentity.SlotIndex));
                    var previousIdentityKey = hasUniqueIdentity
                        ? (previousIdentity!.Zone, previousIdentity.SlotIndex)
                        : key;
                    if (hasUniqueIdentity && previousIdentityKey != key)
                    {
                        // Move a globally unique identity instead of duplicating
                        // it, and carry the character's equipment bundle. Identity
                        // equality also keeps Silver Wolf LV.999 CurrentCost from
                        // leaking to a different occupant.
                        merged.Remove(previousIdentityKey);
                        merged[key] = MergeFormationCharacter(
                                previousIdentity!,
                                currentCharacter)
                            with
                            {
                                Zone = currentCharacter.Zone,
                                SlotIndex = currentCharacter.SlotIndex
                            };
                    }
                    else
                    {
                        // Every selected fixed-slot scan recognizes the complete
                        // card bundle. Once the occupant is recognized, that
                        // bundle is authoritative even when the surrounding
                        // formation scan is partial; otherwise an unequipped
                        // replacement of the same character would inherit stale
                        // equipment, cost and visual effects.
                        merged[key] = currentCharacter;
                    }
                    break;
            }
        }

        foreach (var special in currentValues.Where(item =>
                     item.Zone == FormationZone.Special))
        {
            merged[(special.Zone, special.SlotIndex)] = special;
        }

        var values = merged.Values
            .OrderBy(item => item.Zone)
            .ThenBy(item => item.SlotIndex)
            .ToArray();
        var evidence = previous.Evidence.Concat(current.Evidence)
            .Distinct()
            .ToArray();
        if (current.Status == ObservationStatus.Known || values.Length > 0)
        {
            return current with
            {
                Status = ObservationStatus.Known,
                Value = values,
                Confidence = values.Length == 0
                    ? current.Confidence
                    : values.Average(item => item.Confidence),
                Evidence = evidence
            };
        }

        if (markCurrentUnavailableAsStale &&
            previous.Status is ObservationStatus.Known or ObservationStatus.Stale)
        {
            return previous with { Value = values, Evidence = evidence };
        }

        return current with { Value = values, Evidence = evidence };
    }

    private static bool IsDecisionDrivingOrdinaryCharacter(
        FormationCharacterState item) =>
        item.CanDriveDecisions &&
        item.Zone != FormationZone.Special &&
        !string.IsNullOrWhiteSpace(item.CharacterId) &&
        !item.CharacterId.StartsWith(
            "unknown-formation-unit",
            StringComparison.OrdinalIgnoreCase) &&
        !item.CharacterId.StartsWith(
            "special-formation-unit",
            StringComparison.OrdinalIgnoreCase) &&
        !item.CharacterId.StartsWith(
            "special_unit_",
            StringComparison.OrdinalIgnoreCase);

    public static Observation<IReadOnlyList<InventorySlotState>>
        MergeInventoryObservations(
            Observation<IReadOnlyList<InventorySlotState>> previous,
            Observation<IReadOnlyList<InventorySlotState>> current,
            bool markCurrentUnavailableAsStale = false)
    {
        var previousValues = previous.Value ?? [];
        var currentValues = current.Value ?? [];
        if (previousValues.Count == 0)
        {
            return current;
        }

        if (currentValues.Count == 0)
        {
            return KeepLastReliable(previous, current);
        }

        var merged = previousValues.ToDictionary(item => item.SlotIndex);
        foreach (var item in currentValues)
        {
            if (!merged.TryGetValue(item.SlotIndex, out var saved))
            {
                merged[item.SlotIndex] = item;
                continue;
            }

            var savedReliable = saved.Occupancy is
                EquipmentSlotOccupancy.Empty or EquipmentSlotOccupancy.Equipped;
            var currentReliable = item.Occupancy is
                EquipmentSlotOccupancy.Empty or EquipmentSlotOccupancy.Equipped;
            if (currentReliable && (!savedReliable ||
                                    item.Confidence + 0.05 >= saved.Confidence))
            {
                merged[item.SlotIndex] = item;
            }
            else if (!savedReliable && !currentReliable &&
                     item.Confidence + 0.05 >= saved.Confidence)
            {
                merged[item.SlotIndex] = item with
                {
                    CandidateItemIds = saved.CandidateItemIds
                        .Concat(item.CandidateItemIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
            }
        }

        var values = merged.Values.OrderBy(item => item.SlotIndex).ToArray();
        var evidence = previous.Evidence.Concat(current.Evidence)
            .Distinct()
            .ToArray();
        var allReliable = values.All(item => item.Occupancy is
            EquipmentSlotOccupancy.Empty or EquipmentSlotOccupancy.Equipped);
        if (allReliable)
        {
            return Observation<IReadOnlyList<InventorySlotState>>.Known(
                values,
                values.Average(item => item.Confidence),
                evidence,
                current.ObservedAt ?? previous.ObservedAt);
        }

        if (markCurrentUnavailableAsStale &&
            previous.Status is ObservationStatus.Known or ObservationStatus.Stale)
        {
            return new Observation<IReadOnlyList<InventorySlotState>>
            {
                Status = ObservationStatus.Stale,
                Value = values,
                Confidence = 0,
                Evidence = evidence,
                Uncertainty = ["当前帧未完整显示背包；已保留逐槽最佳可靠状态。"],
                ObservedAt = previous.ObservedAt
            };
        }

        return current with
        {
            Value = values,
            Evidence = evidence,
            Uncertainty = previous.Uncertainty.Concat(current.Uncertainty)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static FormationCharacterState MergeFormationCharacter(
        FormationCharacterState previous,
        FormationCharacterState current)
    {
        var previousHasIdentity = previous.CanDriveDecisions;
        var currentHasIdentity = current.CanDriveDecisions;
        FormationCharacterState preferred;
        FormationCharacterState fallback;
        if (currentHasIdentity && !previousHasIdentity)
        {
            preferred = current;
            fallback = previous;
        }
        else if (previousHasIdentity && !currentHasIdentity)
        {
            preferred = previous;
            fallback = current;
        }
        else if (current.Confidence + 0.05 < previous.Confidence)
        {
            preferred = previous;
            fallback = current;
        }
        else
        {
            preferred = current;
            fallback = previous;
        }

        var equipmentSlots = MergeEquipmentSlots(
            previous.FinalEquipmentSlots,
            current.FinalEquipmentSlots);
        // EquipmentIds is an occurrence list, not an identity set. When
        // structured slots exist they are the canonical source, so two slots
        // carrying the same item must remain two entries. Legacy records that
        // have no slot data still retain their original EquipmentIds list.
        var equipmentIds = equipmentSlots.Count == 0
            ? preferred.EquipmentIds
            : EquippedIds(equipmentSlots);

        return preferred with
        {
            StarLevel = preferred.StarLevel ?? fallback.StarLevel,
            Standing = string.IsNullOrWhiteSpace(preferred.Standing)
                ? fallback.Standing
                : preferred.Standing,
            EquipmentIds = equipmentIds,
            TemporaryId = preferred.CanDriveDecisions
                ? null
                : preferred.TemporaryId ?? fallback.TemporaryId,
            CandidateCharacterIds = (previous.CandidateCharacterIds ?? [])
                .Concat(current.CandidateCharacterIds ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CardRegion = preferred.CardRegion ?? fallback.CardRegion,
            EquipmentSlots = equipmentSlots,
            SpecialEquipmentIds = preferred.SpecialEquipmentIds is { Count: > 0 }
                ? preferred.SpecialEquipmentIds
                : fallback.SpecialEquipmentIds,
            SpecialEquipment = preferred.SpecialEquipment ??
                fallback.SpecialEquipment,
            CurrentCost = string.Equals(
                previous.CharacterId,
                current.CharacterId,
                StringComparison.OrdinalIgnoreCase)
                ? current.CurrentCost ?? previous.CurrentCost
                : preferred.CurrentCost
        };
    }

    private static IReadOnlyList<CharacterEquipmentSlotState> MergeEquipmentSlots(
        IReadOnlyList<CharacterEquipmentSlotState> previous,
        IReadOnlyList<CharacterEquipmentSlotState> current)
    {
        if (previous.Count == 0)
        {
            return current;
        }

        if (current.Count == 0)
        {
            return previous;
        }

        var sourceIds = current
            .Select(item => item.Evidence.SourceId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (current.Count < previous.Count &&
            current.All(IsDefinitive) &&
            current.Select(item => item.SlotIndex)
                .Order()
                .SequenceEqual(Enumerable.Range(0, current.Count)) &&
            sourceIds.Length == 1)
        {
            return current.OrderBy(item => item.SlotIndex).ToArray();
        }

        var merged = previous.ToDictionary(item => item.SlotIndex);
        foreach (var currentSlot in current)
        {
            merged[currentSlot.SlotIndex] = merged.TryGetValue(
                currentSlot.SlotIndex,
                out var previousSlot)
                ? MergeEquipmentSlot(previousSlot, currentSlot)
                : currentSlot;
        }

        return merged.Values
            .OrderBy(item => item.SlotIndex)
            .ToArray();
    }

    private static CharacterEquipmentSlotState MergeEquipmentSlot(
        CharacterEquipmentSlotState previous,
        CharacterEquipmentSlotState current)
    {
        if (previous.Occupancy == EquipmentSlotOccupancy.Equipped &&
            current.Occupancy == EquipmentSlotOccupancy.Equipped &&
            !string.IsNullOrWhiteSpace(previous.EquipmentId) &&
            !string.IsNullOrWhiteSpace(current.EquipmentId) &&
            !string.Equals(
                previous.EquipmentId,
                current.EquipmentId,
                StringComparison.OrdinalIgnoreCase))
        {
            return current with
            {
                Occupancy = EquipmentSlotOccupancy.Unknown,
                EquipmentId = null,
                CandidateEquipmentIds = previous.CandidateEquipmentIds
                    .Append(previous.EquipmentId)
                    .Concat(current.CandidateEquipmentIds)
                    .Append(current.EquipmentId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Confidence = Math.Min(previous.Confidence, current.Confidence),
                FailureReason =
                    "Consecutive frames disagree on the equipment identity; " +
                    "the occupied slot was retained but its identity was fail-closed.",
                CanDriveDecisions = false,
                IsPrivileged = previous.IsPrivileged ?? current.IsPrivileged
            };
        }

        var previousDefinitive = IsDefinitive(previous);
        var currentDefinitive = IsDefinitive(current);
        CharacterEquipmentSlotState preferred;
        if (previousDefinitive && !currentDefinitive)
        {
            preferred = previous;
        }
        else if (!previousDefinitive && currentDefinitive)
        {
            preferred = current;
        }
        else
        {
            preferred = current.Confidence + 0.05 < previous.Confidence
                ? previous
                : current;
        }

        return preferred with
        {
            IsPrivileged = preferred.IsPrivileged ??
                current.IsPrivileged ??
                previous.IsPrivileged,
            CandidateEquipmentIds = previous.CandidateEquipmentIds
                .Concat(current.CandidateEquipmentIds)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static bool IsDefinitive(CharacterEquipmentSlotState slot) =>
        slot.Occupancy == EquipmentSlotOccupancy.Empty ||
        (slot.Occupancy == EquipmentSlotOccupancy.Equipped &&
         !string.IsNullOrWhiteSpace(slot.EquipmentId));

    private static IReadOnlyList<string> EquippedIds(
        IReadOnlyList<CharacterEquipmentSlotState> slots) =>
        slots
            .OrderBy(item => item.SlotIndex)
            .Where(item =>
                item.Occupancy == EquipmentSlotOccupancy.Equipped &&
                !string.IsNullOrWhiteSpace(item.EquipmentId))
            .Select(item => item.EquipmentId!)
            .ToArray();

    private static Observation<string> KeepStableIdentity(
        Observation<string> previous,
        Observation<string> current)
    {
        if (previous.Status == ObservationStatus.Known &&
            previous.Value is not null &&
            current.Status == ObservationStatus.Known &&
            current.Value is not null &&
            !string.Equals(previous.Value, current.Value, StringComparison.OrdinalIgnoreCase))
        {
            return new Observation<string>
            {
                Status = ObservationStatus.Conflict,
                Value = previous.Value,
                Confidence = 0,
                Evidence = previous.Evidence.Concat(current.Evidence)
                    .Distinct()
                    .ToArray(),
                Uncertainty =
                [
                    $"Stable identity conflict: retained '{previous.Value}' and rejected later '{current.Value}'."
                ],
                ObservedAt = previous.ObservedAt
            };
        }

        return KeepLastReliable(previous, current);
    }

    private static Observation<IReadOnlyList<string>> KeepMonotonicStringSet(
        Observation<IReadOnlyList<string>> previous,
        Observation<IReadOnlyList<string>> current)
    {
        if (current.Status != ObservationStatus.Known || current.Value is null)
        {
            return KeepLastReliable(previous, current);
        }

        if (previous.Value is null ||
            previous.Status is not (ObservationStatus.Known or
                ObservationStatus.Stale or ObservationStatus.Conflict))
        {
            return current;
        }

        var merged = previous.Value
            .Concat(current.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Observation<IReadOnlyList<string>>.Known(
            merged,
            Math.Max(previous.Confidence, current.Confidence),
            previous.Evidence.Concat(current.Evidence).Distinct(),
            current.ObservedAt ?? previous.ObservedAt);
    }

    private static Observation<IReadOnlyList<string>>
        KeepMonotonicPartialStringSet(
            Observation<IReadOnlyList<string>> previous,
            Observation<IReadOnlyList<string>> current)
    {
        if (current.Value is null || current.Value.Count == 0)
        {
            return KeepLastReliable(previous, current);
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

    private static RunIdentityEvidence MergeIdentity(
        RunIdentityEvidence current,
        ScreenshotAnalysisResult analysis)
    {
        var environment = current.InvestmentEnvironmentId ??
                          KnownValue(analysis.Snapshot.InvestmentEnvironmentId);
        var strategies = MergeMonotonic(
            current.InvestmentStrategyIds,
            ObservedValues(analysis.Snapshot.InvestmentStrategyIds));
        var affixes = MergeMonotonic(
            current.EnemyAffixIds,
            KnownValues(analysis.OperationalState?.NegativeAffixIds));
        var enemies = MergeMonotonic(
            current.EnemyIds,
            KnownValues(analysis.Snapshot.EnemyIds));
        return new RunIdentityEvidence
        {
            InvestmentEnvironmentId = environment,
            InvestmentStrategyIds = strategies,
            EnemyAffixIds = affixes,
            EnemyIds = enemies
        };
    }

    private static IReadOnlyList<string> MergeMonotonic(
        IReadOnlyList<string> current,
        IReadOnlyList<string>? observed) => observed is null
        ? current
        : current.Concat(observed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int CountKnownFields(
        RunSnapshot snapshot,
        Phase2OperationalState? operational)
    {
        var observations = new[]
        {
            snapshot.PageId.Status,
            snapshot.Stage.Status,
            snapshot.Economy.Status,
            snapshot.CumulativeSpend.Status,
            snapshot.Health.Status,
            snapshot.ActionPoints.Status,
            snapshot.CurrentNodeDamage.Status,
            snapshot.BoardCharacterIds.Status,
            snapshot.BenchCharacterIds.Status,
            snapshot.LineupIds.Status,
            snapshot.SynergyIds.Status,
            snapshot.InvestmentEnvironmentId.Status,
            snapshot.InvestmentStrategyIds.Status,
            snapshot.EquipmentIds.Status,
            snapshot.InventorySlots.Status,
            snapshot.EnemyIds.Status,
            operational?.EnemyDifficulty.Status ?? ObservationStatus.Unknown,
            operational?.FinalBattle.Status ?? ObservationStatus.Unknown
        };
        return observations.Count(value => value == ObservationStatus.Known);
    }

    private static T? KnownValue<T>(Observation<T>? observation) =>
        observation?.Status == ObservationStatus.Known
            ? observation.Value
            : default;

    private static IReadOnlyList<string>? KnownValues(
        Observation<IReadOnlyList<string>>? observation) =>
        observation?.Status == ObservationStatus.Known
            ? observation.Value
            : null;

    private static IReadOnlyList<string>? ObservedValues(
        Observation<IReadOnlyList<string>>? observation) =>
        observation?.Value is { Count: > 0 } values
            ? values
            : null;

    private static class Phase2RecognitionTraceRetention
    {
        private const int MaximumRetainedCount = 32;

        public static IReadOnlyList<Phase2FieldRecognitionTrace> Retain(
            IEnumerable<Phase2FieldRecognitionTrace> traces)
        {
            ArgumentNullException.ThrowIfNull(traces);

            var latestByField = new Dictionary<TraceKey, IndexedTrace>();
            var index = 0;
            foreach (var trace in traces)
            {
                var key = new TraceKey(
                    trace.NodeId ?? string.Empty,
                    trace.SourcePageId ?? string.Empty,
                    trace.Field,
                    IsDiagnosticFailure(trace));
                var indexed = new IndexedTrace(trace, index++);
                if (!latestByField.TryGetValue(key, out var retained) ||
                    trace.CapturedAt > retained.Trace.CapturedAt ||
                    (trace.CapturedAt == retained.Trace.CapturedAt &&
                     indexed.Index > retained.Index))
                {
                    latestByField[key] = indexed;
                }
            }

            return latestByField.Values
                .OrderByDescending(item => item.Trace.CapturedAt)
                .ThenByDescending(item => item.Index)
                .Take(MaximumRetainedCount)
                .OrderBy(item => item.Trace.CapturedAt)
                .ThenBy(item => item.Index)
                .Select(item => item.Trace)
                .ToArray();
        }

        private static bool IsDiagnosticFailure(
            Phase2FieldRecognitionTrace trace) =>
            trace.Status != ObservationStatus.Known || trace.Confidence < 0.65;

        private readonly record struct TraceKey(
            string NodeId,
            string SourcePageId,
            string Field,
            bool IsFailure);

        private readonly record struct IndexedTrace(
            Phase2FieldRecognitionTrace Trace,
            int Index);
    }
}
