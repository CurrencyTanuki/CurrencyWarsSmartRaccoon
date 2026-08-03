using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

public sealed partial class RewardStageAutomationController
{
    private async Task<bool> AdvanceBattleToPageAsync(
        nint windowHandle,
        string preparationPageId,
        string expectedPostBattlePageId,
        TimeSpan battleBudget,
        bool allowIncompleteLineupConfirmation,
        CancellationToken cancellationToken)
    {
        const int maximumStateObservations = 12;
        const int maximumBattleClicks = 3;
        var battleClicks = 0;
        var startOwnership = new RewardBattleStartOwnershipTracker();
        var unknownObservations = 0;
        var stateMachine = new RewardBattleStateMachine(
            preparationPageId,
            expectedPostBattlePageId);

        for (var observation = 1;
             observation <= maximumStateObservations;
             observation++)
        {
            var current = await ReadStablePageAsync(
                windowHandle,
                cancellationToken);
            var transition = stateMachine.Observe(
                current?.PageId,
                LastPageDiagnostics());
            if (!transition.Allowed)
            {
                if (stateMachine.State != RewardBattleFlowState.Battle)
                {
                    Publish(
                        "RewardBattleTransitionRecoveryStarted",
                        transition.Message +
                        " 当前尚未确认处于战斗；先执行逐次识别的三次 Esc 恢复。",
                        TaskEventLevel.Warning);
                    var recovered =
                        await RecoverKnownRewardPageWithEscapeAsync(
                            windowHandle,
                            "RewardBattleRejectedPageEscape",
                            cancellationToken,
                            page => stateMachine.Observe(
                                page.PageId,
                                LastPageDiagnostics()).Allowed);
                    if (recovered is not null)
                    {
                        var recoveredTransition = stateMachine.Observe(
                            recovered.PageId,
                            LastPageDiagnostics());
                        if (recoveredTransition.Allowed)
                        {
                            stateMachine.Apply(recoveredTransition);
                            unknownObservations = 0;
                            Publish(
                                "RewardBattleTransitionRecovered",
                                $"Esc 后页面 {recovered.PageId} 已重新纳入奖励战斗状态图；" +
                                $"当前状态={stateMachine.State}，继续本段有限流程。");
                            continue;
                        }
                    }
                }

                Publish(
                    "RewardBattleTransitionRejected",
                    transition.Message +
                    (stateMachine.State == RewardBattleFlowState.Battle
                        ? " 已确认战斗上下文，按规则不发送 Esc，安全停止。"
                        : " 三次 Esc 恢复后仍无法回到状态图，安全停止。"),
                    TaskEventLevel.Warning);
                return false;
            }

            stateMachine.Apply(transition);

            Publish(
                "RewardBattleStateObserved",
                $"奖励关战斗段第 {observation}/{maximumStateObservations} 次状态识别：" +
                $"{transition.PageId ?? "动画/未形成页面结论"}，" +
                $"观察={transition.Observation}，迁移状态={stateMachine.State}。");

            switch (stateMachine.State)
            {
                case RewardBattleFlowState.ExpectedPostBattle:
                    return true;

                case RewardBattleFlowState.Preparation:
                    unknownObservations = 0;
                    startOwnership.Observe(RewardBattleFlowState.Preparation);
                    if (battleClicks >= maximumBattleClicks)
                    {
                        Publish(
                            "RewardBattleStartFailed",
                            $"已在 {preparationPageId} 执行 {maximumBattleClicks} 次出战，" +
                            "仍未进入战斗或成功页；安全停止。",
                            TaskEventLevel.Warning);
                        return false;
                    }

                    battleClicks++;
                    Publish(
                        "RewardBattleStartAttempt",
                        $"在 {preparationPageId} 执行第 {battleClicks}/" +
                        $"{maximumBattleClicks} 次出战。");
                    if (!await ClickStandardPointAsync(
                            windowHandle,
                            BattlePoint,
                            "出战",
                            cancellationToken))
                    {
                        continue;
                    }

                    if (!stateMachine.TryStartBattle())
                    {
                        Publish(
                            "RewardBattleTransitionRejected",
                            "当前迁移状态不允许执行出战；安全停止。",
                            TaskEventLevel.Warning);
                        return false;
                    }

                    startOwnership.MarkSuccessfulStartInput();

                    if (allowIncompleteLineupConfirmation)
                    {
                        await ConfirmIncompleteLineupPromptIfPresentAsync(
                            windowHandle,
                            cancellationToken);
                    }

                    var afterStart = await WaitForRelevantBattlePageAsync(
                        windowHandle,
                        stateMachine,
                        TimeSpan.FromSeconds(12),
                        cancellationToken);
                    if (afterStart != RewardBattlePageState.Unknown)
                    {
                        Publish(
                            "RewardBattleTransitionObserved",
                            $"出战后的有限候选状态已确认：{stateMachine.State}。");
                    }

                    continue;

                case RewardBattleFlowState.StartingBattle:
                    unknownObservations++;
                    if (unknownObservations >= 3)
                    {
                        var recovered =
                            await RecoverKnownRewardPageWithEscapeAsync(
                                windowHandle,
                                "RewardBattleStartUnknownPage",
                                cancellationToken);
                        if (recovered is not null)
                        {
                            unknownObservations = 0;
                            continue;
                        }

                        Publish(
                            "RewardBattleStartTransitionTimeout",
                            "出战输入后连续 3 次迁移等待仍未确认战斗、秒杀成功、" +
                            "原备战页或预期后置页；逐次识别的三次 Esc 也未恢复已知页，停止。",
                            TaskEventLevel.Warning);
                        return false;
                    }

                    await WaitForRelevantBattlePageAsync(
                        windowHandle,
                        stateMachine,
                        TimeSpan.FromSeconds(3),
                        cancellationToken);
                    continue;

                case RewardBattleFlowState.Battle:
                    unknownObservations = 0;
                    if (startOwnership.Observe(RewardBattleFlowState.Battle))
                    {
                        Publish(
                            "RewardBattleStartOwnershipConfirmed",
                            $"已确认出战输入成功后从 {preparationPageId} 进入 Battle；" +
                            "仅本场获得超时撤退授权。");
                    }
                    Publish(
                        "RewardBattleStarted",
                        "已确认进入奖励关战斗，开始监测战斗结果和自动战斗状态。");
                    var battleResult = await WaitForBattleSuccessAsync(
                        windowHandle,
                        stateMachine,
                        battleBudget,
                        cancellationToken);
                    if (battleResult == RewardBattleWaitResult.TimedOut)
                    {
                        var timeout = await HandleTimedOutRewardBattleAsync(
                            windowHandle,
                            stateMachine,
                            startOwnership.IsAuthorized,
                            cancellationToken);
                        if (timeout ==
                            RewardBattleTimeoutHandlingResult.Completed)
                        {
                            continue;
                        }

                        if (timeout ==
                            RewardBattleTimeoutHandlingResult.RecoveredToHome)
                        {
                            _battleTimeoutRecoveredToHome = true;
                        }

                        return false;
                    }

                    if (battleResult != RewardBattleWaitResult.Completed)
                    {
                        return false;
                    }

                    continue;

                case RewardBattleFlowState.Success:
                    unknownObservations = 0;
                    Publish(
                        "RewardBattleCompleted",
                        battleClicks == 0
                            ? "当前已在挑战成功页；跳过出战/战斗中间状态并继续结算。"
                            : "战斗可能在识别战斗页前已秒杀完成；直接从挑战成功页继续结算。");
                    if (!stateMachine.TryContinueChallenge())
                    {
                        Publish(
                            "RewardBattleTransitionRejected",
                            "当前迁移状态不允许点击继续挑战；安全停止。",
                            TaskEventLevel.Warning);
                        return false;
                    }

                    return await ContinueToPageAsync(
                        windowHandle,
                        expectedPostBattlePageId,
                        cancellationToken);

                case RewardBattleFlowState.ContinuingAfterSuccess:
                    Publish(
                        "RewardBattleContinueStateUnexpected",
                        "继续挑战迁移应由结算后置验证完成；当前重新进入外层观察，安全停止。",
                        TaskEventLevel.Warning);
                    return false;

                default:
                    unknownObservations++;
                    if (unknownObservations >= 3)
                    {
                        var recovered =
                            await RecoverKnownRewardPageWithEscapeAsync(
                                windowHandle,
                                "RewardBattleUnknownPage",
                                cancellationToken);
                        if (recovered is not null)
                        {
                            unknownObservations = 0;
                            continue;
                        }

                        Publish(
                            "RewardBattleUnknownPage",
                            "战斗段连续 3 次无法识别为备战、战斗、挑战成功或后置页面；" +
                            "逐次识别的三次 Esc 也未恢复已知页，停止。",
                            TaskEventLevel.Warning);
                        return false;
                    }

                    break;
            }
        }

        Publish(
            "RewardBattleStateBudgetExhausted",
            $"战斗段状态识别已达到 {maximumStateObservations} 次上限；安全停止。",
            TaskEventLevel.Warning);
        return false;
    }

