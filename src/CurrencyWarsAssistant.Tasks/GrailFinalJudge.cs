namespace CurrencyWarsAssistant.Tasks;

/// <summary>L1 最终判定结论类型。</summary>
public enum GrailVerdictKind
{
    /// <summary>成功：满足收工条件（L7），软件停止操作。</summary>
    Success,

    /// <summary>尚未成功：继续运营或走向山穷水尽判定（L5）。</summary>
    NotYet,
}

/// <summary>L1 最终判定结果（附决策树节点号与理由，便于逐节点追溯）。</summary>
public sealed record GrailVerdict(GrailVerdictKind Kind, string TreeNode, string Reason)
{
    public static GrailVerdict Success(string node, string reason) => new(GrailVerdictKind.Success, node, reason);

    public static GrailVerdict NotYet(string node, string reason) => new(GrailVerdictKind.NotYet, node, reason);
}

/// <summary>
/// 定稿决策树「最终软件成功刷出开局的判定」（L1~L10）纯逻辑实现。
/// 成功路径：
///   单人 = L4（场上含备战席有 5 费且聘用书全开）且（L6 无限之釜已选 或 L2 奇迹代偿已选@选择时血≥89）；
///   全员 = L4 且 L2 且 L10 昔涟在场——无限之釜在全员模式只是辅助（L3→L5），这是用户 2026-08-29 拍板的核心语义。
/// </summary>
public static class GrailFinalJudge
{
    /// <summary>奇迹代偿血量门槛：代价是损失 88 小队生命值，因此必须 >88（即 ≥89）才允许选择。</summary>
    public const int MiracleHealthThreshold = 89;

    public static GrailVerdict Judge(GrailRunSnapshot s)
    {
        // L4：目前场上（前台、后台、备战席），并且五费聘用书全部打开了的情况下，是否有 5 费角色？
        if (!s.HasFiveCostBody)
            return GrailVerdict.NotYet("L4", "场上没有 5 费角色（含备战席，聘用书打开后计入）。");
        if (!s.AllLettersOpened)
            return GrailVerdict.NotYet("L4", "存在未打开的五费聘用书。");

        // L6 + L3（单人）：无限之釜已正确选择 → 视为通关
        if (s.InfiniteCauldronSelected && s.Goal == GrailUserGoal.Single)
            return GrailVerdict.Success("L6→L3→L7", "单人目标：无限之釜已正确选择，视为通关。");

        // L2：是否已经正确选择了令咒决议·奇迹代偿，并且选择这个试炼的时候血量满足大于 88（即 ≥89）？
        if (s.MiracleCompensationSelected && (s.MiracleCompensationSelectedAtHealth ?? 0) >= MiracleHealthThreshold)
        {
            // L9
            if (s.Goal == GrailUserGoal.Single)
                return GrailVerdict.Success("L2→L9→L7", "单人目标：奇迹代偿已正确选择（选择时血≥89）。");

            // L10：目前场上所有的 5 费角色中是否有名为昔涟的角色？
            return s.XilianOnField
                ? GrailVerdict.Success("L2→L9→L10→L7", "全员目标：奇迹代偿已选且本体昔涟在场。")
                : GrailVerdict.NotYet("L10→L5", "全员目标：奇迹代偿已选但场上没有昔涟。");
        }

        // L6 + L3（全员，未选奇迹代偿）：无限之釜只是辅助（视为获得一个 5 费角色），不收工。
        // 修正说明（子代理审查发现）：树的字面结构 L6是→L3→全员→L5 没有回边，会导致
        // 「全员+釜+代偿+昔涟」全真时被误判死局；按用户公理（全员=奇迹代偿+本体昔涟），
        // 全员模式下釜已选时必须继续检查 L2/L10，而非无条件 NotYet。
        if (s.InfiniteCauldronSelected)
            return GrailVerdict.NotYet("L6+L3→L5",
                "全员目标：无限之釜只是辅助（视为获得一个5费角色），仍需奇迹代偿+本体昔涟。");

        return GrailVerdict.NotYet("L2→L5",
            s.MiracleCompensationSelected
                ? "奇迹代偿选择时血量不满足 ≥89。"
                : "无限之釜与奇迹代偿均未正确选择。");
    }
}
