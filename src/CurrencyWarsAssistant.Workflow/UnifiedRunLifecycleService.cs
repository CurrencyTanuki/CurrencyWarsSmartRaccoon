using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Workflow;

public sealed record UnifiedRunLifecycleUpdate(
    string? RunId,
    string Message,
    bool IsError = false,
    bool IsMilestone = false);

public interface IUnifiedRunLifecycleService
{
    event EventHandler<UnifiedRunLifecycleUpdate>? Updated;

    void BeginAutomaticReroll(
        nint gameWindowHandle,
        CancellationToken sessionCancellation);

    void ObserveOpeningProgress(OpeningRerollLoopProgress progress);

    Task<bool> ContinueMatchedRunAsync(CancellationToken cancellationToken);

    Task RunDirectRecordingAsync(
        nint gameWindowHandle,
        RunIdentityEvidence initialIdentity,
        CancellationToken cancellationToken);

    Task EndWithoutMatchAsync();
}

/// <summary>
/// Bridges the accepted phase-one opening into the existing phase-two recorder.
/// It deliberately owns no screenshot or recognition implementation: both the
/// automatic-reroll and direct-recording entries use the same live collector,
/// checkpoint store, and run model.
/// </summary>
public sealed class UnifiedRunLifecycleService : IUnifiedRunLifecycleService
{
    private readonly IPhase2LiveCollectionService collector;
    private readonly LocalRunStore store;
    private readonly object gate = new();
    private Task transitionTail = Task.CompletedTask;
    private CandidateRun? activeCandidate;
    private nint gameWindowHandle;
    private CancellationToken sessionCancellation;
    private bool sessionStarted;

    public UnifiedRunLifecycleService(
        IPhase2LiveCollectionService collector,
        LocalRunStore store)
    {
        this.collector = collector;
        this.store = store;
        collector.Updated += OnCollectorUpdated;
    }

    public event EventHandler<UnifiedRunLifecycleUpdate>? Updated;