    private async Task<RewardBattleWaitResult> WaitForBattleSuccessAsync(
        nint windowHandle,
        RewardBattleStateMachine stateMachine,
        TimeSpan battleBudget,
        CancellationToken cancellationToken)
    {
        const int maximumAutoBattleToggleAttempts = 3;
        var maximumBattleObservations =
            (int)Math.Ceiling(battleBudget.TotalSeconds / 0.75) + 12;
        var autoBattleObservations = 0;
        var autoBattleToggleAttempts = 0;
        var autoBattleConfirmedEnabled = false;
        var autoBattleVerificationFinished = false;
        var recentAutoBattleStates = new List<AutoBattleVisualState>();
        // 自动战斗检测节律：快速检查 0.2s/次（窗口 2s），慢速 2s/次。
        var fastAutoBattleCheck = true;
        var fastAutoBattleCheckUntil =
            ActiveUtcNow + TimeSpan.FromSeconds(2);
        var nextAutoBattleCheck =
            ActiveUtcNow + TimeSpan.FromMilliseconds(200);
        var deadline = ActiveUtcNow + battleBudget;

        for (var observationIndex = 1;
             observationIndex <= maximumBattleObservations;
             observationIndex++)
        {
            if (ActiveUtcNow >= deadline)
            {
                break;
            }
            var (window, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            var observation = stateMachine.Observe(
                page?.PageId,
                LastPageDiagnostics());
            if (!observation.Allowed)
            {
                Publish(
                    "RewardBattleTransitionRejected",
                    observation.Message + " 未执行自动战斗输入，安全停止。",
                    TaskEventLevel.Warning);
                return RewardBattleWaitResult.Failed;
            }

            stateMachine.Apply(observation);

            if (observation.Observation == RewardBattlePageState.Success)
            {
                Publish(
                    "RewardBattleCompleted",
                    "已确认奖励关挑战成功。");
                return RewardBattleWaitResult.Completed;
            }

            if (observation.Observation ==
                RewardBattlePageState.ExpectedPostBattle)
            {
                Publish(
                    "RewardBattlePostPageReached",
                    $"已直接到达 {observation.PageId}；战斗与结算段视为完成。");
                return RewardBattleWaitResult.Completed;
            }

            if (observation.Observation == RewardBattlePageState.Battle ||
                (observation.Observation == RewardBattlePageState.Unknown &&
                 stateMachine.State == RewardBattleFlowState.Battle))
            {
                if (!autoBattleConfirmedEnabled &&
                    !autoBattleVerificationFinished &&
                    ActiveUtcNow >= nextAutoBattleCheck)
                {
                    autoBattleObservations++;
                    var visual = visualDetector.ReadAutoBattleState(frame);
                    recentAutoBattleStates.Add(visual.State);
                    // 保留最近 3 帧用于连续判定。
                    if (recentAutoBattleStates.Count > 3)
                    {
                        recentAutoBattleStates.RemoveAt(0);
                    }

                    Publish(
                        "AutoBattleFrameObserved",
                        $"自动战斗单帧观察 {autoBattleObservations}：" +
                        $"状态={visual.State}，置信度={visual.Confidence:F2}，" +
                        $"金色={visual.GoldPixels}/{visual.RequiredGoldPixels}，" +
                        $"最大金色连通块={visual.LargestGoldComponent}，" +
                        $"中性灰={visual.NeutralPixels}；节律=" +
                        $"{(fastAutoBattleCheck ? "快速0.2s" : "慢速2s")}。");

                    var last3 = recentAutoBattleStates;
                    var consecutiveEnabled = last3.Count >= 3 &&
                        last3[^1] == AutoBattleVisualState.Enabled &&
                        last3[^2] == AutoBattleVisualState.Enabled &&
                        last3[^3] == AutoBattleVisualState.Enabled;
                    var consecutiveDisabled = last3.Count >= 2 &&
                        last3[^1] == AutoBattleVisualState.Disabled &&
                        last3[^2] == AutoBattleVisualState.Disabled;
                    var justToggled = autoBattleToggleAttempts > 0 &&
                        recentAutoBattleStates.Count <= 3;

                    if (consecutiveEnabled)
                    {
                        // 连续 3 帧开启：确认已开启，进入慢速监测。
                        autoBattleConfirmedEnabled = true;
                        fastAutoBattleCheck = false;
                        Publish(
                            "AutoBattleEnabledConfirmed",
                            $"自动战斗已确认开启（连续 3 帧 Enabled）；" +
                            "切换到每 2 秒一次的慢速监测。");
                    }
                    else if (consecutiveDisabled &&
                             autoBattleToggleAttempts <
                                 maximumAutoBattleToggleAttempts)
                    {
                        // 连续 2 帧禁用：按 V 开启（快速检查期间）。
                        autoBattleToggleAttempts++;
                        Publish(
                            "EnableAutoBattleAttempt",
                            $"发送 V 开启自动战斗：第 {autoBattleToggleAttempts}/" +
                            $"{maximumAutoBattleToggleAttempts} 次（连续 2 帧 Disabled）；" +
                            "发送后回到快速检查验证。",
                            TaskEventLevel.Information);
                        var action = await input.PressKeyAsync(
                            window,
                            InputKey.V,
                            new ActionPolicy
                            {
                                AfterActionDelay =
                                    TimeSpan.FromMilliseconds(500)
                            },
                            cancellationToken);
                        if (!action.Succeeded)
                        {
                            Publish(
                                "EnableAutoBattleFailed",
                                action.Message,
                                TaskEventLevel.Warning);
                        }

                        recentAutoBattleStates.Clear();
                        fastAutoBattleCheck = true;
                        fastAutoBattleCheckUntil =
                            ActiveUtcNow + TimeSpan.FromSeconds(2);
                        Publish(
                            "AutoBattlePostToggleVerificationPending",
                            "V 已发送；回到快速检查（0.2s×2s），" +
                            "连续 3 帧 Enabled 即确认开启。");
                    }
                    else if (consecutiveDisabled &&
                             autoBattleToggleAttempts >=
                                 maximumAutoBattleToggleAttempts)
                    {
                        autoBattleVerificationFinished = true;
                        Publish(
                            "AutoBattleEnableUnconfirmed",
                            $"已发送 {autoBattleToggleAttempts} 次 V，最终图标仍未确认开启；" +
                            "本场不再切换，继续只监测战斗结果。",
                            TaskEventLevel.Warning);
                    }

                    // 快速检查窗口耗尽仍未确认：转入慢速 2s。
                    if (!autoBattleConfirmedEnabled &&
                        !autoBattleVerificationFinished &&
                        fastAutoBattleCheck &&
                        ActiveUtcNow >= fastAutoBattleCheckUntil)
                    {
                        fastAutoBattleCheck = false;
                        Publish(
                            "AutoBattleSlowCheckStarted",
                            "快速检查窗口（0.2s×2s）结束且未确认开启；" +
                            "切换到每 2 秒一次的慢速检查。");
                    }

                    nextAutoBattleCheck = ActiveUtcNow +
                        (fastAutoBattleCheck
                            ? TimeSpan.FromMilliseconds(200)
                            : TimeSpan.FromSeconds(2));
                }
            }
            else if (observation.Observation == RewardBattlePageState.Unknown &&
                     observationIndex % 8 == 0)
            {
                Publish(
                    "RewardBattleAnimationFrameIgnored",
                    $"战斗上下文中已连续观察到终结技/过场式隐藏帧；" +
                    $"当前为第 {observationIndex}/{maximumBattleObservations} 帧。" +
                    "不按 Esc、不发送其他页面输入，继续等待自动战斗图标恢复或挑战成功页出现。",
                    TaskEventLevel.Information);
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(750),
                cancellationToken);
        }

        Publish(
            "RewardBattleMonitoringTimeout",            $"战斗监测达到 {battleBudget.TotalMinutes:0} 分钟/" +
            $"{maximumBattleObservations} 帧有限预算；开始超时自动状态复核。",
            TaskEventLevel.Warning);
        return RewardBattleWaitResult.TimedOut;
    }

    internal async Task<RewardBattleTimeoutHandlingResult>
        HandleTimedOutRewardBattleAsync(
            nint windowHandle,
            RewardBattleStateMachine stateMachine,
            bool battleStartedByController,
            CancellationToken cancellationToken,
            TimeSpan? graceOverride = null)
    {
        if (!battleStartedByController)
        {
            Publish(
                "RewardBattleTimeoutRecoveryBlocked",
                "未确认本控制器启动了当前战斗；禁止发送 V、Esc、撤退或放弃结算，安全停止。",
                TaskEventLevel.Error);
            return RewardBattleTimeoutHandlingResult.Blocked;
        }

        var timeoutDecision = await ReadAutoBattleConsensusAsync(
            windowHandle,
            cancellationToken);
        if (timeoutDecision.Consensus == AutoBattleVisualState.Disabled)
        {
            Publish(
                "AutoBattleTimeoutRetry",
                "超时复核多帧确认自动战斗关闭；允许最后一次发送 V。",
                TaskEventLevel.Warning);
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var action = await input.PressKeyAsync(
                window,
                InputKey.V,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(500)
                },
                cancellationToken);
            Publish(
                action.Succeeded
                    ? "AutoBattleTimeoutVSent"
                    : "AutoBattleTimeoutVFailed",
                action.Succeeded
                    ? "超时后的最后一次 V 已发送；进入有限宽限期。"
                    : $"超时后的最后一次 V 输入失败：{action.Message}；仍进入有限宽限期。",
                action.Succeeded
                    ? TaskEventLevel.Information
                    : TaskEventLevel.Warning);
        }
        else
        {
            Publish(
                timeoutDecision.Consensus == AutoBattleVisualState.Enabled
                    ? "AutoBattleTimeoutAlreadyEnabled"
                    : "AutoBattleTimeoutStateUncertain",
                timeoutDecision.Consensus == AutoBattleVisualState.Enabled
                    ? "超时复核已多帧确认自动战斗开启；不按 V，避免反向关闭。"
                    : "超时复核仍不确定；不盲目按 V。",
                timeoutDecision.Consensus == AutoBattleVisualState.Enabled
                    ? TaskEventLevel.Information
                    : TaskEventLevel.Warning);
        }

        if (await WaitForBattleOutcomeDuringGraceAsync(
                windowHandle,
                stateMachine,
                graceOverride ?? TimeSpan.FromSeconds(20),
                cancellationToken))
        {
            return RewardBattleTimeoutHandlingResult.Completed;
        }

        if (!battleStartedByController)
        {
            Publish(
                "RewardBattleTimeoutRecoveryBlocked",
                "未同时满足“出战输入成功”和“随后从当前奖励备战页确认进入 Battle”；" +
                "禁止执行 Esc、撤退或放弃结算，安全停止。",
                TaskEventLevel.Error);
            return RewardBattleTimeoutHandlingResult.Blocked;
        }

        return await RecoverTimedOutRewardBattleAsync(
                windowHandle,
                cancellationToken)
            ? RewardBattleTimeoutHandlingResult.RecoveredToHome
            : RewardBattleTimeoutHandlingResult.Failed;
    }

