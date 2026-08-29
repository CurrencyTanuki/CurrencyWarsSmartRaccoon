using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed record Phase2BatchRecognitionItem(
    string RecognitionObject,
    string SlotKey,
    ObservationStatus Status,
    IReadOnlyList<string> RawOcrTexts,
    string? StandardId,
    string? StandardName,
    double Confidence,
    IReadOnlyList<RelativeRegion> RecognitionRegions,
    string EvidenceKind,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> Conflicts,
    string? TemporaryId = null,
    IReadOnlyDictionary<string, string>? RecognizedFields = null,
    string? FailureReason = null,
    bool CanDriveDecisions = true);

public sealed record Phase2BatchImageReport(
    string FileName,
    string SourcePath,
    int Width,
    int Height,
    Phase2PageFamily PageType,
    double AnalysisElapsedMilliseconds,
    IReadOnlyList<Phase2BatchRecognitionItem> Recognitions,
    IReadOnlyList<Phase2BatchRecognitionItem> UnknownItems,
    IReadOnlyList<Phase2BatchRecognitionItem> ConflictItems,
    IReadOnlyList<string> Diagnostics,
    string AnnotatedImagePath,
    string? Error = null,
    bool? PersistentStateConfirmed = null,
    FinalNodeBattleState? FinalizedBattle = null,
    string? TrackingMessage = null,
    string? ClassifiedPageId = null,
    ObservationStatus PageRecognitionStatus = ObservationStatus.Unknown,
    double PageConfidence = 0);

public sealed record Phase2BatchReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string SourceDirectory,
    string OutputDirectory,
    bool WritesFormalRunRecords,
    IReadOnlyList<Phase2BatchImageReport> Images);

