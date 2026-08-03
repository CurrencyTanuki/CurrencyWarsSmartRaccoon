using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

internal sealed record Phase2RecognitionWorkItem(
    Phase2BufferedFrame BufferedFrame,
    string ScreenshotName,
    string EvidenceSourceId,
    string RunId,
    bool IsCritical);

internal sealed record Phase2RealtimePipelineUpdate(
    CaptureFrame? Frame,
    string? ScreenshotName,
    ScreenshotAnalysisResult? Analysis,
    bool IsHeartbeat,
    bool IsRevalidated,
    bool IsCritical,
    TimeSpan AnalysisAge,
    string? Error = null);

internal static class Phase2PageRecognition
{
    public static bool IsKnown(ScreenshotAnalysisResult analysis)
    {
        if (analysis.OperationalState?.PageFamily == Phase2PageFamily.Transition)
        {
            return false;
        }

        return analysis.Snapshot.PageId.Status == ObservationStatus.Known &&
               !string.IsNullOrWhiteSpace(analysis.Snapshot.PageId.Value) ||
               analysis.OperationalState?.PageFamily is not
                   (null or Phase2PageFamily.Unknown);
    }
}

internal static class Phase2TransitionFramePolicy
{
    private const double TerminalAnchorTolerance = 0.05;

    public static ScreenshotAnalysisResult MarkIfApplicable(
        ScreenshotAnalysisResult analysis,
        Phase2BufferedFrame buffered)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(buffered);
        if (HasReliableBusinessPageEvidence(analysis) ||
            HasNearThresholdBusinessPageEvidence(analysis))
        {
            return analysis;
        }

        var reason = buffered.ChangeKind switch
        {
            Phase2FrameChangeKind.SceneTransition =>
                "large multi-region perceptual-hash change",
            Phase2FrameChangeKind.RegionalChange =>
                "unresolved page while visible regions are still changing",
            _ when IsLowInformationTransition(buffered.Frame) =>
                "low-information dark transition frame",
            _ => null
        };
        if (reason is null)
        {
            return analysis;
        }

        var evidence = new EvidenceReference(
            $"frame:{buffered.Sequence}",
            "frame-difference:transition-animation",
            reason,
            buffered.Frame.CapturedAt,
            Confidence: 0.90);
        var operational = (analysis.OperationalState ?? new Phase2OperationalState()) with
        {
            PageFamily = Phase2PageFamily.Transition,
            PageId = "transition_animation",
            Diagnostics = (analysis.OperationalState?.Diagnostics ?? [])
                .Append(
                    "Frame classified as a scene transition; it is excluded from " +
                    "business-state persistence and cannot replace reliable fields.")
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        return analysis with
        {
            Snapshot = analysis.Snapshot with
            {
                PageId = Observation<string>.Known(
                    "transition_animation",
                    0.90,
                    [evidence],
                    buffered.Frame.CapturedAt),
                Stage = Observation<string>.Unknown(
                    "transition frames do not have a business stage",
                    [evidence],
                    buffered.Frame.CapturedAt)
            },
            OperationalState = operational
        };
    }

    private static bool HasReliableBusinessPageEvidence(
        ScreenshotAnalysisResult analysis)
    {
        if (analysis.Snapshot.PageId.Status == ObservationStatus.Known &&
            !string.IsNullOrWhiteSpace(analysis.Snapshot.PageId.Value))
        {
            return true;
        }

        var state = analysis.OperationalState;
        if (state is null)
        {
            return false;
        }

        return state.PageFamily switch
        {
            Phase2PageFamily.BattleSettlement => true,
            Phase2PageFamily.Preparation =>
                state.NodeId.Status == ObservationStatus.Known ||
                state.Formation.Status == ObservationStatus.Known ||
                state.PlayerProgress.Status == ObservationStatus.Known,
            Phase2PageFamily.Battle =>
                state.NodeId.Status == ObservationStatus.Known ||
                state.RemainingActionValue.Status == ObservationStatus.Known ||
                state.BattleScreenDamageCandidate.Status == ObservationStatus.Known ||
                state.BattleDamage.Status == ObservationStatus.Known ||
                state.BattleSynergyDamage.Status == ObservationStatus.Known,
            Phase2PageFamily.Unknown or Phase2PageFamily.Transition => false,
            _ => state.PageFamily != Phase2PageFamily.Unknown
        };
    }

