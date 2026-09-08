using CurrencyWarsAssistant.App.FrameSandbox;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

/// <summary>帧沙箱测试共用工具：临时目录、最小 PNG 生成、脚本构造。</summary>
internal static class FrameSandboxTestUtil
{
    /// <summary>写一张纯色小 PNG（OpenCV 编码，供 CaptureFrameLoader 解码）。</summary>
    public static string WriteTestPng(
        string directory,
        string fileName,
        byte blue,
        byte green,
        byte red)
    {
        using var mat = new Mat(
            4,
            4,
            MatType.CV_8UC3,
            new Scalar(blue, green, red));
        var path = Path.Combine(directory, fileName);
        if (!Cv2.ImWrite(path, mat))
        {
            throw new IOException($"测试 PNG 写入失败：{path}");
        }

        return path;
    }

    public static string NewTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "framesandbox-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>三步脚本：click(100,100) → click(200,200) → 终局帧（空期望）。</summary>
    public static FrameSandboxScript BuildSimpleScript(
        string directory,
        int tolerance = 30,
        int maxWaitSeconds = 120)
    {
        var first = WriteTestPng(directory, "step1.png", 1, 2, 3);
        var second = WriteTestPng(directory, "step2.png", 4, 5, 6);
        var terminal = WriteTestPng(directory, "terminal.png", 7, 8, 9);
        return new FrameSandboxScript(
            "simple",
            directory,
            [
                new FrameSandboxStep(
                    "step1.png",
                    first,
                    [new FrameSandboxExpectation(
                        FrameSandboxExpectation.OpClick,
                        100,
                        100,
                        null,
                        null,
                        null,
                        null,
                        tolerance,
                        "第一步点击")],
                    maxWaitSeconds),
                new FrameSandboxStep(
                    "step2.png",
                    second,
                    [new FrameSandboxExpectation(
                        FrameSandboxExpectation.OpClick,
                        200,
                        200,
                        null,
                        null,
                        null,
                        null,
                        tolerance,
                        "第二步点击")],
                    maxWaitSeconds),
                new FrameSandboxStep(
                    "terminal.png",
                    terminal,
                    [],
                    maxWaitSeconds),
            ]);
    }

    /// <summary>真实冒烟脚本（tests/Fixtures/FrameSandbox）的仓库根定位。</summary>
    public static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    public static string SmokeScriptPath => Path.Combine(
        RepositoryRoot,
        "tests",
        "CurrencyWarsAssistant.Tests",
        "Fixtures",
        "FrameSandbox",
        "smoke_home_to_preparation.json");

    /// <summary>可推进的假时钟（TimeProvider 注入，测步超时）。</summary>
    public sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
