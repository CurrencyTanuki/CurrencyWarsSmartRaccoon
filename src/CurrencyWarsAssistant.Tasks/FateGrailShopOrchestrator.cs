using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」商店阶段的编排宿主（可注入执行器，可独立单测）。
/// <para>
/// 把已就绪的 6 块决策辅助（含 <see cref="FateGrailFlowDecider"/>）编排成一个顺序流程：
/// 血量判定 → 商店购买名单/禁卖名单 → 商店刷新购买循环 → 备战补员禁卖。
/// 真实点屏操作通过注入的 <paramref name="shopRunner"/> / <paramref name="preparationRunner"/>
/// 委托执行（生产用真实 RewardStageAutomation/备战控制器；测试用 fake 捕获调用），
/// 本编排器自身<b>不触碰屏幕、不读实时状态</b>，因此可脱离游戏做单元测试。
/// </para>
/// </summary>
public sealed class FateGrailShopOrchestrator
{
    /// <summary>本局输入状态。</summary>
    public sealed record Input(
        int CurrentHp,
        IReadOnlySet<string>? AvailableStrategyIds,
        IReadOnlyCollection<string>? HeldOrOwnedNames,
        IReadOnlyCollection<string>? HeldBenchNames,
        // M2/M3 修正：传入真实商店/备战 Options 作为基础，仅覆盖命杯语义字段，不再用 new() 清零。
        RewardStageAutomationOptions? BaseShopOptions = null,
        PreparationBoardOptions? BasePreparationOptions = null);

    /// <summary>编排结果。</summary>
    public sealed record Result(
        FateGrailHealthGate.HealthDecision HealthDecision,
        string? ChosenPurchaseSpecialist,
        IReadOnlySet<string> ChosenStrategyIds,
        bool ShouldBuyDiode,
        string? TargetLeftSlotCost,
        bool ShopLoopRan,
        ShopRefreshPurchaseLoopStatus? FinalShopStatus,
        bool PreparationCompleted);

    private readonly Func<RewardStageAutomationOptions,
        IReadOnlySet<string>,
        CancellationToken,
        Task<ShopRefreshPurchaseLoopResult>> _shopRunner;
    private readonly Func<PreparationBoardOptions,
        CancellationToken,
        Task<bool>> _preparationRunner;

    /// <summary>
    /// 构造编排器。<paramref name="shopRunner"/> 收到 [商店 Options(含 AutoPurchase/Retained)] 与
    /// [采购名单]，它应据此执行"识别→买→刷新→重扫"并返回状态；<paramref name="preparationRunner"/>
    /// 收到 [备战补员 Options(含 Retained/RequiredRetained)] 并返回是否补员成功。
    /// 两者均可为 null（仅做决策编排、不执行操作），此时对应步骤以"跳过"记录。
    /// </summary>
    public FateGrailShopOrchestrator(
        Func<RewardStageAutomationOptions,
            IReadOnlySet<string>,
            CancellationToken,
            Task<ShopRefreshPurchaseLoopResult>>? shopRunner,
        Func<PreparationBoardOptions,
            CancellationToken,
            Task<bool>>? preparationRunner)
    {
        _shopRunner = shopRunner ?? ((_, _, _) => Task.FromResult(
            new ShopRefreshPurchaseLoopResult(
                ShopRefreshPurchaseLoopStatus.Cancelled,
                [],
                "商店执行器未注入，跳过商店。"))) ;
        _preparationRunner = preparationRunner ?? ((_, _) => Task.FromResult(false));
    }

    /// <summary>
    /// 执行一次商店阶段编排：血量门限 → 商店购买名单 → 商店循环 → 备战补员禁卖。
    /// </summary>
    public async Task<Result> RunAsync(
        Input input,
        CancellationToken cancellationToken = default)
    {
        var decision = FateGrailFlowDecider.Compute(
            input.CurrentHp,
            input.AvailableStrategyIds,
            input.HeldOrOwnedNames ?? []);

        // 商店 Options：m2 修正——以真实 BaseShopOptions 为基础（M2），仅覆盖命杯语义字段；
        //    h2 修正——把本局阈值决策写回 shopOps（PreferredInvestmentStrategyIds / SelectedInvestmentEnvironmentId）。
        var baseShop = input.BaseShopOptions ?? new RewardStageAutomationOptions();
        var shopOps = FateGrailShoppingPolicy.ApplyTo(
            baseShop,
            input.HeldOrOwnedNames ?? []);
        // H2：把 二极管276 + 采购专员051/238（决策层的 ChosenStrategyIds）写入真实商店配置，
        //      使计算出的策略不是只进 Result 上报，而是真正影响商店执行。
        shopOps = FateGrailShopOptionsInjection.ApplyStrategySelection(
            shopOps,
            decision.ChosenStrategyIds,
            selectedEnvironmentId: null);

        var autoNames = decision.AutoPurchaseNames ?? new HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase);
        var shopLoopInvoked = autoNames.Count > 0;
        ShopRefreshPurchaseLoopResult shopOutcome =
            new(ShopRefreshPurchaseLoopStatus.Cancelled, [], "未执行");
        if (shopLoopInvoked)
        {
            shopOutcome = await _shopRunner(
                shopOps,
                autoNames,
                cancellationToken);
        }

        // 备战补员：m3 修正——同样以真实 BasePreparationOptions 为基础，不再 new() 清零。
        var basePrep = input.BasePreparationOptions ?? new PreparationBoardOptions();
        var prepOps = FateGrailPreparationPolicy.ApplyRetained(
            basePrep,
            input.HeldBenchNames ?? []);
        var prepCompleted = await _preparationRunner(
            prepOps,
            cancellationToken);

        return new Result(
            decision.Health,
            decision.ChosenPurchaseSpecialist,
            decision.ChosenStrategyIds,
            decision.ShouldBuyDiode,
            decision.TargetLeftSlotCost,
            ShopLoopRan: shopLoopInvoked,
            FinalShopStatus: shopOutcome.Status,
            PreparationCompleted: prepCompleted);
    }
}
