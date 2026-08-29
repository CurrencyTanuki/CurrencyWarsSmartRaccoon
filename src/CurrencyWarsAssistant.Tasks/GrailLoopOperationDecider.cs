namespace CurrencyWarsAssistant.Tasks;

/// <summary>1-3 运营主循环的下一步动作类型（对应定稿树 N14 后置检查链与 L5 展开）。</summary>
public enum GrailLoopOperationKind
{
    /// <summary>L7：L1 判定通过，收工停机。</summary>
    FinishSuccess,

    /// <summary>N14：回商店继续“刷新—见到就买—关店—上场—（弹框）”一轮。</summary>
    ShopPass,

    /// <summary>N17a：先买经验把人口升到 5，再购买并上场第 5 个羁绊成员（顺序固定，防“成员在备战席、人口卡 4”死局）。</summary>
    BuyXpThenDeployMember,

    /// <summary>S1A：出售角色凑买经验所需金币（保留线=星徽数，5 费绝不卖）。</summary>
    SellForXpGold,

    /// <summary>N17b：放弃 5 档激活（永久），继续 4 档运营——第 4 档祈愿已是最后一次试炼机会。</summary>
    GiveUpFiveBond,

    /// <summary>S1B：出售角色凑商店刷新/购买资金。</summary>
    SellForGold,

    /// <summary>R3：山穷水尽——所有能进行的尝试已试尽，退出结算重新开局。</summary>
    ExhaustedReroll,
}

/// <summary>运营循环决策结果。TargetGold：出售动作要凑到的金币目标（其他动作为 0）。</summary>
public sealed record GrailLoopOperation(GrailLoopOperationKind Kind, int TargetGold, string Reason, string TreeNode);

/// <summary>
/// 定稿决策树「1-3 备战运营主循环」（N14 后置检查链 J1/N16/N17/S1A/N17a/N17b/N18/S1B）
/// 与「L5 展开」（G1/G3/R3）的纯逻辑实现。
/// 调用约定：每次循环 tick（含弹框响应后、状态变化后）调用本方法取得下一步动作；
/// J1（新买到昔涟/新打开聘用书）事件由上层直接调用 <see cref="GrailFinalJudge.Judge"/>，本方法先行判定兜底，二者幂等。
/// </summary>
public static class GrailLoopOperationDecider
{
    public static GrailLoopOperation DecideNext(GrailRunSnapshot s)
    {
        // L1 先行判定（幂等兜底）：成功即收工
        var verdict = GrailFinalJudge.Judge(s);
        if (verdict.Kind == GrailVerdictKind.Success)
            return new(
                GrailLoopOperationKind.FinishSuccess,
                TargetGold: 0,
                verdict.Reason,
                "L1→L7");

        var nextMemberObtainable = IsNextMemberObtainable(s);
        var chancesRemain = s.WishesResponded < 4;

        // G1 ③：四次祈愿（2/3/4/5 档各一次）已全部响应完毕
        if (!chancesRemain)
            return G3OrExhausted(s, "四次祈愿已全部响应完毕（G1③不成立）。");

        // G1 ①：不存在可获取的下一个羁绊成员 → 更高档位永远无法激活
        // 注：G1 ②（人口可行）不在此单独设闸——其穷尽效果由下方 N16→S1A/N17b→N18→S1B→R3
        // 操作链自然达成，以保持决策树 N17b→N14“放弃 5 档后继续 4 档运营（金币耗尽才 R3）”的既有边。
        if (!nextMemberObtainable)
            return G3OrExhausted(s, "已无可获取的下一个羁绊成员（G1①不成立）。");

        // N16：激活下一档羁绊所需上场人数 > 当前人口，且第 5 个成员已经出现
        //（商店刷出未拥有命杯成员 / 第二枚星徽到手——执行层回填 NewBondMemberAvailable；
        //  仅“理论上可获取但尚未出现”时不烧 8 金币买经验）
        var needXpForNextMember =
            s.NextTierRequiredMembers > s.Population && nextMemberObtainable && !s.FiveBondGivenUp
            && s.NewBondMemberAvailable;
        if (needXpForNextMember)
        {
            // N17：金币是否足够购买经验把人口升到 5（需 ≥8 金币）
            if (s.Gold >= GrailRunSnapshot.XpPurchaseGoldCost)
                return new(
                    GrailLoopOperationKind.BuyXpThenDeployMember,
                    TargetGold: 0,
                    "金币充足：停下当前商店操作，先买经验升到 5 人口，再购买并上场第 5 个成员（顺序固定），激活 5 档触发最终祈愿。",
                    "N16→N17→N17a");

            // S1A：卖人凑金币（仅 1-3；保留线=星徽数；所有已拥有 5 费绝不卖）
            if (s.SellableBeyondKeepLineCount > 0)
                return new(
                    GrailLoopOperationKind.SellForXpGold,
                    TargetGold: GrailRunSnapshot.XpPurchaseGoldCost,
                    $"金币不足 {GrailRunSnapshot.XpPurchaseGoldCost}，先按保留线出售非命杯、非星徽携带者角色凑金币（5 费绝不卖）。",
                    "N17→S1A");

            // S1A 已无可卖 → N17b：5 档激活机会永久失去，继续 4 档运营
            return new(
                GrailLoopOperationKind.GiveUpFiveBond,
                TargetGold: 0,
                "卖光可卖角色仍凑不足买经验金币，5 档激活机会永久失去；继续 4 档运营（第 4 档祈愿已是最后一次试炼机会）。",
                "S1A→N17b");
        }

        // N18：金币是否足够继续刷新/购买目标角色（命杯成员/本体昔涟）
        if (s.Gold >= GrailRunSnapshot.MinRefreshGold)
            return new(
                GrailLoopOperationKind.ShopPass,
                TargetGold: 0,
                "金币足够，回商店继续刷新购买。",
                "N18→N14");

        // S1B：没钱刷新/购买 → 卖人凑钱
        if (s.SellableBeyondKeepLineCount > 0)
            return new(
                GrailLoopOperationKind.SellForGold,
                TargetGold: GrailRunSnapshot.MinRefreshGold,
                "金币不足以刷新/购买，按保留线出售角色凑资金。",
                "N18→S1B");

        // S1B 已无可卖 → R3
        return new(
            GrailLoopOperationKind.ExhaustedReroll,
            TargetGold: 0,
            "金币耗尽且非命杯角色已卖到保留线（不允许跨过 1-3 获取收入），山穷水尽。",
            "N18→S1B→R3");
    }

