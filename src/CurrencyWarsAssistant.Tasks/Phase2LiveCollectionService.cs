using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed record LiveCollectionUpdate(
    string RunId,
    int SavedObservationCount,
    ScreenshotAnalysisResult? Analysis,
    string Message,
    bool IsError = false,
    bool IsMilestone = false);

public sealed record LiveCollectionStartOptions(
    string? RunId = null,
    RunEntryMode EntryMode = RunEntryMode.DirectRecording,
    IReadOnlyList<string>? MissingNodeIds = null,
    RunIdentityEvidence? InitialIdentityEvidence = null,
    bool DeleteScreenshotsOnCompletion = false)
{
    public IReadOnlyList<string> EffectiveMissingNodeIds => MissingNodeIds ?? [];
}

public interface IPhase2LiveCollectionService
{
    event EventHandler<LiveCollectionUpdate>? Updated;

    Task RunAsync(
        nint gameWindowHandle,
        AdvisorSelection selection,
        CancellationToken cancellationToken);

    Task RunAsync(
        nint gameWindowHandle,
        AdvisorSelection selection,
        LiveCollectionStartOptions options,
        CancellationToken cancellationToken) =>
        RunAsync(gameWindowHandle, selection, cancellationToken);
}

public sealed class Phase2LiveCollectionService(
    IGameWindowService windowService,
    IGameCapture capture,
    ISituationScreenshotAnalyzer analyzer,
    LocalRunStore store,
    IPhase2FastPageClassifier? pageClassifier = null,
    Phase2OfflineOcrSet? phase2Ocr = null,
    Phase2RecognitionWarmUpService? recognitionWarmUp = null,
    IHistoricalDashboardProjection? historicalDashboard = null,
    IChallengeSummaryReportGenerator? summaryReportGenerator = null) :
    IPhase2LiveCollectionService
{
    private static readonly TimeSpan ObservationCheckpointInterval =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunCheckpointInterval =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DegradedCheckpointInterval =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureRecoveryNoticeInterval =
        TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumFailureRecoveryDuration =
        TimeSpan.FromMinutes(2);
    private const int FailureCheckpointThreshold = 5;
    // 对局结束页需要连续帧确认才 finalize（用户要求：只有可靠识别到
    // "挑战结束/挑战失败"两个灰色页面才判定对局结束，防误判提前截断对局）。
    private const int CompletionConfirmationFrames = 2;
    private string? _completionCandidatePageId;
    private int _completionCandidateFrames;

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
        ArgumentNullException.ThrowIfNull(options);
        var runId = string.IsNullOrWhiteSpace(options.RunId)
            ? $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmss}"
            : options.RunId;
        var loadedCheckpoint = options.RunId is null
            ? null
            : await store.LoadCheckpointAsync(runId, cancellationToken)
                .ConfigureAwait(false);
        var isResume = loadedCheckpoint is not null;
        var checkpoint = loadedCheckpoint?.Checkpoint ??
                         RunCheckpointFactory.CreateInitial(
                             runId,
                             options.EntryMode,
                             DateTimeOffset.UtcNow);
        checkpoint = checkpoint with
        {
            LifecycleStatus = RunCheckpointLifecycleStatus.Active,
            EntryMode = isResume ? RunEntryMode.Resumed : options.EntryMode,
            ResumeCount = isResume
                ? checkpoint.ResumeCount + 1
                : checkpoint.ResumeCount,
            LastSavedAtUtc = DateTimeOffset.UtcNow,
            MissingNodeIds = checkpoint.MissingNodeIds
                .Concat(options.EffectiveMissingNodeIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IdentityEvidence = MergeInitialIdentity(
                checkpoint.IdentityEvidence,
                options.InitialIdentityEvidence)
        };
        analyzer.SeedRunIdentity(runId, checkpoint.IdentityEvidence);
        _ = await TrySaveCheckpointAsync(
                checkpoint,
                savedCount: 0,
                analysis: null,
                cancellationToken)
            .ConfigureAwait(false);
        var lastRunCheckpointAt = checkpoint.LastSavedAtUtc;
        string? previousFingerprint = null;
        var lastSavedAt = DateTimeOffset.MinValue;
        var lastDegradedSavedAt = DateTimeOffset.MinValue;
        bool? previousPageRecognized = null;
        var savedCount = 0;
        var consecutiveFailures = 0;
        DateTimeOffset? firstFailureAt = null;
        var lastFailureNoticeAt = DateTimeOffset.MinValue;
        var tracker = new Phase2OperationalStateTracker();
        var postCompletionBoundary = new Phase2PostCompletionBoundaryDetector();
        var currentRunCompleted = false;
        _completionCandidatePageId = null;
        _completionCandidateFrames = 0;
        // 续玩时把磁盘上已保存的历史分析预加载进投影，
        // 节点历史图表/表格继续展示续玩前打过的节点（不显示为空）。
        if (isResume)
        {
            foreach (var historical in await store
                         .LoadAnalysesAsync(runId, cancellationToken)
                         .ConfigureAwait(false))
            {
                historicalDashboard?.Observe(runId, historical);
            }
        }

        CaptureFrame? pendingPreparationFrame = null;
        string? pendingPreparationScreenshotName = null;
        ScreenshotAnalysisResult? pendingPreparationAnalysis = null;
        var pipeline = new Phase2RealtimeRecognitionPipeline(
            windowService,
            capture,
            analyzer,
            pageClassifier);
        Publish(
            runId,
            savedCount,
            null,
            "只读局势收集已开始。不会发送鼠标或键盘输入。",
            isMilestone: true);

        if (recognitionWarmUp is not null || phase2Ocr is not null)
        {
            try
            {
                if (recognitionWarmUp is not null)
                {
                    await recognitionWarmUp.WarmUpAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await phase2Ocr!.WarmUpAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Publish(
                    runId,
                    savedCount,
                    null,
                    "PP-OCR 预热失败；收集器将继续使用现有有界降级识别器：" +
                    exception.Message,
                    isError: true);
            }
        }

        try
        {
            await foreach (var pipelineUpdate in pipeline.RunAsync(
                               gameWindowHandle,
                               selection,
                               () => runId,
                               cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (pipelineUpdate.Error is not null)
                    {
                        if (pipelineUpdate.IsCritical &&
                            pipelineUpdate.Frame is not null &&
                            !string.IsNullOrWhiteSpace(pipelineUpdate.ScreenshotName))
                        {
                            await SaveCriticalPipelineFailureAsync(
                                    pipelineUpdate.Frame,
                                    pipelineUpdate.ScreenshotName,
                                    runId,
                                    pipelineUpdate.Error,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        throw new InvalidOperationException(pipelineUpdate.Error);
                    }

                    if (firstFailureAt is { } failureStarted)
                    {
                        Publish(
                            runId,
                            savedCount,
                            pipelineUpdate.Analysis,
                            $"截图或识别在中断 " +
                            $"{(DateTimeOffset.UtcNow - failureStarted).TotalSeconds:F1} 秒后已自动恢复。",
                            isMilestone: true);
                        firstFailureAt = null;
                        lastFailureNoticeAt = DateTimeOffset.MinValue;
                    }

                    if (pipelineUpdate.IsHeartbeat)
                    {
                        if (pipelineUpdate.IsRevalidated &&
                            pipelineUpdate.Analysis?.OperationalState is not null &&
                            pipelineUpdate.Frame is not null)
                        {
                            var normalizedHeartbeat = WithRunId(
                                pipelineUpdate.Analysis,
                                runId);
                            if (Phase2TransitionFramePolicy.ShouldDiscard(
                                    normalizedHeartbeat))
                            {
                                // Transition/animation frames are scheduling
                                // evidence only. They must not reach the state
                                // tracker, checkpoint or history dashboard.
                                consecutiveFailures = 0;
                                continue;
                            }
                            var heartbeatTracking = tracker.Observe(
                                normalizedHeartbeat.OperationalState!,
                                normalizedHeartbeat.Snapshot.Health);
                            var heartbeatAnalysis = normalizedHeartbeat with
                            {
                                OperationalState = heartbeatTracking.Current
                            };
                            if (heartbeatTracking.FinalizedBattle is not null)
                            {
                                heartbeatAnalysis = heartbeatAnalysis with
                                {
                                    OperationalState = heartbeatTracking.Current with
                                    {
                                        FinalBattle = heartbeatTracking.FinalizedBattle.IsComplete
                                            ? Observation<FinalNodeBattleState>.Known(
                                                heartbeatTracking.FinalizedBattle,
                                                0.85,
                                                [heartbeatTracking.FinalizedBattle.Evidence],
                                                heartbeatTracking.FinalizedBattle.CapturedAt)
                                            : new Observation<FinalNodeBattleState>
                                            {
                                                Status = ObservationStatus.Unknown,
                                                Value = heartbeatTracking.FinalizedBattle,
                                                Confidence = 0,
                                                Evidence = [heartbeatTracking.FinalizedBattle.Evidence],
                                                Uncertainty = heartbeatTracking.FinalizedBattle.FinalUncertainty.Count > 0
                                                    ? heartbeatTracking.FinalizedBattle.FinalUncertainty
                                                    : ["Final battle record is incomplete and is retained for review only."],
                                                ObservedAt = heartbeatTracking.FinalizedBattle.CapturedAt
                                            }
                                    }
                                };
                                var heartbeatName =
                                    $"{pipelineUpdate.Frame.CapturedAt:yyyyMMdd-HHmmssfff}-heartbeat.png";
                                await SaveObservationAsync(
                                        pipelineUpdate.Frame,
                                        heartbeatName,
                                        heartbeatAnalysis,
                                        heartbeatTracking.FinalizedBattle,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                savedCount++;
                            }

                            historicalDashboard?.Observe(runId, heartbeatAnalysis);
                            var heartbeatNow = DateTimeOffset.UtcNow;
                            if (heartbeatTracking.FinalizedBattle is not null ||
                                heartbeatNow - lastRunCheckpointAt >=
                                RunCheckpointInterval)
                            {
                                checkpoint = RunCheckpointFactory.FromAnalysis(
                                    checkpoint,
                                    heartbeatAnalysis,
                                    savedCount,
                                    RunCheckpointLifecycleStatus.Active,
                                    heartbeatNow);
                                _ = await TrySaveCheckpointAsync(
                                        checkpoint,
                                        savedCount,
                                        heartbeatAnalysis,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                lastRunCheckpointAt = heartbeatNow;
                            }
                            Publish(
                                runId,
                                savedCount,
                                heartbeatAnalysis,
                                heartbeatTracking.FinalizedBattle is not null
                                    ? heartbeatTracking.Message
                                    : "Current page was confirmed by a fresh fast-path frame; cached field evidence remains unchanged.",
                                isMilestone:
                                    heartbeatTracking.FinalizedBattle is not null ||
                                    heartbeatTracking.PageChanged);
                            if (!string.IsNullOrWhiteSpace(heartbeatTracking.Diagnostic))
                            {
                                Publish(
                                    runId,
                                    savedCount,
                                    heartbeatAnalysis,
                                    heartbeatTracking.Diagnostic);
                            }

                            consecutiveFailures = 0;
                            continue;
                        }

                        Publish(
                            runId,
                            savedCount,
                            pipelineUpdate.Analysis,
                            pipelineUpdate.IsRevalidated
                                ? "当前画面与最近完整识别证据一致；状态已快速复核。"
                                : $"画面已变化，最近完整状态距今 " +
                                  $"{pipelineUpdate.AnalysisAge.TotalSeconds:F1} 秒；" +
                                  "变化字段暂不作为当前可靠值。");
                        consecutiveFailures = 0;
                        continue;
                    }

                    var frame = pipelineUpdate.Frame ??
                        throw new InvalidDataException("识别结果缺少来源截图。");
                    var screenshotName = pipelineUpdate.ScreenshotName ??
                        throw new InvalidDataException("识别结果缺少截图名称。");
                    var analysis = pipelineUpdate.Analysis ??
                        throw new InvalidDataException("识别管线未返回分析结果。");
                    analysis = WithRunId(analysis, runId);
                    if (Phase2TransitionFramePolicy.ShouldDiscard(analysis))
                    {
                        // The previous reliable state remains authoritative;
                        // the next stable frame will be analyzed normally.
                        consecutiveFailures = 0;
                        continue;
                    }
                var completionKind = Phase2RunCompletionDetector.Classify(
                        analysis,
                        tracker.ActiveBattleNode ?? tracker.LastFinalizedNode);
                if (currentRunCompleted && completionKind is
                    Phase2RunCompletionPageKind.FinalFailure or
                    Phase2RunCompletionPageKind.FinalSuccess)
                {
                    // The finalized page can remain visible for many frames.
                    // Keep observing the capture loop, but never archive it as
                    // another run or append it to the completed run.
                    continue;
                }
                if (completionKind is
                        Phase2RunCompletionPageKind.FinalFailure or
                        Phase2RunCompletionPageKind.FinalSuccess)
                {
                    analysis = EnsureTerminalOperationalState(analysis);
                    var terminalState = analysis.OperationalState!;
                    // 最终“挑战失败/下一步”页或 3-7 后整局评级页才结束对局。
                    // “挑战结束/前往结算”是失败流程过渡；金色节点成功动画
                    // 与“继续挑战”详情页都必须继续采集。
                    var candidatePageId = analysis.Snapshot.PageId.Status ==
                                           ObservationStatus.Known
                        ? analysis.Snapshot.PageId.Value
                        : terminalState.PageId;
                    if (!string.Equals(
                            _completionCandidatePageId,
                            candidatePageId,
                            StringComparison.Ordinal))
                    {
                        _completionCandidatePageId = candidatePageId;
                        _completionCandidateFrames = 0;
                    }

                    _completionCandidateFrames++;
                    if (_completionCandidateFrames < CompletionConfirmationFrames)
                    {
                        Publish(
                            runId,
                            savedCount,
                            analysis,
                            $"检测到对局结束页 {candidatePageId}，" +
                            $"等待第 {_completionCandidateFrames}/" +
                            $"{CompletionConfirmationFrames} 帧确认。");
                    }
                    else
                    {
                        var failedRun = completionKind ==
                            Phase2RunCompletionPageKind.FinalFailure;
                    // 玩家可以在仍有正数生命值时主动结算。最终失败页确认
                    // 失败，但不能把它一律改写成“生命耗尽”。
                    var finalBattle = tracker.CompletePendingBattle(
                        analysis.Snapshot.Health);
                    var completionPageId = analysis.Snapshot.PageId.Status ==
                                           ObservationStatus.Known
                        ? analysis.Snapshot.PageId.Value!
                        : failedRun
                            ? "challenge_failed"
                            : "challenge_success";
                    // 完成节点优先用备战/分析快照的节点（页面停留久、识别更稳），
                    // 战斗页节点号 OCR 易混淆（1-2 读成 2-8），不再优先采信。
                    var completionNodeId = ResolveKnownNode(analysis) ??
                                           finalBattle?.NodeId ??
                                           tracker.LastFinalizedNode ??
                                           "unknown";
                    var completionState = terminalState with
                    {
                        SettlementDamage =
                            Observation<IReadOnlyList<CharacterDamageState>>.Unknown(
                                "whole-run completion page has no node settlement rows"),
                        SettlementScreenDamageCandidate = Observation<long>.Unknown(
                            "whole-run completion page is not a node damage summary"),
                        SettlementGoldReward = Observation<int>.Unknown(
                            "whole-run completion page is not a node reward summary"),
                        FinalBattle = finalBattle is { } completedBattle
                            ? completedBattle.IsComplete
                                ? Observation<FinalNodeBattleState>.Known(
                                    completedBattle,
                                    0.85,
                                    [completedBattle.Evidence],
                                    completedBattle.CapturedAt)
                                : new Observation<FinalNodeBattleState>
                                {
                                    Status = ObservationStatus.Unknown,
                                    Value = completedBattle,
                                    Confidence = 0,
                                    Evidence = [completedBattle.Evidence],
                                    Uncertainty = completedBattle.FinalUncertainty.Count > 0
                                        ? completedBattle.FinalUncertainty
                                        : ["Final node data is incomplete and retained for review."],
                                    ObservedAt = completedBattle.CapturedAt
                                }
                            : terminalState.FinalBattle
                    };
                    analysis = analysis with { OperationalState = completionState };
                    historicalDashboard?.Observe(runId, analysis);
                    await SaveObservationAsync(
                            frame,
                            screenshotName,
                            analysis,
                            finalBattle,
                            cancellationToken)
                        .ConfigureAwait(false);
                    savedCount++;
                    var relativeScreenshot = Path.Combine(
                            "screenshots",
                            screenshotName)
                        .Replace('\\', '/');
                    var rating = phase2Ocr is null
                        ? null
                        : await Phase2RunCompletionDetector.ReadRatingAsync(
                                frame,
                                phase2Ocr.Numeric,
                                cancellationToken)
                            .ConfigureAwait(false);
                    checkpoint = RunCheckpointFactory.FromAnalysis(
                        checkpoint,
                        analysis,
                        savedCount,
                        RunCheckpointLifecycleStatus.Completed,
                        DateTimeOffset.UtcNow);
                    _ = await TrySaveCheckpointAsync(
                            checkpoint,
                            savedCount,
                            analysis,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var archive = await store.CompleteRunAsync(
                            runId,
                            frame.CapturedAt,
                            completionPageId,
                            completionNodeId,
                            relativeScreenshot,
                            rating,
                        cancellationToken)
                        .ConfigureAwait(false);
                    string? reportFile = null;
                    string? reportFailure = null;
                    if (summaryReportGenerator is not null)
                    {
                        try
                        {
                            var reportPath = await summaryReportGenerator.GenerateAsync(
                                    store.GetRunDirectory(runId),
                                    archive,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            reportFile = Path.GetRelativePath(
                                    store.GetRunDirectory(runId),
                                    reportPath)
                                .Replace('\\', '/');
                        }
                        catch (OperationCanceledException)
                            when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            reportFailure = exception.Message;
                            Publish(
                                runId,
                                savedCount,
                                analysis,
                                $"Final run data was archived, but challenge report generation failed: {exception}",
                                isError: true);
                        }
                    }
                    IReadOnlyList<string> deletedImageDirectories = [];
                    string? cleanupFailure = null;
                    if (options.DeleteScreenshotsOnCompletion)
                    {
                        if (reportFailure is not null)
                        {
                            cleanupFailure =
                                "Challenge report generation failed; screenshots were preserved for diagnosis.";
                        }
                        else
                        {
                            try
                            {
                                deletedImageDirectories =
                                    await store.DeleteRunImageArtifactsAsync(
                                            runId,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                            }
                            catch (Exception exception)
                                when (exception is IOException or UnauthorizedAccessException)
                            {
                                cleanupFailure = exception.Message;
                            }
                        }
                    }
                    await store.AppendEventAsync(
                            new RunEvent
                            {
                                EventId = $"{analysis.AnalysisId}:run-completed",
                                RunId = runId,
                                EventType = RunEventType.RunCompleted,
                                OccurredAt = frame.CapturedAt,
                                ObservedAt = frame.CapturedAt,
                                SourceAdapter = "phase2-live-screenshot",
                                Confidence = 1,
                                Evidence = analysis.Snapshot.PageId.Evidence,
                                Payload = JsonSerializer.SerializeToElement(
                                    new
                                    {
                                        archiveFile = "completed-run.v1.json",
                                        reportFile,
                                        reportFailure,
                                        archive.ArchiveVersion,
                                        archive.CompletionNodeId,
                                        failedRun,
                                        screenshotCleanupRequested =
                                            options.DeleteScreenshotsOnCompletion,
                                        deletedImageDirectories,
                                        cleanupFailure
                                    },
                                    AdvisorJson.Options)
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    Publish(
                        runId,
                        savedCount,
                        analysis,
                        $"Detected {completionPageId} at {completionNodeId}; " +
                        (reportFailure is null
                            ? "completed-run.v1.json and the offline challenge report were finalized."
                            : "completed-run.v1.json was finalized, but report generation failed and was logged.") +
                        (options.DeleteScreenshotsOnCompletion
                            ? cleanupFailure is null
                                ? $" Deleted image directories: {string.Join(", ", deletedImageDirectories)}."
                                : $" Screenshot cleanup failed and was retained for review: {cleanupFailure}."
                            : string.Empty),
                        isMilestone: true);
                        currentRunCompleted = true;
                        postCompletionBoundary.Reset();
                        _completionCandidatePageId = null;
                        _completionCandidateFrames = 0;
                        continue;
                    }
                }
                else
                {
                    _completionCandidatePageId = null;
                    _completionCandidateFrames = 0;
                }

                Phase2TrackingUpdate? tracking = null;
                var postCompletionNewRunConfirmed =
                    currentRunCompleted &&
                    postCompletionBoundary.Observe(analysis.OperationalState);
                if (analysis.OperationalState is not null)
                {
                    tracking = tracker.Observe(
                        analysis.OperationalState,
                        analysis.Snapshot.Health);
                    analysis = analysis with
                    {
                        OperationalState = tracking.Current
                    };
                    if (tracking.FinalizedBattle is not null)
                    {
                        analysis = analysis with
                        {
                            OperationalState = tracking.Current with
                            {
                                FinalBattle = tracking.FinalizedBattle.IsComplete
                                    ? Observation<FinalNodeBattleState>.Known(
                                        tracking.FinalizedBattle,
                                        0.85,
                                        [tracking.FinalizedBattle.Evidence],
                                        tracking.FinalizedBattle.CapturedAt)
                                    : new Observation<FinalNodeBattleState>
                                    {
                                        Status = ObservationStatus.Unknown,
                                        Value = tracking.FinalizedBattle,
                                        Confidence = 0,
                                        Evidence = [tracking.FinalizedBattle.Evidence],
                                        Uncertainty = tracking.FinalizedBattle.FinalUncertainty.Count > 0
                                            ? tracking.FinalizedBattle.FinalUncertainty
                                            : ["最终战斗帧包含残缺对象；仅供复盘，不能驱动高风险决策。"],
                                        ObservedAt = tracking.FinalizedBattle.CapturedAt
                                    }
                            }
                        };
                    }

                    if (!string.IsNullOrWhiteSpace(tracking.Diagnostic))
                    {
                        Publish(
                            runId,
                            savedCount,
                            analysis,
                            tracking.Diagnostic);
                    }

                    if (tracking.NewRunBoundaryConfirmed ||
                        postCompletionNewRunConfirmed)
                    {
                        var previousRunId = runId;
                        var boundaryAt = DateTimeOffset.UtcNow;
                        var previousRunWasCompleted = currentRunCompleted;
                        if (!previousRunWasCompleted)
                        {
                            checkpoint = checkpoint with
                            {
                                LifecycleStatus = RunCheckpointLifecycleStatus.Abandoned,
                                LastSavedAtUtc = boundaryAt,
                                Uncertainty = checkpoint.Uncertainty
                                    .Append("A confirmed preparation/battle/settlement reset to 1-1 started a distinct run.")
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray()
                            };
                            _ = await TrySaveCheckpointAsync(
                                    checkpoint,
                                    savedCount,
                                    analysis,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }

                        runId = $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-reset";
                        currentRunCompleted = false;
                        postCompletionBoundary.Reset();
                        analysis = WithRunId(analysis, runId);
                        analyzer.SeedRunIdentity(runId, new RunIdentityEvidence());
                        tracker = new Phase2OperationalStateTracker();
                        tracker.Observe(
                            analysis.OperationalState! with
                            {
                                FinalBattle = Observation<FinalNodeBattleState>.Unknown(
                                    "new run boundary seed")
                            },
                            analysis.Snapshot.Health);
                        checkpoint = RunCheckpointFactory.CreateInitial(
                            runId,
                            options.EntryMode,
                            boundaryAt);
                        savedCount = 0;
                        pendingPreparationFrame = null;
                        pendingPreparationScreenshotName = null;
                        pendingPreparationAnalysis = null;
                        previousFingerprint = null;
                        previousPageRecognized = null;
                        await SaveObservationAsync(
                                frame,
                                screenshotName,
                                analysis,
                                tracking.FinalizedBattle,
                                cancellationToken)
                            .ConfigureAwait(false);
                        savedCount++;
                        historicalDashboard?.Observe(runId, analysis);
                        checkpoint = RunCheckpointFactory.FromAnalysis(
                            checkpoint,
                            analysis,
                            savedCount,
                            RunCheckpointLifecycleStatus.Active,
                            boundaryAt);
                        _ = await TrySaveCheckpointAsync(
                                checkpoint,
                                savedCount,
                                analysis,
                                cancellationToken)
                            .ConfigureAwait(false);
                        lastRunCheckpointAt = boundaryAt;
                        Publish(
                            runId,
                            savedCount,
                            analysis,
                            previousRunWasCompleted
                                ? $"已确认新 1-1 对局；上一局 {previousRunId} 已完成归档，新记录为 {runId}。"
                                : $"已确认新 1-1 对局；旧记录 {previousRunId} 已封存为未完成，新记录为 {runId}。",
                            isMilestone: true);
                        continue;
                    }

                }

                if (currentRunCompleted)
                {
                    // Main menu, opening pages and the first unconfirmed 1-1
                    // frame belong to neither archive. Wait for a confirmed
                    // new boundary without mutating the completed run.
                    continue;
                }

                historicalDashboard?.Observe(runId, analysis);

                var fingerprint = Fingerprint(analysis);
                var now = DateTimeOffset.UtcNow;
                var changed = !string.Equals(
                        fingerprint,
                        previousFingerprint,
                        StringComparison.Ordinal);
                var confirmed = tracking is null ||
                                tracking.PersistentStateConfirmed;
                // One-time pages (enemy overview and investment selection)
                // intentionally bypass the operational state machine. Their
                // classifier-backed Snapshot.PageId is still reliable page
                // evidence and must not be reported or retained as unknown.
                var recognizedPage = Phase2PageRecognition.IsKnown(analysis);
                var pageFamily = analysis.OperationalState?.PageFamily ??
                                 Phase2PageFamily.Unknown;
                var isPreparation = pageFamily == Phase2PageFamily.Preparation;
                var isBattle = pageFamily == Phase2PageFamily.Battle;
                if (Phase2NodeRetentionPolicy.ShouldBufferPreparation(
                        pageFamily,
                        confirmed))
                {
                    pendingPreparationFrame = frame;
                    pendingPreparationScreenshotName = screenshotName;
                    pendingPreparationAnalysis = analysis;
                }

                if (Phase2NodeRetentionPolicy.ShouldFlushPreparation(pageFamily) &&
                    pendingPreparationFrame is not null &&
                    pendingPreparationScreenshotName is not null &&
                    pendingPreparationAnalysis is not null)
                {
                    await SaveObservationAsync(
                            pendingPreparationFrame,
                            pendingPreparationScreenshotName,
                            pendingPreparationAnalysis,
                            null,
                            cancellationToken)
                        .ConfigureAwait(false);
                    savedCount++;
                    pendingPreparationFrame = null;
                    pendingPreparationScreenshotName = null;
                    pendingPreparationAnalysis = null;
                }

                var shouldSaveRecognized =
                    Phase2NodeRetentionPolicy.ShouldPersistCurrent(
                        pageFamily,
                        recognizedPage,
                        changed,
                        confirmed,
                        tracking?.FinalizedBattle is not null,
                        now - lastSavedAt >= ObservationCheckpointInterval);
                var shouldSaveDegraded =
                    Phase2NodeRetentionPolicy.ShouldPersistDegraded(
                        recognizedPage,
                        pipelineUpdate.IsCritical,
                        previousPageRecognized is not false,
                        now - lastDegradedSavedAt >=
                        DegradedCheckpointInterval);
                if (shouldSaveRecognized || shouldSaveDegraded)
                {
                    await SaveObservationAsync(
                            frame,
                            screenshotName,
                            analysis,
                            tracking?.FinalizedBattle,
                            cancellationToken)
                        .ConfigureAwait(false);
                    previousFingerprint = fingerprint;
                    lastSavedAt = now;
                    if (shouldSaveDegraded)
                    {
                        lastDegradedSavedAt = now;
                    }
                    savedCount++;
                    Publish(
                        runId,
                        savedCount,
                        analysis,
                        $"已保存关键状态 #{savedCount}：" +
                        $"{analysis.Snapshot.PageId.Value ?? "未知页面"}。",
                        isMilestone:
                            tracking?.PageChanged is true ||
                            tracking?.FinalizedBattle is not null);
                }

                var reliableCheckpointFrame =
                    recognizedPage && confirmed ||
                    tracking?.FinalizedBattle is not null;
                var confirmedNode = ResolveKnownNode(analysis);
                var confirmedPage = analysis.Snapshot.PageId.Status ==
                                    ObservationStatus.Known
                    ? analysis.Snapshot.PageId.Value
                    : null;
                var checkpointStateChanged =
                    !string.Equals(
                        checkpoint.LastConfirmedNodeId,
                        confirmedNode,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        checkpoint.LastConfirmedPageId,
                        confirmedPage,
                        StringComparison.OrdinalIgnoreCase) ||
                    tracking?.FinalizedBattle is not null;
                if (reliableCheckpointFrame &&
                    (checkpointStateChanged ||
                     now - lastRunCheckpointAt >= RunCheckpointInterval))
                {
                    checkpoint = RunCheckpointFactory.FromAnalysis(
                        checkpoint,
                        analysis,
                        savedCount,
                        RunCheckpointLifecycleStatus.Active,
                        now);
                    _ = await TrySaveCheckpointAsync(
                            checkpoint,
                            savedCount,
                            analysis,
                            cancellationToken)
                        .ConfigureAwait(false);
                    lastRunCheckpointAt = now;
                }

                if ((!recognizedPage || isPreparation || isBattle) &&
                    !shouldSaveDegraded)
                {
                    Publish(
                        runId,
                        savedCount,
                        analysis,
                        tracking?.Message ??
                        "当前帧无法可靠识别；已安全跳过且未写入正式对局记录。",
                        isMilestone:
                            tracking?.PageChanged is true ||
                            tracking?.FinalizedBattle is not null);
                }

                previousPageRecognized = recognizedPage;
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                var failureNow = DateTimeOffset.UtcNow;
                firstFailureAt ??= failureNow;
                var failureDuration = failureNow - firstFailureAt.Value;
                if (consecutiveFailures == 1 ||
                    consecutiveFailures == FailureCheckpointThreshold ||
                    failureNow - lastFailureNoticeAt >=
                    FailureRecoveryNoticeInterval)
                {
                    Publish(
                        runId,
                        savedCount,
                        null,
                        $"采集暂时失败（连续 {consecutiveFailures} 次，" +
                        $"{failureDuration.TotalSeconds:F1} 秒）：{exception.Message}；" +
                        "记录器正在等待窗口或截图管线恢复。",
                        isError: true);
                    lastFailureNoticeAt = failureNow;
                }

                if (consecutiveFailures == FailureCheckpointThreshold)
                {
                    _ = await TrySaveCheckpointAsync(
                            checkpoint with
                            {
                                LifecycleStatus =
                                    RunCheckpointLifecycleStatus.Active,
                                LastSavedAtUtc = failureNow
                            },
                            savedCount,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (FailureRecoveryExpired(
                        firstFailureAt.Value,
                        failureNow))
                {
                    checkpoint = checkpoint with
                    {
                        LifecycleStatus = RunCheckpointLifecycleStatus.Paused,
                        LastSavedAtUtc = failureNow
                    };
                    _ = await TrySaveCheckpointAsync(
                            checkpoint,
                            savedCount,
                            null,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    Publish(
                        runId,
                        savedCount,
                        null,
                        "窗口或截图管线连续两分钟未恢复；断点已保存，" +
                        "收集器已明确暂停，可在窗口恢复后继续记录。",
                        isError: true);
                    return;
                }

                await Task.Delay(
                        FailureRecoveryDelay(consecutiveFailures),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

        }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal collector shutdown.
        }

        checkpoint = checkpoint with
        {
            LifecycleStatus = RunCheckpointLifecycleStatus.Paused,
            LastSavedAtUtc = DateTimeOffset.UtcNow
        };
        _ = await TrySaveCheckpointAsync(
                checkpoint,
                savedCount,
                null,
                CancellationToken.None)
            .ConfigureAwait(false);

        Publish(
            runId,
            savedCount,
            null,
            "只读局势收集已停止。",
            isMilestone: true);
    }

    internal static bool FailureRecoveryExpired(
        DateTimeOffset firstFailureAt,
        DateTimeOffset now) =>
        now - firstFailureAt >= MaximumFailureRecoveryDuration;

    internal static TimeSpan FailureRecoveryDelay(int consecutiveFailures) =>
        TimeSpan.FromMilliseconds(
            Math.Clamp(consecutiveFailures * 150, 200, 2_000));

    private static ScreenshotAnalysisResult WithRunId(
        ScreenshotAnalysisResult analysis,
        string runId) => string.Equals(
            analysis.Snapshot.RunId,
            runId,
            StringComparison.Ordinal)
        ? analysis
        : analysis with { Snapshot = analysis.Snapshot with { RunId = runId } };

    private static ScreenshotAnalysisResult EnsureTerminalOperationalState(
        ScreenshotAnalysisResult analysis)
    {
        if (analysis.OperationalState is not null)
        {
            return analysis;
        }

        var pageId = analysis.Snapshot.PageId.Status == ObservationStatus.Known
            ? analysis.Snapshot.PageId.Value
            : null;
        return analysis with
        {
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.BattleSettlement,
                PageId = pageId,
                NodeId = Observation<string>.Unknown(
                    "whole-run terminal page does not display a reliable node id")
            }
        };
    }

    private static RunIdentityEvidence MergeInitialIdentity(
        RunIdentityEvidence current,
        RunIdentityEvidence? initial)
    {
        if (initial is null)
        {
            return current;
        }

        return new RunIdentityEvidence
        {
            InvestmentEnvironmentId =
                current.InvestmentEnvironmentId ??
                initial.InvestmentEnvironmentId,
            InvestmentStrategyIds = current.InvestmentStrategyIds
                .Concat(initial.InvestmentStrategyIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            EnemyAffixIds = current.EnemyAffixIds.Count > 0
                ? current.EnemyAffixIds
                : initial.EnemyAffixIds,
            EnemyIds = current.EnemyIds.Count > 0
                ? current.EnemyIds
                : initial.EnemyIds
        };
    }

    internal async Task SaveObservationAsync(
        CaptureFrame frame,
        string screenshotName,
        ScreenshotAnalysisResult analysis,
        FinalNodeBattleState? finalizedBattle,
        CancellationToken cancellationToken)
    {
        var runDirectory = store.GetRunDirectory(analysis.Snapshot.RunId);
        var screenshotDirectory = Path.Combine(runDirectory, "screenshots");
        Directory.CreateDirectory(screenshotDirectory);
        var screenshotPath = Path.Combine(screenshotDirectory, screenshotName);
        await Task.Run(
                () => frame.SavePng(screenshotPath),
                cancellationToken)
            .ConfigureAwait(false);
        var operationalState = analysis.OperationalState;
        if (operationalState is not null)
        {
            var pendingIcons = await SavePendingIconCropsAsync(
                    frame,
                    screenshotName,
                    analysis,
                    cancellationToken)
                .ConfigureAwait(false);
            var recognitionTrace = await SaveRecognitionFailureCropsAsync(
                    frame,
                    screenshotName,
                    analysis,
                    cancellationToken)
                .ConfigureAwait(false);
            analysis = analysis with
            {
                OperationalState = operationalState with
                {
                    PendingIcons = pendingIcons,
                    RecognitionTrace = recognitionTrace
                }
            };
        }

        await store.SaveAnalysisAsync(analysis, cancellationToken)
            .ConfigureAwait(false);
        if (finalizedBattle is not null)
        {
            await store.SaveFinalNodeBattleAsync(
                    analysis.Snapshot.RunId,
                    finalizedBattle,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var runEvent in CreateEvents(analysis))
        {
            await store.AppendEventAsync(runEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<RunEvent> CreateEvents(
        ScreenshotAnalysisResult analysis)
    {
        var snapshot = analysis.Snapshot;
        var events = new List<RunEvent>();
        Add(events, analysis, RunEventType.PageObserved, "page", snapshot.PageId);
        Add(events, analysis, RunEventType.StageObserved, "stage", snapshot.Stage);
        Add(events, analysis, RunEventType.EconomyObserved, "economy", snapshot.Economy);
        Add(
            events,
            analysis,
            RunEventType.CumulativeSpendObserved,
            "cumulative-spend",
            snapshot.CumulativeSpend);
        Add(events, analysis, RunEventType.HealthObserved, "health", snapshot.Health);
        Add(events, analysis, RunEventType.ActionPointsObserved, "action-points", snapshot.ActionPoints);
        Add(events, analysis, RunEventType.NodeDamageObserved, "node-damage", snapshot.CurrentNodeDamage);
        Add(events, analysis, RunEventType.BoardObserved, "board", snapshot.BoardCharacterIds);
        Add(events, analysis, RunEventType.BenchObserved, "bench", snapshot.BenchCharacterIds);
        Add(events, analysis, RunEventType.ShopObserved, "shop", snapshot.ShopCharacterIds);
        Add(events, analysis, RunEventType.LineupObserved, "lineup", snapshot.LineupIds);
        Add(events, analysis, RunEventType.SynergiesObserved, "synergies", snapshot.SynergyIds);
        AddIfFresh(events, analysis, RunEventType.InvestmentEnvironmentObserved, "investment-environment", snapshot.InvestmentEnvironmentId);
        AddIfFresh(events, analysis, RunEventType.InvestmentStrategyObserved, "investment-strategies", snapshot.InvestmentStrategyIds);
        Add(events, analysis, RunEventType.EquipmentObserved, "equipment", snapshot.EquipmentIds);
        AddIfFresh(events, analysis, RunEventType.SpecialItemObserved, "special-items", snapshot.SpecialItemIds);
        AddIfFresh(events, analysis, RunEventType.ExpertAdvisorObserved, "expert-advisors", snapshot.ExpertAdvisorIds);
        AddIfFresh(events, analysis, RunEventType.EnemyObserved, "opening-enemies", snapshot.EnemyIds);
        if (analysis.OperationalState is { } operational)
        {
            AddIfFresh(
                events,
                analysis,
                RunEventType.RewardObserved,
                "settlement-gold-reward",
                operational.SettlementGoldReward);
        }
        return events;
    }

    private async Task<IReadOnlyList<PendingIconObservation>> SavePendingIconCropsAsync(
        CaptureFrame frame,
        string screenshotName,
        ScreenshotAnalysisResult analysis,
        CancellationToken cancellationToken)
    {
        var pending = analysis.OperationalState?.PendingIcons ?? [];
        if (pending.Count == 0)
        {
            return pending;
        }

        var directory = Path.Combine(
            store.GetRunDirectory(analysis.Snapshot.RunId),
            "unresolved-icons");
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(screenshotName);
        var results = new PendingIconObservation[pending.Count];
        for (var index = 0; index < pending.Count; index++)
        {
            var item = pending[index];
            var region = new NormalizedRect(
                item.Region.X,
                item.Region.Y,
                item.Region.Width,
                item.Region.Height).ToPixels(frame.Width, frame.Height);
            var fileName =
                $"{SafeFileSegment(stem)}-{index:D2}-{SafeFileSegment(item.Category.ToString())}-{SafeFileSegment(item.SlotKey)}.png";
            var cropPath = Path.Combine(directory, fileName);
            try
            {
                await Task.Run(
                        () => frame.SavePng(cropPath, region),
                        cancellationToken)
                    .ConfigureAwait(false);
                results[index] = item with
                {
                    CropFile = Path.Combine("unresolved-icons", fileName)
                        .Replace('\\', '/')
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A diagnostic crop must never block the durable analysis.
                results[index] = item with { CropFile = null };
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<Phase2FieldRecognitionTrace>> SaveRecognitionFailureCropsAsync(
        CaptureFrame frame,
        string screenshotName,
        ScreenshotAnalysisResult analysis,
        CancellationToken cancellationToken)
    {
        var trace = analysis.OperationalState?.RecognitionTrace ?? [];
        if (trace.Count == 0)
        {
            return trace;
        }

        var runDirectory = store.GetRunDirectory(analysis.Snapshot.RunId);
        var directory = Path.Combine(runDirectory, "recognition-failures");
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(screenshotName);
        var results = trace.ToArray();
        for (var index = 0; index < trace.Count; index++)
        {
            var item = trace[index];
            if (item.Status == ObservationStatus.Known && item.Confidence >= 0.65)
            {
                continue;
            }

            var fileName =
                $"{SafeFileSegment(stem)}-{index:D2}-{SafeFileSegment(item.Field)}.png";
            var region = new NormalizedRect(
                item.Region.X,
                item.Region.Y,
                item.Region.Width,
                item.Region.Height).ToPixels(frame.Width, frame.Height);
            var cropPath = Path.Combine(directory, fileName);
            try
            {
                await Task.Run(
                        () => frame.SavePng(cropPath, region),
                        cancellationToken)
                    .ConfigureAwait(false);
                results[index] = item with
                {
                    CropFile = Path.Combine("recognition-failures", fileName)
                        .Replace('\\', '/')
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep the trace but do not write a path to a missing crop.
                results[index] = item with { CropFile = null };
                continue;
            }

            var manifestEntry = JsonSerializer.Serialize(
                new
                {
                    runId = analysis.Snapshot.RunId,
                    version = analysis.ApplicationVersion,
                    analysisId = analysis.AnalysisId,
                    sourceScreenshot = screenshotName,
                    failedCrop = $"recognition-failures/{fileName}",
                    trace = results[index]
                },
                AdvisorJson.Options);
            await File.AppendAllTextAsync(
                    Path.Combine(directory, "manifest.jsonl"),
                    manifestEntry + Environment.NewLine,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return results;
    }

    private static string SafeFileSegment(string value)
    {
        var safe = string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private async Task SaveCriticalPipelineFailureAsync(
        CaptureFrame frame,
        string screenshotName,
        string runId,
        string reason,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            store.GetRunDirectory(runId),
            "recognition-failures");
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(screenshotName);
        var failedName = $"{stem}-critical-queue-drop.png";
        await Task.Run(
                () => frame.SavePng(Path.Combine(directory, failedName)),
                cancellationToken)
            .ConfigureAwait(false);
        var manifestEntry = JsonSerializer.Serialize(
            new
            {
                runId,
                version = Phase2RecognitionTraceBuilder.ApplicationVersion,
                sourceScreenshot = screenshotName,
                failedScreenshot = $"recognition-failures/{failedName}",
                capturedAt = frame.CapturedAt,
                reason
            },
            AdvisorJson.Options);
        await File.AppendAllTextAsync(
                Path.Combine(directory, "manifest.jsonl"),
                manifestEntry + Environment.NewLine,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Fingerprint(ScreenshotAnalysisResult analysis)
    {
        var material = LocalRunStore.Fingerprint(analysis.Snapshot) + "|" +
                       (analysis.OperationalState is null
                           ? string.Empty
                           : AdvisorJson.Serialize(
                               analysis.OperationalState,
                               indented: false));
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(material)));
    }

    private async Task<bool> TrySaveCheckpointAsync(
        RunCheckpointRecord checkpoint,
        int savedCount,
        ScreenshotAnalysisResult? analysis,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveCheckpointAsync(checkpoint, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Publish(
                checkpoint.RunId,
                savedCount,
                analysis,
                $"对局断点保存失败；现有截图和节点数据仍保留：{exception.Message}",
                isError: true);
            return false;
        }
    }

    private static string? ResolveKnownNode(ScreenshotAnalysisResult analysis) =>
        analysis.OperationalState?.NodeId.Status == ObservationStatus.Known
            ? analysis.OperationalState.NodeId.Value
            : analysis.Snapshot.Stage.Status == ObservationStatus.Known
                ? analysis.Snapshot.Stage.Value
                : null;

    private static void Add<T>(
        ICollection<RunEvent> events,
        ScreenshotAnalysisResult analysis,
        RunEventType eventType,
        string suffix,
        Observation<T> observation)
    {
        var observedAt = observation.ObservedAt ?? analysis.Snapshot.AsOf;
        events.Add(new RunEvent
        {
            EventId = $"{analysis.AnalysisId}:{suffix}",
            RunId = analysis.Snapshot.RunId,
            EventType = eventType,
            OccurredAt = analysis.Snapshot.AsOf,
            ObservedAt = observedAt < analysis.Snapshot.AsOf
                ? analysis.Snapshot.AsOf
                : observedAt,
            SourceAdapter = "phase2-live-screenshot",
            Confidence = observation.Confidence,
            Uncertainty = observation.Uncertainty,
            Evidence = observation.Evidence,
            Payload = JsonSerializer.SerializeToElement(
                observation,
                AdvisorJson.Options)
        });
    }

    private static void AddIfFresh<T>(
        ICollection<RunEvent> events,
        ScreenshotAnalysisResult analysis,
        RunEventType eventType,
        string suffix,
        Observation<T> observation)
    {
        if (observation.Value is null ||
            observation.ObservedAt is not null &&
            observation.ObservedAt != analysis.Snapshot.AsOf)
        {
            return;
        }

        Add(events, analysis, eventType, suffix, observation);
    }

    private void Publish(
        string runId,
        int savedCount,
        ScreenshotAnalysisResult? analysis,
        string message,
        bool isError = false,
        bool isMilestone = false) => Updated?.Invoke(
            this,
            new LiveCollectionUpdate(
                runId,
                savedCount,
                analysis,
                message,
                isError,
                isMilestone));
}

internal static class Phase2NodeRetentionPolicy
{
    public static bool ShouldBufferPreparation(
        Phase2PageFamily page,
        bool confirmed) =>
        page == Phase2PageFamily.Preparation && confirmed;

    public static bool ShouldFlushPreparation(Phase2PageFamily page) =>
        page == Phase2PageFamily.Battle;

    public static bool ShouldPersistCurrent(
        Phase2PageFamily page,
        bool recognizedPage,
        bool changed,
        bool confirmed,
        bool finalizedBattle,
        bool checkpointDue) =>
        finalizedBattle ||
        (recognizedPage &&
         page is not Phase2PageFamily.Preparation and not Phase2PageFamily.Battle &&
         ((changed && confirmed) || checkpointDue));

    public static bool ShouldPersistDegraded(
        bool recognizedPage,
        bool critical,
        bool enteringUnknown,
        bool checkpointDue) =>
        !recognizedPage && (critical || enteringUnknown || checkpointDue);
}
