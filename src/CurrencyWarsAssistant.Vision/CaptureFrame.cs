using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CurrencyWarsAssistant.Core;

namespace CurrencyWarsAssistant.Vision;

public sealed record CaptureFrame(
    int Width,
    int Height,
    int Stride,
    byte[] BgraPixels,
    PixelRect ScreenArea,
    DateTimeOffset CapturedAt)
{
    public BitmapSource ToBitmapSource()
    {
        var source = BitmapSource.Create(
            Width,
            Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            BgraPixels,
            Stride);
        source.Freeze();
        return source;
    }

    public void SavePng(string path)
    {
        SavePng(path, new PixelRect(0, 0, Width, Height));
    }

    public void SavePng(string path, PixelRect region)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var x = Math.Clamp(region.X, 0, Width);
        var y = Math.Clamp(region.Y, 0, Height);
        var right = Math.Clamp(region.Right, x, Width);
        var bottom = Math.Clamp(region.Bottom, y, Height);
        var bounded = new PixelRect(x, y, right - x, bottom - y);
        if (bounded.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                "截图区域不能为空或超出画面。" );
        }

        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        BitmapSource source = bounded.Width == Width && bounded.Height == Height &&
                              bounded.X == 0 && bounded.Y == 0
            ? ToBitmapSource()
            : new CroppedBitmap(
                ToBitmapSource(),
                new System.Windows.Int32Rect(
                    bounded.X,
                    bounded.Y,
                    bounded.Width,
                    bounded.Height));
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
    }
}

/// <summary>
/// 捕获流诊断快照（1.2.96 识别流周期冻结根因诊断，纯观测、零行为变更）：
/// 区分"WGC 层不产帧"（FrameArrivals 停滞）与"捕获请求等待失败"（CaptureTimeouts 增长）、
/// "会话反复重建"（SessionCreations/SessionRebuilds）与"会话稳定但帧停滞"。
/// </summary>
public sealed record CaptureStreamStats(
    long FrameArrivals,
    long CaptureSuccesses,
    long CaptureTimeouts,
    long SessionCreations,
    long SessionRebuilds,
    DateTimeOffset? LastFrameArrivedAt,
    DateTimeOffset? LastCaptureSuccessAt)
{
    public static CaptureStreamStats Empty { get; } = new(0, 0, 0, 0, 0, null, null);
}

public interface IGameCapture
{
    ValueTask<CaptureFrame> CaptureAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken);

    /// <summary>
    /// 捕获流诊断快照；不支持的实现返回 null（接口默认实现——GdiGameCapture
    /// 等后备捕获零改动，WGC 实现覆写返回真实计数）。
    /// </summary>
    CaptureStreamStats? StreamStats => null;
}
