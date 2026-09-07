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
    public async Task AbandonChainStillInGame_PrecedesBlindAdvance_OnPreparationPage()
    {
        // 1.2.119（审计簇 A）契约更新：弃局链遇备战页（对局仍在）=StillInGame
        // 立即返回，取代 1.2.97 旧行为（continue 空转→盲点急停 Failed）——
        // 通宵实测旧行为单段空转最长 23 分钟。盲点直通的击前备战页急停
        // （BlindAdvanceToHomeAsync 内部防御）保留作为其他入口的兜底。
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

        Assert.Equal(RejectedOpeningRecoveryStatus.StillInGame, result.Status);
        Assert.Equal(0, input.BlindAdvanceClicks);
        Assert.Contains("RecoveryStillInGame", sink.EventNames);
    }

    [Fact]
    public async Task AbandonFallbackAnswersGalaBondPopupThenEscSucceeds()
    {
        // 坑50（1.2.114；22:33/22:49 两次实锤卡死）：盛会之星羁绊升档选择框
        // 浮在备战页上——Esc 被模态吞掉，此前识别表无此页→5 连败自保停机。
        // 契约：识别出弹框页→任选一名角色+确认选择→下一轮 Esc 正常弃局；
        // 弹框在屏期间绝不 fallthrough 点 (960,899)（语义未验证，坑39 纪律）。
        var input = new StagedInputController { GalaPopupOnEsc = true };
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

        Assert.Equal(RejectedOpeningRecoveryStatus.Recovered, result.Status);
        Assert.Equal(1, input.GalaPortraitClicks);
        Assert.Equal(1, input.GalaConfirmClicks);
        Assert.Equal(0, input.AbandonExitNextClicks);
        Assert.Equal(0, input.BlindAdvanceClicks);
        Assert.Contains("RecoveryGalaPopupBlocked", sink.EventNames);
        Assert.Contains("RecoveryGalaPopupDismissed", sink.EventNames);
    }

    [Fact]
    public async Task AbandonFallbackGalaPopupNeverDismissesFailsHonestly()
    {
        // 坑50 防御分支：应答点击输入失败（候选耗尽）——如实 Failed，
        // 全程零盲点推进、零 (960,899) 兜底点击，不升级为盲点。
        var input = new StagedInputController
        {
            GalaPopupOnEsc = true,
            GalaPopupForever = true,
            GalaPortraitClickFails = true
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

        var result = await recovery.AbandonCurrentRunAsync(
            window.Handle,
            CancellationToken.None);

        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.Equal(0, input.BlindAdvanceClicks);
        Assert.Equal(0, input.AbandonExitNextClicks);
        Assert.Contains("RecoveryGalaPopupDismissFailed", sink.EventNames);
    }

    [Fact]
    public async Task DismissGalaBondPopupWhenNotOnScreenIsSuccessWithZeroClicks()
    {
        // 坑50 审查 P3-4b：IGalaBondPopupHandler 契约——弹框不在屏=成功语义+零点击
        //（循环泵监听标志可能滞后一帧，陈旧标志绝不产生点击副作用）。
        var input = new StagedInputController();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new FixedClassifier("preparation_generic"),
            input,
            new ImmediateForegroundGuard(window),
            new RecordingEventSink());

        var dismissed = await recovery.DismissGalaBondPopupIfUpAsync(
            window.Handle,
            CancellationToken.None);

        Assert.Equal(GalaBondDismissOutcome.NotOnScreen, dismissed);
        Assert.Equal(0, input.ClickAttempts);
        Assert.Equal(0, input.GalaPortraitClicks);
        Assert.Equal(0, input.GalaConfirmClicks);
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

        public List<string> Messages { get; } = [];

        public void Publish(TaskEvent taskEvent)
        {
            EventNames.Add(taskEvent.Code);
            Messages.Add(taskEvent.Message);
        }
    }

    /// <summary>1.2.119（簇 C）：可编程假祈愿处理器——应答即翻转夹具状态。</summary>
    private sealed class FakeWishHandler(StagedInputController input) : IWishTrialPopupHandler
    {
        public int Calls { get; private set; }

        public async Task<bool> DismissWishTrialPopupIfUpAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(1, cancellationToken);
            input.WishAnswered = true;
            return true;
        }
    }

    [Fact]
    public async Task AbandonChainReturnsStillInGame_OnPreparationPage()
    {
        // 1.2.119（审计簇 A，6-7-2/8-1）：弃局链遇备战页=对局仍在——旧行为 continue
        // 空转（单段最长 23 分钟），新行为 StillInGame 如实立即返回。
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
            CancellationToken.None,
            reason: "审计簇 A 单测：备战页粘滞");

        Assert.Equal(RejectedOpeningRecoveryStatus.StillInGame, result.Status);
        Assert.Contains("RecoveryStillInGame", sink.EventNames);
        Assert.Contains("RecoveryAbandonStarted", sink.EventNames);
        Assert.Contains(sink.Messages, m => m.Contains("审计簇 A 单测：备战页粘滞"));
    }

    [Fact]
    public async Task AbandonChainWishPopupAnswered_ContinuesToRecovery()
    {
        // 1.2.119（审计 4-1/6-7-3）：祈愿弹框在屏=应答而非空转。应答后下一轮
        // Esc 正常弃局（夹具恢复原阶段机→abandon_settlement_prompt→结算→主页）。
        var input = new StagedInputController { WishPopupOnEsc = true };
        var sink = new RecordingEventSink();
        var wish = new FakeWishHandler(input);
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            sink,
            wishTrialHandler: wish);

        var result = await recovery.AbandonCurrentRunAsync(
            window.Handle,
            CancellationToken.None,
            reason: "审计簇 C 单测：祈愿弹框应答");

        Assert.Equal(RejectedOpeningRecoveryStatus.Recovered, result.Status);
        Assert.Equal(1, wish.Calls);
        Assert.Contains("RecoveryAbandonStarted", sink.EventNames);
    }

    [Fact]
    public async Task AbandonChainWishPopupWithoutHandler_FailsHonestly()
    {
        // 1.2.119（审计簇 A）：无祈愿处理器注入时——不空转、不盲点，如实 Failed。
        var input = new StagedInputController { WishPopupOnEsc = true };
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

        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.Contains("RecoveryWishPopupDismissFailed", sink.EventNames);
    }

    [Fact]
    public async Task DismissBlockingModal_AnswersGalaPopup_AndReturnsTrue()
    {
        // 1.2.119（审计簇 C）：统一弹框守卫——盛会弹框在屏=自答并返回 true。
        var input = new StagedInputController { GalaPopupOnEsc = true };
        var sink = new RecordingEventSink();
        var window = Window();
        var recovery = new CurrencyWarsRejectedOpeningRecovery(
            new PreparationNavigator(),
            new StaticCapture(),
            new StagedClassifier(input),
            input,
            new ImmediateForegroundGuard(window),
            sink);

        var handled = await recovery.DismissBlockingModalIfUpAsync(
            window.Handle,
            CancellationToken.None);

        Assert.True(handled);
        Assert.Contains("RecoveryGalaPopupDismissed", sink.EventNames);
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

            // 1.2.119（审计簇 A/簇 C）用例夹具：祈愿试炼弹框——Esc 被模态吞掉，
            // 应答（WishAnswered）后恢复原阶段机。
            if (input.WishPopupOnEsc && !input.WishAnswered)
            {
                return new PageClassificationResult(
                    "wish_trial_selection",
                    "wish_trial_selection",
                    0.99,
                    []);
            }

            // 坑50（1.2.114）用例夹具：盛会之星升档选择框——Esc 被模态吞掉（页面
            // 恒为 gala_star_bond_selection），任选角色+确认后退出恢复原阶段机；
            // GalaPopupForever=应答后仍不退出（候选耗尽→诚实失败）。
            if (input.GalaPopupOnEsc)
            {
                var dismissed = !input.GalaPopupForever && input.GalaConfirmClicks >= 1;
                if (!dismissed)
                {
                    return new PageClassificationResult(
                        CurrencyWarsRejectedOpeningRecovery.GalaBondPopupPageId,
                        CurrencyWarsRejectedOpeningRecovery.GalaBondPopupPageId,
                        0.99,
                        []);
                }
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
        // 坑50（1.2.114）：盛会之星升档选择框场景观测面——Esc 后弹框在屏，
        // 任选角色+确认后退出；GalaPortraitClickFails=点击输入失败（候选耗尽→诚实失败）。
        public bool GalaPopupOnEsc { get; init; }
        public bool GalaPopupForever { get; init; }
        // 1.2.119（审计簇 A/簇 C）：祈愿弹框场景观测面——Esc 后 wish_trial_selection
        // 在屏；FakeWishHandler 应答（WishAnswered=true）后恢复原阶段机。
        public bool WishPopupOnEsc { get; init; }
        public bool WishAnswered { get; set; }
        public bool GalaPortraitClickFails { get; init; }
        public int GalaPortraitClicks { get; private set; }
        public int GalaConfirmClicks { get; private set; }
        public int AbandonExitNextClicks { get; private set; }
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
            else if (target.Id.StartsWith(
                         "grail_abandon_exit",
                         StringComparison.OrdinalIgnoreCase))
            {
                AbandonExitNextClicks++;
            }
            else if (target.Id.StartsWith(
                         "gala_portrait_",
                         StringComparison.OrdinalIgnoreCase))
            {
                GalaPortraitClicks++;
                if (GalaPortraitClickFails)
                {
                    return Task.FromResult(ActionResult.Failure("模拟输入失败"));
                }
            }
            else if (target.Id.StartsWith(
                         "gala_confirm",
                         StringComparison.OrdinalIgnoreCase))
            {
                GalaConfirmClicks++;
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
