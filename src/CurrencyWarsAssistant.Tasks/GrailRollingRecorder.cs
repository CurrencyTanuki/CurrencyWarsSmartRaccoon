using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」的<b>滚动录屏器</b>（隔离新增）。
/// <para>
/// 每局开始 <see cref="StartAsync"/>：起后台线程循环从 <see cref="IGameCapture.CaptureAsync"/>
/// 取帧，把 BGRA 像素流喂给 ffmpeg 编码成<b>临时</b> MP4。
/// 本局结束时 <see cref="FinishAsync"/>：
/// <list type="bullet">
///   <item><paramref name="success"/> 为 true → 把临时 MP4 移到保留目录（精彩瞬间存档）。</item>
///   <item>false → 删除临时文件（这局不符合要求不保留）、清理帧累积。</item>
/// </list>
/// 下一局重新 <see cref="StartAsync"/> 重新录，实现"每局滚动"。
/// </para>
/// <para>依赖外置 ffmpeg（不捆绑，见 <see cref="FfmpegLocator"/>）。</para>
/// <para>1.2.133 楔死修复（审计 P1-4）：stdin 写改异步可取消；stderr/stdout 后台排干
///（防 4KB 管道填满反压楔死）；停止等待加 10 秒超时（超时 Kill ffmpeg 解阻塞）。</para>
/// </summary>
public sealed class GrailRollingRecorder :
    GrailRunLoop.IRoundRecorder,
    IDisposable
{
    private const TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly IGameCapture _capture;
    private readonly GameWindowInfo _window;
    private readonly FateGrailRecordingQuality _quality;
    private readonly string _ffmpegPath;
    private readonly string _tempDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Process? _ffmpeg;
    private Task? _captureLoop;
    private Task? _stderrDrain;
    private Task? _stdoutDrain;
    private string? _tempFile;
    private bool _disposed;

    public GrailRollingRecorder(
        IGameCapture capture,
        GameWindowInfo window,
        FateGrailRecordingQuality quality,
        string ffmpegPath,
        string tempDirectory)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _quality = quality;
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
            ? throw new ArgumentException("ffmpeg 路径不能为空。", nameof(ffmpegPath))
            : ffmpegPath;
        _tempDirectory = tempDirectory
            ?? throw new ArgumentNullException(nameof(tempDirectory));
    }

    /// <summary>是否正在录制。</summary>
    public bool IsRecording
    {
        get
        {
            _gate.Wait();
            try { return _captureLoop is { IsCompleted: false }; }
            finally { _gate.Release(); }
        }
    }

    /// <summary>开始录制本局（临时文件 + 后台采集循环）。</summary>
    public async Task StartAsync(string roundId, CancellationToken cancellationToken = default)
    {
        _gate.Wait(cancellationToken);
        try
        {
            await StopInternalAsync(success: false, outputDirectory: null, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(_tempDirectory);
            _tempFile = Path.Combine(
                _tempDirectory,
                $"grail_{roundId}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}.mp4");
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var startInfo = BuildFfmpegArguments(_tempFile);
            _ffmpeg = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false,
            };
            if (!_ffmpeg.Start())
            {
                _ffmpeg = null;
                throw new InvalidOperationException("无法启动 ffmpeg 进程（录制器启动失败）。");
            }

            // P1-4：stderr/stdout 必须排干——ffmpeg 错误输出填满 4KB 管道缓冲会反压
            // stdin 写入造成楔死（经典 Process 重定向死锁）。任务句柄保留防未观察异常。
            _stderrDrain = _ffmpeg.StandardError.ReadToEndAsync();
            _stdoutDrain = _ffmpeg.StandardOutput.ReadToEndAsync();

            _captureLoop = Task.Run(() => RunCaptureLoopAsync(_cts.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 结束本局录制。
    /// <paramref name="success"/> true → 临时 MP4 保留（移动到保留目录）；
    /// false → 删除临时文件（本局不符合要求）。
    /// </summary>
    public async Task FinishAsync(
        bool success,
        string? outputDirectory,
        CancellationToken cancellationToken = default)
    {
        _gate.Wait(cancellationToken);
        try
        {
            await StopInternalAsync(success, outputDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 局边界旋转（09-10 用户令"每局结束先落盘上一局，再开始下一盘录制"）：
    /// 当前段正常封箱（ffmpeg 收到 stdin EOF 后写 moov，会话被杀也不产生
    /// 无 moov 的不可读段——F6 根治）并移动到保留目录，随后以新 roundId
    /// 开启下一段。未在录制时等价于 StartAsync。
    /// </summary>
    public async Task RotateAsync(
        string newRoundId,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        _gate.Wait(cancellationToken);
        try
        {
            await StopInternalAsync(success: true, outputDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await StartAsync(newRoundId, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopInternalAsync(
        bool success,
        string? outputDirectory,
        CancellationToken cancellationToken)
    {
        // 停采集循环。
        _cts?.Cancel();
        if (_captureLoop is not null)
        {
            try
            {
                // P1-4：采集循环若卡在 stdin 写（磁盘满/ffmpeg 停止消费），Cancel 无法
                // 解除同步 Write 的阻塞——加 10 秒超时，超时走下方 Kill ffmpeg 解阻塞。
                await _captureLoop.WaitAsync(StopWaitTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        _cts = null;

        // 关闭 ffmpeg stdin（发 EOF）并等待其写盘完成。
        var killed = false;
        if (_ffmpeg is not null)
        {
            try { _ffmpeg.StandardInput.Close(); } catch { }
            try
            {
                if (!_ffmpeg.WaitForExit(5000))
                {
                    _ffmpeg.Kill();
                    killed = true;
                    // 审查 P2-1：等句柄释放再动文件（Kill 后进程未必立即退出，
                    // 紧接 File.Move 可能 IOException）。
                    _ffmpeg.WaitForExit(3000);
                }
            }
            catch (Exception) { }
            _ffmpeg.Dispose();
            _ffmpeg = null;
        }
        // 排干任务随进程退出自然完成；观察句柄防未观察异常。
        ObserveDrain(_stderrDrain);
        ObserveDrain(_stdoutDrain);
        _stderrDrain = null;
        _stdoutDrain = null;

        // 处理临时文件。
        if (_tempFile is not null && File.Exists(_tempFile))
        {
            if (killed)
            {
                // 审查 P2-1：Kill=ffmpeg 未收 EOF、moov 从未写出=不可读段。
                // 不得以合法命名进入 Recordings 冒充正常封箱段（rule §九 视为
                // 可信源）——移入 damaged 子目录隔离，审计时明确可疑。
                try
                {
                    var damagedDir = Path.Combine(
                        Path.GetDirectoryName(_tempFile) ?? ".", "damaged");
                    Directory.CreateDirectory(damagedDir);
                    File.Move(
                        _tempFile,
                        Path.Combine(damagedDir, Path.GetFileName(_tempFile)),
                        overwrite: true);
                }
                catch (Exception)
                {
                    try { File.Delete(_tempFile); } catch (Exception) { }
                }
            }
            else if (success && !string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                var safeName = Path.GetFileName(_tempFile);
                var dest = Path.Combine(outputDirectory, safeName);
                File.Move(_tempFile, dest, overwrite: true);
            }
            else
            {
                try { File.Delete(_tempFile); } catch (Exception) { }
            }
        }

        _tempFile = null;
        _captureLoop = null;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ObserveDrain(Task? drain)
    {
        if (drain is null)
        {
            return;
        }
        _ = drain.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task RunCaptureLoopAsync(CancellationToken cancellationToken)
    {
        var frameInterval = _quality.FrameInterval;
        try
        {
            var next = DateTimeOffset.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _capture.CaptureAsync(_window, cancellationToken).ConfigureAwait(false);
                // P1-4：改异步可取消写——磁盘满/ffmpeg 停止消费时不再永久阻塞。
                await WriteFrameAsync(_ffmpeg, frame, cancellationToken).ConfigureAwait(false);

                next += frameInterval;
                var delay = next - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 采集/编码异常：停止本局录制（不冒泡），避免影响刷局主流程。
        }
    }

    private static async Task WriteFrameAsync(Process? ffmpeg, CaptureFrame frame, CancellationToken cancellationToken)
    {
        if (ffmpeg is null || ffmpeg.HasExited)
        {
            return;
        }

        var stream = ffmpeg.StandardInput.BaseStream;
        var pixels = frame.BgraPixels;
        if (frame.Stride == frame.Width * 4)
        {
            // 无 padding：整块写。
            await stream.WriteAsync(pixels, 0, pixels.Length, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 有行 padding：逐行去掉 padding 写成紧密 BGRA。
            var pitch = frame.Width * 4;
            var buffer = new byte[frame.Height * pitch];
            for (var y = 0; y < frame.Height; y++)
            {
                Buffer.BlockCopy(
                    pixels, y * frame.Stride,
                    buffer, y * pitch,
                    pitch);
            }
            await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private ProcessStartInfo BuildFfmpegArguments(string outputFile)
    {
        var width = _window.ClientArea.Width;
        var height = _window.ClientArea.Height;
        var args =
            $"-loglevel error " +
            $"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {_quality.FramesPerSecond} -i - " +
            $"-c:v libx264 -preset veryfast -crf {_quality.Crf} -pix_fmt yuv420p " +
            $"-y \"{outputFile}\"";
        return new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            StopInternalAsync(success: false, outputDirectory: null, CancellationToken.None)
                .GetAwaiter().GetResult();
            _gate.Dispose();
            _disposed = true;
        }
        catch (Exception)
        {
            // Dispose 不容有失。
        }
    }
}