    private async Task<RewardAutoBattleDecision>
        ReadAutoBattleConsensusAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        var states = new List<AutoBattleVisualState>();
        for (var attempt = 1; attempt <= 5 && states.Count < 3; attempt++)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            if (page?.PageId is "reward_battle" or "battle_generic")
            {
                var reading = visualDetector.ReadAutoBattleState(frame);
                states.Add(reading.State);
                Publish(
                    "AutoBattleTimeoutFrameObserved",
                    $"超时自动状态复核 {states.Count}/3：状态={reading.State}，" +
                    $"金色={reading.GoldPixels}/{reading.RequiredGoldPixels}，" +
                    $"最大连通块={reading.LargestGoldComponent}，中性灰={reading.NeutralPixels}。");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        }

        var decision = RewardAutoBattlePolicy.Observe(false, states);
        Publish(
            "AutoBattleTimeoutConsensus",
            $"超时多帧共识={decision.Consensus}，" +
            $"Enabled={decision.EnabledVotes}，Disabled={decision.DisabledVotes}。");
        return decision;
    }

    private async Task<bool> WaitForBattleOutcomeDuringGraceAsync(
        nint windowHandle,
        RewardBattleStateMachine stateMachine,
        TimeSpan grace,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + grace;
        while (ActiveUtcNow < deadline)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            var observation = stateMachine.Observe(
                page?.PageId,
                LastPageDiagnostics());
            if (!observation.Allowed)
            {
                return false;
            }

            stateMachine.Apply(observation);
            if (observation.Observation is RewardBattlePageState.Success or
                RewardBattlePageState.ExpectedPostBattle)
            {
                Publish(
                    "RewardBattleCompletedDuringTimeoutGrace",
                    $"战斗在 {grace.TotalSeconds:0} 秒宽限期内进入 " +
                    $"{observation.Observation}。");
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return false;
    }

    private async Task<bool> RecoverTimedOutRewardBattleAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        Publish(
            "RewardBattleTimeoutRecoveryStarted",
            "仅对本控制器已确认并亲自启动的超时奖励战斗执行：Esc→暂停页→撤退→统一放弃结算。",
            TaskEventLevel.Warning);
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var escape = await input.PressKeyAsync(
            window,
            InputKey.Escape,
            new ActionPolicy
            {
                AfterActionDelay = TimeSpan.FromMilliseconds(350)
            },
            cancellationToken);
        if (!escape.Succeeded ||
            !await WaitForPageAsync(
                windowHandle,
                "reward_battle_pause",
                TimeSpan.FromSeconds(5),
                cancellationToken))
        {
            Publish(
                "RewardBattlePauseNotConfirmed",
                "Esc 后未稳定确认 reward_battle_pause；未点击撤退。",
                TaskEventLevel.Error);
            return false;
        }

        Publish(
            "RewardBattlePauseConfirmed",
            "已稳定确认战斗暂停/关卡信息页，允许点击撤退。");
        if (!await ClickStandardPointAsync(
                windowHandle,
                RetreatBattlePoint,
                "撤退",
                cancellationToken) ||
            !await WaitForPageAsync(
                windowHandle,
                "abandon_settlement_prompt",
                TimeSpan.FromSeconds(5),
                cancellationToken))
        {
            Publish(
                "RewardBattleRetreatPromptNotConfirmed",
                "撤退后未稳定确认 abandon_settlement_prompt；未点击放弃并结算。",
                TaskEventLevel.Error);
            return false;
        }

        var recovery = await settlementRecovery
            .CompleteFromAbandonSettlementPromptAsync(
                windowHandle,
                cancellationToken);
        Publish(
            recovery.Status == RejectedOpeningRecoveryStatus.Recovered
                ? "RewardBattleTimeoutRecoveryCompleted"
                : "RewardBattleTimeoutRecoveryFailed",
            recovery.Message,
            recovery.Status == RejectedOpeningRecoveryStatus.Recovered
                ? TaskEventLevel.Information
                : TaskEventLevel.Error);
        return recovery.Status == RejectedOpeningRecoveryStatus.Recovered;
    }

    private async Task<bool> ContinueToPageAsync(
        nint windowHandle,
        string expectedPageId,
        CancellationToken cancellationToken)
    {
        const int maximumContinueClicks = 20;
        var entry = await ReadStablePageAsync(
            windowHandle,
            cancellationToken);
        if (string.Equals(
                entry?.PageId,
                expectedPageId,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (entry?.PageId != "challenge_success")
        {
            Publish(
                "ContinueChallengeUnexpectedPage",
                $"准备继续挑战时识别到 {entry?.PageId ?? "未知页"}，" +
                "不是挑战成功页；未点击并安全停止。",
                TaskEventLevel.Warning);
            return false;
        }

        for (var attempt = 1; attempt <= maximumContinueClicks; attempt++)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            if (string.Equals(
                    page?.PageId,
                    expectedPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return await WaitForExpectedPostBattlePageAsync(
                    windowHandle,
                    expectedPageId,
                    cancellationToken);
            }

            if (page?.PageId != "challenge_success")
            {
                return await WaitForExpectedPostBattlePageAsync(
                    windowHandle,
                    expectedPageId,
                    cancellationToken);
            }

            Publish(
                "ContinueChallengeAttempt",
                $"挑战成功页连续推进：第 {attempt}/{maximumContinueClicks} 次点击继续挑战。");
            if (!await ClickStandardPointAsync(
                    windowHandle,
                    ContinueChallengePoint,
                    "继续挑战",
                    cancellationToken))
            {
                continue;
            }
        }

        var reached = await WaitForExpectedPostBattlePageAsync(
            windowHandle,
            expectedPageId,
            cancellationToken);
        if (!reached)
        {
            Publish(
                "ContinueChallengeFailed",
                $"连续点击继续挑战已达到 {maximumContinueClicks} 次，" +
                $"仍未到达 {expectedPageId}；安全停止。",
                TaskEventLevel.Warning);
        }

        return reached;
    }

    private async Task<bool> WaitForExpectedPostBattlePageAsync(
        nint windowHandle,
        string expectedPageId,
        CancellationToken cancellationToken)
    {
        if (await WaitForPageAsync(
                windowHandle,
                expectedPageId,
                TimeSpan.FromSeconds(12),
                cancellationToken))
        {
            Publish(
                "RewardPostBattlePageReached",
                $"已稳定进入战斗后置页面 {expectedPageId}。");
            return true;
        }

        var final = await ReadStablePageAsync(
            windowHandle,
            cancellationToken);
        var reached = string.Equals(
            final?.PageId,
            expectedPageId,
            StringComparison.OrdinalIgnoreCase);
        Publish(
            reached
                ? "RewardPostBattlePageReachedLate"
                : "RewardPostBattlePageTimeout",
            reached
                ? $"常规等待预算结束后复核到 {expectedPageId}，接受后置页面。"
                : $"等待并最终复核后仍未到达 {expectedPageId}，当前为" +
                  $" {final?.PageId ?? "未知页"}；安全停止。",
            reached ? TaskEventLevel.Information : TaskEventLevel.Warning);
        return reached;
    }

    private async Task<bool> ConfirmIncompleteLineupPromptIfPresentAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        const string promptPageId = "incomplete_lineup_prompt";
        var deadline = ActiveUtcNow + TimeSpan.FromSeconds(4);
        var stableFrames = 0;
        while (ActiveUtcNow < deadline)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var pageId = pageClassifier.Classify(frame)?.PageId;
            if (string.Equals(
                    pageId,
                    promptPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                stableFrames++;
            }
            else
            {
                stableFrames = 0;
                if (pageId is not null)
                {
                    return false;
                }
            }

            if (stableFrames >= 2)
            {
                Publish(
                    "IncompleteLineupPromptConfirmed",
                    "已连续两帧确认出战人数未达上限提示；仅点击一次右侧确认。",
                    TaskEventLevel.Warning);
                var clicked = await ClickStandardPointAsync(
                    windowHandle,
                    IncompleteLineupConfirmPoint,
                    "确认人数不足仍出战",
                    cancellationToken);
                Publish(
                    clicked
                        ? "IncompleteLineupConfirmationSent"
                        : "IncompleteLineupConfirmationInputFailed",
                    clicked
                        ? "已发送一次人数不足出战确认；继续验证是否进入战斗。"
                        : "人数不足出战确认输入未成功发送；不盲目重试。",
                    clicked
                        ? TaskEventLevel.Information
                        : TaskEventLevel.Warning);
                return clicked;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }

        return false;
    }

    private async Task<RewardBattlePageState> WaitForRelevantBattlePageAsync(
        nint windowHandle,
        RewardBattleStateMachine stateMachine,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + timeout;
        var stability =
            new ConsecutiveObservationTracker<RewardBattlePageState>(2);
        while (ActiveUtcNow < deadline)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            var observation = stateMachine.Observe(
                page?.PageId,
                LastPageDiagnostics());
            if (!observation.Allowed)
            {
                Publish(
                    "RewardBattleTransitionRejected",
                    observation.Message + " 未执行输入，安全停止。",
                    TaskEventLevel.Warning);
                return RewardBattlePageState.Unknown;
            }

            var current = observation.Observation;
            if (current != RewardBattlePageState.Unknown)
            {
                if (stability.Observe(current))
                {
                    stateMachine.Apply(observation);
                    if (observation.UsedContextualBattleEvidence)
                    {
                        Publish(
                            "RewardBattleContextualEvidenceAccepted",
                            "出战迁移图结合顶部战斗状态栏确认进入战斗：" +
                            $"置信度 {observation.BattleConfidence:F3}，" +
                            $"领先其他页面证据 {observation.EvidenceLead:F3}。");
                    }

                    return current;
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
        return RewardBattlePageState.Unknown;
    }

    internal Task<RewardStageAutomationResult>
        CompleteAfterSecondRewardStageAsync(
            nint windowHandle,
            IReadOnlySet<string> preferredStrategyIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferredStrategyIds);
        if (preferredStrategyIds.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publish(
                "RewardStagesCompletedForManualContinuation",
                "用户未设置投资策略条件；1-1、1-2奖励关已完成，自动输入已停止，当前对局将由同一记录器继续记录。");
            return Task.FromResult(new RewardStageAutomationResult(
                RewardStageAutomationStatus
                    .RewardStagesCompletedAwaitingManualStrategy,
                "前两个奖励关已完成并保留记录；未自动选择投资策略，现已交还用户手动继续。"));
        }

        return SelectInvestmentStrategyAsync(
            windowHandle,
            preferredStrategyIds,
            cancellationToken);
    }

    private async Task<RewardStageAutomationResult>
        SelectInvestmentStrategyAsync(
            nint windowHandle,
            IReadOnlySet<string> preferredStrategyIds,
            CancellationToken cancellationToken)
    {
        var slots = await ReadStableStrategiesAsync(
            windowHandle,
            cancellationToken);
        if (slots is null)
        {
            return await SelectFallbackStrategyForRerollAsync(
                windowHandle,
                "初始三张投资策略未能形成稳定完整识别",
                cancellationToken);
        }

        var accepted = slots.FirstOrDefault(item =>
            item.Strategy is not null &&
            preferredStrategyIds.Contains(item.Strategy.Id));
        if (accepted is not null)
        {
            return await SelectStrategyAndConfirmAsync(
                windowHandle,
                accepted.Slot,
                accepted.Strategy!.Name,
                cancellationToken);
        }

        // 快速三连刷：三张策略都不命中时，不逐张刷新后识别——
        // 直接连续点掉三张刷新按钮（中间不识别，省 2 次识别等待），
        // 全部刷新完后再统一识别一次。
        for (var slot = 0; slot < StrategyRefreshPoints.Count; slot++)
        {
            Publish(
                "RefreshInvestmentStrategy",
                $"当前三张投资策略均未命中，连续刷新第 {slot + 1} 张（三连刷不中断识别）。");
            if (!await ClickStandardPointAsync(
                    windowHandle,
                    StrategyRefreshPoints[slot],
                    $"刷新第 {slot + 1} 张投资策略",
                    cancellationToken))
            {
                return Failed(
                    RewardStageAutomationStatus.RecoveryRequested,
                    $"第 {slot + 1} 张投资策略刷新输入失败。");
            }
        }

        // 三张全部刷新后，统一稳定识别一次再判定。
        slots = await ReadStableStrategiesAsync(
            windowHandle,
            cancellationToken);
        if (slots is null)
        {
            return await SelectFallbackStrategyForRerollAsync(
                windowHandle,
                "三连刷后无法稳定识别三张投资策略",
                cancellationToken);
        }

        accepted = slots.FirstOrDefault(item =>
            item.Strategy is not null &&
            preferredStrategyIds.Contains(item.Strategy.Id));
        if (accepted is not null)
        {
            return await SelectStrategyAndConfirmAsync(
                windowHandle,
                accepted.Slot,
                accepted.Strategy!.Name,
                cancellationToken);
        }

        return new RewardStageAutomationResult(
            RewardStageAutomationStatus.InvestmentStrategyNotFound,
            "原始三张及三连刷后的三张投资策略均未命中用户要求，需要退出本局重刷。");
    }

    private async Task<RewardStageAutomationResult>
        SelectFallbackStrategyForRerollAsync(
            nint windowHandle,
            string reason,
            CancellationToken cancellationToken)
    {
        Publish(
            "InvestmentStrategyRecognitionDegraded",
            reason + "；不再因 OCR 不完整停止挂机。将选择第一槽并确认，" +
            "随后把本轮视为没有刷到目标策略并交给重开流程。",
            TaskEventLevel.Warning);
        var selection = await SelectStrategyAndConfirmAsync(
            windowHandle,
            0,
            "识别降级的第一槽投资策略",
            cancellationToken);
        if (!selection.Succeeded)
        {
            return selection;
        }

        return new RewardStageAutomationResult(
            RewardStageAutomationStatus.InvestmentStrategyNotFound,
            reason + "；已选择第一槽完成页面推进，本轮按未命中目标策略重开。");
    }

    private async Task<IReadOnlyList<InvestmentStrategySlot>?>
        ReadStableStrategiesAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(850), cancellationToken);
        string? previousSignature = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var slots = await strategyReader.ReadAsync(
                frame,
                cancellationToken);
            if (slots.Any(item => item.Strategy is null))
            {
                Publish(
                    "InvestmentStrategyRecognitionRetry",
                    $"投资策略第 {attempt}/5 次识别不完整：" +
                    string.Join(
                        "；",
                        slots.Select(item =>
                            item.Strategy?.Name ??
                            $"槽位{item.Slot + 1}=未识别({item.RawText})")));
                await Task.Delay(
                    TimeSpan.FromMilliseconds(450),
                    cancellationToken);
                continue;
            }

            var signature = string.Join(
                "|",
                slots.Select(item => item.Strategy!.Id));
            if (string.Equals(
                    previousSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                Publish(
                    "InvestmentStrategiesRecognized",
                    "投资策略识别：" +
                    string.Join(
                        "、",
                        slots.Select(item => item.Strategy!.Name)));
                return slots;
            }

            previousSignature = signature;
            await Task.Delay(
                TimeSpan.FromMilliseconds(350),
                cancellationToken);
        }

        return null;
    }

    private async Task<RewardStageAutomationResult>
        SelectStrategyAndConfirmAsync(
            nint windowHandle,
            int slot,
            string displayName,
            CancellationToken cancellationToken)
    {
        const int maximumActionAttempts = 3;
        for (var actionAttempt = 1;
             actionAttempt <= maximumActionAttempts;
             actionAttempt++)
        {
            var entry = await ReadStablePageAsync(
                windowHandle,
                cancellationToken);
            if (entry is null)
            {
                entry = await RecoverKnownRewardPageWithEscapeAsync(
                    windowHandle,
                    "InvestmentStrategyUnknownPage",
                    cancellationToken);
            }

            if (!string.Equals(
                    entry?.PageId,
                    "investment_strategy",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failed(
                    RewardStageAutomationStatus.RecoveryRequested,
                    $"选择投资策略前页面为 {entry?.PageId ?? "未知页"}，" +
                    "不是 investment_strategy；未继续点击。");
            }

            if (!await ClickStandardPointAsync(
                    windowHandle,
                    StrategyCardPoints[slot],
                    $"选择投资策略 {displayName}",
                    cancellationToken) ||
                !await ClickStandardPointAsync(
                    windowHandle,
                    StrategyConfirmPoint,
                    "确认投资策略",
                    cancellationToken))
            {
                Publish(
                    "InvestmentStrategyConfirmRetry",
                    $"投资策略“{displayName}”第 {actionAttempt}/" +
                    $"{maximumActionAttempts} 次输入未完成，重新识别并重试。",
                    TaskEventLevel.Warning);
                continue;
            }

            string? previousPostPage = null;
            var departedFrames = 0;
            for (var verification = 1; verification <= 20; verification++)
            {
                var (_, frame) = await CaptureForegroundAsync(
                    windowHandle,
                    cancellationToken);
                var page = pageClassifier.Classify(frame);
                if (string.Equals(
                        page?.PageId,
                        "investment_strategy",
                        StringComparison.OrdinalIgnoreCase))
                {
                    departedFrames = 0;
                    previousPostPage = null;
                }
                else if (page is not null)
                {
                    departedFrames = string.Equals(
                        previousPostPage,
                        page.PageId,
                        StringComparison.OrdinalIgnoreCase)
                            ? departedFrames + 1
                            : 1;
                    previousPostPage = page.PageId;
                    if (departedFrames >= 2)
                    {
                        Publish(
                            "InvestmentStrategyPostPageReached",
                            $"投资策略确认后稳定进入 {page.PageId}。");
                        return new RewardStageAutomationResult(
                            RewardStageAutomationStatus.InvestmentStrategySelected,
                            $"已选择投资策略“{displayName}”并确认，前两层奖励关自动流程完成。");
                    }
                }
                else
                {
                    departedFrames = 0;
                    previousPostPage = null;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }

            Publish(
                "InvestmentStrategyConfirmRetry",
                $"第 {actionAttempt}/{maximumActionAttempts} 次确认后未观察到" +
                "稳定已知后置页面，重新识别后重试。",
                TaskEventLevel.Warning);
        }

        return Failed(
            RewardStageAutomationStatus.RecoveryRequested,
            $"投资策略“{displayName}”已执行 {maximumActionAttempts} 次选择/确认，" +
            "仍未观察到稳定已知后置页面；交由安全退出/重刷流程恢复。");
    }
}
