namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」决策树中的血量门限判定（决策辅助，纯逻辑，可独立单测）。
/// <para>对应决策树「读血量 → hp&lt;88 买二极管(+10) → 复核买后 hp≥88 → 否则放弃奇迹代偿线」这一段。</para>
/// 机制常量（2026-08-24 核对 data）：
/// <list type="bullet">
///   <item><see cref="HealthGateThreshold"/>=88：奇迹代偿(8完美投影仪)需扣 88 血，前置须 hp≥88 才扣得动（hp&lt;88 图标不可点）。</item>
///   <item><see cref="DiodeInvestmentStrategyId"/>=276：投资策略「二极管」，本场遭遇节点只有最低/最高两难度 + 获得 10 小队生命值与上限 + 6 金币（planes=[1]，1-3 可用）。</item>
///   <item><see cref="DiodeHealthBonus"/>=10：二极管固定 +10 血。</item>
/// </list>
/// 无限之釜线不需 88 血，但触发仍需凑 4 羁绊（见 FateGrailShoppingPolicy / 决策树 B4）。
/// </summary>
public static class FateGrailHealthGate
{
    /// <summary>奇迹代偿(扣88血)所需 hp 门槛。</summary>
    public const int HealthGateThreshold = 88;

    /// <summary>投资策略「二极管」的 id。</summary>
    public const string DiodeInvestmentStrategyId = "investment_strategy_276";

    /// <summary>二极管给予的小队生命值/生命上限加成。</summary>
    public const int DiodeHealthBonus = 10;

    /// <summary>血量门限判定结果。</summary>
    public enum HealthDecision
    {
        /// <summary>hp ≥ 88：无需二极管，奇迹代偿/无限之釜两条线都就绪。</summary>
        NoDiodeNeeded,

        /// <summary>78 ≤ hp &lt; 88：买二极管(+10)即可 ≥88，两条线仍可。</summary>
        BuyDiodeAndProceed,

        /// <summary>hp &lt; 78：+10 仍 &lt;88，奇迹代偿线不可行（本局放弃该线）；无限之釜线仍可尝试。</summary>
        MiracleLineUnavailable,
    }

    /// <summary>
    /// 依据当前血量给出本局血量处理决策。
    /// </summary>
    public static HealthDecision EvaluateHealthGate(int currentHp)
    {
        if (currentHp >= HealthGateThreshold)
            return HealthDecision.NoDiodeNeeded;
        // hp < 88：二极管 +10 后是否仍 <88（即 currentHp < 78）？
        if (currentHp < HealthGateThreshold - DiodeHealthBonus)
            return HealthDecision.MiracleLineUnavailable;
        return HealthDecision.BuyDiodeAndProceed;
    }

    /// <summary>
    /// 判断此刻「奇迹代偿（需扣 88 血）」是否可点。
    /// <paramref name="diodeAlreadyApplied"/> 表示已吃过二极管 +10。
    /// </summary>
    public static bool IsMiracleCompensationClickable(
        int currentHp,
        bool diodeAlreadyApplied = false) =>
        (currentHp + (diodeAlreadyApplied ? DiodeHealthBonus : 0)) >=
        HealthGateThreshold;
}
