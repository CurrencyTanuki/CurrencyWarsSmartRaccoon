using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」商店编排器单测（fake 执行器捕获调用）。</summary>
public sealed class FateGrailShopOrchestratorTests
{
    private const string Rin = "远坂凛";

    [Fact]
    public async Task Run_FeedsDecisionAutoPurchase_ToShopRunner()
    {
        IReadOnlySet<string>? capturedPurchase = null;
        var orch = new FateGrailShopOrchestrator(
            shopRunner: (ops, purchase, ct) =>
            {
                capturedPurchase = purchase;
                return Task.FromResult(new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.Complete, [], "ok"));
            },
            preparationRunner: (prep, ct) => Task.FromResult(true));

        var r = await orch.RunAsync(new FateGrailShopOrchestrator.Input(
            CurrentHp: 84,
            AvailableStrategyIds: new HashSet<string>(["investment_strategy_051"]),
            HeldOrOwnedNames: null,
            HeldBenchNames: null));

        // 商店名单 = 命杯 1/2/3 费
        Assert.NotNull(capturedPurchase);
        Assert.Contains(Rin, capturedPurchase!);
        Assert.Equal(ShopRefreshPurchaseLoopStatus.Complete, r.FinalShopStatus);
        Assert.Equal(InvestmentStrategyPicker.PurchaseSpecialistGold,
            r.ChosenPurchaseSpecialist);
        Assert.Equal(FateGrailHealthGate.HealthDecision.BuyDiodeAndProceed,
            r.HealthDecision);
    }

    [Fact]
    public async Task Run_PreparationGetsGrailRetained_AndCompletes()
    {
        PreparationBoardOptions? captured = null;
        var orch = new FateGrailShopOrchestrator(
            null,
            preparationRunner: (prep, ct) =>
            {
                captured = prep;
                return Task.FromResult(true);
            });

        var r = await orch.RunAsync(new FateGrailShopOrchestrator.Input(
            88, null, null, HeldBenchNames: new[] { Rin }));

        Assert.NotNull(captured);
        Assert.Contains(Rin, captured!.RetainedCharacterNames);
        Assert.Contains(Rin, captured.RequiredRetainedCharacterNames);
        Assert.True(r.PreparationCompleted);
    }

    [Fact]
    public async Task Run_NoExecutors_SkipsShopAndPrep_StillReportsDecision()
    {
        var orch = new FateGrailShopOrchestrator(null, null);
        var r = await orch.RunAsync(new FateGrailShopOrchestrator.Input(
            60, null, null, null));

        // 血量：60 -> 奇迹代偿不可行
        Assert.Equal(
            FateGrailHealthGate.HealthDecision.MiracleLineUnavailable,
            r.HealthDecision);
        // 无执行器 -> 商店循环以 Cancelled 记录、备战补员完成=false
        Assert.Equal(ShopRefreshPurchaseLoopStatus.Cancelled, r.FinalShopStatus);
        Assert.False(r.PreparationCompleted);
    }

    [Fact]
    public async Task Run_NullHeldBench_StillInvokesPreparation()
    {
        var prepCalls = 0;
        var orch = new FateGrailShopOrchestrator(
            null,
            preparationRunner: (_, _) =>
            {
                prepCalls++;
                return Task.FromResult(true);
            });

        await orch.RunAsync(new FateGrailShopOrchestrator.Input(
            88, null, null, HeldBenchNames: null));

        // null 入场时备战执行器仍被调用（null 被防御成空集合）
        Assert.Equal(1, prepCalls);
    }

    [Fact]
    public async Task Run_ShopInvoked_OnlyWhenAutoPurchaseNonEmpty()
    {
        var shopCalls = 0;
        var orch = new FateGrailShopOrchestrator(
            shopRunner: (_, _, _) =>
            {
                shopCalls++;
                return Task.FromResult(new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.Cancelled, [], "cancelled"));
            },
            preparationRunner: (_, _) => Task.FromResult(true));

        var r = await orch.RunAsync(new FateGrailShopOrchestrator.Input(
            88, null, null, null));

        // AutoPurchase 恒有三名命杯 -> 商店执行器被调用；ShopLoopRan 反映该次调用
        Assert.Equal(1, shopCalls);
        Assert.True(r.ShopLoopRan);
    }
}
