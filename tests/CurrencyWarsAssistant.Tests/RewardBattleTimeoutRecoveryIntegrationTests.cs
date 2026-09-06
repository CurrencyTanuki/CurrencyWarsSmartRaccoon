using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class RewardBattleTimeoutRecoveryIntegrationTests
{
    [Fact]
    public async Task AuthorizedTimeoutFollowsVerifiedRecoveryPathToHome()
    {
        var fixture = CreateFixture();

        var result = await fixture.Controller.HandleTimedOutRewardBattleAsync(
            fixture.Window.Handle,
            BattleMachine(),
            battleStartedByController: true,
            CancellationToken.None,
            graceOverride: TimeSpan.Zero);

        Assert.Equal(
            RewardBattleTimeoutHandlingResult.RecoveredToHome,
            result);
        // 1.2.101 点法（用户令 2026-09-06）："放弃并结算"只点一次（AbandonAttempts=1），
        // 挑战失败页后中偏下 (750,744) 连点直到主页（SettlementNextAttempts 由击前探测刹停）。
        Assert.Equal("V", fixture.Input.Actions.FirstOrDefault());
        Assert.Contains("Esc", fixture.Input.Actions);
        Assert.Contains("撤退", fixture.Input.Actions);
        Assert.Contains("放弃并结算", fixture.Input.Actions);
        // 1.2.101：末动作=结算推进点击（夹具对 settlement_next* 记固定名"结算下一步"；
        // 实际动作=下一页连点（中偏下，单一位置 750,744））。
        Assert.Equal("结算下一步", fixture.Input.Actions[^1]);
        // 1.2.101 用户令点法：放弃并结算**只点一次**（此前首击+交替连点=2 次）；
        // Esc（暂停菜单）先于撤退是生产暂停页门禁保证的顺序契约。
        Assert.Equal(1, fixture.Input.AbandonAttempts);
        Assert.True(
            fixture.Input.Actions.IndexOf("Esc") < fixture.Input.Actions.IndexOf("撤退"),
            $"order={string.Join(",", fixture.Input.Actions)}");
        Assert.Equal(1, fixture.Input.RetreatAttempts);
        // 1.2.95 审查 P2-1 红线守卫：保存并退出单击后夹具立即返回主页——击前探针必在
        // 推进循环首次迭代刹停，计数恰为 1；若回归击后探测（旧结构）会先在主页多落一击=2。
        // 回主页后不得再有任何点击（rule 8.2#6 模式边界）。
        Assert.Equal(1, fixture.Input.SettlementNextAttempts);
        // 顺序契约：撤退先于放弃并结算（审查 P2-1 顺手补回精确序列放宽后丢失的相对顺序）。
        Assert.True(
            fixture.Input.Actions.IndexOf("撤退") < fixture.Input.Actions.IndexOf("放弃并结算"),
            $"order={string.Join(",", fixture.Input.Actions)}");
    }

    [Fact]
    public async Task TimeoutWithoutConfirmedStartOwnershipNeverEscapesOrRetreats()
    {
        var fixture = CreateFixture();

        var result = await fixture.Controller.HandleTimedOutRewardBattleAsync(
            fixture.Window.Handle,
            BattleMachine(),
            battleStartedByController: false,
            CancellationToken.None,
            graceOverride: TimeSpan.Zero);

        Assert.Equal(RewardBattleTimeoutHandlingResult.Blocked, result);
        Assert.Empty(fixture.Input.Actions);
        Assert.Equal(0, fixture.Input.EscapeAttempts);
        Assert.Equal(0, fixture.Input.RetreatAttempts);
    }

    [Fact]
    public async Task OnePauseFrameDoesNotAuthorizeRetreat()
    {
        var fixture = CreateFixture(singlePauseFrameOnly: true);

        var result = await fixture.Controller.HandleTimedOutRewardBattleAsync(
            fixture.Window.Handle,
            BattleMachine(),
            battleStartedByController: true,
            CancellationToken.None,
            graceOverride: TimeSpan.Zero);

        Assert.Equal(RewardBattleTimeoutHandlingResult.Failed, result);
        Assert.Equal(["V", "Esc"], fixture.Input.Actions);
        Assert.Equal(0, fixture.Input.RetreatAttempts);
        Assert.Equal(0, fixture.Input.AbandonAttempts);
    }

    [Fact]
    public async Task FailedRetreatClickDoesNotAdvanceToAbandonment()
    {
        var fixture = CreateFixture(failRetreat: true);

        var result = await fixture.Controller.HandleTimedOutRewardBattleAsync(
            fixture.Window.Handle,
            BattleMachine(),
            battleStartedByController: true,
            CancellationToken.None,
            graceOverride: TimeSpan.Zero);

        Assert.Equal(RewardBattleTimeoutHandlingResult.Failed, result);
        Assert.Equal(["V", "Esc", "撤退"], fixture.Input.Actions);
        Assert.Equal(1, fixture.Input.RetreatAttempts);
        Assert.Equal(0, fixture.Input.AbandonAttempts);
    }

    private static Fixture CreateFixture(
        bool singlePauseFrameOnly = false,
        bool failRetreat = false)
    {
        var window = new GameWindowInfo(
            123,
            456,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(0, 0, 1920, 1080));
        var input = new RecoveryInputController(failRetreat);
        var classifier = new RecoveryClassifier(
            input,
            singlePauseFrameOnly);
        var capture = new StaticCapture(DisabledAutoBattleFrame());
        var foreground = new ImmediateForegroundGuard(window);
        var events = new RecordingEventSink();
        var settlement = new CurrencyWarsRejectedOpeningRecovery(
            new UnusedNavigator(),
            capture,
            classifier,
            input,
            foreground,
            events);
        var data = GameDataCatalogLoader.Load(
            Path.Combine(RepositoryRoot, "data", "4.4"));
        var ocr = new EmptyOcr();
        var controller = new RewardStageAutomationController(
            capture,
            classifier,
            new RewardShopReader(ocr, data),
            new RewardShopPurchasePlanner(),
            new InvestmentStrategyPageReader(ocr, data),
            new RewardVisualDetector(),
            input,
            foreground,
            new UnusedPreparationCompletionController(),
            settlement,
            events);
        return new Fixture(controller, input, window);
    }

    private static RewardBattleStateMachine BattleMachine()
    {
        var machine = new RewardBattleStateMachine(
            "preparation_1_1",
            "reward_shop");
        machine.Apply(machine.Observe("preparation_1_1", []));
        Assert.True(machine.TryStartBattle());
        machine.Apply(machine.Observe("reward_battle", []));
        Assert.Equal(RewardBattleFlowState.Battle, machine.State);
        return machine;
    }

    private static CaptureFrame DisabledAutoBattleFrame()
    {
        const int width = 1920;
        const int height = 1080;
        var pixels = new byte[width * height * 4];
        for (var y = 32; y < 62; y++)
        {
            for (var x = 1740; x < 1780; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = 140;
                pixels[offset + 1] = 140;
                pixels[offset + 2] = 140;
                pixels[offset + 3] = 255;
            }
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));

    private sealed record Fixture(
        RewardStageAutomationController Controller,
        RecoveryInputController Input,
        GameWindowInfo Window);

    private enum RecoveryStage
    {
        Battle,
        Pause,
        AbandonPrompt,
        ChallengeFailed,
        Home
    }

    private sealed class RecoveryInputController(bool failRetreat)
        : IInputController
    {
        public RecoveryStage Stage { get; private set; } =
            RecoveryStage.Battle;
        public List<string> Actions { get; } = [];
        public int EscapeAttempts { get; private set; }
        public int RetreatAttempts { get; private set; }
        public int AbandonAttempts { get; private set; }
        public int SettlementNextAttempts { get; private set; }

        public Task<ActionResult> ClickAsync(
            ClickTarget target,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Id == "撤退")
            {
                Actions.Add("撤退");
                RetreatAttempts++;
                if (failRetreat)
                {
                    return Task.FromResult(ActionResult.Failure("撤退点击失败"));
                }

                Stage = RecoveryStage.AbandonPrompt;
            }
            else if (target.Id.StartsWith(
                         "abandon_and_settle",
                         StringComparison.OrdinalIgnoreCase))
            {
                Actions.Add("放弃并结算");
                AbandonAttempts++;
                Stage = RecoveryStage.ChallengeFailed;
            }
            else if (target.Id.StartsWith(
                         "settlement_next",
                         StringComparison.OrdinalIgnoreCase))
            {
                Actions.Add("结算下一步");
                SettlementNextAttempts++;
                Stage = RecoveryStage.Home;
            }

            return Task.FromResult(ActionResult.Success(target.DisplayName));
        }

        public Task<ActionResult> PressKeyAsync(
            GameWindowInfo window,
            InputKey key,
            ActionPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key == InputKey.V)
            {
                Actions.Add("V");
            }
            else if (key == InputKey.Escape)
            {
                Actions.Add("Esc");
                EscapeAttempts++;
                Stage = RecoveryStage.Pause;
            }

            return Task.FromResult(ActionResult.Success(key.ToString()));
        }

        public Task<ActionResult> DragAsync(
            ClickTarget source,
            PixelPoint targetClientPoint,
            TimeSpan duration,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ActionResult> ClickWithModifierAsync(
            ClickTarget target,
            InputKey modifier,
            ActionPolicy policy,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecoveryClassifier(
        RecoveryInputController input,
        bool singlePauseFrameOnly) : IAutomationPageClassifier
    {
        private int _pauseFrames;

        public PageClassificationResult? Classify(CaptureFrame frame)
        {
            var pageId = input.Stage switch
            {
                RecoveryStage.Battle => "reward_battle",
                RecoveryStage.Pause when
                    !singlePauseFrameOnly || ++_pauseFrames == 1 =>
                    "reward_battle_pause",
                RecoveryStage.AbandonPrompt => "abandon_settlement_prompt",
                RecoveryStage.ChallengeFailed => "challenge_failed",
                RecoveryStage.Home => "currency_wars_home",
                _ => null
            };
            return pageId is null
                ? null
                : new PageClassificationResult(pageId, pageId, 0.99, []);
        }
    }

    private sealed class StaticCapture(CaptureFrame frame) : IGameCapture
    {
        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(frame);
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

    private sealed class UnusedPreparationCompletionController
        : IPreparationBoardCompletionController
    {
        public Task<IReadOnlyList<RecognizedBenchCharacter>?>
            ReadStableBenchCharactersAsync(
                nint windowHandle,
                string expectedPreparationPageId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PreparationMineCapacityResult> EnsureMineCapacityAsync(
            nint windowHandle,
            IReadOnlyList<PreparationPlacement> existingPlacements,
            PreparationBoardOptions options,
            string expectedPreparationPageId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PreparationBoardResult> CompleteAfterShopAsync(
            nint windowHandle,
            IReadOnlyList<PreparationPlacement> existingPlacements,
            PreparationBoardOptions options,
            string expectedPreparationPageId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedNavigator : ICurrencyWarsOpeningNavigator
    {
        public Task<CurrencyWarsNavigationResult> RunAsync(
            nint windowHandle,
            CurrencyWarsNavigationOptions options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyOcr : IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrTextResult(string.Empty, []));
    }

    private sealed class RecordingEventSink : ITaskEventSink
    {
        public List<TaskEvent> Events { get; } = [];

        public void Publish(TaskEvent taskEvent) => Events.Add(taskEvent);
    }
}
