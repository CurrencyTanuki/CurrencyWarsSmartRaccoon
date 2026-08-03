using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public enum OpeningRerollState
{
    LocatingWindow,
    ReadingOpening,
    Evaluating,
    ExecutingReroll,
    WaitingForOpening,
    Kept,
    DryRunStopped,
    Paused,
    LimitReached
}

public sealed class OpeningRerollOptions
{
    public bool DryRun { get; init; } = true;
    public int MaximumRerolls { get; init; } = 50;
    public TimeSpan MaximumRuntime { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan StableFrameDelay { get; init; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(150);
}

public sealed record OpeningRerollProgress(
    OpeningRerollState State,
    int RerollCount,
    OpeningObservation? Observation,
    OpeningDecision? Decision,
    string Message);

public sealed record OpeningRerollResult(
    OpeningRerollState FinalState,
    int RerollCount,
    OpeningObservation? Observation,
    OpeningDecision? Decision,
    string Message);

public sealed class OpeningRerollTask(
    IGameWindowService windowService,
    IGameCapture capture,
    ITemplateMatcher matcher,
    IOpeningReader openingReader,
    IInputController input,
    OpeningRecognitionConfig recognitionConfig,
    OpeningRuleEvaluator evaluator,
    ITaskEventSink eventSink)
{
    public event EventHandler<OpeningRerollProgress>? ProgressChanged;

    public async Task<OpeningRerollResult> RunAsync(
        nint windowHandle,
        OpeningRuleSet rules,
        OpeningRerollOptions options,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var rerollCount = 0;

        Publish(OpeningRerollState.LocatingWindow, rerollCount, null, null, "正在确认游戏窗口。");
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow - startedAt >= options.MaximumRuntime)
            {
                return Result(
                    OpeningRerollState.LimitReached,
                    rerollCount,
                    null,
                    null,
                    "已达到最长运行时间。");
            }

            if (rerollCount >= options.MaximumRerolls)
            {
                return Result(
                    OpeningRerollState.LimitReached,
                    rerollCount,
                    null,
                    null,
                    "已达到最大重刷次数。");
            }

            var window = windowService.Refresh(windowHandle);
            if (window is null)
            {
                return Result(
                    OpeningRerollState.Paused,
                    rerollCount,
                    null,
                    null,
                    "游戏窗口不存在、最小化或客户区无效。");
            }

            Publish(
                OpeningRerollState.ReadingOpening,
                rerollCount,
                null,
                null,
                "正在读取开局投资环境和敌人词条。");

            var stable = await ReadStableAsync(window, options, cancellationToken);
            if (stable is null)
            {
                return Result(
                    OpeningRerollState.Paused,
                    rerollCount,
                    null,
                    null,
                    "开局页面或识别结果不稳定，已暂停。");
            }

            Publish(
                OpeningRerollState.Evaluating,
                rerollCount,
                stable,
                null,
                "正在评估开局。");
            var decision = evaluator.Evaluate(stable, rules);
            if (decision.Kind == OpeningDecisionKind.Keep)
            {
                return Result(
                    OpeningRerollState.Kept,
                    rerollCount,
                    stable,
                    decision,
                    string.Join("；", decision.Reasons));
            }

            if (decision.Kind == OpeningDecisionKind.Review)
            {
                return Result(
                    OpeningRerollState.Paused,
                    rerollCount,
                    stable,
                    decision,
                    string.Join("；", decision.Reasons));
            }

            if (options.DryRun)
            {
                return Result(
                    OpeningRerollState.DryRunStopped,
                    rerollCount,
                    stable,
                    decision,
                    $"观察模式判断需要重刷：{string.Join("；", decision.Reasons)}");
            }

            Publish(
                OpeningRerollState.ExecutingReroll,
                rerollCount,
                stable,
                decision,
                string.Join("；", decision.Reasons));
            var workflow = await ExecuteRerollWorkflowAsync(
                window,
                options,
                cancellationToken);
            if (!workflow.Succeeded)
            {
                return Result(
                    OpeningRerollState.Paused,
                    rerollCount,
                    stable,
                    decision,
                    workflow.Message);
            }

            rerollCount++;
            Publish(
                OpeningRerollState.WaitingForOpening,
                rerollCount,
                null,
                decision,
                "重开流程完成，正在等待新开局页面。");

            var appeared = await WaitForOpeningPageAsync(windowHandle, options, cancellationToken);
            if (!appeared)
            {
                return Result(
                    OpeningRerollState.Paused,
                    rerollCount,
                    null,
                    decision,
                    "等待新开局页面超时，已暂停。");
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<OpeningObservation?> ReadStableAsync(
        GameWindowInfo window,
        OpeningRerollOptions options,
        CancellationToken cancellationToken)
    {
        var firstFrame = await capture.CaptureAsync(window, cancellationToken);
        var first = await openingReader.ReadAsync(firstFrame, cancellationToken);
        if (!first.IsOpeningPage || first.Observation is null)
        {
            return null;
        }

        await Task.Delay(options.StableFrameDelay, cancellationToken);
        var refreshed = windowService.Refresh(window.Handle);
        if (refreshed is null)
        {
            return null;
        }

        var secondFrame = await capture.CaptureAsync(refreshed, cancellationToken);
        var second = await openingReader.ReadAsync(secondFrame, cancellationToken);
        if (!second.IsOpeningPage || second.Observation is null)
        {
            return null;
        }

        var firstInvestment = first.Observation.InvestmentEnvironment?.Id;
        var secondInvestment = second.Observation.InvestmentEnvironment?.Id;
        var sameInvestment = string.Equals(
            firstInvestment,
            secondInvestment,
            StringComparison.OrdinalIgnoreCase);
        var sameCompetitors =
            first.Observation.CompetitorIds.SetEquals(second.Observation.CompetitorIds);
        var sameModifiers = first.Observation.ModifierIds.SetEquals(second.Observation.ModifierIds);

        return sameInvestment && sameCompetitors && sameModifiers
            ? second.Observation
            : null;
    }

    private async Task<ActionResult> ExecuteRerollWorkflowAsync(
        GameWindowInfo originalWindow,
        OpeningRerollOptions options,
        CancellationToken cancellationToken)
    {
        if (recognitionConfig.RerollSteps.Count == 0)
        {
            return ActionResult.Failure("尚未配置重开页面步骤，已阻止点击。");
        }

        foreach (var step in recognitionConfig.RerollSteps)
        {
            var match = await WaitForTemplateAsync(
                originalWindow.Handle,
                step.Target,
                TimeSpan.FromMilliseconds(step.TimeoutMilliseconds),
                options.PollInterval,
                cancellationToken);
            if (match is null)
            {
                return ActionResult.Failure($"未找到重开步骤目标：{step.Target.DisplayName}");
            }

            var window = windowService.Refresh(originalWindow.Handle);
            if (window is null)
            {
                return ActionResult.Failure("执行重开步骤前游戏窗口失效。");
            }

            var action = await input.ClickAsync(
                new ClickTarget(match.Id, match.DisplayName, window, match.ClientBounds),
                new ActionPolicy(),
                cancellationToken);
            if (!action.Succeeded)
            {
                return action;
            }

            if (step.ExpectedAfter is not null)
            {
                var expected = await WaitForTemplateAsync(
                    originalWindow.Handle,
                    step.ExpectedAfter,
                    TimeSpan.FromMilliseconds(step.TimeoutMilliseconds),
                    options.PollInterval,
                    cancellationToken);
                if (expected is null)
                {
                    return ActionResult.Failure(
                        $"点击“{step.Target.DisplayName}”后未出现预期页面标记：" +
                        step.ExpectedAfter.DisplayName);
                }
            }
        }

        return ActionResult.Success("重开流程的所有步骤均已验证。");
    }

    private async Task<bool> WaitForOpeningPageAsync(
        nint windowHandle,
        OpeningRerollOptions options,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = windowService.Refresh(windowHandle);
            if (window is null)
            {
                return false;
            }

            var frame = await capture.CaptureAsync(window, cancellationToken);
            if (matcher.Find(frame, recognitionConfig.OpeningPageAnchor) is not null)
            {
                return true;
            }

            await Task.Delay(options.PollInterval, cancellationToken);
        }

        return false;
    }

    private async Task<TemplateMatchResult?> WaitForTemplateAsync(
        nint windowHandle,
        TemplateDefinition template,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = windowService.Refresh(windowHandle);
            if (window is null)
            {
                return null;
            }

            var frame = await capture.CaptureAsync(window, cancellationToken);
            var match = matcher.Find(frame, template);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return null;
    }

    private void Publish(
        OpeningRerollState state,
        int rerollCount,
        OpeningObservation? observation,
        OpeningDecision? decision,
        string message)
    {
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            state.ToString(),
            message));
        ProgressChanged?.Invoke(
            this,
            new OpeningRerollProgress(state, rerollCount, observation, decision, message));
    }

    private OpeningRerollResult Result(
        OpeningRerollState state,
        int rerollCount,
        OpeningObservation? observation,
        OpeningDecision? decision,
        string message)
    {
        Publish(state, rerollCount, observation, decision, message);
        return new OpeningRerollResult(state, rerollCount, observation, decision, message);
    }
}
