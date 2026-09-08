using CurrencyWarsAssistant.App.FrameSandbox;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class FrameSandboxPlayerTests
{
    [Fact]
    public async Task AcquireFrame_ServesCurrentStepFrame_AndAdvancesOnExpectedOps()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(directory);
        using var player = CreatePlayer(script, directory);
        var capture = new FileSequenceGameCapture(player.AcquireFrame);
        var window = new StubWindowService().Window;

        var frame1 = await capture.CaptureAsync(window, CancellationToken.None);
        Assert.Equal(4, frame1.Width);

        // 等待期重复捕获同一帧（引擎轮询语义）。
        var frameAgain = await capture.CaptureAsync(window, CancellationToken.None);
        Assert.Equal(4, frameAgain.Width);
        Assert.NotSame(frame1.BgraPixels, frameAgain.BgraPixels);

        player.Record(Op(SandboxOperationRecord.KindClick, 105, 92));
        var frame2 = await capture.CaptureAsync(window, CancellationToken.None);
        Assert.Equal(4, frame2.Width);

        player.Record(Op(SandboxOperationRecord.KindClick, 200, 200));
        Assert.Equal(
            FrameSandboxVerdict.StatusPass,
            player.FinalVerdict!.Status);
        Assert.Equal(0, player.FinalVerdict.ViolationCount);
    }

    [Fact]
    public void OpOutsideTolerance_IsViolation_AndStillSatisfiable()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(directory);
        using var player = CreatePlayer(script, directory);

        player.Record(Op(SandboxOperationRecord.KindClick, 140, 100)); // |40|>30
        player.Record(Op(SandboxOperationRecord.KindClick, 100, 100));

        Assert.Null(player.FinalVerdict);
        Assert.Equal(2, player.Status.StepIndex);
        Assert.Equal(1, player.Status.ViolationCount);

        player.Record(Op(SandboxOperationRecord.KindClick, 200, 200));
        Assert.Equal(
            FrameSandboxVerdict.StatusCompletedWithViolations,
            player.FinalVerdict!.Status);
    }

    [Fact]
    public void WrongOperationKind_IsViolation()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(directory);
        using var player = CreatePlayer(script, directory);

        player.Record(Op(SandboxOperationRecord.KindPressKey, null, null, key: "f"));
        Assert.Equal(1, player.Status.ViolationCount);
        Assert.Equal(1, player.Status.StepIndex);
    }

    [Fact]
    public void RepeatedClicks_AreTolerated_NotViolations()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(directory);
        using var player = CreatePlayer(script, directory);

        player.Record(Op(SandboxOperationRecord.KindClick, 100, 100)); // 推进到第2步
        player.Record(Op(SandboxOperationRecord.KindClick, 100, 100)); // 重复
        player.Record(Op(SandboxOperationRecord.KindClick, 101, 99));  // 重复（容差内）
        Assert.Equal(0, player.Status.ViolationCount);

        player.Record(Op(SandboxOperationRecord.KindClick, 200, 200));
        Assert.Equal(
            FrameSandboxVerdict.StatusPass,
            player.FinalVerdict!.Status);
    }

    [Fact]
    public void StepTimeout_FailsVerdict_WithRemainingExpectations()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(
            directory,
            maxWaitSeconds: 10);
        var clock = new FrameSandboxTestUtil.FakeTimeProvider();
        using var player = CreatePlayer(script, directory, clock);

        player.AcquireFrame(); // 步时钟起点
        clock.Advance(TimeSpan.FromSeconds(11));
        player.AcquireFrame();

        Assert.NotNull(player.FinalVerdict);
        Assert.Equal(
            FrameSandboxVerdict.StatusFailed,
            player.FinalVerdict.Status);
        Assert.Equal(1, player.FinalVerdict.ViolationCount);

        // 终局后的操作只记录不再判定。
        player.Record(Op(SandboxOperationRecord.KindClick, 100, 100));
        Assert.Equal(1, player.FinalVerdict.ViolationCount);
    }

    [Fact]
    public void TimeoutNotReached_DoesNotFail()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(
            directory,
            maxWaitSeconds: 10);
        var clock = new FrameSandboxTestUtil.FakeTimeProvider();
        using var player = CreatePlayer(script, directory, clock);

        player.AcquireFrame();
        clock.Advance(TimeSpan.FromSeconds(9));
        player.AcquireFrame();

        Assert.Null(player.FinalVerdict);
    }

    [Fact]
    public void Drag_MatchesSourceCenterAndTarget()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var frame = FrameSandboxTestUtil.WriteTestPng(directory, "f.png", 9, 9, 9);
        var script = new FrameSandboxScript(
            "drag",
            directory,
            [
                new FrameSandboxStep(
                    "f.png",
                    frame,
                    [new FrameSandboxExpectation(
                        FrameSandboxExpectation.OpDrag,
                        100,
                        100,
                        300,
                        400,
                        null,
                        null,
                        30,
                        "拖拽")],
                    60),
                new FrameSandboxStep("terminal.png",
                    FrameSandboxTestUtil.WriteTestPng(directory, "t.png", 1, 1, 1),
                    [],
                    60),
            ]);
        using var player = CreatePlayer(script, directory);

        // 源点在容差内但目标点超出 → 违规。
        player.Record(Drag(105, 95, 340, 400));
        Assert.Equal(1, player.Status.ViolationCount);
        Assert.Equal(1, player.Status.StepIndex);

        player.Record(Drag(100, 100, 300, 400));
        Assert.Equal(
            FrameSandboxVerdict.StatusCompletedWithViolations,
            player.FinalVerdict!.Status);
        Assert.Equal(1, player.FinalVerdict.ViolationCount);
    }

    [Fact]
    public void PressKey_MatchesCaseInsensitive()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var frame = FrameSandboxTestUtil.WriteTestPng(directory, "f.png", 2, 2, 2);
        var script = new FrameSandboxScript(
            "key",
            directory,
            [
                new FrameSandboxStep(
                    "f.png",
                    frame,
                    [new FrameSandboxExpectation(
                        FrameSandboxExpectation.OpPressKey,
                        null,
                        null,
                        null,
                        null,
                        "f",
                        null,
                        30,
                        "按F")],
                    60),
                new FrameSandboxStep("terminal.png",
                    FrameSandboxTestUtil.WriteTestPng(directory, "t.png", 1, 1, 1),
                    [],
                    60),
            ]);
        using var player = CreatePlayer(script, directory);

        player.Record(Op(
            SandboxOperationRecord.KindPressKey,
            null,
            null,
            key: "F"));
        Assert.Equal(
            FrameSandboxVerdict.StatusPass,
            player.FinalVerdict!.Status);
    }

    [Fact]
    public void RepeatsBeyondCap_ProduceViolation()
    {
        // P2-3（对抗审查）：重复豁免必须有上限——无上限会把弃局链循环操作吞成合法。
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(directory);
        using var player = CreatePlayer(script, directory);

        player.Record(Op(SandboxOperationRecord.KindClick, 100, 100)); // 推进到第2步
        foreach (var _ in Enumerable.Range(0, FrameSandboxPlayer.MaxRepeatsPerExpectation + 2))
        {
            player.Record(Op(SandboxOperationRecord.KindClick, 100, 100));
        }

        Assert.Equal(1, player.Status.ViolationCount); // 破线只记一次，不刷屏
        Assert.Contains(
            "repeat-cap-exceeded",
            ReadAllShared(Path.Combine(
                player.OutputDirectory,
                "sandbox-violations.jsonl")));
    }

    [Fact]
    public void OutputFiles_AreWritten()
    {
        var directory = FrameSandboxTestUtil.NewTempDirectory();
        var script = FrameSandboxTestUtil.BuildSimpleScript(directory);
        using var player = CreatePlayer(script, directory);

        player.Record(Op(SandboxOperationRecord.KindClick, 100, 100));
        player.Record(Op(SandboxOperationRecord.KindClick, 200, 200));

        var ops = ReadAllShared(Path.Combine(player.OutputDirectory, "sandbox-ops.jsonl"));
        Assert.Contains("sandbox-start", ops);
        Assert.Contains("\"kind\":\"click\"", ops);
        Assert.Contains("SatisfiesPending", ops);

        Assert.Equal(
            FrameSandboxVerdict.StatusPass,
            player.FinalVerdict!.Status);
        var verdict = ReadAllShared(
            Path.Combine(player.OutputDirectory, "sandbox-verdict.txt"));
        Assert.Contains("verdict=PASS", verdict);
        Assert.Contains("violations=0", verdict);

        // 零违规时 violations 文件可以不存在，但不得有内容造假。
        var violationsPath = Path.Combine(
            player.OutputDirectory,
            "sandbox-violations.jsonl");
        Assert.True(
            !File.Exists(violationsPath) ||
            new FileInfo(violationsPath).Length == 0);
    }

    /// <summary>沙箱产物以 FileShare.ReadWrite 持有（值守可边跑边读），读方须配套。</summary>
    private static string ReadAllShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static FrameSandboxPlayer CreatePlayer(
        FrameSandboxScript script,
        string directory,
        TimeProvider? timeProvider = null)
    {
        var output = Path.Combine(directory, "out");
        return new FrameSandboxPlayer(script, output, timeProvider);
    }

    private static SandboxOperationRecord Op(
        string kind,
        int? x,
        int? y,
        int? toX = null,
        int? toY = null,
        string? key = null,
        string? modifier = null) =>
        new(
            Sequence: 1,
            At: DateTimeOffset.Now,
            Kind: kind,
            X: x,
            Y: y,
            ToX: toX,
            ToY: toY,
            Key: key,
            Modifier: modifier,
            Message: "test");

    private static SandboxOperationRecord Drag(int x, int y, int toX, int toY) =>
        Op(
            SandboxOperationRecord.KindDrag,
            x,
            y,
            toX,
            toY);
}
