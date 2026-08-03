using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CurrencyWarsAssistant.Advisor;

public sealed class LocalRunStore
{
    public const string CheckpointFileName = "checkpoint.v1.json";
    public const string CheckpointBackupFileName = "checkpoint.v1.json.bak";
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public LocalRunStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string GetRunDirectory(string runId) => Path.Combine(
        RootDirectory,
        SanitizeSegment(runId));

    public async Task SaveCheckpointAsync(
        RunCheckpointRecord checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (string.IsNullOrWhiteSpace(checkpoint.RunId))
        {
            throw new ArgumentException(
                "Checkpoint run id is required.",
                nameof(checkpoint));
        }

        var directory = GetRunDirectory(checkpoint.RunId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CheckpointFileName);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicFileAsync(
                    path,
                    AdvisorJson.Serialize(checkpoint),
                    Path.Combine(directory, CheckpointBackupFileName),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<RunCheckpointSummary>>
        ListIncompleteRunsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        var summaries = new List<RunCheckpointSummary>();
        foreach (var directory in Directory
                     .EnumerateDirectories(RootDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, "completed-run.v1.json")))
            {
                continue;
            }

            var summary = await LoadCheckpointDirectoryAsync(
                    directory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (summary is not null &&
                summary.Checkpoint.LifecycleStatus is not
                    RunCheckpointLifecycleStatus.Completed and not
                    RunCheckpointLifecycleStatus.Abandoned &&
                // 过滤没有任何实质内容的断点（刷开局阶段产生的空 run：
                // 既无观测记录也无已确认节点）——空断点没有节点历史可恢复，
                // 列出只会让用户选错（0.2.776 实测：选到空断点历史为空）。
                (summary.Checkpoint.SavedObservationCount > 0 ||
                 !string.IsNullOrWhiteSpace(
                     summary.Checkpoint.LastConfirmedNodeId)))
            {
                summaries.Add(summary);
            }
        }

        return summaries
            .OrderByDescending(item => item.Checkpoint.LastSavedAtUtc)
            .ThenBy(item => item.Checkpoint.RunId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<ScreenshotAnalysisResult>> LoadAnalysesAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var runDirectory = GetRunDirectory(runId);
        if (!Directory.Exists(runDirectory))
        {
            return [];
        }

        var results = new List<ScreenshotAnalysisResult>();
        foreach (var path in Directory
                     .EnumerateFiles(runDirectory, "analysis-*.json")
                     .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = AdvisorJson.Deserialize<ScreenshotAnalysisResult>(
                    await File.ReadAllTextAsync(path, cancellationToken)
                        .ConfigureAwait(false));
                if (value is not null)
                {
                    results.Add(value);
                }
            }
            catch (JsonException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }

        return results
            .OrderBy(item => item.Snapshot.AsOf)
            .ToArray();
    }

    public async Task<IReadOnlyList<CompletedRunRecord>> ListCompletedRunsAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        var runs = new List<CompletedRunRecord>();
        foreach (var directory in Directory
                     .EnumerateDirectories(RootDirectory, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = Path.Combine(directory, "completed-run.v1.json");
            if (!File.Exists(finalPath))
            {
                continue;
            }

            try
            {
                var record = AdvisorJson.Deserialize<CompletedRunRecord>(
                    await File.ReadAllTextAsync(finalPath, cancellationToken)
                        .ConfigureAwait(false));
                if (record is not null)
                {
                    runs.Add(record);
                }
            }
            catch (JsonException)
            {
                // 单个损坏的对局记录不影响其他记录读取。
            }
            catch (InvalidDataException)
            {
            }
        }

        return runs
            .OrderByDescending(item => item.CompletedAt)
            .ThenBy(item => item.RunId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RunCheckpointSummary?> LoadCheckpointAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var directory = GetRunDirectory(runId);
        return Directory.Exists(directory)
            ? await LoadCheckpointDirectoryAsync(directory, cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    public async Task DeleteIncompleteRunAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var directory = GetRunDirectory(runId);
        var root = Path.GetFullPath(RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The run directory is outside the configured run store.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(fullDirectory))
            {
                return;
            }

            if (File.Exists(Path.Combine(
                    fullDirectory,
                    "completed-run.v1.json")))
            {
                throw new InvalidOperationException(
                    "A completed run cannot be deleted from the incomplete-run list.");
            }

            Directory.Delete(fullDirectory, recursive: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task AppendEventAsync(
        RunEvent runEvent,
        CancellationToken cancellationToken)
    {
        var directory = GetRunDirectory(runEvent.RunId);
        Directory.CreateDirectory(directory);
        var line = AdvisorJson.Serialize(runEvent, indented: false) + Environment.NewLine;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(
                    Path.Combine(directory, "events.jsonl"),
                    line,
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveAnalysisAsync(
        ScreenshotAnalysisResult result,
        CancellationToken cancellationToken)
    {
        var directory = GetRunDirectory(result.Snapshot.RunId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
                Path.Combine(directory, $"analysis-{SanitizeSegment(result.AnalysisId)}.json"),
                AdvisorJson.Serialize(result),
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveFinalNodeBattleAsync(
        string runId,
        FinalNodeBattleState battle,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(GetRunDirectory(runId), "nodes");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
                Path.Combine(
                    directory,
                    $"node-{SanitizeSegment(battle.NodeId)}-final.json"),
                AdvisorJson.Serialize(battle),
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CompletedRunRecord> CompleteRunAsync(
        string runId,
        DateTimeOffset completedAt,
        string completionPageId,
        string completionNodeId,
        string? completionScreenshotFile,
        string? ratingText,
        CancellationToken cancellationToken)
    {
        var runDirectory = GetRunDirectory(runId);
        Directory.CreateDirectory(runDirectory);
        var finalPath = Path.Combine(runDirectory, "completed-run.v1.json");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CompletedRunRecord? existingArchive = null;
            var sourceRevision = ComputeArchiveSourceRevision(runDirectory);
            if (File.Exists(finalPath))
            {
                existingArchive = AdvisorJson.Deserialize<CompletedRunRecord>(
                    await File.ReadAllTextAsync(finalPath, cancellationToken)
                        .ConfigureAwait(false));
                if (!string.IsNullOrWhiteSpace(existingArchive.SourceRevision) &&
                    string.Equals(
                        existingArchive.SourceRevision,
                        sourceRevision,
                        StringComparison.Ordinal))
                {
                    await TryMarkCheckpointCompletedAsync(
                            runId,
                            existingArchive.CompletedAt,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return existingArchive;
                }
            }

            var analysisFiles = Directory
                .EnumerateFiles(runDirectory, "analysis-*.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var analyses = new List<(string Path, ScreenshotAnalysisResult Value)>();
            foreach (var path in analysisFiles)
            {
                try
                {
                    var value = AdvisorJson.Deserialize<ScreenshotAnalysisResult>(
                        await File.ReadAllTextAsync(path, cancellationToken)
                            .ConfigureAwait(false));
                    analyses.Add((path, value));
                }
                catch (JsonException)
                {
                    // A corrupt analysis remains on disk as evidence, but must
                    // not prevent finalization of the rest of the run.
                }
                catch (InvalidDataException)
                {
                    // Same degradation rule as above for unsupported records.
                }
            }

            var battleFiles = Directory.Exists(Path.Combine(runDirectory, "nodes"))
                ? Directory.EnumerateFiles(
                        Path.Combine(runDirectory, "nodes"),
                        "node-*-final.json")
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            var battles = new List<(string Path, FinalNodeBattleState Value)>();
            foreach (var path in battleFiles)
            {
                try
                {
                    var value = JsonSerializer.Deserialize<FinalNodeBattleState>(
                        await File.ReadAllTextAsync(path, cancellationToken)
                            .ConfigureAwait(false),
                        AdvisorJson.Options);
                    if (value is not null)
                    {
                        battles.Add((path, value));
                    }
                }
                catch (JsonException)
                {
                    // Preserve the source file and archive all other nodes.
                }
            }

            var nodeIds = analyses
                .Select(item => ResolveNodeId(item.Value))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Concat(battles.Select(item => item.Value.NodeId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var nodes = nodeIds.Select(nodeId =>
            {
                var preparationSources = analyses
                    .Where(item =>
                        item.Value.OperationalState?.PageFamily ==
                            Phase2PageFamily.Preparation &&
                        string.Equals(
                            ResolveNodeId(item.Value),
                            nodeId,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.Value.Snapshot.AsOf)
                    .ToArray();
                var preparation = MergePreparationAnalyses(
                    preparationSources.Select(item => item.Value));
                var preparationPath = preparationSources.LastOrDefault().Path;
                var battle = battles
                    .Where(item => string.Equals(
                        item.Value.NodeId,
                        nodeId,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Value.CapturedAt)
                    .FirstOrDefault();
                return new CompletedRunNodeRecord(
                    nodeId!,
                    preparation?.Snapshot,
                    preparation?.OperationalState,
                    battle.Value,
                    preparationPath is null
                        ? null
                        : Path.GetRelativePath(runDirectory, preparationPath),
                    battle.Path is null
                        ? null
                        : Path.GetRelativePath(runDirectory, battle.Path));
            }).ToArray();
            RunCheckpointRecord? checkpoint = null;
            var checkpointFile = Path.Combine(runDirectory, "checkpoint.v1.json");
            if (File.Exists(checkpointFile))
            {
                try
                {
                    checkpoint = AdvisorJson.Deserialize<RunCheckpointRecord>(
                        await File.ReadAllTextAsync(checkpointFile, cancellationToken)
                            .ConfigureAwait(false));
                }
                catch (JsonException)
                {
                    // checkpoint 损坏不影响 completed-run 生成。
                }
                catch (InvalidDataException)
                {
                }
            }

            var latest = analyses
                .OrderByDescending(item => item.Value.Snapshot.AsOf)
                .FirstOrDefault();
            var archive = new CompletedRunRecord
            {
                ArchiveVersion = existingArchive is null
                    ? 1
                    : Math.Max(1, existingArchive.ArchiveVersion + 1),
                SourceRevision = sourceRevision,
                RunId = runId,
                CompletedAt = existingArchive?.CompletedAt ?? completedAt,
                CompletionPageId = existingArchive?.CompletionPageId ??
                                   completionPageId,
                CompletionNodeId = existingArchive?.CompletionNodeId ??
                                   completionNodeId,
                CompletionScreenshotFile =
                    existingArchive?.CompletionScreenshotFile ??
                    completionScreenshotFile,
                RatingText = existingArchive?.RatingText ?? ratingText,
                LastSnapshot = checkpoint?.LastSnapshot ?? latest.Value?.Snapshot,
                LastOperationalState = checkpoint?.LastOperationalState ??
                                       latest.Value?.OperationalState,
                Nodes = nodes,
                IdentityEvidence = checkpoint?.IdentityEvidence ??
                                   existingArchive?.IdentityEvidence ??
                                   new(),
                SourceAnalysisFiles = analysisFiles
                    .Select(path => Path.GetRelativePath(runDirectory, path))
                    .ToArray(),
                Uncertainty = analyses.Count == analysisFiles.Length &&
                              battles.Count == battleFiles.Length
                    ? []
                    : ["One or more source JSON files could not be decoded; the original files were retained."]
            };
            var json = AdvisorJson.Serialize(archive);
            var temporaryPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    Encoding.UTF8,
                cancellationToken)
                .ConfigureAwait(false);
            if (existingArchive is not null)
            {
                var historyDirectory = Path.Combine(
                    runDirectory,
                    "archive-history");
                Directory.CreateDirectory(historyDirectory);
                var historyPath = Path.Combine(
                    historyDirectory,
                    $"completed-run.v1.archive-v{existingArchive.ArchiveVersion}.json");
                if (!File.Exists(historyPath))
                {
                    File.Copy(finalPath, historyPath, overwrite: false);
                }
            }
            File.Move(temporaryPath, finalPath, overwrite: true);
            await File.WriteAllTextAsync(
                    finalPath + ".sha256",
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
                    Encoding.ASCII,
                    cancellationToken)
                .ConfigureAwait(false);
            await TryMarkCheckpointCompletedAsync(
                    runId,
                    archive.CompletedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            return archive;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string ComputeArchiveSourceRevision(string runDirectory)
    {
        var sourceFiles = Directory
            .EnumerateFiles(runDirectory, "analysis-*.json")
            .Concat(Directory.Exists(Path.Combine(runDirectory, "nodes"))
                ? Directory.EnumerateFiles(
                    Path.Combine(runDirectory, "nodes"),
                    "node-*-final.json")
                : [])
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in sourceFiles)
        {
            var relative = Path.GetRelativePath(runDirectory, path)
                .Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public Task<IReadOnlyList<string>> DeleteRunImageArtifactsAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var runDirectory = Path.GetFullPath(GetRunDirectory(runId));
            var rootDirectory = Path.GetFullPath(RootDirectory);
            var rootPrefix = rootDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!runDirectory.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The run image cleanup target is outside the configured run store.");
            }

            var deleted = new List<string>();
            foreach (var directoryName in new[]
                     {
                         "screenshots",
                         "unresolved-icons",
                         "recognition-failures"
                     })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(
                    runDirectory,
                    directoryName));
                var runPrefix = runDirectory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (!target.StartsWith(
                        runPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(target))
                {
                    continue;
                }

                Directory.Delete(target, recursive: true);
                deleted.Add(directoryName);
            }

            return deleted;
        }, cancellationToken);
    }

    public static string Fingerprint(RunSnapshot snapshot)
    {
        var material = string.Join(
            "|",
            snapshot.PageId.Status,
            snapshot.PageId.Value,
            snapshot.Stage.Status,
            snapshot.Stage.Value,
            snapshot.Economy.Status,
            snapshot.Economy.Value,
            snapshot.CumulativeSpend.Status,
            snapshot.CumulativeSpend.Value,
            snapshot.Health.Status,
            snapshot.Health.Value,
            snapshot.ActionPoints.Status,
            snapshot.ActionPoints.Value,
            snapshot.CurrentNodeDamage.Status,
            snapshot.CurrentNodeDamage.Value,
            Join(snapshot.BoardCharacterIds),
            Join(snapshot.BenchCharacterIds),
            Join(snapshot.ShopCharacterIds),
            Join(snapshot.LineupIds),
            Join(snapshot.SynergyIds),
            snapshot.InvestmentEnvironmentId.Status,
            snapshot.InvestmentEnvironmentId.Value,
            Join(snapshot.InvestmentStrategyIds),
            Join(snapshot.EquipmentIds),
            Join(snapshot.SpecialItemIds),
            JoinInventory(snapshot.InventorySlots),
            Join(snapshot.ExpertAdvisorIds),
            Join(snapshot.EnemyIds));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string Join(Observation<IReadOnlyList<string>> observation) =>
        observation.Value is null
            ? observation.Status.ToString()
            : string.Join(",", observation.Value.Order(StringComparer.Ordinal));

    private static string JoinInventory(
        Observation<IReadOnlyList<InventorySlotState>> observation) =>
        observation.Value is null
            ? observation.Status.ToString()
            : string.Join(",", observation.Value
                .OrderBy(item => item.SlotIndex)
                .Select(item => string.Join(
                    ":",
                    item.SlotIndex,
                    item.Occupancy,
                    item.ItemKind,
                    item.ItemId ?? "-",
                    string.Join("+", item.CandidateItemIds
                        .Order(StringComparer.Ordinal)),
                    item.CanDriveDecisions)));

    private static string? ResolveNodeId(ScreenshotAnalysisResult analysis)
    {
        var operationalNode = analysis.OperationalState?.NodeId;
        if (operationalNode?.Status == ObservationStatus.Known &&
            RunResumePolicy.TryGetNodeRank(operationalNode.Value, out _))
        {
            return operationalNode.Value;
        }

        return analysis.Snapshot.Stage.Status == ObservationStatus.Known &&
               RunResumePolicy.TryGetNodeRank(
                   analysis.Snapshot.Stage.Value,
                   out _)
            ? analysis.Snapshot.Stage.Value
            : null;
    }

    private static ScreenshotAnalysisResult? MergePreparationAnalyses(
        IEnumerable<ScreenshotAnalysisResult> source)
    {
        var ordered = source.OrderBy(item => item.Snapshot.AsOf).ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var merged = ordered[0];
        foreach (var current in ordered.Skip(1))
        {
            merged = current with
            {
                Snapshot = RunCheckpointFactory.MergeCheckpointSnapshot(
                    merged.Snapshot,
                    current.Snapshot),
                OperationalState =
                    RunCheckpointFactory.MergeCheckpointOperationalState(
                        merged.OperationalState,
                        current.OperationalState),
                Warnings = merged.Warnings.Concat(current.Warnings)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                UnknownFields = merged.UnknownFields
                    .Concat(current.UnknownFields)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }

        return merged;
    }

    private async Task<RunCheckpointSummary?> LoadCheckpointDirectoryAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var primaryPath = Path.Combine(directory, CheckpointFileName);
        var backupPath = Path.Combine(directory, CheckpointBackupFileName);
        var diagnostics = new List<string>();

        var primary = await TryReadCheckpointAsync(
                primaryPath,
                diagnostics,
                "primary",
                cancellationToken)
            .ConfigureAwait(false);
        if (primary is not null)
        {
            return new RunCheckpointSummary(
                primary,
                RunCheckpointHealth.Healthy,
                primaryPath,
                diagnostics);
        }

        var backup = await TryReadCheckpointAsync(
                backupPath,
                diagnostics,
                "backup",
                cancellationToken)
            .ConfigureAwait(false);
        if (backup is not null)
        {
            diagnostics.Add(
                "The primary checkpoint was unreadable; the last good backup was loaded.");
            return new RunCheckpointSummary(
                backup,
                RunCheckpointHealth.RecoveredFromBackup,
                backupPath,
                diagnostics);
        }

        var partial = await TryRecoverPartialCheckpointAsync(
                primaryPath,
                directory,
                diagnostics,
                cancellationToken)
            .ConfigureAwait(false);
        if (partial is not null)
        {
            return new RunCheckpointSummary(
                partial,
                RunCheckpointHealth.PartiallyRecovered,
                primaryPath,
                diagnostics);
        }

        return await SynthesizeCheckpointFromArtifactsAsync(
                directory,
                diagnostics,
                File.Exists(primaryPath) || File.Exists(backupPath),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<RunCheckpointRecord?> TryReadCheckpointAsync(
        string path,
        ICollection<string> diagnostics,
        string label,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            var checkpoint = JsonSerializer.Deserialize<RunCheckpointRecord>(
                json,
                AdvisorJson.Options);
            if (checkpoint is null ||
                checkpoint.CheckpointVersion != 1 ||
                string.IsNullOrWhiteSpace(checkpoint.RunId))
            {
                diagnostics.Add($"The {label} checkpoint is incomplete or unsupported.");
                return null;
            }

            return checkpoint;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(
                $"The {label} checkpoint could not be decoded: {exception.Message}");
            return null;
        }
        catch (IOException exception)
        {
            diagnostics.Add(
                $"The {label} checkpoint could not be read: {exception.Message}");
            return null;
        }
    }

    private static async Task<RunCheckpointRecord?>
        TryRecoverPartialCheckpointAsync(
            string path,
            string directory,
            ICollection<string> diagnostics,
            CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false));
            var root = document.RootElement;
            var runId = ReadString(root, "runId") ??
                        Path.GetFileName(directory);
            var createdAt = ReadDateTimeOffset(root, "createdAtUtc") ??
                            new DateTimeOffset(
                                Directory.GetCreationTimeUtc(directory),
                                TimeSpan.Zero);
            var lastSavedAt = ReadDateTimeOffset(root, "lastSavedAtUtc") ??
                              createdAt;
            diagnostics.Add(
                "A syntactically valid but incomplete checkpoint was recovered with unknown fields preserved as uncertainty.");
            return new RunCheckpointRecord
            {
                RunId = runId,
                CreatedAtUtc = createdAt,
                LastSavedAtUtc = lastSavedAt,
                LifecycleStatus = RunCheckpointLifecycleStatus.Paused,
                EntryMode = RunEntryMode.Resumed,
                LastConfirmedNodeId = ReadString(root, "lastConfirmedNodeId"),
                LastConfirmedPageId = ReadString(root, "lastConfirmedPageId"),
                Uncertainty = ["Some checkpoint fields were missing or invalid."]
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException exception)
        {
            diagnostics.Add(
                $"The partial checkpoint could not be read: {exception.Message}");
            return null;
        }
    }

    private static async Task<RunCheckpointSummary?>
        SynthesizeCheckpointFromArtifactsAsync(
            string directory,
            ICollection<string> diagnostics,
            bool checkpointWasCorrupt,
            CancellationToken cancellationToken)
    {
        var runId = Path.GetFileName(directory);
        var analysisFiles = Directory
            .EnumerateFiles(directory, "analysis-*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var readableAnalyses = new List<ScreenshotAnalysisResult>();
        foreach (var path in analysisFiles)
        {
            try
            {
                var analysis = AdvisorJson.Deserialize<ScreenshotAnalysisResult>(
                    await File.ReadAllTextAsync(path, cancellationToken)
                        .ConfigureAwait(false));
                readableAnalyses.Add(analysis);
            }
            catch (JsonException)
            {
                diagnostics.Add($"Unreadable legacy analysis retained: {Path.GetFileName(path)}");
            }
            catch (InvalidDataException)
            {
                diagnostics.Add($"Unsupported legacy analysis retained: {Path.GetFileName(path)}");
            }
        }

        var nodeDirectory = Path.Combine(directory, "nodes");
        var finalizedNodeIds = Directory.Exists(nodeDirectory)
            ? Directory.EnumerateFiles(nodeDirectory, "node-*-final.json")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Select(name => name["node-".Length..^"-final".Length])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var orderedAnalyses = readableAnalyses
            .OrderBy(item => item.Snapshot.AsOf)
            .ToArray();
        if (orderedAnalyses.Length == 0 &&
            analysisFiles.Length == 0 &&
            finalizedNodeIds.Length == 0 &&
            !checkpointWasCorrupt)
        {
            return null;
        }

        var createdAt = new DateTimeOffset(
            Directory.GetCreationTimeUtc(directory),
            TimeSpan.Zero);
        var checkpoint = RunCheckpointFactory.CreateInitial(
            runId,
            RunEntryMode.Resumed,
            createdAt) with
        {
            LifecycleStatus = RunCheckpointLifecycleStatus.Paused,
            FinalizedNodeIds = finalizedNodeIds,
            SavedObservationCount = analysisFiles.Length
        };
        foreach (var analysis in orderedAnalyses)
        {
            checkpoint = RunCheckpointFactory.FromAnalysis(
                checkpoint,
                analysis,
                analysisFiles.Length,
                RunCheckpointLifecycleStatus.Paused,
                analysis.Snapshot.AsOf);
        }

        diagnostics.Add(
            checkpointWasCorrupt
                ? "The checkpoint was corrupt; readable run artifacts were used for partial recovery."
                : "A legacy incomplete run was synthesized from its existing artifacts.");
        return new RunCheckpointSummary(
            checkpoint,
            checkpointWasCorrupt
                ? RunCheckpointHealth.PartiallyRecovered
                : RunCheckpointHealth.SynthesizedFromArtifacts,
            Path.Combine(directory, CheckpointFileName),
            diagnostics.ToArray());
    }

    private async Task TryMarkCheckpointCompletedAsync(
        string runId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var directory = GetRunDirectory(runId);
        var path = Path.Combine(directory, CheckpointFileName);
        var diagnostics = new List<string>();
        var checkpoint = await TryReadCheckpointAsync(
                path,
                diagnostics,
                "primary",
                cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is null)
        {
            return;
        }

        try
        {
            await WriteAtomicFileAsync(
                    path,
                    AdvisorJson.Serialize(checkpoint with
                    {
                        LifecycleStatus = RunCheckpointLifecycleStatus.Completed,
                        LastSavedAtUtc = completedAt
                    }),
                    Path.Combine(directory, CheckpointBackupFileName),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The final archive is authoritative. A checkpoint cleanup failure
            // must not roll back or invalidate completed-run.v1.json.
        }
    }

    private static async Task WriteAtomicFileAsync(
        string path,
        string content,
        string backupPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(
                             stream,
                             new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(
        JsonElement root,
        string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var result)
            ? result
            : null;

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }
}
