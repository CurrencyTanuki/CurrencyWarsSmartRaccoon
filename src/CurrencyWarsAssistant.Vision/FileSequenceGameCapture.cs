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

    public FileSequenceGameCapture(Func<CaptureFrame> frameProvider)
    {
        ArgumentNullException.ThrowIfNull(frameProvider);
        _frameProvider = frameProvider;
    }

    public ValueTask<CaptureFrame> CaptureAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();
        var frame = _frameProvider();
        var pixels = new byte[frame.BgraPixels.Length];
        Array.Copy(frame.BgraPixels, pixels, pixels.Length);
        var snapshot = frame with
        {
            BgraPixels = pixels,
            CapturedAt = DateTimeOffset.Now
        };
        return ValueTask.FromResult(snapshot);
    }
}