    private static bool HasNearThresholdBusinessPageEvidence(
        ScreenshotAnalysisResult analysis)
    {
        var messages = analysis.Warnings.Concat(
            analysis.OperationalState?.Diagnostics ?? []);
        return messages.Any(message =>
            HasNearThresholdAnchor(
                message,
                "challenge_health_depleted/challenge_ended_title=") ||
            HasNearThresholdAnchor(
                message,
                "challenge_failed/challenge_failed_title=") ||
            HasNearThresholdAnchor(
                message,
                "reward_battle/reward_battle_status_bar=",
                tolerance: 0.07));
    }

    private static bool HasNearThresholdAnchor(
        string message,
        string anchor,
        double tolerance = TerminalAnchorTolerance)
    {
        var start = message.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += anchor.Length;
        var separator = message.IndexOf('/', start);
        if (separator < 0 ||
            !double.TryParse(
                message.AsSpan(start, separator - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var confidence))
        {
            return false;
        }

        var end = separator + 1;
        while (end < message.Length &&
               (char.IsDigit(message[end]) || message[end] is '.' or '-'))
        {
            end++;
        }

        return double.TryParse(
                   message.AsSpan(separator + 1, end - separator - 1),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var threshold) &&
               confidence >= threshold - tolerance;
    }

    private static bool IsLowInformationTransition(CaptureFrame frame)
    {
        const int horizontalSamples = 64;
        const int verticalSamples = 36;
        var dark = 0;
        var samples = 0;
        for (var sampleY = 0; sampleY < verticalSamples; sampleY++)
        {
            var y = Math.Min(
                frame.Height - 1,
                sampleY * frame.Height / verticalSamples);
            for (var sampleX = 0; sampleX < horizontalSamples; sampleX++)
            {
                var x = Math.Min(
                    frame.Width - 1,
                    sampleX * frame.Width / horizontalSamples);
                var offset = y * frame.Stride + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                var luminance = (red * 54 + green * 183 + blue * 19) >> 8;
                if (luminance <= 28)
                {
                    dark++;
                }

                samples++;
            }
        }

        return dark >= samples * 0.88;
    }

    public static bool ShouldDiscard(ScreenshotAnalysisResult analysis) =>
        analysis.OperationalState?.PageFamily == Phase2PageFamily.Transition;
}

internal static class Phase2CriticalFramePolicy
{
    private static readonly TimeSpan MinimumBoundaryInterval =
        TimeSpan.FromSeconds(1);

    public static bool ShouldQueueBoundary(
        bool fastPageChanged,
        DateTimeOffset observedAt,
        ref DateTimeOffset lastQueuedAt,
        bool force = false)
    {
        if (!fastPageChanged ||
            !force && observedAt - lastQueuedAt < MinimumBoundaryInterval)
        {
            return false;
        }

        lastQueuedAt = observedAt;
        return true;
    }
}

public readonly record struct Phase2FastPageObservation(
    bool IsMatched,
    Phase2PageFamily PageFamily,
    string? PageId = null)
{
    public static Phase2FastPageObservation None =>
        new(false, Phase2PageFamily.Unknown);
}

internal readonly record struct Phase2PageDiagnosticInference(
    string PageId,
    Phase2PageFamily PageFamily,
    double Confidence,
    IReadOnlyList<PageAnchorDiagnostic> Evidence);

/// <summary>
/// Resolves read-only phase-two page hints from conservative combinations of
/// positive and exclusion evidence. This does not change the shared page
/// thresholds used by the phase-one input automation.
/// </summary>
internal static class Phase2PageDiagnosticFallback
{
    private const double GenericPreparationMinimum = 0.40;
    private const double SpecificPreparationMinimum = 0.65;
    private const double DegradedGenericPreparationMinimum = 0.35;
    private const double StrongSpecificPreparationMinimum = 0.82;
    private const double BattleExclusionMaximum = 0.25;
    private const double StrongBattleDamageTabsMinimum = 0.82;
    private const double UnambiguousBattleDamageTabsMinimum = 0.74;
    private const double DegradedBattlePauseMinimum = 0.55;
    private const double MainMinimum = 0.62;
    private const double MainDominanceMargin = 0.15;

