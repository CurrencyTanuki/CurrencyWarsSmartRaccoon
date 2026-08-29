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
/// </summary>
public sealed class GrailRollingRecorder :
    GrailRunLoop.IRoundRecorder,
    IDisposable
{
    private readonly IGameCapture _capture;
    private readonly GameWindowInfo _window;
    private readonly FateGrailRecordingQuality _quality;
    private readonly string _ffmpegPath;
    private readonly string _tempDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Process? _ffmpeg;
    private Task? _captureLoop;
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

    private async Task StopInternalAsync(
        bool success,
        string? outputDirectory,
        CancellationToken cancellationToken)
    {
        // 停采集循环。
        _cts?.Cancel();
        if (_captureLoop is not null)
        {
            try { await _captureLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
        _cts = null;

        // 关闭 ffmpeg stdin（发 EOF）并等待其写盘完成。
        if (_ffmpeg is not null)
        {
            try { _ffmpeg.StandardInput.Close(); } catch { }
            try
            {
                if (!_ffmpeg.WaitForExit(5000))
                {
                    _ffmpeg.Kill();
                }
            }
            catch (Exception) { }
            _ffmpeg.Dispose();
            _ffmpeg = null;
        }

        // 处理临时文件。
        if (_tempFile is not null && File.Exists(_tempFile))
        {
            if (success && !string.IsNullOrWhiteSpace(outputDirectory))
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

    private async Task RunCaptureLoopAsync(CancellationToken cancellationToken)
    {
        var frameInterval = _quality.FrameInterval;
        try
        {
            var next = DateTimeOffset.UtcNow;
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await _capture.CaptureAsync(_window, cancellationToken).ConfigureAwait(false);
                WriteFrame(_ffmpeg, frame);

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

    private static void WriteFrame(Process? ffmpeg, CaptureFrame frame)
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
            stream.Write(pixels, 0, pixels.Length);
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
            stream.Write(buffer, 0, buffer.Length);
        }
        stream.Flush();
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