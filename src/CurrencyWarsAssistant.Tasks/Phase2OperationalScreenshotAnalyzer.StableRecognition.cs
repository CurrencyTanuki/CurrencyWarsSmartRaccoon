using System.Collections.Concurrent;
using System.Numerics;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed partial class Phase2OperationalScreenshotAnalyzer
{
    private const int MaximumStableRecognitionAttemptsPerVisual = 3;
    private const int SignificantStableRegionHashDistance = 8;
    private const int SignificantNodeRegionHashDistance = 3;
    private static readonly TimeSpan StableRunRetention = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, StableRunRecognitionState>
        _stableRunRecognitions = new(StringComparer.Ordinal);

    public void NotifyPageObserved(string runId, string? pageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var strategySelectionPage = string.Equals(
            pageId,
            "investment_strategy",
            StringComparison.OrdinalIgnoreCase);
        var preparationNode = PreparationNodeFromPageId(pageId);
        if (!strategySelectionPage && preparationNode is null)
        {
            return;
        }

        var run = GetStableRun(runId);
        lock (run.Gate)
        {
            if (preparationNode is not null &&
                string.Equals(
                    preparationNode,
                    run.LastPreparationNodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                run.LastAccessedAt = DateTimeOffset.UtcNow;
                return;
            }

            if (preparationNode is not null)
            {
                run.LastPreparationNodeId = preparationNode;
            }

            run.StrategyRefreshPending = true;
            run.StrategyAttemptsForVisual = 0;
            run.LastStrategyAttemptSignature = null;
            run.LastAccessedAt = DateTimeOffset.UtcNow;
        }
    }

    private void ObservePreparationNode(
        string runId,
        Observation<string> node)
    {
        if (node.Status != ObservationStatus.Known ||
            PreparationNodeFromPageId($"preparation_{node.Value}") is not
                { } preparationNode)
        {
            return;
        }

        var run = GetStableRun(runId);
        lock (run.Gate)
        {
            if (string.Equals(
                    preparationNode,
                    run.LastPreparationNodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                run.LastAccessedAt = DateTimeOffset.UtcNow;
                return;
            }

            run.LastPreparationNodeId = preparationNode;
            run.StrategyRefreshPending = true;
            run.StrategyAttemptsForVisual = 0;
            run.LastStrategyAttemptSignature = null;
            run.LastAccessedAt = DateTimeOffset.UtcNow;
        }
    }

    internal static string? PreparationNodeFromPageId(string? pageId)    {
        const string prefix = "preparation_";
        if (pageId is null || !pageId.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = pageId[prefix.Length..];
        var parts = candidate.Split(['-', '_'], 2);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var plane) &&
               int.TryParse(parts[1], out var node) &&
               // 货币战争节点为 1~3 位面、每面 1~9 节点；
               // OCR 偶尔把"1-2"读成"1-9"/"2-8"，越界值直接拒绝，
               // 避免节点号错乱污染对局归档（如 2-8 挑战失败实为 1-2）。
               plane is >= 1 and <= 3 &&
               node is >= 1 and <= 9
            ? $"{plane}-{node}"
            : null;
    }

    /// <summary>
    /// 节点号解析：页面 ID 含明确节点号（如 preparation_1_2）时优先采信
    /// （分类器给出、更稳定），OCR 结果仅兜底——实测 OCR 会把 1-2 读成
    /// 1-9，污染节点历史与对局归档。
    /// </summary>
    internal static string? NodeFromPageIdOrOcr(string? pageId, string? ocrNode)
    {
        if (PreparationNodeFromPageId(pageId) is { } fromPage)
        {
            return fromPage;
        }

        return PreparationNodeFromPageId($"preparation_{ocrNode}");
    }

    public void ObserveOpeningEnemyIds(
        string runId,
        Observation<IReadOnlyList<string>> enemyIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (enemyIds.Value is null)
        {
            return;
        }

        var affixIds = enemyIds.Value
            .Where(_negativeAffixIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (affixIds.Length == 0)
        {
            return;
        }

        var hasCompleteAffixSet =
            affixIds.Length == Phase2RecognitionRegions.NegativeAffixSlots.Count;
        var observation = hasCompleteAffixSet
            ? Observation<IReadOnlyList<string>>.Known(
                affixIds,
                enemyIds.Confidence,
                enemyIds.Evidence,
                enemyIds.ObservedAt)
            : new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = affixIds,
                Confidence = 0,
                Evidence = enemyIds.Evidence,
                Uncertainty =
                [
                    "敌人概览只得到部分负面词条；保留已识别身份，但不视为完整集合。"
                ],
                ObservedAt = enemyIds.ObservedAt
            };

        var run = GetStableRun(runId);
        lock (run.Gate)
        {
            if (run.NegativeAffixesFinal)
            {
                run.LastAccessedAt = DateTimeOffset.UtcNow;
                return;
            }

            var retainedIds = (run.NegativeAffixes.Value ?? [])
                .Concat(affixIds)
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            run.NegativeAffixes = hasCompleteAffixSet
                ? observation
                : PartialStableList(
                    retainedIds,
                    run.NegativeAffixes.Evidence.Concat(enemyIds.Evidence),
                    enemyIds.ObservedAt,
                    "Enemy overview provided only part of the four negative affixes; confirmed identities and their source evidence were retained.");
            run.NegativeAffixesFinal = hasCompleteAffixSet;
            run.NegativeAffixContent = [];
            run.LastAccessedAt = DateTimeOffset.UtcNow;
        }
    }

    public void EndRunRecognition(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _stableRunRecognitions.TryRemove(runId, out _);
    }

    internal Phase2StableRecognitionStatistics GetStableRecognitionStatistics(
        string runId)
    {
        if (!_stableRunRecognitions.TryGetValue(runId, out var run))
        {
            return new Phase2StableRecognitionStatistics();
        }

        lock (run.Gate)
        {
            return new Phase2StableRecognitionStatistics(
                run.NegativeAffixRecognitionCount,
                run.EnvironmentRecognitionCount,
                run.StrategyRecognitionCount,
                run.NegativeAffixesFinal,
                run.EnvironmentFinal,
                run.StrategyIds.Count,
                run.StrategyRefreshPending);
        }
    }

    private StableRecognitionPlan PlanStableRecognitions(
        CaptureFrame frame,
        string runId)
    {
        var run = GetStableRun(runId);
        var affixSignature = CreateStableRegionSignature(
            frame,
            Phase2RecognitionRegions.PreparationAffixes);
        var environmentSignature = CreateStableRegionSignature(
            frame,
            Phase2RecognitionRegions.InvestmentIconSlots[0]);
        var strategySignature = CreateStableRegionSignature(
            frame,
            Phase2RecognitionRegions.InvestmentSlots);
        var nodeSignature = CreateStableRegionSignature(
            frame,
            Phase2RecognitionRegions.PreparationNodeValue);

        lock (run.Gate)
        {
            run.LastAccessedAt = DateTimeOffset.UtcNow;
            if (run.LastPreparationNodeSignature.HasValue &&
                BitOperations.PopCount(
                    run.LastPreparationNodeSignature.Value ^ nodeSignature) >=
                SignificantNodeRegionHashDistance)
            {
                if (run.StrategyRefreshPending)
                {
                    run.LastPreparationNodeSignature = nodeSignature;
                    run.PendingPreparationNodeSignature = null;
                    run.PendingPreparationNodeSignatureCount = 0;
                }
                else if (run.PendingPreparationNodeSignature.HasValue &&
                         BitOperations.PopCount(
                             run.PendingPreparationNodeSignature.Value ^
                             nodeSignature) < SignificantNodeRegionHashDistance)
                {
                    run.PendingPreparationNodeSignatureCount++;
                    if (run.PendingPreparationNodeSignatureCount >= 2)
                    {
                        run.StrategyRefreshPending = true;
                        run.StrategyAttemptsForVisual = 0;
                        run.LastStrategyAttemptSignature = null;
                        run.LastPreparationNodeSignature = nodeSignature;
                        run.PendingPreparationNodeSignature = null;
                        run.PendingPreparationNodeSignatureCount = 0;
                    }
                }
                else
                {
                    run.PendingPreparationNodeSignature = nodeSignature;
                    run.PendingPreparationNodeSignatureCount = 1;
                }
            }
            else if (!run.LastPreparationNodeSignature.HasValue)
            {
                run.LastPreparationNodeSignature = nodeSignature;
            }
            else
            {
                run.PendingPreparationNodeSignature = null;
                run.PendingPreparationNodeSignatureCount = 0;
            }

            var recognizeAffixes = !run.NegativeAffixesFinal &&
                ShouldAttemptStableVisual(
                    affixSignature,
                    ref run.LastAffixAttemptSignature,
                    ref run.AffixAttemptsForVisual);
            var recognizeEnvironment = !run.EnvironmentFinal &&
                ShouldAttemptStableVisual(
                    environmentSignature,
                    ref run.LastEnvironmentAttemptSignature,
                    ref run.EnvironmentAttemptsForVisual);

            if (!run.StrategyScanCompleted ||
                HasSignificantRegionChange(
                    run.CommittedStrategySignature,
                    strategySignature))
            {
                run.StrategyRefreshPending = true;
            }

            var recognizeStrategies = run.StrategyRefreshPending &&
                ShouldAttemptStableVisual(
                    strategySignature,
                    ref run.LastStrategyAttemptSignature,
                    ref run.StrategyAttemptsForVisual);

            if (recognizeAffixes)
            {
                run.NegativeAffixRecognitionCount++;
            }

            if (recognizeEnvironment)
            {
                run.EnvironmentRecognitionCount++;
            }

            if (recognizeStrategies)
            {
                run.StrategyRecognitionCount++;
            }

            return new StableRecognitionPlan(
                recognizeAffixes,
                recognizeEnvironment,
                recognizeStrategies,
                affixSignature,
                environmentSignature,
                strategySignature,
                run.NegativeAffixContent,
                run.EnvironmentContent,
                run.StrategyContent,
                run.NegativeAffixes,
                run.Environment,
                run.Strategies);
        }
    }

    private StableRecognitionResult CommitStableRecognitions(
        string runId,
        StableRecognitionPlan plan,
        IReadOnlyList<Phase2NamedContentRecognition> affixContent,
        Observation<IReadOnlyList<string>> affixes,
        IReadOnlyList<Phase2NamedContentRecognition> environmentContent,
        Observation<string> environment,
        IReadOnlyList<Phase2NamedContentRecognition> strategyContent,
        Observation<IReadOnlyList<string>> strategies)
    {
        var run = GetStableRun(runId);
        lock (run.Gate)
        {
            run.LastAccessedAt = DateTimeOffset.UtcNow;
            if (plan.RecognizeNegativeAffixes)
            {
                run.NegativeAffixContent = MergeNegativeAffixContent(
                    run.NegativeAffixContent,
                    affixContent);
                var currentAffixIds = run.NegativeAffixContent
                    .Where(item =>
                        item.Status == ObservationStatus.Known &&
                        item.ObjectId is not null)
                    .Select(item => item.ObjectId!)
                    .ToArray();
                var completeCurrentAffixSet =
                    run.NegativeAffixContent.Count ==
                    Phase2RecognitionRegions.NegativeAffixSlots.Count &&
                    run.NegativeAffixContent.All(item =>
                        item.Status == ObservationStatus.Known &&
                        item.ObjectId is not null);
                if (completeCurrentAffixSet)
                {
                    run.NegativeAffixes =
                        Observation<IReadOnlyList<string>>.Known(
                            currentAffixIds,
                            run.NegativeAffixContent.Average(item =>
                                item.Confidence),
                            run.NegativeAffixContent.Select(item =>
                                item.Evidence),
                            run.NegativeAffixContent
                                .Select(item => item.Evidence.CapturedAt)
                                .Where(item => item.HasValue)
                                .Max());
                }
                else
                {
                    var retainedIds = (run.NegativeAffixes.Value ?? [])
                        .Concat(currentAffixIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToHashSet(StringComparer.Ordinal);
                    run.NegativeAffixes = PartialStableList(
                        retainedIds,
                        run.NegativeAffixes.Evidence
                            .Concat(affixes.Evidence)
                            .Concat(run.NegativeAffixContent.Select(item =>
                                item.Evidence)),
                        affixes.ObservedAt,
                        affixes.Uncertainty.FirstOrDefault() ??
                        "Only part of the four negative affixes was recognized; the partial identities remain non-authoritative.");
                }

                run.NegativeAffixesFinal = completeCurrentAffixSet;
            }

            if (plan.RecognizeEnvironment)
            {
                run.EnvironmentContent = environmentContent;
                run.Environment = environment;
                run.EnvironmentFinal =
                    environment.Status == ObservationStatus.Known &&
                    environment.Value is not null;
            }

            if (plan.RecognizeStrategies)
            {
                var currentStrategyIds = (strategies.Value ?? [])
                    .Concat(strategyContent
                        .Where(item =>
                            item.Status == ObservationStatus.Known &&
                            item.ObjectId is not null)
                        .Select(item => item.ObjectId!))
                    .Distinct(StringComparer.Ordinal);
                foreach (var id in currentStrategyIds)
                {
                    run.StrategyIds.Add(id);
                }

                run.StrategyContent = MergeStrategyContent(
                    run.StrategyContent,
                    strategyContent);
                var hasUnresolvedOccupiedSlot = strategyContent.Any(item =>
                    item.Status != ObservationStatus.Known);
                if (hasUnresolvedOccupiedSlot)
                {
                    run.Strategies = PartialStableList(
                        run.StrategyIds,
                        strategyContent,
                        "投资策略区域仍有未知或冲突图标；保留已确认策略，未知项不能驱动高风险决策。");
                }
                else
                {
                    run.Strategies = Observation<IReadOnlyList<string>>.Known(
                        run.StrategyIds.Order(StringComparer.Ordinal).ToArray(),
                        strategyContent.Count == 0
                            ? 1
                            : strategyContent.Average(item => item.Confidence),
                        strategyContent.Select(item => item.Evidence),
                        strategyContent.Select(item => item.Evidence.CapturedAt)
                            .Where(item => item.HasValue)
                            .Max());
                }

                run.StrategyScanCompleted = true;
                run.CommittedStrategySignature = plan.StrategySignature;
                run.StrategyRefreshPending = hasUnresolvedOccupiedSlot &&
                    run.StrategyAttemptsForVisual <
                    MaximumStableRecognitionAttemptsPerVisual;
            }

            return new StableRecognitionResult(
                run.NegativeAffixContent,
                run.EnvironmentContent,
                run.StrategyContent,
                run.NegativeAffixes,
                run.Environment,
                run.Strategies,
                !plan.RecognizeNegativeAffixes,
                !plan.RecognizeEnvironment,
                !plan.RecognizeStrategies);
        }
    }

    private StableRunRecognitionState GetStableRun(string runId)
    {
        if (_stableRunRecognitions.Count > 64)
        {
            var cutoff = DateTimeOffset.UtcNow - StableRunRetention;
            foreach (var (key, value) in _stableRunRecognitions)
            {
                if (value.LastAccessedAt < cutoff)
                {
                    _stableRunRecognitions.TryRemove(key, out _);
                }
            }
        }

        return _stableRunRecognitions.GetOrAdd(
            runId,
            static _ => new StableRunRecognitionState());
    }

    private static bool ShouldAttemptStableVisual(
        ulong signature,
        ref ulong? lastAttemptSignature,
        ref int attemptsForVisual)
    {
        if (HasSignificantRegionChange(lastAttemptSignature, signature))
        {
            lastAttemptSignature = signature;
            attemptsForVisual = 0;
        }

        if (attemptsForVisual >= MaximumStableRecognitionAttemptsPerVisual)
        {
            return false;
        }

        attemptsForVisual++;
        return true;
    }

    private static bool HasSignificantRegionChange(
        ulong? previous,
        ulong current) =>
        previous is null ||
        BitOperations.PopCount(previous.Value ^ current) >=
        SignificantStableRegionHashDistance;

    private static ulong CreateStableRegionSignature(
        CaptureFrame frame,
        NormalizedRect normalized)
    {
        var region = normalized.ToPixels(frame.Width, frame.Height);
        Span<int> samples = stackalloc int[64];
        var sampleIndex = 0;
        var sum = 0;
        for (var sampleY = 0; sampleY < 8; sampleY++)
        {
            var y = region.Y + Math.Min(
                region.Height - 1,
                (int)Math.Round(sampleY * (region.Height - 1) / 7d));
            for (var sampleX = 0; sampleX < 8; sampleX++)
            {
                var x = region.X + Math.Min(
                    region.Width - 1,
                    (int)Math.Round(sampleX * (region.Width - 1) / 7d));
                var offset = y * frame.Stride + x * 4;
                var luminance =
                    (frame.BgraPixels[offset] * 29 +
                     frame.BgraPixels[offset + 1] * 150 +
                     frame.BgraPixels[offset + 2] * 77) >> 8;
                samples[sampleIndex++] = luminance;
                sum += luminance;
            }
        }

        var average = sum / samples.Length;
        ulong signature = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            if (samples[index] >= average)
            {
                signature |= 1UL << index;
            }
        }

        return signature;
    }

    private static IReadOnlyList<Phase2NamedContentRecognition>
        MergeNegativeAffixContent(
            IReadOnlyList<Phase2NamedContentRecognition> previous,
            IReadOnlyList<Phase2NamedContentRecognition> current)
    {
        var previousBySlot = previous.ToDictionary(
            item => item.SlotKey,
            StringComparer.Ordinal);
        var currentBySlot = current.ToDictionary(
            item => item.SlotKey,
            StringComparer.Ordinal);
        var merged = new List<Phase2NamedContentRecognition>(
            Phase2RecognitionRegions.NegativeAffixSlots.Count);
        for (var index = 0;
             index < Phase2RecognitionRegions.NegativeAffixSlots.Count;
             index++)
        {
            var slotKey = $"{Phase2NamedContentKind.NegativeAffix}-{index + 1}";
            previousBySlot.TryGetValue(slotKey, out var saved);
            currentBySlot.TryGetValue(slotKey, out var observed);
            if (saved is null)
            {
                if (observed is not null)
                {
                    merged.Add(observed);
                }

                continue;
            }

            if (saved.Status == ObservationStatus.Known &&
                saved.ObjectId is not null)
            {
                merged.Add(
                    observed is
                    {
                        Status: ObservationStatus.Known,
                        ObjectId: not null
                    } &&
                    string.Equals(
                        saved.ObjectId,
                        observed.ObjectId,
                        StringComparison.Ordinal) &&
                    observed.Confidence > saved.Confidence
                        ? observed
                        : saved);
                continue;
            }

            merged.Add(
                observed is
                {
                    Status: ObservationStatus.Known,
                    ObjectId: not null
                }
                    ? observed
                    : observed is not null &&
                      observed.Confidence >= saved.Confidence
                        ? observed
                        : saved);
        }

        return merged;
    }

    private static IReadOnlyList<Phase2NamedContentRecognition>
        MergeStrategyContent(
            IReadOnlyList<Phase2NamedContentRecognition> previous,
            IReadOnlyList<Phase2NamedContentRecognition> current)
    {
        var currentSlots = current
            .Select(item => item.SlotKey)
            .ToHashSet(StringComparer.Ordinal);
        var merged = previous
            .Where(item =>
                (item.Status == ObservationStatus.Known &&
                 item.ObjectId is not null) ||
                !currentSlots.Contains(item.SlotKey))
            .Concat(current)
            .GroupBy(
                item => item.ObjectId ?? item.SlotKey,
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Status == ObservationStatus.Known)
                .ThenByDescending(item => item.Confidence)
                .First())
            .ToArray();
        return merged;
    }

    private static Observation<IReadOnlyList<string>> PartialStableList(
        IReadOnlySet<string> values,
        IReadOnlyList<Phase2NamedContentRecognition> content,
        string reason) => PartialStableList(
            values,
            content.Select(item => item.Evidence),
            content.Select(item => item.Evidence.CapturedAt)
                .Where(item => item.HasValue)
                .Max(),
            reason);

    private static Observation<IReadOnlyList<string>> PartialStableList(
        IReadOnlySet<string> values,
        IEnumerable<EvidenceReference> evidence,
        DateTimeOffset? observedAt,
        string reason) => new()
        {
            Status = ObservationStatus.Unknown,
            Value = values.Order(StringComparer.Ordinal).ToArray(),
            Confidence = 0,
            Evidence = evidence.Distinct().ToArray(),
            Uncertainty = [reason],
            ObservedAt = observedAt
        };

    private sealed class StableRunRecognitionState
    {
        public object Gate { get; } = new();
        public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;
        public Observation<IReadOnlyList<string>> NegativeAffixes { get; set; } =
            Observation<IReadOnlyList<string>>.Unknown(
                "尚未在开局敌人概览中确认负面词条。");
        public Observation<string> Environment { get; set; } =
            Observation<string>.Unknown("尚未确认投资环境。");
        public Observation<IReadOnlyList<string>> Strategies { get; set; } =
            Observation<IReadOnlyList<string>>.Unknown("尚未确认投资策略。");
        public IReadOnlyList<Phase2NamedContentRecognition> NegativeAffixContent
        {
            get;
            set;
        } = [];
        public IReadOnlyList<Phase2NamedContentRecognition> EnvironmentContent
        {
            get;
            set;
        } = [];
        public IReadOnlyList<Phase2NamedContentRecognition> StrategyContent
        {
            get;
            set;
        } = [];
        public HashSet<string> StrategyIds { get; } = new(StringComparer.Ordinal);
        public bool NegativeAffixesFinal { get; set; }
        public bool EnvironmentFinal { get; set; }
        public bool StrategyScanCompleted { get; set; }
        public bool StrategyRefreshPending { get; set; }
        public string? LastPreparationNodeId { get; set; }
        public ulong? LastPreparationNodeSignature { get; set; }
        public ulong? PendingPreparationNodeSignature { get; set; }
        public int PendingPreparationNodeSignatureCount { get; set; }
        public ulong? LastAffixAttemptSignature;
        public ulong? LastEnvironmentAttemptSignature;
        public ulong? LastStrategyAttemptSignature;
        public ulong? CommittedStrategySignature { get; set; }
        public int AffixAttemptsForVisual;
        public int EnvironmentAttemptsForVisual;
        public int StrategyAttemptsForVisual;
        public int NegativeAffixRecognitionCount { get; set; }
        public int EnvironmentRecognitionCount { get; set; }
        public int StrategyRecognitionCount { get; set; }
    }

    private sealed record StableRecognitionPlan(
        bool RecognizeNegativeAffixes,
        bool RecognizeEnvironment,
        bool RecognizeStrategies,
        ulong AffixSignature,
        ulong EnvironmentSignature,
        ulong StrategySignature,
        IReadOnlyList<Phase2NamedContentRecognition> CachedAffixContent,
        IReadOnlyList<Phase2NamedContentRecognition> CachedEnvironmentContent,
        IReadOnlyList<Phase2NamedContentRecognition> CachedStrategyContent,
        Observation<IReadOnlyList<string>> CachedAffixes,
        Observation<string> CachedEnvironment,
        Observation<IReadOnlyList<string>> CachedStrategies);

    private sealed record StableRecognitionResult(
        IReadOnlyList<Phase2NamedContentRecognition> AffixContent,
        IReadOnlyList<Phase2NamedContentRecognition> EnvironmentContent,
        IReadOnlyList<Phase2NamedContentRecognition> StrategyContent,
        Observation<IReadOnlyList<string>> Affixes,
        Observation<string> Environment,
        Observation<IReadOnlyList<string>> Strategies,
        bool ReusedAffixes,
        bool ReusedEnvironment,
        bool ReusedStrategies);
}

internal sealed record Phase2StableRecognitionStatistics(
    int NegativeAffixRecognitionCount = 0,
    int EnvironmentRecognitionCount = 0,
    int StrategyRecognitionCount = 0,
    bool NegativeAffixesFinal = false,
    bool EnvironmentFinal = false,
    int StrategyCount = 0,
    bool StrategyRefreshPending = false);
