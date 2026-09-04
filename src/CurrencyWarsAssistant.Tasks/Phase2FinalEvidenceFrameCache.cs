using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// Keeps only the original full frames that can still be referenced by the
/// active battle tracker. Ordinary battle screenshots remain memory-only and
/// are written to disk only if a finalized node actually references them.
/// </summary>
internal sealed class Phase2FinalEvidenceFrameCache
{
    internal const int DefaultMaximumFrames = 16;
    internal const long DefaultMaximumBytes = 512L * 1024 * 1024;

    private readonly string _runId;
    private readonly int _maximumFrames;
    private readonly long _maximumBytes;
    private readonly Dictionary<string, CachedFrame> _frames =
        new(StringComparer.Ordinal);
    private HashSet<string> _activeSourceIds = new(StringComparer.Ordinal);
    private long _cachedBytes;
    private string? _latestFullSourceId;

    public Phase2FinalEvidenceFrameCache(
        string runId,
        int maximumFrames = DefaultMaximumFrames,
        long maximumBytes = DefaultMaximumBytes)
    {
        ValidateRunId(runId);
        if (maximumFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _runId = runId;
        _maximumFrames = maximumFrames;
        _maximumBytes = maximumBytes;
    }

    internal int Count => _frames.Count;
    internal long CachedBytes => _cachedBytes;
    internal string? LatestFullSourceId => _latestFullSourceId;

    internal string Register(string screenshotName, CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var sourceId = CanonicalSourceId(_runId, screenshotName);
        var frameBytes = frame.BgraPixels.LongLength;
        if (frameBytes > _maximumBytes)
        {
            throw new InvalidDataException(
                $"Evidence frame exceeds the bounded cache byte limit: {frameBytes} > {_maximumBytes}.");
        }

        // This full analysis supersedes the prior heartbeat replay anchor.
        // Preserve it only if the tracker independently marks it active.
        _latestFullSourceId = null;
        RemoveUnretainedFrames();
        if (_frames.Remove(sourceId, out var replaced))
        {
            _cachedBytes -= replaced.Bytes;
        }

        if (_frames.Count + 1 > _maximumFrames ||
            _cachedBytes + frameBytes > _maximumBytes)
        {
            throw new InvalidDataException(
                "Active final-battle evidence exceeds the bounded frame cache; " +
                "no candidate was evicted and finalization was stopped.");
        }

        _frames.Add(sourceId, new CachedFrame(frame, frameBytes));
        _cachedBytes += frameBytes;
        _latestFullSourceId = sourceId;
        return sourceId;
    }

    internal void RetainOnly(IEnumerable<string> activeSourceIds)
    {
        ArgumentNullException.ThrowIfNull(activeSourceIds);
        var validated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceId in activeSourceIds)
        {
            var result = ParseSourceId(_runId, sourceId, out _);
            if (result == Phase2EvidenceSourceParseResult.Invalid)
            {
                // 跨会话/非法来源（软件重启后 tracker 帧状态可能残留旧 run 的截图路径）
                // 正是 RetainOnly 该清掉的不活跃证据：跳过即可——它进不了 validated，
                // RemoveUnretainedFrames 会把不属于本会话的帧一并排除。
                // 抛异常反而让周期清理永远失败、脏条目永远清不掉
                // （2026-09-04 实测：每 5 分钟 UnhandledUiException 一次，全天 20+ 次）。
                continue;
            }

            if (result == Phase2EvidenceSourceParseResult.Valid)
            {
                validated.Add(sourceId);
            }
        }

        _activeSourceIds = validated;
        RemoveUnretainedFrames();
    }

