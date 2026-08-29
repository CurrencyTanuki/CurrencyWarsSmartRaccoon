using System.Reflection;
using System.Globalization;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Tasks;

internal static class Phase2RecognitionTraceBuilder
{
    public static string ApplicationVersion { get; } =
        typeof(Phase2RecognitionTraceBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
        typeof(Phase2RecognitionTraceBuilder).Assembly.GetName().Version?.ToString() ??
        "unknown";

    public static ScreenshotAnalysisResult Attach(ScreenshotAnalysisResult analysis)
    {
        if (analysis.OperationalState is not { } state)
        {
            return analysis with { ApplicationVersion = ApplicationVersion };
        }

        var pageId = analysis.Snapshot.PageId.Value ?? state.PageId;
        var nodeId = state.NodeId.Value;
        var traces = new List<Phase2FieldRecognitionTrace>();
        var nodeRegion = state.PageFamily switch
        {
            Phase2PageFamily.Preparation => Phase2RecognitionRegions.PreparationNodeValue,
            Phase2PageFamily.Battle => Phase2RecognitionRegions.BattleNodeValue,
            Phase2PageFamily.BattleSettlement => Phase2RecognitionRegions.SettlementNodeValue,
            _ => default
        };
        if (state.PageFamily is Phase2PageFamily.Preparation or
            Phase2PageFamily.Battle or
            Phase2PageFamily.BattleSettlement)
        {
            traces.Add(Trace(
                "nodeId",
                nodeId,
                pageId,
                Raw(state.NodeId),
                state.NodeId.Value,
                state.NodeId.Status,
                state.NodeId.Confidence,
                Reason(state.NodeId),
                nodeRegion,
                state.NodeId.ObservedAt ?? analysis.Snapshot.AsOf));
        }

        if (state.PageFamily is Phase2PageFamily.Preparation or
            Phase2PageFamily.BattleSettlement)
        {
            var healthRegion = state.PageFamily == Phase2PageFamily.BattleSettlement
                ? Phase2RecognitionRegions.SettlementHealth
                : Phase2RecognitionRegions.PreparationHealthValue;
            traces.Add(Trace(
                "health",
                nodeId,
                pageId,
                Raw(analysis.Snapshot.Health),
                Normalized(analysis.Snapshot.Health),
                analysis.Snapshot.Health.Status,
                analysis.Snapshot.Health.Confidence,
                Reason(analysis.Snapshot.Health),
                healthRegion,
                analysis.Snapshot.Health.ObservedAt ?? analysis.Snapshot.AsOf));
        }

        if (state.PageFamily == Phase2PageFamily.Battle)
        {
            traces.Add(Trace(
                "nodeTotalDamage",
                nodeId,
                pageId,
                Raw(state.BattleScreenDamageCandidate),
                Normalized(state.BattleScreenDamageCandidate),
                state.BattleScreenDamageCandidate.Status,
                state.BattleScreenDamageCandidate.Confidence,
                Reason(state.BattleScreenDamageCandidate),
                Phase2RecognitionRegions.BattleDamagePanel,
                state.BattleScreenDamageCandidate.ObservedAt ?? analysis.Snapshot.AsOf));
            traces.AddRange((state.BattleDamage.Value ?? []).Select(item => Trace(
                $"battleCharacterDamage[{item.Rank}]",
                nodeId,
                pageId,
                [item.RawText],
                item.Damage.ToString(),
                state.BattleDamage.Status,
                Math.Min(item.AvatarConfidence, item.DamageConfidence),
                item.FailureReason,
                new NormalizedRect(
                    item.DamageRegion.X,
                    item.DamageRegion.Y,
                    item.DamageRegion.Width,
                    item.DamageRegion.Height),
                item.Evidence.CapturedAt ?? analysis.Snapshot.AsOf)));
        }

        if (state.PageFamily == Phase2PageFamily.BattleSettlement)
        {
            traces.Add(Trace(
                "settlementGoldReward",
                nodeId,
                pageId,
                Raw(state.SettlementGoldReward),
                Normalized(state.SettlementGoldReward),
                state.SettlementGoldReward.Status,
                state.SettlementGoldReward.Confidence,
                Reason(state.SettlementGoldReward),
                Phase2RecognitionRegions.SettlementGoldRewardLabeledRow,
                state.SettlementGoldReward.ObservedAt ?? analysis.Snapshot.AsOf));
            traces.Add(Trace(
                "nodeTotalDamage",
                nodeId,
                pageId,
                Raw(state.SettlementScreenDamageCandidate),
                Normalized(state.SettlementScreenDamageCandidate),
                state.SettlementScreenDamageCandidate.Status,
                state.SettlementScreenDamageCandidate.Confidence,
                Reason(state.SettlementScreenDamageCandidate),
                Phase2RecognitionRegions.SettlementDamagePanel,
                state.SettlementScreenDamageCandidate.ObservedAt ?? analysis.Snapshot.AsOf));
            traces.AddRange((state.SettlementDamage.Value ?? []).Select(item => Trace(
                $"settlementTopThree[{item.Rank}]",
                nodeId,
                pageId,
                [item.RawText],
                item.Damage.ToString(),
                state.SettlementDamage.Status,
                Math.Min(item.AvatarConfidence, item.DamageConfidence),
                item.FailureReason,
                new NormalizedRect(
                    item.DamageRegion.X,
                    item.DamageRegion.Y,
                    item.DamageRegion.Width,
                    item.DamageRegion.Height),
                item.Evidence.CapturedAt ?? analysis.Snapshot.AsOf)));
        }

        return analysis with
        {
            ApplicationVersion = ApplicationVersion,
            OperationalState = state with
            {
                RecognitionTrace = state.RecognitionTrace
                    .Concat(traces)
                    .Distinct()
                    .ToArray()
            }
        };
    }

    private static Phase2FieldRecognitionTrace Trace(
        string field,
        string? nodeId,
        string? pageId,
        IReadOnlyList<string> raw,
        string? normalized,
        ObservationStatus status,
        double confidence,
        string? degradationReason,
        NormalizedRect region,
        DateTimeOffset capturedAt) => new(
            field,
            nodeId,
            pageId,
            raw,
            normalized,
            status,
            confidence,
            1,
            degradationReason,
            new RelativeRegion(region.X, region.Y, region.Width, region.Height),
            capturedAt);

    private static IReadOnlyList<string> Raw<T>(Observation<T> observation) =>
        observation.Evidence
            .Select(item => item.Summary)
            .OfType<string>()
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string? Reason<T>(Observation<T> observation) =>
        observation.Uncertainty.Count == 0
            ? null
            : string.Join(" | ", observation.Uncertainty);

    private static string? Normalized<T>(Observation<T> observation) =>
        observation.Status == ObservationStatus.Known && observation.Value is not null
            ? Convert.ToString(observation.Value, CultureInfo.InvariantCulture)
            : null;
}
