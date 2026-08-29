using System.Numerics;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// Keeps the most recent fixed-slot formation for each node so a slot-only
/// recognition result can be expanded back into a complete same-node formation
/// before derived fields (notably synergies) are calculated.
/// </summary>
internal sealed class Phase2NodeFormationFusion
{
    private const int MaximumRememberedNodes = 4;
    private const int SignificantNodeRegionHashDistance = 3;
    private readonly object _gate = new();
    private readonly Dictionary<
        string,
        Observation<IReadOnlyList<FormationCharacterState>>> _formations =
            new(StringComparer.Ordinal);
    private string? _runId;
    private string? _lastKnownNodeId;
    private ulong? _lastKnownNodeSignature;
    private ulong? _pendingNodeSignature;
    private int _pendingNodeSignatureCount;

    public Observation<IReadOnlyList<FormationCharacterState>> Merge(
        string runId,
        Observation<string> nodeId,
        Observation<IReadOnlyList<FormationCharacterState>> current,
        IReadOnlyList<Phase2FormationSlotObservation> slotObservations,
        bool observationsAreAtomic,
        bool observationsArePartial = true,
        string? fallbackNodeId = null,
        ulong? nodeRegionSignature = null)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(slotObservations);

        lock (_gate)
        {
            if (!string.Equals(_runId, runId, StringComparison.Ordinal))
            {
                _runId = runId;
                _formations.Clear();
                _lastKnownNodeId = null;
                _lastKnownNodeSignature = null;
                _pendingNodeSignature = null;
                _pendingNodeSignatureCount = 0;
            }

            var key = ResolveNodeKey(
                nodeId,
                fallbackNodeId,
                nodeRegionSignature);
            if (key is null)
            {
                return current;
            }

            var merged = _formations.TryGetValue(key, out var previous) &&
                         slotObservations.Count > 0
                ? RunCheckpointFactory.MergeFormationObservations(
                    previous,
                    current,
                    markCurrentUnavailableAsStale: false,
                    slotObservations,
                    observationsAreAtomic,
                    observationsArePartial)
                : current;

            if (merged.Value is { Count: > 0 })
            {
                _formations[key] = merged;
                TrimOldestNodeIfNeeded(key);
            }
            else if (slotObservations.Count == 0
                         ? merged.Status == ObservationStatus.Known
                         : IsDefinitiveEmpty(
                             slotObservations, observationsArePartial))
            {
                _formations.Remove(key);
            }

            return merged;
        }
    }

    private string? ResolveNodeKey(
        Observation<string> nodeId,
        string? fallbackNodeId,
        ulong? nodeRegionSignature)
    {
        if (nodeId.Status == ObservationStatus.Known &&
            !string.IsNullOrWhiteSpace(nodeId.Value))
        {
            var knownNodeId = nodeId.Value.Trim();
            _lastKnownNodeId = knownNodeId;
            _lastKnownNodeSignature = nodeRegionSignature;
            _pendingNodeSignature = null;
            _pendingNodeSignatureCount = 0;
            return knownNodeId;
        }

        if (string.IsNullOrWhiteSpace(fallbackNodeId) ||
            !string.Equals(
                fallbackNodeId,
                _lastKnownNodeId,
                StringComparison.OrdinalIgnoreCase) ||
            !_lastKnownNodeSignature.HasValue ||
            !nodeRegionSignature.HasValue)
        {
            return null;
        }

        var changedFromKnown = BitOperations.PopCount(
            _lastKnownNodeSignature.Value ^ nodeRegionSignature.Value) >=
            SignificantNodeRegionHashDistance;
        if (!changedFromKnown)
        {
            _pendingNodeSignature = null;
            _pendingNodeSignatureCount = 0;
            return _lastKnownNodeId;
        }

        var repeatsPendingChange = _pendingNodeSignature.HasValue &&
            BitOperations.PopCount(
                _pendingNodeSignature.Value ^ nodeRegionSignature.Value) <
            SignificantNodeRegionHashDistance;
        if (repeatsPendingChange)
        {
            _pendingNodeSignatureCount++;
        }
        else
        {
            _pendingNodeSignature = nodeRegionSignature;
            _pendingNodeSignatureCount = 1;
        }

        if (_pendingNodeSignatureCount >= 2)
        {
            _lastKnownNodeId = null;
            _lastKnownNodeSignature = null;
            _pendingNodeSignature = null;
            _pendingNodeSignatureCount = 0;
        }

        return null;
    }

    private static bool IsDefinitiveEmpty(
        IReadOnlyList<Phase2FormationSlotObservation> observations,
        bool observationsArePartial) =>
        !observationsArePartial &&
        observations.Count > 0 &&
        observations.All(item =>
            item.Occupancy == Phase2FormationSlotOccupancy.Empty);

    private void TrimOldestNodeIfNeeded(string currentKey)
    {
        if (_formations.Count <= MaximumRememberedNodes)
        {
            return;
        }

        var oldestOtherKey = _formations.Keys
            .Where(key => !string.Equals(key, currentKey, StringComparison.Ordinal))
            .OrderBy(NodeRank)
            .FirstOrDefault();
        if (oldestOtherKey is not null)
        {
            _formations.Remove(oldestOtherKey);
        }
    }

    private static int NodeRank(string nodeId)
    {
        var parts = nodeId.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var phase) &&
               int.TryParse(parts[1], out var node)
            ? (phase * 100) + node
            : int.MinValue;
    }
}
