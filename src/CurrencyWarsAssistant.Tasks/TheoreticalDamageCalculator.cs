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
        if (!HasActionLimit(nodeId))
        {
            return Unknown(null, "reward node has no action limit");
        }

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
            // 用户规则（2026-08-06）：识别到瓦尔特 → 假设技能放满达到上限。
            // 仅当实际观测到加成（confirmedActionIncrease > 0）时用观测值；
            // 否则按星级估计（1/2星 +100、3星 +999）。
            // 不能用 reliableActionSamples>=2 替代观测（行动值样本多但加成
            // 未观测到时仍应走星级估计——否则 increase=0 上限不提高，
            // 实测 3-7 行动值 163 ≥ 160 拒绝计算理论极限）。
            if (confirmedActionIncrease > 0)
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

    public static bool HasActionLimit(string? nodeId) => nodeId is not (
        "1-1" or "1-2" or "1-8" or "2-6" or "3-6");

    private static int? ResolveBaseMaximum(string nodeId)
    {
        var separator = nodeId.IndexOf('-');
        if (separator <= 0 ||
            !int.TryParse(nodeId[..separator], out var plane))
        {
            return null;
        }

        // 3-7 是第三面最后首领节点，初始资源量 160 行动值（用户
        // 2026-08-06 实测确认），不是普通第三面节点的 120。
        if (plane == 3 &&
            string.Equals(nodeId, "3-7", StringComparison.OrdinalIgnoreCase))
        {
            return 160;
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
