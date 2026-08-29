using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// Rebuilds the report-only view of the newest incomplete run after an app
/// restart. It only replays persisted evidence and never settles a node.
/// </summary>
internal static class IncompleteRunHistoryRestorer
{
    internal static async Task<IReadOnlyList<HistoricalNodeDetailEntry>>
        RestoreLatestAsync(
            LocalRunStore store,
            IReadOnlyList<RunCheckpointSummary> summaries,
            CancellationToken cancellationToken)
        => await RestoreLatestAsync(
                store,
                summaries,
                targetProjection: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<IReadOnlyList<HistoricalNodeDetailEntry>>
        RestoreLatestAsync(
            LocalRunStore store,
            IReadOnlyList<RunCheckpointSummary> summaries,
            IHistoricalDashboardProjection? targetProjection,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(summaries);

        var latest = summaries
            .OrderByDescending(item => item.Checkpoint.LastSavedAtUtc)
            .ThenBy(item => item.Checkpoint.RunId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latest is null)
        {
            return [];
        }

        var checkpoint = latest.Checkpoint;
        var projection = targetProjection ?? new HistoricalDashboardProjection();
        var analyses = await store.LoadAnalysesAsync(
                checkpoint.RunId,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var analysis in analyses)
        {
            projection.Observe(checkpoint.RunId, analysis);
        }

        var snapshot = checkpoint.LastSnapshot;
        var state = checkpoint.LastOperationalState;
        var nodeId = ResolveNodeId(checkpoint, state, snapshot);
        var isAlreadyFinalized = !string.IsNullOrWhiteSpace(nodeId) &&
            checkpoint.FinalizedNodeIds.Contains(
                nodeId,
                StringComparer.OrdinalIgnoreCase);
        if (snapshot is not null && state is not null &&
            !string.IsNullOrWhiteSpace(nodeId) &&
            !isAlreadyFinalized &&
            string.Equals(snapshot.RunId, checkpoint.RunId, StringComparison.Ordinal))
        {
            var checkpointState = state with
            {
                NodeId = Observation<string>.Known(
                    nodeId,
                    1,
                    observedAt: snapshot.AsOf),
                // A paused checkpoint is not a settlement boundary. Never
                // promote a stale/partial FinalBattle value while restoring.
                FinalBattle = Observation<FinalNodeBattleState>.Unknown(
                    "paused checkpoint is not finalized")
            };
            projection.Observe(
                checkpoint.RunId,
                new ScreenshotAnalysisResult
                {
                    AnalysisId = $"checkpoint-restore:{checkpoint.RunId}:{checkpoint.LastSavedAtUtc:O}",
                    ApplicationVersion = analyses.LastOrDefault()?.ApplicationVersion,
                    Snapshot = snapshot,
                    OperationalState = checkpointState,
                    Warnings = checkpoint.Uncertainty
                });
        }

        var restored = projection.Current.DetailNodes.ToList();
        if (snapshot is null || state is null || string.IsNullOrWhiteSpace(nodeId) ||
            isAlreadyFinalized)
        {
            return restored;
        }

        var sanitizedState = state with
        {
            FinalBattle = Observation<FinalNodeBattleState>.Unknown(
                "paused checkpoint is not finalized")
        };
        var existingIndex = restored.FindIndex(item => string.Equals(
            item.NodeId,
            nodeId,
            StringComparison.OrdinalIgnoreCase));
        var existing = existingIndex >= 0 ? restored[existingIndex] : null;
        var latestEntry = new HistoricalNodeDetailEntry(
            checkpoint.RunId,
            nodeId,
            snapshot,
            sanitizedState,
            state.PageFamily == Phase2PageFamily.Preparation
                ? sanitizedState
                : existing?.LatestPreparationState,
            FinalBattle: null,
            LatestAnalysis: existing?.LatestAnalysis,
            UpdatedAt: checkpoint.LastSavedAtUtc,
            PreparationAnalysisFile: existing?.PreparationAnalysisFile,
            FinalBattleFile: null);
        if (existingIndex >= 0)
        {
            restored[existingIndex] = latestEntry;
        }
        else
        {
            restored.Add(latestEntry);
        }

        return restored;
    }

    internal static IReadOnlyList<HistoricalNodeDetailEntry> MergeEntries(
        IReadOnlyList<HistoricalNodeDetailEntry> live,
        IReadOnlyList<HistoricalNodeDetailEntry> restored)
    {
        if (live.Count == 0)
        {
            return restored;
        }

        var runId = live[0].RunId;
        var restoredForRun = restored
            .Where(item => string.Equals(
                item.RunId,
                runId,
                StringComparison.Ordinal))
            .ToArray();
        if (restoredForRun.Length == 0)
        {
            return live;
        }

        return live
            .Concat(restoredForRun)
            .GroupBy(item => item.NodeId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.UpdatedAt)
                .First())
            .OrderBy(item => NodeRank(item.NodeId))
            .ThenBy(item => item.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static HistoricalDashboardSnapshot BuildDashboardSnapshot(
        HistoricalDashboardSnapshot projected,
        IReadOnlyList<HistoricalNodeDetailEntry> restored)
    {
        var entries = MergeEntries(projected.DetailNodes, restored);
        return new HistoricalDashboardSnapshot(
            projected.RunId ?? entries.FirstOrDefault()?.RunId,
            projected.Nodes,
            projected.DamageScale,
            projected.TheoryScale)
        {
            DetailNodes = entries
        };
    }

    private static int NodeRank(string nodeId)
    {
        var parts = nodeId.Split('-', 2);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var plane) &&
               int.TryParse(parts[1], out var node)
            ? plane * 100 + node
            : int.MaxValue;
    }

    private static string? ResolveNodeId(
        RunCheckpointRecord checkpoint,
        Phase2OperationalState? state,
        RunSnapshot? snapshot)
    {
        if (state?.NodeId.Status == ObservationStatus.Known &&
            !string.IsNullOrWhiteSpace(state.NodeId.Value))
        {
            return state.NodeId.Value;
        }

        if (snapshot?.Stage.Status == ObservationStatus.Known &&
            !string.IsNullOrWhiteSpace(snapshot.Stage.Value))
        {
            return snapshot.Stage.Value;
        }

        return checkpoint.LastConfirmedNodeId;
    }
}
