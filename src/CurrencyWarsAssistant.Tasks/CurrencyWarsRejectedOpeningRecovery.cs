using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public static class RewardSettlementDetailEvidence
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;

    public static bool IsMatch(CaptureFrame frame) =>
        HasRatio(frame, new PixelRect(770, 860, 380, 70), IsLight, 0.55) &&
        HasRatio(frame, new PixelRect(760, 150, 900, 690), IsDark, 0.72);

    private static bool HasRatio(
        CaptureFrame frame,
        PixelRect reference,
        Func<byte, byte, byte, bool> predicate,
        double minimum)
    {
        if (frame.Width <= 0 || frame.Height <= 0 ||
            Math.Abs(frame.Width / (double)frame.Height - 16d / 9d) > 0.02)
        {
            return false;
        }

        var left = (int)Math.Round(reference.X * frame.Width / (double)ReferenceWidth);
        var top = (int)Math.Round(reference.Y * frame.Height / (double)ReferenceHeight);
        var right = (int)Math.Round(reference.Right * frame.Width / (double)ReferenceWidth);
        var bottom = (int)Math.Round(reference.Bottom * frame.Height / (double)ReferenceHeight);
        var matched = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                var offset = y * frame.Stride + x * 4;
                if (offset < 0 || offset + 2 >= frame.BgraPixels.Length)
                {
                    return false;
                }

                sampled++;
                if (predicate(
                        frame.BgraPixels[offset + 2],
                        frame.BgraPixels[offset + 1],
                        frame.BgraPixels[offset]))
                {
                    matched++;
                }
            }
        }

        return sampled > 0 && matched / (double)sampled >= minimum;
    }

    private static bool IsLight(byte red, byte green, byte blue) =>
        red >= 210 && green >= 210 && blue >= 210;

    private static bool IsDark(byte red, byte green, byte blue) =>
        red <= 55 && green <= 55 && blue <= 55;
}

public static class CurrencyWarsHomeEvidence
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;

    // Context-only fallback for the Currency Wars home page. The normal title
    // template can be obscured by the automation log overlay, while these two
    // independent layout regions remain visible.
    public static bool IsMatch(CaptureFrame frame) =>
        HasRatio(frame, new PixelRect(1320, 925, 570, 105), IsLightBlue, 0.55) &&
        HasRatio(frame, new PixelRect(40, 260, 250, 210), IsDarkBlue, 0.85);

    private static bool HasRatio(
        CaptureFrame frame,
        PixelRect reference,
        Func<byte, byte, byte, bool> predicate,
        double minimum)
    {
        if (frame.Width <= 0 || frame.Height <= 0 ||
            Math.Abs(frame.Width / (double)frame.Height - 16d / 9d) > 0.02)
        {
            return false;
        }

        var left = (int)Math.Round(reference.X * frame.Width / (double)ReferenceWidth);
        var top = (int)Math.Round(reference.Y * frame.Height / (double)ReferenceHeight);
        var right = (int)Math.Round(reference.Right * frame.Width / (double)ReferenceWidth);
        var bottom = (int)Math.Round(reference.Bottom * frame.Height / (double)ReferenceHeight);
        var matched = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y += 4)
        {
            for (var x = left; x < right; x += 4)
            {
                var offset = y * frame.Stride + x * 4;
                if (offset < 0 || offset + 2 >= frame.BgraPixels.Length)
                {
                    return false;
                }

                sampled++;
                if (predicate(
                        frame.BgraPixels[offset + 2],
                        frame.BgraPixels[offset + 1],
                        frame.BgraPixels[offset]))
                {
                    matched++;
                }
            }
        }

        return sampled > 0 && matched / (double)sampled >= minimum;
    }

    private static bool IsLightBlue(byte red, byte green, byte blue) =>
        red >= 170 && green >= 185 && blue >= 210 && blue >= red + 15;

    private static bool IsDarkBlue(byte red, byte green, byte blue) =>
        red < 100 && green < 120 && blue < 170;
}

/// <summary>
/// Completes the currently displayed investment selection, enters preparation,
/// abandons the run, advances the settlement pages and verifies that the
/// Currency Wars home page has returned.
/// </summary>
/// <summary>弃局兜底：主动放弃当前对局并回到安全入口页。异常不外泄，失败也继续（四轮 R1-R5）。</summary>
public interface IRunAbandoner
{
    Task<RejectedOpeningRecoveryResult> AbandonCurrentRunAsync(
        nint windowHandle,
        CancellationToken cancellationToken);
}

