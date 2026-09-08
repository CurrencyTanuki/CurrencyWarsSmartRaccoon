using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class FrameSandboxInfrastructureTests
{
    [Fact]
    public void StubWindowService_ReturnsReadySandboxWindow()
    {
        var service = new StubWindowService("测试窗口");
        var candidates = service.FindCandidates();

        var window = Assert.Single(candidates);
        Assert.True(window.IsReadyForAutomation);
        Assert.Equal(1920, window.ClientArea.Width);
        Assert.Equal(1080, window.ClientArea.Height);
        Assert.Equal(StubWindowService.SandboxWindowHandle, window.Handle);
        Assert.NotEqual(0, window.Handle);

        Assert.Same(window, service.Refresh(window.Handle));
        Assert.Same(window, service.Refresh(0x1234));
        Assert.Null(service.Refresh(0));
        Assert.True(service.IsForeground(window));
        Assert.True(service.BringToForeground(window));
    }

    [Fact]
    public async Task AlwaysForegroundGuard_ReturnsImmediately()
    {
        var service = new StubWindowService();
        var guard = new AlwaysForegroundGuard(service);

        Assert.Equal(TimeSpan.Zero, guard.TotalPausedDuration);

        var window = service.Window;
        var byWindow = await guard.WaitUntilForegroundAsync(
            window,
            CancellationToken.None);
        Assert.Same(window, byWindow);

        var byHandle = await guard.WaitUntilForegroundAsync(
            window.Handle,
            CancellationToken.None);
        Assert.Same(window, byHandle);
    }

    [Fact]
    public async Task RecordingInputController_RecordsOperations_WithCenterPoints()
    {
        var records = new List<SandboxOperationRecord>();
        var sink = new TestSink(records);
        var controller = new RecordingInputController(sink);
        var window = new StubWindowService().Window;

        var clickTarget = new ClickTarget(
            "btn",
            "按钮",
            window,
            new PixelRect(80, 90, 40, 20));
        var click = await controller.ClickAsync(
            clickTarget,
            new ActionPolicy(),
            CancellationToken.None);
        Assert.True(click.Succeeded);
        Assert.Equal(100, records[0].X);
        Assert.Equal(100, records[0].Y);
        Assert.Equal(SandboxOperationRecord.KindClick, records[0].Kind);

        var drag = await controller.DragAsync(
            clickTarget,
            new PixelPoint(300, 400),
            TimeSpan.FromMilliseconds(250),
            new ActionPolicy(),
            CancellationToken.None);
        Assert.True(drag.Succeeded);
        Assert.Equal(SandboxOperationRecord.KindDrag, records[1].Kind);
        Assert.Equal(100, records[1].X);
        Assert.Equal(100, records[1].Y);
        Assert.Equal(300, records[1].ToX);
        Assert.Equal(400, records[1].ToY);

        await controller.PressKeyAsync(
            window,
            InputKey.Escape,
            new ActionPolicy(),
            CancellationToken.None);
        Assert.Equal(SandboxOperationRecord.KindPressKey, records[2].Kind);
        Assert.Equal("escape", records[2].Key);

        await controller.ClickWithModifierAsync(
            clickTarget,
            InputKey.LeftAlt,
            new ActionPolicy(),
            CancellationToken.None);
        Assert.Equal(
            SandboxOperationRecord.KindClickWithModifier,
            records[3].Kind);
        Assert.Equal("leftalt", records[3].Modifier);
        Assert.Equal(100, records[3].X);
        Assert.Equal(100, records[3].Y);
    }

    [Fact]
    public async Task RecordingInputController_WithoutSink_StillSucceeds()
    {
        var controller = new RecordingInputController();
        var window = new StubWindowService().Window;
        var result = await controller.PressKeyAsync(
            window,
            InputKey.F,
            new ActionPolicy(),
            CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task FileSequenceGameCapture_ClonesPixels_StampsFreshTime()
    {
        var source = new CaptureFrame(
            2,
            2,
            8,
            new byte[2 * 2 * 4],
            new PixelRect(0, 0, 2, 2),
            DateTimeOffset.Now - TimeSpan.FromHours(1));
        var before = DateTimeOffset.Now;
        IGameCapture capture = new FileSequenceGameCapture(() => source);
        var window = new StubWindowService().Window;

        var frame = await capture.CaptureAsync(window, CancellationToken.None);

        Assert.Equal(2, frame.Width);
        Assert.NotSame(source.BgraPixels, frame.BgraPixels);
        Assert.True(frame.CapturedAt >= before);

        // 改写克隆不影响提供者持有的缓存帧（真实捕获语义：每次快照独立）。
        frame.BgraPixels[0] = 0xFF;
        Assert.Equal(0, source.BgraPixels[0]);

        // 接口默认实现：文件序列捕获器不提供流统计。
        Assert.Null(capture.StreamStats);
    }

    private sealed class TestSink(List<SandboxOperationRecord> records)
        : ISandboxOperationSink
    {
        public void Record(SandboxOperationRecord operation) => records.Add(operation);
    }
}
