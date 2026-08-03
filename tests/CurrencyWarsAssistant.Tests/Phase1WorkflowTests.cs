using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Workflow;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase1WorkflowTests
{
    [Fact]
    public void ConfigurationSnapshotDeepCopiesMutableCollections()
    {
        var competitors = new List<OpeningItemFilter>
        {
            new("competitor_1", "A", OpeningFilterState.Require)
        };
        var autoBuy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Character A"
        };
        var sourceFilters = new OpeningFilterSet
        {
            Competitors = competitors,
            Combinations =
            [
                new OpeningCombinationFilter
                {
                    Id = "combo",
                    DisplayName = "combo",
                    State = OpeningFilterState.Reject,
                    CompetitorIds = ["competitor_1"]
                }
            ]
        };
        var sourceOptions = new OpeningRerollLoopOptions
        {
            RewardStage = new RewardStageAutomationOptions
            {
                AutoPurchaseCharacterNames = autoBuy
            }
        };

        var snapshot = Phase1RunConfiguration.Create(
            123,
            sourceFilters,
            sourceOptions);
        competitors.Clear();
        autoBuy.Clear();

        Assert.Single(snapshot.Filters.Competitors);
        Assert.Single(snapshot.Filters.Combinations[0].CompetitorIds);
        Assert.Contains(
            "Character A",
            snapshot.Options.RewardStage.AutoPurchaseCharacterNames);
    }

    [Fact]
    public async Task ServiceRejectsOverlappingRunsAndPublishesCompletion()
    {
        var runner = new BlockingRunner();
        var service = new Phase1AutomationService(runner);
        var configuration = Phase1RunConfiguration.Create(
            123,
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions());

        var first = service.RunAsync(configuration, CancellationToken.None);
        await runner.Started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunAsync(configuration, CancellationToken.None));

        runner.Complete();
        var result = await first;
        Assert.True(result.Succeeded);
        Assert.Equal(Phase1WorkflowState.Completed, service.Status.State);
    }

    [Fact]
    public void ServiceForwardsStructuredOpeningMilestones()
    {
        var runner = new BlockingRunner();
        var service = new Phase1AutomationService(runner);
        OpeningRerollLoopProgress? observed = null;
        service.OpeningProgressChanged += (_, progress) => observed = progress;

        runner.Publish(new OpeningRerollLoopProgress(
            OpeningRerollLoopState.Navigating,
            3,
            "accepted",
            OpeningRerollMilestone.AcceptedOpeningReadyForRecording));

        Assert.NotNull(observed);
        Assert.Equal(3, observed.Round);
        Assert.Equal(
            OpeningRerollMilestone.AcceptedOpeningReadyForRecording,
            observed.Milestone);
    }

    private sealed class BlockingRunner :
        IOpeningRerollRunner,
        IOpeningRerollProgressSource
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<OpeningRerollLoopProgress>? ProgressChanged;

        public async Task<OpeningRerollLoopResult> RunAsync(
            nint windowHandle,
            OpeningFilterSet filters,
            OpeningRerollLoopOptions options,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new OpeningRerollLoopResult(
                OpeningRerollLoopState.Matched,
                1,
                null,
                null,
                null,
                null,
                "matched");
        }

        public void Complete() => _release.TrySetResult();

        public void Publish(OpeningRerollLoopProgress progress) =>
            ProgressChanged?.Invoke(this, progress);
    }
}
