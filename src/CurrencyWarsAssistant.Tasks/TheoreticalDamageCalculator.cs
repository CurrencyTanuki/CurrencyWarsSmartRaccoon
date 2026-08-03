using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

public sealed record TheoreticalDamageCalculation(
    long? Value,
    int? BaseMaximumActionValue,
    int? ConfirmedActionIncrease,
    int? EffectiveMaximumActionValue,
    TheoreticalDamageQuality Quality,
    string Rule);

public static class TheoreticalDamageCalculator
{
    public static TheoreticalDamageCalculation Calculate(
        string nodeId,
        long? finalDamage,
        RemainingActionValueState? remainingAction,
        NodeClearStatus clearStatus,
        bool hasWalter,
        int? walterStarLevel,
        int confirmedActionIncrease,
        int reliableActionSamples)
    {
        var baseMaximum = ResolveBaseMaximum(nodeId);
        if (finalDamage is null || finalDamage < 0)
        {
            return Unknown(baseMaximum, "final damage is unavailable");
        }

        if (clearStatus == NodeClearStatus.NotPerfect)
        {
            return new TheoreticalDamageCalculation(
                finalDamage,
                baseMaximum,
                hasWalter ? confirmedActionIncrease : 0,
                baseMaximum is null
                    ? null
                    : baseMaximum + (hasWalter ? confirmedActionIncrease : 0),
                TheoreticalDamageQuality.ActionExhausted,
                "not-perfect node: action was exhausted, so limit equals final damage");
        }

        if (clearStatus != NodeClearStatus.Perfect || baseMaximum is null)
        {
            return Unknown(
                baseMaximum,
                clearStatus == NodeClearStatus.Unknown
                    ? "clear status is unknown"
                    : "node plane is unknown");
        }

        var increase = 0;
        var quality = TheoreticalDamageQuality.Exact;
        var increaseRule = "no Walter action increase";
        if (hasWalter)
        {
            if (confirmedActionIncrease > 0 || reliableActionSamples >= 2)
            {
                increase = confirmedActionIncrease;
                quality = TheoreticalDamageQuality.WalterObserved;
                increaseRule = $"observed Walter increase +{increase}";
            }
            else if (walterStarLevel is 1 or 2)
            {
                increase = 100;
                quality = TheoreticalDamageQuality.WalterEstimated;
                increaseRule = "estimated Walter 1/2-star cap +100";
            }
            else if (walterStarLevel == 3)
            {
                increase = 999;
                quality = TheoreticalDamageQuality.WalterEstimated;
                increaseRule = "estimated Walter 3-star cap +999";
            }
            else
            {
                return Unknown(
                    baseMaximum,
                    "Walter is present but star level and actual increase are unavailable");
            }
        }

        var effectiveMaximum = checked(baseMaximum.Value + increase);
        if (remainingAction is null ||
            remainingAction.TotalActionValue < 0 ||
            remainingAction.TotalActionValue >= effectiveMaximum)
        {
            return new TheoreticalDamageCalculation(
                null,
                baseMaximum,
                hasWalter ? confirmedActionIncrease : 0,
                effectiveMaximum,
                TheoreticalDamageQuality.Unknown,
                "remaining action is unavailable or leaves no consumed action");
        }

        var used = effectiveMaximum - remainingAction.TotalActionValue;
        var projected = decimal.ToInt64(decimal.Round(
            finalDamage.Value / (decimal)used * effectiveMaximum,
            0,
            MidpointRounding.AwayFromZero));
        return new TheoreticalDamageCalculation(
            projected,
            baseMaximum,
            hasWalter ? confirmedActionIncrease : 0,
            effectiveMaximum,
            quality,
            $"D/U*M; D={finalDamage}; U={used}; M={effectiveMaximum}; {increaseRule}");
    }

    private static int? ResolveBaseMaximum(string nodeId)
    {
        var separator = nodeId.IndexOf('-');
        if (separator <= 0 ||
            !int.TryParse(nodeId[..separator], out var plane))
        {
            return null;
        }

        return plane switch
        {
            1 => 180,
            2 => 150,
            3 => 120,
            _ => null
        };
    }

    private static TheoreticalDamageCalculation Unknown(
        int? baseMaximum,
        string reason) =>
        new(
            null,
            baseMaximum,
            null,
            null,
            TheoreticalDamageQuality.Unknown,
            reason);
}