    public static Phase2PageDiagnosticInference? TryInfer(
        IReadOnlyList<PageAnchorDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var home = diagnostics.FirstOrDefault(item =>
            string.Equals(
                item.AnchorId,
                "currency_wars_home_title",
                StringComparison.Ordinal));
        if (home is not null)
        {
            var competitors = diagnostics
                .Where(item => !string.Equals(
                    item.PageId,
                    home.PageId,
                    StringComparison.Ordinal))
                .OrderByDescending(item => item.Confidence)
                .ToArray();
            var strongestCompetitor = competitors.FirstOrDefault();
            var hasPassingCompetitor = competitors.Any(item =>
                item.Confidence >= item.Threshold);
            var isDominant = strongestCompetitor is null ||
                             home.Confidence - strongestCompetitor.Confidence >=
                             MainDominanceMargin;
            if (home.Confidence >= MainMinimum &&
                !hasPassingCompetitor &&
                isDominant)
            {
                return new Phase2PageDiagnosticInference(
                    "currency_wars_home",
                    Phase2PageFamily.Main,
                    Math.Clamp(home.Confidence, 0.62, 0.82),
                    strongestCompetitor is null
                        ? [home]
                        : [home, strongestCompetitor]);
            }
        }

        var battleDamageTabs = diagnostics.FirstOrDefault(item =>
            string.Equals(
                item.AnchorId,
                "battle_generic_damage_tabs",
                StringComparison.Ordinal));
        var battlePause = diagnostics.FirstOrDefault(item =>
            string.Equals(
                item.AnchorId,
                "battle_generic_pause_control",
                StringComparison.Ordinal));
        var generic = diagnostics.FirstOrDefault(item =>
            string.Equals(
                item.AnchorId,
                "preparation_stage_label",
                StringComparison.Ordinal));
        var specific = diagnostics
            .Where(item => item.AnchorId is
                "preparation_stage_1_1" or "preparation_stage_1_2")
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        var hasPreparationPair = generic is not null && specific is not null &&
                                 generic.Confidence >= GenericPreparationMinimum &&
                                 specific.Confidence >= SpecificPreparationMinimum;
        if (battleDamageTabs is not null &&
            battleDamageTabs.Confidence >= battleDamageTabs.Threshold &&
            (battleDamageTabs.Confidence >= StrongBattleDamageTabsMinimum ||
             battlePause is not null &&
             battlePause.Confidence >= DegradedBattlePauseMinimum ||
             battleDamageTabs.Confidence >= UnambiguousBattleDamageTabsMinimum &&
             !hasPreparationPair &&
             (home is null || home.Confidence < MainMinimum)))
        {
            var evidence = battlePause is null
                ? new[] { battleDamageTabs }
                : [battleDamageTabs, battlePause];
            return new Phase2PageDiagnosticInference(
                "battle_generic",
                Phase2PageFamily.Battle,
                Math.Clamp(battleDamageTabs.Confidence, 0.72, 0.88),
                evidence);
        }

        if (generic is null || specific is null)
        {
            return null;
        }

        var hasStrongBattleEvidence = battleDamageTabs is not null &&
                                      battleDamageTabs.Confidence >=
                                      battleDamageTabs.Threshold;
        if (hasStrongBattleEvidence)
        {
            return null;
        }

        var normalPair =
            generic.Confidence >= GenericPreparationMinimum &&
            specific.Confidence >= SpecificPreparationMinimum;
        // A blank preparation board weakens the small generic edge template.
        // Recover only when the larger stage template is strong and the
        // mutually-exclusive battle damage tabs are demonstrably absent. This
        // is phase-two evidence fusion; it does not lower shared automation
        // thresholds or promote a single weak match.
        var degradedButUnambiguousPair =
            generic.Confidence >= DegradedGenericPreparationMinimum &&
            specific.Confidence >= StrongSpecificPreparationMinimum &&
            battleDamageTabs is not null &&
            battleDamageTabs.Confidence <= BattleExclusionMaximum;
        if (!normalPair && !degradedButUnambiguousPair)
        {
            return null;
        }

        var confidence = Math.Clamp(
            (generic.Confidence + specific.Confidence) / 2,
            0.55,
            0.80);
        return new Phase2PageDiagnosticInference(
            "preparation_generic",
            Phase2PageFamily.Preparation,
            confidence,
            [generic, specific]);
    }
}

public interface IPhase2FastPageClassifier
{
    Phase2FastPageObservation Classify(CaptureFrame frame);
}

public sealed class Phase2FastPageClassifier : IPhase2FastPageClassifier
{
    private static readonly IReadOnlySet<string> FastPageIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "currency_wars_home",
            "preparation_1_1",
            "preparation_1_2",
            "preparation_generic",
            "reward_shop",
            "reward_battle",
            "reward_battle_pause",
            "battle_generic",
            "incomplete_lineup_prompt",
            "challenge_success",
            "challenge_failed",
            "challenge_health_depleted",
            "investment_environment",
            "investment_strategy",
            "companion_selection",
            "enemy_overview"
        };
    private readonly TemplateGamePageClassifier classifier;

