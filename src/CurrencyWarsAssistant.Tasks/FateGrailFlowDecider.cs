using System.Collections.Generic;
using System.Linq;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」商店流程的单一决策面记录。
/// <para>字段语义（含审查报告 H1/H2/L3 修正）：
/// <list type="bullet">
///   <item><see cref="ChosenStrategyIds"/> = 本局要<b>纳入</b>的投资策略 id 全集（含二极管276 与 采购专员051/238，可能同时）。</item>
///   <item><see cref="ShouldBuyDiode"/> = 血量判定要求先买二极管(+10)。</item>
///   <item><see cref="ChosenPurchaseSpecialist"/> = （单值，向后兼容）仅采购专员；若同时需要二极管请用 <see cref="ChosenStrategyIds"/>。</item>
///   <item><see cref="TargetLeftSlotCost"/> = 采购专员触发需要放到备战席<b>最左</b>的目标费用角色名（L3 最左置位信号）。</item>
/// </list></para>
/// </summary>
public sealed record FateGrailShopDecision(
    IReadOnlySet<string> AutoPurchaseNames,
    IReadOnlySet<string> RetainedNames,
    FateGrailHealthGate.HealthDecision Health,
    string? ChosenPurchaseSpecialist,
    IReadOnlySet<string> ChosenStrategyIds,
    bool ShouldBuyDiode,
    string? TargetLeftSlotCost);

/// <summary>
/// 「1-3 三星五费」商店流程的编排门面（纯函数，可独立单测）。
/// <para>
/// 把三个策略辅助组合成一个派生入口，供将来接入商店刷新购买循环时一次性拿到本局决策：
/// 商店名单 → <see cref="FateGrailShoppingPolicy"/>；血量门限 → <see cref="FateGrailHealthGate"/>；
/// 采购专员策略 → <see cref="InvestmentStrategyPicker"/>。
/// </para>
/// <para><b>契约（重要）</b>：本门面是<b>全量急切计算</b>，对 hp 判定无关的采购名单也会一并构建——
/// 三个被组合的策略类均为<b>纯函数、无副作用、不读实时状态</b>，急切计算无代价。
/// 调用方若只要血量判定结果而无需采购名单，可在取到 <see cref="FateGrailShopDecision.Health"/> 后自行忽略其它字段。</para>
/// 注：现有商店刷新购买循环 <c>RunShopRefreshPurchaseLoopAsync</c> 是独立公开入口（当前无流程调用，
/// 由调用方决定何时调用）；本门面即其“名单 / 血量 / 策略”决策来源，接入时传本决策即可，不必侵入式改主流程。
/// </summary>
public static class FateGrailFlowDecider
{
    /// <summary>
    /// 依据本局状态（当前血量、可选投资策略、已持有角色名）产出商店流程所需的一次完整决策。
    /// <para>修订说明（审查报告处理后）：
    /// <list type="number">
    ///   <item><b>H1</b>：血量判定需买二极管且 276 可选时，把 276 纳入 <see cref="FateGrailShopDecision.ChosenStrategyIds"/> 并置 <see cref="FateGrailShopDecision.ShouldBuyDiode"/>。</item>
    ///   <item><b>H2</b>：采购专员(051/238)与 276 一起返回全集 <see cref="FateGrailShopDecision.ChosenStrategyIds"/>，供编排器写入商店 Options。</item>
    ///   <item><b>L2</b>：自动购买名单剔除已持有成员。</item>
    ///   <item><b>L3</b>：命中采购专员时，通过 <see cref="FateGrailShopDecision.TargetLeftSlotCost"/> 给出应放备战席最左的目标费用角色名。</item>
    /// </list></para>
    /// </summary>
    public static FateGrailShopDecision Compute(
        int currentHp,
        IEnumerable<string>? availableStrategyIds,
        IEnumerable<string>? heldOrOwnedNames)
    {
        // 边界物化一次并把无效 id 过滤掉，避免桩层惰性序列被多次枚举 / null 元素抛错。
        var strategyIds = availableStrategyIds?
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var holders = (heldOrOwnedNames ?? []).ToList();

        var decision = FateGrailHealthGate.EvaluateHealthGate(currentHp);
        // H1：血量要求买二极管时，仅当 276 可选才真正置肩（否则置 ShouldBuyDiode=false）。
        var shouldBuyDiode =
            decision == FateGrailHealthGate.HealthDecision.BuyDiodeAndProceed &&
            strategyIds is not null &&
            strategyIds.Contains(FateGrailHealthGate.DiodeInvestmentStrategyId);

        // 采购专员（单值，向后兼容现有测试）
        var specialist = InvestmentStrategyPicker
            .PickPurchaseSpecialist(strategyIds);

        // H1/H2：本局要纳入的策略 id 全集 = 二极管(若需要且可选) + 采购专员。
        var chosenStrategyIds = new HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase);
        if (shouldBuyDiode)
            chosenStrategyIds.Add(FateGrailHealthGate.DiodeInvestmentStrategyId);
        if (specialist is not null)
            chosenStrategyIds.Add(specialist);

        // L2：自动购买名单剔除已持有成员。
        var autoPurchase = FateGrailShoppingPolicy
            .BuildAutoPurchaseNames(holders);

        // L3：命中采购专员时，给出应放最左的目标费用角色名 = 商店可买且仍缺的最高费成员
        //      （最需要抬该费用的那个；全部已持有则 null，表示无需抬）。
        string? targetLeftSlotCost = null;
        if (specialist is not null)
        {
            // 商店按费用从高到低：Saber(3) > 吉尔伽美什(2) > 远坂凛(1)；缺者需抬。
            foreach (var candidate in FateGrailShoppingPolicy.StorePurchaseNames.Reverse())
            {
                if (autoPurchase.Contains(candidate)) // 仍在购买名单 = 尚缺
                {
                    targetLeftSlotCost = candidate;
                    break;
                }
            }
        }

        return new FateGrailShopDecision(
            autoPurchase,
            FateGrailShoppingPolicy.BuildRetainedNames(holders),
            decision,
            specialist,
            chosenStrategyIds,
            shouldBuyDiode,
            targetLeftSlotCost);
    }
}