    public void BeginAutomaticReroll(
        nint gameWindowHandle,
        CancellationToken sessionCancellation)
    {
        if (gameWindowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameWindowHandle));
        }

        lock (gate)
        {
            if (activeCandidate is not null || !transitionTail.IsCompleted)
            {
                throw new InvalidOperationException(
                    "A unified run lifecycle is already active.");
            }

            this.gameWindowHandle = gameWindowHandle;
            this.sessionCancellation = sessionCancellation;
            sessionStarted = true;
        }
    }

    public void ObserveOpeningProgress(OpeningRerollLoopProgress progress)
    {
        if (progress.Milestone == OpeningRerollMilestone.None)
        {
            return;
        }

        lock (gate)
        {
            if (!sessionStarted)
            {
                return;
            }

            var previous = transitionTail;
            transitionTail = progress.Milestone switch
            {
                OpeningRerollMilestone.AcceptedOpeningReadyForRecording =>
                    StartCandidateAfterAsync(
                        previous,
                        progress.Round,
                        progress.Snapshot),
                OpeningRerollMilestone.AcceptedOpeningRejected =>
                    AbandonCandidateAfterAsync(previous, progress.Message),
                _ => previous
            };
        }
    }

    public async Task<bool> ContinueMatchedRunAsync(
        CancellationToken cancellationToken)
    {
        var pending = GetTransitionTail();
        await pending.ConfigureAwait(false);

        CandidateRun? candidate;
        lock (gate)
        {
            candidate = activeCandidate;
            if (candidate is not null)
            {
                candidate.Promoted = true;
            }
        }

        if (candidate is null)
        {
            Publish(
                null,
                "开局已达标，但未获得统一记录候选；为避免伪造奖励关数据，未自动创建补录记录。",
                isError: true,
                isMilestone: true);
            return false;
        }

        Publish(
            candidate.RunId,
            "满意开局已保留；同一记录器将继续记录后续节点。",
            isMilestone: true);
        try
        {
            await candidate.CollectorTask.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            candidate.Cancellation.Cancel();
            await AwaitCollectorShutdownAsync(candidate).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeCandidate, candidate))
                {
                    activeCandidate = null;
                }

                sessionStarted = false;
            }

            candidate.Cancellation.Dispose();
        }

        return true;
    }

    public async Task RunDirectRecordingAsync(
        nint gameWindowHandle,
        RunIdentityEvidence initialIdentity,
        CancellationToken cancellationToken)
    {
        if (gameWindowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameWindowHandle));
        }

        ArgumentNullException.ThrowIfNull(initialIdentity);
        Publish(
            null,
            "已到达1-1；同一记录器将继续采集后续备战、战斗和节点数据。",
            isMilestone: true);
        await collector.RunAsync(
                gameWindowHandle,
                new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
                new LiveCollectionStartOptions(
                    EntryMode: RunEntryMode.DirectRecording,
                    InitialIdentityEvidence: initialIdentity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EndWithoutMatchAsync()
    {
        Task pending;
        lock (gate)
        {
            var previous = transitionTail;
            transitionTail = AbandonCandidateAfterAsync(
                previous,
                "自动刷取未形成满意开局；候选记录不进入正式节点历史。");
            pending = transitionTail;
            sessionStarted = false;
        }

        await pending.ConfigureAwait(false);
    }

    private async Task StartCandidateAfterAsync(
        Task previous,
        int round,
        OpeningSnapshot? snapshot)
    {
        await previous.ConfigureAwait(false);
        await StopActiveCandidateAsync(
                abandon: true,
                "新的达标候选已出现；较早候选按现有规则结束。")
            .ConfigureAwait(false);

        nint handle;
        CancellationToken cancellation;
        lock (gate)
        {
            if (!sessionStarted)
            {
                return;
            }

            handle = gameWindowHandle;
            cancellation = sessionCancellation;
        }

        var runId =
            $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-r{round}-{Guid.NewGuid():N}";
        var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var identity = snapshot is null
            ? null
            : new RunIdentityEvidence
            {
                InvestmentEnvironmentId =
                    snapshot.InvestmentEnvironmentIds.FirstOrDefault(),
                EnemyIds = snapshot.CompetitorIds,
                EnemyAffixIds = snapshot.EnemyModifierIds
            };
        var task = collector.RunAsync(
            handle,
            new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4"),
            new LiveCollectionStartOptions(
                runId,
                RunEntryMode.AutomaticReroll,
                InitialIdentityEvidence: identity),
            linkedCancellation.Token);
        var candidate = new CandidateRun(runId, linkedCancellation, task);
        lock (gate)
        {
            activeCandidate = candidate;
        }

        Publish(
            runId,
            $"第 {round} 轮开局达标；已用正式记录器开始采集两个奖励关及后续状态。",
            isMilestone: true);
    }

    private async Task AbandonCandidateAfterAsync(Task previous, string reason)
    {
        await previous.ConfigureAwait(false);
        await StopActiveCandidateAsync(abandon: true, reason)
            .ConfigureAwait(false);
    }

    private async Task StopActiveCandidateAsync(bool abandon, string reason)
    {
        CandidateRun? candidate;
        lock (gate)
        {
            candidate = activeCandidate;
            activeCandidate = null;
        }

        if (candidate is null)
        {
            return;
        }

        candidate.Cancellation.Cancel();
        await AwaitCollectorShutdownAsync(candidate).ConfigureAwait(false);
        if (abandon && !candidate.Promoted)
        {
            var summary = await store.LoadCheckpointAsync(
                    candidate.RunId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (summary is not null)
            {
                await store.SaveCheckpointAsync(
                        summary.Checkpoint with
                        {
                            LifecycleStatus = RunCheckpointLifecycleStatus.Abandoned,
                            LastSavedAtUtc = DateTimeOffset.UtcNow,
                            Uncertainty = summary.Checkpoint.Uncertainty
                                .Append(reason)
                                .Distinct(StringComparer.Ordinal)
                                .ToArray()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        candidate.Cancellation.Dispose();
        Publish(candidate.RunId, reason, isMilestone: true);
    }

    private static async Task AwaitCollectorShutdownAsync(CandidateRun candidate)
    {
        try
        {
            await candidate.CollectorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The collector persists a paused checkpoint during normal shutdown.
        }
    }

    private Task GetTransitionTail()
    {
        lock (gate)
        {
            return transitionTail;
        }
    }

    private void OnCollectorUpdated(object? sender, LiveCollectionUpdate update) =>
        Publish(
            update.RunId,
            update.Message,
            update.IsError,
            update.IsMilestone);

    private void Publish(
        string? runId,
        string message,
        bool isError = false,
        bool isMilestone = false) =>
        Updated?.Invoke(
            this,
            new UnifiedRunLifecycleUpdate(
                runId,
                message,
                isError,
                isMilestone));

    private sealed class CandidateRun(
        string runId,
        CancellationTokenSource cancellation,
        Task collectorTask)
    {
        public string RunId { get; } = runId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task CollectorTask { get; } = collectorTask;
        public bool Promoted { get; set; }
    }
}
