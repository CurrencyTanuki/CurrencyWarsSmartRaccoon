using System.Collections.Generic;
using System.Linq;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」决策树中的投资策略自选辅助（纯逻辑，可独立单测）。
/// <para>对应决策树「缺某费用成员 → 在可选策略里找采购专员(金051每7刷/彩238每5刷)」这一段。</para>
/// 注意：投资策略由<b>软件自选</b>（详见决策树与说明），本类只提供"优先挑选采购专员"的决策辅助，
/// 不涉及识别用户已选策略。
/// 采购专员·金(051)=每7刷；采购专员·彩(238)=每5刷（更频繁，优先）。planes=[1,2,3]，1-3 可用。
/// </summary>
public static class InvestmentStrategyPicker
{
    /// <summary>采购专员·金（每 7 次刷新出 5 张备战席最左同费角色）。</summary>
    public const string PurchaseSpecialistGold = "investment_strategy_051";

    /// <summary>采购专员·彩（每 5 次刷新出 5 张备战席最左同费角色，更频繁）。</summary>
    public const string PurchaseSpecialistColor = "investment_strategy_238";

    /// <summary>
    /// 判断某投资策略 id 是否为采购专员（金/彩）。
    /// </summary>
    public static bool IsPurchaseSpecialist(string strategyId) =>
        string.Equals(strategyId, PurchaseSpecialistGold,
                System.StringComparison.OrdinalIgnoreCase) ||
        string.Equals(strategyId, PurchaseSpecialistColor,
            System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在可选策略集合里挑采购专员（用于缺目标费用成员时抬该费用刷出概率）。
    /// <para>优先级：<b>彩 238（每5刷）优先 &gt; 金 051（每7刷）兜底</b>；
    /// 都没有 → null（表示"没有采购专员"，接去刷新或放弃）。</para>
    /// </summary>
    public static string? PickPurchaseSpecialist(
        IEnumerable<string>? availableStrategyIds)
    {
        var available = new HashSet<string>(
            availableStrategyIds ?? Enumerable.Empty<string>(),
            System.StringComparer.OrdinalIgnoreCase);
        // 颜色(每5刷) 优先，黄金(每7刷) 兜底
        if (available.Contains(PurchaseSpecialistColor))
            return PurchaseSpecialistColor;
        if (available.Contains(PurchaseSpecialistGold))
            return PurchaseSpecialistGold;
        return null;
    }
}
