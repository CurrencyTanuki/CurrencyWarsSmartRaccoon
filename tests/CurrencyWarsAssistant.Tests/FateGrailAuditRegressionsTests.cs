using System.Collections.Generic;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 外部 AI 代码审查（《独立识别管线 · 代码审查报告》+ 等价性复核）所提 BUG 的回归测试。
/// 逐条对应：H1（二极管脱节）/ H2（决策未写入商店）/ M1（空引用）/ M2/M3（new() 清零）/
/// L1（祈愿关键词过宽）/ L2（AutoPurchase 恒 3 人）/ L3（最左置位信号缺失）/ 二极管状态失忆 / 多值策略。
/// </summary>
public sealed class FateGrailAuditRegressionsTests
{
    private const string Rin = "远坂凛";
    private const string Gil = "吉尔伽美什";
    private const string Saber = "Saber";

    // ---------- H1：血量需买二极管且 276 可选时，纳入策略 ----------
    [Fact]
    public void H1_Compute_BuyDiodeNeeded_AndDiodeAvailable_ShouldBuyDiode()
    {
        // hp=84 需二极管；276 可选 -> ShouldBuyDiode=true，ChosenStrategyIds 含 276
        var d = FateGrailFlowDecider.Compute(
            currentHp: 84,
            availableStrategyIds: new[]
            {
                FateGrailHealthGate.DiodeInvestmentStrategyId,
                InvestmentStrategyPicker.PurchaseSpecialistGold,
            },
            heldOrOwnedNames: null);

        Assert.True(d.ShouldBuyDiode);
        Assert.Contains(FateGrailHealthGate.DiodeInvestmentStrategyId, d.ChosenStrategyIds);
    }

    [Fact]
    public void H1_Compute_BuyDiodeNeeded_But276Missing_ShouldNotBuyDiode()
    {
        // hp=84 需二极管，但 276 不在可选策略 -> 不应置肩买二极管（无从可买）
        var d = FateGrailFlowDecider.Compute(84, new[] { InvestmentStrategyPicker.PurchaseSpecialistGold }, null);
        Assert.False(d.ShouldBuyDiode);
        Assert.DoesNotContain(FateGrailHealthGate.DiodeInvestmentStrategyId, d.ChosenStrategyIds);
    }

    // ---------- H2：决策写入商店 Options（注入层） ----------
    [Fact]
    public void H2_Injection_WritesChosenStrategyIds_IntoShopOptions()
    {
        var baseOps = new RewardStageAutomationOptions();
        var injected = FateGrailShopOptionsInjection.ApplyStrategySelection(
            baseOps,
            new[] { FateGrailHealthGate.DiodeInvestmentStrategyId,
                    InvestmentStrategyPicker.PurchaseSpecialistColor });

        Assert.Contains(FateGrailHealthGate.DiodeInvestmentStrategyId, injected.PreferredInvestmentStrategyIds);
        Assert.Contains(InvestmentStrategyPicker.PurchaseSpecialistColor, injected.PreferredInvestmentStrategyIds);
    }

    [Fact]
    public void H2_Injection_MergesWithBaseStrategies_NotReplaces()
    {
        // 评审修正：注入应与 base 已有策略合并，空决策时不误清已有配置。
        var baseOps = new RewardStageAutomationOptions
        {
            PreferredInvestmentStrategyIds = new HashSet<string>(
                ["existing_strategy"], System.StringComparer.OrdinalIgnoreCase),
        };
        var injected = FateGrailShopOptionsInjection.ApplyStrategySelection(
            baseOps, strategyIds: null);
        Assert.Contains("existing_strategy", injected.PreferredInvestmentStrategyIds);
    }

    [Fact]
    public async Task H2_Orchestrator_FeedsInjectedStrategies_ToShopRunnerOptions()
    {
        RewardStageAutomationOptions? captured = null;
        var orch = new FateGrailShopOrchestrator(
            shopRunner: (ops, purchase, ct) =>
            {
                captured = ops;
                return Task.FromResult(new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.Complete, [], "ok"));
            },
            preparationRunner: (_, _) => Task.FromResult(true));

        await orch.RunAsync(new FateGrailShopOrchestrator.Input(
            CurrentHp: 84,
            AvailableStrategyIds: new HashSet<string>
            {
                FateGrailHealthGate.DiodeInvestmentStrategyId,
                InvestmentStrategyPicker.PurchaseSpecialistGold,
            },
            HeldOrOwnedNames: null,
            HeldBenchNames: null));

