using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.App;

public sealed record Phase2DatasetCaptureCommand(
    string OutputDirectory,
    TimeSpan Duration,
    double FramesPerSecond,
    int EncoderWorkers = 3)
{
    public const string Switch = "--phase2-capture-dataset";

    public static Phase2DatasetCaptureCommand? Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 ||
            !string.Equals(arguments[0], Switch, StringComparison.Ordinal))
        {
            return null;
        }

        string? outputDirectory = null;
        var duration = TimeSpan.FromMinutes(10);
        var framesPerSecond = 5d;
        var encoderWorkers = 3;
        for (var index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count)
            {
                throw UsageException();
            }

            switch (arguments[index])
            {
                case "--output":
                    outputDirectory = arguments[index + 1];
                    break;
                case "--duration-seconds" when
                    int.TryParse(arguments[index + 1], out var seconds) &&
                    seconds is >= 1 and <= 21_600:
                    duration = TimeSpan.FromSeconds(seconds);
                    break;
                case "--fps" when
                    double.TryParse(
                        arguments[index + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var fps) &&
                    fps is >= 4 and <= 6:
                    framesPerSecond = fps;
                    break;
                case "--encoder-workers" when
                    int.TryParse(arguments[index + 1], out var workers) &&
                    workers is >= 1 and <= 6:
                    encoderWorkers = workers;
                    break;
                default:
                    throw UsageException();
            }
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw UsageException();
        }

        return new Phase2DatasetCaptureCommand(
            Path.GetFullPath(outputDirectory),
            duration,
            framesPerSecond,
            encoderWorkers);
    }

    private static ArgumentException UsageException() => new(
        $"用法：{Switch} --output <目录> " +
        "[--duration-seconds 1..21600] [--fps 4..6] " +
        "[--encoder-workers 1..6]");
}

public sealed record Phase2DatasetFrameRecord(
    long Sequence,
    string Status,
    DateTimeOffset AttemptedAt,
    DateTimeOffset? CapturedAt,
    string? FileName,
    int? Width,
    int? Height,
    double CaptureElapsedMilliseconds,
    double QueueWaitMilliseconds,
    double SaveElapsedMilliseconds,
    double? IntervalMilliseconds,
    string? Error);

public sealed record Phase2DatasetCaptureReport(
    string SchemaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string OutputDirectory,
    double TargetFramesPerSecond,
    double ActualFramesPerSecond,
    int SuccessfulFrames,
    int FailedFrames,
    double AverageIntervalMilliseconds,
    double P95IntervalMilliseconds,
    double MaximumIntervalMilliseconds,
    double AverageCaptureMilliseconds,
    double P95CaptureMilliseconds,
    double MaximumQueueWaitMilliseconds,
    int Width,
    int Height,
    string ProcessName,
    string WindowTitle,
    bool SendsInput,
    bool ReadsGameMemory);

