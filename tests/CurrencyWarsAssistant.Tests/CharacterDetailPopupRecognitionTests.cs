using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// P1-B（2026-09-09 修复批）：角色详情残留框的真帧识别守卫。
/// G15 局实锤帧（G15_detail_7900.png，2560×1440 原生）入库为夹具——全量分类器与
/// 自动化子集都必须认出 character_detail_popup（此前识别表无此页→残留框挂 90% 局
/// 时长污染右侧槽位识别）。标定：彩色双锚点（详情按钮 0.976/负 max 0.44 + 装备推荐
/// 按钮 0.976/负 max 0.52，77 张负样本全帧最坏情形），阈值 0.85，两侧余量 ≥0.12。
/// 负样本守卫：备战页/商店页等高频页面绝不误判为详情框（误判=守卫在正常运营中
/// 乱点空白带）。
/// </summary>
public sealed class CharacterDetailPopupRecognitionTests
{
    private const string DetailPopupPageId =
        CurrencyWarsRejectedOpeningRecovery.CharacterDetailPopupPageId;

    [Fact]
    public void DetailFrameClassifiesAsCharacterDetailPopupOnBothClassifiers()
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
            "character_detail_popup_g15_7900.png"));

        using var matcher = new OpenCvTemplateMatcher();

        // 全量分类器（导航/页面识别同源）
        var full = new TemplateGamePageClassifier(matcher, config.Pages);
        var fullResult = full.Classify(frame);
        Assert.True(
            string.Equals(fullResult?.PageId, DetailPopupPageId, StringComparison.OrdinalIgnoreCase),
            $"全量分类器未认出角色详情框：{(fullResult is null ? "无匹配" : fullResult.PageId)}");

        // 自动化子集（弹框守卫用的 IGamePageClassifier 同源）
        var automation = AutomationPageIds.Create(matcher, config.Pages);
        var autoResult = automation.Classify(frame);
        Assert.True(
            string.Equals(autoResult?.PageId, DetailPopupPageId, StringComparison.OrdinalIgnoreCase),
            $"自动化子集未认出角色详情框：{(autoResult is null ? "无匹配" : autoResult.PageId)}");

        // 坑48 纪律守卫：fast 集白名单必须同步包含（漏③=fast 层"未知"覆盖
        // 全量结论、I1 恒报未知——2026-09-05 断线自愈实锤病理）。
        var fastIds = typeof(Phase2FastPageClassifier)
            .GetField("FastPageIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as System.Collections.Generic.IReadOnlySet<string>;
        Assert.True(fastIds?.Contains(DetailPopupPageId) == true, "FastPageIds 未同步 character_detail_popup");
    }

    [Theory]
    [InlineData("preparation_1_1.jpg")] // 备战页=最高代价误命中场景（守卫会在正常运营中点空白）
    [InlineData("preparation_1_3_after_shop_2559x1439.png")] // 1-3 备战页 2559×1439（与正样本同分辨率族）
    [InlineData("reward_shop_after_two_purchases_2048x1152.png")] // 商店页（含按钮族）
    [InlineData("companion_selection_single_unselected_2048x1152.png")] // 同为右侧面板族的选择弹框
    [InlineData("gala_star_bond_popup_stall_20260906.png")] // 盛会弹框（同为备战页浮层）
    public void PreparationFamilyFramesNeverClassifyAsCharacterDetailPopup(string fixtureName)
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
                DetailPopupPageId,
                StringComparison.OrdinalIgnoreCase),
            $"负样本 {fixtureName} 被误判为角色详情框");
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
