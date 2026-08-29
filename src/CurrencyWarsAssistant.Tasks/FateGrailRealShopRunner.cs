using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 为 <see cref="FateGrailShopOrchestrator"/> 提供<b>真实商店执行器</b>（适配实际点屏控制器）。
/// <para>
/// 把 <see cref="RewardStageAutomationController.RunShopRefreshPurchaseLoopAsync"/>
/// 包装成编排器所要求的 <c>Func&lt;RewardStageAutomationOptions, IReadOnlySet&lt;string&gt;, CancellationToken, Task&lt;ShopRefreshPurchaseLoopResult&gt;&gt;</c>。
/// 捕获固定窗口句柄与本局的 <see cref="RewardStageAutomationController"/> 实例。
/// <paramref name="readRemainingGold"/> 传 null 表示"不判断剩余金币"，走插件缺省（买齐/金币&lt;刷新费2 由插件内部判断）。
/// </para>
/// 注意：本适配器<b>不 resolve 控制器实例</b>（由上层 DI/调用方按局构造并传入），
/// 仅做“实例+窗口句柄 → 编排器委托”的粘合；也不接任何既有主流程（仍由调用方决定何时调用）。
/// </summary>
public static class FateGrailRealShopRunner
{
    /// <summary>
    /// 返回一条商店执行器委托：收到 [商店 Options] 与 [采购名单] 时，
    /// 调用生产控制器的刷新购买循环（每槽名单内点一次买→刷新→重扫），返回最终状态。
    /// </summary>
    public static Func<RewardStageAutomationOptions,
        IReadOnlySet<string>,
        CancellationToken,
        Task<ShopRefreshPurchaseLoopResult>> For(
            RewardStageAutomationController controller,
            nint windowHandle,
            Func<CaptureFrame, CancellationToken, System.Threading.Tasks.Task<int?>>? readRemainingGold = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (windowHandle == 0)
            throw new ArgumentException(
                "windowHandle 不能为 0（需真实游戏窗口句柄）。", nameof(windowHandle));
        return (ops, purchase, ct) =>
            controller.RunShopRefreshPurchaseLoopAsync(
                windowHandle,
                ops,
                readRemainingGold,
                ct,
                purchase);
    }
}
