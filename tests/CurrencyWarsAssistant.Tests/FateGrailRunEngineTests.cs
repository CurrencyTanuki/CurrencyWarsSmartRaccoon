using System;
using System.Collections.Generic;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」决策树主链引擎（FateGrailRunEngine）单测——覆盖决策树各节点分支。</summary>
public sealed class FateGrailRunEngineTests
{
    private const string Rin = "远坂凛";
    private const string Gil = "吉尔伽美什";
    private const string Saber = "Saber";
    private const string Archer = "Archer";
    private static readonly string[] StoreAll = { Rin, Gil, Saber };
    private static readonly string[] NoStrategies = Array.Empty<string>();

    private static FateGrailRunEngine.Snapshot Base(
        string env = FateGrailRunEngine.EnvironmentHeroArrival,
        int hp = 94,
        IReadOnlySet<string>? owned = null,
        FateGrailRunEngine.UserGoal goal = FateGrailRunEngine.UserGoal.AnyOne,
        IReadOnlySet<string>? strategies = null,
        bool hasBody = true,
        bool hasStar = true,
        int bondTier = 4)
        => new(
            EnvironmentId: env,
            Hp: hp,
            Gold: 50,
            OwnedMembers: owned ?? new HashSet<string>(StoreAll),
            AvailableStrategyIds: strategies ?? new HashSet<string>(
                new[] { InvestmentStrategyPicker.PurchaseSpecialistColor }),
            Goal: goal,
            Line: FateGrailRunEngine.EndLine.MiracleCompensation,
            HasBody5Cost: hasBody,
            HasStarBadge: hasStar,
            BondTier: bondTier);

    private static IReadOnlySet<string> Owned(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    // ---------- D1 环境可推进 ----------
    [Fact]
    public void D1_EnvironmentNotViable_RequestsReroll()
    {
        var step = FateGrailRunEngine.Step(
            Base(env: "investment_environment_999"));
        Assert.True(step.RequiresReroll);
        Assert.Equal("D1", step.Node);
    }

    [Fact]
    public void D1_ContractEnvironment_NotViable_RequestsReroll()
    {
        // 2026-08-26 用户裁决：018 命运圣杯契约 1-3 最多 3 人口，摸不到最终圣杯试炼门槛，
        // 不可能达成三星五费 -> 已从可推进环境剔除，遇到应重刷。
        var step = FateGrailRunEngine.Step(
            Base(env: FateGrailRunEngine.EnvironmentContract));
        Assert.True(step.RequiresReroll);
        Assert.Equal("D1", step.Node);
    }

    // ---------- 奇迹代偿线 X1/X2 买二极管 ----------
    [Fact]
    public void MiracleLine_HpBelow88_WithDiode_RequestsBuyDiode()
    {
        // 84 < 88，策略含 276 -> 买二极管补到 +10=94
        var s = Base(hp: 84, hasBody: true,
            strategies: Owned(FateGrailHealthGate.DiodeInvestmentStrategyId));
                s = s with { LeftTrial = "令人决议·奇迹代偿：扣88血", RightTrial = "普通试炼" };
var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.BuyDiode, step.Action);
        Assert.Equal("X2", step.Node);
    }

    [Fact]
    public void MiracleLine_HpBelow88_DiodeMissing_DirectReroll()
    {
        // 84 < 88 且策略里没有 276 -> 无法补血 -> 重刷
        var s = Base(hp: 84, strategies: Owned(InvestmentStrategyPicker.PurchaseSpecialistColor));
                s = s with { LeftTrial = "令人决议·奇迹代偿：扣88血", RightTrial = "普通试炼" };
var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("N2", step.Node);
    }

    [Fact]
    public void MiracleLine_HpBelow78_EvenWithDiode_Reroll()
    {
        var s = Base(hp: 70, strategies: Owned(FateGrailHealthGate.DiodeInvestmentStrategyId));
                s = s with { LeftTrial = "令人决议·奇迹代偿：扣88血", RightTrial = "普通试炼" };
var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("N2", step.Node);
    }

