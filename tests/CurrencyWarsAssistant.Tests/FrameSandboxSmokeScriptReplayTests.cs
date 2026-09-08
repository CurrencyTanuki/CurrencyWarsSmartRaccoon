using CurrencyWarsAssistant.App.FrameSandbox;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 冒烟脚本全链路回放：加载真实 smoke_home_to_preparation.json，
/// 按 M8 快速刷开局路径（navigation-flow.json + FastPathFromHome 盲点连点）
/// 经 RecordingInputController 注入操作流，断言裁判判 PASS 零违规；
/// 再注入一次脚本外操作（已命中环境页不该出现的机制刷新点）断言判违规。
/// 这是 Phase 1 骨架的端到端（无窗口）验证：脚本=驱动器+裁判、四件套协同。
/// </summary>
public sealed class FrameSandboxSmokeScriptReplayTests
{
    private static readonly GameWindowInfo Window =
        new StubWindowService().Window;

    [Fact]
    public async Task SmokeScript_FastRerollHappyPath_JudgesPass()
    {
        var script = FrameSandboxScriptLoader.Load(FrameSandboxTestUtil.SmokeScriptPath);
        var output = Path.Combine(
            FrameSandboxTestUtil.NewTempDirectory(),
            "out");
        using var player = new FrameSandboxPlayer(script, output);
        var controller = new RecordingInputController(player);

        // 第1帧 normal_hud：Alt+点击打开指南。
        await Click(controller, 1598, 45, modifier: InputKey.LeftAlt);
        // 第2帧 guide_shell：切第三个页签。
        await Click(controller, 602, 211);
        // 第3帧 guide_currency_wars：参与点击 + 三次 F。
        await Click(controller, 1492, 878);
        await Press(controller, InputKey.F);
        await Press(controller, InputKey.F);
        await Press(controller, InputKey.F);
        // 第4/5帧：更新说明、积分奖励，各一次 Esc。
        await Press(controller, InputKey.Escape);
        await Press(controller, InputKey.Escape);
        // 第6帧 currency_wars_home：开始货币战争。
        await Click(controller, 1590, 976);

        // 快速刷开局：盲点连点"开始本局"(1690,967) 4 秒 ×50ms≈80 次。
        // 第1次满足 mode_selection 期望、第2次满足 rank_difficulty 期望，
        // 其余全是已满足期望的重复（不算违规）。
        foreach (var _ in Enumerable.Range(0, 80))
        {
            await Click(controller, 1690, 967);
        }

        Assert.Equal(9, player.Status.StepIndex); // Status 为 1 基：已推进到第9步（enemy_overview）

        // 第8帧 enemy_overview：确认 + 同位置盲点连点3秒≈60 次（重复）。
        await Click(controller, 1514, 985);
        foreach (var _ in Enumerable.Range(0, 60))
        {
            await Click(controller, 1514, 985);
        }

        Assert.Equal(10, player.Status.StepIndex); // Status 为 1 基：已推进到第10步（命中环境页）

        // 第10帧 命中环境页：019 在第2卡 → 选中 (960,530) + 确认 (1083,984)。
        await Click(controller, 960, 530);
        await Click(controller, 960, 530); // 重复点击=已满足期望的重复，不算违规
        await Click(controller, 1083, 984);

        // 终局帧 preparation_1_1 到达 → PASS 零违规。
        Assert.NotNull(player.FinalVerdict);
        Assert.Equal(FrameSandboxVerdict.StatusPass, player.FinalVerdict.Status);
        Assert.Equal(0, player.FinalVerdict.ViolationCount);
        Assert.Equal(11, player.FinalVerdict.StepsCompleted);

        // 终局后的操作（如 DECIDE 在 1-1 继续开金矿）只记录不判定。
        await Click(controller, 960, 540);
        Assert.Equal(0, player.FinalVerdict.ViolationCount);

        var verdict = File.ReadAllText(Path.Combine(output, "sandbox-verdict.txt"));
        Assert.Contains("verdict=PASS", verdict);
    }

