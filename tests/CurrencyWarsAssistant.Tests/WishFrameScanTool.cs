using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>临时工具测试（素材收集用）：对 wish-scan 帧跑真实分类器，结果写 summary.txt。用完即删。</summary>
public sealed class WishFrameScanTool
{
    [Fact]
    public void ScanWishFrames()
    {
        var scanDir = @"D:\CurrencyWarsData\sandbox-frames\wish-scan";
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            FrameSandboxTestUtil.RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        using var matcher = new OpenCvTemplateMatcher();
        var classifier = new TemplateGamePageClassifier(matcher, config.Pages);
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(scanDir, "*.png").OrderBy(f => f))
        {
            var frame = CaptureFrameLoader.LoadFile(file);
            var result = classifier.Classify(frame);
            lines.Add(
                $"{Path.GetFileName(file)}  {frame.Width}x{frame.Height}  ->  " +
                $"{result?.PageId ?? "null"}  conf={result?.Confidence:P1}");
        }

        File.WriteAllLines(Path.Combine(scanDir, "summary.txt"), lines);
        Assert.True(lines.Count > 0);
    }
}
