using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」生产执行器（<see cref="FateGrailRunCoordinator.IActionExecutor"/> 的
/// 真实点屏实现，隔离新增，不碰主识别管线）。
/// <para>
/// 每个决策动作映射到真实鼠标操作：
/// <list type="bullet">
///   <item><see cref="FateGrailRunEngine.Action.BuyDiode"/> → 投资策略页挑二极管276并确认（三连刷兜底）。</item>
///   <item><see cref="FateGrailRunEngine.Action.BuyStoreMembers"/> → 商店刷新购买循环买命杯成员（凛/闪/Saber）。</item>
///   <item><see cref="FateGrailRunEngine.Action.ChoosePassiveTrial"/> / <see cref="FateGrailRunEngine.Action.ChooseMiracleCompensation"/>
///     → 祈愿试炼弹框点<see cref="FateGrailRunEngine.FateGrailStep.TrialToChoose"/>对应侧并确认。</item>
///   <item><see cref="FateGrailRunEngine.Action.Ensure5CostBody"/> → J1：等待祈愿试炼补 5 费本体。
///     1-3 商店 5 费概率为 0%（等级未到 7），不做商店刷买；真正来源是祈愿试炼的
///     5 费聘用书/登场Archer 与环境 067 英雄登场（决策层继续推进祈愿即可）。
///     连续等待超上限仍无本体时返回 false（交外层重刷），避免空转到 maxDecisions。</item>
///   <item><see cref="FateGrailRunEngine.Action.None"/> → 无动作（成功）。</item>
/// </list>
/// 执行失败（策略找不到/商店实际未买成/关键路径试炼未识别/本体久等不至）返回 false，
/// 协调器转为 Stopped 交外层重刷/重试。
/// </para>
/// </summary>
public sealed class FateGrailProductionExecutor : FateGrailRunCoordinator.IActionExecutor
{
    private readonly RewardStageAutomationController _rewardController;
    private readonly WishTrialSelectionAutomation _trialSelection;
    private readonly Func<
        Func<RewardStageAutomationOptions,
            IReadOnlySet<string>,
            CancellationToken,
            Task<ShopRefreshPurchaseLoopResult>>,
        Func<PreparationBoardOptions, CancellationToken, Task<bool>>,
        FateGrailShopOrchestrator> _shopOrchestratorFactory;
    private readonly nint _windowHandle;
    private readonly RewardStageAutomationOptions _baseShopOptions;
    private readonly PreparationBoardOptions _basePreparationOptions;

    /// <summary>J1 等待祈愿补本体的连续步数上限（超过则判失败交重刷，避免有界空转到 maxDecisions）。</summary>
    private const int MaxConsecutiveBodyWaits = 30;

    /// <summary>J1 已连续等待本体的步数（任一其它动作后清零）。</summary>
    private int _consecutiveBodyWaits;

    /// <summary>
    /// 构造生产执行器。
    /// </summary>
    /// <param name="rewardController">奖励关/策略/商店刷新滚动控制器（DI 单例/transient 均可）。</param>
    /// <param name="trialSelection">祈愿试炼弹框识别+点选模块（DI 单例）。</param>
    /// <param name="shopOrchestratorFactory">商店编排器工厂；收到商店执行器与备战执行器返回编排器。
    /// 传 null 时用 <see cref="FateGrailOrchestratorFactory.Create"/> 默认实现（真实商店执行器）。</param>
    /// <param name="windowHandle">真实游戏窗口句柄（实机时由上层每局传入）。</param>
    /// <param name="baseShopOptions">本局真实商店基础配置（含用户禁卖/保护项）。传 null 用默认配置；
    /// 命杯语义字段（购买/禁卖名单、首选策略）由编排器覆盖，其余字段按本配置透传，避免被清零。</param>
    /// <param name="basePreparationOptions">本局真实备战基础配置。传 null 用默认防卖配置</param>
    public FateGrailProductionExecutor(
        RewardStageAutomationController rewardController,
        WishTrialSelectionAutomation trialSelection,
        nint windowHandle,
        Func<
            Func<RewardStageAutomationOptions,
                IReadOnlySet<string>,
                CancellationToken,
                Task<ShopRefreshPurchaseLoopResult>>,
            Func<PreparationBoardOptions, CancellationToken, Task<bool>>,
            FateGrailShopOrchestrator>? shopOrchestratorFactory = null,
        RewardStageAutomationOptions? baseShopOptions = null,
        PreparationBoardOptions? basePreparationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(rewardController);
        ArgumentNullException.ThrowIfNull(trialSelection);
        if (windowHandle == 0)
        {
            throw new ArgumentException(
                "windowHandle 不能为 0（需真实游戏窗口句柄）。", nameof(windowHandle));
        }

        _rewardController = rewardController;
        _trialSelection = trialSelection;
        _windowHandle = windowHandle;
        _shopOrchestratorFactory = shopOrchestratorFactory
            ?? ((shopRunner, prepRunner) =>
                new FateGrailShopOrchestrator(shopRunner, prepRunner));
        _baseShopOptions = baseShopOptions ?? new RewardStageAutomationOptions();
        // 未注入真实备战配置时，至少保护备战席首槽（常放采购专员最左置位的目标角色），杜绝误卖。
        _basePreparationOptions = basePreparationOptions
            ?? new PreparationBoardOptions { ProtectFirstBenchSlotFromSale = true };
    }

