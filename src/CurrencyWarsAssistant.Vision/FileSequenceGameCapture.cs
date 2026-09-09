namespace CurrencyWarsAssistant.Vision;

/// <summary>
/// 帧沙箱（docs/SANDBOX_FEASIBILITY_20260908.md）的文件序列捕获器：
/// 忽略传入的窗口，从帧提供者（帧沙箱裁判按脚本步进供应的 PNG 解码帧）
/// 取当前帧返回。像素数组按次克隆——消费者持有/改写不会污染缓存帧，
/// 与真实捕获"每次快照独立"的语义一致。CapturedAt=服务时刻（可行性案 §五.3：
/// 帧龄看门狗按新帧处理，自然通过等待类守卫）。
/// </summary>
public sealed class FileSequenceGameCapture : IGameCapture
{
    private readonly Func<CaptureFrame> _frameProvider;

    /// <summary>
    /// 沙箱捕获节流（2026-09-09 DECIDE 级堵点修复）：真实 WGC 捕获随游戏渲染
    /// 帧率天然阻塞（几十毫秒/帧），充当识别管线帧选择循环的节奏；文件捕获
    /// 曾瞬时返回→管线全速空转（testhost 单核 100%、12 分钟零更新实锤），
    /// LatestAnalysis 永不更新→快照门禁全挡。300ms/帧≈3.3fps 与实机帧到达
    /// 节奏同量级，恢复管线既有 1.5~2s 分析间隔的语义。仅沙箱实例使用。
    /// </summary>
    private static readonly TimeSpan CaptureCadence =
        TimeSpan.FromMilliseconds(300);

    public FileSequenceGameCapture(Func<CaptureFrame> frameProvider)
    {
        ArgumentNullException.ThrowIfNull(frameProvider);
        _frameProvider = frameProvider;
    }

    public async ValueTask<CaptureFrame> CaptureAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(CaptureCadence, cancellationToken).ConfigureAwait(false);
        var frame = _frameProvider();
        var pixels = new byte[frame.BgraPixels.Length];
        Array.Copy(frame.BgraPixels, pixels, pixels.Length);
        var snapshot = frame with
        {
            BgraPixels = pixels,
            CapturedAt = DateTimeOffset.Now
        };
        return snapshot;
    }
}