    // ---------- G 买商店成员 ----------
    [Fact]
    public void G_MissingStoreMember_RequestsBuy()
    {
        var s = Base(owned: Owned(Rin, Gil));
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.BuyStoreMembers, step.Action);
        Assert.Equal("G", step.Node);
    }

    // ---------- D3 凑 4 圣杯 ----------
    [Fact]
    public void D3_NoFourthBondTrigger_RequestsReroll()
    {
        // 商店3人全持，但既无 Archer 也无星徽 -> 第4档缺 -> 重刷
        var s = Base(owned: Owned(StoreAll), hasStar: false);
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.RequiresReroll);
        Assert.Equal("D3", step.Node);
    }

    // ---------- D4/J1 本体 ----------
    [Fact]
    public void J1_No5CostBody_RequestsEnsureBody()
    {
        var s = Base(hasBody: false);
                s = s with { LeftTrial = "令人决议·奇迹代偿：扣88血", RightTrial = "普通试炼" };
var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.Ensure5CostBody, step.Action);
    }

    // ---------- D5/H2/N1 祈愿奇迹代偿 ----------
    [Fact]
    public void N1_MiracleTrialWithEnoughHp_ChoosesMiracleCompensation()
    {
        var s = Base(hp: 94) with { LeftTrial = "令人决议·奇迹代偿：扣88血" };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChooseMiracleCompensation, step.Action);
        Assert.Equal("N1", step.Node);
    }

    [Fact]
    public void N2_MiracleTrialHpBelow88_ChoosesPassiveTrial()
    {
        // 无限之釜线（不需补血）：祈愿浮现奇迹代偿但 hp.80<88 -> 图标不可点，改选另一试炼（不重刷）
        var s = Base(hp: 80, hasBody: true) with
        {
            Line = FateGrailRunEngine.EndLine.InfernalCauldron,
            LeftTrial = "令人决议·奇迹代偿：扣88血",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("N2", step.Node);
    }

    // ---------- 无限之釜线达成（N1 同构：先点选所在侧、再收工） ----------
    [Fact]
    public void CauldronLine_AnyOne_DoneAndUserManual()
    {
        var s = Base(goal: FateGrailRunEngine.UserGoal.AnyOne,
            strategies: Owned(InvestmentStrategyPicker.PurchaseSpecialistColor))
            with { LeftTrial = "诅咒·无限之釜：直接获得1个三星5费Archer" };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.Done);
        // （N1 修复）祈愿试炼是 2 选 1 弹框，必须先点选才算领取 -> 发 ChoosePassiveTrial 而非 Achieved
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("P", step.Node);
        Assert.Equal(FateGrailRunEngine.TrialSide.Left, step.TrialToChoose);
        Assert.True(step.RequiresUserManual);
        Assert.Equal("EAA", step.NextNode);
    }

    [Fact]
    public void CauldronLine_RightSide_ChoosesRight()
    {
        // （N1 回归）无限之釜在右侧 -> TrialToChoose=Right
        var s = Base(strategies: Owned(InvestmentStrategyPicker.PurchaseSpecialistColor))
            with { RightTrial = "诅咒·无限之釜：直接获得1个三星5费Archer" };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.Done);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal(FateGrailRunEngine.TrialSide.Right, step.TrialToChoose);
    }

    [Fact]
    public void CauldronLine_GoalAll_DoneWithoutUserManual_ToEAB()
    {
        // （N1 回归·镜像）目标 B 全员：收工但不交人工，下一节点 EAB
        var s = Base(goal: FateGrailRunEngine.UserGoal.All,
            strategies: Owned(InvestmentStrategyPicker.PurchaseSpecialistColor))
            with { LeftTrial = "诅咒·无限之釜：直接获得1个三星5费Archer" };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.Done);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.False(step.RequiresUserManual);
        Assert.Equal("EAB", step.NextNode);
    }

    [Fact]
    public void CauldronLine_MissingFourthBond_RequestsReroll()
    {
        // （N2 修复）无限之釜线也要过 D3：无 Archer 且无星徽 -> 4 圣杯凑不齐，
        // 胜利试炼永不浮现 -> 早重刷，而不是空转到 maxDecisions。
        var s = Base(hasStar: false,
            strategies: Owned(InvestmentStrategyPicker.PurchaseSpecialistColor))
            with
            {
                Line = FateGrailRunEngine.EndLine.InfernalCauldron,
                LeftTrial = "普通试炼A",
                RightTrial = "普通试炼B",
            };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.RequiresReroll);
        Assert.Equal("D3", step.Node);
    }

    [Fact]
    public void N2_MiracleTrialOnRight_HpBelow88_ChoosesLeft()
    {
        // （镜像）奇迹代偿在右侧、血<88 -> 改选左侧（非禁选、未耗尽 -> 不重刷）
        var s = Base(hp: 80) with
        {
            Line = FateGrailRunEngine.EndLine.InfernalCauldron,
            RightTrial = "令人决议·奇迹代偿：扣88血",
            LeftTrial = "普通试炼A",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("N2", step.Node);
        Assert.Equal(FateGrailRunEngine.TrialSide.Left, step.TrialToChoose);
        Assert.False(step.RequiresReroll);
    }

    [Fact]
    public void N2_MiracleTrialHpBelow88_OtherSideForbidden_Rerolls()
    {
        // （N3 修复）奇迹代偿不可点（血<88）且另一侧是禁选试炼（出售圣杯）-> 不能点自毁项 -> 重刷。
        // 走无限之釜线（不需 88 血）才能到达试炼分支；奇迹线会先在 X1 因缺二极管重刷。
        var s = Base(hp: 80) with
        {
            Line = FateGrailRunEngine.EndLine.InfernalCauldron,
            LeftTrial = "令人决议·奇迹代偿：扣88血",
            RightTrial = "出售所有圣杯，换取金币",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.RequiresReroll);
        Assert.Equal("C2", step.Node);
    }

    // ---------- 奇迹代偿达成（用户 2026-08-25 简化：选奇迹代偿即成功，无需再确认 8 投影） ----------
    [Fact]
    public void MiracleN1_WithBody_NoXilian_AchievedAnyOne()
    {
        // 血≥88 + 已识别奇迹代偿 + 5 费本体在池（非昔涟）-> 点选奇迹代偿即成功（无需 8 投影识别）。
        var s = Base(hp: 94, hasBody: true) with
        {
            LeftTrial = "令人决议·奇迹代偿：扣88血",
            FiveCostBodyIds = new HashSet<string>(new[] { "Archer" }),
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.Done);
        Assert.Equal(FateGrailRunEngine.Action.ChooseMiracleCompensation, step.Action);
        Assert.Equal("N1", step.Node);
        Assert.Equal("EAA", step.NextNode);      // 非昔涟 -> 达成 1 个三星五费
        Assert.True(step.RequiresUserManual);    // 交用户手动拖 8 投影成型
    }

    [Fact]
    public void MiracleN1_WithXilianBody_AchievedAll()
    {
        // 5 费本体含昔涟 + 奇迹代偿 -> 全员三星五费达成（EAB）。
        var s = Base(hp: 96, hasBody: true, goal: FateGrailRunEngine.UserGoal.All) with
        {
            LeftTrial = "诅咒·奇迹代偿：扣88血",
            DiodeTaken = false,
            FiveCostBodyIds = new HashSet<string>(new[] { "Archer", "昔涟" }),
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.Done);
        Assert.Equal(FateGrailRunEngine.Action.ChooseMiracleCompensation, step.Action);
        Assert.Equal("N1", step.Node);
        Assert.Equal("EAB", step.NextNode);      // 含昔涟 -> 全员三星五费
        Assert.True(step.RequiresUserManual);
    }

    [Fact]
    public void MiracleN1_NoBody_EnsureBodyFirst()
    {
        // 血≥88 + 奇迹代偿但 5 费本体未在池 -> 先确保本体（J1），不直接成功。
        var s = Base(hp: 94, hasBody: false) with
        {
            LeftTrial = "令人决议·奇迹代偿：扣88血",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.Ensure5CostBody, step.Action);
        Assert.Equal("J1", step.Node);
        Assert.False(step.Done);
    }

    // ---------- 一般试炼择优 ----------
    [Fact]
    public void Trial_HighValueLeft_SelectsIt()
    {
        var s = Base(hp: 94, hasBody: true) with
        {
            LeftTrial = "获得两张五费聘用书",
            RightTrial = "普通试炼",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("M2", step.Node); // 有高价值侧 -> 选目标试炼
    }

    [Fact]
    public void Trial_NoDesiredBoth_DefaultLeftNoReroll()
    {
        var s = Base(hp: 94, hasBody: true) with
        {
            LeftTrial = "普通试炼A",
            RightTrial = "普通试炼B",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.False(step.RequiresReroll);
    }

    // ---------- 目标 B 全员：本体未到手先走 J1；本体获取失败即重刷 ----------
    [Fact]
    public void GoalAll_NoBody_EnsureBodyFirst()
    {
        var s = Base(goal: FateGrailRunEngine.UserGoal.All, hasBody: false) with
        {
            LeftTrial = "令人决议·奇迹代偿：扣88血",
            RightTrial = "普通试炼B",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.Ensure5CostBody, step.Action);
        Assert.Equal("J1", step.Node);
    }

    [Fact]
    public void GoalAny_NoBody_BodyAcquisitionFailed_Reroll()
    {
        // 本体尚未到手且获取失败（登场Archer/聘书均不可得、金币花完）-> J2 重刷
        var s = Base(goal: FateGrailRunEngine.UserGoal.AnyOne, hasBody: false) with
        {
            BodyAcquisitionFailed = true,
            LeftTrial = "令人决议·奇迹代偿：扣88血",
            RightTrial = "普通试炼B",
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.RequiresReroll);
        Assert.Equal("J2", step.Node);
    }

    // ---------- 抽取机会耗尽（BondTier=5 圣杯成员收满仍无胜利试炼 -> 重刷） ----------
    [Fact]
    public void Tier5AllMembersCollected_NoVictoryTrial_Rerolls()
    {
        // 用户 2026-08-25：5 个圣杯羁绊角色收满（BondTier=5）= 抽取机会用尽；
        // 本轮仍无胜利试炼（普通试炼两侧）-> 本局无法达成目标 -> 重刷。
        // 人口已满 5：5 圣杯齐但无胜利件 -> 机会耗尽重刷。
        var s = Base(bondTier: 5) with
        {
            LeftTrial = "普通试炼A",
            RightTrial = "普通试炼B",
            Population = 5,
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.True(step.RequiresReroll);
        Assert.Equal("C2", step.Node);
    }

    [Fact]
    public void Tier5AllMembersCollected_PopulationBelow5_RequestsBuyPopulation()
    {
        // 用户 2026-08-26：5 圣杯齐但人口<5 时，先买经验升人口（激活5圣杯），不直接重刷。
        var s = Base(bondTier: 5) with
        {
            LeftTrial = "普通试炼A",
            RightTrial = "普通试炼B",
            Population = 4,
        };
        var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.BuyPopulation, step.Action);
        Assert.False(step.RequiresReroll);
        Assert.Equal("POP", step.Node);
    }

    // ---------- 偏差① DiodeTaken/Hp 语义回归（评审确定性 bug） ----------
    [Fact]
    public void MiracleLine_DiodeTakenHp80_EffectHp90_Continues()
    {
        // Hp=原始80，已吃二极管 -> 有效 hp=90>=88：不应重刷、不再刷二极管、继续推进到祈愿择优。
        //（修复前旧逻辑会因 Hp<88 且 DiodeTaken 误重刷。）
        var s = Base(hp: 80) with { DiodeTaken = true };
        var step = FateGrailRunEngine.Step(s);
        Assert.False(step.RequiresReroll);
        Assert.NotEqual(FateGrailRunEngine.Action.BuyDiode, step.Action);
        // 商店成员全持、本体在池、非黑杯 -> 有效 hp 通过血门后落入祈愿择优（M2 选侧）。
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("M2", step.Node);
    }

    [Fact]
    public void MiracleLine_DiodeTakenHp70_EffectHp80_Reroll()
    {
        // Hp=原始70，已吃二极管 -> 有效 hp=80 仍<88：奇迹线永久不可点 -> 重刷。
        var s = Base(hp: 70) with { DiodeTaken = true };
                s = s with { LeftTrial = "令人决议·奇迹代偿：扣88血", RightTrial = "普通试炼" };
var step = FateGrailRunEngine.Step(s);
        Assert.Equal(FateGrailRunEngine.Action.ChoosePassiveTrial, step.Action);
        Assert.Equal("N2", step.Node);
    }
}