    public IReadOnlyList<PageAnchorDiagnostic> LastDiagnostics =>
        classifier.LastDiagnostics;

    public Phase2FastPageClassifier(
        ITemplateMatcher templateMatcher,
        IReadOnlyList<GamePageDefinition> pages)
    {
        var fastPages = pages
            .Where(page => FastPageIds.Contains(page.Id))
            .ToArray();
        if (fastPages.Length != FastPageIds.Count)
        {
            var missing = FastPageIds
                .Except(fastPages.Select(page => page.Id), StringComparer.Ordinal);
            throw new InvalidDataException(
                "Fast page classifier is missing definitions: " +
                string.Join(", ", missing));
        }

        classifier = new TemplateGamePageClassifier(templateMatcher, fastPages);
    }

    public Phase2FastPageObservation Classify(CaptureFrame frame)
    {
        var result = classifier.Classify(frame);
        if (result is not null)
        {
            return new Phase2FastPageObservation(
                true,
                Phase2OperationalScreenshotAnalyzer.MapPage(result.PageId),
                result.PageId);
        }

        var inferred = Phase2PageDiagnosticFallback.TryInfer(
            classifier.LastDiagnostics);
        return inferred is null
            ? Phase2FastPageObservation.None
            : new Phase2FastPageObservation(
                true,
                inferred.Value.PageFamily,
                inferred.Value.PageId);
    }
}

internal sealed record Phase2SelectedFrame(
    Phase2BufferedFrame BufferedFrame,
    bool IsCritical);

internal sealed record Phase2FrameSelection(
    Phase2BufferedFrame Current,
    IReadOnlyList<Phase2SelectedFrame> FramesToRecognize);

/// <summary>
/// Owns the real-time ring buffer and candidate-selection clocks. Keeping this
/// decision separate from capture and OCR lets dataset replay exercise exactly
/// the same selection path without introducing capture delays.
/// </summary>
internal sealed class Phase2RealtimeFrameSelector(int bufferCapacity = 12)
{
    /// <summary>
    /// 相似关键帧去重阈值：与最近选中帧的差异低于该比例即视为重复
    /// （快速切换/过场动画时同一画面会被反复截获），只保留一个。
    /// </summary>
    private const double DuplicateFrameThreshold = 0.05;
    private Phase2FrameSignature? _lastSelectedSignature;

