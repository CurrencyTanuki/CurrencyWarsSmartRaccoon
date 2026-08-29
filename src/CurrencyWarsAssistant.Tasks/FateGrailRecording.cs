using System;
using System.IO;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「滚动录屏」画质档位（低/中/高）。纯参数定义，可独立单测。
/// <para>用户 2026-08-26 拍板：画质可调，默认高配（60FPS / 1080P）；
/// 分辨率用游戏窗口实际大小，本类定义帧率与码率/CRF。</para>
/// </summary>
public enum FateGrailRecordingQualityLevel
{
    /// <summary>省空间：15fps、CRF 28（文件小，够看过程）。</summary>
    Low,
    /// <summary>均衡：30fps、CRF 23（默认推荐）。</summary>
    Medium,
    /// <summary>高配默认：60fps、CRF 22（画面流畅清晰，文件较大）。</summary>
    High,
}

/// <summary>一档录屏质量的具体参数（帧率 + x264 CRF）。</summary>
public sealed record FateGrailRecordingQuality(
    int FramesPerSecond,
    int Crf)
{
    public static FateGrailRecordingQuality FromLevel(FateGrailRecordingQualityLevel level) =>
        level switch
        {
            FateGrailRecordingQualityLevel.Low => new FateGrailRecordingQuality(15, 28),
            FateGrailRecordingQualityLevel.Medium => new FateGrailRecordingQuality(30, 23),
            FateGrailRecordingQualityLevel.High => new FateGrailRecordingQuality(60, 22),
            _ => new FateGrailRecordingQuality(60, 22),
        };

    /// <summary>相邻帧最小间隔（用于录屏循环节流）。</summary>
    public TimeSpan FrameInterval => TimeSpan.FromSeconds(1d / FramesPerSecond);
}

/// <summary>ffmpeg 定位结果。</summary>
public sealed record FfmpegLocateResult(
    string? ExecutablePath,
    string DownloadUrl)
{
    public bool Found => ExecutablePath is not null;
}

/// <summary>
/// 定位本机可用 ffmpeg.exe（PATH + 常见安装目录）。
/// 未找到时给出可打开的下载页（用户 2026-08-26：不捆绑 ffmpeg，启用功能前检测）。
/// </summary>
public static class FfmpegLocator
{
    /// <summary>ffmpeg 官方下载页（用户期望"自动打开浏览器"的地址）。</summary>
    public const string DefaultDownloadUrl =
        "https://www.ffmpeg.org/download.html";

    private static readonly string[] CommonPaths =
    {
        // PATH 里的 ffmpeg 已由 which 兜底；这里补 Windows 常见安装位置。
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
    };

    /// <summary>尝试定位 ffmpeg。</summary>
    public static FfmpegLocateResult Locate(string? configuredPath = null)
    {
        // 1) 显式配置（运行时传入）。
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            File.Exists(configuredPath))
        {
            return new FfmpegLocateResult(
                Path.GetFullPath(configuredPath),
                DefaultDownloadUrl);
        }

        // 2) PATH。
        var fromPath = FindInPath("ffmpeg.exe");
        if (fromPath is not null)
        {
            return new FfmpegLocateResult(fromPath, DefaultDownloadUrl);
        }

        // 3) 常见安装目录。
        foreach (var candidate in CommonPaths)
        {
            if (File.Exists(candidate))
            {
                return new FfmpegLocateResult(candidate, DefaultDownloadUrl);
            }
        }

        return new FfmpegLocateResult(null, DefaultDownloadUrl);
    }

    private static string? FindInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception)
            {
                // 某些 PATH 项可能非法；跳过。
            }
        }

        return null;
    }
}
/// <summary>
/// 「滚动录屏」装配配置（由调用方（MainWindow）在启动刷局时构建；ffmpeg 已定位成功才算有效）。
/// </summary>
public sealed record FateGrailRecordingOptions(
    IGameCapture Capture,
    FateGrailRecordingQuality Quality,
    string FfmpegPath,
    string TempDirectory,
    string OutputDirectory);
