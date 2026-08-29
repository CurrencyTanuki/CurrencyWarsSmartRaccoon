using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// Legacy keeps the current recognizer unchanged. ObserveOnly additionally
/// measures it and runs the experimental scheduler as a read-only observer.
/// </summary>
public enum Phase2RealtimeDiagnosticsMode
{
    Legacy,
    MeasureOnly,
    ObserveOnly
}

public static class Phase2RealtimeDiagnostics
{
    private static int mode;
    private static string? lastReportDirectory;
    private static string? lastReportError;

    public static Phase2RealtimeDiagnosticsMode Mode
    {
        get => (Phase2RealtimeDiagnosticsMode)Volatile.Read(ref mode);
        set => Volatile.Write(ref mode, (int)value);
    }

    public static string? LastReportDirectory =>
        Volatile.Read(ref lastReportDirectory);

    public static string? LastReportError =>
        Volatile.Read(ref lastReportError);

    internal static Phase2PerformanceDiagnosticSession? TryStartSession(
        string runId)
    {
        var sessionMode = Mode;
        if (sessionMode == Phase2RealtimeDiagnosticsMode.Legacy)
        {
            return null;
        }

        try
        {
            Volatile.Write(ref lastReportDirectory, null);
            Volatile.Write(ref lastReportError, null);
            return new Phase2PerformanceDiagnosticSession(runId, sessionMode);
        }
        catch (Exception exception)
        {
            // Diagnostics must never prevent the production recognizer from
            // starting. A failed observer is equivalent to Legacy mode.
            Volatile.Write(ref lastReportError, exception.Message);
            return null;
        }
    }

    internal static void SetReportDirectory(string path) =>
        Volatile.Write(ref lastReportDirectory, path);

    internal static void SetReportError(string message) =>
        Volatile.Write(ref lastReportError, message);
}

internal static class Phase2RecognitionMetrics
{
    internal const string MeterName = "CurrencyWarsAssistant.Phase2";
    private const string Prefix = "currencywars.phase2.";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> CaptureDuration =
        Meter.CreateHistogram<double>(Prefix + "capture.duration", "ms");
    private static readonly Histogram<double> CaptureInterval =
        Meter.CreateHistogram<double>(Prefix + "capture.interval", "ms");
    private static readonly Histogram<long> CaptureBytes =
        Meter.CreateHistogram<long>(Prefix + "capture.bytes", "By");
    private static readonly Histogram<double> FrameBroadcastDuration =
        Meter.CreateHistogram<double>(Prefix + "frame.broadcast.duration", "ms");
    private static readonly Histogram<double> FastClassificationDuration =
        Meter.CreateHistogram<double>(Prefix + "page.fast.duration", "ms");
    private static readonly Histogram<double> SelectionDuration =
        Meter.CreateHistogram<double>(Prefix + "frame.selection.duration", "ms");
    private static readonly Counter<long> SelectedFrames =
        Meter.CreateCounter<long>(Prefix + "frame.selected");
    private static readonly Histogram<long> QueueDepth =
        Meter.CreateHistogram<long>(Prefix + "queue.depth", "{item}");
    private static readonly Histogram<double> QueueAge =
        Meter.CreateHistogram<double>(Prefix + "queue.age", "ms");
    private static readonly Counter<long> QueueDropped =
        Meter.CreateCounter<long>(Prefix + "queue.dropped");
    private static readonly Histogram<double> AnalysisDuration =
        Meter.CreateHistogram<double>(Prefix + "analysis.duration", "ms");
    private static readonly Histogram<double> OutputWaitDuration =
        Meter.CreateHistogram<double>(Prefix + "output.wait.duration", "ms");
    private static readonly Histogram<double> CaptureToOutputDuration =
        Meter.CreateHistogram<double>(Prefix + "capture_to_output", "ms");
    private static readonly Histogram<double> CollectorReceiveAge =
        Meter.CreateHistogram<double>(Prefix + "collector.receive_age", "ms");
    private static readonly Histogram<double> TrackerDuration =
        Meter.CreateHistogram<double>(Prefix + "tracker.duration", "ms");
    private static readonly Histogram<double> StatePublishDuration =
        Meter.CreateHistogram<double>(Prefix + "state.publish.duration", "ms");
    private static readonly Histogram<double> CaptureToStateDuration =
        Meter.CreateHistogram<double>(Prefix + "capture_to_state", "ms");
    private static readonly Histogram<double> PersistenceDuration =
        Meter.CreateHistogram<double>(Prefix + "persistence.duration", "ms");
    private static readonly Histogram<double> OcrCallDuration =
        Meter.CreateHistogram<double>(Prefix + "ocr.call.duration", "ms");
    private static readonly Histogram<double> OcrLaneWaitDuration =
        Meter.CreateHistogram<double>(Prefix + "ocr.lane_wait.duration", "ms");
    private static readonly Histogram<double> OcrInferenceDuration =
        Meter.CreateHistogram<double>(Prefix + "ocr.inference.duration", "ms");
    private static readonly Histogram<long> OcrRegionPixels =
        Meter.CreateHistogram<long>(Prefix + "ocr.region_pixels", "{pixel}");
    private static readonly Counter<long> OcrTimeouts =
        Meter.CreateCounter<long>(Prefix + "ocr.timeout");
    private static readonly Counter<long> OcrFallbacks =
        Meter.CreateCounter<long>(Prefix + "ocr.fallback");
    private static readonly Counter<long> ShadowExceptions =
        Meter.CreateCounter<long>(Prefix + "shadow.exception");
    private static readonly Histogram<double> ShadowObserveDuration =
        Meter.CreateHistogram<double>(Prefix + "shadow.observe.duration", "ms");
    private static readonly Counter<long> ShadowLockSkipped =
        Meter.CreateCounter<long>(Prefix + "shadow.lock_skipped");
    private static readonly Histogram<double> CaptureLoopOverrun =
        Meter.CreateHistogram<double>(Prefix + "capture.loop.overrun", "ms");
    private static Phase2PerformanceDiagnosticSession? activeSession;