    private static readonly TimeSpan RegularAnalysisInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ChangedAnalysisInterval =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PreparationStabilizationInterval =
        TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PreparationStabilizationWindow =
        TimeSpan.FromSeconds(2);
    // 未知页面（过场/主界面/快速切换）触发关键帧的独立限流：
    // 这类切换太频繁，若与正常边界共用 1 秒限流会刷爆关键帧队列。
    private static readonly TimeSpan UnknownBoundaryInterval =
        TimeSpan.FromSeconds(3);
    private readonly Phase2RealtimeFrameBuffer frameBuffer =
        new(bufferCapacity);
    private DateTimeOffset lastQueuedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastBoundaryQueuedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastUnknownBoundaryQueuedAt = DateTimeOffset.MinValue;
    private string? lastFastPageId;
    private Phase2PageFamily lastFastPageFamily = Phase2PageFamily.Unknown;
    private DateTimeOffset preparationStabilizationUntil = DateTimeOffset.MinValue;
    private string? retainedFastPageId;
    private readonly Queue<Phase2BufferedFrame> retainedFastPageFrames = new(3);

    public Phase2FrameSelection Observe(
        CaptureFrame frame,
        bool wasReliable,
        Phase2PageFamily lastKnownPage,
        Phase2FastPageObservation fastPage = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var buffered = frameBuffer.Add(frame, wasReliable);
        var now = frame.CapturedAt;
        var fastPageKey = BoundaryKey(fastPage.PageId);
        var previousFastPageFamily = lastFastPageFamily;
        var fastPageChanged = fastPage.IsMatched &&
                              !string.Equals(
                                  fastPageKey,
                                  lastFastPageId,
                                  StringComparison.Ordinal);
        var retainedBoundaryPredecessors = fastPageChanged &&
                                           lastFastPageId is not null &&
                                           string.Equals(
                                               retainedFastPageId,
                                               lastFastPageId,
                                               StringComparison.Ordinal)
            ? retainedFastPageFrames.TakeLast(2).ToArray()
            : [];
        if (fastPage.IsMatched)
        {
            lastFastPageId = fastPageKey;
            lastFastPageFamily = fastPage.PageFamily;
            if (fastPageChanged)
            {
                preparationStabilizationUntil =
                    string.Equals(fastPageKey, "preparation", StringComparison.Ordinal)
                        ? now + PreparationStabilizationWindow
                        : DateTimeOffset.MinValue;
            }
        }
        // 过场动画帧（场景转换、未知页面）不再作为关键帧识别：
        // 它们大部分是战斗入场/位面切换动画，识别只会得到 Unknown 并阻塞
        // 关键帧序列。真正的关键帧只来自 fast 页面变化（备战/战斗/结算页）。
        var critical = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged,
            now,
            ref lastBoundaryQueuedAt,
            force: fastPageChanged &&
                   (fastPage.PageFamily == Phase2PageFamily.BattleSettlement ||
                    previousFastPageFamily == Phase2PageFamily.BattleSettlement ||
                    previousFastPageFamily == Phase2PageFamily.Battle &&
                    fastPage.PageFamily == Phase2PageFamily.Preparation));
        // 未知页面（过场动画等）不再强制识别：大量过场帧只会刷爆队列。
        var interval = now <= preparationStabilizationUntil
            ? PreparationStabilizationInterval
            : buffered.ChangeKind == Phase2FrameChangeKind.Unchanged ||
              fastPage.IsMatched
                ? RegularAnalysisInterval
                : ChangedAnalysisInterval;
        var selected = new List<Phase2SelectedFrame>(4);
        // 开源节流：页面未变化时与最近选中帧高度相似的帧直接丢弃
        // （快速切换/过场动画时同一画面会被反复截获），只保留一个。
        // 页面真正变化（fastPageChanged）的关键帧不做去重——那是新页面边界。
        if (!critical && !fastPageChanged &&
            now - lastQueuedAt < interval &&
            _lastSelectedSignature is { } lastSignature &&
            buffered.Signature.DifferenceRatio(lastSignature) < DuplicateFrameThreshold)
        {
            return new Phase2FrameSelection(buffered, []);
        }

