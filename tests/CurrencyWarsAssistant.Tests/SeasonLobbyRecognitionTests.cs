using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 零和博弈赛季主页识别守卫（1.2.117）：
/// 该页（钻石+人群+「开始货币战争」大按钮）此前不在识别表语义内，live 管线曾把它
/// 误报成 preparation_generic（未知页 fallback 降级）——会让 DECIDE 入口误走
/// "备战页已有对局"弃局分支（坑50 同族病理的页面级变体）。
/// 修复=currency_wars_home 增加第二锚点「开始货币战争」按钮（3D 形态主页与赛季主页
/// 同按钮）+minimumAnchorMatches=1。本守卫：赛季主页与 3D 主页都必须判 home，
/// 备战页必须仍判 preparation。
/// </summary>
public sealed class SeasonLobbyRecognitionTests
{
    [Theory]
    [InlineData("currency_wars_season_lobby_20260907.png", "currency_wars_home")]
    [InlineData("currency_wars_home.jpg", "currency_wars_home")]
    [InlineData("preparation_1_1.jpg", "preparation_generic")]
    public void HomeFormsAndPreparationFramesClassifyCorrectly(string fixture, string expected)
    {
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot, "config", "page-recognition.1920x1080.json"));
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot, "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "PageReplay", fixture));
        using var matcher = new OpenCvTemplateMatcher();

        var full = new TemplateGamePageClassifier(matcher, config.Pages);
        Assert.True(
            string.Equals(full.Classify(frame)?.PageId, expected, StringComparison.OrdinalIgnoreCase),
            $"{fixture} 期望 {expected}，实得 {full.Classify(frame)?.PageId ?? "无匹配"}");

        var automation = AutomationPageIds.Create(matcher, config.Pages);
        Assert.True(
            string.Equals(automation.Classify(frame)?.PageId, expected, StringComparison.OrdinalIgnoreCase),
            $"自动化子集：{fixture} 期望 {expected}，实得 {automation.Classify(frame)?.PageId ?? "无匹配"}");
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
