using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」在 <see cref="PreparationBoardController"/> 上的新增适配动作
/// （定稿决策树 N12/N13/N14/N17a 与 S1A/S1B 卖人）。
/// 机制依据（用户 2026-08-29 确认）：拖到空位=移动，拖到有人位置=互换；
/// 上场=拖到前台/后台空位；N12 最左置位=拖到备战席第 0 格（有人自动互换）。
/// </summary>
public sealed partial class PreparationBoardController
{
    /// <summary>把一名备战席角色拖上场（前台/后台指定槽位，既有 DeployWithVerificationAsync 带验证）。</summary>
    internal async Task<bool> GrailDeployBenchCharacterAsync(
        nint windowHandle,
        RecognizedBenchCharacter candidate,
        PreparationLane lane,
        int targetSlot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        return await DeployWithVerificationAsync(
            windowHandle,
            new PreparationPlacement(candidate, lane, targetSlot),
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>
    /// N12：把一名备战席角色拖到备战席最左侧一格（第 0 格）——拖到有人位置自动互换。
    /// 机制依据（定稿树 N12）：采购专员/全是这家伙的错 按备战席最左角色费用刷牌，
    /// 5 费角色必须放最左；已是最左（BenchSlot==0）直接返回。
    /// 复用既有 DeployWithVerificationAsync 的验证基建（源槽/目标槽像素差分）。
    /// </summary>
    internal async Task<bool> GrailMoveToLeftmostBenchAsync(
        nint windowHandle,
        RecognizedBenchCharacter candidate,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        if (candidate.BenchSlot == 0)
        {
            return true; // 已是最左侧一格
        }

        var sourceReference = BenchSlots[candidate.BenchSlot];
        var targetReference = BenchSlots[0];
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: attempt == 1,
                cancellationToken);
            if (captured is null)
            {
                return false;
            }

            var window = captured.Value.Window;
            var before = captured.Value.Frame;
            var sourcePoint = MapReferencePoint(window, sourceReference.Center);
            var targetPoint = MapReferencePoint(window, targetReference.Center);
            Publish(
                TaskEventLevel.Information,
                "GrailMoveToLeftmostAttempt",
                $"N12 把“{candidate.Character.Name}”从备战席{candidate.BenchSlot + 1}号位拖到最左侧第 1 格" +
                $"（第 {attempt}/5 次）。");
            var drag = await input.DragAsync(
                new ClickTarget(
                    $"grail_leftmost_{candidate.Character.Id}",
                    $"N12 {candidate.Character.Name} 最左置位",
                    window,
                    BoundsAround(window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                continue;
            }

            var verification = await VerifyMoveAsync(
                windowHandle,
                before,
                sourceReference,
                targetReference,
                expectedPreparationPageId,
                cancellationToken);
            if (verification.MoveObserved)
            {
                Publish(
                    TaskEventLevel.Information,
                    "GrailMoveToLeftmostVerified",
                    $"已确认“{candidate.Character.Name}”移动到备战席最左侧：" +
                    $"源槽变化 {verification.SourceDifference:F1}，目标槽变化 {verification.TargetDifference:F1}。");
                return true;
            }

            if (!verification.DefinitelyUnchanged)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailMoveToLeftmostAmbiguous",
                    $"“{candidate.Character.Name}”最左置位后的源槽/目标槽变化不完整" +
                    $"（{verification.SourceDifference:F1}/{verification.TargetDifference:F1}）；" +
                    "停止本次尝试，交由下一帧快照复核。");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// N2：把物品栏一枚命运圣杯星徽拖到备战席指定角色卡上（装备装配）。
    /// 源点 = 物品栏星徽区域中心（识别层 0..1 相对窗口客户区）；目标 = 备战席角色槽中心。
    /// 装备拖拽没有现成“空槽双帧验证”基建，输入成功即算成功（最多 3 次重试）；
    /// 若实际未装配，外层 1-3 循环下一帧快照仍会看到物品栏星徽并再次触发（与
    /// <see cref="GrailSellDeployedCharacterAsync"/> 同一自限模式）。
    /// </summary>
    internal async Task<bool> GrailDragBadgeToBenchCharacterAsync(
        nint windowHandle,
        RelativeRegion badgeRegion,
        RecognizedBenchCharacter target,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        return await GrailDragBadgeToReferenceAsync(
            windowHandle,
            badgeRegion,
            BenchSlots[target.BenchSlot],
            $"备战席{target.BenchSlot}号位",
            $"bench_{target.BenchSlot}",
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>把星徽拖到【已上场】的前台角色（2026-09-02 用户拍板：星徽只装给已上场角色，装给备战席角色=白装）。</summary>
    internal async Task<bool> GrailDragBadgeToFrontCharacterAsync(
        nint windowHandle,
        RelativeRegion badgeRegion,
        int frontSlot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        return await GrailDragBadgeToReferenceAsync(
            windowHandle,
            badgeRegion,
            FrontSlots[frontSlot],
            $"前台{frontSlot + 1}号位角色",
            $"front_{frontSlot}",
            expectedPreparationPageId,
            cancellationToken);
    }

    // 旧版 DetectBadgePanelCentroid（2560 基准窄条带+全局质心）已于 1.2.26 废除，
    // 星徽定位统一走 StarBadgeLocator（1920×1080 基准+全面板+连通簇+位置先验+模板判别，坑 35）。

    /// <summary>卖出验证：源卡位区域拖拽前后平均像素差（0..255）。</summary>
    private static double SellRegionMeanDelta(
        CaptureFrame before,
        CaptureFrame after,
        PixelRect frameRect)
    {
        if (before.Width != after.Width || before.Height != after.Height || before.Stride != after.Stride)
        {
            return double.PositiveInfinity;
        }

        var x0 = Math.Max(0, frameRect.X);
        var y0 = Math.Max(0, frameRect.Y);
        var x1 = Math.Min(before.Width - 1, frameRect.X + frameRect.Width);
        var y1 = Math.Min(before.Height - 1, frameRect.Y + frameRect.Height);
        long total = 0;
        var count = 0;
        for (var y = y0; y <= y1; y++)
        {
            var rowBase = y * before.Stride;
            for (var x = x0; x <= x1; x++)
            {
                var index = rowBase + x * 4;
                total += Math.Abs(before.BgraPixels[index] - after.BgraPixels[index])
                       + Math.Abs(before.BgraPixels[index + 1] - after.BgraPixels[index + 1])
                       + Math.Abs(before.BgraPixels[index + 2] - after.BgraPixels[index + 2]);
                count++;
            }
        }

        return count == 0 ? 0 : total / (double)count / 3.0;
    }


    /// <summary>把星徽拖到后台指定槽位角色（1.2.25 位置语义：槽位=固定 BackSlots 后台几何
    /// ——审查 P1 修正：绝不能用 BenchSlots 备战席表，装给备战席=白装；源点同前台版=物品栏全面板探测）。</summary>
    internal async Task<bool> GrailDragBadgeToBenchSlotAsync(
        nint windowHandle,
        int backSlot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        return await GrailDragBadgeToReferenceAsync(
            windowHandle,
            new RelativeRegion(0, 0, 0, 0),
            BackSlots[backSlot],
            $"后台{backSlot + 1}号位角色",
            $"back_{backSlot}",
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>星徽拖拽本体（1.2.25 定稿）：源点=物品栏全面板金色质心探测（探测不到=诚实失败，
    /// 定标盲拖已废除）；目标=固定槽位几何；拖后重扫面板自证装配成功与否。</summary>
    private async Task<bool> GrailDragBadgeToReferenceAsync(
        nint windowHandle,
        RelativeRegion badgeRegion,
        PixelRect targetReference,
        string targetLabel,
        string targetId,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        // badgeRegion 参数已废弃：2026-09-03 用户定稿——探测不到星徽绝不回退定标盲拖
        //（1.2.22 实测：探测未命中→回退定标点盲拖→谎报 OK，星徽装到换人动画中的角色=白装）。
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: attempt == 1,
                cancellationToken);
            if (captured is null)
            {
                return false;
            }

            var window = captured.Value.Window;
            if (!StarBadgeLocator.TryLocate(captured.Value.Frame, out var badgeCenter, out var badgeScore))
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailBadgePanelProbe",
                    $"物品栏未探测到命运圣杯星徽（第 {attempt}/3 次）——无星徽即无拖拽，诚实失败（定标盲拖已废除）。");
                return false;
            }

            var sourcePoint = badgeCenter;
            Publish(
                TaskEventLevel.Information,
                "GrailBadgePanelProbe",
                $"物品栏星徽探测命中（模板分 {badgeScore:F2}），质心=({badgeCenter.X},{badgeCenter.Y}) 客户区坐标。");
            var targetPoint = MapReferencePoint(window, targetReference.Center);
            Publish(
                TaskEventLevel.Information,
                "GrailBadgeAssemblyAttempt",
                $"N2 把物品栏命运圣杯星徽拖到{targetLabel}上（第 {attempt}/3 次）。");
            var drag = await input.DragAsync(
                new ClickTarget(
                    $"grail_badge_to_{targetId}",
                    $"N2 星徽装配到{targetLabel}",
                    window,
                    BoundsAround(window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    // 星徽=物品栏图标抓取：按下后需停留一拍让游戏把物品吸附到光标
                    //（2026-09-02 实测两次即时拖拽均未带走星徽；角色卡拖拽不受影响）。
                    MouseButtonHoldDelay = TimeSpan.FromMilliseconds(250),
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailBadgeAssemblyInputRejected",
                    $"N2 星徽装配第 {attempt}/3 次输入未发送成功：" + drag.Message);
                continue;
            }

            // 拖后自证（审查 P2）：连续两帧（间隔 300ms）均未定位到星徽=装配成功；
            // 单帧阴性可能被动画/悬浮窗遮挡误判，不得据此谎报成功；任一帧仍在=未装上重试。
            var badgeStillVisible = false;
            foreach (var checkDelay in new[] { 300, 300 })
            {
                await Task.Delay(TimeSpan.FromMilliseconds(checkDelay), cancellationToken);
                var check = await CaptureVerifiedPreparationAsync(
                    windowHandle,
                    expectedPreparationPageId,
                    allowEscapeRecovery: false,
                    cancellationToken);
                if (check is null)
                {
                    Publish(
                        TaskEventLevel.Warning,
                        "GrailBadgeAssemblyRetry",
                        "拖后截图失败，装配结果未知——不谎报成功，交由外层快照复核。");
                    return false;
                }

                if (StarBadgeLocator.TryLocate(check.Value.Frame, out var remainingCenter, out var remainingScore))
                {
                    badgeStillVisible = true;
                    Publish(
                        TaskEventLevel.Warning,
                        "GrailBadgeAssemblyRetry",
                        $"拖后星徽仍在物品栏（质心=({remainingCenter.X},{remainingCenter.Y})，模板分 {remainingScore:F2}），第 {attempt}/3 次未装上，重试。");
                    break;
                }
            }

            if (badgeStillVisible)
            {
                continue;
            }

            Publish(
                TaskEventLevel.Information,
                "GrailBadgeAssemblyVerified",
                "连续两帧未在物品栏定位到星徽，判定装配成功。");
            return true;
        }

        return false;
    }

    /// <summary>出售一名指定备战席角色（既有 SellCharacterWithVerificationAsync：拖到出售区+双帧空槽验证）。</summary>
    internal async Task<bool> GrailSellBenchCharacterAsync(
        nint windowHandle,
        RecognizedBenchCharacter candidate,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        return await SellCharacterWithVerificationAsync(
            windowHandle,
            candidate,
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>
    /// 出售一名场上（前台/后台）角色：把卡牌从识别区域中心直接拖到既有出售区
    /// （<see cref="SellTargetPoints"/> 左右交替，用户拍板卖法）。
    /// 场上区域没有备战席的“空槽双帧验证”基建，拖拽输入成功即算成功（最多 3 次重试）；
    /// 若实际未卖掉，外层 1-3 循环下一帧快照仍会看到该角色并再次触发卖出（自限）。
    /// </summary>
    internal async Task<bool> GrailSellDeployedCharacterAsync(
        nint windowHandle,
        GrailDeployedCharacter candidate,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        if (candidate.CardRegion is not { } region)
        {
            return false;
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: attempt == 1,
                cancellationToken);
            if (captured is null)
            {
                return false;
            }

            // CardRegion 是 0..1 相对窗口客户区的卡牌区域（识别层 ToRelative 回填），
            // 拖拽起点 = 卡牌区域中心映射到窗口像素。
            var sourcePoint = new PixelPoint(
                (int)Math.Round((region.X + region.Width / 2) * captured.Value.Window.ClientArea.Width),
                (int)Math.Round((region.Y + region.Height / 2) * captured.Value.Window.ClientArea.Height));
            var targetIndex = (attempt - 1) % SellTargetPoints.Count;
            var targetPoint = MapReferencePoint(
                captured.Value.Window,
                SellTargetPoints[targetIndex]);
            Publish(
                TaskEventLevel.Information,
                "GrailDeployedSaleAttempt",
                $"出售场上“{candidate.Name}”：第 {attempt}/3 次拖到" +
                $"{(targetIndex == 0 ? "左侧" : "右侧")}出售区。");
            var drag = await input.DragAsync(
                new ClickTarget(
                    $"grail_deployed_sell_{candidate.Name}",
                    $"出售场上{candidate.Name}",
                    captured.Value.Window,
                    BoundsAround(captured.Value.Window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailDeployedSaleInputRejected",
                    $"出售场上“{candidate.Name}”第 {attempt}/3 次输入未发送成功：" + drag.Message);
                continue;
            }

            // 卖出验证（2026-09-02 补：两次"输入成功"实未卖出的教训——识别卡位残留已修，
            // 但场上无空槽双帧验证基建，用源卡位区域拖拽前后像素差自证，无变化=未卖出重试）。
            var after = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: false,
                cancellationToken);
            if (after is null)
            {
                return false;
            }

            var saleRect = new PixelRect(
                (int)Math.Round(region.X * after.Value.Frame.Width),
                (int)Math.Round(region.Y * after.Value.Frame.Height),
                Math.Max(8, (int)Math.Round(region.Width * after.Value.Frame.Width)),
                Math.Max(8, (int)Math.Round(region.Height * after.Value.Frame.Height)));
            var saleDelta = SellRegionMeanDelta(
                captured.Value.Frame, after.Value.Frame, saleRect);
            if (saleDelta < 6.0)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailDeployedSaleNotVerified",
                    $"出售场上“{candidate.Name}”第 {attempt}/3 次拖拽后卡位无变化（差分 {saleDelta:F1}），判定未卖出，重试。");
                continue;
            }

            Publish(
                TaskEventLevel.Information,
                "GrailDeployedSaleVerified",
                $"已确认场上“{candidate.Name}”卡位变化（差分 {saleDelta:F1}），判定卖出成功。");
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 卖光卖人（S1A/S1B）：先卖备战席候选（既有验证路径），再卖场上候选（拖出售区），
    /// **不因凑够 targetGold 提前停**（用户拍板：全部卖光；星徽携带者/5 费/命杯的保护
    /// 已在组装器完成，本方法只执行）。返回实际所得金币与是否达成目标。
    /// </summary>
    internal async Task<GrailSellResult> GrailSellAllAsync(
        nint windowHandle,
        IReadOnlyList<RecognizedBenchCharacter> benchCandidates,
        IReadOnlyList<int> benchGoldPerSale,
        IReadOnlyList<GrailDeployedCharacter> deployedCandidates,
        int currentGold,
        int targetGold,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var sold = 0;
        var gold = currentGold;
        for (var index = 0; index < benchCandidates.Count; index++)
        {
            var ok = await SellCharacterWithVerificationAsync(
                windowHandle,
                benchCandidates[index],
                expectedPreparationPageId,
                cancellationToken);
            if (!ok)
            {
                break;
            }

            gold += index < benchGoldPerSale.Count ? benchGoldPerSale[index] : 0;
            sold++;
        }

        for (var index = 0; index < deployedCandidates.Count; index++)
        {
            var ok = await GrailSellDeployedCharacterAsync(
                windowHandle,
                deployedCandidates[index],
                expectedPreparationPageId,
                cancellationToken);
            if (!ok)
            {
                break;
            }

            gold += deployedCandidates[index].SaleValue;
            sold++;
        }

        return new GrailSellResult(sold, gold, gold >= targetGold);
    }
}

/// <summary>TargetGold 卖人结果。</summary>
public sealed record GrailSellResult(int SoldCount, int EstimatedGold, bool TargetReached);
