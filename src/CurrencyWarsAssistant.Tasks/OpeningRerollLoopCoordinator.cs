using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

public enum RejectedOpeningRecoveryStatus
{
    Recovered,
    RecoveryNotConfigured,
    Failed
}

public sealed record RejectedOpeningRecoveryResult(
    RejectedOpeningRecoveryStatus Status,
    string Message)
{
    public static RejectedOpeningRecoveryResult Recovered(string message) =>
        new(RejectedOpeningRecoveryStatus.Recovered, message);

    public static RejectedOpeningRecoveryResult NotConfigured(string message) =>
        new(RejectedOpeningRecoveryStatus.RecoveryNotConfigured, message);

    public static RejectedOpeningRecoveryResult Failed(string message) =>
        new(RejectedOpeningRecoveryStatus.Failed, message);
}

public interface IRejectedOpeningRecovery
{
    Task<RejectedOpeningRecoveryResult> RecoverAsync(
        nint windowHandle,
        OpeningSnapshot rejectedOpening,
        OpeningFilterEvaluation evaluation,
        CancellationToken cancellationToken);
}

public interface IAbandonSettlementRecovery
{
    Task<RejectedOpeningRecoveryResult>
        CompleteFromAbandonSettlementPromptAsync(
            nint windowHandle,
            CancellationToken cancellationToken);
}

/// <summary>
/// Safe placeholder until the exact two-step restart UI has been captured.
/// It never sends keyboard or mouse input.
/// </summary>
public sealed class NotConfiguredRejectedOpeningRecovery : IRejectedOpeningRecovery
{
    public Task<RejectedOpeningRecoveryResult> RecoverAsync(
        nint windowHandle,
        OpeningSnapshot rejectedOpening,
        OpeningFilterEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RejectedOpeningRecoveryResult.NotConfigured(
            "尚未配置不合格开局的返回操作，已停在投资环境页面。"));
    }
}

public sealed class OpeningRerollLoopOptions
{
    public int? MaximumRounds { get; init; }
    public TimeSpan? MaximumRuntime { get; init; }
    public bool DeployMatchedOpening { get; init; }
    public bool CompleteRewardStages { get; init; }
    /// <summary>
    /// Optional reward-stage deployment allow-list. It must not be populated
    /// from the user's retain-or-buy lists. Null or empty uses the built-in
    /// reward formation roster.
    /// </summary>
    public IReadOnlySet<string>? InitialRewardCharacterNames { get; init; }
    public PreparationBenchSaleMode BenchSaleMode { get; init; }
    public int BenchSaleInterestThreshold { get; init; } = 10;
    public RewardStageAutomationOptions RewardStage { get; init; } = new();
    public bool EnableUnknownPageEscapeRecovery { get; init; } = true;
    public CurrencyWarsGameMode GameMode { get; init; } = CurrencyWarsGameMode.Standard;
    /// <summary>
    /// 快速刷开局模式：Stable（完整验证）/ Fast（去验证）/ Extreme（无脑部署）。
    /// </summary>
    public FastRerollMode FastReroll { get; init; } = FastRerollMode.Stable;
}

public enum OpeningRerollLoopState
{
    Navigating,
    Evaluating,
    Recovering,
    Matched,
    WaitingForRecovery,
    RecoveryFailed,
    NavigationFailed,
    MaximumRoundsReached,
    MaximumRuntimeReached
}

public enum OpeningRerollMilestone
{
    None,
    AcceptedOpeningReadyForRecording,
    AcceptedOpeningRejected
}

public sealed record OpeningRerollLoopProgress(
    OpeningRerollLoopState State,
    int Round,
    string Message,
    OpeningRerollMilestone Milestone = OpeningRerollMilestone.None,
    OpeningSnapshot? Snapshot = null);

public static class OpeningNavigationRetryPolicy
{
    public const int MaximumAttemptsPerRound = 6;
    public const int MaximumRepeatedFingerprintCount = 3;

    public static bool IsExhausted(
        int totalAttempts,
        int repeatedFingerprintCount) =>
        totalAttempts >= MaximumAttemptsPerRound ||
        repeatedFingerprintCount >= MaximumRepeatedFingerprintCount;
}

