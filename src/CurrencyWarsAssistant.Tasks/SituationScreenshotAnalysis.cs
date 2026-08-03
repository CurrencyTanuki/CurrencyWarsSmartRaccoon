using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public interface ISituationScreenshotAnalyzer
{
    void SeedRunIdentity(string runId, RunIdentityEvidence identity)
    {
    }

    Task<ScreenshotAnalysisResult> AnalyzeAsync(
        CaptureFrame frame,
        string evidenceSourceId,
        AdvisorSelection selection,
        CancellationToken cancellationToken,
        string? runId = null);
}

public sealed partial class CurrencyWarsSituationScreenshotAnalyzer(
    IGamePageClassifier pageClassifier,
    ICharacterCardRecognizer characterRecognizer,
    IReadOnlyList<CharacterCardTemplateDefinition> characterTemplates,
    IGoldDigitRecognizer goldDigitRecognizer,
    IReadOnlyList<GoldDigitTemplateDefinition> goldDigitTemplates,
    IOcrOpeningPageReader openingPageReader,
    RewardShopReader rewardShopReader,
    IOfflineOcr ocr,
    GameDataCatalog gameData,
    GuideRepository guideRepository,
    AdvisorEngine advisorEngine,
    string guideDirectory,
    Phase2OperationalScreenshotAnalyzer? operationalAnalyzer = null,
    IOfflineOcr? numericOcr = null,
    IReadOnlyList<Phase2IconTemplateDefinition>? phase2IconTemplates = null) :
    ISituationScreenshotAnalyzer
{
    private static readonly bool EnableTimingDiagnostics = string.Equals(
        Environment.GetEnvironmentVariable("CURRENCY_WARS_PHASE2_TIMING"),
        "1",
        StringComparison.Ordinal);
    private readonly IOfflineOcr _numericOcr = numericOcr ?? ocr;
    private readonly IReadOnlyList<Phase2IconTemplateDefinition>
        _phase2IconTemplates = phase2IconTemplates ?? [];
    private readonly IReadOnlySet<string> _competitorIds = gameData.Competitors
        .Select(item => item.Id)
        .ToHashSet(StringComparer.Ordinal);
    private readonly OpenCvUiDigitSequenceRecognizer _uiDigitRecognizer = new();

    public void SeedRunIdentity(string runId, RunIdentityEvidence identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.EnemyAffixIds.Count > 0)
        {
            operationalAnalyzer?.ObserveOpeningEnemyIds(
                runId,
                Observation<IReadOnlyList<string>>.Known(
                    identity.EnemyAffixIds,
                    0.9,
                    observedAt: DateTimeOffset.UtcNow));
        }

        if (identity.EnemyIds.Count > 0)
        {
            _openingEnemyIdsByRun[runId] =
                Observation<IReadOnlyList<string>>.Known(
                    identity.EnemyIds,
                    0.9,
                    observedAt: DateTimeOffset.UtcNow);
        }
    }

    private static readonly IReadOnlyList<PixelRect> BenchSlots =
    [
        new(383, 844, 114, 137),
        new(506, 844, 119, 137),
        new(633, 844, 117, 137),
        new(759, 844, 114, 137),
        new(883, 844, 116, 137),
        new(1005, 844, 116, 137),
        new(1128, 844, 116, 137),
        new(1250, 844, 116, 137),
        new(1374, 844, 116, 137)
    ];

    private static readonly IReadOnlyList<PixelRect> BoardSlots =
    [
        new(681, 329, 128, 140),
        new(827, 329, 122, 140),
        new(972, 329, 120, 140),
        new(1114, 329, 120, 140),
        new(535, 600, 140, 145),
        new(687, 600, 130, 145),
        new(829, 600, 130, 145),
        new(966, 600, 130, 145),
        new(1108, 600, 130, 145),
        new(1258, 600, 130, 145)
    ];

    private static readonly PixelRect GoldDigitRegion = new(1620, 895, 60, 55);
    private static readonly PixelRect GoldOcrRegion = new(1590, 920, 120, 150);
    private static readonly PixelRect EconomyTightOcrRegion =
        // Keep only the numeric portion of the shop card. Including the coin
        // icon produced false leading digits while the mouse cost tooltip was
        // visible. The left edge must still include the full first digit.
        new(1628, 912, 67, 60);
    private static readonly PixelRect EconomyNarrowOcrRegion =
        new(1640, 912, 55, 60);
    private static readonly PixelRect HealthOcrRegion = new(1420, 30, 60, 70);
    private static readonly PixelRect HealthTightOcrRegion = new(1430, 64, 44, 34);
    private static readonly PixelRect ActionPointsOcrRegion = new(1500, 815, 90, 120);
    private static readonly PixelRect ChallengeSuccessHealthRegion = new(590, 445, 330, 105);
    private static readonly IReadOnlyList<PixelRect> ChallengeSuccessDamageRegions =
    [
        new(1165, 565, 255, 105),
        new(1165, 645, 255, 105),
        new(1175, 755, 125, 85)
    ];
    private static readonly PixelRect ChallengeSuccessZeroDamageGlyphRegion =
        new(1196, 790, 28, 34);
    private static readonly PixelRect ChallengeFailureHealthRegion = new(815, 245, 320, 95);

    private readonly IReadOnlyDictionary<string, CurrencyWarsCharacterData>
        _charactersById = gameData.CurrencyWarsCharacters.ToDictionary(
            character => character.Id,
            StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<IReadOnlyList<GuidePlaybook>> _guides = new(
        () => guideRepository.LoadDirectory(guideDirectory),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ConcurrentDictionary<
        string,
        Observation<IReadOnlyList<string>>> _openingEnemyIdsByRun =
            new(StringComparer.Ordinal);

    public async Task<ScreenshotAnalysisResult> AnalyzeAsync(
        CaptureFrame frame,
        string evidenceSourceId,
        AdvisorSelection selection,
        CancellationToken cancellationToken,
        string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceSourceId);
        cancellationToken.ThrowIfCancellationRequested();
        var overallStopwatch = Stopwatch.StartNew();

        if (!OpenCvTemplateMatcher.HasSupportedAspectRatio(frame.Width, frame.Height))
        {
            throw new InvalidDataException(
                $"截图必须为 16:9；实际为 {frame.Width}×{frame.Height}。");
        }

        var analysisId = $"{frame.CapturedAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var evidence = new EvidenceReference(
            evidenceSourceId,
            $"frame:{frame.Width}x{frame.Height}",
            "当前分析所用的原始截图。",
            frame.CapturedAt);
        var warnings = new List<string>();
        var classificationStopwatch = Stopwatch.StartNew();
        var classification = pageClassifier.Classify(frame);
        classificationStopwatch.Stop();
        var afterClassification = overallStopwatch.Elapsed;
        var diagnosticInference = classification is null &&
                                  pageClassifier is IGamePageClassifierDiagnostics
                                      phase2Diagnostics
            ? Phase2PageDiagnosticFallback.TryInfer(
                phase2Diagnostics.LastDiagnostics)
            : null;
        var shouldProbeSettlementSemantics = classification is null ||
            classification.PageId is
                "challenge_failed" or
                "challenge_health_depleted" or
                "challenge_success";
        var semanticSettlement = shouldProbeSettlementSemantics
            ? await Phase2SettlementSemanticClassifier.TryClassifyAsync(
                    frame,
                    ocr,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (classification is null &&
            semanticSettlement is null &&
            pageClassifier is IGamePageClassifierDiagnostics classifierDiagnostics)
        {
            warnings.Add(
                "recognition:classifier-miss " +
                string.Join(
                    "; ",
                    classifierDiagnostics.LastDiagnostics
                        .OrderByDescending(item => item.Confidence)
                        .Take(5)
                        .Select(item =>
                            $"{item.PageId}/{item.AnchorId}=" +
                            $"{item.Confidence:F3}/{item.Threshold:F3}")));
        }
        var classifiedPageId = semanticSettlement?.PageId ??
                               classification?.PageId ??
                               diagnosticInference?.PageId;
        var classifiedPageConfidence = semanticSettlement?.Confidence ??
                                       classification?.Confidence ??
                                       diagnosticInference?.Confidence ?? 0;
        Observation<string> page;
        if (semanticSettlement is not null)
        {
            page = Observation<string>.Known(
                semanticSettlement.PageId,
                semanticSettlement.Confidence,
                [evidence with
                {
                    Locator = "ocr:settlement-semantic-layout",
                    Summary = string.Join(" | ", semanticSettlement.Evidence),
                    Confidence = semanticSettlement.Confidence
                }],
                frame.CapturedAt);
        }
        else if (classification is not null)
        {
            page = Observation<string>.Known(
                classification.PageId,
                classification.Confidence,
                [evidence with
                {
                    Locator = "page-classifier:" +
                              string.Join(
                                  ",",
                                  classification.AnchorMatches.Select(match =>
                                      $"{match.Id}={match.Confidence:F3}"))
                }],
                frame.CapturedAt);
        }
        else if (diagnosticInference is { } inferred)
        {
            page = Observation<string>.Known(
                inferred.PageId,
                inferred.Confidence,
                [evidence with
                {
                    Locator = "phase2:diagnostic-page-fallback:" +
                              string.Join(
                                  ",",
                                  inferred.Evidence.Select(item =>
                                      $"{item.AnchorId}={item.Confidence:F3}")),
                    Summary = "两个独立备战页面锚点联合确认；未改变自动化全局阈值。",
                    Confidence = inferred.Confidence
                }],
                frame.CapturedAt);
        }
        else
        {
            page = Observation<string>.Unknown(
                "未命中任何已配置页面锚点",
                [evidence],
                frame.CapturedAt);
        }

        var stage = classifiedPageId is null
            ? Observation<string>.Unknown(
                "页面未知，无法判断阶段",
                [evidence],
                frame.CapturedAt)
            : Observation<string>.Known(
                MapStage(classifiedPageId),
                classifiedPageConfidence,
                page.Evidence,
                frame.CapturedAt);

        var effectiveRunId = runId ?? analysisId;
        var snapshot = UnknownSnapshot(effectiveRunId, frame.CapturedAt) with
        {
            PageId = page,
            Stage = stage
        };

        Task<RunSnapshot>? pageSnapshotTask = null;
        var hasPreparationSnapshot = false;
        if (classifiedPageId is
            "preparation_1_1" or "preparation_1_2" or "preparation_generic")
        {
            hasPreparationSnapshot = true;
            pageSnapshotTask = AnalyzePreparationAsync(
                frame,
                snapshot,
                evidence,
                warnings,
                cancellationToken,
                includeFormation: operationalAnalyzer is null);
        }
        else if (classifiedPageId is not null)
        {
            pageSnapshotTask = classifiedPageId switch
            {
                "reward_shop" => AnalyzeShopAsync(
                    frame,
                    snapshot,
                    evidence,
                    warnings,
                    cancellationToken),
                "enemy_overview" => AnalyzeEnemyOverviewAsync(
                    frame,
                    snapshot,
                    evidence,
                    warnings,
                    cancellationToken),
                "investment_environment" =>
                    AnalyzeInvestmentOptionsAsync(
                        frame,
                        snapshot,
                        warnings,
                        cancellationToken),
                "challenge_success" => AnalyzeChallengeSuccessAsync(
                    frame,
                    snapshot,
                    evidence,
                    cancellationToken),
                "challenge_failed" => AnalyzeChallengeFailureAsync(
                    frame,
                    snapshot,
                    evidence,
                    cancellationToken),
                "challenge_health_depleted" => AnalyzeChallengeHealthDepletedAsync(
                    frame,
                    snapshot,
                    evidence,
                    cancellationToken),
                _ => null
            };
        }

        operationalAnalyzer?.NotifyPageObserved(
            effectiveRunId,
            classifiedPageId);

        var afterTaskScheduling = overallStopwatch.Elapsed;

        var skipOperationalAnalysis = IsOneTimeSelectionPage(
            classifiedPageId);
        Phase2OperationalState? operational;
        if (operationalAnalyzer is null || skipOperationalAnalysis)
        {
            if (pageSnapshotTask is not null)
            {
                snapshot = await pageSnapshotTask.ConfigureAwait(false);
            }

            operational = null;
        }
        else
        {
            var operationalTask = operationalAnalyzer.AnalyzeAsync(
                frame,
                classifiedPageId ?? "__classifier-miss__",
                evidenceSourceId,
                snapshot,
                cancellationToken);
            if (pageSnapshotTask is null)
            {
                operational = await operationalTask.ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAll(pageSnapshotTask, operationalTask)
                    .ConfigureAwait(false);
                snapshot = pageSnapshotTask.Result;
                operational = operationalTask.Result;
            }
        }
        var afterAnalyzerTasks = overallStopwatch.Elapsed;
        if (operational is not null)
        {
            if (operational.PageFamily == Phase2PageFamily.Preparation &&
                !hasPreparationSnapshot &&
                (snapshot.Economy.Status == ObservationStatus.Unknown ||
                 snapshot.Health.Status == ObservationStatus.Unknown))
            {
                snapshot = await AnalyzePreparationAsync(
                        frame,
                        snapshot,
                        evidence,
                        warnings,
                        cancellationToken,
                        includeFormation: false)
                    .ConfigureAwait(false);
            }
            else if (operational.PageFamily == Phase2PageFamily.BattleSettlement &&
                     snapshot.Health.Status == ObservationStatus.Unknown)
            {
                snapshot = await AnalyzeChallengeSuccessAsync(
                        frame,
                        snapshot,
                        evidence,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (classifiedPageId is null &&
                operational.PageFamily != Phase2PageFamily.Unknown)
            {
                var inferredPage = operational.PageFamily.ToString();
                snapshot = snapshot with
                {
                    PageId = Observation<string>.Known(
                        inferredPage,
                        0.72,
                        [evidence with
                        {
                            Locator = "phase2:page-evidence-fusion",
                            Summary = inferredPage,
                            Confidence = 0.72
                        }],
                        frame.CapturedAt),
                    Stage = Observation<string>.Known(
                        inferredPage,
                        0.72,
                        [evidence with
                        {
                            Locator = "phase2:page-evidence-fusion",
                            Summary = inferredPage,
                            Confidence = 0.72
                        }],
                        frame.CapturedAt)
                };
            }

            if (operational.CumulativeSpend.Status != ObservationStatus.Unknown ||
                operational.CumulativeSpend.Evidence.Count > 0)
            {
                snapshot = snapshot with
                {
                    CumulativeSpend = operational.CumulativeSpend
                };
            }

            snapshot = MergeOperationalSnapshot(snapshot, operational);

            warnings.AddRange(operational.Diagnostics);
        }

        // Enemy overview OCR runs asynchronously. Consume and cache it only
        // after the page task has completed; doing this before the await fed
        // an Unknown placeholder into both opening filters and run history.
        if (string.Equals(
                classifiedPageId,
                "enemy_overview",
                StringComparison.OrdinalIgnoreCase))
        {
            var mixedEnemyOverview = snapshot.EnemyIds;
            operationalAnalyzer?.ObserveOpeningEnemyIds(
                effectiveRunId,
                mixedEnemyOverview);
            snapshot = snapshot with
            {
                // The overview reader returns the faction and four affixes in
                // one list for legacy callers. EnemyIds is the faction field;
                // affixes are routed separately to the stable operational
                // cache above and must not pollute this identity collection.
                EnemyIds = operationalAnalyzer is null
                    ? mixedEnemyOverview
                    : FilterIdentityObservation(
                    mixedEnemyOverview,
                    _competitorIds,
                    "敌人概览未可靠识别敌人阵营")
            };
            if (snapshot.EnemyIds.Value is { Count: > 0 })
            {
                _openingEnemyIdsByRun[effectiveRunId] = snapshot.EnemyIds;
            }
        }
        else if (snapshot.EnemyIds.Status == ObservationStatus.Unknown &&
                 _openingEnemyIdsByRun.TryGetValue(
                     effectiveRunId,
                     out var openingEnemyIds))
        {
            snapshot = snapshot with { EnemyIds = openingEnemyIds };
        }

        var afterRecognitionTasks = overallStopwatch.Elapsed;

        var recognitionElapsed = overallStopwatch.Elapsed;
        var adviceStopwatch = Stopwatch.StartNew();
        var advice = advisorEngine.Evaluate(snapshot, _guides.Value, selection);
        adviceStopwatch.Stop();
        warnings.AddRange(advice.Warnings);
        var unknown = UnknownFields(snapshot);
        if (unknown.Count > 0)
        {
            warnings.Add(
                "以下字段在这张截图中不可可靠确定：" +
                string.Join("、", unknown));
        }

        overallStopwatch.Stop();
        if (EnableTimingDiagnostics)
        {
            warnings.Add(
                FormattableString.Invariant(
                    $"perf:situation classification={classificationStopwatch.Elapsed.TotalMilliseconds:F1}ms; post-classification={afterClassification.TotalMilliseconds:F1}ms; scheduled={afterTaskScheduling.TotalMilliseconds:F1}ms; analyzers={afterAnalyzerTasks.TotalMilliseconds:F1}ms; post-process={afterRecognitionTasks.TotalMilliseconds:F1}ms; recognition={recognitionElapsed.TotalMilliseconds:F1}ms; advice={adviceStopwatch.Elapsed.TotalMilliseconds:F1}ms; total={overallStopwatch.Elapsed.TotalMilliseconds:F1}ms"));
        }

        return new ScreenshotAnalysisResult
        {
            AnalysisId = analysisId,
            Snapshot = snapshot,
            RouteCandidates = advice.Matches,
            Recommendations = advice.Recommendations,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            UnknownFields = unknown,
            OperationalState = operational
        };
    }

    internal static bool IsOneTimeSelectionPage(string? pageId) => pageId is
        "enemy_overview" or
        "investment_environment" or
        "investment_strategy";

    private static RunSnapshot MergeOperationalSnapshot(
        RunSnapshot snapshot,
        Phase2OperationalState operational)
    {
        var formation = operational.Formation.Value ?? [];
        var boardIds = formation
            .Where(item => item.Zone is FormationZone.Front or FormationZone.Back)
            .Select(item => item.CharacterId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var benchIds = formation
            .Where(item => item.Zone == FormationZone.Bench)
            .Select(item => item.CharacterId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // Lineup means units that are actually deployed. Bench ownership is a
        // separate field and must not silently change the active formation.
        var lineupIds = boardIds;
        var synergyIds = (operational.ActiveSynergies.Value ?? [])
            .Select(item => item.SynergyId)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var equipmentIds = formation.SelectMany(item => item.EquipmentIds)
            .Concat((operational.InventorySlots.Value ?? [])
                .Where(item =>
                    item.Occupancy == EquipmentSlotOccupancy.Equipped &&
                    item.ItemId is not null)
                .Select(item => item.ItemId!))
            .Concat(operational.SimpleEquipmentIds.Value ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return snapshot with
        {
            CumulativeSpend = operational.CumulativeSpend,
            BoardCharacterIds = ProjectObservation(
                operational.Formation,
                boardIds,
                "阵容仅得到残缺证据；前台/后台角色不能作为完整可靠状态。"),
            BenchCharacterIds = ProjectObservation(
                operational.Formation,
                benchIds,
                "阵容仅得到残缺证据；候补角色不能作为完整可靠状态。"),
            LineupIds = ProjectObservation(
                operational.Formation,
                lineupIds,
                "阵容仅得到残缺证据；完整角色集合不能驱动高风险建议。"),
            SynergyIds = ProjectObservation(
                operational.ActiveSynergies,
                synergyIds,
                "羁绊图标存在未识别或冲突项。"),
            InvestmentEnvironmentId = operational.InvestmentEnvironmentId,
            InvestmentStrategyIds = operational.InvestmentStrategyIds,
            EquipmentIds = ProjectEquipmentObservation(
                operational.Formation,
                operational.SimpleEquipmentIds,
                equipmentIds),
            SpecialItemIds = operational.SpecialItemIds,
            InventorySlots = operational.InventorySlots,
            ActionPoints = operational.RemainingActionValue.Status ==
                           ObservationStatus.Known &&
                           operational.RemainingActionValue.Value is not null
                ? Observation<int>.Known(
                    operational.RemainingActionValue.Value.TotalActionValue,
                    operational.RemainingActionValue.Confidence,
                    operational.RemainingActionValue.Evidence,
                    operational.RemainingActionValue.ObservedAt)
                : snapshot.ActionPoints,
            CurrentNodeDamage = operational.BattleScreenDamageCandidate.Status ==
                                ObservationStatus.Known
                ? operational.BattleScreenDamageCandidate
                : snapshot.CurrentNodeDamage
        };
    }

    private static Observation<IReadOnlyList<string>> ProjectObservation<T>(
        Observation<T> source,
        IReadOnlyList<string> values,
        string uncertainty) =>
        source.Status == ObservationStatus.Known
            ? Observation<IReadOnlyList<string>>.Known(
                values,
                source.Confidence,
                source.Evidence,
                source.ObservedAt)
            : new Observation<IReadOnlyList<string>>
            {
                Status = source.Status,
                Value = values,
                Confidence = 0,
                Evidence = source.Evidence,
                Uncertainty = source.Uncertainty.Count > 0
                    ? source.Uncertainty
                    : [uncertainty],
                ObservedAt = source.ObservedAt
            };

    internal static Observation<IReadOnlyList<string>> FilterIdentityObservation(
        Observation<IReadOnlyList<string>> source,
        IReadOnlySet<string> acceptedIds,
        string missingReason)
    {
        var values = (source.Value ?? [])
            .Where(acceptedIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
        {
            return Observation<IReadOnlyList<string>>.Unknown(
                missingReason,
                source.Evidence,
                source.ObservedAt);
        }

        return source.Status == ObservationStatus.Known
            ? Observation<IReadOnlyList<string>>.Known(
                values,
                source.Confidence,
                source.Evidence,
                source.ObservedAt)
            : new Observation<IReadOnlyList<string>>
            {
                Status = source.Status,
                Value = values,
                Confidence = source.Confidence,
                Evidence = source.Evidence,
                Uncertainty = source.Uncertainty.Count > 0
                    ? source.Uncertainty
                    : [missingReason],
                ObservedAt = source.ObservedAt
            };
    }

    private static Observation<IReadOnlyList<string>> ProjectEquipmentObservation(
        Observation<IReadOnlyList<FormationCharacterState>> formation,
        Observation<IReadOnlyList<string>> inventory,
        IReadOnlyList<string> values)
    {
        var equipmentSlotsComplete = (formation.Value ?? [])
            .SelectMany(item => item.FinalEquipmentSlots)
            .All(item => item.Occupancy is
                EquipmentSlotOccupancy.Empty or
                EquipmentSlotOccupancy.Equipped);
        var isComplete = formation.Status == ObservationStatus.Known &&
                         inventory.Status == ObservationStatus.Known &&
                         equipmentSlotsComplete;
        var evidence = formation.Evidence.Concat(inventory.Evidence).ToArray();
        var observedAt = new[] { formation.ObservedAt, inventory.ObservedAt }
            .OfType<DateTimeOffset>()
            .DefaultIfEmpty()
            .Max();
        if (isComplete)
        {
            return Observation<IReadOnlyList<string>>.Known(
                values,
                Math.Min(formation.Confidence, inventory.Confidence),
                evidence,
                observedAt == default ? null : observedAt);
        }

        return new Observation<IReadOnlyList<string>>
        {
            Status = formation.Status == ObservationStatus.Conflict ||
                     inventory.Status == ObservationStatus.Conflict
                ? ObservationStatus.Conflict
                : ObservationStatus.Unknown,
            Value = values,
            Confidence = 0,
            Evidence = evidence,
            Uncertainty = formation.Uncertainty
                .Concat(inventory.Uncertainty)
                .Append("装备数据不完整；已识别的装备ID仍作为残缺证据保留。")
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ObservedAt = observedAt == default ? null : observedAt
        };
    }

    private async Task<RunSnapshot> AnalyzePreparationAsync(
        CaptureFrame frame,
        RunSnapshot snapshot,
        EvidenceReference evidence,
        ICollection<string> warnings,
        CancellationToken cancellationToken,
        bool includeFormation = true)
    {
        var totalStarted = Stopwatch.GetTimestamp();
        var bench = snapshot.BenchCharacterIds;
        var board = snapshot.BoardCharacterIds;
        var lineup = snapshot.LineupIds;
        var synergies = snapshot.SynergyIds;
        if (includeFormation)
        {
            var benchSlots = characterRecognizer.Recognize(
                frame,
                characterTemplates,
                BenchSlots);
            var boardSlots = characterRecognizer.Recognize(
                frame,
                characterTemplates,
                BoardSlots);
            bench = ObserveCharacters(
                benchSlots,
                evidence,
                "bench",
                warnings,
                frame.CapturedAt);
            board = ObserveCharacters(
                boardSlots,
                evidence,
                "board",
                warnings,
                frame.CapturedAt);
            var lineupIds = (board.Value ?? [])
                .Concat(bench.Value ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var lineupConfidence = Math.Min(
                board.Status == ObservationStatus.Known ? board.Confidence : 0.5,
                bench.Status == ObservationStatus.Known ? bench.Confidence : 0.5);
            lineup = Observation<IReadOnlyList<string>>.Known(
                lineupIds,
                lineupConfidence,
                board.Evidence.Concat(bench.Evidence),
                frame.CapturedAt);
            var synergyIds = lineupIds
                .Where(_charactersById.ContainsKey)
                .SelectMany(id => _charactersById[id].BondNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => $"bond:{name}")
                .ToArray();
            synergies = Observation<IReadOnlyList<string>>.Known(
                synergyIds,
                lineupConfidence,
                lineup.Evidence,
                frame.CapturedAt);
        }

        var legacyGold = goldDigitRecognizer.Recognize(
            frame,
            goldDigitTemplates,
            GoldDigitRegion);
        var economyTask = MeasureAsync(async () =>
        {
            // The old 60-pixel crop can clip the leading digit (for example,
            // 43 becomes a confident 3). Prefer the full localized value crop
            // and retain the legacy recognizer only as a bounded fallback.
            var localized = await ReadPreparationEconomyAsync(
                    frame,
                    evidence,
                    cancellationToken)
                .ConfigureAwait(false);
            if (localized.Status == ObservationStatus.Known ||
                legacyGold.Value is not int value)
            {
                return localized;
            }

            return Observation<int>.Known(
                value,
                legacyGold.Confidence,
                [evidence with
                {
                    Locator = "vision:gold-digit-legacy-fallback",
                    Summary = string.Join("; ", localized.Uncertainty)
                }],
                frame.CapturedAt);
        });
        var healthTask = MeasureAsync(() => ReadPreparationHealthAsync(
            frame,
            evidence,
            cancellationToken));
        await Task.WhenAll(economyTask, healthTask).ConfigureAwait(false);
        var economyResult = await economyTask.ConfigureAwait(false);
        var healthResult = await healthTask.ConfigureAwait(false);
        var economy = economyResult.Value;
        var health = healthResult.Value;
        if (EnableTimingDiagnostics)
        {
            warnings.Add(
                $"perf:snapshot-preparation economy={economyResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"health={healthResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"total={Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds:F1}ms");
        }
        return snapshot with
        {
            Economy = economy,
            Health = health,
            BoardCharacterIds = board,
            BenchCharacterIds = bench,
            LineupIds = lineup,
            SynergyIds = synergies
        };
    }

    private async Task<RunSnapshot> AnalyzeShopAsync(
        CaptureFrame frame,
        RunSnapshot snapshot,
        EvidenceReference evidence,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var slotsTask = rewardShopReader.ReadAsync(frame, cancellationToken);
        var economyTask = ReadPreparationEconomyAsync(
            frame,
            evidence,
            cancellationToken);
        var healthTask = ReadPreparationHealthAsync(
            frame,
            evidence,
            cancellationToken);
        await Task.WhenAll(slotsTask, economyTask, healthTask)
            .ConfigureAwait(false);
        var slots = slotsTask.Result;
        var recognized = slots
            .Where(slot => slot.Character is not null)
            .Select(slot => slot.Character!.Id)
            .ToArray();
        if (recognized.Length != slots.Count)
        {
            warnings.Add(
                $"商店仅可靠识别 {recognized.Length}/{slots.Count} 个槽位；" +
                "未识别槽位不会被猜测。" );
        }
        var confidence = recognized.Length == 0
            ? 0
            : slots.Where(slot => slot.Character is not null)
                .Average(slot => slot.Confidence);
        var shop = recognized.Length == 0
            ? Observation<IReadOnlyList<string>>.Unknown(
                "商店角色未能可靠识别",
                [evidence],
                frame.CapturedAt)
            : Observation<IReadOnlyList<string>>.Known(
                recognized,
                confidence,
                [evidence with { Locator = "ocr:reward-shop-slots" }],
                frame.CapturedAt);
        return snapshot with
        {
            Economy = economyTask.Result,
            Health = healthTask.Result,
            ShopCharacterIds = shop
        };
    }

    private async Task<RunSnapshot> AnalyzeEnemyOverviewAsync(
        CaptureFrame frame,
        RunSnapshot snapshot,
        EvidenceReference evidence,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = await openingPageReader
            .ReadEnemyOverviewAsync(frame, cancellationToken)
            .ConfigureAwait(false);
        var ids = result.RecognizedCompetitors
            .Concat(result.RecognizedEnemyModifiers)
            .Select(item => item.Id)
            .ToArray();
        if (!result.IsComplete)
        {
            warnings.Add("敌人概览存在未识别项；只保留已经通过现有 OCR 门槛的结果。");
        }
        var enemyIds = ids.Length == 0
            ? Observation<IReadOnlyList<string>>.Unknown(
                "敌人概览未能可靠识别",
                [evidence],
                frame.CapturedAt)
            : result.IsComplete
                ? Observation<IReadOnlyList<string>>.Known(
                    ids,
                    0.9,
                    [evidence with { Locator = "ocr:enemy-overview" }],
                    frame.CapturedAt)
                : new Observation<IReadOnlyList<string>>
                {
                    Status = ObservationStatus.Unknown,
                    Value = ids,
                    Confidence = 0.6,
                    Evidence =
                    [
                        evidence with { Locator = "ocr:enemy-overview:partial" }
                    ],
                    Uncertainty =
                    [
                        "敌人概览识别不完整；只保留已确认身份，不能视为完整敌人集合。"
                    ],
                    ObservedAt = frame.CapturedAt
                };
        return snapshot with { EnemyIds = enemyIds };
    }

    private async Task<RunSnapshot> AnalyzeInvestmentOptionsAsync(
        CaptureFrame frame,
        RunSnapshot snapshot,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = await openingPageReader
            .ReadInvestmentEnvironmentsAsync(frame, cancellationToken)
            .ConfigureAwait(false);
        warnings.Add(result.IsComplete
            ? "当前画面展示的是投资环境候选项，不能据此认定玩家已经选择其中一项。"
            : "投资环境候选项识别不完整，且该画面不能证明最终选择。");
        return snapshot;
    }

    private async Task<RunSnapshot> AnalyzeChallengeSuccessAsync(
        CaptureFrame frame,
        RunSnapshot snapshot,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var health = await ReadOutcomeHealthAsync(
            frame,
            ChallengeSuccessHealthRegion,
            "challenge-success-health",
            evidence,
            cancellationToken).ConfigureAwait(false);
        return snapshot with
        {
            Health = health
        };
    }

    private async Task<RunSnapshot> AnalyzeChallengeFailureAsync(
        CaptureFrame frame,
        RunSnapshot snapshot,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var health = await ReadOutcomeHealthAsync(
            frame,
            ChallengeFailureHealthRegion,
            "challenge-failure-health",
            evidence,
            cancellationToken).ConfigureAwait(false);
        return snapshot with
        {
            Health = health
        };
    }

    private async Task<RunSnapshot> AnalyzeChallengeHealthDepletedAsync(
        CaptureFrame frame,
        RunSnapshot snapshot,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var health = await ReadOutcomeHealthAsync(
            frame,
            ChallengeFailureHealthRegion,
            "challenge-health-depleted-deficit",
            evidence,
            cancellationToken).ConfigureAwait(false);
        return snapshot with
        {
            Health = Observation<int>.Unknown(
                "挑战结束页只证明生命值已耗尽；负数是超额扣除量，不能当作当前生命值或精确血量变化",
                health.Evidence.Count > 0 ? health.Evidence : [evidence],
                frame.CapturedAt)
        };
    }

    private async Task<Observation<int>> ReadOutcomeHealthAsync(
        CaptureFrame frame,
        PixelRect referenceRegion,
        string locator,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        if (!_numericOcr.IsAvailable)
        {
            return Observation<int>.Unknown(
                "Windows 中文 OCR 不可用",
                [evidence],
                frame.CapturedAt);
        }

        var recognized = await _numericOcr.RecognizeAsync(
                frame,
                Scale(referenceRegion, frame),
                cancellationToken)
            .ConfigureAwait(false);
        var multiDigitValues = recognized.Lines
            .Prepend(recognized.Text)
            .SelectMany(text => IntegerPattern().Matches(text))
            .Where(match => match.Length >= 2)
            .Select(match => int.TryParse(
                match.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
                    ? (int?)value
                    : null)
            .Where(value => value is >= 0 and <= 100)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();
        if (multiDigitValues.Length == 1)
        {
            return KnownInteger(
                multiDigitValues[0],
                locator,
                string.Join(" | ", recognized.Lines),
                evidence,
                frame.CapturedAt);
        }

        var spacedDigitValues = recognized.Lines
            .Where(text => Regex.IsMatch(
                text.Trim(),
                "^[0-9](?:\\s+[0-9])+$",
                RegexOptions.CultureInvariant))
            .Select(text => text.Replace(
                " ",
                string.Empty,
                StringComparison.Ordinal))
            .Select(text => int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value)
                    ? (int?)value
                    : null)
            .Where(value => value is >= 0 and <= 100)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();
        if (spacedDigitValues.Length == 1)
        {
            return KnownInteger(
                spacedDigitValues[0],
                locator,
                string.Join(" | ", recognized.Lines),
                evidence,
                frame.CapturedAt);
        }

        return Observation<int>.Unknown(
            $"{locator} OCR 未从生命值上下文得到唯一数字",
            [evidence with
            {
                Locator = $"ocr:{locator}",
                Summary = string.Join(" | ", recognized.Lines)
            }],
            frame.CapturedAt);
    }

    private static Observation<int> KnownInteger(
        int value,
        string locator,
        string recognizedText,
        EvidenceReference evidence,
        DateTimeOffset observedAt) => Observation<int>.Known(
        value,
        0.7,
        [evidence with
        {
            Locator = $"ocr:{locator}",
            Summary = recognizedText
        }],
        observedAt);

    private async Task<Observation<long>> ReadDamageTotalAsync(
        CaptureFrame frame,
        IReadOnlyList<PixelRect> referenceRegions,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        if (!ocr.IsAvailable)
        {
            return Observation<long>.Unknown(
                "Windows 中文 OCR 不可用",
                [evidence],
                frame.CapturedAt);
        }

        var values = new List<long>();
        var summaries = new List<string>();
        for (var index = 0; index < referenceRegions.Count; index++)
        {
            var recognized = await ocr.RecognizeAsync(
                    frame,
                    Scale(referenceRegions[index], frame),
                    cancellationToken)
                .ConfigureAwait(false);
            var parsed = recognized.Lines
                .Prepend(recognized.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .SelectMany(ParseDamageCandidates)
                .OrderByDescending(candidate => candidate.Score)
                .ToArray();
            if (parsed.Length == 0)
            {
                if (index == referenceRegions.Count - 1 &&
                    LooksLikeZeroDamageGlyph(frame))
                {
                    values.Add(0);
                    summaries.Add($"row{index + 1}:pixel:hollow-zero-glyph");
                    continue;
                }

                return Observation<long>.Unknown(
                    $"结算伤害第 {index + 1} 行 OCR 未得到可解析数值",
                    [evidence with
                    {
                        Locator = $"ocr:challenge-success-damage-row-{index + 1}",
                        Summary = string.Join(" | ", recognized.Lines)
                    }],
                    frame.CapturedAt);
            }

            var bestScore = parsed[0].Score;
            var best = parsed
                .Where(candidate => candidate.Score == bestScore)
                .Select(candidate => candidate.Value)
                .Distinct()
                .ToArray();
            if (best.Length != 1)
            {
                return Observation<long>.Unknown(
                    $"结算伤害第 {index + 1} 行 OCR 存在同置信度冲突",
                    [evidence with
                    {
                        Locator = $"ocr:challenge-success-damage-row-{index + 1}",
                        Summary = string.Join(" | ", recognized.Lines)
                    }],
                    frame.CapturedAt);
            }

            values.Add(best[0]);
            summaries.Add($"row{index + 1}:{parsed[0].Text}");
        }

        var total = values.Sum();
        return Observation<long>.Known(
            total,
            0.7,
            [evidence with
            {
                Locator = "ocr:challenge-success-damage",
                Summary = string.Join("; ", summaries)
            }],
            frame.CapturedAt);
    }

    private static bool LooksLikeZeroDamageGlyph(CaptureFrame frame)
    {
        var region = Scale(ChallengeSuccessZeroDamageGlyphRegion, frame);
        var foreground = new bool[region.Height, region.Width];
        for (var y = region.Y; y < region.Bottom; y++)
        {
            for (var x = region.X; x < region.Right; x++)
            {
                var offset = y * frame.Stride + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                var minimum = Math.Min(red, Math.Min(green, blue));
                var maximum = Math.Max(red, Math.Max(green, blue));
                var luminance = (red + green + blue) / 3;
                if (luminance >= 115 && maximum - minimum <= 65)
                {
                    foreground[y - region.Y, x - region.X] = true;
                }
            }
        }

        var visited = new bool[region.Height, region.Width];
        var components = new List<IReadOnlyList<PixelPoint>>();
        for (var y = 0; y < region.Height; y++)
        {
            for (var x = 0; x < region.Width; x++)
            {
                if (!foreground[y, x] || visited[y, x])
                {
                    continue;
                }

                var component = new List<PixelPoint>();
                var queue = new Queue<PixelPoint>();
                queue.Enqueue(new PixelPoint(x, y));
                visited[y, x] = true;
                while (queue.Count > 0)
                {
                    var point = queue.Dequeue();
                    component.Add(point);
                    foreach (var neighbor in new[]
                             {
                                 new PixelPoint(point.X - 1, point.Y),
                                 new PixelPoint(point.X + 1, point.Y),
                                 new PixelPoint(point.X, point.Y - 1),
                                 new PixelPoint(point.X, point.Y + 1)
                             })
                    {
                        if (neighbor.X < 0 ||
                            neighbor.Y < 0 ||
                            neighbor.X >= region.Width ||
                            neighbor.Y >= region.Height ||
                            visited[neighbor.Y, neighbor.X] ||
                            !foreground[neighbor.Y, neighbor.X])
                        {
                            continue;
                        }

                        visited[neighbor.Y, neighbor.X] = true;
                        queue.Enqueue(neighbor);
                    }
                }

                components.Add(component);
            }
        }

        var points = components
            .OrderByDescending(component => component.Count)
            .FirstOrDefault() ?? [];
        if (points.Count < Math.Max(18, region.Width * region.Height / 40))
        {
            return false;
        }

        var left = points.Min(point => point.X);
        var right = points.Max(point => point.X);
        var top = points.Min(point => point.Y);
        var bottom = points.Max(point => point.Y);
        var width = right - left + 1;
        var height = bottom - top + 1;
        if (width < Math.Max(5, region.Width / 5) ||
            height < Math.Max(10, region.Height / 3) ||
            width > region.Width * 9 / 10 ||
            height > region.Height * 9 / 10)
        {
            return false;
        }

        var centerLeft = left + width * 3 / 10;
        var centerRight = right - width * 3 / 10;
        var centerTop = top + height * 3 / 10;
        var centerBottom = bottom - height * 3 / 10;
        var centerPoints = points.Count(point =>
            point.X >= centerLeft &&
            point.X <= centerRight &&
            point.Y >= centerTop &&
            point.Y <= centerBottom);
        var centerArea = Math.Max(
            1,
            (centerRight - centerLeft + 1) *
            (centerBottom - centerTop + 1));
        if (centerPoints * 5 >= centerArea * 3)
        {
            return false;
        }

        var upper = top + height / 4;
        var lower = bottom - height / 4;
        var innerLeft = left + width / 3;
        var innerRight = right - width / 3;
        return points.Any(point => point.Y <= upper && point.X >= innerLeft && point.X <= innerRight) &&
               points.Any(point => point.Y >= lower && point.X >= innerLeft && point.X <= innerRight) &&
               points.Any(point => point.X <= left + width / 3 && point.Y >= upper && point.Y <= lower) &&
               points.Any(point => point.X >= right - width / 3 && point.Y >= upper && point.Y <= lower);
    }

    private static IEnumerable<DamageCandidate> ParseDamageCandidates(string text)
    {
        foreach (Match match in DamagePattern().Matches(text))
        {
            var rawNumber = match.Groups["number"].Value;
            var hasDecimalSeparator = rawNumber.IndexOfAny(
                ['.', ',', '，', '·']) >= 0;
            var hasWhitespaceBetweenDigits = Regex.IsMatch(
                rawNumber,
                "[0-9]\\s+[0-9]",
                RegexOptions.CultureInvariant);
            if (hasWhitespaceBetweenDigits && !hasDecimalSeparator)
            {
                continue;
            }

            var numericText = rawNumber
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace(',', '.')
                .Replace('，', '.')
                .Replace('·', '.');
            if (!decimal.TryParse(
                    numericText,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var numericValue))
            {
                continue;
            }

            var multiplier = match.Groups["unit"].Value switch
            {
                "万" => 10_000m,
                "亿" or "億" => 100_000_000m,
                _ => 1m
            };
            var scaled = numericValue * multiplier;
            if (scaled is < 0 or > 10_000_000_000m)
            {
                continue;
            }

            var value = decimal.ToInt64(decimal.Round(
                scaled,
                0,
                MidpointRounding.AwayFromZero));
            var hasUnit = match.Groups["unit"].Length > 0;
            var score = (hasUnit ? 2 : 0) +
                        (hasDecimalSeparator ? 2 : 0) +
                        (rawNumber.Trim().Length > 1 ? 1 : 0);
            yield return new DamageCandidate(value, score, match.Value.Trim());
        }
    }

    private sealed record DamageCandidate(long Value, int Score, string Text);

    private async Task<Observation<int>> ReadPreparationEconomyAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var attempts = new List<EvidenceReference>();
        var candidates = new List<(
            string Source,
            int Value,
            double Confidence,
            int DigitCount,
            EvidenceReference Evidence)>();
        if (_phase2IconTemplates.Count > 0)
        {
            foreach (var region in new[]
                     {
                         (Name: "wide", Value: Phase2RecognitionRegions.EconomyValue),
                         (Name: "narrow", Value: Phase2RecognitionRegions.EconomyValueNarrow)
                     })
            {
                (int Value, double Confidence, int DigitCount, EvidenceReference Evidence)? best = null;
                foreach (var foregroundStyle in new[]
                         {
                             UiDigitForegroundStyle.DarkOnLight,
                             UiDigitForegroundStyle.BrightOnDark
                         })
                {
                    var localized = _uiDigitRecognizer.Recognize(
                        frame,
                        region.Value.ToPixels(frame.Width, frame.Height),
                        _phase2IconTemplates,
                        0,
                        999,
                        foregroundStyle);
                    var attempt = evidence with
                    {
                        Locator = $"template:economy-{region.Name}-candidate",
                        Summary = $"style={foregroundStyle}; " +
                                  $"value={localized.Value?.ToString() ?? "unknown"}; " +
                                  $"confidence={localized.Confidence:F3}; " +
                                  $"runner-up={localized.RunnerUpConfidence:F3}; " +
                                  $"reason={localized.FailureReason}",
                        Confidence = localized.Confidence
                    };
                    attempts.Add(attempt);
                    if (localized.IsRecognized &&
                        localized.Confidence >= 0.58 &&
                        (best is null || localized.Confidence > best.Value.Confidence))
                    {
                        best = (
                            localized.Value!.Value,
                            localized.Confidence,
                            localized.Glyphs.Count,
                            attempt);
                    }
                }

                if (best is { } selected)
                {
                    candidates.Add((
                        $"template-{region.Name}",
                        selected.Value,
                        selected.Confidence,
                        selected.DigitCount,
                        selected.Evidence));
                }
            }
        }

        if (candidates.Count > 0 &&
            candidates.Select(item => item.Value).Distinct().Count() == 1)
        {
            var selected = candidates.OrderByDescending(item => item.Confidence).First();
            return Observation<int>.Known(
                selected.Value,
                Math.Min(0.72, selected.Confidence),
                attempts,
                frame.CapturedAt);
        }

        var glyphRanked = candidates
            .OrderByDescending(item => item.DigitCount)
            .ThenByDescending(item => item.Confidence)
            .ToArray();
        if (glyphRanked.Length >= 2 &&
            glyphRanked[0].DigitCount > glyphRanked[1].DigitCount &&
            (glyphRanked[0].Value.ToString(CultureInfo.InvariantCulture)
                .EndsWith(
                    glyphRanked[1].Value.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) ||
             glyphRanked[0].Value.ToString(CultureInfo.InvariantCulture)
                .StartsWith(
                    glyphRanked[1].Value.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)))
        {
            return new Observation<int>
            {
                Status = ObservationStatus.Known,
                Value = glyphRanked[0].Value,
                Confidence = Math.Min(
                    0.70,
                    Math.Max(glyphRanked[0].Confidence, glyphRanked[1].Confidence)),
                Evidence = attempts.Distinct().ToArray(),
                Uncertainty =
                [
                    "重叠金币区域候选不一致；采用包含更多完整数字字形的候选。"
                ],
                ObservedAt = frame.CapturedAt
            };
        }

        var wideOcrTask = ReadIntegerAsync(
                frame,
                EconomyTightOcrRegion,
                0,
                999,
                "economy-wide",
                evidence,
                cancellationToken,
                _numericOcr,
                robust: true);
        var narrowOcrTask = ReadIntegerAsync(
                frame,
                EconomyNarrowOcrRegion,
                0,
                999,
                "economy-narrow",
                evidence,
                cancellationToken,
                _numericOcr,
                robust: true);
        await Task.WhenAll(wideOcrTask, narrowOcrTask).ConfigureAwait(false);
        foreach (var item in new[]
                 {
                     (Source: "ocr-wide", Observation: wideOcrTask.Result),
                     (Source: "ocr-narrow", Observation: narrowOcrTask.Result)
                 })
        {
            attempts.AddRange(item.Observation.Evidence);
            if (item.Observation.Status == ObservationStatus.Known)
            {
                candidates.Add((
                    item.Source,
                    item.Observation.Value,
                    item.Observation.Confidence,
                    item.Observation.Value.ToString(CultureInfo.InvariantCulture).Length,
                    item.Observation.Evidence.FirstOrDefault() ?? evidence));
            }
        }

        var ranked = candidates
            .GroupBy(item => item.Value)
            .Select(group => new
            {
                Value = group.Key,
                Votes = group.Select(item => item.Source).Distinct().Count(),
                Confidence = group.Max(item => item.Confidence)
            })
            .OrderByDescending(item => item.Votes)
            .ThenByDescending(item => item.Confidence)
            .ToArray();
        if (ranked.Length > 0 &&
            (ranked.Length == 1 || ranked[0].Votes > ranked[1].Votes))
        {
            return new Observation<int>
            {
                Status = ObservationStatus.Known,
                Value = ranked[0].Value,
                Confidence = Math.Min(0.72, ranked[0].Confidence),
                Evidence = attempts.Distinct().ToArray(),
                Uncertainty = ranked.Length > 1
                    ? ["重叠金币区域候选不一致；已按独立模板/OCR证据多数选择。"]
                    : [],
                ObservedAt = frame.CapturedAt
            };
        }

        return Observation<int>.Unknown(
            ranked.Length == 0
                ? "金币模板与 OCR 均未得到有效数字。"
                : "重叠金币区域得到票数相同的冲突结果，未猜测。",
            attempts.Distinct().ToArray(),
            frame.CapturedAt);
    }

    private async Task<Observation<int>> ReadPreparationHealthAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        // Health uses a different glyph treatment from the economy digits.
        // The heart icon gives the neural OCR enough context to read the
        // stylized digits reliably (84), while the generic digit templates can
        // confuse the same glyphs with 67. Use this bounded contextual crop as
        // the fast primary path and retain value-only crops as fallbacks.
        var contextualOcrObservation = await ReadIntegerAsync(
                frame,
                HealthOcrRegion,
                0,
                100,
                "health-context",
                evidence,
                cancellationToken,
                _numericOcr,
                robust: true)
            .ConfigureAwait(false);
        if (contextualOcrObservation.Status == ObservationStatus.Known)
        {
            return contextualOcrObservation;
        }

        var ocrObservation = await ReadIntegerAsync(
                frame,
                HealthTightOcrRegion,
                0,
                100,
                "health",
                evidence,
                cancellationToken,
                _numericOcr,
                robust: true)
            .ConfigureAwait(false);
        if (ocrObservation.Status == ObservationStatus.Known)
        {
            return ocrObservation;
        }

        var enlargedHealth = CaptureFramePreprocessor.CreateEnlargedCrop(
            frame,
            Scale(HealthTightOcrRegion, frame),
            scale: 4);
        var enlargedOcrObservation = await ReadIntegerAsync(
                enlargedHealth,
                new PixelRect(0, 0, 1920, 1080),
                0,
                100,
                "health-enlarged",
                evidence,
                cancellationToken,
                _numericOcr,
                robust: true)
            .ConfigureAwait(false);
        if (enlargedOcrObservation.Status == ObservationStatus.Known)
        {
            return enlargedOcrObservation;
        }

        var localizedEvidence = new List<EvidenceReference>();
        if (_phase2IconTemplates.Count > 0)
        {
            var localized = _uiDigitRecognizer.Recognize(
                frame,
                Phase2RecognitionRegions.PreparationHealthValue.ToPixels(
                    frame.Width,
                    frame.Height),
                _phase2IconTemplates,
                0,
                100,
                UiDigitForegroundStyle.BrightOnDark);
            localizedEvidence.Add(evidence with
            {
                Locator = "template:health-localized-digits-candidate",
                Summary = $"value={localized.Value?.ToString() ?? "unknown"}; " +
                          $"confidence={localized.Confidence:F3}; " +
                          $"runner-up={localized.RunnerUpConfidence:F3}; " +
                          $"reason={localized.FailureReason}",
                Confidence = localized.Confidence
            });
            if (localized.IsRecognized &&
                localized.Confidence >= 0.75 &&
                localized.Confidence - localized.RunnerUpConfidence >= 0.08)
            {
                return Observation<int>.Known(
                    localized.Value!.Value,
                    Math.Min(0.72, localized.Confidence),
                    [evidence with
                    {
                        Locator = "template:health-localized-digits",
                        Summary = $"value={localized.Value.Value}; " +
                                  $"confidence={localized.Confidence:F3}; " +
                                  $"runner-up={localized.RunnerUpConfidence:F3}",
                        Confidence = localized.Confidence
                    }],
                    frame.CapturedAt);
            }
        }

        var combinedOcr = enlargedOcrObservation with
        {
            Evidence = enlargedOcrObservation.Evidence
                .Concat(contextualOcrObservation.Evidence)
                .Concat(ocrObservation.Evidence)
                .ToArray(),
            Uncertainty = enlargedOcrObservation.Uncertainty
                .Concat(contextualOcrObservation.Uncertainty)
                .Concat(ocrObservation.Uncertainty)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        return localizedEvidence.Count == 0
            ? combinedOcr
            : combinedOcr with
            {
                Evidence = combinedOcr.Evidence
                    .Concat(localizedEvidence)
                    .ToArray()
            };
    }

    private async Task<Observation<int>> ReadIntegerAsync(
        CaptureFrame frame,
        PixelRect referenceRegion,
        int minimum,
        int maximum,
        string locator,
        EvidenceReference evidence,
        CancellationToken cancellationToken,
        IOfflineOcr? preferredOcr = null,
        bool robust = false)
    {
        var selectedOcr = preferredOcr ?? ocr;
        if (!selectedOcr.IsAvailable)
        {
            return Observation<int>.Unknown(
                "Windows 中文 OCR 不可用",
                [evidence],
                frame.CapturedAt);
        }

        var recognized = robust && selectedOcr is IAdaptiveOfflineOcr adaptive
            ? await adaptive.RecognizeRobustAsync(
                    frame,
                    Scale(referenceRegion, frame),
                    cancellationToken)
                .ConfigureAwait(false)
            : await selectedOcr.RecognizeAsync(
                    frame,
                    Scale(referenceRegion, frame),
                    cancellationToken)
                .ConfigureAwait(false);
        var values = recognized.Lines
            .Prepend(recognized.Text)
            .SelectMany(text => IntegerPattern().Matches(text)
                .Select(match => int.TryParse(
                    match.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value)
                        ? (int?)value
                        : null))
            .Where(value => value is >= 0)
            .Select(value => value!.Value)
            .Where(value => value >= minimum && value <= maximum)
            .Distinct()
            .ToArray();
        return values.Length == 1
            ? Observation<int>.Known(
                values[0],
                0.65,
                [evidence with
                {
                    Locator = $"ocr:{locator}",
                    Summary = recognized.Text
                }],
                frame.CapturedAt)
            : Observation<int>.Unknown(
                values.Length == 0
                    ? $"{locator} OCR 未得到唯一数字"
                    : $"{locator} OCR 得到多个冲突数字：{string.Join(",", values)}",
                [evidence with { Locator = $"ocr:{locator}" }],
                frame.CapturedAt);
    }

    private static Observation<IReadOnlyList<string>> ObserveCharacters(
        IReadOnlyList<CharacterCardSlotRecognition> slots,
        EvidenceReference evidence,
        string locator,
        ICollection<string> warnings,
        DateTimeOffset observedAt)
    {
        var recognized = slots
            .Where(slot =>
                slot.State == CharacterCardSlotState.Recognized &&
                slot.CharacterId is not null)
            .ToArray();
        var uncertain = slots.Count(slot => slot.State == CharacterCardSlotState.Uncertain);
        if (uncertain > 0)
        {
            warnings.Add($"{locator} 有 {uncertain} 个槽位状态不确定，未将其猜成具体角色。");
        }
        var confidence = recognized.Length == 0
            ? (uncertain == 0 ? 1 : 0)
            : recognized.Average(slot => slot.Confidence) *
              (uncertain == 0 ? 1 : 0.75);
        return Observation<IReadOnlyList<string>>.Known(
            recognized.Select(slot => slot.CharacterId!).ToArray(),
            confidence,
            [evidence with
            {
                Locator = $"vision:{locator}-slots",
                Summary = string.Join(
                    ", ",
                    slots.Select(slot =>
                        $"{slot.SlotIndex + 1}:{slot.State}:" +
                        $"{slot.DisplayName ?? "-"}:{slot.Confidence:F3}"))
            }],
            observedAt);
    }

    private static RunSnapshot UnknownSnapshot(
        string runId,
        DateTimeOffset observedAt) => new()
    {
        RunId = runId,
        AsOf = observedAt,
        CumulativeSpend = Observation<int>.Unknown(
            "单张截图无法建立累计花费历史",
            observedAt: observedAt)
    };

    private static string MapStage(string pageId) => pageId switch
    {
        "preparation_1_1" => "preparation_1_1",
        "preparation_1_2" => "preparation_1_2",
        "reward_shop" => "reward_shop",
        "reward_battle" or "reward_battle_pause" or "battle_generic" => "reward_battle",
        "challenge_success" => "node_complete",
        "challenge_failed" or "challenge_health_depleted" => "node_failed",
        "enemy_overview" => "opening_enemy_overview",
        "investment_environment" => "opening_investment_environment",
        "investment_strategy" => "investment_strategy_selection",
        _ => pageId
    };

    private static PixelRect Scale(PixelRect source, CaptureFrame frame) => new(
        (int)Math.Round(source.X * frame.Width / 1920d),
        (int)Math.Round(source.Y * frame.Height / 1080d),
        (int)Math.Round(source.Width * frame.Width / 1920d),
        (int)Math.Round(source.Height * frame.Height / 1080d));

    private static IReadOnlyList<string> UnknownFields(RunSnapshot snapshot)
    {
        var fields = new List<string>();
        Add(fields, "page", snapshot.PageId.Status);
        Add(fields, "stage", snapshot.Stage.Status);
        Add(fields, "economy", snapshot.Economy.Status);
        Add(fields, "cumulativeSpend", snapshot.CumulativeSpend.Status);
        Add(fields, "health", snapshot.Health.Status);
        Add(fields, "actionPoints", snapshot.ActionPoints.Status);
        Add(fields, "damage", snapshot.CurrentNodeDamage.Status);
        Add(fields, "board", snapshot.BoardCharacterIds.Status);
        Add(fields, "bench", snapshot.BenchCharacterIds.Status);
        Add(fields, "shop", snapshot.ShopCharacterIds.Status);
        Add(fields, "investmentEnvironment", snapshot.InvestmentEnvironmentId.Status);
        Add(fields, "investmentStrategies", snapshot.InvestmentStrategyIds.Status);
        Add(fields, "equipment", snapshot.EquipmentIds.Status);
        Add(fields, "inventorySlots", snapshot.InventorySlots.Status);
        Add(fields, "specialItems", snapshot.SpecialItemIds.Status);
        Add(fields, "expertAdvisors", snapshot.ExpertAdvisorIds.Status);
        Add(fields, "enemies", snapshot.EnemyIds.Status);
        return fields;
    }

    private static void Add(
        ICollection<string> fields,
        string field,
        ObservationStatus status)
    {
        if (status != ObservationStatus.Known)
        {
            fields.Add($"{field}({status})");
        }
    }

    private static async Task<TimedRecognition<T>> MeasureAsync<T>(
        Func<Task<T>> operation)
    {
        var started = Stopwatch.GetTimestamp();
        var value = await operation().ConfigureAwait(false);
        return new TimedRecognition<T>(
            value,
            Stopwatch.GetElapsedTime(started));
    }

    private sealed record TimedRecognition<T>(T Value, TimeSpan Elapsed);

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(
        "(?<number>(?:[0-9]\\s*)+(?:[.,，·]\\s*(?:[0-9]\\s*)+)?)\\s*(?<unit>[万亿億]?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DamagePattern();
}