    /// <summary>
    /// G1 ①：是否还存在可获取的下一个羁绊成员（按不同角色去重）。
    /// 来源：①凛/闪/Saber 商店购买（未拥有的）②Archer=试炼/聘用书（商店 0%；还有祈愿机会或未开聘用书即视为可获取）
    /// ③已获得但未被携带的星徽（再上场一名携带者即 +1 成员）。
    /// </summary>
    private static bool IsNextMemberObtainable(GrailRunSnapshot s)
    {
        foreach (var name in GrailRunSnapshot.ShopBondMemberNames)
        {
            if (!s.OwnedCharacterNames.Contains(name))
                return true;
        }

        if (!s.OwnedCharacterNames.Contains(GrailRunSnapshot.ArcherName)
            && (s.LettersOpened < s.LettersObtained || s.WishesResponded < 4))
            return true;

        return s.UncarriedStarBadges > 0;
    }

    /// <summary>
    /// G3：是否存在无需新祈愿、就能翻转最终判定的剩余手段。
    /// 仅全员成立：奇迹代偿已“有效”选择（旗标+选择时血≥89——与 L2 同口径，
    /// 防止病态脏旗标无限续命）、只差本体昔涟进场，且仍有资金/可卖角色/未开聘用书。
    /// 单人模式没有试炼就没有任何翻盘路径（用户公理：成功必须刷出两个关键试炼之一）。
    /// </summary>
    private static GrailLoopOperation G3OrExhausted(GrailRunSnapshot s, string g1FailureReason)
    {
        var canFarmXilian = s.Goal == GrailUserGoal.All
            && s.MiracleCompensationSelected
            && (s.MiracleCompensationSelectedAtHealth ?? 0) >= GrailFinalJudge.MiracleHealthThreshold
            && !s.XilianOnField
            && (s.Gold >= GrailRunSnapshot.MinRefreshGold
                || s.SellableBeyondKeepLineCount > 0
                || s.LettersOpened < s.LettersObtained);

        return canFarmXilian
            ? new(
                GrailLoopOperationKind.ShopPass,
                TargetGold: 0,
                $"{g1FailureReason} 但全员模式只差本体昔涟进场且仍有手段（G3 成立），继续刷新/采购专员找昔涟。",
                "L5→G1→G3→BACK")
            : new(
                GrailLoopOperationKind.ExhaustedReroll,
                TargetGold: 0,
                $"{g1FailureReason} 且无翻盘手段（G3 不成立），山穷水尽：所有能进行的尝试已全部试尽。",
                "L5→G1→G3→R3");
    }
}
