using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」整局循环的运行时组装工厂（隔离新增）。
/// <para>
/// <see cref="FateGrailRunLoop"/> 依赖<b>运行时窗口句柄</b>（每局动态），不能靠 DI 无参构造；
/// 因此按钮点击「刷三星五费」时在 App 层解析好服务传参给 <see cref="Create"/>，现场组装一个
/// <see cref="FateGrailRunLoop"/> 实例，再 <code>RunAsync</code>。
/// </para>
/// </summary>
public static class FateGrailRunLoopFactory
{
    /// <summary>
    /// 用已解析好的服务现场组装一个「三星五费」整局循环。
    /// </summary>
    /// <param name="rewardController">奖励关/策略/商店控制器（App 层解析 RewardStageAutomationController）。</param>
    /// <param name="trialSelection">祈愿试炼识别+点选模块。</param>
    /// <param name="gameData">游戏数据目录。</param>
    /// <param name="liveService">实时对战记录服务（订阅其 Updated 拿最新识别结果）。</param>
    /// <param name="openingCoordinator">开局重刷循环器。</param>
    /// <param name="gameWindow">真实游戏窗口（含句柄与客户区尺寸，录屏用它）。</param>
    /// <param name="goal">用户目标（A 单只 / B 全员）。</param>
    /// <param name="recording">可选滚动录屏配置（null=不录屏）。</param>
    public static (FateGrailRunLoop Loop, FateGrailLiveSnapshotSource SnapshotSource)
        Create(
            RewardStageAutomationController rewardController,
            WishTrialSelectionAutomation trialSelection,
            GameDataCatalog gameData,
            IPhase2LiveCollectionService liveService,
            OpeningRerollLoopCoordinator openingCoordinator,
            GameWindowInfo gameWindow,
            FateGrailRunEngine.UserGoal goal,
            FateGrailRecordingOptions? recording = null)
    {
        ArgumentNullException.ThrowIfNull(rewardController);
        ArgumentNullException.ThrowIfNull(trialSelection);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(liveService);
        ArgumentNullException.ThrowIfNull(openingCoordinator);
        ArgumentNullException.ThrowIfNull(gameWindow);
        if (gameWindow.Handle == 0)
        {
            throw new ArgumentException(
                "gameWindow.Handle 不能为 0（需真实游戏窗口句柄）。", nameof(gameWindow));
        }

        // ① 实时快照源（订阅实时识别结果）。
        var snapshotSource = new FateGrailLiveSnapshotSource(liveService, autoSubscribe: true);

        // ② 决策协调器：readSnapshot 委托从快照源组装。
        var readSnapshot = FateGrailLiveSnapshotAssembler.CreateReadSnapshot(
            snapshotSource,
            gameData,
            trialSelection,
            goal);

        // ③ 生产执行器（真实点屏）。
        var executor = new FateGrailProductionExecutor(
            rewardController,
            trialSelection,
            gameWindow.Handle);

        var coordinator = new FateGrailRunCoordinator(
            readSnapshot,
            executor);

        // ⑤ 可选滚动录屏：每局临时文件，成功保留 / 失败删除。
        FateGrailRunLoop.IRoundRecorder? roundRecorder = null;
        string? recordingOutputDirectory = null;
        if (recording is not null)
        {
            roundRecorder = new FateGrailRollingRecorder(
                recording.Capture,
                gameWindow,
                recording.Quality,
                recording.FfmpegPath,
                recording.TempDirectory);
            recordingOutputDirectory = recording.OutputDirectory;
        }

        // ④ 整局循环：开局重刷（复用 OpeningRerollLoopCoordinator）→ 决策驱动 → 达成/重开。
        var loop = new FateGrailRunLoop(
            (filters, options, ct) => openingCoordinator.RunAsync(
                gameWindow.Handle,
                filters,
                options,
                ct),
            coordinator)
        {
            RoundRecorder = roundRecorder,
            RecordingOutputDirectory = recordingOutputDirectory,
        };

        return (loop, snapshotSource);
    }

    /// <summary>构造三星五费可接受的开局过滤器（只收 067/019 两个可推进环境，D1；018 契约已剔除）。
    /// 语义：任一 Require 环境命中即满足（OpeningFilterEvaluator 实现为 offered.Length&gt;0）。</summary>
    public static OpeningFilterSet BuildViableEnvironmentFilter()
    {
        return new OpeningFilterSet
        {
            InvestmentEnvironments = new OpeningItemFilter[]
            {
                NewRequire(FateGrailRunEngine.EnvironmentHeroArrival, "英雄登场"),
                NewRequire(FateGrailRunEngine.EnvironmentInvitation, "命运圣杯邀请"),
            },
        };
    }

    private static OpeningItemFilter NewRequire(string id, string displayName) =>
        new(id, displayName, OpeningFilterState.Require);
}