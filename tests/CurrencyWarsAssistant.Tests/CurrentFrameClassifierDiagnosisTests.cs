using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 当前屏幕帧 × 软件快速分类器诊断（2026-09-04 用户令："用软件原有的快速分类器排查
/// 这个到底是什么界面"）。截屏文件路径通过环境变量 CW_DIAG_FRAME 传入；
/// 用与 App.xaml.cs 完全相同的配置（page-recognition.1920x1080.json + OpenCvTemplateMatcher）
/// 构建全量与自动化子集两个分类器，输出每页得分，回答"这到底是什么界面"。
/// </summary>
public sealed class CurrentFrameClassifierDiagnosisTests
{
    private readonly ITestOutputHelper _output;

    public CurrentFrameClassifierDiagnosisTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void ClassifyCapturedFrameWithSoftwareClassifier()
    {
        var framePath = Environment.GetEnvironmentVariable("CW_DIAG_FRAME");
        Assert.False(string.IsNullOrWhiteSpace(framePath), "未设置 CW_DIAG_FRAME 环境变量");
        Assert.True(File.Exists(framePath), $"帧文件不存在：{framePath}");

        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            repositoryRoot, "config", "page-recognition.1920x1080.json"));
        using var matcher = new OpenCvTemplateMatcher();
        var frame = CaptureFrameLoader.LoadFile(framePath);

        // 全量分类器（与导航/页面识别同源）
        var full = new TemplateGamePageClassifier(matcher, config.Pages);
        var fullResult = full.Classify(frame);
        _output.WriteLine($"全量分类器：{(fullResult is null ? "无匹配" : $"{fullResult.PageId} 置信={fullResult.Confidence:F3}")}");
        foreach (var page in config.Pages)
        {
            var top = 0d;
            string? topAnchor = null;
            foreach (var anchor in page.Anchors)
            {
                var probe = matcher.Probe(frame, anchor);
                if (probe is { } p && p.Confidence > top)
                {
                    top = p.Confidence;
                    topAnchor = anchor.Id;
                }
            }
            if (top >= 0.3)
            {
                _output.WriteLine($"  候选 {page.Id}: 最高锚点 {topAnchor}={top:F3}");
            }
        }

        // 自动化子集分类器（与 A/M 指令组件同源）
        var automation = AutomationPageIds.Create(matcher, config.Pages);
        var autoResult = automation.Classify(frame);
        _output.WriteLine($"自动化子集分类器：{(autoResult is null ? "无匹配" : $"{autoResult.PageId} 置信={autoResult.Confidence:F3}")}");
        foreach (var d in automation is IGamePageClassifierDiagnostics diag
                     ? diag.LastDiagnostics.Where(item => item.Confidence >= 0.3)
                     : Enumerable.Empty<PageAnchorDiagnostic>())
        {
            _output.WriteLine($"  自动化锚点 {d.PageId}/{d.AnchorId}={d.Confidence:F3}（阈值 {d.Threshold:F2}）");
        }

        Assert.True(true); // 诊断测试：断言无硬性失败，结论看输出
    }
}
