using System;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」生产 <c>readSnapshot</c> 数据源（隔离新增，只读订阅，不改识别管线）。
/// <para>
/// 订阅 <see cref="IPhase2LiveCollectionService.Updated"/>，把最近一次识别结果
/// （<see cref="ScreenshotAnalysisResult.OperationalState"/> 阵容/血量/环境/策略 +
/// <see cref="ScreenshotAnalysisResult.Snapshot"/> 里 <c>Economy</c> 金币）保存在内存，
/// 供 <see cref="FateGrailRunCoordinator"/> 的 readSnapshot 委托在组装
/// <see cref="FateGrailRunEngine.Snapshot"/> 时读取。
/// </para>
/// <para>装配（后续接线时）：
/// <code>
///   var source = new FateGrailLiveSnapshotSource(liveService);
///   // coordinator 的 readSnapshot 委托：
///   //   source.TryGetLatest(...) → FateGrailSnapshotAssembler.Assemble(...)
/// </code></para>
/// </summary>
public sealed class FateGrailLiveSnapshotSource
{
    private readonly object _gate = new();
    private ScreenshotAnalysisResult? _latest;
    private bool _subscribed;

    /// <summary>构造并（可选）立即订阅 live collection 的更新事件。</summary>
    /// <param name="liveService">实时对战记录服务；其 Updated 事件携带最新识别结果。</param>
    /// <param name="autoSubscribe">为 true 时构造即订阅，false 时需手动调用
    /// <see cref="Subscribe"/>（便于在「刷三星五费」开始后才订阅，避免干扰正常记录）。</param>
    public FateGrailLiveSnapshotSource(
        IPhase2LiveCollectionService liveService,
        bool autoSubscribe = false)
    {
        ArgumentNullException.ThrowIfNull(liveService);
        LiveService = liveService;
        if (autoSubscribe)
        {
            Subscribe();
        }
    }

    /// <summary>订阅的实时记录服务。</summary>
    public IPhase2LiveCollectionService LiveService { get; }

    /// <summary>最近一次识别结果（null=尚未收到任何更新）。</summary>
    public ScreenshotAnalysisResult? Latest
    {
        get { lock (_gate) { return _latest; } }
    }

    /// <summary>开始订阅（幂等）。一般在「刷三星五费」开始时调用。</summary>
    public void Subscribe()
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            LiveService.Updated += OnUpdated;
        }
    }

    /// <summary>停止订阅并清空缓存（运行时/结束时调用）。</summary>
    public void Unsubscribe()
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                return;
            }

            LiveService.Updated -= OnUpdated;
            _latest = null;
            _subscribed = false;
        }
    }

    private void OnUpdated(object? sender, LiveCollectionUpdate update)
    {
        if (update.Analysis is null)
        {
            return;
        }

        lock (_gate) { _latest = update.Analysis; }
    }
}

/// <summary>
/// 生产 readSnapshot 装配器：从 <see cref="FateGrailLiveSnapshotSource.Latest"/> 组装
/// <see cref="FateGrailRunEngine.Snapshot"/>（给协调器用）。
/// <para>这是「识别 → 决策」的生产接线（用户 2026-08-25 授权接入）。</para>
/// </summary>
public static class FateGrailLiveSnapshotAssembler
{
    /// <summary>
    /// 返回协调器可用的 readSnapshot 委托。
    /// </summary>
    /// <param name="source">实时快照源（保持订阅中）。</param>
    /// <param name="gameData">游戏数据目录。</param>
    /// <param name="trialSelection">祈愿试炼识别模块（读最新左右试炼名）。</param>
    /// <param name="goal">用户目标（A 单只 / B 全员）。</param>
    /// <param name="internalState">整局内部状态（二极管/试炼计数/本体失败）由调用方维护并随次传入。</param>
    public static Func<CancellationToken, Task<FateGrailRunEngine.Snapshot?>>
        CreateReadSnapshot(
            FateGrailLiveSnapshotSource source,
            GameDataCatalog gameData,
            WishTrialSelectionAutomation trialSelection,
            FateGrailRunEngine.UserGoal goal,
            FateGrailRunEngine.InternalState? internalState = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(trialSelection);

        return _ =>
        {
            var analysis = source.Latest;
            if (analysis?.OperationalState is not { } state)
            {
                return Task.FromResult<FateGrailRunEngine.Snapshot?>(null);
            }

            var trial = trialSelection.LatestTrialInfo;
            var snapshot = FateGrailSnapshotAssembler.Assemble(
                state,
                gameData,
                goal,
                gold: analysis.Snapshot?.Economy?.Value ?? 0,
                leftTrial: trial?.LeftName,
                rightTrial: trial?.RightName,
                trialOneTaken: internalState?.TrialOneTaken ?? false,
                trialTwoTaken: internalState?.TrialTwoTaken ?? false,
                diodeTaken: internalState?.DiodeTaken ?? false,
                bodyAcquisitionFailed: internalState?.BodyAcquisitionFailed ?? false,
                hasStarBadge: internalState?.HasStarBadge ?? false,
                population: state.Population.Status == ObservationStatus.Known
                    ? state.Population.Value
                    : 0);
            return Task.FromResult<FateGrailRunEngine.Snapshot?>(snapshot);
        };
    }
}