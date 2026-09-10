using CurrencyWarsAssistant.App.FrameSandbox;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 指南风暴探针（T3，2026-09-11）：赛季刷新（09-07）后指南默认落"每日实训"页
/// （不在识别表）曾致 2 小时导航死循环——修复 bc56a6e 补识别表+导航自愈节点。
/// 本测试把该实机 BUG 沉淀为沙箱探针：
/// ① 帧级守卫：storm 实况帧（2560×1440）必须被真实分类器判成对应页面——
///   防止"识别表条目存在但模板在实况分辨率下失效"的回归（风暴当夜病理）；
/// ② 回放：normal_hud→打开指南→默认落每日实训→切第三页签→进货币战争→进局，
///   按引擎操作流走完判 PASS 零违规。
/// </summary>
public sealed class GuideStormProbeTests
{
    private static readonly GameWindowInfo Window =
        new StubWindowService().Window;

    private static string StormScriptPath => Path.Combine(
        FrameSandboxTestUtil.RepositoryRoot,
        "tests", "CurrencyWarsAssistant.Tests",
        "Fixtures", "FrameSandbox", "guide_storm_probe.json");

    [Fact]
    public void StormScript_EveryFrame_ClassifiesToExpectedPage()
    {
        var expectedPages = new[]
        {
            "normal_hud",
            "guide_daily_training",
            "guide_currency_wars",
            "update_popup",
            "score_popup",
            "currency_wars_home",
            "mode_selection",
            "rank_difficulty",
            "enemy_overview",
            "investment_environment",
            "preparation_generic",
        };
        var script = FrameSandboxScriptLoader.Load(StormScriptPath);
        Assert.Equal(expectedPages.Length, script.Steps.Count);

        var config = GamePageRecognitionConfig.Load(Path.Combine(
            FrameSandboxTestUtil.RepositoryRoot,
            "config", "page-recognition.1920x1080.json"));
        using var matcher = new OpenCvTemplateMatcher();
        var classifier = new TemplateGamePageClassifier(matcher, config.Pages);

        for (var index = 0; index < script.Steps.Count; index++)
        {
            var frame = CaptureFrameLoader.LoadFile(
                script.Steps[index].ImageFullPath);
            var result = classifier.Classify(frame);
            Assert.True(
                result is not null,
                $"第 {index + 1} 步 {script.Steps[index].Image} 无法分类");
            Assert.True(
                string.Equals(
                    result.PageId,
                    expectedPages[index],
                    StringComparison.Ordinal),
                $"第 {index + 1} 步 {script.Steps[index].Image} 分类为 " +
                $"{result.PageId}({result.Confidence:P1})，期望 {expectedPages[index]}");
        }
    }

    [Fact]
    public async Task StormScript_DailyTrainingDetour_HappyPath_JudgesPass()
    {
        var script = FrameSandboxScriptLoader.Load(StormScriptPath);
        var output = Path.Combine(
            FrameSandboxTestUtil.NewTempDirectory(),
            "out");
        using var player = new FrameSandboxPlayer(script, output);
        var controller = new RecordingInputController(player);

        // 第1帧 normal_hud：Alt+点击打开指南。
        await Click(controller, 1598, 45, modifier: InputKey.LeftAlt);
        // 第2帧 guide_daily_training（默认落每日实训页）：切第三页签。
        await Click(controller, 605, 229);
        // 第3帧 guide_currency_wars（storm 实况帧）：参与点击 + 三次 F。
        await Click(controller, 1492, 878);
        await Press(controller, InputKey.F);
        await Press(controller, InputKey.F);
        await Press(controller, InputKey.F);
        // 第4/5帧：更新说明、积分奖励，各一次 Esc。
        await Press(controller, InputKey.Escape);
        await Press(controller, InputKey.Escape);
        // 第6帧 currency_wars_home：开始货币战争。
        await Click(controller, 1590, 976);

        // 快速刷开局：盲点连点 (1690,967) 覆盖 mode_selection/rank_difficulty 两帧。
        foreach (var _ in Enumerable.Range(0, 80))
        {
            await Click(controller, 1690, 967);
        }

        Assert.Equal(9, player.Status.StepIndex); // 1 基：已推进到第9步（enemy_overview）

        // 第9帧 enemy_overview：确认 + 盲点连点重复。
        await Click(controller, 1514, 985);
        foreach (var _ in Enumerable.Range(0, 60))
        {
            await Click(controller, 1514, 985);
        }

        Assert.Equal(10, player.Status.StepIndex); // 已推进到第10步（命中环境页）

        // 第10帧 命中 019：选中 (960,530) + 确认 (1083,984)。
        await Click(controller, 960, 530);
        await Click(controller, 1083, 984);

        // 终局帧 preparation_1_1 到达 → PASS 零违规。
        Assert.NotNull(player.FinalVerdict);
        Assert.Equal(FrameSandboxVerdict.StatusPass, player.FinalVerdict.Status);
        Assert.Equal(0, player.FinalVerdict.ViolationCount);
        Assert.Equal(11, player.FinalVerdict.StepsCompleted);

        var verdict = File.ReadAllText(Path.Combine(output, "sandbox-verdict.txt"));
        Assert.Contains("verdict=PASS", verdict);
    }

    /// <summary>帧沙箱专用测试设施（沙箱种子语义）：风暴链进局后备战帧的
    /// 决策依赖由 DECIDE 级 E2E 覆盖；本测试只需守卫导航链本身。</summary>
    private static Task Click(
        RecordingInputController controller,
        int x,
        int y,
        InputKey? modifier = null)
    {
        var target = new ClickTarget(
            $"({x},{y})",
            $"标准点({x},{y})",
            Window,
            new PixelRect(x - 20, y - 20, 40, 40));
        return modifier is { } key
            ? controller.ClickWithModifierAsync(target, key, Policy(), CancellationToken.None)
            : controller.ClickAsync(target, Policy(), CancellationToken.None);
    }

    private static Task Press(
        RecordingInputController controller,
        InputKey key) =>
        controller.PressKeyAsync(Window, key, Policy(), CancellationToken.None);

    private static ActionPolicy Policy() => new();
}