public sealed class CurrencyWarsRejectedOpeningRecovery(
    ICurrencyWarsOpeningNavigator navigator,
    IGameCapture capture,
    IGamePageClassifier classifier,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    ITaskEventSink eventSink) :
    IRejectedOpeningRecovery,
    IAbandonSettlementRecovery,
    IRunAbandoner
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;
    private static readonly StandardPoint AbandonAndSettlePoint = new(750, 744);
    private static readonly StandardPoint NextPoint = new(960, 899);
    // "前台区域无角色，无法出战"提示弹窗的确认按钮（12:08 实拍帧裁测，1920 参考系）。
    private static readonly StandardPoint UncompletedPromptConfirmPoint = new(960, 699);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private TimeSpan _pauseBaseline;

    private DateTimeOffset ActiveUtcNow =>
        DateTimeOffset.UtcNow -
        (foregroundGuard.TotalPausedDuration - _pauseBaseline);

    /// <summary>
    /// 弃局兜底入口（不依赖开局快照，卡在任意页面均可）：Esc 进入放弃确认弹窗 →
    /// 完成结算推进回主页；Esc 无效（非对局页）时按页面身份分流——结算详情页点
    /// "下一页/保存并退出"位推进，备战页绝不点击（防误触出战），主界面即成功
    /// （2026-09-04 用户令）。最多 3 轮。
    /// </summary>
    public async Task<RejectedOpeningRecoveryResult> AbandonCurrentRunAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // 1.2.58（独立分析 P-12）：进 1-1 后 1ms 即发 Esc 的失败率 22%——
            // 先给入场动画 2.5 秒；Esc 重试 1→2 次（多数失败几秒后重按即成功）。
            await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
            var exitPrompt = await PressKeyUntilPageAsync(
                windowHandle,
                InputKey.Escape,
                "使用 Esc 退出当前对局",
                "abandon_settlement_prompt",
                TimeSpan.FromSeconds(4),
                2,
                cancellationToken);

            if (exitPrompt is null)
            {
                // 2026-09-04 用户令：兜底点击必须按页面身份分流——
                // ①备战页（preparation_）绝不点击：(960,899) 在备战页上是出战按钮，
                //   误触会真实开战（备战期灾难）；Esc 无法弃局就如实报失败。
                // ②主界面（normal_hud/currency_wars_home）：弃局目标已达成，直接成功。
                // ③无法出战提示弹窗：点"确认"关闭。
                // ④其余（结算详情页等）：点"下一页/保存并退出"位推进，链走完回主界面。
                var fallbackPage = await ReadStablePageAsync(
                    windowHandle,
                    cancellationToken);
                var fallbackPageId = fallbackPage?.PageId ?? string.Empty;
                if (fallbackPageId.StartsWith(
                        "preparation_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Publish(
                        "RecoverySkipPreparationClick",
                        $"当前为备战页（{fallbackPageId}），Esc 无法弃局且禁止点击出战区；" +
                        "等待外层重试或人工结算。",
                        TaskEventLevel.Warning);
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken);
                    continue;
                }

                if (fallbackPageId is "normal_hud" or "currency_wars_home")
                {
                    Publish(
                        "RecoveryAlreadyAtHome",
                        $"已识别 {fallbackPageId}——弃局目标（回到主界面）已达成。");
                    return RejectedOpeningRecoveryResult.Recovered(
                        "已回到货币战争主界面。");
                }

                // 1.2.58（架构审查 3-4 遗漏①）：页面完全无法识别（Esc 可能刚打开
                // 系统菜单/半透明动画态）时禁止一切兜底点击——(960,899) 在未知页面
                // 上的语义未经验证，点了就是盲点。只报警并交给外层重试。
                if (fallbackPage is null)
                {
                    Publish(
                        "RecoverySkipUnknownClick",
                        "当前页面无法识别（Unknown）——禁止兜底点击，等待下一轮 Esc 后重判。",
                        TaskEventLevel.Warning);
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken);
                    continue;
                }

                var isPrompt = fallbackPage is
                    { PageId: "uncompleted_battle_prompt" };
                var clickResult = await ClickStandardPointAsync(
                    windowHandle,
                    $"grail_abandon_exit_{attempt}",
                    isPrompt ? "确认关闭无法出战提示" : "保存并退出/结算推进位",
                    isPrompt ? UncompletedPromptConfirmPoint : NextPoint,
                    new ActionPolicy(),
                    cancellationToken);
                if (clickResult.Succeeded)
                {
                    exitPrompt = await WaitForPageAsync(
                        windowHandle,
                        "abandon_settlement_prompt",
                        TimeSpan.FromSeconds(4),
                        cancellationToken);
                }
            }

            if (exitPrompt is not null)
            {
                return await CompleteFromAbandonSettlementPromptCoreAsync(
                    windowHandle,
                    cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return Failed("弃局兜底：Esc 与退出按钮均未能进入放弃结算确认页；已达到安全重试上限。");
    }

    public async Task<RejectedOpeningRecoveryResult> RecoverAsync(
        nint windowHandle,
        OpeningSnapshot rejectedOpening,
        OpeningFilterEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        Publish(
            "RecoveryStarted",
            $"当前开局不满足条件：{string.Join("；", evaluation.Reasons)}");

        CurrencyWarsNavigationResult? preparation = null;
        const int maximumPreparationAttempts = 3;
        for (var attempt = 1;
             attempt <= maximumPreparationAttempts;
             attempt++)
        {
            preparation = await navigator.RunAsync(
                windowHandle,
                new CurrencyWarsNavigationOptions
                {
                    StopAfterOpeningRecognition = false,
                    StopAtPreparation = true
                },
                cancellationToken);
            if (preparation.FinalState ==
                CurrencyWarsNavigationState.ReachedPreparation)
            {
                break;
            }

            Publish(
                "RecoveryActionRetry",
                $"进入 1-1 备战页第 {attempt} 次未成功：{preparation.Message}；" +
                "准备从当前已知页面继续重试。",
                TaskEventLevel.Warning);
            if (attempt < maximumPreparationAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        if (preparation is null ||
            preparation.FinalState != CurrencyWarsNavigationState.ReachedPreparation)
        {
            return Failed(
                $"无法进入 1-1 备战页：{preparation?.Message ?? "未产生导航结果"}");
        }

        // 1.2.58（独立分析 P-12）：同上——先等入场动画，Esc 重试 1→2 次。
        await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
        var exitPrompt = await PressKeyUntilPageAsync(
            windowHandle,
            InputKey.Escape,
            "使用 Esc 退出当前对局",
            "abandon_settlement_prompt",
            TimeSpan.FromSeconds(4),
            2,
            cancellationToken);
        if (exitPrompt is null)
        {
            // 2026-09-04 用户令（1.2.57 补分流）：Esc 无效=非对局页，兜底点击必须
            // 按页面身份分流——与 AbandonCurrentRunAsync 同款规则。1.2.56 漏改本处，
            // 导致备战页被连点 (960,899)=出战按钮三次（15:17 专家研讨会局实况）。
            var fallbackPage = await ReadStablePageAsync(
                windowHandle,
                cancellationToken);
            var fallbackPageId = fallbackPage?.PageId ?? string.Empty;
            if (fallbackPageId.StartsWith(
                    "preparation_",
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    "RecoverySkipPreparationClick",
                    $"当前为备战页（{fallbackPageId}），禁止点击出战区；" +
                    "本路径无法弃局，交由外层重试。",
                    TaskEventLevel.Warning);
                return Failed("备战页 Esc 无法弃局且禁止点击出战区；已停止兜底点击。");
            }

            if (fallbackPageId is "normal_hud" or "currency_wars_home")
            {
                Publish(
                    "RecoveryAlreadyAtHome",
                    $"已识别 {fallbackPageId}——弃局目标（回到主界面）已达成。");
                return RejectedOpeningRecoveryResult.Recovered(
                    "已回到货币战争主界面。");
            }

            // 1.2.58（架构审查 3-4 遗漏①）：Unknown 页禁止兜底点击（同上）。
            if (fallbackPage is null)
            {
                Publish(
                    "RecoverySkipUnknownClick",
                    "当前页面无法识别（Unknown）——禁止兜底点击，交由外层重试。",
                    TaskEventLevel.Warning);
                return Failed("页面无法识别（Unknown）；已停止兜底点击。");
            }

            Publish(
                "RecoveryFallbackStarted",
                "Esc 未进入放弃确认页，改点结算链\"下一页/保存并退出\"位并进行有限重试。",
                TaskEventLevel.Information);
            exitPrompt = await ClickUntilPageAsync(
                windowHandle,
                "exit_rejected_run",
                "保存并退出/结算推进位",
                NextPoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(200)
                },
                "abandon_settlement_prompt",
                TimeSpan.FromSeconds(4),
                3,
                cancellationToken);
        }

        if (exitPrompt is null)
        {
            return Failed("Esc 与退出按钮均未能进入放弃结算确认页；已达到安全重试上限。");
        }

        return await CompleteFromAbandonSettlementPromptCoreAsync(
            windowHandle,
            cancellationToken);
    }

    public async Task<RejectedOpeningRecoveryResult>
        CompleteFromAbandonSettlementPromptAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        var prompt = await WaitForPageAsync(
            windowHandle,
            "abandon_settlement_prompt",
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (prompt is null)
        {
            return Failed(
                "复用放弃结算恢复前未稳定确认 abandon_settlement_prompt；未发送危险输入。");
        }

        Publish(
            "RecoveryPromptConfirmed",
            "已稳定确认放弃结算提示页，复用统一结算返回主页流程。");
        return await CompleteFromAbandonSettlementPromptCoreAsync(
            windowHandle,
            cancellationToken);
    }

    private async Task<RejectedOpeningRecoveryResult>
        CompleteFromAbandonSettlementPromptCoreAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {

        PageClassificationResult? challengeFailed = null;
        // 1.2.91（用户令点法 2026-09-05 晚）："放弃并结算"（750,744 中偏下）连点
        // **3 秒、0.5 秒间隔、单轮**——不再 8s×2 轮。连点内单帧快探：
        // challenge_failed（连续 2 帧）/主页（分类或强证据）→提前收；
        // 备战页→立即停手（弃局未生效）。
        {
            var settleClick = await ClickStandardPointAsync(
                windowHandle,
                "abandon_and_settle",
                "放弃并结算",
                AbandonAndSettlePoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.Zero
                },
                cancellationToken);
            if (!settleClick.Succeeded)
            {
                Publish(
                    "RecoveryStrategyCycle",
                    "放弃结算点击输入失败；重新识别并继续下一轮。",
                    TaskEventLevel.Warning);
            }
            else
            {
                var rapidDeadline = ActiveUtcNow + TimeSpan.FromSeconds(3);
                var challengeStrikes = 0;
                while (ActiveUtcNow < rapidDeadline && challengeFailed is null)
                {
                    await ClickStandardPointAsync(
                        windowHandle,
                        "abandon_and_settle_rapid",
                        "放弃并结算连点",
                        AbandonAndSettlePoint,
                        new ActionPolicy
                        {
                            AfterActionDelay = TimeSpan.Zero
                        },
                        cancellationToken);
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(500),
                        cancellationToken);

                    var probeWindow = await foregroundGuard.WaitUntilForegroundAsync(
                        windowHandle,
                        cancellationToken);
                    var probeFrame = await capture.CaptureAsync(probeWindow, cancellationToken);
                    var page = classifier.Classify(probeFrame);

                    if (page?.PageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Publish(
                            "RecoveryRapidClickAbortPreparation",
                            "连点探测到备战页——弃局未生效，立即停止推进（备战页绝不点击）。",
                            TaskEventLevel.Warning);
                        return Failed("弃局连点期间回到备战页；已停止推进交外层处理。");
                    }

                    if (page is not null &&
                        string.Equals(page.PageId, "challenge_failed", StringComparison.OrdinalIgnoreCase))
                    {
                        challengeStrikes++;
                        if (challengeStrikes >= 2)
                        {
                            challengeFailed = page;
                            Publish(
                                "RecoveryPromptConfirmed",
                                $"连点推进已进入挑战失败页（{page.Confidence:P1}，连续 {challengeStrikes} 帧）。");
                        }
                    }
                    else
                    {
                        challengeStrikes = 0;
                    }

                    if (page?.PageId is "currency_wars_home" or "normal_hud"
                        || (page is null && CurrencyWarsHomeEvidence.IsMatch(probeFrame)))
                    {
                        challengeFailed = new PageClassificationResult(
                            "challenge_failed",
                            "challenge_failed_via_home",
                            0.95,
                            []);
                        Publish(
                            "RecoveryCompletedEarlyHome",
                            "连点期间已回到货币战争主界面（结算直接完成）——停止连点。");
                    }
                }

                if (challengeFailed is null)
                {
                    Publish(
                        "RecoveryStrategyCycle",
                        "放弃结算连点 3 秒未探测到挑战失败页。",
                        TaskEventLevel.Warning);
                }
            }
        }

        if (challengeFailed is null)
        {
            return Failed("放弃并结算未进入挑战失败页；已达到 2 轮安全重试上限。");
        }

        // 1.2.91（用户令点法 2026-09-05 晚）：challenge_failed 之后——
        // ①页面偏左的"保存并退出/结算推进"位（960,899 结算链按钮）**只点击一次**；
        // ②页面中下部（750,744）连点 3 秒（0.5 秒间隔）；
        // ③统一验证回主页（3 秒）。替换原 12 连点×(400ms+双页检) 结构。
        var saveExit = await ClickStandardPointAsync(
            windowHandle,
            "settlement_save_exit_once",
            "保存并退出（单次）",
            NextPoint,
            new ActionPolicy
            {
                AfterActionDelay = TimeSpan.Zero
            },
            cancellationToken);
        Publish(
            saveExit.Succeeded ? "RecoveryNextClicked" : "RecoveryActionRetry",
            saveExit.Succeeded
                ? "已点击保存并退出/结算推进位（单次，按用户点法）。"
                : $"保存并退出点击失败：{saveExit.Message}——继续中下部连点。",
            saveExit.Succeeded ? TaskEventLevel.Information : TaskEventLevel.Warning);

        var returnedHome = false;
        var advanceDeadline = ActiveUtcNow + TimeSpan.FromSeconds(3);
        while (ActiveUtcNow < advanceDeadline && !cancellationToken.IsCancellationRequested)
        {
            await ClickStandardPointAsync(
                windowHandle,
                "settlement_advance_rapid",
                "结算推进连点（中下部）",
                AbandonAndSettlePoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.Zero
                },
                cancellationToken);
            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                cancellationToken);

            // 击后单帧查主页早停（1.2.63 实拍教训保留：回主界面绝不盲点页面中部）。
            var probeWindow = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var probeFrame = await capture.CaptureAsync(probeWindow, cancellationToken);
            var page = classifier.Classify(probeFrame);
            if (page is not null &&
                string.Equals(page.PageId, "currency_wars_home", StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    "RecoveryCompletedEarlyHome",
                    "结算推进连点期间已确认回到货币战争主界面——停止连点。");
                returnedHome = true;
                break;
            }
        }

        // 统一验证：连点结束后等主页出现（最多 3 秒）。
        var finalPage = await WaitForKnownSettlementPageAsync(
            windowHandle,
            TimeSpan.FromMilliseconds(3000),
            cancellationToken);
        if (finalPage is { PageId: "currency_wars_home" })
        {
            returnedHome = true;
        }
        else
        {
            Publish(
                "RecoverySettlementPending",
                "结算连点推进后仍未确认主页；继续有限推进或交由被动恢复。",
                TaskEventLevel.Information);
        }

        const string message = "已放弃不合格开局并返回货币战争主界面。";
        if (!returnedHome)
        {
            return Failed(
                "结算推进连点 3 秒后仍未确认回到主界面；输入已停止，交被动恢复接管。");
        }

        Publish("RecoveryCompleted", message);
        return RejectedOpeningRecoveryResult.Recovered(message);
    }

    private async Task<ActionResult> ClickStandardPointAsync(
        nint windowHandle,
        string id,
        string displayName,
        StandardPoint point,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);

        var mapped = new PixelPoint(
            (int)Math.Round(point.X * window.ClientArea.Width / (double)ReferenceWidth),
            (int)Math.Round(point.Y * window.ClientArea.Height / (double)ReferenceHeight));
        var bounds = new PixelRect(
            Math.Clamp(mapped.X - 3, 0, Math.Max(0, window.ClientArea.Width - 6)),
            Math.Clamp(mapped.Y - 3, 0, Math.Max(0, window.ClientArea.Height - 6)),
            6,
            6);
        Publish("RecoveryAction", $"准备执行：{displayName}");
        return await input.ClickAsync(
            new ClickTarget(id, displayName, window, bounds),
            policy,
            cancellationToken);
    }

    private async Task<PageClassificationResult?> ClickUntilPageAsync(
        nint windowHandle,
        string actionId,
        string displayName,
        StandardPoint point,
        ActionPolicy policy,
        string expectedPageId,
        TimeSpan verificationTimeout,
        int maximumAttempts,
        CancellationToken cancellationToken,
        string? requiredPageId = null)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requiredPageId is not null &&
                await WaitForPageAsync(
                    windowHandle,
                    requiredPageId,
                    TimeSpan.FromSeconds(2),
                    cancellationToken) is null)
            {
                Publish(
                    "RecoveryActionPageMismatch",
                    $"执行“{displayName}”前未稳定确认 {requiredPageId}；" +
                    "本次不发送危险输入。",
                    TaskEventLevel.Warning);
                return null;
            }

            Publish(
                "RecoveryActionAttempt",
                $"执行“{displayName}”：尝试 {attempt}/{maximumAttempts}。");
            var action = await ClickStandardPointAsync(
                windowHandle,
                $"{actionId}_{attempt}",
                displayName,
                point,
                policy,
                cancellationToken);
            if (action.Succeeded)
            {
                var detected = await WaitForPageAsync(
                    windowHandle,
                    expectedPageId,
                    verificationTimeout,
                    cancellationToken);
                if (detected is not null)
                {
                    return detected;
                }
            }

            var failure = action.Succeeded
                ? $"游戏没有进入预期页面“{expectedPageId}”"
                : action.Message;
            Publish(
                "RecoveryActionRetry",
                $"“{displayName}”第 {attempt}/{maximumAttempts} 次未成功：{failure}；" +
                (attempt < maximumAttempts ? "准备重试。" : "已达到重试上限。"),
                TaskEventLevel.Warning);
            if (attempt < maximumAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
        }

        return null;
    }

    private async Task<PageClassificationResult?>
        WaitForKnownSettlementPageAsync(
            nint windowHandle,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + timeout;
        string? previousPageId = null;
        PageClassificationResult? previous = null;
        var stable = 0;
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(window, cancellationToken);
            var current = classifier.Classify(frame);
            if (current is null && CurrencyWarsHomeEvidence.IsMatch(frame))
            {
                current = new PageClassificationResult(
                    "currency_wars_home",
                    "currency_wars_home_context",
                    0.95,
                    []);
            }
            else if (current is null &&
                RewardSettlementDetailEvidence.IsMatch(frame))
            {
                current = new PageClassificationResult(
                    "challenge_failed",
                    "settlement_detail",
                    0.95,
                    []);
            }
            var accepted = current?.PageId is
                "challenge_failed" or "currency_wars_home";
            if (accepted && string.Equals(
                    previousPageId,
                    current!.PageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                stable++;
            }
            else
            {
                stable = accepted ? 1 : 0;
            }

            previousPageId = accepted ? current!.PageId : null;
            previous = accepted ? current : null;
            if (stable >= 2)
            {
                return previous;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private async Task<PageClassificationResult?> PressKeyUntilPageAsync(
        nint windowHandle,
        InputKey key,
        string displayName,
        string expectedPageId,
        TimeSpan verificationTimeout,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            Publish(
                "RecoveryKeyAttempt",
                $"执行按键方案“{displayName}”：尝试 {attempt}/{maximumAttempts}。");
            var action = await input.PressKeyAsync(
                window,
                key,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(200)
                },
                cancellationToken);
            if (action.Succeeded)
            {
                var detected = await WaitForPageAsync(
                    windowHandle,
                    expectedPageId,
                    verificationTimeout,
                    cancellationToken);
                if (detected is not null)
                {
                    Publish(
                        "RecoveryKeySucceeded",
                        $"按键方案“{displayName}”成功，已进入预期页面。");
                    return detected;
                }
            }

            Publish(
                "RecoveryKeyRetry",
                $"按键方案“{displayName}”第 {attempt}/{maximumAttempts} 次未成功；" +
                (attempt < maximumAttempts ? "准备重试。" : "已达到重试上限。"),
                TaskEventLevel.Warning);
            if (attempt < maximumAttempts)
            {
                // 1.2.89 Esc 纪律（用户令 2026-09-05 第 5 问题）：补按前必须先认页——
                // ①预期弹框其实已开（识别慢/漏帧）时再按=把弹框关掉，改为继续等待；
                // ②已在主界面时再按=退出货币战争模式（16:32 实锤弹到游戏本体），立即停手；
                // ③Unknown（弹框疑似开着）不补按，继续等待；
                // ④祈愿试炼弹框不按 Esc（模态，需 M3 应答），继续等待；
                // ⑤其余已分类页（reward_shop/enemy_overview/challenge_failed 等）允许补按
                //   （Esc 关店等是合法用途）——注释口径以此为准（审查 P3-3 修正）。
                var currentPageBeforeRetry = await ReadStablePageAsync(
                    windowHandle,
                    cancellationToken);
                var currentPageId = currentPageBeforeRetry?.PageId ?? string.Empty;
                if (string.Equals(currentPageId, expectedPageId, StringComparison.OrdinalIgnoreCase))
                {
                    Publish(
                        "RecoveryKeyPageConfirmedLate",
                        $"页面其实已是预期页（识别滞后）——不补按，继续等待稳定确认。");
                    var lateDetected = await WaitForPageAsync(
                        windowHandle,
                        expectedPageId,
                        verificationTimeout,
                        cancellationToken);
                    if (lateDetected is not null)
                    {
                        Publish(
                            "RecoveryKeySucceeded",
                            $"按键方案“{displayName}”成功（延迟确认）。");
                        return lateDetected;
                    }

                    continue;
                }

                if (currentPageId is "normal_hud" or "currency_wars_home")
                {
                    Publish(
                        "RecoveryKeyAbortAtHome",
                        "已识别货币战争主界面——停止按键方案（防止 Esc 退出模式）。",
                        TaskEventLevel.Warning);
                    return null;
                }

                if (currentPageBeforeRetry is null
                    || currentPageId.StartsWith("wish_trial", StringComparison.OrdinalIgnoreCase))
                {
                    Publish(
                        "RecoveryKeySkipRetryUnknown",
                        currentPageBeforeRetry is null
                            ? "当前页面无法识别（疑似弹框开着）——不补按 Esc，继续等待识别。"
                            : "检测到祈愿试炼弹框——Esc 无效且有害，不补按，继续等待识别。",
                        TaskEventLevel.Warning);
                    var graceDetected = await WaitForPageAsync(
                        windowHandle,
                        expectedPageId,
                        verificationTimeout,
                        cancellationToken);
                    if (graceDetected is not null)
                    {
                        return graceDetected;
                    }

                    continue;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
        }

        return null;
    }

    private async Task<PageClassificationResult?> WaitForPageAsync(
        nint windowHandle,
        string expectedPageId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + timeout;
        var stability = new ConsecutiveObservationTracker<string>(
            2,
            StringComparer.OrdinalIgnoreCase);
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);

            var frame = await capture.CaptureAsync(window, cancellationToken);
            var detected = classifier.Classify(frame);
            if (detected is not null &&
                string.Equals(
                    detected.PageId,
                    expectedPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (stability.Observe(detected.PageId))
                {
                    Publish(
                        "RecoveryPageRecognized",
                        $"已识别：{detected.DisplayName}（{detected.Confidence:P1}）");
                    return detected;
                }
            }
            else
            {
                stability.Reset();
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private RejectedOpeningRecoveryResult Failed(string message)
    {
        Publish("RecoveryFailed", message, TaskEventLevel.Error);
        return RejectedOpeningRecoveryResult.Failed(message);
    }

    /// <summary>通用稳定页读取：连续 2 帧同一已知页面才算数（兜底分流依据）。</summary>
    private async Task<PageClassificationResult?> ReadStablePageAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        string? stablePageId = null;
        var stableCount = 0;
        var deadline = ActiveUtcNow + TimeSpan.FromSeconds(3);
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(window, cancellationToken);
            var detected = classifier.Classify(frame);
            if (detected is not null &&
                string.Equals(
                    stablePageId,
                    detected.PageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                stableCount++;
            }
            else
            {
                stablePageId = detected?.PageId;
                stableCount = detected is null ? 0 : 1;
            }

            if (detected is not null && stableCount >= 2)
            {
                return detected;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
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


