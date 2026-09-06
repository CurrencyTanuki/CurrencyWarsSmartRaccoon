using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class RejectedOpeningRecoveryRetryTests
{
    [Fact]
    public async Task UsesVerifiedEscapeBeforeClickingUnstableExitButton()
    {
        var input = new StagedInputController();
        var window = new GameWindowInfo(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(0, 0, 1920, 1080));
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());

        var result = await recovery.RecoverAsync(
            window.Handle,
            new OpeningSnapshot([], [], []),
            new OpeningFilterEvaluation(false, ["不合格"], [], []),
            CancellationToken.None);

        Assert.Equal(
            RejectedOpeningRecoveryStatus.Recovered,
            result.Status);
        Assert.Equal(1, input.EscapeAttempts);
        Assert.Equal(0, input.ExitAttempts);
        Assert.Equal(1, input.SettlementNextAttempts);
    }

    [Fact]
    public async Task SharedSettlementRecoveryRejectsWrongPageBeforeClicking()
    {
        var input = new StagedInputController();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new FixedClassifier("currency_wars_home"),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());

        var result = await recovery
            .CompleteFromAbandonSettlementPromptAsync(
                window.Handle,
                CancellationToken.None);

        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.Equal(0, input.ClickAttempts);
        Assert.Equal(0, input.EscapeAttempts);
    }

    [Fact]
    public async Task SharedSettlementRecoveryHonorsCancellation()
    {
        var input = new StagedInputController();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new FixedClassifier("abandon_settlement_prompt"),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recovery.CompleteFromAbandonSettlementPromptAsync(
                window.Handle,
                cancellation.Token));
        Assert.Equal(0, input.ClickAttempts);
    }

    [Fact]
    public async Task SharedSettlementRecoveryDoesNotAuthorizeFromOneLateFrame()
    {
        var input = new StagedInputController();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new SingleFrameClassifier(
                "abandon_settlement_prompt",
                matchingCall: 13),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());

        var result = await recovery
            .CompleteFromAbandonSettlementPromptAsync(
                window.Handle,
                CancellationToken.None);

        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.Equal(0, input.ClickAttempts);
        Assert.Equal(0, input.EscapeAttempts);
    }

    [Fact]
    public async Task AuthorizedSettlementKeepsClickingThroughUnrecognizedTransition()
    {
        var input = new StagedInputController
        {
            SettlementPageUnknown = true,
            ReturnHomeAfterSettlementAttempts = 6
        };
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());

        var result = await recovery.RecoverAsync(
            window.Handle,
            new OpeningSnapshot([], [], []),
            new OpeningFilterEvaluation(false, ["reject"], [], []),
            CancellationToken.None);

        Assert.Equal(RejectedOpeningRecoveryStatus.Recovered, result.Status);
        Assert.Equal(6, input.SettlementNextAttempts);
    }

    [Fact]
    public async Task AuthorizedSettlementStopsAtBoundedAttemptLimit()
    {
        var input = new StagedInputController
        {
            SettlementPageUnknown = true,
            NeverReturnHome = true
        };
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());

        var result = await recovery.RecoverAsync(
            window.Handle,
            new OpeningSnapshot([], [], []),
            new OpeningFilterEvaluation(false, ["reject"], [], []),
            CancellationToken.None);

        // 1.2.101 点法（用户令）：挑战失败页后 (750,744) 连点直到主页，15 秒时框
        // （0.5 秒节奏+击前探测）——永不回主页时按时框停，输入有界仍是契约本体。
        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.InRange(input.SettlementNextAttempts, 18, 32);
    }

    [Fact]
    public async Task SettlementAdvanceClicksSingleMidLowButton()
    {
        var input = new StagedInputController
        {
            SettlementPageUnknown = true,
            NeverReturnHome = true
        };
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());

        var result = await recovery.RecoverAsync(
            window.Handle,
            new OpeningSnapshot([], [], []),
            new OpeningFilterEvaluation(false, ["reject"], [], []),
            CancellationToken.None);

        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        // 1.2.102（实弹修正）：结算推进全部点击单一位置 960,899（用户口径"页面中间
        // 偏下的下一页"=正中偏底；1.2.100 盲点同点位 43/43 实弹全成功；750,744 曾致
        // 8/8 弃局烧满）。单一位置连点直到主页，不交替。
        var xs = input.SettlementNextClickCenterXs;
        Assert.True(xs.Count >= 3, $"clicks={xs.Count}");
        Assert.All(xs, x => Assert.Equal(960, x));
    }

    [Fact]
    public async Task AuthorizedSettlementWaitsForDelayedHomeBeforeClickingAgain()
    {
        var input = new StagedInputController
        {
            SettlementPageUnknown = true,
            HomeTransitionDelay = TimeSpan.FromMilliseconds(1400)
        };
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            new NullTaskEventSink());

        var result = await recovery.RecoverAsync(
            window.Handle,
            new OpeningSnapshot([], [], []),
            new OpeningFilterEvaluation(false, ["reject"], [], []),
            CancellationToken.None);

        Assert.Equal(RejectedOpeningRecoveryStatus.Recovered, result.Status);
        // 1.2.91 点法后结算推进=500ms 节奏（0.5 秒间隔，击前探测）：1400ms 延迟窗口内
        // 会真实点击约 3 次再等到主页，2..4 覆盖节奏与探测耗时的合理抖动。
        Assert.True(input.SettlementNextAttempts is >= 2 and <= 4,
            $"settlement clicks={input.SettlementNextAttempts}, expect 2..4 (500ms cadence)");
    }

    private static GameWindowInfo Window() =>
        new(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(0, 0, 1920, 1080));

    private sealed class PreparationNavigator : ICurrencyWarsOpeningNavigator
    {
        public Task<CurrencyWarsNavigationResult> RunAsync(
            nint windowHandle,
            CurrencyWarsNavigationOptions options,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CurrencyWarsNavigationResult(
                CurrencyWarsNavigationState.ReachedPreparation,
                "preparation_1_1",
                "ready"));
    }

    private sealed class StaticCapture : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CaptureFrame(
                1,
                1,
                4,
                new byte[4],
                new PixelRect(0, 0, 1, 1),
                DateTimeOffset.Now));
    }

    [Fact]
    public async Task AbandonFallbackBlindAdvanceReturnsHomeAfterEscChainExhausted()
    {
        var input = new StagedInputController { BlindAdvanceScenario = true };
        var sink = new RecordingEventSink();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            sink);

        var result = await recovery.AbandonCurrentRunAsync(
            window.Handle,
            CancellationToken.None);

        // 1.2.97 热修（实弹 05:0x：1-2 战败"对局未完成"统计页 Unknown 卡死）：
        // Esc 链耗尽后按 rule 四.22 汇入盲点直通——识别不了的结算中间页族不识别、
        // 盲点推进位点到回主界面为止。早停设计：首击后确认主页即停。
        Assert.Equal(RejectedOpeningRecoveryStatus.Recovered, result.Status);
        Assert.Equal(1, input.BlindAdvanceClicks);
        Assert.Contains("RecoveryFallbackBlindAdvance", sink.EventNames);
    }

    [Fact]
    public async Task AbandonFallbackBlindAdvanceAbortsOnPreparationPage()
    {
        var input = new StagedInputController { PreparationStuck = true };
        var sink = new RecordingEventSink();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            sink);

        var result = await recovery.AbandonCurrentRunAsync(
            window.Handle,
            CancellationToken.None);

        // 1.2.97 审查 P2：盲点推进位 (960,899) 在备战页=出战按钮——击前探测到
        // 备战页必须急停（零点击、诚实失败），防误触真实开战。
        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.Equal(0, input.BlindAdvanceClicks);
        Assert.Contains("RecoveryBlindAdvanceAbortPreparation", sink.EventNames);
    }

    [Fact]
    public async Task SettleTimeoutFallsThroughToBlindAdvanceAndRecovers()
    {
        // （1.2.100 审查 P3-2：SettlementPageUnknown/NeverReturnHome 是死配置已删——
        // 本路径 Stage 不会到 Settlement，settle 段不读这两个开关。）
        var input = new StagedInputController
        {
            SettleNeverChallengeFailed = true
        };
        var sink = new RecordingEventSink();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            sink);

        var result = await recovery.RecoverAsync(
            window.Handle,
            new OpeningSnapshot([], [], []),
            new OpeningFilterEvaluation(false, ["reject"], [], []),
            CancellationToken.None);

        // 1.2.100（实弹 09:54/09:57 两轮复现）：settle 连点 6 秒不出挑战失败页
        // =游戏停在结算链中间页——超时即盲点直通（rule 四.22），不再 Failed 交外层空转。
        Assert.Equal(RejectedOpeningRecoveryStatus.Recovered, result.Status);
        Assert.Equal(1, input.BlindAdvanceClicks);
        Assert.Contains("RecoverySettleTimeoutBlindAdvance", sink.EventNames);
    }

    [Fact]
    public async Task AdvanceTimeoutFailsHonestlyAfterClickThroughWindow()
    {
        var input = new StagedInputController { AdvanceNeverHome = true };
        var sink = new RecordingEventSink();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            sink);

        var result = await recovery.RecoverAsync(
            window.Handle,
            new OpeningSnapshot([], [], []),
            new OpeningFilterEvaluation(false, ["reject"], [], []),
            CancellationToken.None);

        // 1.2.101（用户令点法）：推进本身就是 (750,744) 连点直到主页——15 秒窗尽
        // 仍未主页=诚实 Failed 交外层（连点即终点，不再另起盲点直通）。
        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.Equal(0, input.BlindAdvanceClicks);
        Assert.True(input.SettlementNextAttempts >= 18,
            $"settlementNext={input.SettlementNextAttempts}");
    }

    private sealed class RecordingEventSink : ITaskEventSink
    {
        public List<string> EventNames { get; } = [];

        public void Publish(TaskEvent taskEvent) => EventNames.Add(taskEvent.Code);
    }

    private sealed class StagedClassifier(StagedInputController input)
        : IGamePageClassifier
    {
        public PageClassificationResult? Classify(CaptureFrame frame)
        {
            // 1.2.97 审查 P2 用例夹具：备战页粘滞——A9 兜底每轮走"备战页 continue"，
            // 耗尽后盲点直通击前探测命中急停（零点击、Failed）。
            if (input.PreparationStuck)
            {
                return new PageClassificationResult(
                    "preparation_1_1",
                    "preparation_1_1",
                    0.99,
                    []);
            }

            // 1.2.97 审查 P2 用例夹具：恒 Unknown 驱动 A9 兜底耗尽；盲点首击后
            // 分类器确认主页——击前探测刹停（点击数有界，不烧满 15s 窗口）。
            if (input.BlindAdvanceScenario)
            {
                return input.BlindAdvanceClicks >= 1
                    ? new PageClassificationResult(
                        "currency_wars_home",
                        "currency_wars_home",
                        0.99,
                        [])
                    : null;
            }

            // 1.2.100 用例夹具：settle 连点窗内永不出现挑战失败页；盲点首击后回主页。
            // 仅 Abandon 阶段生效——Exit 阶段的 abandon_settlement_prompt 识别保持原规则，
            // 否则 RecoverAsync 在 Esc 链兜底就分流，走不进 settle 段。
            if (input.SettleNeverChallengeFailed && input.Stage == InputStage.Abandon)
            {
                return input.BlindAdvanceClicks >= 1
                    ? new PageClassificationResult(
                        "currency_wars_home",
                        "currency_wars_home",
                        0.99,
                        [])
                    : null;
            }

            // 1.2.100 审查 P2-1 用例夹具：推进段（含统一验证窗）恒不见主页；盲点首击后 home。
            if (input.AdvanceNeverHome && input.Stage == InputStage.Settlement)
            {
                return input.BlindAdvanceClicks >= 1
                    ? new PageClassificationResult(
                        "currency_wars_home",
                        "currency_wars_home",
                        0.99,
                        [])
                    : null;
            }

            var pageId = input.Stage switch
            {
                InputStage.Exit when
                    input.EscapeAttempts >= 1 || input.ExitAttempts >= 2 =>
                    "abandon_settlement_prompt",
                InputStage.Abandon => "challenge_failed",
                InputStage.Settlement when input.HomeTransitionDelay is not null &&
                    input.ElapsedSinceSettlement < input.HomeTransitionDelay => null,
                InputStage.Settlement when input.HomeTransitionDelay is not null =>
                    "currency_wars_home",
                InputStage.Settlement when input.SettlementPageUnknown &&
                    input.SettlementNextAttempts < input.ReturnHomeAfterSettlementAttempts => null,
                InputStage.Settlement when !input.NeverReturnHome &&
                    input.SettlementNextAttempts >= input.ReturnHomeAfterSettlementAttempts => "currency_wars_home",
                InputStage.Settlement => "challenge_failed",
                _ => null
            };
            return pageId is null
                ? null
                : new PageClassificationResult(
                    pageId,
                    pageId,
                    0.99,
                    []);
        }
    }

    private sealed class FixedClassifier(string pageId) : IAutomationPageClassifier
    {
        public PageClassificationResult? Classify(CaptureFrame frame) =>
            new(
                pageId,
                pageId,
                0.99,
                []);
    }

    private sealed class SingleFrameClassifier(
        string pageId,
        int matchingCall) : IGamePageClassifier
    {
        private int _calls;

        public PageClassificationResult? Classify(CaptureFrame frame)
        {
            _calls++;
            return _calls == matchingCall
                ? new PageClassificationResult(pageId, pageId, 0.99, [])
                : null;
        }
    }

    private enum InputStage
    {
        None,
        Exit,
        Abandon,
        Settlement
    }

    private sealed class StagedInputController : IInputController
    {
        public InputStage Stage { get; private set; }
        public int ExitAttempts { get; private set; }
        public int EscapeAttempts { get; private set; }
        public int ClickAttempts { get; private set; }
        public int SettlementNextAttempts { get; private set; }
        // 1.2.95：记录推进段每次点击的中心 X——双点位交替契约的观测面。
        public List<int> SettlementNextClickCenterXs { get; } = [];
        // 1.2.97 审查 P2：盲点直通热修的观测面。
        public bool BlindAdvanceScenario { get; init; }
        public bool PreparationStuck { get; init; }
        public int BlindAdvanceClicks { get; private set; }
        // 1.2.100：settle 连点 6 秒不出挑战失败页（盲点直通接管）。
        public bool SettleNeverChallengeFailed { get; init; }
        // 1.2.100 审查 P2-1：推进连点+验证窗恒不见主页（盲点直通接管）。
        public bool AdvanceNeverHome { get; init; }
        public bool NeverReturnHome { get; init; }
        public bool SettlementPageUnknown { get; init; }
        public int ReturnHomeAfterSettlementAttempts { get; init; } = 1;
        public TimeSpan? HomeTransitionDelay { get; init; }
        public TimeSpan ElapsedSinceSettlement => _settlementStartedAt is null
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - _settlementStartedAt.Value;
        private DateTimeOffset? _settlementStartedAt;

        public Task<ActionResult> ClickAsync(
            ClickTarget target,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            ClickAttempts++;
            if (target.Id.StartsWith(
                    "exit_rejected_run",
                    StringComparison.OrdinalIgnoreCase))
            {
                Stage = InputStage.Exit;
                ExitAttempts++;
            }
            else if (target.Id.StartsWith(
                         "abandon_and_settle",
                         StringComparison.OrdinalIgnoreCase))
            {
                Stage = InputStage.Abandon;
            }
            else if (target.Id.StartsWith(
                         "settlement_next",
                         StringComparison.OrdinalIgnoreCase))
            {
                Stage = InputStage.Settlement;
                SettlementNextAttempts++;
                SettlementNextClickCenterXs.Add(target.ClientBounds.X + target.ClientBounds.Width / 2);
                _settlementStartedAt ??= DateTimeOffset.UtcNow;
            }

            else if (target.Id.StartsWith(
                         "blind_advance_next",
                         StringComparison.OrdinalIgnoreCase))
            {
                BlindAdvanceClicks++;
            }

            return Task.FromResult(ActionResult.Success(target.DisplayName));
        }

        public Task<ActionResult> DragAsync(
            ClickTarget source,
            PixelPoint targetClientPoint,
            TimeSpan duration,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ActionResult> PressKeyAsync(
            GameWindowInfo window,
            InputKey key,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            Assert.Equal(InputKey.Escape, key);
            Stage = InputStage.Exit;
            EscapeAttempts++;
            return Task.FromResult(ActionResult.Success("Esc"));
        }

        public Task<ActionResult> ClickWithModifierAsync(
            ClickTarget target,
            InputKey modifier,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ImmediateForegroundGuard(GameWindowInfo window)
        : IGameForegroundGuard
    {
        public TimeSpan TotalPausedDuration => TimeSpan.Zero;

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            nint windowHandle,
            CancellationToken cancellationToken) =>
            Task.FromResult(window);

        public Task<GameWindowInfo> WaitUntilForegroundAsync(
            GameWindowInfo current,
            CancellationToken cancellationToken) =>
            Task.FromResult(window);
    }
}