public sealed record OpeningRerollLoopResult(
    OpeningRerollLoopState FinalState,
    int CompletedRounds,
    OpeningSnapshot? Snapshot,
    OpeningFilterEvaluation? Evaluation,
    CurrencyWarsNavigationResult? Navigation,
    RejectedOpeningRecoveryResult? Recovery,
    string Message)
{
    public bool Succeeded => FinalState == OpeningRerollLoopState.Matched;
}

public sealed class OpeningRerollLoopCoordinator(
    ICurrencyWarsOpeningNavigator navigator,
    OpeningFilterEvaluator evaluator,
    IRejectedOpeningRecovery recovery,
    ITaskEventSink eventSink,
    IGameForegroundGuard? foregroundGuard = null,
    IPreparationBoardController? preparationBoardController = null,
    IRewardStageAutomationController? rewardStageController = null,
    Func<int, int>? randomIndexSelector = null,
    IPassiveRecoveryMonitor? passiveRecoveryMonitor = null)
{
    private TimeSpan _pauseBaseline;

    private DateTimeOffset ActiveUtcNow =>
        DateTimeOffset.UtcNow -
        ((foregroundGuard?.TotalPausedDuration ?? TimeSpan.Zero) -
         _pauseBaseline);

    public event EventHandler<OpeningRerollLoopProgress>? ProgressChanged;

    public async Task<OpeningRerollLoopResult> RunAsync(
        nint windowHandle,
        OpeningFilterSet filters,
        OpeningRerollLoopOptions options,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            OpeningRerollLoopResult result;
            try
            {
                result = await RunUntilTerminalAsync(
                    windowHandle,
                    filters,
                    options,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (!ShouldMonitorAfterFailure(options))
                {
                    throw;
                }

                eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Error,
                    "RerollLoopExceptionEnteringPassiveMonitor",
                    $"自动流程异常，转入只读页面监测：{exception.Message}"));
                await passiveRecoveryMonitor!.WaitForSafeEntryPageAsync(
                    windowHandle,
                    cancellationToken);
                continue;
            }

            if (result.Succeeded || !ShouldMonitorAfterFailure(options))
            {
                return result;
            }

            Publish(
                OpeningRerollLoopState.WaitingForRecovery,
                result.CompletedRounds,
                "本轮未完成，转入只读页面监测；只在安全入口页稳定出现后恢复。" );
            await passiveRecoveryMonitor!.WaitForSafeEntryPageAsync(
                windowHandle,
                cancellationToken);
        }
    }

    private bool ShouldMonitorAfterFailure(OpeningRerollLoopOptions options) =>
        passiveRecoveryMonitor is not null &&
        options.MaximumRounds is null &&
        options.MaximumRuntime is null;

    private async Task<OpeningRerollLoopResult> RunUntilTerminalAsync(
        nint windowHandle,
        OpeningFilterSet filters,
        OpeningRerollLoopOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumRounds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "最大刷取轮数必须大于零。");
        }

        if (options.MaximumRuntime is { } configuredMaximumRuntime &&
            configuredMaximumRuntime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "最长运行时间必须大于零。");
        }

        _pauseBaseline =
            foregroundGuard?.TotalPausedDuration ?? TimeSpan.Zero;
        var deadline = options.MaximumRuntime is { } maximumRuntime
            ? ActiveUtcNow + maximumRuntime
            : (DateTimeOffset?)null;
        for (var round = 1; ; round++)
        {
            var rewardStageAlreadyRecoveredToHome = false;
            var acceptedOpeningRecordingStarted = false;

            void RejectAcceptedOpeningRecording(string reason)
            {
                if (!acceptedOpeningRecordingStarted)
                {
                    return;
                }

                acceptedOpeningRecordingStarted = false;
                Publish(
                    OpeningRerollLoopState.Evaluating,
                    round,
                    reason,
                    OpeningRerollMilestone.AcceptedOpeningRejected);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (deadline is { } runtimeDeadline &&
                ActiveUtcNow >= runtimeDeadline)
            {
                return Result(
                    OpeningRerollLoopState.MaximumRuntimeReached,
                    round - 1,
                    null,
                    null,
                    null,
                    null,
                    "已达到最长刷取时间。");
            }

            Publish(
                OpeningRerollLoopState.Navigating,
                round,
                $"第 {round} 轮：正在导航并识别完整开局信息。");
            CurrencyWarsNavigationResult navigation;
            var navigationAttempt = 0;
            var repeatedFailureCount = 0;
            string? previousFailureFingerprint = null;
            while (true)
            {
                navigationAttempt++;
                try
                {
                    using var operationCancellation =
                        CreateOperationCancellation(
                            deadline,
                            cancellationToken);
                    navigation = await navigator.RunAsync(
                        windowHandle,
                        new CurrencyWarsNavigationOptions
                        {
                            StopAfterOpeningRecognition = true,
                            StopAtPreparation = true,
                            EnableUnknownPageEscapeRecovery =
                                options.EnableUnknownPageEscapeRecovery,
                            FastPathFromHome =
                                options.GameMode == CurrencyWarsGameMode.Standard,
                            FastReroll = options.FastReroll,
                            GameMode = options.GameMode,
                            PreferredInvestmentEnvironmentIds =
                                GetAllCandidateInvestmentEnvironmentIds(filters)
                        },
                        operationCancellation.Token);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested &&
                          deadline is { } navigationDeadline &&
                          ActiveUtcNow >= navigationDeadline)
                {
                    return Result(
                        OpeningRerollLoopState.MaximumRuntimeReached,
                        round - 1,
                        null,
                        null,
                        null,
                        null,
                        "导航过程中达到最长刷取时间。");
                }

                if (navigation.FinalState is
                        CurrencyWarsNavigationState.OpeningRecognized or
                        CurrencyWarsNavigationState
                            .InvestmentEnvironmentFallbackSelected &&
                    navigation.EnemyOverview is not null &&
                    navigation.InvestmentEnvironments is not null)
                {
                    break;
                }

                if (!IsRetriableNavigationFailure(navigation.FinalState))
                {
                    return Result(
                        OpeningRerollLoopState.NavigationFailed,
                        round - 1,
                        null,
                        null,
                        navigation,
                        null,
                        $"第 {round} 轮导航或识别失败：{navigation.Message}");
                }

                var failureFingerprint =
                    $"{navigation.FinalState}|{navigation.PageId}|" +
                    $"{navigation.Message}";
                if (string.Equals(
                        previousFailureFingerprint,
                        failureFingerprint,
                        StringComparison.Ordinal))
                {
                    repeatedFailureCount++;
                }
                else
                {
                    previousFailureFingerprint = failureFingerprint;
                    repeatedFailureCount = 1;
                }

                if (OpeningNavigationRetryPolicy.IsExhausted(
                        navigationAttempt,
                        repeatedFailureCount))
                {
                    return Result(
                        OpeningRerollLoopState.NavigationFailed,
                        round - 1,
                        null,
                        null,
                        navigation,
                        null,
                        $"第 {round} 轮导航已达到有限重试预算：" +
                        $"总计 {navigationAttempt}/" +
                        $"{OpeningNavigationRetryPolicy.MaximumAttemptsPerRound} 次，" +
                        $"同指纹连续 {repeatedFailureCount}/" +
                        $"{OpeningNavigationRetryPolicy.MaximumRepeatedFingerprintCount} 次；" +
                        "所有安全识别备用方案均已尝试，停止无效重复。");
                }

                Publish(
                    OpeningRerollLoopState.Navigating,
                    round,
                    $"第 {round} 轮第 {navigationAttempt} 次识别未稳定，" +
                    $"将从当前页面继续重试：{navigation.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            var snapshot = ToSnapshot(navigation);
            var degradedInvestmentEnvironment =
                navigation.FinalState == CurrencyWarsNavigationState
                    .InvestmentEnvironmentFallbackSelected;
            Publish(
                OpeningRerollLoopState.Evaluating,
                round,
                degradedInvestmentEnvironment
                    ? $"第 {round} 轮投资环境识别不完整，已任选一项进入 1-1；" +
                      "不调用开局筛选器猜测结果，本轮直接按未命中进入重开。"
                    : $"第 {round} 轮：正在评估 {snapshot.InvestmentEnvironmentIds.Count} 个投资环境、" +
                      $"{snapshot.CompetitorIds.Count} 个敌人阵营和 " +
                      $"{snapshot.EnemyModifierIds.Count} 个负面词条。");
            var evaluation = degradedInvestmentEnvironment
                ? InvestmentEnvironmentFallbackEvaluation(navigation.Message)
                : evaluator.Evaluate(snapshot, filters);
            if (evaluation.Matched)
            {
                var selectedProfile = SelectMatchedProfile(
                    filters,
                    evaluation);
                var selectedInvestmentEnvironmentIds =
                    SelectInvestmentEnvironmentIds(
                        filters,
                        selectedProfile,
                        snapshot);
                if (selectedProfile is not null)
                {
                    Publish(
                        OpeningRerollLoopState.Evaluating,
                        round,
                        $"本轮命中 {evaluation.EffectiveMatchedProfileIds.Count} 组方案，" +
                        $"已随机锁定“{selectedProfile.DisplayName}”；" +
                        "后续投资环境与投资策略只跟随该方案。");
                }

                if (!options.DeployMatchedOpening)
                {
                    return Result(
                        OpeningRerollLoopState.Matched,
                        round,
                        snapshot,
                        evaluation,
                        navigation,
                        null,
                        $"第 {round} 轮开局满足全部条件，已停在投资环境页面。");
                }

                Publish(
                    OpeningRerollLoopState.Navigating,
                    round,
                    $"第 {round} 轮开局条件满足，正在选择投资环境并进入 1-1。",
                    OpeningRerollMilestone.AcceptedOpeningReadyForRecording,
                    snapshot);
                acceptedOpeningRecordingStarted = true;
                using var operationCancellation =
                    CreateOperationCancellation(
                        deadline,
                        cancellationToken);
                navigation = await navigator.RunAsync(
                    windowHandle,
                    new CurrencyWarsNavigationOptions
                    {
                        StopAfterOpeningRecognition = false,
                        StopAtPreparation = true,
                        EnableUnknownPageEscapeRecovery =
                            options.EnableUnknownPageEscapeRecovery,
                        FastPathFromHome =
                            options.GameMode == CurrencyWarsGameMode.Standard,
                        FastReroll = options.FastReroll,
                        GameMode = options.GameMode,
                        PreferredInvestmentEnvironmentIds =
                            selectedInvestmentEnvironmentIds
                    },
                    operationCancellation.Token);
                if (navigation.FinalState !=
                    CurrencyWarsNavigationState.ReachedPreparation)
                {
                    RejectAcceptedOpeningRecording(
                        $"第 {round} 轮进入 1-1 失败；候选记录不进入正式对局历史。");
                    return Result(
                        OpeningRerollLoopState.NavigationFailed,
                        round,
                        snapshot,
                        evaluation,
                        navigation,
                        null,
                        $"第 {round} 轮开局已满足条件，但进入 1-1 失败：" +
                        navigation.Message);
                }

                if (preparationBoardController is null)
                {
                    RejectAcceptedOpeningRecording(
                        $"第 {round} 轮缺少备战控制器；候选记录不进入正式对局历史。");
                    return Result(
                        OpeningRerollLoopState.NavigationFailed,
                        round,
                        snapshot,
                        evaluation,
                        navigation,
                        null,
                        "已到达 1-1，但未配置备战席识别与布阵控制器。");
                }

                var preparationOptions = BuildPreparationOptions(
                    options,
                    deferBenchSaleUntilShopCompletion:
                        options.CompleteRewardStages);
                var preparation = await preparationBoardController.PrepareAsync(
                    windowHandle,
                    preparationOptions,
                    operationCancellation.Token);
                if (preparation.Succeeded)
                {
                    if (!options.CompleteRewardStages)
                    {
                        return Result(
                            OpeningRerollLoopState.Matched,
                            round,
                            snapshot,
                            evaluation,
                            navigation,
                            null,
                            $"第 {round} 轮开局满足全部条件；{preparation.Message}");
                    }

                    if (rewardStageController is null)
                    {
                        RejectAcceptedOpeningRecording(
                            $"第 {round} 轮缺少奖励关控制器；候选记录不进入正式对局历史。");
                        return Result(
                            OpeningRerollLoopState.NavigationFailed,
                            round,
                            snapshot,
                            evaluation,
                            navigation,
                            null,
                            "已完成 1-1 布阵，但未配置奖励关自动化控制器。");
                    }

                    var rewardStages = await rewardStageController.RunAsync(
                        windowHandle,
                        BuildRewardStageOptions(
                            options.RewardStage,
                            selectedProfile,
                            options,
                            preparation,
                            navigation),
                        operationCancellation.Token);
                    if (rewardStages.Status == RewardStageAutomationStatus
                            .RewardStagesCompletedAwaitingManualStrategy)
                    {
                        return Result(
                            OpeningRerollLoopState.Matched,
                            round,
                            snapshot,
                            evaluation,
                            navigation,
                            null,
                            rewardStages.Message);
                    }

                    if (rewardStages.Succeeded)
                    {
                        return Result(
                            OpeningRerollLoopState.Matched,
                            round,
                            snapshot,
                            evaluation,
                            navigation,
                            null,
                            $"第 {round} 轮开局与投资策略均满足条件；" +
                            rewardStages.Message);
                    }

                    if (!rewardStages.ShouldReroll)
                    {
                        RejectAcceptedOpeningRecording(
                            $"第 {round} 轮奖励关无法安全完成；候选记录不进入正式对局历史。");
                        return Result(
                            OpeningRerollLoopState.NavigationFailed,
                            round,
                            snapshot,
                            evaluation,
                            navigation,
                            null,
                            "奖励关自动流程未能安全完成：" +
                            rewardStages.Message);
                    }

                    evaluation = new OpeningFilterEvaluation(
                        false,
                        [.. evaluation.Reasons, rewardStages.Message],
                        evaluation.MatchedConditions,
                        evaluation.ViolatedConditions);
                    rewardStageAlreadyRecoveredToHome =
                        rewardStages.AlreadyRecoveredToHome;
                    Publish(
                        OpeningRerollLoopState.Evaluating,
                        round,
                        rewardStageAlreadyRecoveredToHome
                            ? $"第 {round} 轮奖励战斗超时恢复已返回主页；" +
                              "不会再次执行放弃结算，直接继续重刷。"
                            : $"第 {round} 轮奖励关需要安全重刷；将退出本局并继续。");
                }

                if (!preparation.Succeeded &&
                    !preparation.ShouldReroll)
                {
                    RejectAcceptedOpeningRecording(
                        $"第 {round} 轮备战无法安全完成；候选记录不进入正式对局历史。");
                    return Result(
                        OpeningRerollLoopState.NavigationFailed,
                        round,
                        snapshot,
                        evaluation,
                        navigation,
                        null,
                        $"第 {round} 轮已到达 1-1，但未能安全完成布阵：" +
                        preparation.Message);
                }

                if (!preparation.Succeeded)
                {
                    evaluation = new OpeningFilterEvaluation(
                        false,
                        [.. evaluation.Reasons, preparation.Message],
                        evaluation.MatchedConditions,
                        evaluation.ViolatedConditions);
                    Publish(
                        OpeningRerollLoopState.Evaluating,
                        round,
                        $"第 {round} 轮投资环境满足条件，但初始备战席不满足奖励关阵容要求，" +
                        "将退出本局并继续重刷。");
                }
            }

            RejectAcceptedOpeningRecording(
                $"第 {round} 轮奖励阶段未达标；候选记录按现有规则结束，不进入正式节点历史。");

            if (options.MaximumRounds is { } maximumRounds &&
                round >= maximumRounds)
            {
                return Result(
                    OpeningRerollLoopState.MaximumRoundsReached,
                    round,
                    snapshot,
                    evaluation,
                    navigation,
                    null,
                    $"第 {round} 轮开局不合格，并已达到最大刷取轮数。");
            }

            if (deadline is { } evaluationDeadline &&
                ActiveUtcNow >= evaluationDeadline)
            {
                return Result(
                    OpeningRerollLoopState.MaximumRuntimeReached,
                    round,
                    snapshot,
                    evaluation,
                    navigation,
                    null,
                    "当前开局不合格，并已达到最长刷取时间。");
            }

            Publish(
                OpeningRerollLoopState.Recovering,
                round,
                $"第 {round} 轮开局不合格，正在请求安全返回主界面。");
            if (rewardStageAlreadyRecoveredToHome)
            {
                Publish(
                    OpeningRerollLoopState.Recovering,
                    round,
                    $"第 {round} 轮已由奖励战斗超时恢复确认返回主页；跳过重复恢复。");
                continue;
            }

            RejectedOpeningRecoveryResult recoveryResult;
            try
            {
                using var operationCancellation =
                    CreateOperationCancellation(
                        deadline,
                        cancellationToken);
                recoveryResult = await recovery.RecoverAsync(
                    windowHandle,
                    snapshot,
                    evaluation,
                    operationCancellation.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      deadline is { } recoveryDeadline &&
                      ActiveUtcNow >= recoveryDeadline)
            {
                return Result(
                    OpeningRerollLoopState.MaximumRuntimeReached,
                    round,
                    snapshot,
                    evaluation,
                    navigation,
                    null,
                    "返回主界面的过程中达到最长刷取时间。");
            }
            if (recoveryResult.Status ==
                RejectedOpeningRecoveryStatus.RecoveryNotConfigured)
            {
                return Result(
                    OpeningRerollLoopState.WaitingForRecovery,
                    round,
                    snapshot,
                    evaluation,
                    navigation,
                    recoveryResult,
                    recoveryResult.Message);
            }

            if (recoveryResult.Status != RejectedOpeningRecoveryStatus.Recovered)
            {
                return Result(
                    OpeningRerollLoopState.RecoveryFailed,
                    round,
                    snapshot,
                    evaluation,
                    navigation,
                    recoveryResult,
                    recoveryResult.Message);
            }

            Publish(
                OpeningRerollLoopState.Recovering,
                round,
                $"第 {round} 轮返回完成，准备开始下一轮。");
        }
    }

    private static OpeningSnapshot ToSnapshot(
        CurrencyWarsNavigationResult navigation) =>
        new(
            navigation.InvestmentEnvironments!.InvestmentEnvironments
                .Select(item => item.Id)
                .ToArray(),
            navigation.EnemyOverview!.RecognizedCompetitors
                .Select(item => item.Id)
                .ToArray(),
            navigation.EnemyOverview.RecognizedEnemyModifiers
                .Select(item => item.Id)
                .ToArray());

    private OpeningFilterProfile? SelectMatchedProfile(
        OpeningFilterSet filters,
        OpeningFilterEvaluation evaluation)
    {
        var matchedIds = evaluation.EffectiveMatchedProfileIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var matched = filters.Profiles
            .Where(profile =>
                profile.IsEnabled &&
                matchedIds.Contains(profile.Id))
            .ToArray();
        if (matched.Length == 0)
        {
            return null;
        }

        var index = randomIndexSelector?.Invoke(matched.Length) ??
                    Random.Shared.Next(matched.Length);
        return matched[Math.Clamp(index, 0, matched.Length - 1)];
    }

    private IReadOnlySet<string> SelectInvestmentEnvironmentIds(
        OpeningFilterSet filters,
        OpeningFilterProfile? selectedProfile,
        OpeningSnapshot snapshot)
    {
        var profileEnvironmentIds =
            selectedProfile?.AcceptedInvestmentEnvironmentIds ?? [];
        var configured = selectedProfile is null ||
                         profileEnvironmentIds.Count == 0
            ? filters.InvestmentEnvironments
                .Where(item => item.State == OpeningFilterState.Require)
                .Select(item => item.Id)
                .ToArray()
            : profileEnvironmentIds.ToArray();
        var offered = configured
            .Where(id => snapshot.InvestmentEnvironmentIds.Contains(
                id,
                StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (offered.Length == 0)
        {
            return configured.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var index = randomIndexSelector?.Invoke(offered.Length) ??
                    Random.Shared.Next(offered.Length);
        return new HashSet<string>(
            [offered[Math.Clamp(index, 0, offered.Length - 1)]],
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string>
        GetAllCandidateInvestmentEnvironmentIds(OpeningFilterSet filters) =>
        filters.InvestmentEnvironments
            .Where(item => item.State == OpeningFilterState.Require)
            .Select(item => item.Id)
            .Concat(
                filters.Profiles
                    .Where(profile => profile.IsEnabled)
                    .SelectMany(profile =>
                        profile.AcceptedInvestmentEnvironmentIds))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static OpeningFilterEvaluation
        InvestmentEnvironmentFallbackEvaluation(string navigationMessage)
    {
        var outcome = new OpeningConditionOutcome(
            "investment_environment_recognition_fallback",
            "投资环境识别降级",
            OpeningConditionKind.InvestmentEnvironment,
            OpeningFilterState.Require,
            navigationMessage);
        return new OpeningFilterEvaluation(
            false,
            [navigationMessage],
            [],
            [outcome]);
    }

    private static RewardStageAutomationOptions BuildRewardStageOptions(
        RewardStageAutomationOptions configured,
        OpeningFilterProfile? selectedProfile,
        OpeningRerollLoopOptions loopOptions,
        PreparationBoardResult preparation,
        CurrencyWarsNavigationResult navigation) =>
        new()
        {
            EnableEarlyStrongFormationPurchase =
                configured.EnableEarlyStrongFormationPurchase,
            EnableGalaxyScholarRewardStrategy =
                configured.EnableGalaxyScholarRewardStrategy,
            AutoPurchaseCharacterNames =
                configured.AutoPurchaseCharacterNames,
            RetainedCharacterNames =
                configured.RetainedCharacterNames,
            InitialOwnedCharacters = preparation.Bench,
            FormationCharacterNames =
                ResolveFormationCharacterNames(
                    loopOptions.InitialRewardCharacterNames),
            InitialFormationPlacements = preparation.Placements,
            PreparationCompletionOptions = BuildPreparationOptions(
                loopOptions,
                deferBenchSaleUntilShopCompletion: false),
            PreferredInvestmentStrategyIds =
                selectedProfile is not null &&
                selectedProfile.PreferredInvestmentStrategyIds.Count > 0
                    ? selectedProfile.PreferredInvestmentStrategyIds.ToHashSet(
                        StringComparer.OrdinalIgnoreCase)
                    : configured.PreferredInvestmentStrategyIds,
            SelectedInvestmentEnvironmentId =
                navigation.SelectedInvestmentEnvironmentId
        };

    private static PreparationBoardOptions BuildPreparationOptions(
        OpeningRerollLoopOptions options,
        bool deferBenchSaleUntilShopCompletion) =>
        new()
        {
            EligibleCharacterNames = ResolveFormationCharacterNames(
                options.InitialRewardCharacterNames),
            EnableGalaxyScholarPairFormation =
                options.RewardStage.EnableGalaxyScholarRewardStrategy,
            BenchSaleMode = options.BenchSaleMode,
            InterestThreshold = options.BenchSaleInterestThreshold,
            RetainedCharacterNames =
                options.RewardStage.RetainedCharacterNames,
            EnableEarlyStrongFormationRetention =
                options.RewardStage.EnableEarlyStrongFormationPurchase,
            DeferBenchSaleUntilShopCompletion =
                deferBenchSaleUntilShopCompletion,
            FastReroll = options.FastReroll
        };

    private static IReadOnlySet<string> ResolveFormationCharacterNames(
        IReadOnlySet<string>? configured) =>
        configured is { Count: > 0 }
            ? configured
            : InitialRewardFormationPlanner.DefaultEligibleCharacterNames;

    private CancellationTokenSource CreateOperationCancellation(
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (foregroundGuard is null &&
            deadline is { } runtimeDeadline)
        {
            var remaining = runtimeDeadline - ActiveUtcNow;
            source.CancelAfter(
                remaining > TimeSpan.Zero
                    ? remaining
                    : TimeSpan.FromMilliseconds(1));
        }

        return source;
    }

    private static bool IsRetriableNavigationFailure(
        CurrencyWarsNavigationState state) =>
        state is CurrencyWarsNavigationState.RecognitionIncomplete
            or CurrencyWarsNavigationState.TimedOut
            or CurrencyWarsNavigationState.InputBlocked;

    private void Publish(
        OpeningRerollLoopState state,
        int round,
        string message,
        OpeningRerollMilestone milestone = OpeningRerollMilestone.None,
        OpeningSnapshot? snapshot = null)
    {
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            state.ToString(),
            message));
        ProgressChanged?.Invoke(
            this,
            new OpeningRerollLoopProgress(
                state,
                round,
                message,
                milestone,
                snapshot));
    }

    private OpeningRerollLoopResult Result(
        OpeningRerollLoopState state,
        int rounds,
        OpeningSnapshot? snapshot,
        OpeningFilterEvaluation? evaluation,
        CurrencyWarsNavigationResult? navigation,
        RejectedOpeningRecoveryResult? recoveryResult,
        string message)
    {
        Publish(state, rounds, message);
        return new OpeningRerollLoopResult(
            state,
            rounds,
            snapshot,
            evaluation,
            navigation,
            recoveryResult,
            message);
    }
}
