using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>定稿决策树 1-3 运营主循环（J1/N16~N18/S1A/S1B）与 L5 展开（G1/G3/R3）全分支测试。</summary>
public class GrailLoopOperationDeciderTests
{
    private static GrailRunSnapshot Snapshot(
        GrailUserGoal goal = GrailUserGoal.Single,
        int population = 4,
        int gold = 20,
        string[]? deployed = null,
        int badgeCarriers = 0,
        int uncarriedBadges = 0,
        string[]? owned = null,
        bool hasFiveCost = false,
        bool xilian = false,
        int lettersObtained = 0,
        int lettersOpened = 0,
        bool miracle = false,
        int? miracleHealth = null,
        bool cauldron = false,
        int wishesResponded = 2,
        bool fiveBondGivenUp = false,
        int sellable = 3,
        bool newMemberAvailable = false)
    {
        return new GrailRunSnapshot
        {
            Goal = goal,
            Population = population,
            Gold = gold,
            DeployedBondMembers = new HashSet<string>(deployed ?? []),
            BadgeCarrierNonMembers = badgeCarriers,
            UncarriedStarBadges = uncarriedBadges,
            OwnedCharacterNames = new HashSet<string>(owned ?? []),
            HasFiveCostBody = hasFiveCost,
            XilianOnField = xilian,
            LettersObtained = lettersObtained,
            LettersOpened = lettersOpened,
            MiracleCompensationSelected = miracle,
            MiracleCompensationSelectedAtHealth = miracleHealth,
            InfiniteCauldronSelected = cauldron,
            WishesResponded = wishesResponded,
            FiveBondGivenUp = fiveBondGivenUp,
            SellableBeyondKeepLineCount = sellable,
            NewBondMemberAvailable = newMemberAvailable,
        };
    }

