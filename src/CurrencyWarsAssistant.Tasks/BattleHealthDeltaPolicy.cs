namespace CurrencyWarsAssistant.Tasks;

public sealed record BattleHealthDeltaDecision(
    int? Value,
    string? Uncertainty = null);

/// <summary>
/// Validates whether a positive health transition can be attributed to the
/// battle that is being finalized. Impossible gains are preserved as raw
/// pre/post evidence, but they must not become a trusted delta.
/// </summary>
public static class BattleHealthDeltaPolicy
{
    public static BattleHealthDeltaDecision Evaluate(
        string? nodeId,
        int? preBattleHealth,
        int? postBattleHealth,
        int? rawDelta)
    {
        if (rawDelta is null or <= 0 || rawDelta == 2)
        {
            return new BattleHealthDeltaDecision(rawDelta);
        }

        if (rawDelta == 1 && postBattleHealth == 100)
        {
            return new BattleHealthDeltaDecision(rawDelta);
        }

        if (rawDelta == 42 && string.Equals(
                nodeId,
                "3-7",
                StringComparison.OrdinalIgnoreCase))
        {
            return new BattleHealthDeltaDecision(rawDelta);
        }

        return new BattleHealthDeltaDecision(
            null,
            "rejected implausible health transition " +
            $"{Format(preBattleHealth)}→{Format(postBattleHealth)} " +
            $"(+{rawDelta}); raw values were retained for diagnosis");
    }

    private static string Format(int? value) =>
        value?.ToString() ?? "unknown";
}