        if (critical)
        {
            // Include the raw immediate predecessors as well as page-matched
            // frames. A one-frame yellow settlement can be captured even when
            // its fast classifier misses; the following preparation boundary
            // must still send that frame to full OCR.
            selected.AddRange(retainedBoundaryPredecessors
                .Concat(frameBuffer.LockLatestPredecessors(2))
                .Where(candidate => candidate.Sequence < buffered.Sequence)
                .GroupBy(candidate => candidate.Sequence)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Sequence)
                .TakeLast(3)
                .Select(candidate => new Phase2SelectedFrame(candidate, true)));
        }

        if (critical || now - lastQueuedAt >= interval)
        {
            selected.Add(new Phase2SelectedFrame(buffered, critical));
            _lastSelectedSignature = buffered.Signature;
            lastQueuedAt = now;
        }

        if (fastPage.IsMatched &&
            (retainedFastPageFrames.Count == 0 ||
             !string.Equals(
                 retainedFastPageId,
                 fastPageKey,
                 StringComparison.Ordinal) ||
             buffered.ChangeKind != Phase2FrameChangeKind.SceneTransition))
        {
            if (!string.Equals(
                    retainedFastPageId,
                    fastPageKey,
                    StringComparison.Ordinal))
            {
                retainedFastPageFrames.Clear();
            }

            retainedFastPageId = fastPageKey;
            if (retainedFastPageFrames.Count == 0 ||
                retainedFastPageFrames.Last().Sequence != buffered.Sequence)
            {
                retainedFastPageFrames.Enqueue(buffered);
                while (retainedFastPageFrames.Count > 3)
                {
                    retainedFastPageFrames.Dequeue();
                }
            }
        }

        return new Phase2FrameSelection(buffered, selected);
    }

    private static string? BoundaryKey(string? pageId) => pageId switch
    {
        "preparation_1_1" or "preparation_1_2" or "preparation_generic" =>
            "preparation",
        "reward_battle" or "reward_battle_pause" or "battle_generic" =>
            "battle",
        _ => pageId
    };
}

internal sealed class Phase2BoundedRecognitionQueue(
    int capacity = 6,
    int maximumCriticalCapacity = 20)
{
    private readonly object gate = new();
    private readonly LinkedList<Phase2RecognitionWorkItem> items = [];
    private readonly SemaphoreSlim available = new(0);

    public bool Enqueue(Phase2RecognitionWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            var existingRegular = items.First;
            while (existingRegular is not null &&
                   existingRegular.Value.IsCritical)
            {
                existingRegular = existingRegular.Next;
            }

            if (!item.IsCritical && existingRegular is not null)
            {
                items.Remove(existingRegular);
                items.AddLast(item);
                return true;
            }

            if (items.Count >= capacity)
            {
                if (!item.IsCritical)
                {
                    return false;
                }

                if (existingRegular is not null)
                {
                    items.Remove(existingRegular);
                    items.AddLast(item);
                    return true;
                }

                if (items.Count >= maximumCriticalCapacity)
                {
                    // 队列拥塞时保留最新关键帧：快速切换（如主动结算/保存并退出）
                    // 的终局、结算关键帧位于队列尾部，丢弃最旧关键帧腾出空间，
                    // 避免结束页帧因拥塞被直接丢弃导致对局结束识别不到。
                    items.RemoveFirst();
                    items.AddLast(item);
                    available.Release();
                    return true;
                }
            }

            items.AddLast(item);
            available.Release();
            return true;
        }
    }

    public async ValueTask<Phase2RecognitionWorkItem> DequeueAsync(
        CancellationToken cancellationToken)
    {
        await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            var first = items.First ??
                throw new InvalidOperationException(
                    "Recognition queue semaphore was signaled without an item.");
            items.RemoveFirst();
            return first.Value;
        }
    }
}

