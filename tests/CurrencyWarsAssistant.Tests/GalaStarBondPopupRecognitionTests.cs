using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 坑50（1.2.114）：盛会之星羁绊升档选择框的真帧识别守卫。
/// 22:33/22:49 两次卡死实锤帧（stall_end.png，2560×1440 原生）入库为夹具——
/// 全量分类器与自动化子集都必须认出 gala_star_bond_selection（此前识别表无此页
/// → I1 报底层 preparation 族 → Esc 被模态吞掉 → 弃局 5 连败自保停机）。
/// 负样本守卫：相近金色系页面（挑战成功结算/伙伴选择）绝不误判为该弹框。
/// 标定（1.2.117 重标，生产参数 Cv2 线性缩放）：彩色双锚点——副标题带正 0.900/1.000
/// （负 max 0.75，阈值 0.85）+大标题正 0.993/1.000（负 max 0.507，阈值 0.80），
/// minimumAnchorMatches=1 任一命中即确认。边缘模板在该缩放路径下负样本 0.829>正 0.594
/// 已废弃（插值敏感，教训入坑50）。
/// </summary>
public sealed class GalaStarBondPopupRecognitionTests
{
    private const string GalaPageId =
        CurrencyWarsRejectedOpeningRecovery.GalaBondPopupPageId;

    [Fact]
    public void StallFrameClassifiesAsGalaBondPopupOnBothClassifiers()
    {
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "gala_star_bond_popup_stall_20260906.png"));

        using var matcher = new OpenCvTemplateMatcher();

        // 全量分类器（导航/页面识别同源）
        var full = new TemplateGamePageClassifier(matcher, config.Pages);
        var fullResult = full.Classify(frame);
        Assert.True(
            string.Equals(fullResult?.PageId, GalaPageId, StringComparison.OrdinalIgnoreCase),
            $"全量分类器未认出盛会弹框：{(fullResult is null ? "无匹配" : fullResult.PageId)}");

        // 自动化子集（弃局链/循环泵用的 IGamePageClassifier 同源）
        var automation = AutomationPageIds.Create(matcher, config.Pages);
        var autoResult = automation.Classify(frame);
        Assert.True(
            string.Equals(autoResult?.PageId, GalaPageId, StringComparison.OrdinalIgnoreCase),
            $"自动化子集未认出盛会弹框：{(autoResult is null ? "无匹配" : autoResult.PageId)}");

        // 坑48 纪律守卫：fast 集白名单必须同步包含（漏③=fast 层"未知"覆盖
        // 全量结论、I1 恒报未知——2026-09-05 断线自愈实锤病理）。
        var fastIds = typeof(Phase2FastPageClassifier)
            .GetField("FastPageIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as System.Collections.Generic.IReadOnlySet<string>;
        Assert.True(fastIds?.Contains(GalaPageId) == true, "FastPageIds 未同步 gala_star_bond_selection");
    }

    [Theory]
    [InlineData("challenge_success_1_1.jpg")] // 暗金背景纹理族（离线标定最高分负样本族）
    [InlineData("companion_selection_single_unselected_2048x1152.png")] // 2048×1152 非基准分辨率+金框选择弹框族
    [InlineData("preparation_1_1.jpg")] // 审查 P2-2：备战页=最高代价误命中场景（泵会在正常运营中对它点头像）
    [InlineData("preparation_five_cards_2048x1152.png")] // 备战页五卡布局（卡片行与弹框卡片行同区）
    [InlineData("preparation_1_3_stable_2559x1439.png")] // 1-3 备战页 2559×1439
    [InlineData("investment_environment.jpg")] // 投资环境页（开局序列，误命中=浪费一局）
    public void GoldTintedNonPopupFramesNeverClassifyAsGalaBondPopup(string fixtureName)
    {
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            fixtureName));
        using var matcher = new OpenCvTemplateMatcher();
        var full = new TemplateGamePageClassifier(matcher, config.Pages);

        Assert.False(
            string.Equals(
                full.Classify(frame)?.PageId,
                GalaPageId,
                StringComparison.OrdinalIgnoreCase),
            $"负样本 {fixtureName} 被误判为盛会弹框");
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