    [Fact]
    public void JudgePass_ShortCircuits_ToFinishSuccess()
    {
        // 单人 + 无限之釜已选 → L1 通过 → 收工（循环决策的幂等兜底）
        var s = Snapshot(
            goal: GrailUserGoal.Single, hasFiveCost: true, cauldron: true,
            owned: ["远坂凛", "Archer"]);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.FinishSuccess, op.Kind);
        Assert.Equal("L1→L7", op.TreeNode);
    }

    [Fact]
    public void NormalState_ShopPass()
    {
        // 3 名成员在场（2 档祈愿已响应）、人口够、金币够 → 继续商店循环
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什"], owned: ["远坂凛", "吉尔伽美什"],
            gold: 20, wishesResponded: 1);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
        Assert.Equal("N18→N14", op.TreeNode);
    }

    [Fact]
    public void N16_FifthMemberAvailable_GoldEnough_BuyXpThenDeploy()
    {
        // 4 名成员（4 档）+ 第二枚星徽到手（第 5 成员可获取）+ 人口 4 + 金币 ≥8 → N17a
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            uncarriedBadges: 1, newMemberAvailable: true,
            population: 4, gold: 12, wishesResponded: 3, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.BuyXpThenDeployMember, op.Kind);
        Assert.Equal("N16→N17→N17a", op.TreeNode);
    }

    [Fact]
    public void N17_GoldShort_SellForXpGold()
    {
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            uncarriedBadges: 1, newMemberAvailable: true,
            population: 4, gold: 5, wishesResponded: 3, sellable: 3);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.SellForXpGold, op.Kind);
        Assert.Equal(GrailRunSnapshot.XpPurchaseGoldCost, op.TargetGold);
        Assert.Equal("N17→S1A", op.TreeNode);
    }

    [Fact]
    public void S1A_NothingSellable_GiveUpFiveBond()
    {
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            uncarriedBadges: 1, newMemberAvailable: true,
            population: 4, gold: 5, wishesResponded: 3, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.GiveUpFiveBond, op.Kind);
        Assert.Equal("S1A→N17b", op.TreeNode);
    }

    [Fact]
    public void FiveBondGivenUp_NeverRetriggersXp_ContinuesShopPass()
    {
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            uncarriedBadges: 1, newMemberAvailable: true,
            population: 4, gold: 30, wishesResponded: 3,
            fiveBondGivenUp: true, sellable: 5);
        var op = GrailLoopOperationDecider.DecideNext(s);

        // 已放弃 5 档：闩锁挡住买经验分支（N17b→N14 边）；金币够 → 回商店继续 4 档运营，
        // 直到金币被刷新耗尽走 N18→S1B→R3，而不是立刻判死
        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
    }

    [Fact]
    public void PopulationAlreadyFive_DoesNotNeedXp_ShopPass()
    {
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            uncarriedBadges: 1, newMemberAvailable: true,
            population: 5, gold: 20, wishesResponded: 3, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        // 人口已是 5：第 5 个成员直接上场即可，无需买经验
        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
    }

    [Fact]
    public void N18_GoldShort_SellForGold()
    {
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什"], owned: ["远坂凛", "吉尔伽美什"],
            gold: 1, wishesResponded: 1, sellable: 4);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.SellForGold, op.Kind);
        Assert.Equal(GrailRunSnapshot.MinRefreshGold, op.TargetGold);
        Assert.Equal("N18→S1B", op.TreeNode);
    }

    [Fact]
    public void N18_GoldShort_NothingSellable_ExhaustedReroll()
    {
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什"], owned: ["远坂凛", "吉尔伽美什"],
            gold: 0, wishesResponded: 1, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ExhaustedReroll, op.Kind);
        Assert.Equal("N18→S1B→R3", op.TreeNode);
    }

    [Fact]
    public void G1_AllWishesUsed_Single_ExhaustedReroll()
    {
        // 单人：四次祈愿用尽且未选关键试炼 → G3 不成立（无试炼无翻盘）→ R3
        var s = Snapshot(
            goal: GrailUserGoal.Single,
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            hasFiveCost: true, gold: 30, wishesResponded: 4);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ExhaustedReroll, op.Kind);
        Assert.Equal("L5→G1→G3→R3", op.TreeNode);
    }

    [Fact]
    public void G3_AllMode_MiracleSelected_NoXilian_StillHasGold_ShopPass()
    {
        // 全员：奇迹代偿已选、昔涟未到场、四次祈愿用尽但金币尚可 → G3 成立，继续找昔涟
        var s = Snapshot(
            goal: GrailUserGoal.All,
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            hasFiveCost: true, miracle: true, miracleHealth: 90,
            gold: 10, wishesResponded: 4, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
        Assert.Equal("L5→G1→G3→BACK", op.TreeNode);
    }

    [Fact]
    public void G3_AllMode_MiracleSelected_ButEverythingDry_ExhaustedReroll()
    {
        var s = Snapshot(
            goal: GrailUserGoal.All,
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            hasFiveCost: true, miracle: true, miracleHealth: 90,
            gold: 0, wishesResponded: 4, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ExhaustedReroll, op.Kind);
    }

    [Fact]
    public void G3_SingleMode_NeverFarms()
    {
        // 单人模式即使血量金币都在，四次用尽也没有翻盘路径（成功必须来自两个关键试炼之一）
        var s = Snapshot(
            goal: GrailUserGoal.Single,
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            hasFiveCost: true, gold: 30, wishesResponded: 4, sellable: 5);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ExhaustedReroll, op.Kind);
    }

    [Fact]
    public void G1_NoNextMemberObtainable_ExhaustedReroll_EvenWithChancesLeft()
    {
        // 三名商店成员 + Archer 全拥有、无未携带星徽：第 5 个成员永远拿不到，
        // 剩余祈愿永远无法触发 → 等价于机会耗尽 → R3（试遍所有能进行的尝试后判定）
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            gold: 30, wishesResponded: 3, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ExhaustedReroll, op.Kind);
    }

    [Fact]
    public void G1_UncarriedBadge_CountsAsObtainableMember()
    {
        // 4 名成员、还有 1 枚未携带星徽（第二个星徽到手）→ 第 5 成员可获取 → 走买经验分支
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            uncarriedBadges: 1, newMemberAvailable: true,
            population: 4, gold: 12, wishesResponded: 3, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.BuyXpThenDeployMember, op.Kind);
    }

    [Fact]
    public void UnownedShopMember_KeepsLoopAlive_EvenLateWishes()
    {
        // 只买到凛+闪：Saber 未拥有 → 成员可获取 → 继续商店循环
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什"], owned: ["远坂凛", "吉尔伽美什"],
            gold: 20, wishesResponded: 3, sellable: 2);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
    }

    [Fact]
    public void N16_MemberTheoreticallyObtainableButNotPresent_NoXpPurchase()
    {
        // 4 档（3 命杯成员+1 星徽携带者）、Archer 未拥有但尚未出现：不烧 8 金币买经验，继续商店刷新
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber"],
            badgeCarriers: 1,
            owned: ["远坂凛", "吉尔伽美什", "Saber"],
            population: 4, gold: 30, wishesResponded: 3, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
    }

    [Fact]
    public void BadgeCarrierNonMembers_CountTowardBondTier()
    {
        // 3 名命杯成员 + 1 名星徽携带者（非成员）= 4 档；第 5 成员出现 → 买经验
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber"],
            badgeCarriers: 1,
            owned: ["远坂凛", "吉尔伽美什", "Saber"],
            newMemberAvailable: true,
            population: 4, gold: 12, wishesResponded: 2, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.BuyXpThenDeployMember, op.Kind);
    }

    [Fact]
    public void G3_DirtyMiracleFlag_HealthBelow89_DoesNotFarm()
    {
        // 脏旗标（双侧奇迹代偿病态强选，选择时血 80）：L2 永不通过，G3 同口径不放行 → R3
        var s = Snapshot(
            goal: GrailUserGoal.All,
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            hasFiveCost: true, miracle: true, miracleHealth: 80,
            gold: 30, wishesResponded: 4, sellable: 5);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ExhaustedReroll, op.Kind);
    }

    [Fact]
    public void G3_CanContinueViaUnopenedLetters()
    {
        // 全员：奇迹代偿已选（血 90）、差昔涟，金币 0、无可卖，但聘用书未开（可能开出昔涟）→ 继续
        var s = Snapshot(
            goal: GrailUserGoal.All,
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            hasFiveCost: true, miracle: true, miracleHealth: 90,
            gold: 0, wishesResponded: 4, sellable: 0,
            lettersObtained: 2, lettersOpened: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
    }

    [Fact]
    public void FiveTierCapped_NextTierNeverExceedsFive()
    {
        // 5 档已满员时下一档需求封顶为 5，不出现“需 6 人”的幻觉判定
        var s = Snapshot(
            deployed: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            badgeCarriers: 1,
            owned: ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
            uncarriedBadges: 1, newMemberAvailable: true,
            population: 5, gold: 20, wishesResponded: 3, sellable: 0);
        var op = GrailLoopOperationDecider.DecideNext(s);

        // 5 档封顶：下一档需求=5≤人口5，不触发买经验
        Assert.Equal(GrailLoopOperationKind.ShopPass, op.Kind);
    }
}