    internal static bool IsEnabled =>
        Volatile.Read(ref activeSession) is not null;

    internal static void Attach(Phase2PerformanceDiagnosticSession session) =>
        Volatile.Write(ref activeSession, session);

    internal static void Detach(Phase2PerformanceDiagnosticSession session) =>
        Interlocked.CompareExchange(ref activeSession, null, session);

    internal static void RecordCapture(
        TimeSpan duration,
        TimeSpan? interval,
        long bytes)
    {
        try
        {
            var milliseconds = duration.TotalMilliseconds;
            CaptureDuration.Record(milliseconds);
            CaptureBytes.Record(bytes);
            Record("capture.duration.ms", milliseconds);
            Record("capture.bytes", bytes);
            if (interval is { } observedInterval)
            {
                CaptureInterval.Record(observedInterval.TotalMilliseconds);
                Record("capture.interval.ms", observedInterval.TotalMilliseconds);
            }
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordFrameBroadcast(TimeSpan duration)
    {
        try
        {
            FrameBroadcastDuration.Record(duration.TotalMilliseconds);
            Record("frame.broadcast.ms", duration.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordFastClassification(TimeSpan duration)
    {
        try
        {
            FastClassificationDuration.Record(duration.TotalMilliseconds);
            Record("page.fast.ms", duration.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordSelection(TimeSpan duration)
    {
        try
        {
            SelectionDuration.Record(duration.TotalMilliseconds);
            Record("frame.selection.ms", duration.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordSelected(bool isCritical, bool isIncremental)
    {
        try
        {
            var kind = isCritical
                ? "critical"
                : isIncremental
                    ? "incremental"
                    : "full";
            SelectedFrames.Add(1, new KeyValuePair<string, object?>("kind", kind));
            Record("frame.selected." + kind, 1);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordQueueDepth(int depth)
    {
        try
        {
            QueueDepth.Record(depth);
            Record("queue.depth", depth);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordQueueAge(TimeSpan duration)
    {
        try
        {
            QueueAge.Record(duration.TotalMilliseconds);
            Record("queue.age.ms", duration.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordQueueDropped(int count, string reason)
    {
        try
        {
            if (count <= 0)
            {
                return;
            }
    
            QueueDropped.Add(
                count,
                new KeyValuePair<string, object?>("reason", reason));
            Record("queue.dropped." + reason, count);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordAnalysis(
        TimeSpan duration,
        string outcome,
        Phase2PageFamily pageFamily = Phase2PageFamily.Unknown,
        string executionType = "legacy")
    {
        try
        {
            var boundedOutcome = BoundedMetricPart(outcome, "unknown");
            var boundedExecution = BoundedExecutionType(executionType);
            var boundedPage = pageFamily.ToString().ToLowerInvariant();
            AnalysisDuration.Record(
                duration.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", boundedOutcome),
                new KeyValuePair<string, object?>("page_family", boundedPage),
                new KeyValuePair<string, object?>("execution_type", boundedExecution));
            Record(
                $"analysis.page.{boundedPage}.{boundedExecution}.{boundedOutcome}.ms",
                duration.TotalMilliseconds);
    
        }

        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    private static string BoundedExecutionType(string value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "full" => "full",
            "fields" => "fields",
            _ => "legacy"
        };

    private static string BoundedMetricPart(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().ToLowerInvariant() switch
            {
                "completed" => "completed",
                "failed" => "failed",
                _ => fallback
            };

    internal static void RecordOutputWait(TimeSpan duration)
    {
        try
        {
            OutputWaitDuration.Record(duration.TotalMilliseconds);
            Record("output.wait.ms", duration.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordCaptureToOutput(TimeSpan duration)
    {
        try
        {
            CaptureToOutputDuration.Record(duration.TotalMilliseconds);
            Record("capture_to_output.ms", duration.TotalMilliseconds);
    
        }

        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordCollectorReceiveAge(TimeSpan duration)
    {
        try
        {
            CollectorReceiveAge.Record(duration.TotalMilliseconds);
            Record("collector.receive_age.ms", duration.TotalMilliseconds);
        }
        catch (Exception)
        {
            // Metrics are strictly observational and must not alter collection.
        }
    }

    internal static void RecordTracker(TimeSpan duration)
    {
        try
        {
            TrackerDuration.Record(duration.TotalMilliseconds);
            Record("tracker.duration.ms", duration.TotalMilliseconds);
        }
        catch (Exception)
        {
            // Metrics are strictly observational and must not alter collection.
        }
    }

    internal static void RecordStatePublish(
        TimeSpan duration,
        TimeSpan captureToState)
    {
        try
        {
            StatePublishDuration.Record(duration.TotalMilliseconds);
            CaptureToStateDuration.Record(captureToState.TotalMilliseconds);
            Record("state.publish.ms", duration.TotalMilliseconds);
            Record("capture_to_state.ms", captureToState.TotalMilliseconds);
        }
        catch (Exception)
        {
            // Metrics are strictly observational and must not alter collection.
        }
    }

    internal static void RecordPersistence(TimeSpan duration)
    {
        try
        {
            PersistenceDuration.Record(duration.TotalMilliseconds);
            Record("persistence.duration.ms", duration.TotalMilliseconds);
        }
        catch (Exception)
        {
            // Metrics are strictly observational and must not alter collection.
        }
    }

    internal static void RecordOcrCall(
        TimeSpan duration,
        long regionPixels,
        string outcome)
    {
        try
        {
            OcrCallDuration.Record(
                duration.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", outcome));
            OcrRegionPixels.Record(regionPixels);
            Record("ocr.call." + outcome + ".ms", duration.TotalMilliseconds);
            Record("ocr.region_pixels", regionPixels);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordOcrLaneWait(TimeSpan duration, bool timedOut)
    {
        try
        {
            OcrLaneWaitDuration.Record(
                duration.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", timedOut ? "timeout" : "acquired"));
            Record("ocr.lane_wait.ms", duration.TotalMilliseconds);
            if (timedOut)
            {
                OcrTimeouts.Add(
                    1,
                    new KeyValuePair<string, object?>("stage", "lane"));
                Record("ocr.timeout.lane", 1);
            }
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordOcrInference(TimeSpan duration, bool timedOut)
    {
        try
        {
            OcrInferenceDuration.Record(
                duration.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", timedOut ? "timeout" : "completed"));
            Record("ocr.inference.ms", duration.TotalMilliseconds);
            if (timedOut)
            {
                OcrTimeouts.Add(
                    1,
                    new KeyValuePair<string, object?>("stage", "inference"));
                Record("ocr.timeout.inference", 1);
            }
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordOcrFallback(TimeSpan duration)
    {
        try
        {
            OcrFallbacks.Add(1);
            Record("ocr.fallback.count", 1);
            Record("ocr.fallback.ms", duration.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordShadowObserve(TimeSpan duration)
    {
        try
        {
            ShadowObserveDuration.Record(duration.TotalMilliseconds);
            Record("shadow.observe.ms", duration.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordShadowLockSkipped()
    {
        try
        {
            ShadowLockSkipped.Add(1);
            Record("shadow.lock_skipped", 1);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordCaptureLoopOverrun(TimeSpan overrun)
    {
        try
        {
            CaptureLoopOverrun.Record(overrun.TotalMilliseconds);
            Record("capture.loop.overrun.ms", overrun.TotalMilliseconds);
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    internal static void RecordShadowException()
    {
        try
        {
            ShadowExceptions.Add(1);
            Volatile.Read(ref activeSession)?.RecordShadowException();
    
        }
        catch (Exception)
        {
            // Observability failures are deliberately ignored so metrics can
            // never turn a successful capture or OCR call into a failure.
        }
    }

    private static void Record(string name, double value) =>
        Volatile.Read(ref activeSession)?.RecordMetric(name, value);
}

internal sealed class Phase2PerformanceDiagnosticSession : IAsyncDisposable
{
    private const int MaximumShadowRecords = 10_000;
    private readonly ConcurrentDictionary<string, MetricAccumulator> metrics =
        new(StringComparer.Ordinal);
    private readonly object shadowGate = new();
    private readonly object validityFileGate = new();
    private readonly List<Phase2ShadowJournalRecord> shadowRecords = [];
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private string? lastDecisionFingerprint;
    private string? reportDirectory;
    private long droppedShadowRecords;
    private long lateAfterDisposeRecords;
    private long pipelineDrainTimeouts;
    private long shadowExceptions;
    private int disposed;

    public Phase2PerformanceDiagnosticSession(
        string runId,
        Phase2RealtimeDiagnosticsMode mode =
            Phase2RealtimeDiagnosticsMode.ObserveOnly)
    {
        RunId = string.IsNullOrWhiteSpace(runId) ? "unassigned" : runId;
        Mode = mode;
        Phase2RecognitionMetrics.Attach(this);
    }

    public string RunId { get; }
    public Phase2RealtimeDiagnosticsMode Mode { get; }
    internal long LateAfterDisposeRecords =>
        Interlocked.Read(ref lateAfterDisposeRecords);
    internal long PipelineDrainTimeouts =>
        Interlocked.Read(ref pipelineDrainTimeouts);
    internal bool IsShadowReportValid =>
        Interlocked.Read(ref droppedShadowRecords) == 0 &&
        LateAfterDisposeRecords == 0 &&
        PipelineDrainTimeouts == 0;

    internal void RecordMetric(string name, double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        metrics.GetOrAdd(name, static _ => new MetricAccumulator()).Record(value);
    }
    internal void RecordShadowDecision(Phase2ShadowJournalRecord record)
    {
        var fingerprint = string.Join(
            '|',
            record.RecordType,
            record.RunId,
            record.PageFamily,
            record.Action,
            record.Reason,
            record.Fields is null ? string.Empty : string.Join(',', record.Fields),
            record.LegacySelected,
            record.LegacyCritical);
        lock (shadowGate)
        {
            if (string.Equals(
                    fingerprint,
                    lastDecisionFingerprint,
                    StringComparison.Ordinal))
            {
                return;
            }

            lastDecisionFingerprint = fingerprint;
            AddShadowRecord(record);
        }
    }

    internal void RecordShadowComparison(Phase2ShadowJournalRecord record)
    {
        lock (shadowGate)
        {
            AddShadowRecord(record);
        }
    }

    internal void RecordShadowException() =>
        Interlocked.Increment(ref shadowExceptions);

    internal void RecordPipelineDrainTimeout() =>
        Interlocked.Increment(ref pipelineDrainTimeouts);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Phase2RecognitionMetrics.Detach(this);
        try
        {
            var endedAt = DateTimeOffset.UtcNow;
            var safeRunId = SafeFileName(RunId);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CurrencyWarsSmartRaccoon",
                "performance-diagnostics",
                $"{endedAt:yyyyMMdd-HHmmssfff}-{safeRunId}");
            Directory.CreateDirectory(directory);
            Volatile.Write(ref reportDirectory, directory);
            Phase2RealtimeDiagnostics.SetReportDirectory(directory);

            Phase2ShadowJournalRecord[] records;
            lock (shadowGate)
            {
                records = shadowRecords.ToArray();
            }

            var metricSummaries = metrics
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(
                    item => item.Key,
                    item => item.Value.Snapshot(),
                    StringComparer.Ordinal);
            var droppedRecords = Interlocked.Read(ref droppedShadowRecords);
            var lateRecords = Interlocked.Read(ref lateAfterDisposeRecords);
            var drainTimeouts = Interlocked.Read(ref pipelineDrainTimeouts);
            var invalidReasons = ShadowInvalidReasons(
                droppedRecords,
                lateRecords,
                drainTimeouts);
            var comparisonCounts = records
                .Where(record => record.RecordType == "comparison")
                .GroupBy(record => record.Comparison ?? "inconclusive")
                .ToDictionary(
                    group => group.Key,
                    group => group.LongCount(),
                    StringComparer.Ordinal);
            var summary = new Phase2PerformanceSummary(
                RunId,
                Mode,
                startedAt,
                endedAt,
                metricSummaries,
                new Phase2ShadowSummary(
                    records.LongLength,
                    droppedRecords,
                    lateRecords,
                    drainTimeouts,
                    Interlocked.Read(ref shadowExceptions),
                    invalidReasons.Length == 0,
                    invalidReasons,
                    comparisonCounts));
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            await File.WriteAllTextAsync(
                    Path.Combine(directory, "summary.json"),
                    JsonSerializer.Serialize(summary, jsonOptions),
                    Encoding.UTF8)
                .ConfigureAwait(false);

            var journal = new StringBuilder(records.Length * 256);
            foreach (var record in records)
            {
                journal.AppendLine(JsonSerializer.Serialize(record));
            }

            await File.WriteAllTextAsync(
                    Path.Combine(directory, "shadow-decisions.jsonl"),
                    journal.ToString(),
                    Encoding.UTF8)
                .ConfigureAwait(false);
            WriteValidityFile();
        }
        catch (Exception exception)
        {
            // A report failure is diagnostic state, never a recognition error.
            Phase2RealtimeDiagnostics.SetReportError(exception.Message);
        }
    }

    private void AddShadowRecord(Phase2ShadowJournalRecord record)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            Interlocked.Increment(ref lateAfterDisposeRecords);
            ScheduleValidityRefresh();
            return;
        }

        if (shadowRecords.Count >= MaximumShadowRecords)
        {
            Interlocked.Increment(ref droppedShadowRecords);
            return;
        }

        shadowRecords.Add(record);
    }

    private void ScheduleValidityRefresh()
    {
        if (Volatile.Read(ref reportDirectory) is null)
        {
            return;
        }

        // This path exists only after session disposal. Write the tiny marker
        // synchronously so process shutdown cannot leave a stale "valid" file.
        WriteValidityFile();
    }

    private void WriteValidityFile()
    {
        var directory = Volatile.Read(ref reportDirectory);
        if (directory is null)
        {
            return;
        }

        try
        {
            lock (validityFileGate)
            {
                var droppedRecords = Interlocked.Read(
                    ref droppedShadowRecords);
                var lateRecords = Interlocked.Read(
                    ref lateAfterDisposeRecords);
                var drainTimeouts = Interlocked.Read(
                    ref pipelineDrainTimeouts);
                var invalidReasons = ShadowInvalidReasons(
                    droppedRecords,
                    lateRecords,
                    drainTimeouts);
                var validity = new Phase2ShadowReportValidity(
                    invalidReasons.Length == 0,
                    droppedRecords,
                    lateRecords,
                    drainTimeouts,
                    invalidReasons);
                File.WriteAllText(
                    Path.Combine(directory, "shadow-report-validity.json"),
                    JsonSerializer.Serialize(
                        validity,
                        new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Validity reporting remains diagnostic-only. A failed marker
            // must never escape into capture or recognition.
        }
    }

    private static string[] ShadowInvalidReasons(
        long droppedRecords,
        long lateRecords,
        long drainTimeouts)
    {
        var reasons = new List<string>(3);
        if (droppedRecords > 0)
        {
            reasons.Add("dropped-shadow-records");
        }

        if (lateRecords > 0)
        {
            reasons.Add("late-after-dispose-records");
        }

        if (drainTimeouts > 0)
        {
            reasons.Add("pipeline-drain-timeout");
        }

        return reasons.ToArray();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .Take(80)
            .ToArray();
        return characters.Length == 0 ? "unassigned" : new string(characters);
    }

    private sealed class MetricAccumulator
    {
        private const int ReservoirCapacity = 4_096;
        private readonly object gate = new();
        private readonly double[] reservoir = new double[ReservoirCapacity];
        private long count;
        private double sum;
        private double minimum = double.PositiveInfinity;
        private double maximum = double.NegativeInfinity;

        public void Record(double value)
        {
            lock (gate)
            {
                reservoir[count % ReservoirCapacity] = value;
                count++;
                sum += value;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        public Phase2MetricSummary Snapshot()
        {
            lock (gate)
            {
                var sampleCount = (int)Math.Min(count, ReservoirCapacity);
                var samples = reservoir.Take(sampleCount).Order().ToArray();
                return count == 0
                    ? new Phase2MetricSummary(0, 0, 0, 0, 0, 0, 0, 0)
                    : new Phase2MetricSummary(
                        count,
                        sum / count,
                        minimum,
                        maximum,
                        Percentile(samples, 0.50),
                        Percentile(samples, 0.90),
                        Percentile(samples, 0.95),
                        Percentile(samples, 0.99));
            }
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }
    }
}

internal sealed record Phase2MetricSummary(
    long Count,
    double Mean,
    double Minimum,
    double Maximum,
    double P50,
    double P90,
    double P95,
    double P99);

internal sealed record Phase2ShadowSummary(
    long WrittenRecords,
    long DroppedRecords,
    long LateAfterDisposeRecords,
    long PipelineDrainTimeouts,
    long ObserverExceptions,
    bool IsValid,
    IReadOnlyList<string> InvalidReasons,
    IReadOnlyDictionary<string, long> Comparisons);

internal sealed record Phase2ShadowReportValidity(
    bool IsValid,
    long DroppedRecords,
    long LateAfterDisposeRecords,
    long PipelineDrainTimeouts,
    IReadOnlyList<string> InvalidReasons);

internal sealed record Phase2PerformanceSummary(
    string RunId,
    Phase2RealtimeDiagnosticsMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyDictionary<string, Phase2MetricSummary> Metrics,
    Phase2ShadowSummary Shadow);

internal sealed record Phase2ShadowJournalRecord(
    string RecordType,
    DateTimeOffset ObservedAt,
    long Sequence,
    string RunId,
    string? PageId,
    string PageFamily,
    string? ChangeKind = null,
    string? Action = null,
    IReadOnlyList<string>? Fields = null,
    IReadOnlyDictionary<string, double>? ChangeStrength = null,
    string? Reason = null,
    bool? LegacySelected = null,
    bool? LegacyCritical = null,
    bool? LegacyIncremental = null,
    string? Comparison = null,
    string? Field = null,
    bool? CoverageComplete = null,
    bool? RequiresLegacyFullForUncovered = null);
