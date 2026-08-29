namespace CurrencyWarsAssistant.Tasks;

/// <summary>1-3 投资策略选择决策结果（定稿树 N3~N11）。</summary>
public sealed record GrailStrategyDecision(
    string? StrategyId,
    int? SlotIndex,
    string Reason,
    string TreeNode)
{
    /// <summary>N11：四个目标策略均未出现 → 按正常流程选最左边一个，不为挑策略重刷。</summary>
    public static GrailStrategyDecision Leftmost() =>
        new(null, null, "四个目标投资策略均未刷出，按正常流程选最左边的一个，不为挑策略重刷。", "N9→N11");
}

/// <summary>
/// 定稿决策树「1-3 投资策略选择」（N3~N11）纯逻辑实现。
/// 目标策略四选一：采购专员·彩(238) / 全是这家伙的错！(333) / 采购专员·金(051) / 二极管(276)。
/// N10：有且只有 333 与彩 238 可能同现，此时优先彩 238；其余互斥，出现即选。
/// N11：四个都未出现（刷新后全部未命中）→ 选最左，不重刷。
/// 输入为策略页三个槽位识别出的策略 ID（图标模板匹配产物， investment_strategy_XXX）。
/// </summary>
public static class GrailInvestmentStrategyDecider
{
    public const string ColorSpecialistId = "investment_strategy_238";
    public const string ItIsHisFaultId = "investment_strategy_333";
    public const string GoldSpecialistId = "investment_strategy_051";
    public const string DiodeId = "investment_strategy_276";

    /// <summary>选择优先级：彩 238 &gt; 333 &gt; 金 051 &gt; 276（同现仅彩/333，其余互斥；顺序仅为确定性）。</summary>
    private static readonly string[] Priority =
    [
        ColorSpecialistId,
        ItIsHisFaultId,
        GoldSpecialistId,
        DiodeId,
    ];

    public static GrailStrategyDecision Decide(IReadOnlyList<string?> slotStrategyIds)
    {
        foreach (var target in Priority)
        {
            for (var slot = 0; slot < slotStrategyIds.Count; slot++)
            {
                if (string.Equals(slotStrategyIds[slot], target, StringComparison.OrdinalIgnoreCase))
                    return new(
                        target,
                        slot,
                        $"命中目标投资策略 {target}（槽位 {slot}），选择之。",
                        "N3→N10");
            }
        }

        return GrailStrategyDecision.Leftmost();
    }
}