internal sealed class Phase2RealtimeRecognitionPipeline(
    IGameWindowService windowService,
    IGameCapture capture,
    ISituationScreenshotAnalyzer analyzer,
    IPhase2FastPageClassifier? fastPageClassifier = null)
{
    private static readonly TimeSpan CaptureInterval =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RegularAnalysisInterval =
        TimeSpan.FromSeconds(2);
    private readonly Phase2RealtimeFrameSelector frameSelector = new();

    public IAsyncEnumerable<Phase2RealtimePipelineUpdate> RunAsync(
        nint gameWindowHandle,
        AdvisorSelection selection,
        string runId,
        CancellationToken cancellationToken) =>
        RunAsync(
            gameWindowHandle,
            selection,
            () => runId,
            cancellationToken);

    public async IAsyncEnumerable<Phase2RealtimePipelineUpdate> RunAsync(
        nint gameWindowHandle,
        AdvisorSelection selection,
        Func<string> runIdProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var workQueue = new Phase2BoundedRecognitionQueue();
        var output = Channel.CreateBounded<Phase2RealtimePipelineUpdate>(
            new BoundedChannelOptions(16)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        var shared = new SharedPipelineState();
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceFramesAsync(
            gameWindowHandle,
            runIdProvider,
            workQueue,
            output.Writer,
            shared,
            linkedCancellation.Token);
        var consumer = RecognizeFramesAsync(
            selection,
            workQueue,
            output.Writer,
            shared,
            linkedCancellation.Token);
        _ = CompleteOutputAsync(producer, consumer, output.Writer);

        await foreach (var update in output.Reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return update;
        }

        linkedCancellation.Cancel();
    }

    private async Task ProduceFramesAsync(
        nint gameWindowHandle,
        Func<string> runIdProvider,
        Phase2BoundedRecognitionQueue workQueue,
        ChannelWriter<Phase2RealtimePipelineUpdate> output,
        SharedPipelineState shared,
        CancellationToken cancellationToken)
    {
        var lastHeartbeatAt = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            var iterationStartedAt = Stopwatch.GetTimestamp();
            try
            {
                var window = windowService.Refresh(gameWindowHandle) ??
                    throw new InvalidOperationException(
                        "游戏窗口已关闭、最小化或不可捕获。");
                var frame = await capture.CaptureAsync(window, cancellationToken)
                    .ConfigureAwait(false);
                var wasReliable = Volatile.Read(ref shared.LastPageReliable) == 1;
                var now = frame.CapturedAt;
                var lastKnownPage = (Phase2PageFamily)Volatile.Read(
                    ref shared.LastKnownPage);
                var fastPage = ClassifyFastPage(frame);
                var selection = frameSelector.Observe(
                    frame,
                    wasReliable,
                    lastKnownPage,
                    fastPage);
                foreach (var selected in selection.FramesToRecognize)
                {
                    var queued = Enqueue(
                        workQueue,
                        selected.BufferedFrame,
                        runIdProvider(),
                        selected.IsCritical);
                    if (!queued && selected.IsCritical)
                    {
                        var screenshotName = ScreenshotName(selected.BufferedFrame);
                        await output.WriteAsync(
                            new Phase2RealtimePipelineUpdate(
                                selected.BufferedFrame.Frame,
                                screenshotName,
                                null,
                                IsHeartbeat: false,
                                IsRevalidated: false,
                                IsCritical: true,
                                TimeSpan.Zero,
                                "关键页面识别队列已满；该帧未进入 OCR，已交给收集器保存失败证据。"),
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                if (now - lastHeartbeatAt >= RegularAnalysisInterval)
                {
                    var completed = Volatile.Read(ref shared.LastCompleted);
                    if (completed is not null)
                    {
                        var difference = selection.Current.Signature.DifferenceRatio(
                            completed.Signature);
                        await output.WriteAsync(
                            new Phase2RealtimePipelineUpdate(
                                frame,
                                null,
                                completed.Analysis,
                                IsHeartbeat: true,
                                IsRevalidated: difference < 0.035,
                                IsCritical: false,
                                AnalysisAge: now - completed.CompletedAt),
                            cancellationToken).ConfigureAwait(false);
                    }

                    lastHeartbeatAt = now;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await output.WriteAsync(
                    new Phase2RealtimePipelineUpdate(
                        null,
                        null,
                        null,
                        IsHeartbeat: false,
                        IsRevalidated: false,
                        IsCritical: false,
                        TimeSpan.Zero,
                        exception.Message),
                    cancellationToken).ConfigureAwait(false);
            }

            var remainingDelay = CaptureInterval -
                                 Stopwatch.GetElapsedTime(iterationStartedAt);
            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private Phase2FastPageObservation ClassifyFastPage(CaptureFrame frame)
    {
        if (fastPageClassifier is null)
        {
            return Phase2FastPageObservation.None;
        }

        try
        {
            return fastPageClassifier.Classify(frame);
        }
        catch
        {
            // Fast page evidence is only a scheduling hint. A transient
            // template failure must never stop capture or replace the full
            // bounded recognizer.
            return Phase2FastPageObservation.None;
        }
    }

    private async Task RecognizeFramesAsync(
        AdvisorSelection selection,
        Phase2BoundedRecognitionQueue workQueue,
        ChannelWriter<Phase2RealtimePipelineUpdate> output,
        SharedPipelineState shared,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var item = await workQueue.DequeueAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var analysis = await analyzer.AnalyzeAsync(
                    item.BufferedFrame.Frame,
                    item.EvidenceSourceId,
                    selection,
                    cancellationToken,
                    runId: item.RunId)
                    .ConfigureAwait(false);
                analysis = Phase2TransitionFramePolicy.MarkIfApplicable(
                    analysis,
                    item.BufferedFrame);
                analysis = Phase2RecognitionTraceBuilder.Attach(analysis);
                var completed = new CompletedAnalysis(
                    analysis,
                    item.BufferedFrame.Signature,
                    DateTimeOffset.UtcNow);
                Volatile.Write(ref shared.LastCompleted, completed);
                Volatile.Write(
                    ref shared.LastPageReliable,
                    Phase2PageRecognition.IsKnown(analysis)
                        ? 1
                        : 0);
                if (analysis.OperationalState?.PageFamily is { } page &&
                    page is not Phase2PageFamily.Unknown and
                        not Phase2PageFamily.Transition)
                {
                    Volatile.Write(ref shared.LastKnownPage, (int)page);
                }
                await output.WriteAsync(
                    new Phase2RealtimePipelineUpdate(
                        item.BufferedFrame.Frame,
                        item.ScreenshotName,
                        analysis,
                        IsHeartbeat: false,
                        IsRevalidated: true,
                        IsCritical: item.IsCritical,
                        DateTimeOffset.UtcNow - item.BufferedFrame.Frame.CapturedAt),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await output.WriteAsync(
                    new Phase2RealtimePipelineUpdate(
                        item.BufferedFrame.Frame,
                        item.ScreenshotName,
                        null,
                        IsHeartbeat: false,
                        IsRevalidated: false,
                        IsCritical: item.IsCritical,
                        TimeSpan.Zero,
                        exception.Message),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool Enqueue(
        Phase2BoundedRecognitionQueue queue,
        Phase2BufferedFrame buffered,
        string runId,
        bool isCritical)
    {
        var screenshotName = ScreenshotName(buffered);
        return queue.Enqueue(new Phase2RecognitionWorkItem(
            buffered,
            screenshotName,
            $"run:{runId}/screenshots/{screenshotName}",
            runId,
            isCritical));
    }

    private static string ScreenshotName(Phase2BufferedFrame buffered) =>
        $"{buffered.Frame.CapturedAt:yyyyMMdd-HHmmssfff}.png";

    private static async Task CompleteOutputAsync(
        Task producer,
        Task consumer,
        ChannelWriter<Phase2RealtimePipelineUpdate> output)
    {
        try
        {
            await Task.WhenAll(producer, consumer).ConfigureAwait(false);
            output.TryComplete();
        }
        catch (OperationCanceledException)
        {
            output.TryComplete();
        }
        catch (Exception exception)
        {
            output.TryComplete(exception);
        }
    }

    private sealed class SharedPipelineState
    {
        public int LastPageReliable;
        public int LastKnownPage;
        public CompletedAnalysis? LastCompleted;
    }

    private sealed record CompletedAnalysis(
        ScreenshotAnalysisResult Analysis,
        Phase2FrameSignature Signature,
        DateTimeOffset CompletedAt);
}
