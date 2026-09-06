namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 凑息材料过滤纯逻辑（1.2.116，用户拍板"1-2 出战前凑 10 金吃利息"）：
/// 同名角色合计（备战席+上场）≥2 张 = 升星材料链，凑息绝不卖（保守口径——
/// 卖一张材料卡换 1 金利息，可能拖慢三星进度，不对称）。识别误名/无名明细
/// 一律按"可能材料"保守跳过（与清场的"数据查不到=保护"同向）。
/// </summary>
public static class InterestTopUpPlanner
{
    /// <summary>统计各角色名出现次数（明细格式："槽位:角色名[星徽]"，PureName 去掉方括号尾）。</summary>
    public static IReadOnlyDictionary<string, int> CountNames(
        IReadOnlyList<string> benchDetails,
        IReadOnlyList<string> deployedDetails)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var detail in benchDetails.Concat(deployedDetails))
        {
            var name = PureNameOf(detail);
            if (name is null)
            {
                continue;
            }

            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return counts;
    }

    /// <summary>该备战席明细是否可作凑息候选：可解析出名字 ∧ 同名合计 &lt; 2（非材料）。</summary>
    public static bool IsInterestSellable(
        string benchDetail,
        IReadOnlyDictionary<string, int> nameCounts)
    {
        var name = PureNameOf(benchDetail);
        if (name is null)
        {
            return false;
        }

        return nameCounts.GetValueOrDefault(name) < 2;
    }

    /// <summary>明细 → 角色名（去 [星徽] 等方括号尾）；无 ':' 头或空名 = null（不可解析）。</summary>
    public static string? PureNameOf(string detail)
    {
        var separator = detail.IndexOf(':');
        if (separator <= 0 || separator + 1 >= detail.Length)
        {
            return null;
        }

        var body = detail[(separator + 1)..];
        var open = body.IndexOf('[');
        var name = open > 0 ? body[..open] : body;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>卖价=卡最低费用（既有口径 Costs.Min）；费用缺失/解析不出 = null（永不达标，保守不卖）。</summary>
    public static int? SaleValueOf(IReadOnlyList<int>? costs) =>
        costs is { Count: > 0 } ? costs.Min() : null;

    /// <summary>达标判定：金 + 卖价 ≥ 目标线才值得卖（审查 P1-1：低金币段白卖=净损战力换 0 利息）。</summary>
    public static bool ReachesTarget(int gold, int? saleValue, int targetGold) =>
        gold + (saleValue ?? int.MinValue) >= targetGold;
}
