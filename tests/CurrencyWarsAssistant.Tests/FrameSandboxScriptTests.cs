using CurrencyWarsAssistant.App.FrameSandbox;

namespace CurrencyWarsAssistant.Tests;

public sealed class FrameSandboxScriptTests
{
    [Fact]
    public void Load_SmokeScript_ParsesStepsAndExpects()
    {
        var script = FrameSandboxScriptLoader.Load(FrameSandboxTestUtil.SmokeScriptPath);
        Assert.Equal("smoke_home_to_preparation", script.Name);
        Assert.Equal(11, script.Steps.Count);

        var first = script.Steps[0];
        Assert.EndsWith("normal_hud.jpg", first.ImageFullPath, StringComparison.Ordinal);
        Assert.True(File.Exists(first.ImageFullPath));
        var guide = Assert.Single(first.Expect);
        Assert.Equal(FrameSandboxExpectation.OpClickWithModifier, guide.Op);
        Assert.Equal(1598, guide.X);
        Assert.Equal(45, guide.Y);
        Assert.Equal("leftalt", guide.Modifier);
        Assert.Equal(FrameSandboxScriptLoader.DefaultTolerance, guide.Tolerance);

        // 快速刷开局：mode_selection/rank_difficulty 两帧的期望点都是开始本局位置。
        var modeSelection = script.Steps[6];
        var modeClick = Assert.Single(modeSelection.Expect);
        Assert.Equal(1690, modeClick.X);
        Assert.Equal(967, modeClick.Y);
        var rankDifficulty = script.Steps[7];
        var rankClick = Assert.Single(rankDifficulty.Expect);
        Assert.Equal(1690, rankClick.X);
        Assert.Equal(967, rankClick.Y);

        // 命中环境帧：019 在第2卡，选中+确认两个期望。
        var environment = script.Steps[9];
        Assert.EndsWith(
            "investment_environment_hit_019_1920.png",
            environment.ImageFullPath,
            StringComparison.Ordinal);
        Assert.Equal(2, environment.Expect.Count);
        Assert.Equal(960, environment.Expect[0].X);
        Assert.Equal(530, environment.Expect[0].Y);
        Assert.Equal(180, environment.MaxWaitSeconds);

        // 终局帧：空期望只允许在最后一步。
        var terminal = script.Steps[10];
        Assert.Empty(terminal.Expect);
        Assert.EndsWith("preparation_1_1.jpg", terminal.ImageFullPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingImage_Throws()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, """
            { "steps": [ { "image": "no_such_frame.png",
                "expect": [ { "op": "click", "near": [1, 2] } ] } ] }
            """);
        var error = Assert.Throws<InvalidDataException>(
            () => FrameSandboxScriptLoader.Load(path));
        Assert.Contains("帧图片不存在", error.Message);
    }

    [Fact]
    public void Load_UnknownOp_Throws()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var frame = FrameSandboxTestUtil.WriteTestPng(directory, "f.png", 1, 2, 3);
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, """
            { "steps": [ { "image": "f.png",
                "expect": [ { "op": "swipe", "near": [1, 2] } ] } ] }
            """);
        Assert.Throws<InvalidDataException>(() => FrameSandboxScriptLoader.Load(path));
        Assert.True(File.Exists(frame));
    }

    [Fact]
    public void Load_ClickWithoutNear_Throws()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        FrameSandboxTestUtil.WriteTestPng(directory, "f.png", 1, 2, 3);
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, """
            { "steps": [ { "image": "f.png",
                "expect": [ { "op": "click" } ] } ] }
            """);
        var error = Assert.Throws<InvalidDataException>(
            () => FrameSandboxScriptLoader.Load(path));
        Assert.Contains("near", error.Message);
    }

    [Fact]
    public void Load_PressKeyWithoutKey_Throws()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        FrameSandboxTestUtil.WriteTestPng(directory, "f.png", 1, 2, 3);
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, """
            { "steps": [ { "image": "f.png",
                "expect": [ { "op": "presskey" } ] } ] }
            """);
        Assert.Throws<InvalidDataException>(() => FrameSandboxScriptLoader.Load(path));
    }

    [Fact]
    public void Load_EmptyExpectOnNonLastStep_Throws()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        FrameSandboxTestUtil.WriteTestPng(directory, "a.png", 1, 2, 3);
        FrameSandboxTestUtil.WriteTestPng(directory, "b.png", 4, 5, 6);
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, """
            { "steps": [
                { "image": "a.png", "expect": [] },
                { "image": "b.png",
                  "expect": [ { "op": "click", "near": [1, 2] } ] } ] }
            """);
        var error = Assert.Throws<InvalidDataException>(
            () => FrameSandboxScriptLoader.Load(path));
        Assert.Contains("空 expect", error.Message);
    }

    [Fact]
    public void Load_UnknownKey_Throws()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        FrameSandboxTestUtil.WriteTestPng(directory, "f.png", 1, 2, 3);
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, """
            { "steps": [ { "image": "f.png",
                "expect": [ { "op": "presskey", "key": "space" } ] } ] }
            """);
        var error = Assert.Throws<InvalidDataException>(
            () => FrameSandboxScriptLoader.Load(path));
        Assert.Contains("key", error.Message);
    }

    [Fact]
    public void Load_EmptySteps_Throws()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, "{ \"steps\": [] }");
        Assert.Throws<InvalidDataException>(() => FrameSandboxScriptLoader.Load(path));
    }

    [Fact]
    public void LaunchOptions_ParsesSwitchForms()
    {
        var parsed = FrameSandboxLaunchOptions.Parse(new[]
        {
            "--frame-sandbox", @"C:\frames\smoke.json",
        });
        Assert.NotNull(parsed);
        Assert.Equal(@"C:\frames\smoke.json", parsed.ScriptPath);
        Assert.Null(parsed.OutputDirectoryOverride);

        var inline = FrameSandboxLaunchOptions.Parse(new[]
        {
            "--frame-sandbox=C:\\frames\\smoke.json",
            "--frame-sandbox-out=D:\\sandbox-out",
        });
        Assert.NotNull(inline);
        Assert.Equal(@"C:\frames\smoke.json", inline.ScriptPath);
        Assert.Equal(@"D:\sandbox-out", inline.OutputDirectoryOverride);

        Assert.Null(FrameSandboxLaunchOptions.Parse(new[] { "--command-test" }));
    }

    [Fact]
    public void LaunchOptions_DanglingSwitch_Throws()
    {
        // P2-2（对抗审查）：开关缺路径必须显式报错，绝不静默降级为正式模式。
        Assert.Throws<ArgumentException>(
            () => FrameSandboxLaunchOptions.Parse(new[] { "--frame-sandbox" }));
        Assert.Throws<ArgumentException>(
            () => FrameSandboxLaunchOptions.Parse(new[] { "--frame-sandbox-out" }));
        Assert.Throws<ArgumentException>(
            () => FrameSandboxLaunchOptions.Parse(new[] { "--frame-sandbox=" }));
    }

    [Fact]
    public void Load_SingleTerminalStepScript_Throws()
    {
        // P3-5（对抗审查）：单步空期望脚本永远等不到到达判定，只能超时误判。
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        FrameSandboxTestUtil.WriteTestPng(directory, "only.png", 1, 2, 3);
        var path = Path.Combine(directory, "script.json");
        File.WriteAllText(path, "{ \"steps\": [ { \"image\": \"only.png\", \"expect\": [] } ] }");
        var error = Assert.Throws<InvalidDataException>(
            () => FrameSandboxScriptLoader.Load(path));
        Assert.Contains("终局步骤", error.Message);
    }
}
