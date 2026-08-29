using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class OpeningRerollLoopCoordinatorTests
{
    [Fact]
    public async Task AutomaticRunMonitorsTerminalFailureThenResumesAtEntry()
    {
        var navigator = new FakeNavigator(
            new CurrencyWarsNavigationResult(
                CurrencyWarsNavigationState.UnknownPage,
                null,
                "unknown"),
            CompleteNavigation());
        var monitor = new FakePassiveRecoveryMonitor();
        var coordinator = CreateCoordinator(
            navigator,
            new FakeRecovery(RejectedOpeningRecoveryResult.Recovered("unused")),
            passiveRecoveryMonitor: monitor);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Equal(1, monitor.Calls);
        Assert.Equal(2, navigator.Options.Count);
    }

    [Fact]
    public async Task UserCancellationExitsPassiveMonitoring()
    {
        var monitor = new FakePassiveRecoveryMonitor(blockUntilCancelled: true);
        var coordinator = CreateCoordinator(
            new FakeNavigator(new CurrencyWarsNavigationResult(
                CurrencyWarsNavigationState.UnknownPage,
                null,
                "unknown")),
            new FakeRecovery(RejectedOpeningRecoveryResult.Recovered("unused")),
            passiveRecoveryMonitor: monitor);
        using var cancellation = new CancellationTokenSource();

        var run = coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            cancellation.Token);
        await monitor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task EmptyFiltersMatchFirstCompleteOpeningAtInvestmentPage()
    {
        var navigator = new FakeNavigator(CompleteNavigation());
        var recovery = new FakeRecovery(RejectedOpeningRecoveryResult.Recovered("unused"));
        var coordinator = CreateCoordinator(navigator, recovery);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Equal(1, result.CompletedRounds);
        Assert.Equal(["environment_1", "environment_2", "environment_3"],
            result.Snapshot!.InvestmentEnvironmentIds);
        Assert.Empty(recovery.Snapshots);
        Assert.True(navigator.Options.Single().StopAfterOpeningRecognition);
    }

    [Fact]
    public async Task RejectedOpeningUsesSafeNotConfiguredRecoveryAndWaits()
    {
        var recovery = new NotConfiguredRejectedOpeningRecovery();
        var coordinator = CreateCoordinator(
            new FakeNavigator(CompleteNavigation()),
            recovery);
        var filters = new OpeningFilterSet
        {
            Competitors =
            [
                new OpeningItemFilter(
                    "competitor_1",
                    "competitor_1",
                    OpeningFilterState.Reject)
            ]
        };

        var result = await coordinator.RunAsync(
            1,
            filters,
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.WaitingForRecovery, result.FinalState);
        Assert.Equal(
            RejectedOpeningRecoveryStatus.RecoveryNotConfigured,
            result.Recovery!.Status);
        Assert.Equal("investment_environment", result.Navigation!.PageId);
    }

    [Fact]
    public async Task RecoveredRejectedOpeningsStopAtMaximumRounds()
    {
        var navigator = new FakeNavigator(
            CompleteNavigation(),
            CompleteNavigation());
        var recovery = new FakeRecovery(
            RejectedOpeningRecoveryResult.Recovered("recovered"));
        var coordinator = CreateCoordinator(navigator, recovery);
        var filters = new OpeningFilterSet
        {
            EnemyModifiers =
            [
                new OpeningItemFilter(
                    "missing_required",
                    "missing_required",
                    OpeningFilterState.Require)
            ]
        };

        var result = await coordinator.RunAsync(
            1,
            filters,
            new OpeningRerollLoopOptions { MaximumRounds = 2 },
            CancellationToken.None);

        Assert.Equal(
            OpeningRerollLoopState.MaximumRoundsReached,
            result.FinalState);
        Assert.Equal(2, result.CompletedRounds);
        Assert.Single(recovery.Snapshots);
        Assert.Equal(2, navigator.Options.Count);
    }

    [Fact]
    public async Task DefaultAutomaticRunContinuesBeyondFormerRoundLimit()
    {
        var results = Enumerable
            .Repeat(CompleteNavigation(), 50)
            .Append(CompleteNavigation("safe_competitor"))
            .ToArray();
        var navigator = new FakeNavigator(results);
        var recovery = new FakeRecovery(
            RejectedOpeningRecoveryResult.Recovered("recovered"));
        var coordinator = CreateCoordinator(navigator, recovery);
        var filters = new OpeningFilterSet
        {
            Competitors =
            [
                new OpeningItemFilter(
                    "competitor_1",
                    "competitor_1",
                    OpeningFilterState.Reject)
            ]
        };

        var result = await coordinator.RunAsync(
            1,
            filters,
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Equal(51, result.CompletedRounds);
        Assert.Equal(50, recovery.Snapshots.Count);
        Assert.Equal(51, navigator.Options.Count);
    }

    [Fact]
    public void DefaultAutomaticRunHasNoOverallLimits()
    {
        var options = new OpeningRerollLoopOptions();

        Assert.Null(options.MaximumRounds);
        Assert.Null(options.MaximumRuntime);
    }

    [Fact]
    public async Task NavigationFailureDoesNotRequestRecovery()
    {
        var navigator = new FakeNavigator(new CurrencyWarsNavigationResult(
            CurrencyWarsNavigationState.WindowUnavailable,
            "enemy_overview",
            "window unavailable"));
        var recovery = new FakeRecovery(
            RejectedOpeningRecoveryResult.Recovered("unused"));
        var coordinator = CreateCoordinator(navigator, recovery);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.NavigationFailed, result.FinalState);
        Assert.Empty(recovery.Snapshots);
    }

    [Fact]
    public async Task RetriableRecognitionFailureContinuesFromCurrentPage()
    {
        var navigator = new FakeNavigator(
            new CurrencyWarsNavigationResult(
                CurrencyWarsNavigationState.RecognitionIncomplete,
                "enemy_overview",
                "incomplete"),
            CompleteNavigation());
        var recovery = new FakeRecovery(
            RejectedOpeningRecoveryResult.Recovered("unused"));
        var coordinator = CreateCoordinator(navigator, recovery);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Equal(2, navigator.Options.Count);
        Assert.Empty(recovery.Snapshots);
    }

    [Fact]
    public async Task InvestmentEnvironmentFallbackForcesRerollWithoutEvaluatorGuess()
    {
        var partialEnvironments = new InvestmentEnvironmentReadResult(
        [
            new RecognizedOpeningItem(0, "?", null),
            new RecognizedOpeningItem(1, "environment_2", new ObservedItem(
                "environment_2",
                "environment_2",
                0.9)),
            new RecognizedOpeningItem(2, "?", null)
        ]);
        var fallback = new CurrencyWarsNavigationResult(
            CurrencyWarsNavigationState.InvestmentEnvironmentFallbackSelected,
            "preparation_1_1",
            "投资环境识别降级，本轮强制重开")
        {
            EnemyOverview = new EnemyOverviewReadResult(
                Items("competitor", 3),
                Items("modifier", 4)),
            InvestmentEnvironments = partialEnvironments
        };
        var navigator = new FakeNavigator(fallback, CompleteNavigation());
        var recovery = new FakeRecovery(
            RejectedOpeningRecoveryResult.Recovered("recovered"));
        var preparation = new FakePreparationBoardController(
            new PreparationBoardResult(
                PreparationBoardStatus.Deployed,
                [],
                [],
                "unused"));
        var rewards = new FakeRewardStageController(
            new RewardStageAutomationResult(
                RewardStageAutomationStatus.InvestmentStrategySelected,
                "unused"));
        var coordinator = CreateCoordinator(
            navigator,
            recovery,
            preparation,
            rewards);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Equal(2, result.CompletedRounds);
        Assert.Single(recovery.Snapshots);
        Assert.Equal(0, preparation.Calls);
        Assert.Equal(0, rewards.Calls);
        Assert.Contains(
            "强制重开",
            recovery.Evaluations.Single().Reasons.Single());
    }

    [Fact]
    public async Task CancellationIsObservedBeforeNavigation()
    {
        var navigator = new FakeNavigator(CompleteNavigation());
        var coordinator = CreateCoordinator(
            navigator,
            new NotConfiguredRejectedOpeningRecovery());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.RunAsync(
                1,
                new OpeningFilterSet(),
                new OpeningRerollLoopOptions(),
                cancellation.Token));
        Assert.Empty(navigator.Options);
    }

    [Fact]
    public async Task MaximumRuntimeCancelsNavigationAndReturnsLimitState()
    {
        var coordinator = CreateCoordinator(
            new SlowNavigator(),
            new NotConfiguredRejectedOpeningRecovery());

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions
            {
                MaximumRuntime = TimeSpan.FromMilliseconds(50)
            },
            CancellationToken.None);

        Assert.Equal(
            OpeningRerollLoopState.MaximumRuntimeReached,
            result.FinalState);
        Assert.Equal(0, result.CompletedRounds);
    }

    [Fact]
    public async Task SelectedGameModeIsForwardedToEveryNavigationRound()
    {
        var navigator = new FakeNavigator(CompleteNavigation());
        var coordinator = CreateCoordinator(
            navigator,
            new NotConfiguredRejectedOpeningRecovery());

        await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions
            {
                GameMode = CurrencyWarsGameMode.Overclock
            },
            CancellationToken.None);

        Assert.Equal(
            CurrencyWarsGameMode.Overclock,
            navigator.Options.Single().GameMode);
    }

    [Fact]
    public async Task PreferredInvestmentsAreForwardedToNavigationForRefresh()
    {
        var navigator = new FakeNavigator(CompleteNavigation());
        var coordinator = CreateCoordinator(
            navigator,
            new NotConfiguredRejectedOpeningRecovery());

        await coordinator.RunAsync(
            1,
            new OpeningFilterSet
            {
                InvestmentEnvironments =
                [
                    new OpeningItemFilter(
                        "environment_a",
                        "environment_a",
                        OpeningFilterState.Require),
                    new OpeningItemFilter(
                        "environment_b",
                        "environment_b",
                        OpeningFilterState.Require)
                ]
            },
            new OpeningRerollLoopOptions(),
            CancellationToken.None);

        Assert.True(
            navigator.Options.Single().PreferredInvestmentEnvironmentIds
                .SetEquals(["environment_a", "environment_b"]));
    }

    [Fact]
    public async Task MatchedAutomaticRunNavigatesToPreparationAndDeploysTeam()
    {
        var navigator = new FakeNavigator(
            CompleteNavigation(),
            ReachedPreparation("environment_2"));
        var preparation = new FakePreparationBoardController(
            new PreparationBoardResult(
                PreparationBoardStatus.Deployed,
                [],
                [],
                "布阵完成，停在 1-1。"));
        var coordinator = CreateCoordinator(
            navigator,
            new NotConfiguredRejectedOpeningRecovery(),
            preparation);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions
            {
                DeployMatchedOpening = true,
                InitialRewardCharacterNames =
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            },
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Contains("布阵完成", result.Message);
        Assert.Equal(2, navigator.Options.Count);
        Assert.True(navigator.Options[0].StopAfterOpeningRecognition);
        Assert.False(navigator.Options[1].StopAfterOpeningRecognition);
        Assert.True(navigator.Options[1].StopAtPreparation);
        Assert.Equal(1, preparation.Calls);
        Assert.Equal(
            PreparationBenchSaleMode.None,
            preparation.LastOptions!.BenchSaleMode);
        Assert.Contains(
            "飞霄",
            preparation.LastOptions.EligibleCharacterNames!);
        Assert.Contains(
            "银枝",
            preparation.LastOptions.EligibleCharacterNames!);
        Assert.Contains(
            "阿格莱雅",
            preparation.LastOptions.EligibleCharacterNames!);
    }

    [Fact]
    public async Task UnusableInitialBenchTurnsMatchedOpeningIntoReroll()
    {
        var navigator = new FakeNavigator(
            CompleteNavigation(),
            ReachedPreparation());
        var preparation = new FakePreparationBoardController(
            new PreparationBoardResult(
                PreparationBoardStatus.NoEligibleCharacter,
                [],
                [],
                "备战席没有可用角色。"));
        var recovery = new FakeRecovery(
            RejectedOpeningRecoveryResult.Recovered("unused"));
        var coordinator = CreateCoordinator(
            navigator,
            recovery,
            preparation);
        var milestones = new List<OpeningRerollMilestone>();
        coordinator.ProgressChanged += (_, progress) =>
            milestones.Add(progress.Milestone);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions
            {
                DeployMatchedOpening = true,
                MaximumRounds = 1
            },
            CancellationToken.None);

        Assert.Equal(
            OpeningRerollLoopState.MaximumRoundsReached,
            result.FinalState);
        Assert.False(result.Evaluation!.Matched);
        Assert.Contains(
            "备战席没有可用角色。",
            result.Evaluation.Reasons);
        Assert.Empty(recovery.Snapshots);
        Assert.Contains(
            OpeningRerollMilestone.AcceptedOpeningReadyForRecording,
            milestones);
        Assert.Contains(
            OpeningRerollMilestone.AcceptedOpeningRejected,
            milestones);
    }

    [Fact]
    public async Task CompletedFormationCanContinueThroughRewardStages()
    {
        var navigator = new FakeNavigator(
            CompleteNavigation(),
            ReachedPreparation());
        var preparation = new FakePreparationBoardController(
            new PreparationBoardResult(
                PreparationBoardStatus.Deployed,
                [],
                [],
                "布阵完成。"));
        var rewards = new FakeRewardStageController(
            new RewardStageAutomationResult(
                RewardStageAutomationStatus.InvestmentStrategySelected,
                "奖励关完成并选中投资策略。"));
        var coordinator = CreateCoordinator(
            navigator,
            new NotConfiguredRejectedOpeningRecovery(),
            preparation,
            rewards);
        var milestones = new List<OpeningRerollMilestone>();
        coordinator.ProgressChanged += (_, progress) =>
            milestones.Add(progress.Milestone);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions
            {
                DeployMatchedOpening = true,
                CompleteRewardStages = true
            },
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Contains("奖励关完成", result.Message);
        Assert.Equal(1, rewards.Calls);
        Assert.Contains(
            OpeningRerollMilestone.AcceptedOpeningReadyForRecording,
            milestones);
        Assert.DoesNotContain(
            OpeningRerollMilestone.AcceptedOpeningRejected,
            milestones);
        Assert.True(new RewardStageAutomationResult(
            RewardStageAutomationStatus.PreparationIncomplete,
            "阵容不足，重刷。").ShouldReroll);
        Assert.True(new RewardStageAutomationResult(
            RewardStageAutomationStatus.RecoveryRequested,
            "页面或动作未闭环，退出重刷。").ShouldReroll);
        Assert.False(new RewardStageAutomationResult(
            RewardStageAutomationStatus.InputFailed,
            "真实输入失败。").ShouldReroll);
        Assert.True(new PreparationBoardResult(
            PreparationBoardStatus.InputFailed,
            [],
            [],
            "布阵未闭环，退出重刷。").ShouldReroll);
    }

    [Fact]
    public async Task RewardStagesWithoutStrategyConditionHandOffToRecorder()
    {
        var navigator = new FakeNavigator(
            CompleteNavigation(),
            ReachedPreparation());
        var preparation = new FakePreparationBoardController(
            new PreparationBoardResult(
                PreparationBoardStatus.Deployed,
                [],
                [],
                "formation completed"));
        var rewards = new FakeRewardStageController(
            new RewardStageAutomationResult(
                RewardStageAutomationStatus
                    .RewardStagesCompletedAwaitingManualStrategy,
                "reward stages completed; manual strategy selection"));
        var coordinator = CreateCoordinator(
            navigator,
            new NotConfiguredRejectedOpeningRecovery(),
            preparation,
            rewards);
        var milestones = new List<OpeningRerollMilestone>();
        coordinator.ProgressChanged += (_, progress) =>
            milestones.Add(progress.Milestone);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet
            {
                Competitors =
                [
                    new OpeningItemFilter(
                        "competitor_1",
                        "competitor_1",
                        OpeningFilterState.Require)
                ]
            },
            new OpeningRerollLoopOptions
            {
                DeployMatchedOpening = true,
                CompleteRewardStages = true
            },
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Equal(1, rewards.Calls);
        Assert.Empty(rewards.LastOptions!.PreferredInvestmentStrategyIds);
        Assert.Contains(
            OpeningRerollMilestone.AcceptedOpeningReadyForRecording,
            milestones);
        Assert.DoesNotContain(
            OpeningRerollMilestone.AcceptedOpeningRejected,
            milestones);
    }

    [Fact]
    public async Task MultipleMatchedProfilesLockOneCompleteProfileAtRandom()
    {
        var navigator = new FakeNavigator(
            CompleteNavigation(),
            ReachedPreparation("environment_2"));
        var preparation = new FakePreparationBoardController(
            new PreparationBoardResult(
                PreparationBoardStatus.Deployed,
                [],
                [],
                "布阵完成。"));
        var rewards = new FakeRewardStageController(
            new RewardStageAutomationResult(
                RewardStageAutomationStatus.InvestmentStrategySelected,
                "策略完成。"));
        var coordinator = CreateCoordinator(
            navigator,
            new NotConfiguredRejectedOpeningRecovery(),
            preparation,
            rewards,
            _ => 1);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet
            {
                Profiles =
                [
                    new OpeningFilterProfile
                    {
                        Id = "profile_a",
                        DisplayName = "方案A",
                        AcceptedInvestmentEnvironmentIds = ["environment_1"],
                        PreferredInvestmentStrategyIds = ["strategy_a"]
                    },
                    new OpeningFilterProfile
                    {
                        Id = "profile_b",
                        DisplayName = "方案B",
                        AcceptedInvestmentEnvironmentIds = ["environment_2"],
                        PreferredInvestmentStrategyIds = ["strategy_b"]
                    }
                ]
            },
            new OpeningRerollLoopOptions
            {
                DeployMatchedOpening = true,
                CompleteRewardStages = true,
                InitialRewardCharacterNames = new HashSet<string>(
                    ["阿格莱雅", "乱破"],
                    StringComparer.OrdinalIgnoreCase),
                BenchSaleMode = PreparationBenchSaleMode.InterestThreshold,
                BenchSaleInterestThreshold = 20,
                RewardStage = new RewardStageAutomationOptions
                {
                    EnableEarlyStrongFormationPurchase = true,
                    EnableGalaxyScholarRewardStrategy = true,
                    RetainedCharacterNames = new HashSet<string>(
                        ["用户保留角色"],
                        StringComparer.OrdinalIgnoreCase)
                }
            },
            CancellationToken.None);

        Assert.Equal(OpeningRerollLoopState.Matched, result.FinalState);
        Assert.Equal(
            ["environment_1", "environment_2"],
            navigator.Options[0].PreferredInvestmentEnvironmentIds.Order());
        Assert.Equal(
            ["environment_2"],
            navigator.Options[1].PreferredInvestmentEnvironmentIds);
        Assert.Equal(
            ["strategy_b"],
            rewards.LastOptions!.PreferredInvestmentStrategyIds);
        Assert.Equal(
            "environment_2",
            rewards.LastOptions.SelectedInvestmentEnvironmentId);
        Assert.Equal(
            PreparationBenchSaleMode.InterestThreshold,
            preparation.LastOptions!.BenchSaleMode);
        Assert.Equal(20, preparation.LastOptions.InterestThreshold);
        Assert.Contains(
            "用户保留角色",
            preparation.LastOptions.RetainedCharacterNames);
        Assert.True(
            preparation.LastOptions.EnableEarlyStrongFormationRetention);
        Assert.True(
            preparation.LastOptions.EnableGalaxyScholarPairFormation);
        Assert.True(
            preparation.LastOptions.DeferBenchSaleUntilShopCompletion);
        Assert.Equal(
            PreparationBenchSaleMode.InterestThreshold,
            rewards.LastOptions.PreparationCompletionOptions.BenchSaleMode);
        Assert.False(
            rewards.LastOptions.PreparationCompletionOptions
                .DeferBenchSaleUntilShopCompletion);
        Assert.Equal(2, rewards.LastOptions.FormationCharacterNames.Count);
        Assert.True(rewards.LastOptions.EnableGalaxyScholarRewardStrategy);
        Assert.Contains("乱破", rewards.LastOptions.FormationCharacterNames);
        Assert.Contains("阿格莱雅", rewards.LastOptions.FormationCharacterNames);
        Assert.Contains(
            "用户保留角色",
            rewards.LastOptions.PreparationCompletionOptions
                .RetainedCharacterNames);
    }

    [Fact]
    public async Task RewardTimeoutAlreadyRecoveredToHomeSkipsSecondRecovery()
    {
        var navigator = new FakeNavigator(
            CompleteNavigation(),
            ReachedPreparation("investment_environment_061"));
        var preparation = new FakePreparationBoardController(
            new PreparationBoardResult(
                PreparationBoardStatus.Deployed,
                [],
                [],
                "布阵完成。"));
        var rewards = new FakeRewardStageController(
            new RewardStageAutomationResult(
                RewardStageAutomationStatus.RecoveredToHome,
                "超时后已回主页。"));
        var recovery = new FakeRecovery(
            RejectedOpeningRecoveryResult.Recovered("不应调用"));
        var coordinator = CreateCoordinator(
            navigator,
            recovery,
            preparation,
            rewards);

        var result = await coordinator.RunAsync(
            1,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions
            {
                DeployMatchedOpening = true,
                CompleteRewardStages = true,
                MaximumRounds = 1
            },
            CancellationToken.None);

        Assert.Equal(
            OpeningRerollLoopState.MaximumRoundsReached,
            result.FinalState);
        Assert.Empty(recovery.Snapshots);
    }

    private static OpeningRerollLoopCoordinator CreateCoordinator(
        ICurrencyWarsOpeningNavigator navigator,
        IRejectedOpeningRecovery recovery,
        IPreparationBoardController? preparation = null,
        IRewardStageAutomationController? rewards = null,
        Func<int, int>? randomIndexSelector = null,
        IPassiveRecoveryMonitor? passiveRecoveryMonitor = null) =>
        new(
            navigator,
            new OpeningFilterEvaluator(),
            recovery,
            new FakeTaskEventSink(),
            preparationBoardController: preparation,
            rewardStageController: rewards,
            randomIndexSelector: randomIndexSelector,
            passiveRecoveryMonitor: passiveRecoveryMonitor);

    private static CurrencyWarsNavigationResult CompleteNavigation(
        string competitorPrefix = "competitor")
    {
        var competitors = new EnemyOverviewReadResult(
            Items(competitorPrefix, 3),
            Items("modifier", 4));
        var environments = new InvestmentEnvironmentReadResult(
            Items("environment", 3));
        return new CurrencyWarsNavigationResult(
            CurrencyWarsNavigationState.OpeningRecognized,
            "investment_environment",
            "complete")
        {
            EnemyOverview = competitors,
            InvestmentEnvironments = environments
        };
    }

    private static CurrencyWarsNavigationResult ReachedPreparation(
        string? selectedInvestmentEnvironmentId = null) =>
        new CurrencyWarsNavigationResult(
            CurrencyWarsNavigationState.ReachedPreparation,
            "preparation_1_1",
            "reached preparation")
        {
            SelectedInvestmentEnvironmentId =
                selectedInvestmentEnvironmentId
        };

    private static IReadOnlyList<RecognizedOpeningItem> Items(
        string prefix,
        int count) =>
        Enumerable.Range(1, count)
            .Select(index => new RecognizedOpeningItem(
                index - 1,
                $"{prefix}_{index}",
                new ObservedItem(
                    $"{prefix}_{index}",
                    $"{prefix}_{index}",
                    1)))
            .ToArray();

    private sealed class FakeNavigator(
        params CurrencyWarsNavigationResult[] results)
        : ICurrencyWarsOpeningNavigator
    {
        private readonly Queue<CurrencyWarsNavigationResult> results = new(results);

        public List<CurrencyWarsNavigationOptions> Options { get; } = [];

        public Task<CurrencyWarsNavigationResult> RunAsync(
            nint windowHandle,
            CurrencyWarsNavigationOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Options.Add(options);
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class FakeRecovery(RejectedOpeningRecoveryResult result)
        : IRejectedOpeningRecovery
    {
        public List<OpeningSnapshot> Snapshots { get; } = [];

        public List<OpeningFilterEvaluation> Evaluations { get; } = [];

        public Task<RejectedOpeningRecoveryResult> RecoverAsync(
            nint windowHandle,
            OpeningSnapshot rejectedOpening,
            OpeningFilterEvaluation evaluation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshots.Add(rejectedOpening);
            Evaluations.Add(evaluation);
            return Task.FromResult(result);
        }
    }

    private sealed class SlowNavigator : ICurrencyWarsOpeningNavigator
    {
        public async Task<CurrencyWarsNavigationResult> RunAsync(
            nint windowHandle,
            CurrencyWarsNavigationOptions options,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FakePreparationBoardController(
        PreparationBoardResult result) : IPreparationBoardController
    {
        public int Calls { get; private set; }
        public PreparationBoardOptions? LastOptions { get; private set; }

        public Task<PreparationBoardResult> PrepareAsync(
            nint windowHandle,
            PreparationBoardOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastOptions = options;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRewardStageController(
        RewardStageAutomationResult result)
        : IRewardStageAutomationController
    {
        public int Calls { get; private set; }
        public RewardStageAutomationOptions? LastOptions { get; private set; }

        public Task<RewardStageAutomationResult> RunAsync(
            nint windowHandle,
            RewardStageAutomationOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastOptions = options;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeTaskEventSink : ITaskEventSink
    {
        public void Publish(TaskEvent taskEvent)
        {
        }
    }

    private sealed class FakePassiveRecoveryMonitor(bool blockUntilCancelled = false)
        : IPassiveRecoveryMonitor
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> WaitForSafeEntryPageAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            if (blockUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return "currency_wars_home";
        }
    }
}
