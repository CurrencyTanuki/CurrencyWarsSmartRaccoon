using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

public sealed class RewardStageAutomationOptions
{
    public bool EnableEarlyStrongFormationPurchase { get; init; }
    public bool EnableGalaxyScholarRewardStrategy { get; init; }
    public IReadOnlyList<RecognizedBenchCharacter> InitialOwnedCharacters
        { get; init; } = [];
    public IReadOnlySet<string> AutoPurchaseCharacterNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> RetainedCharacterNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> FormationCharacterNames { get; init; } =
        InitialRewardFormationPlanner.DefaultEligibleCharacterNames;
    public IReadOnlyList<PreparationPlacement> InitialFormationPlacements
        { get; init; } = [];
    public PreparationBoardOptions PreparationCompletionOptions { get; init; } =
        new();
    public IReadOnlySet<string> PreferredInvestmentStrategyIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string? SelectedInvestmentEnvironmentId { get; init; }
}
public static class PreparationPlacementConsistencyPolicy
{
    public static bool HasThreeDistinctPlacements(
        IEnumerable<PreparationPlacement> placements) =>
        placements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() >= InitialRewardFormationPlanner.InitialTeamCapacity;

    public static PreparationBoardResult CreateDegradedContinuation(
        PreparationBoardResult latest,
        params IReadOnlyList<PreparationPlacement>[] priorSnapshots)
    {
        var bestKnownPlacements = priorSnapshots
            .Append(latest.Placements)
            .OrderByDescending(snapshot => snapshot
                .Select(item => item.Source.Character.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count())
            .First();

        return new PreparationBoardResult(
            PreparationBoardStatus.Deployed,
            latest.Bench,
            bestKnownPlacements,
            "规则内补位仍不足三人；保留最佳已知部署记录并降级继续出战。" );
    }
}
public enum RewardStageAutomationStatus
{
    InvestmentStrategySelected,
    RewardStagesCompletedAwaitingManualStrategy,
    InvestmentStrategyNotFound,
    PreparationIncomplete,
    RecoveredToHome,
    RecoveryRequested,
    RecognitionFailed,
    InputFailed
}

public sealed record RewardStageAutomationResult(
    RewardStageAutomationStatus Status,
    string Message)
{
    public bool Succeeded =>
        Status is RewardStageAutomationStatus.InvestmentStrategySelected or
            RewardStageAutomationStatus.RewardStagesCompletedAwaitingManualStrategy;
    public bool ShouldReroll =>
        Status is RewardStageAutomationStatus.InvestmentStrategyNotFound
            or RewardStageAutomationStatus.PreparationIncomplete
            or RewardStageAutomationStatus.RecoveredToHome
            or RewardStageAutomationStatus.RecoveryRequested;

    public bool AlreadyRecoveredToHome =>
        Status == RewardStageAutomationStatus.RecoveredToHome;
}
public interface IRewardStageAutomationController
{
    Task<RewardStageAutomationResult> RunAsync(
        nint windowHandle,
        RewardStageAutomationOptions options,
        CancellationToken cancellationToken);
}

public sealed partial class RewardStageAutomationController(
    IGameCapture capture,
    IGamePageClassifier pageClassifier,
    RewardShopReader shopReader,
    RewardShopPurchasePlanner shopPurchasePlanner,
    InvestmentStrategyPageReader strategyReader,
    RewardVisualDetector visualDetector,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    IPreparationBoardCompletionController preparationCompletionController,
    IAbandonSettlementRecovery settlementRecovery,
    ITaskEventSink eventSink) : IRewardStageAutomationController
{
    private static readonly StandardPoint ShopTogglePoint =
        new(1620, 975);
    private static readonly StandardPoint BattlePoint =
        new(1785, 750);
    private static readonly StandardPoint IncompleteLineupConfirmPoint =
        new(1170, 675);
    private static readonly StandardPoint ContinueChallengePoint =
        new(960, 895);
    private static readonly StandardPoint RetreatBattlePoint =
        new(1225, 985);
    private static readonly IReadOnlyList<StandardPoint> ShopCardPoints =
    [
        new(485, 175),
        new(750, 175),
        new(1015, 175),
        new(1280, 175),
        new(1545, 175)
    ];
    private static readonly IReadOnlyList<PixelRect> ShopCardVisualBounds =
    [
        new(360, 65, 230, 265),
        new(625, 65, 230, 265),
        new(890, 65, 230, 265),
        new(1155, 65, 230, 265),
        new(1420, 65, 230, 265)
    ];
    private static readonly ActionPolicy ShopPurchaseInputPolicy = new()
    {
        // The game resolves a card's pointer target on its render loop. Keep
        // move, press and release on separate frames instead of queuing all
        // three transitions back-to-back.
        PointerSettleDelay = TimeSpan.FromMilliseconds(80),
        MouseButtonHoldDelay = TimeSpan.FromMilliseconds(40),
        MaximumPointerPlacementAttempts = 2,
        PointerArrivalTolerance = 2,
        VerifyPointerArrivalBeforeClick = true,
        VerifyForegroundBeforeClick = true,
        AfterActionDelay = TimeSpan.FromMilliseconds(450)
    };
    private static readonly IReadOnlyList<StandardPoint> StrategyCardPoints =
    [
        new(460, 350),
        new(925, 350),
        new(1390, 350)
    ];
    private static readonly IReadOnlyList<StandardPoint> StrategyRefreshPoints =
    [
        new(390, 850),
        new(890, 850),
        new(1390, 850)
    ];
    private static readonly StandardPoint StrategyConfirmPoint =
        new(960, 985);
    private TimeSpan _pauseBaseline;
    private bool _battleTimeoutRecoveredToHome;

    private DateTimeOffset ActiveUtcNow =>
        DateTimeOffset.UtcNow -
        (foregroundGuard.TotalPausedDuration - _pauseBaseline);

    public async Task<RewardStageAutomationResult> RunAsync(
        nint windowHandle,
        RewardStageAutomationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        _battleTimeoutRecoveredToHome = false;
        var battleBudget = RewardBattleTimingPolicy.SelectBattleBudget(
            options.SelectedInvestmentEnvironmentId);
        var ownedCharacters = options.InitialOwnedCharacters
            .Select(item => item.Character)
            .Concat(options.InitialFormationPlacements.Select(item =>
                item.Source.Character))
            .DistinctBy(
                item => item.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                item => item.Name,
                StringComparer.OrdinalIgnoreCase);
        var presetPurchaseSuppressedNames = ownedCharacters.Keys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var shopPurchasedRetentionNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var currentFormationPlacements = options.InitialFormationPlacements
            .ToArray();
        var formationReservedNames = ownedCharacters.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Publish("RewardAutomationStarted", "开始执行前两层奖励关自动流程。");
        Publish(
            "RewardBattleBudgetSelected",
            $"本局实际选中投资环境=" +
            $"{options.SelectedInvestmentEnvironmentId ?? "未知"}；" +
            $"单场奖励战斗预算={battleBudget.TotalMinutes:0} 分钟。");
        if (await OpenMineBallsWithCapacityGuardAsync(
            windowHandle,
            currentFormationPlacements,
            options.PreparationCompletionOptions,
            "preparation_1_1",
            cancellationToken))
        {
            await SynchronizeOwnedCharactersAfterMineAsync(
                windowHandle,
                "preparation_1_1",
                options,
                presetPurchaseSuppressedNames,
                ownedCharacters,
                cancellationToken);
        }
        var firstShop = await OpenShopAsync(
            windowHandle,
            "preparation_1_1",
            cancellationToken);
        if (!firstShop)
        {
            return Failed(
                RewardStageAutomationStatus.RecoveryRequested,
                "未能确认 1-1 商店已打开。");
        }

        if (!await PurchaseShopCharactersAsync(
                windowHandle,
                "preparation_1_1",
                options,
                presetPurchaseSuppressedNames,
                formationReservedNames,
                ownedCharacters,
                shopPurchasedRetentionNames,
                allowGalaxyScholarPairPurchase: true,
                cancellationToken: cancellationToken))
        {
            return Failed(
                RewardStageAutomationStatus.RecognitionFailed,
                "1-1 商店可识别候选的购买输入失败；未继续发送购买输入。");
        }

        if (!await CloseShopAsync(
                windowHandle,
                "preparation_1_1",
                cancellationToken: cancellationToken))
        {
            return Failed(
                RewardStageAutomationStatus.RecoveryRequested,
                "未能确认 1-1 商店关闭。");
        }

        if (RequiresPreparationCompletion(
                currentFormationPlacements,
                options.PreparationCompletionOptions))
        {
            var completion = await CompleteFormationWithBenchRetryAsync(
                windowHandle,
                currentFormationPlacements,
                BuildPreparationCompletionOptions(
                    options.PreparationCompletionOptions,
                    shopPurchasedRetentionNames,
                    enableGalaxyScholarPairFormation:
                        options.PreparationCompletionOptions
                            .EnableGalaxyScholarPairFormation),
                "preparation_1_1",
                "1-1",
                cancellationToken);
            if (!completion.Succeeded)
            {
                return Failed(
                    completion.Status ==
                        PreparationBoardStatus.NoEligibleCharacter
                        ? RewardStageAutomationStatus.PreparationIncomplete
                        : RewardStageAutomationStatus.RecoveryRequested,
                    "1-1 商店补员、布阵或出售未通过后置验证：" +
                    completion.Message);
            }

            currentFormationPlacements = completion.Placements.ToArray();
            formationReservedNames.UnionWith(
                currentFormationPlacements.Select(item =>
                    item.Source.Character.Name));
        }

        var lockedGalaxyScholarNames = options.EnableGalaxyScholarRewardStrategy
            ? currentFormationPlacements
                .Where(item => GalaxyScholarPairPolicy.IsCandidate(
                    item.Source.Character))
                .Select(item => item.Source.Character.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(GalaxyScholarPairPolicy.ActivationCharacterCount)
                .ToArray()
            : [];
        if (options.EnableGalaxyScholarRewardStrategy)
        {
            Publish(
                lockedGalaxyScholarNames.Length ==
                    GalaxyScholarPairPolicy.ActivationCharacterCount
                    ? "GalaxyScholarRewardActivatedForBothBattles"
                    : "GalaxyScholarRewardNotActivatedAtFirstBattle",
                lockedGalaxyScholarNames.Length ==
                    GalaxyScholarPairPolicy.ActivationCharacterCount
                    ? $"1-1 出战前已锁定银河学者：{string.Join("、", lockedGalaxyScholarNames)}；" +
                      "两名角色将原样保留到 1-2，第二商店不再判定或补凑。"
                    : "1-1 出战前未凑齐两名不同银河学者；本轮猫猫糕策略不生效，" +
                      "1-2 不再补凑或重新触发。",
                lockedGalaxyScholarNames.Length ==
                    GalaxyScholarPairPolicy.ActivationCharacterCount
                    ? TaskEventLevel.Information
                    : TaskEventLevel.Warning);
        }

        if (!await AdvanceBattleToPageAsync(
                windowHandle,
                "preparation_1_1",
                "reward_shop",
                battleBudget,
                allowIncompleteLineupConfirmation:
                    !PreparationPlacementConsistencyPolicy
                        .HasThreeDistinctPlacements(
                            currentFormationPlacements),
                cancellationToken: cancellationToken))
        {
            return Failed(
                _battleTimeoutRecoveredToHome
                    ? RewardStageAutomationStatus.RecoveredToHome
                    : RewardStageAutomationStatus.InputFailed,
                _battleTimeoutRecoveredToHome
                    ? "1-1 战斗超时后已按验证路径撤退、放弃结算并返回主页；本轮可直接重刷。"
                    : "1-1 出战或结算流程未能通过页面验证。");
        }

        if (!await PurchaseShopCharactersAsync(
                windowHandle,
                "preparation_1_2",
                options,
                presetPurchaseSuppressedNames,
                formationReservedNames,
                ownedCharacters,
                shopPurchasedRetentionNames,
                allowGalaxyScholarPairPurchase: false,
                cancellationToken: cancellationToken))
        {
            return Failed(
                RewardStageAutomationStatus.RecognitionFailed,
                "1-2 商店可识别候选的购买输入失败；未继续发送购买输入。");
        }

        if (!await CloseShopAsync(windowHandle, "preparation_1_2", cancellationToken))
        {
            return Failed(
                RewardStageAutomationStatus.RecoveryRequested,
                "未能确认 1-2 商店关闭。");
        }

        var secondPreparationOptions = BuildPreparationCompletionOptions(
            options.PreparationCompletionOptions,
            shopPurchasedRetentionNames,
            enableGalaxyScholarPairFormation: false);
        if (await OpenMineBallsWithCapacityGuardAsync(
            windowHandle,
            currentFormationPlacements,
            secondPreparationOptions,
            "preparation_1_2",
            cancellationToken))
        {
            await SynchronizeOwnedCharactersAfterMineAsync(
                windowHandle,
                "preparation_1_2",
                options,
                presetPurchaseSuppressedNames,
                ownedCharacters,
                cancellationToken);
        }
        if (RequiresPreparationCompletion(
                currentFormationPlacements,
                secondPreparationOptions))
        {
            Publish(
                "PreparationAfterSecondMineStarted",
                "1-2 晶矿球已处理，重新识别当前备战席并执行补位/出售规划；" +
                "完成后才允许 1-2 出战。");
            var completion = await CompleteFormationWithBenchRetryAsync(
                windowHandle,
                currentFormationPlacements,
                secondPreparationOptions,
                "preparation_1_2",
                "1-2",
                cancellationToken);
            if (!completion.Succeeded)
            {
                return Failed(
                    completion.Status ==
                        PreparationBoardStatus.NoEligibleCharacter
                        ? RewardStageAutomationStatus.PreparationIncomplete
                        : RewardStageAutomationStatus.RecoveryRequested,
                    "1-2 晶矿球后的备战席补位或出售未通过后置验证：" +
                    completion.Message);
            }

            currentFormationPlacements = completion.Placements.ToArray();
        }
        if (lockedGalaxyScholarNames.Length ==
            GalaxyScholarPairPolicy.ActivationCharacterCount)
        {
            Publish(
                "GalaxyScholarRewardCarriedIntoSecondBattle",
                $"1-2 沿用 1-1 已锁定的银河学者：" +
                $"{string.Join("、", lockedGalaxyScholarNames)}；未执行第二次羁绊判定。");
        }

        if (!await AdvanceBattleToPageAsync(
                windowHandle,
                "preparation_1_2",
                "investment_strategy",
                battleBudget,
                allowIncompleteLineupConfirmation:
                    !PreparationPlacementConsistencyPolicy
                        .HasThreeDistinctPlacements(
                            currentFormationPlacements),
                cancellationToken: cancellationToken))
        {
            return Failed(
                _battleTimeoutRecoveredToHome
                    ? RewardStageAutomationStatus.RecoveredToHome
                    : RewardStageAutomationStatus.InputFailed,
                _battleTimeoutRecoveredToHome
                    ? "1-2 战斗超时后已按验证路径撤退、放弃结算并返回主页；本轮可直接重刷。"
                    : "1-2 出战或结算流程未能通过页面验证。");
        }

        return await CompleteAfterSecondRewardStageAsync(
            windowHandle,
            options.PreferredInvestmentStrategyIds,
            cancellationToken);
    }

    private static bool RequiresPreparationCompletion(
        IReadOnlyList<PreparationPlacement> placements,
        PreparationBoardOptions options) =>
        placements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() < InitialRewardFormationPlanner.InitialTeamCapacity ||
        options.EnableGalaxyScholarPairFormation ||
        options.BenchSaleMode != PreparationBenchSaleMode.None;

    private static PreparationBoardOptions BuildPreparationCompletionOptions(
        PreparationBoardOptions source,
        IEnumerable<string> shopPurchasedNames,
        bool enableGalaxyScholarPairFormation) =>
        new()
        {
            EligibleCharacterNames = source.EligibleCharacterNames,
            EnableGalaxyScholarPairFormation =
                enableGalaxyScholarPairFormation,
            BenchSaleMode = source.BenchSaleMode,
            InterestThreshold = source.InterestThreshold,
            RetainedCharacterNames = source.RetainedCharacterNames
                .Concat(shopPurchasedNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            RequiredRetainedCharacterNames =
                source.RequiredRetainedCharacterNames
                    .Concat(shopPurchasedNames)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
            EnableEarlyStrongFormationRetention =
                source.EnableEarlyStrongFormationRetention,
            DeferBenchSaleUntilShopCompletion =
                source.DeferBenchSaleUntilShopCompletion
        };

    private async Task<PreparationBoardResult>
        CompleteFormationWithBenchRetryAsync(
            nint windowHandle,
            IReadOnlyList<PreparationPlacement> initialPlacements,
            PreparationBoardOptions options,
            string preparationPageId,
            string phase,
            CancellationToken cancellationToken)
    {
        var completion = await preparationCompletionController
            .CompleteAfterShopAsync(
                windowHandle,
                initialPlacements,
                options,
                preparationPageId,
                cancellationToken);
        if (completion.Status != PreparationBoardStatus.NoEligibleCharacter)
        {
            return completion;
        }

        var initialNames = initialPlacements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var verifiedNames = completion.Placements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var shopNames = verifiedNames
            .Except(initialNames, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Publish(
            "PreparationIncompleteBenchRetryStarted",
            $"阶段={phase}；首次后置结果={completion.Status}；" +
            $"控制器部署记录={verifiedNames.Length}/3[{string.Join("、", verifiedNames)}]；" +
            $"初始来源=[{string.Join("、", initialNames)}]；" +
            $"商店/晶矿补位来源=[{string.Join("、", shopNames)}]；" +
            $"不足原因={completion.Message}；返回 PreparationIncomplete 前再次识别备战席并重试补位。",
            TaskEventLevel.Warning);
        if (PreparationPlacementConsistencyPolicy.HasThreeDistinctPlacements(
                completion.Placements))
        {
            Publish(
                "PreparationIncompleteResultContainedThreePlacements",
                $"阶段={phase} 的控制器结果已携带 3 名不同角色的部署记录；" +
                "这只是内存 Placements 一致性检查，不宣称已视觉确认棋盘 3/3。继续奖励关。");
            return new PreparationBoardResult(
                PreparationBoardStatus.Deployed,
                completion.Bench,
                completion.Placements,
                "控制器结果携带 3 名不同角色部署记录；忽略矛盾的不完整状态。" );
        }

        await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
        var final = await preparationCompletionController
            .CompleteAfterShopAsync(
                windowHandle,
                completion.Placements,
                options,
                preparationPageId,
                cancellationToken);
        var finalNames = final.Placements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (PreparationPlacementConsistencyPolicy.HasThreeDistinctPlacements(
                final.Placements))
        {
            Publish(
                "PreparationCompletionBenchRetrySucceeded",
                $"阶段={phase} 再次识别备战席并重试补位后，控制器返回 " +
                $"{finalNames.Length}/3 名不同部署记录：{string.Join("、", finalNames)}；" +
                "继续出战。该事件不是棋盘 3/3 视觉复核证据。");
            return final.Succeeded
                ? final
                : new PreparationBoardResult(
                    PreparationBoardStatus.Deployed,
                    final.Bench,
                    final.Placements,
                    "再次识别备战席并重试补位后得到 3 名不同部署记录。" );
        }

        var degraded = PreparationPlacementConsistencyPolicy
            .CreateDegradedContinuation(
                final,
                initialPlacements,
                completion.Placements);
        var degradedNames = degraded.Placements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Publish(
            "PreparationIncompleteDegradedContinuation",
            $"阶段={phase}；规则内补位及重读均未得到三名部署记录；" +
            $"保留最佳已知控制器部署={degradedNames.Length}/3" +
            $"[{string.Join("、", degradedNames)}] 并继续尝试出战。" +
            "未恢复任意备战角色兜底，也不声称已视觉确认棋盘 3/3。",
            TaskEventLevel.Warning);
        return degraded;
    }
    private async Task<bool> ClickStandardPointAsync(
        nint windowHandle,
        StandardPoint standardPoint,
        string displayName,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var point = MapStandardPoint(
            window,
            new PixelPoint(standardPoint.X, standardPoint.Y));
        var action = await input.ClickAsync(
            new ClickTarget(
                displayName,
                displayName,
                window,
                BoundsAround(window, point)),
            new ActionPolicy
            {
                AfterActionDelay = TimeSpan.FromMilliseconds(450)
            },
            cancellationToken);
        if (!action.Succeeded)
        {
            Publish(
                "RewardActionFailed",
                action.Message,
                TaskEventLevel.Warning);
        }

        return action.Succeeded;
    }

    private async Task<ActionResult> ClickShopCardAsync(
        nint windowHandle,
        int slot,
        string characterName,
        CancellationToken cancellationToken)
    {
        if (slot < 0 || slot >= ShopCardPoints.Count)
        {
            return ActionResult.Failure(
                $"商店槽位 {slot + 1} 超出可点击范围。");
        }

        var (window, frame) = await CaptureForegroundAsync(
            windowHandle,
            cancellationToken);
        var page = pageClassifier.Classify(frame);
        if (!string.Equals(
                page?.PageId,
                "reward_shop",
                StringComparison.OrdinalIgnoreCase))
        {
            var rejected = ActionResult.Failure(
                $"购买前页面为 {page?.PageId ?? "Unknown"}，已阻止旧商店坐标输入。");
            Publish(
                "ShopPurchaseInputReadinessRejected",
                $"槽位={slot + 1}；角色={characterName}；" +
                $"截图={frame.Width}x{frame.Height}；页面={page?.PageId ?? "Unknown"}；" +
                "动画/过渡状态=未稳定为商店；未发送鼠标输入。",
                TaskEventLevel.Warning);
            return rejected;
        }

        var point = GetShopCardClientPoint(
            slot,
            window.ClientArea.Width,
            window.ClientArea.Height);
        var target = new ClickTarget(
            $"shop_purchase_slot_{slot + 1}",
            $"购买 {characterName}",
            window,
            BoundsAround(window, point));
        Publish(
            "ShopPurchaseInputPrepared",
            $"槽位={slot + 1}；角色={characterName}；" +
            $"窗口=0x{window.Handle:X}；客户区=" +
            $"({window.ClientArea.X},{window.ClientArea.Y}," +
            $"{window.ClientArea.Width}x{window.ClientArea.Height})；" +
            $"截图={frame.Width}x{frame.Height}；缩放=" +
            $"({window.ClientArea.Width / 1920d:F4}," +
            $"{window.ClientArea.Height / 1080d:F4})；" +
            $"购买区域=({target.ClientBounds.X},{target.ClientBounds.Y}," +
            $"{target.ClientBounds.Width}x{target.ClientBounds.Height})；" +
            $"客户区落点=({point.X},{point.Y})；页面=reward_shop；" +
            "商店快照=连续两帧稳定；窗口焦点=前台守卫已通过；" +
            "动画/过渡状态=稳定商店页。",
            TaskEventLevel.Information);

        var action = await input.ClickAsync(
            target,
            ShopPurchaseInputPolicy,
            cancellationToken);
        if (action.Diagnostic is { } diagnostic)
        {
            Publish(
                action.Succeeded
                    ? "ShopPurchaseInputDispatched"
                    : "ShopPurchaseInputRejected",
                $"槽位={slot + 1}；角色={characterName}；" +
                $"输入方式={diagnostic.InputMethod}；" +
                $"屏幕落点=({diagnostic.TargetScreenPoint.X}," +
                $"{diagnostic.TargetScreenPoint.Y})；" +
                $"移动后光标=" +
                (diagnostic.CursorAfterMove is { } cursor
                    ? $"({cursor.X},{cursor.Y})"
                    : "读取失败") + "；" +
                $"发送前焦点={(diagnostic.ForegroundBeforeSend ? "游戏" : "非游戏")}；" +
                $"落点顶层窗口=0x{diagnostic.WindowAtTarget:X}；" +
                $"移动尝试={diagnostic.PointerPlacementAttempts}/" +
                $"{ShopPurchaseInputPolicy.MaximumPointerPlacementAttempts}；" +
                $"SendInput返回=" +
                $"move:{diagnostic.MoveSendCount}," +
                $"down:{diagnostic.MouseDownSendCount}," +
                $"up:{diagnostic.MouseUpSendCount}；" +
                $"移动稳定={diagnostic.PointerSettleDelay.TotalMilliseconds:F0}ms；" +
                $"按键保持={diagnostic.MouseButtonHoldDelay.TotalMilliseconds:F0}ms；" +
                $"结果={action.Message}",
                action.Succeeded
                    ? TaskEventLevel.Information
                    : TaskEventLevel.Warning);
        }
        else if (!action.Succeeded)
        {
            Publish(
                "ShopPurchaseInputRejected",
                $"槽位={slot + 1}；角色={characterName}；{action.Message}",
                TaskEventLevel.Warning);
        }

        return action;
    }

    internal static PixelPoint GetShopCardClientPoint(
        int slot,
        int clientWidth,
        int clientHeight)
    {
        if (slot < 0 || slot >= ShopCardPoints.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var reference = ShopCardPoints[slot];
        return new PixelPoint(
            (int)Math.Round(reference.X * clientWidth / 1920d),
            (int)Math.Round(reference.Y * clientHeight / 1080d));
    }

    internal static PixelRect GetShopCardVisualBounds(
        int slot,
        int clientWidth,
        int clientHeight)
    {
        if (slot < 0 || slot >= ShopCardVisualBounds.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        var reference = ShopCardVisualBounds[slot];
        return new PixelRect(
            (int)Math.Round(reference.X * clientWidth / 1920d),
            (int)Math.Round(reference.Y * clientHeight / 1080d),
            (int)Math.Round(reference.Width * clientWidth / 1920d),
            (int)Math.Round(reference.Height * clientHeight / 1080d));
    }

    private async Task<bool> WaitForPageAsync(
        nint windowHandle,
        string pageId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + timeout;
        var stability = new ConsecutiveObservationTracker<string>(
            2,
            StringComparer.OrdinalIgnoreCase);
        while (ActiveUtcNow < deadline)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            if (page is not null &&
                string.Equals(
                    page.PageId,
                    pageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (stability.Observe(page.PageId))
                {
                    return true;
                }
            }
            else
            {
                stability.Reset();
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(350),
                cancellationToken);
        }

        return false;
    }

    private async Task<PageClassificationResult?> ReadStablePageAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var stability = new ConsecutiveObservationTracker<string>(
            2,
            StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var current = pageClassifier.Classify(frame);
            if (current is not null && stability.Observe(current.PageId))
            {
                return current;
            }

            if (current is null)
            {
                stability.Reset();
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }

        return null;
    }

    private async Task<PageClassificationResult?>
        RecoverKnownRewardPageWithEscapeAsync(
            nint windowHandle,
            string eventCode,
            CancellationToken cancellationToken,
            Func<PageClassificationResult, bool>? acceptPage = null)
    {
        const int maximumEscapeAttempts = 3;
        for (var attempt = 1; attempt <= maximumEscapeAttempts; attempt++)
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            Publish(
                eventCode,
                $"当前页面未知或不符合本阶段状态，执行第 {attempt}/{maximumEscapeAttempts} 次 Esc；" +
                "按键后重新识别是否回到已知页面。",
                TaskEventLevel.Warning);
            var action = await input.PressKeyAsync(
                window,
                InputKey.Escape,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(500)
                },
                cancellationToken);
            if (!action.Succeeded)
            {
                continue;
            }

            var recovered = await ReadStablePageAsync(
                windowHandle,
                cancellationToken);
            if (recovered is not null &&
                (acceptPage is null || acceptPage(recovered)))
            {
                Publish(
                    eventCode + "Recovered",
                    $"第 {attempt} 次 Esc 后已稳定识别到 {recovered.PageId}，" +
                    "返回状态机继续处理。");
                return recovered;
            }

            if (recovered is not null)
            {
                Publish(
                    eventCode + "PageStillRejected",
                    $"第 {attempt} 次 Esc 后识别到 {recovered.PageId}，" +
                    "但该页仍不符合当前状态图；继续下一次有限 Esc。",
                    TaskEventLevel.Warning);
            }
        }

        Publish(
            eventCode + "Failed",
            "三次 Esc 均已逐次重新识别，但仍未回到任何已知页面。",
            TaskEventLevel.Warning);
        return null;
    }

    private IReadOnlyList<PageAnchorDiagnostic> LastPageDiagnostics() =>
        pageClassifier is IGamePageClassifierDiagnostics diagnostics
            ? diagnostics.LastDiagnostics
            : [];

    private async Task<(GameWindowInfo Window, CaptureFrame Frame)>
        CaptureForegroundAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var frame = await capture.CaptureAsync(
            window,
            cancellationToken);
        return (window, frame);
    }

    private static PixelPoint MapStandardPoint(
        GameWindowInfo window,
        PixelPoint point) =>
        new(
            (int)Math.Round(point.X * window.ClientArea.Width / 1920d),
            (int)Math.Round(point.Y * window.ClientArea.Height / 1080d));

    private static PixelRect BoundsAround(
        GameWindowInfo window,
        PixelPoint point)
    {
        var radius = Math.Max(
            4,
            (int)Math.Round(6 * window.ClientArea.Width / 1920d));
        return new PixelRect(
            Math.Clamp(
                point.X - radius,
                0,
                Math.Max(0, window.ClientArea.Width - radius * 2)),
            Math.Clamp(
                point.Y - radius,
                0,
                Math.Max(0, window.ClientArea.Height - radius * 2)),
            radius * 2,
            radius * 2);
    }

    private RewardStageAutomationResult Failed(
        RewardStageAutomationStatus status,
        string message)
    {
        Publish(status.ToString(), message, TaskEventLevel.Warning);
        return new RewardStageAutomationResult(status, message);
    }

    private void Publish(
        string code,
        string message,
        TaskEventLevel level = TaskEventLevel.Information) =>
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            level,
            code,
            message));
}
