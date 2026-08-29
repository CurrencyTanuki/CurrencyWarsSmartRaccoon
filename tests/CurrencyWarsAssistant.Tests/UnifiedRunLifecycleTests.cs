using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Workflow;

namespace CurrencyWarsAssistant.Tests;

public sealed class UnifiedRunLifecycleTests
{
    [Fact]
    public async Task RejectedAcceptedOpeningIsHiddenFromFormalResumeList()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var collector = new CheckpointingCollector(store);
            var lifecycle = new UnifiedRunLifecycleService(collector, store);
            lifecycle.BeginAutomaticReroll(123, CancellationToken.None);

            lifecycle.ObserveOpeningProgress(Accepted(round: 2));
            var runId = await collector.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            lifecycle.ObserveOpeningProgress(Rejected(round: 2));
            await lifecycle.EndWithoutMatchAsync();

            var checkpoint = await store.LoadCheckpointAsync(
                runId,
                CancellationToken.None);
            Assert.NotNull(checkpoint);
            Assert.Equal(
                RunCheckpointLifecycleStatus.Abandoned,
                checkpoint.Checkpoint.LifecycleStatus);
            Assert.Empty(await store.ListIncompleteRunsAsync(
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MatchedOpeningKeepsSameRunAndPausesForLaterResume()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var collector = new CheckpointingCollector(store);
            var lifecycle = new UnifiedRunLifecycleService(collector, store);
            lifecycle.BeginAutomaticReroll(123, CancellationToken.None);
            lifecycle.ObserveOpeningProgress(Accepted(round: 4));
            var runId = await collector.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            using var cancellation = new CancellationTokenSource();
            var recording = lifecycle.ContinueMatchedRunAsync(
                cancellation.Token);
            cancellation.Cancel();

            Assert.True(await recording);
            var checkpoint = await store.LoadCheckpointAsync(
                runId,
                CancellationToken.None);
            Assert.NotNull(checkpoint);
            Assert.Equal(
                RunCheckpointLifecycleStatus.Paused,
                checkpoint.Checkpoint.LifecycleStatus);
            Assert.Equal(
                RunEntryMode.AutomaticReroll,
                checkpoint.Checkpoint.EntryMode);
            Assert.Equal(
                runId,
                Assert.Single(await store.ListIncompleteRunsAsync(
                    CancellationToken.None)).Checkpoint.RunId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectEntryContinuesRecordingAndCarriesOpeningIdentity()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new LocalRunStore(root);
            var collector = new CheckpointingCollector(store);
            var lifecycle = new UnifiedRunLifecycleService(collector, store);
            var identity = new RunIdentityEvidence
            {
                InvestmentEnvironmentId = "environment-a",
                EnemyAffixIds = ["affix-a", "affix-b"],
                EnemyIds = ["enemy-a"]
            };

            using var cancellation = new CancellationTokenSource();
            var recording = lifecycle.RunDirectRecordingAsync(
                123,
                identity,
                cancellation.Token);
            _ = await collector.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await recording;

            Assert.NotNull(collector.LastOptions);
            Assert.Equal(
                RunEntryMode.DirectRecording,
                collector.LastOptions.EntryMode);
            Assert.Same(identity, collector.LastOptions.InitialIdentityEvidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OpeningRerollLoopProgress Accepted(int round) => new(
        OpeningRerollLoopState.Navigating,
        round,
        "accepted",
        OpeningRerollMilestone.AcceptedOpeningReadyForRecording);

    private static OpeningRerollLoopProgress Rejected(int round) => new(
        OpeningRerollLoopState.Evaluating,
        round,
        "rejected",
        OpeningRerollMilestone.AcceptedOpeningRejected);

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CheckpointingCollector(LocalRunStore store) :
        IPhase2LiveCollectionService
    {
        public TaskCompletionSource<string> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public LiveCollectionStartOptions? LastOptions { get; private set; }

        public event EventHandler<LiveCollectionUpdate>? Updated;

        public Task RunAsync(
            nint gameWindowHandle,
            AdvisorSelection selection,
            CancellationToken cancellationToken) => RunAsync(
                gameWindowHandle,
                selection,
                new LiveCollectionStartOptions(),
                cancellationToken);

        public async Task RunAsync(
            nint gameWindowHandle,
            AdvisorSelection selection,
            LiveCollectionStartOptions options,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            var runId = options.RunId ?? $"run-{Guid.NewGuid():N}";
            var checkpoint = RunCheckpointFactory.CreateInitial(
                runId,
                options.EntryMode,
                DateTimeOffset.UtcNow) with
            {
                // 满意开局后应已确认 1-1 节点并有观测记录（贴近真实断点）。
                LastConfirmedNodeId = "1-1",
                SavedObservationCount = 1
            };
            await store.SaveCheckpointAsync(checkpoint, CancellationToken.None);
            Started.TrySetResult(runId);
            Updated?.Invoke(
                this,
                new LiveCollectionUpdate(runId, 0, null, "started"));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                // Match the production collector's bounded shutdown behavior.
            }

            await store.SaveCheckpointAsync(
                checkpoint with
                {
                    LifecycleStatus = RunCheckpointLifecycleStatus.Paused,
                    LastSavedAtUtc = DateTimeOffset.UtcNow
                },
                CancellationToken.None);
        }
    }
}
