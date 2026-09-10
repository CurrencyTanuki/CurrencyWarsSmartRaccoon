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
    /// <summary>星徽装配取证帧落盘（1.2.106 用户令"拿出装上的证据"）：装配成功/失败
    /// 的前后帧各存一张到 %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\badge-evidence\，
    /// 保存异常绝不影响装配主流程。</summary>
    private static void SaveBadgeEvidence(
        CaptureFrame frame,
        string tag)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CurrencyWarsSmartRaccoon",
                "badge-evidence");
            Directory.CreateDirectory(directory);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{tag}.png";
            frame.SavePng(Path.Combine(directory, name));
        }
        catch
        {
            // 取证保存失败不影响装配。
        }
    }

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
                    // 09-10 夜审 P1-B：N12 拖拽同样漏接坑44 按压，统一 250ms。
                    MouseButtonHoldDelay = TimeSpan.FromMilliseconds(250),
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

            // 指南风暴班星徽专项（09-10）：1.2.126 会话装配失败时拖拽诊断与成功例
            // 字节级零差异（同 700ms/1100ms/同落点/同页面 preparation）——121 审计
            // "字段零差异不可定案"在 700ms 时代依旧。既定对策（121 审计建议）实施：
            // 重试时源点 ±30px 横移换抓取点（同一星徽内换像素位置按下），对抗
            // 拖拽吸附的间歇性拒收。attempt1=质心原点，attempt2=+30，attempt3=-30。
            var grabOffset = attempt switch
            {
                2 => 30,
                3 => -30,
                _ => 0
            };
            var sourcePoint = new PixelPoint(
                badgeCenter.X + grabOffset,
                badgeCenter.Y);
            SaveBadgeEvidence(captured.Value.Frame, $"badge-before-a{attempt}");
            Publish(
                TaskEventLevel.Information,
                "GrailBadgePanelProbe",
                $"物品栏星徽探测命中（模板分 {badgeScore:F2}），质心=({badgeCenter.X},{badgeCenter.Y}) 客户区坐标。");
            var targetPoint = MapReferencePoint(window, targetReference.Center);

            // 1.2.104（019 局实弹 9 连败取证）：物品栏图标的拖拽吸附交互与备战席卡牌
            // 不同，曾为此引入"奇数拖拽/偶数点选"交替模式（审查 P3-3：旧策略陈述已删，
            // 取证事实保留）。
            // 09-10 夜审 P2-A：1.2.104 的"奇数拖拽/偶数点选"交替模式退役——09-09 夜班
            // 实测点选模式 4/4 次点开角色详情弹框（character_detail_popup）且 0 次装上
            //（C8/C11/C18/C19，徽章整局留在物品栏；墙图 f_00360/f_00750/f_00807 实拍），
            // 而拖拽模式（450ms 按压+300ms 悬停）同晚 6+ 次全部成功。3 次尝试全部走
            // 拖拽；拒绝横幅检测在拖拽路径内已有（P1-A），点选分支的横幅检查不流失。

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
                TimeSpan.FromMilliseconds(1100),
                new ActionPolicy
                {
                    // 星徽=物品栏图标抓取：按下后需停留一拍让游戏把物品吸附到光标
                    //（2026-09-02 实测两次即时拖拽均未带走星徽；角色卡拖拽不受影响）。
                    // 1.2.103（019 局实弹 6 连败"拖后仍在"）：机器/游戏整体变慢日，
                    // 250ms 按压不再足够吸附——按压 450ms+按下前悬停 300ms+时长 850ms。
                    // 09-10 夜审（1.2.121 会话）：450ms 档在 15:58-17:09 负载时段仍 18 连
                    // 败"拖后仍在"（C4/C7/C9 各 6 试 0 成，17:31 后全成=时段性吸附劣化）
                    // ——按压 450→700ms+时长 850→1100ms（插值节奏不变，总时长随按压延长）。
                    PointerSettleDelay = TimeSpan.FromMilliseconds(300),
                    MouseButtonHoldDelay = TimeSpan.FromMilliseconds(700),
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
            CaptureFrame? lastCheckFrame = null;
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
                    SaveBadgeEvidence(check.Value.Frame, $"badge-stillpanel-a{attempt}");
                    Publish(
                        TaskEventLevel.Warning,
                        "GrailBadgeAssemblyRetry",
                        $"拖后星徽仍在物品栏（质心=({remainingCenter.X},{remainingCenter.Y})，模板分 {remainingScore:F2}），第 {attempt}/3 次未装上，重试。");
                    // P1-A（2026-09-09 修复批）：拒绝横幅在屏=游戏判定目标已携带同羁绊
                    // 星徽——重试只会继续弹横幅（G15 实锤双横幅），如实上报停止重试。
                    if (await IsBadgeRejectionBannerUpAsync(check.Value.Frame, cancellationToken))
                    {
                        return true;
                    }
                    break;
                }

                lastCheckFrame = check.Value.Frame;
            }

            if (badgeStillVisible)
            {
                continue;
            }

            if (lastCheckFrame is not null)
            {
                SaveBadgeEvidence(lastCheckFrame, "badge-ASSEMBLED");
            }

            Publish(
                TaskEventLevel.Information,
                "GrailBadgeAssemblyVerified",
                "连续两帧未在物品栏定位到星徽，判定装配成功（取证帧已存 badge-evidence）。");
            return true;
        }

        return false;
    }

    // P1-A（2026-09-09 修复批）：装配拒绝横幅的 OCR 检测带（1920 参考系，顶部宽幅网扫）。
    // 注：横幅正样本帧已随审计样本清理，无法做模板/区域精标——宽幅带+词组联合判定
    // 是诚实上限：未命中时行为同现状不劣化，命中即止损（幂等预查仍是主防线）。
    private static readonly PixelRect BadgeRejectionBannerRegion = new(360, 100, 1200, 120);

    /// <summary>
    /// 装配拒绝横幅检测（P1-A）：OCR 顶部横幅带并判「无法穿戴相同羁绊的星徽」词组。
    /// 命中=目标已携带同羁绊星徽（装配目标已达成）。OCR 不可用/异常=未命中（不阻断）。
    /// </summary>
    private async Task<bool> IsBadgeRejectionBannerUpAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        if (!ocr.IsAvailable)
        {
            return false;
        }

        try
        {
            var horizontalScale = frame.Width / (double)1920;
            var verticalScale = frame.Height / (double)1080;
            var region = new PixelRect(
                (int)Math.Round(BadgeRejectionBannerRegion.X * horizontalScale),
                (int)Math.Round(BadgeRejectionBannerRegion.Y * verticalScale),
                (int)Math.Round(BadgeRejectionBannerRegion.Width * horizontalScale),
                (int)Math.Round(BadgeRejectionBannerRegion.Height * verticalScale));
            var text = await ocr.RecognizeAsync(frame, region, cancellationToken);
            if (GrailBadgeAssemblyGuard.IsBadgeRejectionBannerText(text.Text))
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailBadgeAssemblyRejected",
                    $"装配被游戏拒绝（横幅 OCR：「{text.Text.ReplaceLineEndings(" ")}」）——目标已携带同羁绊星徽，停止重试按已携带上报。");
                return true;
            }
        }
        catch (Exception ocrError) when (ocrError is not OperationCanceledException)
        {
            Publish(
                TaskEventLevel.Warning,
                "GrailBadgeAssemblyBannerOcrFailed",
                $"拒绝横幅 OCR 失败（不阻断重试判定）：{ocrError.Message}");
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
    internal async Task<GrailDeployedSaleResult> GrailSellDeployedCharacterAsync(
        nint windowHandle,
        GrailDeployedCharacter candidate,
        string expectedPreparationPageId,
        CancellationToken cancellationToken,
        bool backRow = false)
    {
        if (candidate.CardRegion is not { } region)
        {
            return new GrailDeployedSaleResult(false, false);
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
                return new GrailDeployedSaleResult(false, false);
            }

            // 坑38 修复①（拖前必查槽，连续两帧）：空槽=卡已不在（很可能此前已卖出成功），
            // 绝不空拖——2026-09-03 实测"已卖出后仍连拖 6 次"的直接根因。
            // 识别器契约=1920×1080 参考系矩形（审查 P1：CardRegion 相对参考系而非帧像素）。
            var slotReferenceRect = RegionToReferenceRect(region);
            // 终审 P2-1：后台槽与快照管线同口径用 BackRow 选项（6px 内缩+lenient 阈值）
            // ——默认 Standard 在后台能量特效污染下大概率判 Uncertain→比对被跳过→保护失效。
            var slotOptions = backRow
                ? CharacterCardRecognitionOptions.Standard with { BackRow = true }
                : CharacterCardRecognitionOptions.Standard;
            var slotBefore = recognizer.Recognize(
                captured.Value.Frame,
                templates,
                [slotReferenceRect],
                slotOptions)[0];
            if (slotBefore.State == CharacterCardSlotState.Empty)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                var recaptured = await CaptureVerifiedPreparationAsync(
                    windowHandle,
                    expectedPreparationPageId,
                    allowEscapeRecovery: false,
                    cancellationToken);
                var slotBeforeSecond = recaptured is null
                    ? null
                    : recognizer.Recognize(recaptured.Value.Frame, templates, [slotReferenceRect])[0];
                if (slotBeforeSecond is not null && slotBeforeSecond.State == CharacterCardSlotState.Empty)
                {
                    Publish(
                        TaskEventLevel.Information,
                        "GrailDeployedSaleAlreadyCompleted",
                        $"出售“{candidate.Name}”前复核发现目标槽位连续两帧为空（可能此前已卖出成功），按已完成处理，不再拖动。");
                    return new GrailDeployedSaleResult(false, true);
                }
            }

            // 坑 34 双源确认落地（2026-09-08 下午批 P1-C：模型漂移误卖实锤——引擎台账说
            // "F2=黑塔"，实际黑塔在备战席、该槽站着别的卡，位置语义拖拽把无辜卡拖走）：
            // 拖前把槽位卡面识别读到的名字与决策层传入的期望名比对，不符=拒绝拖拽交对账。
            // 期望名为空（旧调用方）或识别不可读（Uncertain/无名，四.14：1 费名混淆常态）时
            // 不因识别抖动卡死流程，维持位置语义放行但留审计事件。
            if (slotBefore.State == CharacterCardSlotState.SpecialOccupied)
            {
                // rule 四.15a：武装箱/聘用书箱=特殊占用位，绝不出售。
                Publish(
                    TaskEventLevel.Warning,
                    "GrailDeployedSaleIdentityMismatch",
                    $"出售“{candidate.Name}”前复核发现目标槽位是特殊占用位（武装箱/聘用书箱族）" +
                    "——拒绝拖拽（rule 四.15a）。");
                return new GrailDeployedSaleResult(false, false, IdentityMismatch: true);
            }

            // 终审 P2-2：比对门用 State==Recognized（识别器已带 lead-over/lenient 阈值门，
            // 佩佩 0.472/银狼 0.39 等变费角色判 Recognized 但 conf<0.5——自设 0.5 会让
            // 这类角色的槽位漂移保护失效）。
            if (slotBefore.State == CharacterCardSlotState.Recognized &&
                !string.IsNullOrWhiteSpace(slotBefore.DisplayName) &&
                !string.IsNullOrWhiteSpace(candidate.Name) &&
                !candidate.Name.StartsWith("前台", StringComparison.Ordinal) &&
                !candidate.Name.StartsWith("后台", StringComparison.Ordinal) &&
                !GrailSaleIdentityMatcher.NameMatches(candidate.Name, slotBefore.DisplayName))
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailDeployedSaleIdentityMismatch",
                    $"出售“{candidate.Name}”前复核发现目标槽位实际是「{slotBefore.DisplayName}」" +
                    $"（识别置信 {slotBefore.Confidence:P0}）——引擎占用模型与画面不符，" +
                    "拒绝拖拽防误卖；请对账重建部署台账后重试。");
                return new GrailDeployedSaleResult(false, false, IdentityMismatch: true);
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
                    // 09-10 夜审 P1-B：场上出售首试 holdMs=0 同属坑44 漏接（差分 0.1
                    // 判"未卖出"重试的家系），与部署路径统一 250ms 按压。
                    MouseButtonHoldDelay = TimeSpan.FromMilliseconds(250),
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

            // 坑38 修复②（拖后以"卡是否消失"为准，像素差分只作旁证）：
            // 卖出=卡从槽位消失（且区域差分佐证≥6，矛盾=不确定）；卡还在且区域无变化=确实没卖出才允许重试；
            // 卡还在但区域变化大（动画/位移，判据互相矛盾）=不确定——立即停手回事实，绝不重拖
            //（2026-09-03 实测：动画期像素判定误报"未卖出"导致卖出后继续重拖）。
            var after = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: false,
                cancellationToken);
            if (after is null)
            {
                return new GrailDeployedSaleResult(false, false);
            }

            var saleRect = RegionToPixelRect(region, after.Value.Frame);
            var saleDelta = SellRegionMeanDelta(
                captured.Value.Frame, after.Value.Frame, saleRect);
            var afterSlot = recognizer.Recognize(after.Value.Frame, templates, [slotReferenceRect])[0];
            if (afterSlot.State == CharacterCardSlotState.Empty)
            {
                if (saleDelta < 6.0)
                {
                    // 判据矛盾（卡消失不可能差分≈0）：不确定即停，绝不谎报成功（审查 P3）。
                    Publish(
                        TaskEventLevel.Warning,
                        "GrailDeployedSaleUncertain",
                        $"出售场上“{candidate.Name}”第 {attempt}/3 次后槽读空但差分仅 {saleDelta:F1}，判据矛盾——停止重试，交由决策层复核。");
                    return new GrailDeployedSaleResult(false, false);
                }

                Publish(
                    TaskEventLevel.Information,
                    "GrailDeployedSaleVerified",
                    $"已确认场上“{candidate.Name}”槽位卡牌消失（差分 {saleDelta:F1}），判定卖出成功。");
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                return new GrailDeployedSaleResult(true, false);
            }

            if (saleDelta >= 6.0)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "GrailDeployedSaleUncertain",
                    $"出售场上“{candidate.Name}”第 {attempt}/3 次后槽位仍有卡但区域变化大（差分 {saleDelta:F1}）" +
                    "——卖出与否不确定，停止重试（防已卖出后继续重拖），交由决策层复核。");
                return new GrailDeployedSaleResult(false, false);
            }

            Publish(
                TaskEventLevel.Warning,
                "GrailDeployedSaleNotVerified",
                $"出售场上“{candidate.Name}”第 {attempt}/3 次拖拽后卡位无变化（差分 {saleDelta:F1}），判定未卖出，重试。");
        }

        return new GrailDeployedSaleResult(false, false);
    }

    /// <summary>
    /// 通用参考点点击（2026-09-04 决策层夜间批次）：用于关闭识别表外的阻塞弹窗
    /// （公告 ✕、活动弹窗等）。只回输入事实，页面是否恢复由调用方 I1 复核。
    /// </summary>
    public async Task<bool> GrailClickReferencePointAsync(
        nint windowHandle,
        int referenceX,
        int referenceY,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var targetPoint = MapReferencePoint(window, new PixelPoint(referenceX, referenceY));
        var click = await input.ClickAsync(
            new ClickTarget(
                "grail_generic_reference_click",
                $"通用点击 ({referenceX},{referenceY})",
                window,
                BoundsAround(window, targetPoint)),
            new ActionPolicy { AfterActionDelay = TimeSpan.FromMilliseconds(300) },
            cancellationToken);
        return click.Succeeded;
    }

    /// <summary>
    /// 按交互键 Enter（2026-09-04 夜间：世界内"货币战争"交互提示实测对点击无响应，
    /// 推测需要交互键触发）。只回输入事实，交互是否生效由调用方 I1 复核。
    /// </summary>
    public async Task<bool> GrailPressInteractKeyAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var press = await input.PressKeyAsync(
            window,
            InputKey.Enter,
            new ActionPolicy { AfterActionDelay = TimeSpan.FromMilliseconds(500) },
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        return press.Succeeded;
    }

    /// <summary>0..1 相对区域 → 1920×1080 参考系矩形（识别器 Recognize 的槽表契约域；审查 P1）。</summary>
    private static PixelRect RegionToReferenceRect(RelativeRegion region) => new(
        (int)Math.Round(region.X * OpenCvTemplateMatcher.ReferenceWidth),
        (int)Math.Round(region.Y * OpenCvTemplateMatcher.ReferenceHeight),
        Math.Max(8, (int)Math.Round(region.Width * OpenCvTemplateMatcher.ReferenceWidth)),
        Math.Max(8, (int)Math.Round(region.Height * OpenCvTemplateMatcher.ReferenceHeight)));

    /// <summary>0..1 相对区域 → 当前帧像素矩形（SellRegionMeanDelta 的像素域消费）。</summary>
    private static PixelRect RegionToPixelRect(RelativeRegion region, CaptureFrame frame) => new(
        (int)Math.Round(region.X * frame.Width),
        (int)Math.Round(region.Y * frame.Height),
        Math.Max(8, (int)Math.Round(region.Width * frame.Width)),
        Math.Max(8, (int)Math.Round(region.Height * frame.Height)));

    /// <summary>
    /// A15 简易装备选择（2026-09-03 实测新增）：晶矿掉落的「为『阿哈』选择 1 件简易装备」
    /// 模态不在页面识别表，会阻塞一切指令流。四个按钮一排排布、参考系 1920×1080 定点：
    /// 幸运星(682,245)/折叠小刀(972,245)/轮滑鞋(1262,245)/和平手枪(1550,245)。
    /// 直取窗口截屏（模态页无识别表项，不走备战页门禁）；点击后仅回输入事实，
    /// 弹框是否消失由调用方 I1 复核。
    /// </summary>
    internal async Task<bool> GrailChooseSimpleEquipmentAsync(
        nint windowHandle,
        int buttonIndex,
        CancellationToken cancellationToken)
    {
        PixelPoint[] buttonPoints =
        [
            new(682, 245),
            new(972, 245),
            new(1262, 245),
            new(1550, 245),
        ];
        if (buttonIndex < 0 || buttonIndex >= buttonPoints.Length)
        {
            return false;
        }

        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var targetPoint = MapReferencePoint(window, buttonPoints[buttonIndex]);
        var click = await input.ClickAsync(
            new ClickTarget(
                "grail_simple_equipment_choice",
                $"简易装备选择第 {buttonIndex + 1} 项",
                window,
                BoundsAround(window, targetPoint)),
            new ActionPolicy { AfterActionDelay = TimeSpan.FromMilliseconds(300) },
            cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        return click.Succeeded;
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
            // 坑38 批次（审查 P3）：槽位已空（此前已卖出）= 不计金、不中断，继续卖下一个候选；
            // 只有"确实没卖出"才中断批量序列。
            var outcome = await GrailSellDeployedCharacterAsync(
                windowHandle,
                deployedCandidates[index],
                expectedPreparationPageId,
                cancellationToken);
            if (!outcome.Sold)
            {
                if (!outcome.AlreadyGone)
                {
                    break;
                }

                continue;
            }

            gold += deployedCandidates[index].SaleValue;
            sold++;
        }

        return new GrailSellResult(sold, gold, gold >= targetGold);
    }
}

