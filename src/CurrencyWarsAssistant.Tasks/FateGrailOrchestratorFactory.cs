using System;
using System.Threading;
using System.Threading.Tasks;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 把「真实商店执行器」组装进 <see cref="FateGrailShopOrchestrator"/> 的接线工厂。
/// <para>
/// 决策辅助层（商店策略/血量/编排）是纯逻辑、不触屏；真实点屏由
/// <see cref="RewardStageAutomationController"/> 负责。本工厂把两者接上：
/// <code>
///   var orch = FateGrailOrchestratorFactory.Create(rewardStageController, windowHandle);
///   var result = await orch.RunAsync(input, ct);   // 会真实进商店买命杯成员
/// </code>
/// </para>
/// 注意：<paramref name="windowHandle"/> 是真实游戏窗口句柄（实机时由上层每局传入）。
/// 备战补员执行器可另行提供；为 null 时编排器用默认"不补员"占位（见 <see cref="FateGrailShopOrchestrator"/>）。
/// </summary>
public static class FateGrailOrchestratorFactory
{
    /// <summary>
    /// 用真实商店控制器与窗口句柄组装编排器；商店执行器走 <see cref="FateGrailRealShopRunner"/>。
    /// </summary>
    public static FateGrailShopOrchestrator Create(
        RewardStageAutomationController rewardStageController,
        nint windowHandle,
        Func<PreparationBoardOptions,
            CancellationToken,
            Task<bool>>? preparationRunner = null) =>
        new(
            FateGrailRealShopRunner.For(
                controller: rewardStageController,
                windowHandle: windowHandle),
            preparationRunner);
}
