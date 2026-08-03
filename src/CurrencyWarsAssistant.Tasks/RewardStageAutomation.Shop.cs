using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

public sealed partial class RewardStageAutomationController
{
    private async Task<bool> OpenShopAsync(
        nint windowHandle,
        string expectedPreparationPage,
        CancellationToken cancellationToken)
    {
        var current = await ReadStablePageAsync(
            windowHandle,
            cancellationToken);
        if (current is null)
        {
            current = await RecoverKnownRewardPageWithEscapeAsync(
                windowHandle,
                "OpenRewardShopUnknownPage",
                cancellationToken);
        }

        if (current is { PageId: "reward_shop" })
        {
            return true;
        }

        if (!string.Equals(
                current?.PageId,
                expectedPreparationPage,
                StringComparison.OrdinalIgnoreCase))
        {
            Publish(
                "OpenRewardShopPageMismatch",
                $"打开商店前页面为 {current?.PageId ?? "未知页"}，" +
                $"预期 {expectedPreparationPage}；" +
                "三次 Esc 恢复后仍不满足输入条件。",
                TaskEventLevel.Warning);
            return false;
        }

        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            Publish(
                "OpenRewardShop",
                $"从 {expectedPreparationPage} 打开商店：第 {attempt} 次尝试。");
            if (!await ClickStandardPointAsync(
                    windowHandle,
                    ShopTogglePoint,
                    "打开商店",
                    cancellationToken))
            {
                continue;
            }

            if (await WaitForPageAsync(
                    windowHandle,
                    "reward_shop",
                    TimeSpan.FromSeconds(5),
                    cancellationToken))
            {
                return true;
            }
        }

