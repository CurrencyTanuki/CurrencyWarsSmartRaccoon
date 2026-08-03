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
public sealed class CurrencyWarsRejectedOpeningRecovery(
    ICurrencyWarsOpeningNavigator navigator,
    IGameCapture capture,
    IGamePageClassifier classifier,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    ITaskEventSink eventSink) :
    IRejectedOpeningRecovery,
    IAbandonSettlementRecovery
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;
    private static readonly StandardPoint ExitRunPoint = new(55, 65);
    private static readonly StandardPoint AbandonAndSettlePoint = new(750, 744);
    private static readonly StandardPoint NextPoint = new(960, 899);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private TimeSpan _pauseBaseline;

    private DateTimeOffset ActiveUtcNow =>
        DateTimeOffset.UtcNow -
        (foregroundGuard.TotalPausedDuration - _pauseBaseline);

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

        var exitPrompt = await PressKeyUntilPageAsync(
            windowHandle,
            InputKey.Escape,
            "使用 Esc 退出当前对局",
            "abandon_settlement_prompt",
            TimeSpan.FromSeconds(4),
            1,
            cancellationToken);
        if (exitPrompt is null)
        {
            Publish(
                "RecoveryFallbackStarted",
                "Esc 未进入放弃确认页，改用左上角退出按钮并进行有限重试。",
                TaskEventLevel.Information);
            exitPrompt = await ClickUntilPageAsync(
                windowHandle,
                "exit_rejected_run",
                "退出当前对局",
                ExitRunPoint,
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
        for (var recoveryCycle = 1;
             challengeFailed is null && recoveryCycle <= 2;
             recoveryCycle++)
        {
            challengeFailed = await ClickUntilPageAsync(
                windowHandle,
                "abandon_and_settle",
                "放弃并结算",
                AbandonAndSettlePoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(200)
                },
                "challenge_failed",
                TimeSpan.FromSeconds(6),
                5,
                cancellationToken,
                requiredPageId: "abandon_settlement_prompt");
            if (challengeFailed is null)
            {
                Publish(
                    "RecoveryStrategyCycle",
                    $"放弃结算第 {recoveryCycle} 轮未成功；重新识别并继续下一轮。",
                    TaskEventLevel.Warning);
            }
        }

        if (challengeFailed is null)
        {
            return Failed("放弃并结算未进入挑战失败页；已达到 2 轮安全重试上限。");
        }

        const int maximumSettlementAdvanceAttempts = 12;
        var returnedHome = false;
        for (var attempt = 1;
             attempt <= maximumSettlementAdvanceAttempts;
             attempt++)
        {
            var beforeClick = await WaitForKnownSettlementPageAsync(
                windowHandle,
                TimeSpan.FromMilliseconds(400),
                cancellationToken);
            if (beforeClick is { PageId: "currency_wars_home" })
            {
                returnedHome = true;
                break;
            }

            var next = await ClickStandardPointAsync(
                windowHandle,
                $"settlement_next_{attempt}",
                $"结算下一步（尝试 {attempt}）",
                NextPoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.Zero
                },
                cancellationToken);
            if (!next.Succeeded)
            {
                Publish(
                    "RecoveryActionRetry",
                    $"结算下一步第 {attempt} 次输入失败：{next.Message}；准备重试。",
                    TaskEventLevel.Warning);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(300),
                    cancellationToken);
                continue;
            }

            Publish(
                "RecoveryNextClicked",
                $"已点击结算下一步：尝试 {attempt}，正在验证是否返回主界面。");
            var current = await WaitForKnownSettlementPageAsync(
                    windowHandle,
                    TimeSpan.FromMilliseconds(2500),
                    cancellationToken);
            if (current is { PageId: "currency_wars_home" })
            {
                returnedHome = true;
                break;
            }

            if (current is not { PageId: "challenge_failed" })
            {
                Publish(
                    "RecoverySettlementPageUnconfirmed",
                    "快速结算点击后处于动画或暂未稳定识别；当前已由挑战失败页授权，" +
                    "继续点击同一结算位置并监测主页。",
                    TaskEventLevel.Warning);
            }

            Publish(
                "RecoverySettlementPending",
                $"第 {attempt} 次结算推进后仍是结算动画或中间页，将继续有限推进。",
                TaskEventLevel.Information);
        }

        const string message = "已放弃不合格开局并返回货币战争主界面。";
        if (!returnedHome)
        {
            return Failed(
                $"Settlement advancement reached the safe limit of " +
                $"{maximumSettlementAdvanceAttempts} attempts; input stopped " +
                "so passive recovery can take over.");
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


