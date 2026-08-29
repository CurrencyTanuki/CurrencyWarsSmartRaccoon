using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// Selects whether realtime recognition continues through the current Legacy
/// path or a future hybrid scheduler. The default remains Legacy.
/// </summary>
public enum Phase2RealtimeExecutionMode
{
    Legacy,
    HybridRealtime
}

/// <summary>
/// Stores the production recognition execution mode independently from
/// diagnostics such as MeasureOnly and ObserveOnly.
/// </summary>
public static class Phase2RealtimeExecution
{
    private static int mode;

    public static Phase2RealtimeExecutionMode ResolveMode(
        bool enableHybridRealtimeRecognition) =>
        enableHybridRealtimeRecognition
            ? Phase2RealtimeExecutionMode.HybridRealtime
            : Phase2RealtimeExecutionMode.Legacy;

    public static Phase2RealtimeExecutionMode Mode
    {
        get => (Phase2RealtimeExecutionMode)Volatile.Read(ref mode);
        set => Volatile.Write(ref mode, (int)value);
    }
}

internal enum Phase2HybridPageStrategy
{
    PreparationRoiCandidate,
    BattleLegacy,
    SettlementStableFull,
    OverlayLegacy,
    UnknownFullFallback
}

internal static class Phase2HybridPageStrategyRouter
{
    private static readonly IReadOnlySet<string> OverlayPageIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "reward_shop",
            "investment_strategy",
            "incomplete_lineup_prompt",
            "companion_selection"
        };

    internal static Phase2HybridPageStrategy Route(
        Phase2PageFamily pageFamily,
        string? pageId,
        IReadOnlySet<string>? fullFallbackPageIds = null)
    {
        if (!string.IsNullOrWhiteSpace(pageId) &&
            fullFallbackPageIds?.Contains(pageId) == true)
        {
            return Phase2HybridPageStrategy.UnknownFullFallback;
        }

        if (!string.IsNullOrWhiteSpace(pageId) &&
            OverlayPageIds.Contains(pageId))
        {
            return Phase2HybridPageStrategy.OverlayLegacy;
        }

        return pageFamily switch
        {
            Phase2PageFamily.Preparation =>
                Phase2HybridPageStrategy.PreparationRoiCandidate,
            Phase2PageFamily.Battle =>
                Phase2HybridPageStrategy.BattleLegacy,
            Phase2PageFamily.BattleSettlement =>
                Phase2HybridPageStrategy.SettlementStableFull,
            _ => Phase2HybridPageStrategy.UnknownFullFallback
        };
    }
}