    public async Task<bool> ExecuteAsync(
        FateGrailRunEngine.Action action,
        string stepNode,
        string message,
        FateGrailRunEngine.TrialSide? trialToChoose,
        CancellationToken cancellationToken)
    {
        // J1 等待计数：只在连续发 Ensure5CostBody 时累加，任一其它动作说明外部状态有推进 -> 清零。
        if (action != FateGrailRunEngine.Action.Ensure5CostBody)
            _consecutiveBodyWaits = 0;

        switch (action)
        {
            case FateGrailRunEngine.Action.BuyDiode:
                return await BuyDiodeAsync(cancellationToken);

            case FateGrailRunEngine.Action.BuyStoreMembers:
                return await BuyStoreMembersAsync(cancellationToken);

            case FateGrailRunEngine.Action.ChoosePassiveTrial:
            case FateGrailRunEngine.Action.ChooseMiracleCompensation:
                return await ChooseTrialSideAsync(
                    action,
                    stepNode,
                    trialToChoose,
                    cancellationToken);

            case FateGrailRunEngine.Action.Ensure5CostBody:
                return await EnsureFiveCostBodyAsync(cancellationToken);

            case FateGrailRunEngine.Action.BuyPopulation:
                return await BuyPopulationAsync(cancellationToken);

            case FateGrailRunEngine.Action.None:
                return true;

            default:
                // 决策层不该发出其它动作（Reroll/Achieved 由协调器处理）；
                // 出现未知动作视为执行失败，避免盲跑。
                return false;
        }
    }

