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

        Assert.Equal(RejectedOpeningRecoveryStatus.Failed, result.Status);
        Assert.Equal(12, input.SettlementNextAttempts);
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
        Assert.Equal(1, input.SettlementNextAttempts);
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

    private sealed class StagedClassifier(StagedInputController input)
        : IGamePageClassifier
    {
        public PageClassificationResult? Classify(CaptureFrame frame)
        {
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

    private sealed class FixedClassifier(string pageId) : IGamePageClassifier
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
                _settlementStartedAt ??= DateTimeOffset.UtcNow;
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