public sealed class Phase2DatasetCaptureService(
    IGameWindowService windowService,
    IGameCapture capture)
{
    public async Task<Phase2DatasetCaptureReport> CaptureAsync(
        Phase2DatasetCaptureCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Directory.CreateDirectory(command.OutputDirectory);
        var framesDirectory = Path.Combine(command.OutputDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);

        var window = windowService.FindCandidates().FirstOrDefault() ??
                     throw new InvalidOperationException(
                         "未找到可捕获的《崩坏：星穹铁道》游戏窗口。请先打开游戏且不要最小化。");
        var records = new ConcurrentQueue<Phase2DatasetFrameRecord>();
        var queue = Channel.CreateBounded<PendingFrame>(
            new BoundedChannelOptions(32)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        var savers = Enumerable.Range(0, command.EncoderWorkers)
            .Select(_ => SaveFramesAsync(
                queue.Reader,
                framesDirectory,
                records,
                cancellationToken))
            .ToArray();

        var startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1d / command.FramesPerSecond);
        var nextDue = TimeSpan.Zero;
        long sequence = 0;
        DateTimeOffset? previousCapturedAt = null;
        while (clock.Elapsed < command.Duration &&
               !cancellationToken.IsCancellationRequested)
        {
            var delay = nextDue - clock.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            sequence++;
            var attemptedAt = DateTimeOffset.UtcNow;
            var captureClock = Stopwatch.StartNew();
            try
            {
                window = windowService.Refresh(window.Handle) ??
                         throw new InvalidOperationException(
                             "游戏窗口已关闭、最小化或不可捕获。");
                var frame = await capture.CaptureAsync(window, cancellationToken)
                    .ConfigureAwait(false);
                captureClock.Stop();
                var capturedAt = frame.CapturedAt;
                var frameInterval = previousCapturedAt is null
                    ? null
                    : (double?)(capturedAt - previousCapturedAt.Value)
                    .TotalMilliseconds;
                previousCapturedAt = capturedAt;
                var fileName = $"{sequence:D7}-{capturedAt:yyyyMMdd-HHmmssfff}.png";
                var queueClock = Stopwatch.StartNew();
                await queue.Writer.WriteAsync(
                        new PendingFrame(
                            sequence,
                            attemptedAt,
                            frame,
                            fileName,
                            captureClock.Elapsed.TotalMilliseconds,
                            frameInterval),
                        cancellationToken)
                    .ConfigureAwait(false);
                queueClock.Stop();
                if (queueClock.Elapsed > TimeSpan.FromMilliseconds(1))
                {
                    // Record actual backpressure on the frame itself; no frame is
                    // silently dropped merely to make the FPS report look better.
                    records.Enqueue(new Phase2DatasetFrameRecord(
                        sequence,
                        "queue-backpressure",
                        attemptedAt,
                        capturedAt,
                        fileName,
                        frame.Width,
                        frame.Height,
                        captureClock.Elapsed.TotalMilliseconds,
                        queueClock.Elapsed.TotalMilliseconds,
                        0,
                        frameInterval,
                        null));
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                captureClock.Stop();
                records.Enqueue(new Phase2DatasetFrameRecord(
                    sequence,
                    "capture-failed",
                    attemptedAt,
                    null,
                    null,
                    null,
                    null,
                    captureClock.Elapsed.TotalMilliseconds,
                    0,
                    0,
                    null,
                    exception.Message));
            }

            nextDue += interval;
            if (clock.Elapsed - nextDue > interval)
            {
                nextDue = clock.Elapsed;
            }
        }

        queue.Writer.TryComplete();
        await Task.WhenAll(savers).ConfigureAwait(false);
        clock.Stop();
        var endedAt = DateTimeOffset.UtcNow;
        var ordered = records
            .Where(item => item.Status != "queue-backpressure")
            .OrderBy(item => item.Sequence)
            .ToArray();
        var backpressureBySequence = records
            .Where(item => item.Status == "queue-backpressure")
            .GroupBy(item => item.Sequence)
            .ToDictionary(group => group.Key, group => group.Max(item =>
                item.QueueWaitMilliseconds));
        ordered = ordered.Select(item => item with
        {
            QueueWaitMilliseconds = backpressureBySequence.GetValueOrDefault(
                item.Sequence)
        }).ToArray();

        await WriteFrameManifestAsync(
                command.OutputDirectory,
                ordered,
                cancellationToken)
            .ConfigureAwait(false);
        var successful = ordered.Where(item => item.Status == "saved").ToArray();
        var intervals = successful
            .Select(item => item.IntervalMilliseconds)
            .OfType<double>()
            .Where(value => value > 0)
            .Order()
            .ToArray();
        var captureTimes = successful
            .Select(item => item.CaptureElapsedMilliseconds)
            .Order()
            .ToArray();
        var samplingSpan = successful.Length >= 2
            ? successful[^1].CapturedAt!.Value - successful[0].CapturedAt!.Value
            : TimeSpan.Zero;
        var actualFramesPerSecond = samplingSpan > TimeSpan.Zero
            ? (successful.Length - 1) / samplingSpan.TotalSeconds
            : 0;
        var report = new Phase2DatasetCaptureReport(
            "1.0.0",
            startedAt,
            endedAt,
            command.OutputDirectory,
            command.FramesPerSecond,
            actualFramesPerSecond,
            successful.Length,
            ordered.Length - successful.Length,
            Average(intervals),
            Percentile(intervals, 0.95),
            intervals.LastOrDefault(),
            Average(captureTimes),
            Percentile(captureTimes, 0.95),
            ordered.Select(item => item.QueueWaitMilliseconds).DefaultIfEmpty().Max(),
            successful.FirstOrDefault()?.Width ?? window.ClientArea.Width,
            successful.FirstOrDefault()?.Height ?? window.ClientArea.Height,
            window.ProcessName,
            window.Title,
            SendsInput: false,
            ReadsGameMemory: false);
        await File.WriteAllTextAsync(
                Path.Combine(command.OutputDirectory, "capture-report.json"),
                JsonSerializer.Serialize(report, JsonOptions),
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        return report;
    }

    private static async Task SaveFramesAsync(
        ChannelReader<PendingFrame> reader,
        string framesDirectory,
        ConcurrentQueue<Phase2DatasetFrameRecord> records,
        CancellationToken cancellationToken)
    {
        await foreach (var pending in reader.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            var saveClock = Stopwatch.StartNew();
            try
            {
                await Task.Run(
                        () => pending.Frame.SavePng(
                            Path.Combine(framesDirectory, pending.FileName)),
                        cancellationToken)
                    .ConfigureAwait(false);
                saveClock.Stop();
                records.Enqueue(new Phase2DatasetFrameRecord(
                    pending.Sequence,
                    "saved",
                    pending.AttemptedAt,
                    pending.Frame.CapturedAt,
                    pending.FileName,
                    pending.Frame.Width,
                    pending.Frame.Height,
                    pending.CaptureElapsedMilliseconds,
                    0,
                    saveClock.Elapsed.TotalMilliseconds,
                    pending.IntervalMilliseconds,
                    null));
            }
            catch (Exception exception) when (exception is not
                OperationCanceledException)
            {
                saveClock.Stop();
                records.Enqueue(new Phase2DatasetFrameRecord(
                    pending.Sequence,
                    "save-failed",
                    pending.AttemptedAt,
                    pending.Frame.CapturedAt,
                    pending.FileName,
                    pending.Frame.Width,
                    pending.Frame.Height,
                    pending.CaptureElapsedMilliseconds,
                    0,
                    saveClock.Elapsed.TotalMilliseconds,
                    pending.IntervalMilliseconds,
                    exception.Message));
            }
        }
    }

    private static async Task WriteFrameManifestAsync(
        string outputDirectory,
        IEnumerable<Phase2DatasetFrameRecord> records,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(outputDirectory, "frames.jsonl");
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            65_536,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonLineOptions))
                .ConfigureAwait(false);
        }
    }

    private static double Average(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? 0 : values.Average();

    private static double Percentile(IReadOnlyList<double> sorted, double value)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp(
            (int)Math.Ceiling(value * sorted.Count) - 1,
            0,
            sorted.Count - 1);
        return sorted[index];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record PendingFrame(
        long Sequence,
        DateTimeOffset AttemptedAt,
        CaptureFrame Frame,
        string FileName,
        double CaptureElapsedMilliseconds,
        double? IntervalMilliseconds);
}