    [Fact]
    public async Task SmokeScript_UnexpectedRefreshClick_IsViolation()
    {
        var script = FrameSandboxScriptLoader.Load(FrameSandboxTestUtil.SmokeScriptPath);
        using var player = new FrameSandboxPlayer(
            script,
            FrameSandboxTestUtil.NewTempDirectory());
        var controller = new RecordingInputController(player);

        await FastForwardToEnvironmentFrameAsync(player, controller);

        // 剧本外操作：本帧已命中 019（第2卡），正确行为是选中(960,530)+确认(1083,984)；
        // 点机制刷新=未命中路径的指纹，在命中帧上出现=偏离。
        await Click(controller, 676, 984);
        Assert.Equal(1, player.Status.ViolationCount);

        // 之后按剧本走完：判 CompletedWithViolations 而非 PASS。
        await Click(controller, 960, 530);
        await Click(controller, 1083, 984);
        Assert.NotNull(player.FinalVerdict);
        Assert.Equal(
            FrameSandboxVerdict.StatusCompletedWithViolations,
            player.FinalVerdict.Status);
        Assert.Equal(1, player.FinalVerdict.ViolationCount);
    }

    [Fact]
    public void SmokeScript_FirstFrame_IsDecodable()
    {
        var script = FrameSandboxScriptLoader.Load(FrameSandboxTestUtil.SmokeScriptPath);
        using var player = new FrameSandboxPlayer(
            script,
            FrameSandboxTestUtil.NewTempDirectory());
        var frame = player.AcquireFrame();
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
    }

    /// <summary>
    /// 帧级守卫（对抗审查 P1/P2-1 整改）：冒烟脚本每一帧用真实模板分类器
    /// （page-recognition.1920x1080.json 全锚点）分类，必须判成对应页面。
    /// 防止"拿错截图"（曾实锤：命中环境帧误用备战页截图，操作流回放测不出）。
    /// </summary>
    [Fact]
    public void SmokeScript_EveryFrame_ClassifiesToExpectedPage()
    {
        var expectedPages = new[]
        {
            "normal_hud",
            "guide_shell",
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
        var script = FrameSandboxScriptLoader.Load(FrameSandboxTestUtil.SmokeScriptPath);
        Assert.Equal(expectedPages.Length, script.Steps.Count);

        var config = GamePageRecognitionConfig.Load(Path.Combine(
            FrameSandboxTestUtil.RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        using var matcher = new OpenCvTemplateMatcher();
        var classifier = new TemplateGamePageClassifier(matcher, config.Pages);

        for (var index = 0; index < script.Steps.Count; index++)
        {
            var frame = CaptureFrameLoader.LoadFile(script.Steps[index].ImageFullPath);
            var result = classifier.Classify(frame);
            Assert.True(
                result is not null,
                $"第 {index + 1} 步 {script.Steps[index].Image} 无法分类");
            Assert.True(
                string.Equals(result.PageId, expectedPages[index], StringComparison.Ordinal),
                $"第 {index + 1} 步 {script.Steps[index].Image} 分类为 " +
                $"{result.PageId}({result.Confidence:P1})，期望 {expectedPages[index]}");
        }
    }

    /// <summary>按真实操作序列推进到第9步（命中环境页成为当前帧）。</summary>
    private static async Task FastForwardToEnvironmentFrameAsync(
        FrameSandboxPlayer player,
        RecordingInputController controller)
    {
        await Click(controller, 1598, 45, modifier: InputKey.LeftAlt);
        await Click(controller, 602, 211);
        await Click(controller, 1492, 878);
        await Press(controller, InputKey.F);
        await Press(controller, InputKey.F);
        await Press(controller, InputKey.F);
        await Press(controller, InputKey.Escape);
        await Press(controller, InputKey.Escape);
        await Click(controller, 1590, 976);
        foreach (var _ in Enumerable.Range(0, 80))
        {
            await Click(controller, 1690, 967);
        }

        await Click(controller, 1514, 985);
        foreach (var _ in Enumerable.Range(0, 60))
        {
            await Click(controller, 1514, 985);
        }

        Assert.Equal(10, player.Status.StepIndex);
    }

    /// <summary>构造以标准点为中心的 ClickTarget 并发点击（包围盒中心=标准点）。</summary>
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

    private static Task Press(RecordingInputController controller, InputKey key) =>
        controller.PressKeyAsync(Window, key, Policy(), CancellationToken.None);

    private static ActionPolicy Policy() => new();
}
