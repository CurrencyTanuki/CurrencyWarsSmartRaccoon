using System.IO;
using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public static class CaptureFrameLoader
{
    public static CaptureFrame LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadBytes(File.ReadAllBytes(Path.GetFullPath(path)));
    }

    public static CaptureFrame LoadBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new InvalidDataException("截图文件为空。");
        }

        using var source = Cv2.ImDecode(bytes.ToArray(), ImreadModes.Unchanged);
        if (source.Empty())
        {
            throw new InvalidDataException("无法解码截图；仅支持 OpenCV 可读取的图片格式。");
        }

        using var bgra = new Mat();
        switch (source.Channels())
        {
            case 4:
                source.CopyTo(bgra);
                break;
            case 3:
                Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
                break;
            case 1:
                Cv2.CvtColor(source, bgra, ColorConversionCodes.GRAY2BGRA);
                break;
            default:
                throw new InvalidDataException(
                    $"不支持的截图通道数：{source.Channels()}。");
        }

        var stride = checked(bgra.Width * 4);
        var pixels = new byte[checked(stride * bgra.Height)];
        for (var row = 0; row < bgra.Height; row++)
        {
            Marshal.Copy(
                bgra.Ptr(row),
                pixels,
                checked(row * stride),
                stride);
        }

        return new CaptureFrame(
            bgra.Width,
            bgra.Height,
            stride,
            pixels,
            new PixelRect(0, 0, bgra.Width, bgra.Height),
            DateTimeOffset.UtcNow);
    }
}