    /// <summary>买「二极管276」策略补血：策略页挑276并确认；找不到返回 false（交外层重刷）。</summary>
    private async Task<bool> BuyDiodeAsync(CancellationToken cancellationToken)
    {
        var result = await _rewardController.TrySelectInvestmentStrategyAsync(
            _windowHandle,
            new HashSet<string>(
                new[] { FateGrailHealthGate.DiodeInvestmentStrategyId },
                StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        return result.Succeeded;
    }

    /// <summary>
    /// 买商店命杯成员（凛/闪/Saber）：走商店刷新购买循环。
    /// 成功判定基于商店循环的 <b>最终状态</b>（FinalShopStatus），而非“是否下发过购买指令”
    /// （ShopLoopRan 只代表下发了指令，购买实际失败也会为 true，不能当成功信号）。
    /// </summary>
    private async Task<bool> BuyStoreMembersAsync(CancellationToken cancellationToken)
    {
        var shopRunner = FateGrailRealShopRunner.For(
            _rewardController,
            _windowHandle);
        var orchestrator = _shopOrchestratorFactory(
            shopRunner,
            (_, _) => Task.FromResult(true));
        var result = await orchestrator.RunAsync(
            new FateGrailShopOrchestrator.Input(
                CurrentHp: 0,                    // 血量门限由决策层步进处理，编排器内不重复判
                AvailableStrategyIds: null,
                HeldOrOwnedNames: null,
                HeldBenchNames: null,
                // 传入本局真实基础配置（未注入时为防卖默认），命杯语义字段由编排器覆盖，
                // 其余用户配置不再被 new() 清零。
                BaseShopOptions: _baseShopOptions,
                BasePreparationOptions: _basePreparationOptions),
            cancellationToken);
        return result.FinalShopStatus == ShopRefreshPurchaseLoopStatus.Complete;
    }

    /// <summary>
    /// 祈愿试炼弹框：点 <see cref="FateGrailRunEngine.FateGrailStep.TrialToChoose"/> 对应侧并确认。
    /// <para>
    /// 成功信号失真防护：<b>关键路径</b>（N1 点奇迹代偿、P 点无限之釜——都是 Done 收工步）
    /// 必须真实确认选中（Confirmed）；弹框未被识别（NotDetected）时返回 false 交外层重试/重刷，
    /// 不能当成功——否则协调器会虚报 Achieved。一般试炼（M1/M2/N2 的 ChoosePassiveTrial）
    /// 容错：NotDetected 视为已处理，下一帧快照若试炼仍在会再次步进。
    /// </para>
    /// </summary>
    private async Task<bool> ChooseTrialSideAsync(
        FateGrailRunEngine.Action action,
        string stepNode,
        FateGrailRunEngine.TrialSide? trialToChoose,
        CancellationToken cancellationToken)
    {
        var side = trialToChoose ?? FateGrailRunEngine.TrialSide.Left;
        var status = await _trialSelection.TryHandleSelectionAsync(
            _windowHandle,
            cancellationToken,
            (left, right) => (int)ToSelectSide(side));
        if (status == WishTrialSelectionStatus.Confirmed)
            return true;
        // 关键路径：N1（奇迹代偿）与 P（无限之釜）均为收工步，未识别不能当成功。
        var critical = action == FateGrailRunEngine.Action.ChooseMiracleCompensation
            || stepNode == "P";
        if (critical)
            return false;
        return status == WishTrialSelectionStatus.NotDetected;
    }

    /// <summary>侧 → WishTrialSelectionAutomation 的 select 返回值（0=左，1=右；与模块约定一致）。</summary>
    internal static int ToSelectSide(FateGrailRunEngine.TrialSide side) =>
        side == FateGrailRunEngine.TrialSide.Right ? 1 : 0;

    /// <summary>
    /// J1 补救（用户 2026-08-25 澄清后的正确语义）：不在此处刷商店买 5 费——
    /// 1-3 商店 5 费概率为 0%（等级未到 7），刷商店买 5 费必然空耗金币死循环。
    /// 5 费本体的正确来源是：祈愿试炼的「5 费聘用书 / 登场 Archer」与
    /// 环境 067「英雄登场」白送 2 星 5 费——这些都由决策层继续推进祈愿/试炼获得，
    /// 识别层反馈阵容后自动判定 HasBody5Cost。执行器在此只确认"等待推进"，
    /// 不做商店操作（避免烧金币）；后续轮次由祈愿试炼择优自然带出本体。
    /// </summary>
    /// <summary>买经验升人口：点商店等级识别框两次升 1 人口（激活 5 圣杯）。</summary>
    private Task<bool> BuyPopulationAsync(CancellationToken cancellationToken) =>
        _rewardController.BuyPopulationAsync(_windowHandle, cancellationToken);

    private Task<bool> EnsureFiveCostBodyAsync(CancellationToken cancellationToken)
    {
        // 等待祈愿试炼补本体（登场Archer/5费聘书/英雄登场）；无商店操作。
        // 有界等待：连续多步仍无本体（识别反馈未变化）时返回 false，交协调器 Stopped→外层重刷，
        // 避免空转到 maxDecisions 才退出。判负即清零，避免计数跨局残留污染下一局首步。
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(false);
        _consecutiveBodyWaits++;
        var withinLimit = _consecutiveBodyWaits <= MaxConsecutiveBodyWaits;
        if (!withinLimit)
            _consecutiveBodyWaits = 0;
        return Task.FromResult(withinLimit);
    }
}