/// <summary>TargetGold 卖人结果。</summary>
public sealed record GrailSellResult(int SoldCount, int EstimatedGold, bool TargetReached);

/// <summary>
/// 场上单卡卖出结果（坑38 批次）：Sold=本次确认卖出；AlreadyGone=槽位已空（此前已卖出，
/// 按已完成处理，调用方不计金不中断）；两者皆否=未卖出/不确定。
/// </summary>
/// <summary>IdentityMismatch=拖前槽位卡面识别与引擎台账名字不符（1-3 试用卡轮换/系统重排
/// 会导致占用模型漂移）——拒绝拖拽防误卖（坑 34 双源确认落地，2026-09-08 下午批 P1-C）。</summary>
/// <summary>卖出身份比对：中点/空白规范化后全等（忽略大小写）。
/// 刻意不用前缀包含——「姬子」与「姬子•启行」是不同角色（character_42 vs _01），
/// 前缀包含会把误卖放行成"相符"。识别名与台账名同源官方数据，全等即足。</summary>
public static class GrailSaleIdentityMatcher
{
    public static bool NameMatches(string expected, string recognized)
    {
        static string Norm(string s) => s.Replace(" ", string.Empty)
            .Replace("·", string.Empty).Replace("•", string.Empty).Trim();
        var e = Norm(expected);
        var r = Norm(recognized);
        if (e.Length == 0 || r.Length == 0)
        {
            return true; // 无法比对时不据此拒绝（调用方已保证空名走放行分支）
        }

        return string.Equals(e, r, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record GrailDeployedSaleResult(bool Sold, bool AlreadyGone, bool IdentityMismatch = false);
