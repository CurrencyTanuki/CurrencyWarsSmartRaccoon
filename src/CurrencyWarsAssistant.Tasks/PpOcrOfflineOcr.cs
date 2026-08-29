using System.IO;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// Runs only the recognition stage of a PP-OCR model against a caller-provided
/// UI region. Currency Wars uses stable, normalized regions, so running text
/// detection and orientation models again would add latency without useful
/// information.
/// </summary>
/// <remarks>
/// The resize/normalization and CTC decoding behavior follows the Apache-2.0
/// RapidOCR and PaddleOCR recognition pipeline. Third-party source and model
/// provenance are shipped beside the model under data/ocr/rapidocr.
/// </remarks>
public sealed class PpOcrOfflineOcr : IAdaptiveOfflineOcr, IDisposable
{
    private const int TargetHeight = 48;
    private const int TargetWidth = 320;
    private readonly string _modelPath;
    private readonly Lazy<ModelSession[]> _sessions;
    private readonly Lazy<Task> _warmup;
    private readonly SemaphoreSlim _lanes;
    /// <summary>单次 ONNX 推理超时：正常识别 &lt;2 秒/区域，20 秒防误杀；
    /// 超时视为该 session 卡死（丢弃不回池），防止 8 个 lane 全卡拖死记录器。</summary>
    internal static readonly TimeSpan OcrInferenceTimeout = TimeSpan.FromSeconds(20);
    /// <summary>等待可用 lane 的超时：8 个 lane 全卡时降级返回空，不阻塞调用方。</summary>
    internal static readonly TimeSpan LaneWaitTimeout = TimeSpan.FromSeconds(30);
    private readonly System.Collections.Concurrent.ConcurrentQueue<ModelSession>
        _sessionPool = new();
    private readonly int _laneCount;
    private bool _disposed;

    public PpOcrOfflineOcr(string modelPath, int maximumConcurrency = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (maximumConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        }

        _modelPath = Path.GetFullPath(modelPath);
        _laneCount = maximumConcurrency;
        _lanes = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        // 每个 lane 一个独立 InferenceSession：同一 session 绝不并发 Run
        // （ORT CPU EP 并发 Run 是 coreclr c0000005 崩溃根因），同时保留
        // 多路吞吐（0.2.762 串行化后识别变慢导致关键帧队列被刷满）。
        _sessions = new Lazy<ModelSession[]>(
            () =>
            {
                var sessions = Enumerable.Range(0, maximumConcurrency)
                    .Select(_ => ModelSession.Create(_modelPath))
                    .ToArray();
                foreach (var session in sessions)
                {
                    _sessionPool.Enqueue(session);
                }

                return sessions;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        _warmup = new Lazy<Task>(
            WarmUpCoreAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => !_disposed && File.Exists(_modelPath);

    /// <summary>
    /// Whether the ONNX sessions are running on the GPU via DirectML
    /// (AMD/Intel/NVIDIA). False when falling back to CPU.
    /// </summary>
    public bool IsUsingGpu =>
        _sessions.IsValueCreated &&
        _sessions.Value is { Length: > 0 } sessions &&
        sessions[0].UsesGpu;

    /// <summary>
    /// Loads the shared ONNX session and performs one harmless blank inference
    /// before real screenshots enter the bounded recognition queue. The work is
    /// shared across concurrent callers and never touches the game process.
    /// </summary>
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return cancellationToken.CanBeCanceled
            ? _warmup.Value.WaitAsync(cancellationToken)
            : _warmup.Value;
    }

    public ValueTask<OcrTextResult> RecognizeAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken) =>
        RecognizeCoreAsync(frame, region, cancellationToken);

    public ValueTask<OcrTextResult> RecognizeRobustAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken) =>
        RecognizeCoreAsync(frame, region, cancellationToken);

    private async ValueTask<OcrTextResult> RecognizeCoreAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            throw new FileNotFoundException(
                "The packaged PP-OCR recognition model is unavailable.",
                _modelPath);
        }

        // 连续超时熔断已移除（用户 2026-08-06 明确：识别失败应释放工位、
        // 下一帧继续尝试，而不是永久降级打补丁）。
        // 单次推理超时后 lane/session 可靠释放（见下方 catch），
        // 不会出现"工位被永久占用"。

        var bounded = Bound(region, frame.Width, frame.Height);
        if (bounded.IsEmpty)
        {
            return new OcrTextResult(string.Empty, [])
            {
                Confidence = 0,
                Provider = "ppocr-recognition-only"
            };
        }