        Publish(
            "OpenRewardShopFailed",
            $"打开商店达到 {maximumAttempts} 次上限；未继续盲点输入。",
            TaskEventLevel.Warning);
        return false;
    }

    internal async Task<bool> CloseShopAsync(
        nint windowHandle,
        string expectedPreparationPage,
        CancellationToken cancellationToken)
    {
        var current = await ReadStablePageAsync(
            windowHandle,
            cancellationToken);
        if (string.Equals(
                current?.PageId,
                expectedPreparationPage,
                StringComparison.OrdinalIgnoreCase))
        {
            Publish(
                "RewardShopAlreadyClosed",
                $"商店购买后已自动返回 {expectedPreparationPage}；不再点击商店开关。");
            return true;
        }

        if (current is null)
        {
            Publish(
                "CloseRewardShopMonitoringTransition",
                "批量购买后的页面仍在过渡动画或暂时未知；持续监测商店/备战页，" +
                "不按 Esc、不复核旧槽位、不停止奖励关。",
                TaskEventLevel.Warning);
            var transitionDeadline =
                ActiveUtcNow + RewardShopPurchaseTiming.VerificationTimeout;
            do
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(300),
                    cancellationToken);
                current = await ReadStablePageAsync(
                    windowHandle,
                    cancellationToken);
            }
            while (current is null && ActiveUtcNow < transitionDeadline);

            if (current is null)
            {
                Publish(
                    "CloseRewardShopTransitionTimedOut",
                    "The page transition after closing the shop did not " +
                    "stabilize within the bounded wait; input has stopped.",
                    TaskEventLevel.Warning);
                return false;
            }

            if (string.Equals(
                    current?.PageId,
                    expectedPreparationPage,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (current?.PageId != "reward_shop")
        {
            Publish(
                "CloseRewardShopPageMismatch",
                $"收起商店前页面为 {current?.PageId ?? "未知页"}；未发送商店开关输入。",
                TaskEventLevel.Warning);
            return false;
        }

        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            if (!await ClickStandardPointAsync(
                    windowHandle,
                    ShopTogglePoint,
                    "收起商店",
                    cancellationToken))
            {
                continue;
            }

            if (await WaitForPageAsync(
                    windowHandle,
                    expectedPreparationPage,
                    TimeSpan.FromSeconds(5),
                    cancellationToken))
            {
                return true;
            }

            Publish(
                "CloseRewardShopRetry",
                $"第 {attempt} 次收起商店后未识别到 {expectedPreparationPage}，准备重试。",
                TaskEventLevel.Warning);
        }

        return false;
    }

    private async Task<bool> PurchaseShopCharactersAsync(
        nint windowHandle,
        string expectedPreparationPage,
        RewardStageAutomationOptions options,
        ISet<string> presetPurchaseSuppressedNames,
        ISet<string> formationReservedNames,
        IDictionary<string, CurrencyWarsCharacterData> ownedCharacters,
        ISet<string> shopPurchasedRetentionNames,
        bool allowGalaxyScholarPairPurchase,
        CancellationToken cancellationToken)
    {
        // One snapshot, one plan, one trusted click per planned slot. Purchased
        // empty slots must never start another recognition transaction.
        var batchSnapshot = await ReadStableShopAsync(
            windowHandle,
            consumedSlots: null,
            cancellationToken);
        if (batchSnapshot is null)
        {
            Publish(
                "ShopBatchSnapshotUnavailable",
                "本轮未取得稳定商店快照；跳过可选购买并继续后续页面接管，不停止奖励关。",
                TaskEventLevel.Warning);
            return true;
        }

        var batchDecisions = shopPurchasePlanner.Plan(
                batchSnapshot,
                options,
                presetPurchaseSuppressedNames,
                formationReservedNames,
                ownedCharacters.Values,
                allowGalaxyScholarPairPurchase)
            .GroupBy(item => item.Slot.Slot)
            .Select(group => group.First())
            .OrderBy(item => item.Slot.Slot)
            .ToArray();
        Publish(
            "ShopBatchPurchasePlanned",
            batchDecisions.Length == 0
                ? "一次稳定快照中没有购买目标；不再重复读取商店。"
                : $"一次稳定快照规划 {batchDecisions.Length} 个购买槽位；" +
                  "每个槽位只点击一次，批次内不复核空槽、不重新识别或规划。");
        var batchDeadline =
            ActiveUtcNow + RewardShopPurchaseTiming.MaximumBatchDuration;

        foreach (var decision in batchDecisions)
        {
            if (ActiveUtcNow >= batchDeadline)
            {
                Publish(
                    "ShopBatchPurchaseBudgetExhausted",
                    $"商店购买批次达到 " +
                    $"{RewardShopPurchaseTiming.MaximumBatchDuration.TotalSeconds:F0} 秒总预算；" +
                    "未继续点击剩余目标，准备退出商店。",
                    TaskEventLevel.Warning);
                break;
            }

            var purchaseInput = await ClickShopCardAsync(
                windowHandle,
                decision.Slot.Slot,
                decision.Character.Name,
                cancellationToken);
            if (!purchaseInput.Succeeded)
            {
                Publish(
                    "ShopBatchPurchaseInputSkipped",
                    $"{decision.Character.Name} 的购买输入未成功发送；" +
                    "没有发送购买点击，不重试旧坐标，继续下一目标。" +
                    $"原因：{purchaseInput.Message}",
                    TaskEventLevel.Warning);
                continue;
            }

            var verification = await VerifyShopPurchaseAsync(
                windowHandle,
                batchSnapshot,
                decision,
                expectedPreparationPage,
                batchDeadline,
                cancellationToken);
            if (verification.Postcondition ==
                RewardShopPurchasePostcondition.NotPurchased)
            {
                Publish(
                    "ShopPurchaseNotCompleted",
                    $"Slot {decision.Slot.Slot + 1} still contained " +
                    $"{decision.Character.Name} for two consecutive frames. " +
                    "Treating this as insufficient currency or an ineffective " +
                    "input; no more purchases will be attempted in this shop.",
                    TaskEventLevel.Warning);
                await RecoverStoppedPurchaseContextAsync(
                    windowHandle,
                    verification,
                    expectedPreparationPage,
                    cancellationToken);
                break;
            }

            if (!verification.Confirmed)
            {
                shopPurchasedRetentionNames.Add(decision.Character.Name);
                Publish(
                    "ShopPurchasePostconditionUncertain",
                    $"The purchase result for slot {decision.Slot.Slot + 1} " +
                    "could not be confirmed within the bounded verification " +
                    "window. The batch has stopped to prevent repeat input.",
                    TaskEventLevel.Warning);
                await RecoverStoppedPurchaseContextAsync(
                    windowHandle,
                    verification,
                    expectedPreparationPage,
                    cancellationToken);
                break;
            }

            ownedCharacters.TryAdd(
                decision.Character.Name,
                decision.Character);

            if (decision.IsPresetCandidate)
            {
                presetPurchaseSuppressedNames.Add(decision.Character.Name);
            }

            if (decision.IsFormationCandidate)
            {
                formationReservedNames.Add(decision.Character.Name);
            }

            // A trusted click protects against selling/replanning. This path
            // deliberately does not claim that the game confirmed ownership.
            shopPurchasedRetentionNames.Add(decision.Character.Name);
            if (verification.ShopAutomaticallyClosed)
            {
                Publish(
                    "ShopPurchaseConfirmedAfterAutomaticClose",
                    $"Purchase of {decision.Character.Name} was confirmed by " +
                    $"the stable {expectedPreparationPage} page.");
                break;
            }
            Publish(
                "ShopBatchPurchaseClickedProtected",
                $"已对槽位 {decision.Slot.Slot + 1} 的 {decision.Character.Name} " +
                "发送一次可信点击；已加入出售保护，未虚假记录为已确认拥有。");
        }

        return true;
    }

    private async Task<RewardShopPurchaseVerification> VerifyShopPurchaseAsync(
        nint windowHandle,
        IReadOnlyList<RewardShopSlot> beforePurchase,
        RewardShopPurchaseDecision decision,
        string expectedPreparationPage,
        DateTimeOffset batchDeadline,
        CancellationToken cancellationToken)
    {
        var tracker = new RewardShopPurchasePageTracker(
            decision.Slot.Slot,
            decision.Character.Id,
            expectedPreparationPage);
        var deadline = ActiveUtcNow + RewardShopPurchaseTiming.VerificationTimeout;
        if (batchDeadline < deadline)
        {
            deadline = batchDeadline;
        }
        var latest = new RewardShopPurchaseVerification(
            RewardShopPurchasePostcondition.Uncertain,
            null);
        var observation = 0;

        while (ActiveUtcNow < deadline)
        {
            observation++;
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            IReadOnlyList<RewardShopSlot>? slots = null;
            var pageIdForTracker = page?.PageId;
            if (page is null || string.Equals(
                    page.PageId,
                    "reward_shop",
                    StringComparison.OrdinalIgnoreCase))
            {
                var observed = await shopReader.ReadAsync(
                    frame,
                    cancellationToken);
                if (RewardShopPurchaseContextPolicy.CanUseObservation(
                        page?.PageId,
                        beforePurchase,
                        observed,
                        decision.Slot.Slot))
                {
                    slots = observed;
                    pageIdForTracker = "reward_shop";
                }
            }

            latest = tracker.Observe(pageIdForTracker, slots);
            if (page is null && slots is not null)
            {
                latest = latest with { PageId = null };
            }
            var observedCharacter = slots?
                .FirstOrDefault(item => item.Slot == decision.Slot.Slot)
                ?.Character?.Name;
            Publish(
                "ShopPurchaseVerificationObserved",
                $"槽位={decision.Slot.Slot + 1}；角色={decision.Character.Name}；" +
                $"后置帧={observation}；截图={frame.Width}x{frame.Height}；" +
                $"页面={page?.PageId ?? "Unknown"}；" +
                $"原槽识别={observedCharacter ?? "空/未识别"}；" +
                $"成交状态={latest.Postcondition}。",
                TaskEventLevel.Information);
            if (latest.Postcondition !=
                RewardShopPurchasePostcondition.Uncertain)
            {
                return latest;
            }

            await Task.Delay(
                RewardShopPurchaseTiming.VerificationPollInterval,
                cancellationToken);
        }

        return latest;
    }

    private async Task RecoverStoppedPurchaseContextAsync(
        nint windowHandle,
        RewardShopPurchaseVerification verification,
        string expectedPreparationPage,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                verification.PageId,
                "reward_shop",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                verification.PageId,
                expectedPreparationPage,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (verification.PageId is not null)
        {
            Publish(
                "ShopPurchaseRecoverySkippedForKnownPage",
                $"购买停止后识别到 {verification.PageId}；" +
                "未发送商店上下文 Esc，交由外层状态机处理。",
                TaskEventLevel.Warning);
            return;
        }

        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        Publish(
            "ShopPurchaseOverlayDismissRequested",
            "购买结果停止于 Unknown；只发送一次 Esc，" +
            "用于关闭金币不足/购买失败遮罩或收起商店，随后重新识别。",
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
            Publish(
                "ShopPurchaseOverlayDismissInputFailed",
                action.Message,
                TaskEventLevel.Warning);
            return;
        }

        var recovered = await ReadStablePageAsync(
            windowHandle,
            cancellationToken);
        var accepted = string.Equals(
                recovered?.PageId,
                "reward_shop",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                recovered?.PageId,
                expectedPreparationPage,
                StringComparison.OrdinalIgnoreCase);
        Publish(
            accepted
                ? "ShopPurchaseOverlayDismissed"
                : "ShopPurchaseOverlayDismissUnconfirmed",
            accepted
                ? $"一次 Esc 后已稳定识别到 {recovered!.PageId}；不再重复清理输入。"
                : "一次 Esc 后仍未稳定识别商店或备战页；不再重复输入，交由外层恢复。",
            accepted ? TaskEventLevel.Information : TaskEventLevel.Warning);
    }

    private async Task<IReadOnlyList<RewardShopSlot>?> ReadStableShopAsync(
        nint windowHandle,
        IReadOnlySet<int>? consumedSlots,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken);
        var accumulator = new RewardShopRecognitionAccumulator(
            ignoredSlots: consumedSlots);
        for (var attempt = 1;
             attempt <= RewardShopBatchSnapshotPolicy.MaximumObservations;
             attempt++)
        {
            var (_, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            if (page?.PageId != "reward_shop")
            {
                Publish(
                    "ShopRecognitionPageChanged",
                    $"商店批处理快照第 {attempt}/" +
                    $"{RewardShopBatchSnapshotPolicy.MaximumObservations} 帧时页面为 " +
                    $"{page?.PageId ?? "未知页"}；" +
                    "不读取旧槽位坐标。",
                    TaskEventLevel.Warning);
                return null;
            }

            var slots = await shopReader.ReadAsync(
                frame,
                cancellationToken);
            accumulator.Observe(slots);
            Publish(
                "ShopRecognitionObserved",
                $"商店批处理快照第 {attempt}/" +
                $"{RewardShopBatchSnapshotPolicy.MaximumObservations} 帧：" +
                string.Join(
                    "；",
                    slots.Select(item =>
                        item.Character?.Name ??
                        $"槽位{item.Slot + 1}=未识别({item.RawText})")));
            if (attempt < RewardShopBatchSnapshotPolicy.MaximumObservations)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(350),
                    cancellationToken);
            }
        }

        var stable = accumulator.Snapshot();
        if (stable
            .Where(item => consumedSlots?.Contains(item.Slot) != true)
            .All(item => item.Character is null))
        {
            Publish(
                "ShopRecognitionEmptySnapshotAccepted",
                "两帧批处理快照中没有稳定角色；空槽属于合法结果，" +
                "本轮规划为空并继续奖励关，不追加识别轮次。",
                TaskEventLevel.Warning);
        }

        var unknown = stable
            .Where(item => item.Character is null &&
                           consumedSlots?.Contains(item.Slot) != true)
            .Select(item => item.Slot + 1)
            .ToArray();
        if (unknown.Length > 0)
        {
            Publish(
                "ShopRecognitionPartial",
                $"商店槽位 {string.Join("、", unknown)} 未能稳定识别。" +
                "自动购买属于可选增强，本轮跳过这些槽位并继续奖励关，避免挂机中断。",
                TaskEventLevel.Warning);
        }

        Publish(
            "ShopRecognized",
            "商店识别：" +
            string.Join(
                "、",
                stable.Select(item =>
                    item.Character?.Name ?? "未识别")));
        return stable;
    }

    private async Task OpenMineBallsAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        for (var pass = 1; pass <= 3; pass++)
        {
            var (window, frame) = await CaptureForegroundAsync(
                windowHandle,
                cancellationToken);
            var mines = visualDetector.FindMineBalls(frame);
            if (mines.Count == 0)
            {
                return;
            }

            Publish(
                "MineBallsDetected",
                $"第 {pass} 轮检测到 {mines.Count} 个可开启晶矿球。");
            foreach (var mine in mines)
            {
                var point = MapStandardPoint(window, mine);
                var click = await input.ClickAsync(
                    new ClickTarget(
                        "open_mine_ball",
                        "开启晶矿球",
                        window,
                        BoundsAround(window, point)),
                    new ActionPolicy
                    {
                        AfterActionDelay =
                            TimeSpan.FromMilliseconds(250)
                    },
                    cancellationToken);
                if (!click.Succeeded)
                {
                    Publish(
                        "MineBallClickFailed",
                        click.Message,
                        TaskEventLevel.Warning);
                }
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }
    }

    private async Task<bool> OpenMineBallsWithCapacityGuardAsync(
        nint windowHandle,
        IReadOnlyList<PreparationPlacement> existingPlacements,
        PreparationBoardOptions options,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var capacity = await preparationCompletionController
            .EnsureMineCapacityAsync(
                windowHandle,
                existingPlacements,
                options,
                expectedPreparationPageId,
                cancellationToken);
        Publish(
            capacity.CanOpenMine
                ? "MineCapacityAvailable"
                : "MineBallsSkippedBenchFull",
            capacity.Message,
            capacity.CanOpenMine
                ? TaskEventLevel.Information
                : TaskEventLevel.Warning);
        if (capacity.CanOpenMine)
        {
            await OpenMineBallsAsync(windowHandle, cancellationToken);
            return true;
        }

        return false;
    }

    private async Task SynchronizeOwnedCharactersAfterMineAsync(
        nint windowHandle,
        string expectedPreparationPageId,
        RewardStageAutomationOptions options,
        ISet<string> presetPurchaseSuppressedNames,
        IDictionary<string, CurrencyWarsCharacterData> ownedCharacters,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
        var bench = await preparationCompletionController
            .ReadStableBenchCharactersAsync(
                windowHandle,
                expectedPreparationPageId,
                cancellationToken);
        if (bench is null)
        {
            Publish(
                "MineOwnershipSynchronizationSkipped",
                $"{expectedPreparationPageId} 晶矿动画后未能稳定读取备战席；" +
                "未据此声称获得任何角色，后续仍按现有安全边界处理。",
                TaskEventLevel.Warning);
            return;
        }

        var newlySuppressed = RewardMineOwnershipPolicy.Synchronize(
            bench,
            options.EnableEarlyStrongFormationPurchase,
            presetPurchaseSuppressedNames,
            ownedCharacters);

        Publish(
            "MineOwnershipSynchronized",
            $"{expectedPreparationPageId} 晶矿后已连续稳定读取备战席；" +
            $"当前确认角色=[{string.Join("、", bench.Select(item => item.Character.Name))}]；" +
            (newlySuppressed.Count > 0
                ? $"三仙舟/DOT 后续商店去重新增=[{string.Join("、", newlySuppressed)}]。"
                : "没有新增三仙舟/DOT 商店去重名称。"));
    }
}
