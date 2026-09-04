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
    bool IsCritical,
    bool IsIncremental = false,
    Phase2IncrementalSelection? IncrementalSelection = null);

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
    private const double GenericPreparationMinimum = 0.35;
    private const double DegradedGenericPreparationMinimum = 0.30;
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
        // 备战页不再按节点做模板（用户 2026-08-04 方案）：preparation_generic
        // 的"备战阶段"通用标题单独即可判定备战页状态，节点数字由 OCR 识别。
        // 旧逻辑要求 generic + specific(1_1/1_2) 双锚点，删除模板后失效。
        var hasPreparationMarker =
            generic is not null &&
            generic.Confidence >= GenericPreparationMinimum;
        if (battleDamageTabs is not null &&
            battleDamageTabs.Confidence >= battleDamageTabs.Threshold &&
            (battleDamageTabs.Confidence >= StrongBattleDamageTabsMinimum ||
             battlePause is not null &&
             battlePause.Confidence >= DegradedBattlePauseMinimum ||
             battleDamageTabs.Confidence >= UnambiguousBattleDamageTabsMinimum &&
             !hasPreparationMarker &&
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

        if (!hasPreparationMarker)
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

        // 空白备战席会削弱通用边缘模板（generic 分数偏低，如 0.377）：
        // 只要"备战阶段"标题模板达到保守下限，且互斥的战斗伤害页签明确
        // 缺失，即判定备战页。这是二阶段证据融合，不降低共享自动化阈值。
        var genericConfidence = generic!.Confidence;
        var battleAbsent =
            battleDamageTabs is null ||
            battleDamageTabs.Confidence <= BattleExclusionMaximum;
        if (!(genericConfidence >= GenericPreparationMinimum && battleAbsent) &&
            !(genericConfidence >= DegradedGenericPreparationMinimum &&
              battleAbsent))
        {
            return null;
        }

        var confidence = Math.Clamp(genericConfidence, 0.55, 0.80);
        return new Phase2PageDiagnosticInference(
            "preparation_generic",
            Phase2PageFamily.Preparation,
            confidence,
            [generic]);
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
            "preparation_generic",
            "reward_shop",
            "reward_battle",
            "reward_battle_pause",
            "battle_generic",
            "incomplete_lineup_prompt",
            "disconnect_prompt",
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
    bool IsCritical,
    bool IsIncremental = false);

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
    // 2026-08-18 增量识别：静止画面（ChangeKind==Unchanged）的增量帧间隔。
    // 画面静止时只补上次失败字段，节省全量成本；1.5s 增量 + 2s 全量兜底。
    private static readonly TimeSpan IncrementalAnalysisInterval =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PreparationStabilizationInterval =
        TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PreparationStabilizationWindow =
        TimeSpan.FromSeconds(2);
    // 未知页面（过场/主界面/快速切换）触发关键帧的独立限流：
    // 这类切换太频繁，若与正常边界共用 1 秒限流会刷爆关键帧队列。
    private static readonly TimeSpan UnknownBoundaryInterval =
        TimeSpan.FromSeconds(3);
    // 黑屏过渡检测：游戏开战/加载时全黑（实测亮度 3~16）；
    // 低于此值视为黑屏中，恢复到该值以上视为黑屏结束。
    private const double BlackScreenLuminanceThreshold = 20;
    private const double BlackScreenRecoveryThreshold = 40;
    // 亮度采样步长：每 N 像素取 1 个，2560×1440 约 5.7 万采样，足够判定黑屏。
    private const int LuminanceSampleStride = 64;
    private readonly Phase2RealtimeFrameBuffer frameBuffer =
        new(bufferCapacity);
    private DateTimeOffset lastQueuedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastBoundaryQueuedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastUnknownBoundaryQueuedAt = DateTimeOffset.MinValue;
    // 2026-08-18 增量识别时钟：全量帧用于 2s 全量兜底判定；增量帧用于
    // 静止画面 1.5s 一次的失败字段补充。全量命中时同时重置两者，避免
    // 全量刚识别完所有字段后 1500ms 内重复触发增量（纯浪费）。
    private DateTimeOffset lastFullAnalysisAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastIncrementalAnalysisAt = DateTimeOffset.MinValue;
    private string? lastFastPageId;
    private Phase2PageFamily lastFastPageFamily = Phase2PageFamily.Unknown;
    private DateTimeOffset preparationStabilizationUntil = DateTimeOffset.MinValue;
    private string? retainedFastPageId;
    private readonly Queue<Phase2BufferedFrame> retainedFastPageFrames = new(3);
    private double? _previousFrameLuminance;

    public Phase2FrameSelection Observe(
        CaptureFrame frame,
        bool wasReliable,
        Phase2PageFamily lastKnownPage,
        Phase2FastPageObservation fastPage = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var buffered = frameBuffer.Add(frame, wasReliable);
        var now = frame.CapturedAt;
        // 游戏开战/加载时会有全黑过渡帧（实测 1-1 约 2 秒、1-3 约 1 秒，亮度 3~16）。
        // 黑屏期间 fast 分类器无页面可判 → 关键帧断裂 → "战斗开始"延迟。
        // 检测"亮度从极低恢复"作为黑屏结束事件，强制触发关键帧，
        // 让战斗页加载完成的瞬间立即进入完整识别。
        var luminance = SampleFrameLuminance(frame);
        var blackScreenRecovery =
            _previousFrameLuminance is { } previous &&
            previous < BlackScreenLuminanceThreshold &&
            luminance >= BlackScreenRecoveryThreshold;
        _previousFrameLuminance = luminance;
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
        // 例外：黑屏结束恢复（游戏开战加载动画结束）强制触发关键帧——
        // 否则战斗页加载完但 fast 尚未匹配 Battle 时，关键帧会持续断裂。
        var critical = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged || blackScreenRecovery,
            now,
            ref lastBoundaryQueuedAt,
            force: blackScreenRecovery ||
                   fastPageChanged &&
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
        // 2026-08-18 增量识别（参照版语义）：全量/增量到期判定提前到早退
        // 分支之前——静止帧（ChangeKind==Unchanged 阈值 0.035）必然满足相似
        // 度 0.05，若早退不豁免会拦截所有增量帧（else-if 死代码）并推迟 2s
        // 全量。fullDue = 距上次全量满 2s 强制全量；incrementalDue = 静止帧
        // 且距上次全量<2s 且距上次增量≥1.5s → 只补失败字段的增量帧。
        var fullDue = now - lastFullAnalysisAt >= RegularAnalysisInterval;
        var incrementalDue = !fullDue &&
                             buffered.ChangeKind ==
                                 Phase2FrameChangeKind.Unchanged &&
                             now - lastIncrementalAnalysisAt >=
                             IncrementalAnalysisInterval;
        // 开源节流：页面未变化时与最近选中帧高度相似的帧直接丢弃
        // （快速切换/过场动画时同一画面会被反复截获），只保留一个。
        // 页面真正变化（fastPageChanged）的关键帧不做去重——那是新页面边界。
        // 全量到期/增量到期的帧必须放行（去重不得推迟 2s 全量兜底或饿死增量）。
        // 2026-08-19「F」修复：战斗/结算帧【不做相似去重】。实测 run-20260818
        // 中被丢的 32 关键帧多数 fast 判 battle_generic(战斗持续，fastPageChanged=false)
        // 或 __classifier-miss__，画面只差伤害数字/结算面板（相似度>0.95）被去重丢弃
        // → 结算伤害读不到、节点记录缺失、滞后 20s+。战斗/结算帧画面即使相似也可能
        // 携带终局/结算信息，必须保留（由下方 interval 控制频率，不刷爆队列）。
        var isBattleOrSettlement = fastPage.IsMatched &&
            (fastPage.PageFamily == Phase2PageFamily.Battle ||
             fastPage.PageFamily == Phase2PageFamily.BattleSettlement);
        // miss(未识别)帧：#~2/3 是真 unknown（过场/加载动画），但结算/战斗结束
        // 帧也常见 miss（083348 胜利结算、083808 等）。场景切换帧(ChangeKind==
        // SceneTransition，差异≥0.20)代表画面大幅变化，可能携带结算/终局信息，
        // 同样不做相似去重——真正的静止/区域性图帧仍走去重节流。
        var isSceneTransition = buffered.ChangeKind ==
            Phase2FrameChangeKind.SceneTransition;
        if (!critical && !fastPageChanged && !fullDue && !incrementalDue &&
            !isBattleOrSettlement && !isSceneTransition &&
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

        // 全量帧：关键帧 || 距上次全量满 2s || 常规分析间隔到。
        // 命中时重置全量/增量时钟：全量刚识别完所有字段，1.5s 内不重复增量。
        if (critical || fullDue || now - lastQueuedAt >= interval)
        {
            selected.Add(new Phase2SelectedFrame(buffered, critical));
            _lastSelectedSignature = buffered.Signature;
            lastFullAnalysisAt = now;
            lastIncrementalAnalysisAt = now;
            lastQueuedAt = now;
        }
        else if (incrementalDue && !fastPageChanged)
        {
            // 增量帧：静止画面、2s 全量未到期、距上次增量≥1.5s。
            // 只补上次失败字段；页面变化帧 fastPageChanged 走全量捕捉变化。
            selected.Add(new Phase2SelectedFrame(
                buffered,
                false,
                IsIncremental: true));
            _lastSelectedSignature = buffered.Signature;
            lastIncrementalAnalysisAt = now;
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

    /// <summary>
    /// 采样帧平均亮度（BGRA，采样步长 LuminanceSampleStride）。
    /// 用于检测游戏开战/加载时的黑屏过渡帧及黑屏结束恢复。
    /// </summary>
    private static double SampleFrameLuminance(CaptureFrame frame)
    {
        var pixels = frame.BgraPixels;
        var stride = frame.Stride;
        var width = frame.Width;
        var height = frame.Height;
        if (pixels.Length == 0 || width <= 0 || height <= 0 || stride <= 0)
        {
            return 0;
        }

        long sum = 0;
        var count = 0;
        for (var y = 0; y < height; y += LuminanceSampleStride)
        {
            var rowBase = y * stride;
            for (var x = 0; x < width; x += LuminanceSampleStride)
            {
                var offset = rowBase + x * 4;
                if (offset + 2 >= pixels.Length)
                {
                    continue;
                }

                sum += pixels[offset] +
                       pixels[offset + 1] +
                       pixels[offset + 2];
                count++;
            }
        }

        return count == 0 ? 0 : sum / (double)count / 3d;
    }

    private static string? BoundaryKey(string? pageId) => pageId switch
    {
        // 所有备战页（preparation_generic 及兼容的 preparation_1_x）归为
        // 同一个"备战"边界键：页面状态相同，节点数字变化不是页面切换。
        string value when value.StartsWith(
            "preparation_",
            StringComparison.OrdinalIgnoreCase) =>
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
                    // 注意：RemoveFirst 后 AddLast，items 数量不变，不能再
                    // Release——否则 available 计数比 items 多 1，队列清空后
                    // DequeueAsync 在 items 空时抛异常（识别管线终止）。
                    items.RemoveFirst();
                    items.AddLast(item);
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

    /// <summary>
    /// 关键事件通道出队：关键帧（页面边界/结算页/终局）优先于普通帧，
    /// 即使普通帧先入队。单帧分析约 10s 时，FIFO 会让 3-7 评级页关键帧
    /// 排在普通帧后面数分钟，页面早已过去 → 整局结束识别无输入
    /// （用户实机反馈 3-7 未检测到结算画面）。无关键帧时保持 FIFO。
    /// 信号量语义与 DequeueAsync 一致：每次 Wait 消费一个入队信号。
    /// </summary>
    public async ValueTask<Phase2RecognitionWorkItem>
        DequeuePreferCriticalAsync(CancellationToken cancellationToken)
    {
        await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            var first = items.First ??
                throw new InvalidOperationException(
                    "Recognition queue semaphore was signaled without an item.");
            var node = first;
            var cursor = first;
            while (cursor is not null)
            {
                if (cursor.Value.IsCritical)
                {
                    node = cursor;
                    break;
                }

                cursor = cursor.Next;
            }

            items.Remove(node);
            return node.Value;
        }
    }

    /// <summary>
    /// 丢弃积压的旧帧，只保留最新一帧（跳帧，不积累识别延迟）。
    /// 识别一帧耗时超过固定间隔（用户 2026-08-06 要求：识别 >1.5 秒就自动
    /// 跳几帧）时调用——识别期间新入队的帧画面已过期，逐帧处理只会让
    /// 登记越来越滞后；保留最新帧让下一轮识别追上实时画面。
    /// 关键帧（页面边界/终局/结算）不能被跳帧丢弃：若队列里有关键帧，
    /// 保留"最新关键帧 + 最新帧"（最多 2 帧），否则 tracker 漏节点/漏对局结束。
    /// </summary>
    public void DropStaleFrames()
    {
        lock (gate)
        {
            if (items.Count <= 2)
            {
                return;
            }

            // 关键事件通道：关键帧（页面边界/结算/终局）必须全部保留
            // ——关键帧优先出队（DequeuePreferCriticalAsync）下，结算页
            // 关键帧排队期间不能被"只保留最新关键前驱"丢弃，否则 3-7
            // 评级页帧会在识别前被清掉（review 2026-08-15 抓到）。
            // 只丢弃过期普通帧（保留最新普通帧）；关键帧数量由
            // maximumCriticalCapacity(20) 约束。
            // 保留"全部关键帧 + 最新普通帧"（普通帧至多一个）：
            // 最新普通帧代表当前状态通道的最新画面，不得删除。
            var latestRegular = items.Last;
            while (latestRegular is not null &&
                   latestRegular.Value.IsCritical)
            {
                latestRegular = latestRegular.Previous;
            }

            var current = items.First;
            while (current is not null)
            {
                var next = current.Next;
                if (!current.Value.IsCritical &&
                    !ReferenceEquals(current, latestRegular))
                {
                    items.Remove(current);
                    // 被丢弃的帧对应的可用信号量计数同步释放。
                    _ = available.Wait(0);
                }

                current = next;
            }
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
    /// <summary>识别一帧超过该时长视为"滞后"：识别完成后丢弃积压旧帧、
    /// 只保留最新（跳帧，不积累登记延迟）。与画面变化帧间隔（约 1.5s）对齐。</summary>
    private static readonly TimeSpan FrameSkipThreshold =
        TimeSpan.FromSeconds(1.5);
    private readonly Phase2RealtimeFrameSelector frameSelector = new();
    private readonly object subscriberGate = new();
    private readonly List<Action<Phase2RealtimePipelineUpdate>> subscribers = [];
    private readonly object frameSubscriberGate = new();
    private readonly List<Action<CaptureFrame>> frameSubscribers = [];

    /// <summary>
    /// 订阅统一识别流：pipeline 的每个输出（含 heartbeat/关键帧/错误）都会
    /// 同步广播给订阅者。记录员通过 channel 消费（现有路径），刷开局等
    /// 其他消费者通过本订阅共享同一个"摄像机+识别器"的识别结果
    /// （用户架构：识别器独立、数据分发）。
    /// </summary>
    public IDisposable Subscribe(Action<Phase2RealtimePipelineUpdate> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (subscriberGate)
        {
            subscribers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    /// <summary>
    /// 订阅原始帧流：pipeline 每次截图（约 100ms 一帧）都会同步广播给订阅者。
    /// 刷开局等需要对帧做专用识别的消费者（自动战斗图标/商店槽位 OCR）
    /// 通过本订阅共享同一个"摄像机"的帧（不再自己 CaptureAsync）。
    /// </summary>
    public IDisposable SubscribeFrames(Action<CaptureFrame> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (frameSubscriberGate)
        {
            frameSubscribers.Add(observer);
        }

        return new FrameSubscription(this, observer);
    }

    private void Broadcast(Phase2RealtimePipelineUpdate update)
    {
        Action<Phase2RealtimePipelineUpdate>[]? snapshot = null;
        lock (subscriberGate)
        {
            if (subscribers.Count > 0)
            {
                snapshot = subscribers.ToArray();
            }
        }

        if (snapshot is not null)
        {
            foreach (var observer in snapshot)
            {
                try
                {
                    observer(update);
                }
                catch (Exception)
                {
                    // 订阅者异常不得中断识别主循环（记录员/刷开局各负其责）。
                }
            }
        }
    }

    private void BroadcastFrame(CaptureFrame frame)
    {
        Action<CaptureFrame>[]? snapshot = null;
        lock (frameSubscriberGate)
        {
            if (frameSubscribers.Count > 0)
            {
                snapshot = frameSubscribers.ToArray();
            }
        }

        if (snapshot is not null)
        {
            foreach (var observer in snapshot)
            {
                try
                {
                    observer(frame);
                }
                catch (Exception)
                {
                    // 帧订阅者异常不得中断截图主循环。
                }
            }
        }
    }

    private sealed class Subscription(
        Phase2RealtimeRecognitionPipeline owner,
        Action<Phase2RealtimePipelineUpdate> observer) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (owner.subscriberGate)
            {
                owner.subscribers.Remove(observer);
            }
        }
    }

    private sealed class FrameSubscription(
        Phase2RealtimeRecognitionPipeline owner,
        Action<CaptureFrame> observer) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (owner.frameSubscriberGate)
            {
                owner.frameSubscribers.Remove(observer);
            }
        }
    }

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
            Broadcast(update);
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
                BroadcastFrame(frame);
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
                    // 增量帧在入队时捕获失败字段识别集（基于当时的 shared
                    // LastCompleted）：worker 出队时 LastCompleted 可能已被
                    // 后入队的关键帧更新（DequeuePreferCriticalAsync 关键帧
                    // 优先），若现场重算会用新页面的 KnownPageId 分析旧画面，
                    // 导致错区域识别。入队时捕获保证分析与画面所属页面一致。
                    var queued = Enqueue(
                        workQueue,
                        selected.BufferedFrame,
                        runIdProvider(),
                        selected.IsCritical,
                        selected.IsIncremental,
                        selected.IsIncremental
                            ? BuildIncrementalSelection(
                                Volatile.Read(ref shared.LastCompleted))
                            : null);
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
            var item = await workQueue.DequeuePreferCriticalAsync(cancellationToken)
                .ConfigureAwait(false);
            var recognitionStartedAt = Stopwatch.GetTimestamp();
            try
            {
                // 2026-08-18 增量识别（参照版语义简化移植）：增量帧只补上次
                // 失败字段（成功字段跳过，画面静止值不变绝对安全）；全量帧
                // incremental=null 全部识别。增量识别集在入队时已基于当时的
                // shared LastCompleted 捕获（见 producer Enqueue）——出队时
                // LastCompleted 可能已被后入队的关键帧更新，现场重算会用新
                // 页面的 KnownPageId 分析旧画面。因此增量帧只使用 item 携带
                // 的 selection；为 null（入队时页面未知/无失败字段）直接跳过，
                // 由 2s 全量兜底负责页面探测，避免 unknown 页增量帧空跑。
                var incremental = item.IncrementalSelection;
                if (item.IsIncremental && incremental is null)
                {
                    continue;
                }

                var analysis = await analyzer.AnalyzeAsync(
                    item.BufferedFrame.Frame,
                    item.EvidenceSourceId,
                    selection,
                    cancellationToken,
                    runId: item.RunId,
                    incremental: incremental)
                    .ConfigureAwait(false);
                analysis = Phase2TransitionFramePolicy.MarkIfApplicable(
                    analysis,
                    item.BufferedFrame);
                analysis = Phase2RecognitionTraceBuilder.Attach(analysis);
                var completed = new CompletedAnalysis(
                    analysis,
                    item.BufferedFrame.Signature,
                    DateTimeOffset.UtcNow);
                // 降频 + 状态权威源：只有全量帧更新 shared 状态（LastCompleted/
                // LastPageReliable/LastKnownPage）。增量帧只补失败字段，其跳过
                // 字段是 Unknown——若覆盖 LastCompleted 会把已 Known 的成功字段
                // 劣化为 Unknown（导致下一增量帧重复识别+心跳下发瞬态未知），
                // 违背"静止画面只补失败字段"的初衷。增量帧结果通过下方
                // output 广播给下游（记录员/收集器），不依赖 shared 状态；
                // 最近一次全量始终是权威基线，供心跳与增量选择使用。
                if (!item.IsIncremental)
                {
                    if (completed.Analysis.OperationalState is { } op)
                    {
                        UpdateFieldFailStreaks(op);
                    }

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
                // 跳帧：识别一帧耗时超过固定间隔（画面变化帧间隔约 1.5s，
                // 用户 2026-08-06 要求）时，识别期间新入队的旧帧画面已过期，
                // 丢弃它们只保留最新，避免登记延迟无限积累。
                if (Stopwatch.GetElapsedTime(recognitionStartedAt) > FrameSkipThreshold)
                {
                    workQueue.DropStaleFrames();
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
        bool isCritical,
        bool isIncremental = false,
        Phase2IncrementalSelection? incrementalSelection = null)
    {
        var screenshotName = ScreenshotName(buffered);
        return queue.Enqueue(new Phase2RecognitionWorkItem(
            buffered,
            screenshotName,
            $"run:{runId}/screenshots/{screenshotName}",
            runId,
            isCritical,
            isIncremental,
            incrementalSelection));
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

    // 2026-08-18 增量识别字段降频（用户拍板语义）：连续 3 次全量失败 →
    // 该字段在增量帧上不再重试（等 2s 全量；全量时仍尝试），成功即清零
    // 2026-08-18: producer(Enqueue)读 + worker(全量帧)写 两个线程触碰同一 Dictionary，
    // 并发导致枚举中修改/撕裂计数(review should-fix)。加锁保护读写。
    private readonly object _fieldFailStreakLock = new();
    private readonly Dictionary<string, int> _fieldFailStreaks = new();
    private const int FieldFailStreakThreshold = 3;

    private Phase2IncrementalSelection? BuildIncrementalSelection(
        CompletedAnalysis? lastCompleted)
    {
        var operational = lastCompleted?.Analysis.OperationalState;
        if (operational is null)
        {
            return null;
        }

        // 上次失败的字段（Unknown/低置信）→ 本次增量帧重新识别；
        // 上次已成功字段 → 跳过（画面静止值不变，绝对安全）。
        var recognize = new HashSet<string>(StringComparer.Ordinal);
        if (operational.Health.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Health);
        }

        if (operational.StoreLevel.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.StoreLevel);
        }

        if (operational.Population.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Population);
        }

        var formationRetrySlots =
            Phase2FormationRetrySelector.SelectRetrySlotKeys(operational);
        if (operational.Formation.Status != ObservationStatus.Known ||
            formationRetrySlots.Count > 0)
        {
            recognize.Add(Phase2IncrementalFields.Formation);
        }

        if (operational.ActiveSynergies.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Synergy);
        }

        // 2026-08-18 B 提速补强：将这 5 个 OCR 字段纳入"上次失败才重试"。
        // 增量帧（画面静止）值不变 → 成功后跳过；失败 → 重试补全。
        // review 建议：Node 是 StateTracker 页面/战斗边界锚点、消费面广，
        // 跳过导致的 Stale 化可能影响边界判定——为稳妥【不放 Node 进增量跳过】。
        if (operational.EnemyDifficulty.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Difficulty);
        }

        if (operational.Interest.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Interest);
        }

        if (operational.CumulativeSpend.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Spend);
        }

        if (operational.PlayerProgress.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Progress);
        }

        if (operational.DismantleToolCount.Status != ObservationStatus.Known)
        {
            recognize.Add(Phase2IncrementalFields.Tools);
        }

        // 降频：连续失败 ≥3 次的字段本轮增量不再重试（等 2s 全量）。
        // 例外：阵容若有未识别槽位，仍允许增量重试阵容（必须补齐才能显示）。
        foreach (var field in recognize.ToArray())
        {
            int? streak;
            lock (_fieldFailStreakLock)
            {
                streak = _fieldFailStreaks.TryGetValue(field, out var s)
                    ? s
                    : (int?)null;
            }
            if (streak is int st && st >= FieldFailStreakThreshold)
            {
                if (field != Phase2IncrementalFields.Formation ||
                    formationRetrySlots.Count == 0)
                {
                    recognize.Remove(field);
                }
            }
        }

        // 增量帧复用上次识别页面（KnownPageId），situation 分析器据此跳过
        // 全量页面分类（300-530ms）——画面静止页面未变，分类是纯浪费。
        // 页面未知（null/哨兵 __classifier-miss__）时返回 null：unknown 页
        // 增量帧无内容可补，worker 直接跳过，由心跳与 2s 全量兜底探测页面。
        var knownPageId = operational.PageId;
        if (string.IsNullOrWhiteSpace(knownPageId) ||
            string.Equals(
                knownPageId,
                "__classifier-miss__",
                StringComparison.Ordinal))
        {
            return null;
        }

        return new Phase2IncrementalSelection(
            recognize,
            KnownPageId: knownPageId,
            FormationSlotKeys: formationRetrySlots.Count == 0
                ? null
                : formationRetrySlots);
    }

    private void UpdateFieldFailStreaks(Phase2OperationalState operational)
    {
        static bool IsFailed(ObservationStatus status) =>
            status != ObservationStatus.Known;

        var states = new Dictionary<string, ObservationStatus>(
            StringComparer.Ordinal)
        {
            [Phase2IncrementalFields.Health] = operational.Health.Status,
            [Phase2IncrementalFields.StoreLevel] = operational.StoreLevel.Status,
            [Phase2IncrementalFields.Population] = operational.Population.Status,
            [Phase2IncrementalFields.Formation] = operational.Formation.Status,
            [Phase2IncrementalFields.Synergy] =
                operational.ActiveSynergies.Status,
            // 2026-08-18 B 提速补强：5 个 OCR 字段纳入 fail-streak 统计，
            // 与 BuildIncrementalSelection 的失败重试配套（Node 按 review 保守不放）。
            [Phase2IncrementalFields.Difficulty] =
                operational.EnemyDifficulty.Status,
            [Phase2IncrementalFields.Interest] = operational.Interest.Status,
            [Phase2IncrementalFields.Spend] =
                operational.CumulativeSpend.Status,
            [Phase2IncrementalFields.Progress] =
                operational.PlayerProgress.Status,
            [Phase2IncrementalFields.Tools] =
                operational.DismantleToolCount.Status
        };
        foreach (var (field, status) in states)
        {
            lock (_fieldFailStreakLock)
            {
                if (IsFailed(status))
                {
                    _fieldFailStreaks[field] =
                        _fieldFailStreaks.GetValueOrDefault(field) + 1;
                }
                else
                {
                    _fieldFailStreaks.Remove(field);
                }
            }
        }
    }

    private sealed record CompletedAnalysis(
        ScreenshotAnalysisResult Analysis,
        Phase2FrameSignature Signature,
        DateTimeOffset CompletedAt);
}