public sealed class Phase2BatchImageAnalysisService(
    Phase2OperationalScreenshotAnalyzer analyzer,
    ISituationScreenshotAnalyzer? situationAnalyzer = null,
    TimeSpan? perImageTimeout = null)
{
    private readonly TimeSpan _perImageTimeout =
        perImageTimeout ?? TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions ReportJsonOptions = new(
        AdvisorJson.Options)
    {
        WriteIndented = true
    };

    public async Task<Phase2BatchReport> AnalyzeDirectoryAsync(
        string sourceDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        bool continuousSequence = false,
        bool writeAnnotations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var source = Path.GetFullPath(sourceDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"批量测试图片目录不存在：{source}");
        }

        if (IsUnder(output, source))
        {
            throw new InvalidDataException(
                "批量测试输出目录不能位于输入图集目录中，避免下一轮把标注图再次当成输入。");
        }

        Directory.CreateDirectory(output);
        var annotatedDirectory = Path.Combine(output, "annotated");
        if (writeAnnotations)
        {
            Directory.CreateDirectory(annotatedDirectory);
        }
        var progressPath = Path.Combine(output, "phase2-batch-progress.jsonl");
        var files = Directory.EnumerateFiles(source)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sequenceRunId =
            $"batch-test-no-formal-record:sequence:{Guid.NewGuid():N}";
        var sequenceTracker = continuousSequence
            ? new Phase2OperationalStateTracker()
            : null;
        var reports = new List<Phase2BatchImageReport>(files.Length);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            await AppendProgressAsync(
                    progressPath,
                    Path.GetFileName(path),
                    "started",
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var frame = CaptureFrameLoader.LoadFile(path);
                var evidenceSourceId =
                    $"batch-test:{Path.GetFileName(path)}";
                var analysisRunId = continuousSequence
                    ? sequenceRunId
                    : $"batch-test-no-formal-record:{Path.GetFileName(path)}";
                Phase2OperationalState operational;
                RunSnapshot snapshot;
                IReadOnlyList<string> diagnostics;
                if (situationAnalyzer is not null)
                {
                    var analysis = await situationAnalyzer.AnalyzeAsync(
                            frame,
                            evidenceSourceId,
                            new AdvisorSelection(
                                AdvisorMode.Auto,
                                "stable",
                                "4.4"),
                            cancellationToken,
                            analysisRunId)
                        .WaitAsync(_perImageTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    operational = analysis.OperationalState ?? new Phase2OperationalState
                    {
                        PageFamily = Phase2PageFamily.Unknown,
                        Diagnostics = analysis.Warnings
                    };
                    snapshot = analysis.Snapshot;
                    diagnostics = analysis.Warnings;
                }
                else
                {
                    snapshot = EmptySnapshot(
                        frame.CapturedAt,
                        analysisRunId);
                    operational = await analyzer.AnalyzeAsync(
                            frame,
                            "unknown",
                            evidenceSourceId,
                            snapshot,
                            cancellationToken)
                        .WaitAsync(_perImageTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    diagnostics = operational.Diagnostics;
                }

                stopwatch.Stop();
                var tracking = sequenceTracker?.Observe(
                    operational,
                    snapshot.Health);
                var recognitions = CreateRecognitions(operational, snapshot);
                var annotatedPath = string.Empty;
                if (writeAnnotations)
                {
                    var annotatedName =
                        $"{Path.GetFileNameWithoutExtension(path)}.annotated.png";
                    annotatedPath = Path.Combine(annotatedDirectory, annotatedName);
                    await Task.Run(
                            () => SaveAnnotatedImage(
                                frame,
                                recognitions,
                                annotatedPath),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                reports.Add(new Phase2BatchImageReport(
                    Path.GetFileName(path),
                    path,
                    frame.Width,
                    frame.Height,
                    operational.PageFamily,
                    stopwatch.Elapsed.TotalMilliseconds,
                    recognitions,
                    recognitions.Where(item =>
                        item.Status == ObservationStatus.Unknown).ToArray(),
                    recognitions.Where(item =>
                        item.Status == ObservationStatus.Conflict).ToArray(),
                    diagnostics,
                    writeAnnotations
                        ? Path.GetRelativePath(output, annotatedPath)
                            .Replace('\\', '/')
                        : string.Empty,
                    PersistentStateConfirmed:
                        tracking?.PersistentStateConfirmed,
                    FinalizedBattle: tracking?.FinalizedBattle,
                    TrackingMessage: tracking?.Message,
                    ClassifiedPageId: snapshot.PageId.Value,
                    PageRecognitionStatus: snapshot.PageId.Status,
                    PageConfidence: snapshot.PageId.Confidence));
                await AppendProgressAsync(
                        progressPath,
                        Path.GetFileName(path),
                        "completed",
                        stopwatch.Elapsed.TotalMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                reports.Add(new Phase2BatchImageReport(
                    Path.GetFileName(path),
                    path,
                    0,
                    0,
                    Phase2PageFamily.Unknown,
                    stopwatch.Elapsed.TotalMilliseconds,
                    [],
                    [],
                    [],
                    [$"批量识别单图失败：{exception.GetType().Name}: {exception.Message}"],
                    string.Empty,
                    $"{exception.GetType().FullName}: {exception.Message}"));
                await AppendProgressAsync(
                        progressPath,
                        Path.GetFileName(path),
                        exception is TimeoutException ? "timed-out" : "failed",
                        stopwatch.Elapsed.TotalMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var report = new Phase2BatchReport(
            "1.0.0",
            DateTimeOffset.UtcNow,
            source,
            output,
            false,
            reports);
        if (continuousSequence)
        {
            analyzer.EndRunRecognition(sequenceRunId);
        }
        await File.WriteAllTextAsync(
                Path.Combine(output, "phase2-batch-report.json"),
                JsonSerializer.Serialize(report, ReportJsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        var jsonLines = reports.Select(item =>
            JsonSerializer.Serialize(item, AdvisorJson.Options));
        await File.WriteAllLinesAsync(
                Path.Combine(output, "phase2-batch-report.jsonl"),
                jsonLines,
                cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    private static Task AppendProgressAsync(
        string progressPath,
        string fileName,
        string stage,
        double? elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        var entry = JsonSerializer.Serialize(
            new
            {
                timestamp = DateTimeOffset.UtcNow,
                fileName,
                stage,
                elapsedMilliseconds
            },
            AdvisorJson.Options);
        return File.AppendAllTextAsync(
            progressPath,
            entry + Environment.NewLine,
            cancellationToken);
    }

    private static IReadOnlyList<Phase2BatchRecognitionItem> CreateRecognitions(
        Phase2OperationalState state,
        RunSnapshot? snapshot = null)
    {
        var results = state.NamedContent.Select(item =>
                new Phase2BatchRecognitionItem(
                    item.Kind.ToString(),
                    item.SlotKey,
                    item.Status,
                    item.RawOcrTexts,
                    item.ObjectId,
                    item.StandardName,
                    item.Confidence,
                    [item.Region],
                    item.EvidenceKind.ToString(),
                    item.CandidateIds,
                    item.Conflicts))
            .ToList();
        AddOperationalFields(results, state, snapshot);
        if (state.BattleDamage.Value is not null)
        {
            results.AddRange(state.BattleDamage.Value.Select(item =>
                new Phase2BatchRecognitionItem(
                    "CharacterDamage",
                    $"damage-row-{item.Rank}",
                    item.CanDriveDecisions &&
                    item.CharacterId is not null &&
                    !item.CharacterId.StartsWith("unknown-", StringComparison.Ordinal)
                        ? ObservationStatus.Known
                        : ObservationStatus.Unknown,
                    [item.RawText],
                    item.CharacterId,
                    null,
                    Math.Min(item.AvatarConfidence, item.DamageConfidence),
                    [item.AvatarRegion, item.DamageRegion],
                    "CharacterAvatarAndOcr",
                    item.CharacterId is null ? [] : [item.CharacterId],
                    state.BattleDamage.Uncertainty,
                    item.TemporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["damage"] = item.Damage.ToString(CultureInfo.InvariantCulture)
                    },
                    item.FailureReason,
                    item.CanDriveDecisions)));
        }

        if (state.BattleSynergyDamage.Value is not null)
        {
            results.AddRange(state.BattleSynergyDamage.Value.Select(item =>
                new Phase2BatchRecognitionItem(
                    "SynergyDamage",
                    $"damage-row-{item.Rank}",
                    item.SynergyId is not null && item.CanDriveDecisions
                        ? ObservationStatus.Known
                        : ObservationStatus.Unknown,
                    [item.RawText],
                    item.SynergyId,
                    null,
                    Math.Min(item.IconConfidence, item.DamageConfidence),
                    [item.IconRegion, item.DamageRegion],
                    "BattleSynergyIconAndOcr",
                    item.SynergyId is null ? [] : [item.SynergyId],
                    state.BattleSynergyDamage.Uncertainty,
                    item.TemporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["damage"] = item.Damage.ToString(CultureInfo.InvariantCulture)
                    },
                    item.FailureReason,
                    item.CanDriveDecisions)));
        }

        if (state.BattleUnresolvedDamage.Value is not null)
        {
            results.AddRange(state.BattleUnresolvedDamage.Value.Select(item =>
                new Phase2BatchRecognitionItem(
                    item.SourceKind.ToString(),
                    $"damage-row-{item.Rank}",
                    ObservationStatus.Unknown,
                    [item.RawText],
                    item.SourceId,
                    null,
                    Math.Min(item.IconConfidence, item.DamageConfidence),
                    [item.IconRegion, item.DamageRegion],
                    "UnresolvedDamageSourceAndOcr",
                    item.CandidateIds,
                    [item.FailureReason],
                    item.TemporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["damage"] = item.Damage.ToString(CultureInfo.InvariantCulture),
                        ["sourceKind"] = item.SourceKind.ToString()
                    },
                    item.FailureReason,
                    item.CanDriveDecisions)));
        }

        if (state.SettlementDamage.Value is not null)
        {
            results.AddRange(state.SettlementDamage.Value.Select(item =>
                new Phase2BatchRecognitionItem(
                    "SettlementCharacterDamage",
                    $"settlement-damage-character-{item.Rank}",
                    item.CanDriveDecisions &&
                    item.CharacterId is not null &&
                    !item.CharacterId.StartsWith("unknown-", StringComparison.Ordinal)
                        ? ObservationStatus.Known
                        : ObservationStatus.Unknown,
                    [item.RawText],
                    item.CanDriveDecisions ? item.CharacterId : null,
                    null,
                    Math.Min(item.AvatarConfidence, item.DamageConfidence),
                    [item.AvatarRegion, item.DamageRegion],
                    "SettlementCharacterAvatarAndOcr",
                    item.CandidateCharacterIds ?? [],
                    string.IsNullOrWhiteSpace(item.FailureReason)
                        ? state.SettlementDamage.Uncertainty
                        : [item.FailureReason],
                    item.TemporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["damage"] = item.Damage.ToString(CultureInfo.InvariantCulture),
                        ["rank"] = item.Rank.ToString(CultureInfo.InvariantCulture)
                    },
                    item.FailureReason,
                    item.CanDriveDecisions)));
        }

        if (state.SettlementGoldReward.Status == ObservationStatus.Known ||
            state.SettlementGoldReward.Evidence.Count > 0)
        {
            results.Add(new Phase2BatchRecognitionItem(
                "SettlementGoldReward",
                "settlement-gold-reward",
                state.SettlementGoldReward.Status,
                state.SettlementGoldReward.Evidence
                    .Select(item => item.Summary)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray(),
                null,
                null,
                state.SettlementGoldReward.Confidence,
                [
                    ToRelative(Phase2RecognitionRegions.SettlementGoldReward),
                    ToRelative(Phase2RecognitionRegions.SettlementGoldRewardLabeledRow)
                ],
                "SettlementNumericOcr",
                [],
                state.SettlementGoldReward.Uncertainty,
                RecognizedFields: state.SettlementGoldReward.Status == ObservationStatus.Known
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["goldReward"] = state.SettlementGoldReward.Value
                            .ToString(CultureInfo.InvariantCulture)
                    }
                    : null,
                FailureReason: state.SettlementGoldReward.Uncertainty.FirstOrDefault(),
                CanDriveDecisions: state.SettlementGoldReward.Status == ObservationStatus.Known));
        }

        AddDamageCandidate(
            results,
            "BattleScreenDamageCandidate",
            "battle-screen-damage-candidate",
            state.BattleScreenDamageCandidate,
            ToRelative(Phase2RecognitionRegions.BattleDamagePanel));
        AddDamageCandidate(
            results,
            "SettlementScreenDamageCandidate",
            "settlement-screen-damage-candidate",
            state.SettlementScreenDamageCandidate,
            ToRelative(Phase2RecognitionRegions.SettlementDamagePanel));

        results.AddRange(state.PartialFields.Select(item =>
            new Phase2BatchRecognitionItem(
                "PartialField",
                item.Field,
                ObservationStatus.Unknown,
                item.RawTexts,
                null,
                null,
                item.Confidence,
                [item.Region],
                "PartialRegionEvidence",
                item.CandidateIds,
                [item.FailureReason],
                item.TemporaryId,
                item.RecognizedFields,
                item.FailureReason,
                item.CanDriveDecisions)));

        results.AddRange(state.PendingIcons
            .Where(pending => results.All(item =>
                !string.Equals(
                    item.SlotKey,
                    pending.SlotKey,
                    StringComparison.Ordinal)))
            .Select(item => new Phase2BatchRecognitionItem(
                item.Category.ToString(),
                item.SlotKey,
                item.Status.Contains("conflict", StringComparison.Ordinal)
                    ? ObservationStatus.Conflict
                    : ObservationStatus.Unknown,
                item.Evidence.Summary is null ? [] : [item.Evidence.Summary],
                null,
                null,
                item.Confidence,
                [item.Region],
                "Icon",
                item.CandidateTemplateIds ?? [],
                item.Status.Contains("conflict", StringComparison.Ordinal)
                    ? [item.Status]
                    : [],
                item.TemporaryId,
                item.RecognizedFields,
                item.Status,
                item.CanDriveDecisions)));
        return results;
    }

    private static void AddOperationalFields(
        ICollection<Phase2BatchRecognitionItem> results,
        Phase2OperationalState state,
        RunSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            results.Add(new Phase2BatchRecognitionItem(
                "Page",
                "page-id",
                snapshot.PageId.Status,
                snapshot.PageId.Value is null ? [] : [snapshot.PageId.Value],
                snapshot.PageId.Value,
                snapshot.PageId.Value,
                snapshot.PageId.Confidence,
                [new RelativeRegion(0, 0, 1, 1)],
                "PageClassifier",
                snapshot.PageId.Value is null ? [] : [snapshot.PageId.Value],
                snapshot.PageId.Uncertainty,
                FailureReason: snapshot.PageId.Uncertainty.FirstOrDefault(),
                CanDriveDecisions:
                    snapshot.PageId.Status == ObservationStatus.Known));
        }

        var nodeRegion = state.PageFamily switch
        {
            Phase2PageFamily.Battle =>
                ToRelative(Phase2RecognitionRegions.BattleNodeValue),
            Phase2PageFamily.BattleSettlement =>
                ToRelative(Phase2RecognitionRegions.SettlementNodeValue),
            _ => ToRelative(Phase2RecognitionRegions.PreparationNodeValue)
        };
        AddObservation(
            results,
            "NodeId",
            "node-id",
            state.NodeId,
            nodeRegion,
            "NumericOcr",
            value => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nodeId"] = value
            });
        AddObservation(
            results,
            "EnemyDifficulty",
            "enemy-difficulty",
            state.EnemyDifficulty,
            ToRelative(Phase2RecognitionRegions.PreparationDifficultyValue),
            "NumericOcr",
            value => IntegerField("enemyDifficulty", value));
        AddObservation(
            results,
            "Interest",
            "interest",
            state.Interest,
            ToRelative(Phase2RecognitionRegions.Interest),
            "NumericOcr",
            value => IntegerField("interest", value));
        AddObservation(
            results,
            "CumulativeSpend",
            "cumulative-spend",
            state.CumulativeSpend,
            ToRelative(Phase2RecognitionRegions.CumulativeSpend),
            "NumericOcr",
            value => IntegerField("cumulativeSpend", value));
        AddObservation(
            results,
            "PlayerProgress",
            "player-progress",
            state.PlayerProgress,
            ToRelative(Phase2RecognitionRegions.LevelAndExperience),
            "NumericOcr",
            value => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["level"] = value.Level.ToString(CultureInfo.InvariantCulture),
                ["experience"] = value.Experience.ToString(
                    CultureInfo.InvariantCulture),
                ["experienceToNextLevel"] = value.ExperienceToNextLevel
                    .ToString(CultureInfo.InvariantCulture)
            });
        AddObservation(
            results,
            "DismantleToolCount",
            "dismantle-tool-count",
            state.DismantleToolCount,
            ToRelative(Phase2RecognitionRegions.DismantleToolCountValue),
            "IconAndNumericOcr",
            value => IntegerField("count", value));
        AddObservation(
            results,
            "RemainingActionValue",
            "remaining-action-value",
            state.RemainingActionValue,
            ToRelative(Phase2RecognitionRegions.BattleActionTimeline),
            "PixelAndNumericOcr",
            value => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["remainingRounds"] = value.RemainingRounds.ToString(
                    CultureInfo.InvariantCulture),
                ["currentRoundActionValue"] = value.CurrentRoundActionValue
                    .ToString(CultureInfo.InvariantCulture),
                ["totalActionValue"] = value.TotalActionValue.ToString(
                    CultureInfo.InvariantCulture)
            });

        if (state.Formation.Status != ObservationStatus.Unknown ||
            state.Formation.Evidence.Count > 0)
        {
            var formation = state.Formation.Value ?? [];
            results.Add(new Phase2BatchRecognitionItem(
                "FormationSummary",
                "formation-summary",
                state.Formation.Status,
                EvidenceSummaries(state.Formation),
                null,
                null,
                state.Formation.Confidence,
                [ToRelative(Phase2RecognitionRegions.PreparationFront),
                    ToRelative(Phase2RecognitionRegions.PreparationBack),
                    ToRelative(Phase2RecognitionRegions.Bench)],
                "CharacterAvatarTemplate",
                [],
                state.Formation.Uncertainty,
                RecognizedFields: new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["characterCount"] = formation.Count.ToString(
                        CultureInfo.InvariantCulture)
                },
                FailureReason: state.Formation.Uncertainty.FirstOrDefault(),
                CanDriveDecisions: state.Formation.Status ==
                    ObservationStatus.Known));
            foreach (var character in formation)
            {
                var incomplete = !character.CanDriveDecisions ||
                                 character.CharacterId.StartsWith(
                                     "unknown-",
                                     StringComparison.Ordinal);
                results.Add(new Phase2BatchRecognitionItem(
                    "FormationCharacter",
                    $"formation-{character.Zone}-{character.SlotIndex + 1}",
                    incomplete
                        ? ObservationStatus.Unknown
                        : ObservationStatus.Known,
                    character.Evidence.Summary is null
                        ? []
                        : [character.Evidence.Summary],
                    incomplete ? null : character.CharacterId,
                    null,
                    character.Confidence,
                    [FormationRegion(character)],
                    "CharacterAvatarTemplate",
                    character.CandidateCharacterIds ??
                        (incomplete ? [] : [character.CharacterId]),
                    string.IsNullOrWhiteSpace(character.FailureReason)
                        ? []
                        : [character.FailureReason],
                    character.TemporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["zone"] = character.Zone.ToString(),
                        ["slotIndex"] = character.SlotIndex.ToString(
                            CultureInfo.InvariantCulture),
                        ["standing"] = character.Standing,
                        ["equipmentIds"] = string.Join(
                            ",",
                            character.EquipmentIds)
                    },
                    character.FailureReason,
                    character.CanDriveDecisions));
            }
        }

        if (state.SimpleEquipmentIds.Status == ObservationStatus.Known &&
            state.SimpleEquipmentIds.Value is { } equipmentIds)
        {
            for (var index = 0; index < equipmentIds.Count; index++)
            {
                results.Add(new Phase2BatchRecognitionItem(
                    "SimpleEquipment",
                    $"simple-equipment-known-{index + 1}",
                    ObservationStatus.Known,
                    EvidenceSummaries(state.SimpleEquipmentIds),
                    equipmentIds[index],
                    null,
                    state.SimpleEquipmentIds.Confidence,
                    [ToRelative(Phase2RecognitionRegions.InventoryIconSlots[
                        Math.Min(index + 1,
                            Phase2RecognitionRegions.InventoryIconSlots.Count - 1)])],
                    "IconTemplate",
                    [equipmentIds[index]],
                    []));
            }
        }

        if (snapshot is null)
        {
            return;
        }

        AddObservation(
            results,
            "Economy",
            "economy",
            snapshot.Economy,
            ToRelative(Phase2RecognitionRegions.Economy),
            "ExistingGoldDigitRecognizer",
            value => IntegerField("gold", value));
        AddObservation(
            results,
            "Health",
            "health",
            snapshot.Health,
            ToRelative(Phase2RecognitionRegions.PreparationHealth),
            "NumericOcr",
            value => IntegerField("health", value));
    }

    private static void AddObservation<T>(
        ICollection<Phase2BatchRecognitionItem> results,
        string recognitionObject,
        string slotKey,
        Observation<T> observation,
        RelativeRegion region,
        string evidenceKind,
        Func<T, IReadOnlyDictionary<string, string>> fields)
    {
        if (observation.Status == ObservationStatus.Unknown &&
            observation.Evidence.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string>? recognizedFields = null;
        if (observation.Status is ObservationStatus.Known or
            ObservationStatus.Stale && observation.Value is { } value)
        {
            recognizedFields = fields(value);
        }

        results.Add(new Phase2BatchRecognitionItem(
            recognitionObject,
            slotKey,
            observation.Status,
            EvidenceSummaries(observation),
            null,
            null,
            observation.Confidence,
            [region],
            evidenceKind,
            [],
            observation.Uncertainty,
            RecognizedFields: recognizedFields,
            FailureReason: observation.Uncertainty.FirstOrDefault(),
            CanDriveDecisions: observation.Status == ObservationStatus.Known));
    }

    private static IReadOnlyList<string> EvidenceSummaries<T>(
        Observation<T> observation) => observation.Evidence
        .Select(item => item.Summary)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Cast<string>()
        .ToArray();

    private static IReadOnlyDictionary<string, string> IntegerField(
        string name,
        int value) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [name] = value.ToString(CultureInfo.InvariantCulture)
    };

    private static RelativeRegion FormationRegion(
        FormationCharacterState character)
    {
        if (character.CardRegion is { } observedRegion)
        {
            return observedRegion;
        }

        PixelRect region;
        if (character.Zone == FormationZone.Bench)
        {
            region = Phase2RecognitionRegions.BenchCharacterSlots1920[
                Math.Clamp(
                    character.SlotIndex,
                    0,
                    Phase2RecognitionRegions.BenchCharacterSlots1920.Count - 1)];
        }
        else
        {
            region = Phase2RecognitionRegions.PreparationCharacterSlots1920[
                Math.Clamp(
                    character.SlotIndex,
                    0,
                    Phase2RecognitionRegions.PreparationCharacterSlots1920.Count - 1)];
        }

        return new RelativeRegion(
            region.X / 1920d,
            region.Y / 1080d,
            region.Width / 1920d,
            region.Height / 1080d);
    }

    private static void AddDamageCandidate(
        ICollection<Phase2BatchRecognitionItem> results,
        string recognitionObject,
        string slotKey,
        Observation<long> observation,
        RelativeRegion region)
    {
        if (observation.Status == ObservationStatus.Unknown &&
            observation.Evidence.Count == 0 &&
            observation.Value == 0)
        {
            return;
        }

        results.Add(new Phase2BatchRecognitionItem(
            recognitionObject,
            slotKey,
            observation.Status,
            observation.Evidence
                .Select(item => item.Summary)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray(),
            null,
            null,
            observation.Confidence,
            [region],
            "DerivedDamageCandidate",
            [],
            observation.Uncertainty,
            RecognizedFields: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["damage"] = observation.Value.ToString(CultureInfo.InvariantCulture)
            },
            FailureReason: observation.Uncertainty.FirstOrDefault(),
            CanDriveDecisions: observation.Status == ObservationStatus.Known));
    }

    private static RelativeRegion ToRelative(NormalizedRect region) => new(
        region.X,
        region.Y,
        region.Width,
        region.Height);

    private static void SaveAnnotatedImage(
        CaptureFrame frame,
        IReadOnlyList<Phase2BatchRecognitionItem> recognitions,
        string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(
                frame.ToBitmapSource(),
                new Rect(0, 0, frame.Width, frame.Height));
            for (var index = 0; index < recognitions.Count; index++)
            {
                var item = recognitions[index];
                var color = item.Status switch
                {
                    ObservationStatus.Known => Colors.LimeGreen,
                    ObservationStatus.Conflict => Colors.Red,
                    _ => Colors.Gold
                };
                var pen = new Pen(new SolidColorBrush(color), 3);
                foreach (var region in item.RecognitionRegions)
                {
                    var rectangle = new Rect(
                        region.X * frame.Width,
                        region.Y * frame.Height,
                        region.Width * frame.Width,
                        region.Height * frame.Height);
                    drawing.DrawRectangle(null, pen, rectangle);
                    var text = new FormattedText(
                        $"{index + 1}:{item.Status.ToString()[0]}",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        Math.Max(12, frame.Width / 160d),
                        new SolidColorBrush(color),
                        1);
                    drawing.DrawText(
                        text,
                        new Point(rectangle.X, Math.Max(0, rectangle.Y - text.Height)));
                }
            }
        }

        var bitmap = new RenderTargetBitmap(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static bool IsUnder(string candidate, string parent)
    {
        var parentPrefix = Path.TrimEndingDirectorySeparator(parent) +
                           Path.DirectorySeparatorChar;
        return candidate.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static RunSnapshot EmptySnapshot(
        DateTimeOffset observedAt,
        string runId) => new()
    {
        RunId = runId,
        AsOf = observedAt
    };
}