    internal async Task PersistFinalEvidenceAsync(
        string runDirectory,
        FinalNodeBattleState finalBattle,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentNullException.ThrowIfNull(finalBattle);
        var screenshotDirectory = Path.GetFullPath(
            Path.Combine(runDirectory, "screenshots"));
        Directory.CreateDirectory(screenshotDirectory);
        var boundedDirectory = screenshotDirectory.EndsWith(
            Path.DirectorySeparatorChar)
            ? screenshotDirectory
            : screenshotDirectory + Path.DirectorySeparatorChar;

        foreach (var sourceId in Phase2FinalEvidenceSourceCollector
                     .Collect(finalBattle)
                     .Distinct(StringComparer.Ordinal))
        {
            var parseResult = ParseSourceId(
                _runId,
                sourceId,
                out var screenshotName);
            if (parseResult == Phase2EvidenceSourceParseResult.NotScreenshot)
            {
                continue;
            }

            if (parseResult == Phase2EvidenceSourceParseResult.Invalid)
            {
                throw new InvalidDataException(
                    $"Final battle evidence contains an invalid or cross-run source: {sourceId}");
            }

            var screenshotPath = Path.GetFullPath(
                Path.Combine(screenshotDirectory, screenshotName!));
            if (!screenshotPath.StartsWith(
                    boundedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Final battle evidence escapes the run screenshot directory: {sourceId}");
            }

            if (File.Exists(screenshotPath))
            {
                EnsureLoadablePng(screenshotPath, sourceId);
                continue;
            }

            if (!_frames.TryGetValue(sourceId, out var cached))
            {
                throw new InvalidDataException(
                    $"Final battle evidence frame is missing from both cache and disk: {sourceId}");
            }

            var temporaryPath = Path.Combine(
                screenshotDirectory,
                $".{Guid.NewGuid():N}.evidence.tmp");
            try
            {
                await Task.Run(
                        () => cached.Frame.SavePng(temporaryPath),
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureLoadablePng(temporaryPath, sourceId);
                File.Move(temporaryPath, screenshotPath);
                EnsureLoadablePng(screenshotPath, sourceId);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    internal void Clear()
    {
        _frames.Clear();
        _activeSourceIds.Clear();
        _cachedBytes = 0;
        _latestFullSourceId = null;
    }

    internal static string CanonicalSourceId(
        string runId,
        string screenshotName)
    {
        ValidateRunId(runId);
        if (!IsSafeScreenshotName(screenshotName))
        {
            throw new InvalidDataException(
                $"Evidence screenshot name is not a safe PNG file name: {screenshotName}");
        }

        return $"run:{runId}/screenshots/{screenshotName}";
    }

    internal static Phase2EvidenceSourceParseResult ParseSourceId(
        string expectedRunId,
        string sourceId,
        out string? screenshotName)
    {
        screenshotName = null;
        if (string.IsNullOrWhiteSpace(sourceId) ||
            !sourceId.StartsWith("run:", StringComparison.Ordinal))
        {
            return Phase2EvidenceSourceParseResult.NotScreenshot;
        }

        var prefix = $"run:{expectedRunId}/screenshots/";
        if (!sourceId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Phase2EvidenceSourceParseResult.Invalid;
        }

        var candidate = sourceId[prefix.Length..];
        if (!IsSafeScreenshotName(candidate) ||
            !string.Equals(
                sourceId,
                prefix + candidate,
                StringComparison.Ordinal))
        {
            return Phase2EvidenceSourceParseResult.Invalid;
        }

        screenshotName = candidate;
        return Phase2EvidenceSourceParseResult.Valid;
    }

    private static bool IsSafeScreenshotName(string? screenshotName) =>
        !string.IsNullOrWhiteSpace(screenshotName) &&
        !Path.IsPathRooted(screenshotName) &&
        string.Equals(
            screenshotName,
            Path.GetFileName(screenshotName),
            StringComparison.Ordinal) &&
        screenshotName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        string.Equals(
            Path.GetExtension(screenshotName),
            ".png",
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureLoadablePng(string path, string sourceId)
    {
        try
        {
            var loaded = CaptureFrameLoader.LoadFile(path);
            if (loaded.Width <= 0 || loaded.Height <= 0)
            {
                throw new InvalidDataException("PNG dimensions are empty.");
            }
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException(
                $"Final battle evidence is not a loadable PNG: {sourceId}",
                exception);
        }
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId is "." or ".." ||
            runId.Contains('/') ||
            runId.Contains('\\') ||
            runId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Evidence run id is not a safe canonical segment: {runId}");
        }
    }

    private void RemoveUnretainedFrames()
    {
        foreach (var sourceId in _frames.Keys.ToArray())
        {
            if (_activeSourceIds.Contains(sourceId) ||
                string.Equals(
                    sourceId,
                    _latestFullSourceId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var removed = _frames[sourceId];
            _frames.Remove(sourceId);
            _cachedBytes -= removed.Bytes;
        }
    }

    private sealed record CachedFrame(CaptureFrame Frame, long Bytes);
}

internal enum Phase2EvidenceSourceParseResult
{
    NotScreenshot,
    Valid,
    Invalid
}

internal static class Phase2FinalEvidenceSourceCollector
{
    internal static IReadOnlyList<string> Collect(Phase2OperationalState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var result = new List<string>();
        Add(result, state.NodeId.Evidence);
        Add(result, state.BattleDamage.Evidence);
        Add(result, state.BattleDamage.Value?.Select(item => item.Evidence));
        Add(result, state.BattleSynergyDamage.Evidence);
        Add(result, state.BattleSynergyDamage.Value?.Select(item => item.Evidence));
        Add(result, state.BattleUnresolvedDamage.Evidence);
        Add(result, state.BattleUnresolvedDamage.Value?.Select(item => item.Evidence));
        Add(result, state.BattleScreenDamageCandidate.Evidence);
        Add(result, state.SettlementDamage.Evidence);
        Add(result, state.SettlementDamage.Value?.Select(item => item.Evidence));
        Add(result, state.SettlementScreenDamageCandidate.Evidence);
        Add(result, state.SettlementGoldReward.Evidence);
        Add(result, state.RemainingActionValue.Evidence);
        Add(result, state.PendingIcons.Select(item => item.Evidence));
        Add(result, state.PartialFields.Select(item => item.Evidence));
        return result;
    }

    internal static IReadOnlyList<string> Collect(FinalNodeBattleState battle)
    {
        ArgumentNullException.ThrowIfNull(battle);
        var result = new List<string>();
        Add(result, battle.Evidence);
        Add(result, battle.CharacterDamage.Select(item => item.Evidence));
        Add(result, battle.FinalSynergyDamage.Select(item => item.Evidence));
        Add(result, battle.FinalUnresolvedDamage.Select(item => item.Evidence));
        Add(result, battle.FinalSettlementTopThree.Select(item => item.Evidence));
        Add(result, battle.FinalDegradedObservations.Select(item => item.Evidence));
        Add(result, battle.FinalPartialFields.Select(item => item.Evidence));
        return result;
    }

    private static void Add(
        ICollection<string> destination,
        EvidenceReference evidence) => destination.Add(evidence.SourceId);

    private static void Add(
        ICollection<string> destination,
        IEnumerable<EvidenceReference>? evidence)
    {
        if (evidence is null)
        {
            return;
        }

        foreach (var item in evidence)
        {
            destination.Add(item.SourceId);
        }
    }
}