        if (!await _lanes.WaitAsync(
                LaneWaitTimeout,
                cancellationToken)
            .ConfigureAwait(false))
        {
            // 8 个 lane 全部被卡住的推理占用（等不到可用 lane）：
            // 降级返回空，绝不阻塞调用方（记录器全链依赖 OCR 不卡）。
            return new OcrTextResult(string.Empty, [])
            {
                Confidence = 0,
                Provider = "ppocr-lane-timeout"
            };
        }

        try
        {
            var sessions = _sessions.Value;
            if (!_sessionPool.TryDequeue(out var session) ||
                session is null)
            {
                // 池耗尽（超时丢弃的 session 不回池）：按需创建，绝不抛异常
                // 卡死调用链（review：池不补充会在 6 次超时后每帧抛异常）。
                session = ModelSession.Create(_modelPath);
            }

            var sessionTimedOut = false;
            Task<OcrTextResult>? inferenceTask = null;
            try
            {
                // ONNX 推理是同步阻塞调用（无法中途取消）。
                // 若某帧推理卡住（模型/资源问题），必须超时放弃，否则
                // lane 与 session 永久占用 → 8 个 lane 全卡 → 识别队列满 →
                // 记录器全链卡死（实测 0.2.831 08:11 后停）。
                inferenceTask = Task.Run(
                    () => Recognize(frame, bounded, session),
                    cancellationToken);
                var recognized = await inferenceTask
                    .WaitAsync(OcrInferenceTimeout, cancellationToken)
                    .ConfigureAwait(false);
                return recognized;
            }
            catch (TimeoutException)
            {
                sessionTimedOut = true;
                session.MarkAbandoned();
                // 后台 ONNX Run 不可取消且可能仍在执行：不 Dispose（避免
                // use-after-free / coreclr c0000005，与 Dispose() 的"排空正在
                // 执行的 Run"策略一致）。该 session 永久丢弃（后台任务结束
                // 后由 GC/原生释放），池由上方"按需创建"兜底补充。
                // 观察后台任务异常，避免未观察异常。
                _ = inferenceTask?.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return new OcrTextResult(string.Empty, [])
                {
                    Confidence = 0,
                    Provider = "ppocr-recognition-timeout"
                };
            }
            finally
            {
                if (!sessionTimedOut)
                {
                    _sessionPool.Enqueue(session);
                }
            }
        }
        finally
        {
            _lanes.Release();
        }
    }

    private async Task WarmUpCoreAsync()
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException(
                "The packaged PP-OCR recognition model is unavailable.",
                _modelPath);
        }

        var stride = TargetWidth * 4;
        var frame = new CaptureFrame(
            TargetWidth,
            TargetHeight,
            stride,
            new byte[stride * TargetHeight],
            new PixelRect(0, 0, TargetWidth, TargetHeight),
            DateTimeOffset.UtcNow);
        // 预热全部 lane session，避免首次真实识别时逐个触发模型初始化。
        var sessions = _sessions.Value;
        foreach (var session in sessions)
        {
            _ = await Task.Run(
                    () => Recognize(
                        frame,
                        new PixelRect(0, 0, TargetWidth, TargetHeight),
                        session),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private OcrTextResult Recognize(
        CaptureFrame frame,
        PixelRect region,
        ModelSession model)
    {
        var tensor = new DenseTensor<float>(
            [1, 3, TargetHeight, TargetWidth]);
        var ratio = region.Width / (double)region.Height;
        var resizedWidth = Math.Clamp(
            (int)Math.Ceiling(TargetHeight * ratio),
            1,
            TargetWidth);
        ResizeAndNormalizeBgr(frame, region, tensor, resizedWidth);

        using var results = model.Session.Run([
            NamedOnnxValue.CreateFromTensor(model.InputName, tensor)
        ]);
        var output = results.Single().AsTensor<float>();
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1)
        {
            throw new InvalidDataException(
                $"Unexpected PP-OCR output shape: [{string.Join(',', dimensions)}].");
        }

        var text = new System.Text.StringBuilder();
        var confidenceTotal = 0d;
        var characterCount = 0;
        var previous = -1;
        for (var time = 0; time < dimensions[1]; time++)
        {
            var bestClass = 0;
            var bestScore = float.NegativeInfinity;
            for (var candidate = 0; candidate < dimensions[2]; candidate++)
            {
                var score = output[0, time, candidate];
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestClass = candidate;
            }

            if (bestClass != 0 && bestClass != previous)
            {
                text.Append(model.Characters[bestClass]);
                confidenceTotal += bestScore;
                characterCount++;
            }

            previous = bestClass;
        }

        var recognized = text.ToString().Trim();
        var confidence = characterCount == 0
            ? 0
            : confidenceTotal / characterCount;
        return new OcrTextResult(
            recognized,
            recognized.Length == 0 ? [] : [recognized])
        {
            Confidence = confidence,
            Provider = "ppocr-recognition-only"
        };
    }

    private static void ResizeAndNormalizeBgr(
        CaptureFrame frame,
        PixelRect region,
        DenseTensor<float> target,
        int resizedWidth)
    {
        for (var y = 0; y < TargetHeight; y++)
        {
            var sourceY = region.Y +
                          ((y + 0.5) * region.Height / TargetHeight) -
                          0.5;
            var y0 = Math.Clamp(
                (int)Math.Floor(sourceY),
                region.Y,
                region.Bottom - 1);
            var y1 = Math.Min(y0 + 1, region.Bottom - 1);
            var fy = Math.Clamp(sourceY - y0, 0, 1);
            for (var x = 0; x < resizedWidth; x++)
            {
                var sourceX = region.X +
                              ((x + 0.5) * region.Width / resizedWidth) -
                              0.5;
                var x0 = Math.Clamp(
                    (int)Math.Floor(sourceX),
                    region.X,
                    region.Right - 1);
                var x1 = Math.Min(x0 + 1, region.Right - 1);
                var fx = Math.Clamp(sourceX - x0, 0, 1);
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = Lerp(
                        Pixel(frame, x0, y0, channel),
                        Pixel(frame, x1, y0, channel),
                        fx);
                    var bottom = Lerp(
                        Pixel(frame, x0, y1, channel),
                        Pixel(frame, x1, y1, channel),
                        fx);
                    var value = Lerp(top, bottom, fy);
                    target[0, channel, y, x] =
                        (float)(value / 127.5 - 1.0);
                }
            }
        }
    }

    private static byte Pixel(
        CaptureFrame frame,
        int x,
        int y,
        int channel) =>
        frame.BgraPixels[checked(y * frame.Stride + x * 4 + channel)];

    private static double Lerp(double left, double right, double amount) =>
        left + ((right - left) * amount);

    private static PixelRect Bound(PixelRect region, int width, int height)
    {
        var x = Math.Clamp(region.X, 0, width);
        var y = Math.Clamp(region.Y, 0, height);
        var right = Math.Clamp(region.Right, x, width);
        var bottom = Math.Clamp(region.Bottom, y, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // 排空正在执行的 ONNX Run，避免 Session 释放后原生线程仍在访问
        // （use-after-free 会表现为 coreclr c0000005 崩溃）。
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (_lanes.CurrentCount >= _laneCount)
            {
                break;
            }

            Thread.Sleep(20);
        }

        if (_sessions.IsValueCreated)
        {
            foreach (var session in _sessions.Value)
            {
                // 已丢弃（超时且后台 Run 可能仍在执行）的 session 跳过释放，
                // 避免退出时与原生线程并发访问（use-after-free / c0000005）。
                if (!session.Abandoned)
                {
                    session.Dispose();
                }
            }
        }

        _lanes.Dispose();
    }

    private sealed class ModelSession : IDisposable
    {
        private ModelSession(
            InferenceSession session,
            string inputName,
            string[] characters,
            bool usesGpu)
        {
            Session = session;
            InputName = inputName;
            Characters = characters;
            UsesGpu = usesGpu;
        }

        public InferenceSession Session { get; }

        public string InputName { get; }

        public string[] Characters { get; }

        public bool UsesGpu { get; }

        private volatile bool abandoned;

        /// <summary>该 session 因推理超时被丢弃（后台 Run 可能仍在执行）。</summary>
        public bool Abandoned => abandoned;

        public void MarkAbandoned() => abandoned = true;

        public static ModelSession Create(string modelPath)
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel =
                    GraphOptimizationLevel.ORT_ENABLE_ALL,
                // 多 session 并发推理，每个 session 单线程，避免 CPU 过载。
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
                // 关闭 CPU 内存 arena：并发/释放竞态下 arena 复用会破坏原生堆。
                EnableCpuMemArena = false
            };
            // DirectML（GPU）推理在 AMD 25.3.1 驱动下实测挂起（0.2.764 卡死：
            // 线程卡在原生层、UI 空闲、CPU 零负载）。0.2.765 起默认 CPU 推理
            // （6 路独立 session 并发吞吐足够）；GPU 路径待驱动兼容性验证后再启用。
            var usesGpu = false;
            var session = new InferenceSession(modelPath, options);

            try
            {
                var inputName = session.InputMetadata.Keys.Single();
                var rawCharacters = session.ModelMetadata
                    .CustomMetadataMap["character"]
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Split('\n');
                if (rawCharacters.Length > 0 &&
                    rawCharacters[^1].Length == 0)
                {
                    rawCharacters = rawCharacters[..^1];
                }

                string[] characters = ["<blank>", .. rawCharacters, " "];
                var classCount = session.OutputMetadata.Values
                    .Single()
                    .Dimensions[^1];
                if (classCount > 0 && classCount != characters.Length)
                {
                    throw new InvalidDataException(
                        $"PP-OCR class count {classCount} does not match " +
                        $"the embedded character count {characters.Length}.");
                }

                return new ModelSession(session, inputName, characters, usesGpu);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public void Dispose() => Session.Dispose();
    }
}