        // H2：编排器把决策的 二极管276 写入实际传给商店执行器的 Options
        Assert.NotNull(captured);
        Assert.Contains(FateGrailHealthGate.DiodeInvestmentStrategyId,
            captured!.PreferredInvestmentStrategyIds);
    }

    // ---------- M1：空引用防御 ----------
    [Fact]
    public void M1_ApplyTo_InitialOwnedWithNullCharacter_DoesNotThrow()
    {
        var source = new RewardStageAutomationOptions
        {
            InitialOwnedCharacters = new[]
            {
                new RecognizedBenchCharacter(0, null!, 0.9d),
            }
        };
        // 不抛异常即通过（此前 item.Character 为 null 会 NRE）
        var applied = FateGrailShoppingPolicy.ApplyTo(source);
        Assert.NotNull(applied);
    }

    // ---------- M2/M3：用真实 Options 而非 new()（传 Base 保留其它字段） ----------
    [Fact]
    public async Task M2_Orchestrator_PreservesBaseShopFields()
    {
        RewardStageAutomationOptions? captured = null;
        var orch = new FateGrailShopOrchestrator(
            shopRunner: (ops, _, _) =>
            {
                captured = ops;
                return Task.FromResult(new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.Complete, [], "ok"));
            },
            preparationRunner: (_, _) => Task.FromResult(true));

        var baseShop = new RewardStageAutomationOptions
        {
            EnableGalaxyScholarRewardStrategy = true,
            FormationCharacterNames = new HashSet<string>(["X"], System.StringComparer.OrdinalIgnoreCase),
        };

        await orch.RunAsync(new FateGrailShopOrchestrator.Input(
            CurrentHp: 88,
            AvailableStrategyIds: null,
            HeldOrOwnedNames: null,
            HeldBenchNames: null,
            BaseShopOptions: baseShop));

        // M2：真实 base 配置的关键非命杯开关应保留（不被 new() 清零）
        Assert.NotNull(captured);
        Assert.True(captured!.EnableGalaxyScholarRewardStrategy);
    }

    // ---------- L1：祈愿关键词收窄，中性"圣杯"文本不误判为高价值 ----------
    [Fact]
    public void L1_PrayTrial_BareStarword_NotHighValue()
    {
        // 含"圣杯"但非高价值短语：不应被 Valuable 命中为高价值
        var result = PrayTrialPreference.Choose(
            "一个普通圣杯相关的试炼",
            "另一个普通试炼");
        // 两侧都非明确高价值、非危险 -> Either（任选）；不应因为"圣杯"就偏右
        Assert.Equal(PrayTrialPreference.TrialChoice.Either, result);
    }

    [Fact]
    public void L1_PrayTrial_MiracleCompensation_IsHighValue()
    {
        var result = PrayTrialPreference.Choose(
            "令人决议·奇迹代偿：扣88血换8完美投影仪",
            "普通试炼");
        // 奇迹代偿为胜利档高价值 -> 选左
        Assert.Equal(PrayTrialPreference.TrialChoice.Left, result);
    }

    // ---------- L2：AutoPurchase 剔除已持有成员 ----------
    [Fact]
    public void L2_BuildAutoPurchase_RemovesAlreadyHeld()
    {
        // 已持有 2 名命杯成员 -> 只应剩 1 名需购买
        var auto = FateGrailShoppingPolicy.BuildAutoPurchaseNames([Rin, Gil]);
        Assert.Single(auto);
        Assert.Contains(Saber, auto);
        Assert.DoesNotContain(Rin, auto);
        Assert.DoesNotContain(Gil, auto);
    }

    // ---------- L3：采购专员最左置位信号 ----------
    [Fact]
    public void L3_Compute_PurchaseSpecialist_ProducesLeftSlotTarget()
    {
        // 已持有凛；缺 3 费 Saber；策略有金051 -> 采购专员命中 -> 目标最左角色 = 仍缺的最高费商店成员 Saber
        var d = FateGrailFlowDecider.Compute(
            88,
            new[] { InvestmentStrategyPicker.PurchaseSpecialistGold },
            heldOrOwnedNames: new[] { Rin });

        Assert.Equal(InvestmentStrategyPicker.PurchaseSpecialistGold, d.ChosenPurchaseSpecialist);
        Assert.Equal(Saber, d.TargetLeftSlotCost);
    }

    [Fact]
    public void L3_Compute_NoSpecialist_NoLeftSlotTarget()
    {
        var d = FateGrailFlowDecider.Compute(88, null, null);
        Assert.Null(d.TargetLeftSlotCost);
    }

    // ---------- 二极管状态跟踪（评审 H1 子项） ----------
    [Fact]
    public void HealthGate_IsMiracleClickable_AppliesDiodeBonus()
    {
        // 80 血 + 已吃二极管 +10 = 90 >= 88 -> 可点
        Assert.True(FateGrailHealthGate.IsMiracleCompensationClickable(80, diodeAlreadyApplied: true));
        // 未吃二极管 80 < 88 -> 不可点
        Assert.False(FateGrailHealthGate.IsMiracleCompensationClickable(80, diodeAlreadyApplied: false));
    }

    // ---------- FlowDecider 默认不再强制 3 人（L2 贯通到编排门面） ----------
    [Fact]
    public void FlowDecider_HeldAllThree_ShrinksAutoPurchase()
    {
        var d = FateGrailFlowDecider.Compute(88, null, new[] { Rin, Gil, Saber });
        Assert.Empty(d.AutoPurchaseNames);
    }
}