/// <summary>
/// Uses the neural recognizer first and invokes the mature Windows OCR path
/// only when the primary provider has no usable result. Conflicting fallback
/// results remain separate evidence lines instead of being silently chosen.
/// </summary>
public sealed class ConfidenceFallbackOfflineOcr(
    IOfflineOcr primary,
    IOfflineOcr fallback,
    double minimumPrimaryConfidence = 0.55) : IAdaptiveOfflineOcr
{
    public bool IsAvailable => primary.IsAvailable || fallback.IsAvailable;

    public ValueTask<OcrTextResult> RecognizeAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken) =>
        RecognizeAsync(frame, region, cancellationToken, robust: false);

    public ValueTask<OcrTextResult> RecognizeRobustAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken) =>
        RecognizeAsync(frame, region, cancellationToken, robust: true);

    private async ValueTask<OcrTextResult> RecognizeAsync(
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken,
        bool robust)
    {
        OcrTextResult primaryResult = new(string.Empty, []);
        if (primary.IsAvailable)
        {
            primaryResult = robust && primary is IAdaptiveOfflineOcr adaptive
                ? await adaptive.RecognizeRobustAsync(
                    frame,
                    region,
                    cancellationToken).ConfigureAwait(false)
                : await primary.RecognizeAsync(
                    frame,
                    region,
                    cancellationToken).ConfigureAwait(false);
        }

        if (!NeedsFallback(primaryResult) || !fallback.IsAvailable)
        {
            return primaryResult;
        }

        var fallbackResult = robust && fallback is IAdaptiveOfflineOcr fallbackAdaptive
            ? await fallbackAdaptive.RecognizeRobustAsync(
                frame,
                region,
                cancellationToken).ConfigureAwait(false)
            : await fallback.RecognizeAsync(
                frame,
                region,
                cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(primaryResult.Text))
        {
            return fallbackResult;
        }
        if (string.IsNullOrWhiteSpace(fallbackResult.Text))
        {
            return primaryResult;
        }

        var lines = primaryResult.Lines.Prepend(primaryResult.Text)
            .Concat(fallbackResult.Lines.Prepend(fallbackResult.Text))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new OcrTextResult(primaryResult.Text, lines)
        {
            Confidence = primaryResult.Confidence,
            Provider = $"{primaryResult.Provider}+{fallbackResult.Provider ?? "fallback"}"
        };
    }

    private bool NeedsFallback(OcrTextResult result) =>
        string.IsNullOrWhiteSpace(result.Text) ||
        result.Confidence is { } confidence &&
        confidence < minimumPrimaryConfidence;
}

public sealed record Phase2OfflineOcrSet(
    IOfflineOcr Text,
    IOfflineOcr Numeric)
{
    private const int WarmUpConcurrency = 4;

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const int width = 64;
        const int height = 32;
        const int stride = width * 4;
        var frame = new CaptureFrame(
            width,
            height,
            stride,
            new byte[stride * height],
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
        var region = new PixelRect(0, 0, width, height);
        var work = Enumerable.Range(0, WarmUpConcurrency)
            .SelectMany(_ => new[]
            {
                Text.RecognizeAsync(frame, region, cancellationToken).AsTask(),
                Numeric.RecognizeAsync(frame, region, cancellationToken).AsTask()
            });
        await Task.WhenAll(work).ConfigureAwait(false);
    }
}
