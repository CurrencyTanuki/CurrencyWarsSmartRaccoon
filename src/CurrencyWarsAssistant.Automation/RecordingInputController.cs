using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Automation;

/// <summary>
/// 帧沙箱（docs/SANDBOX_FEASIBILITY_20260908.md）的记录型输入控制器：
/// 不发送任何真实鼠标/键盘事件——把每次操作记为 SandboxOperationRecord 交给
/// 裁判（sink），并按 ActionResult.Success 返回（可行性案：全部操作返回成功，
/// 失败注入属 Phase 3 专项）。
/// </summary>
public sealed class RecordingInputController : IInputController
{
    private readonly ISandboxOperationSink? _sink;
    private long _sequence;

    public RecordingInputController(ISandboxOperationSink? sink = null)
    {
        _sink = sink;
    }

    public Task<ActionResult> ClickAsync(
        ClickTarget target,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var center = target.ClientBounds.Center;
        return Task.FromResult(Publish(
            SandboxOperationRecord.KindClick,
            center,
            ToX: null,
            ToY: null,
            Key: null,
            Modifier: null,
            $"[沙箱记录] 点击 {target.DisplayName} @({center.X},{center.Y})"));
    }

    public Task<ActionResult> DragAsync(
        ClickTarget source,
        PixelPoint targetClientPoint,
        TimeSpan duration,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var center = source.ClientBounds.Center;
        return Task.FromResult(Publish(
            SandboxOperationRecord.KindDrag,
            center,
            targetClientPoint.X,
            targetClientPoint.Y,
            Key: null,
            Modifier: null,
            $"[沙箱记录] 拖拽 {source.DisplayName} @({center.X},{center.Y})" +
            $"→({targetClientPoint.X},{targetClientPoint.Y})"));
    }

    public Task<ActionResult> PressKeyAsync(
        GameWindowInfo window,
        InputKey key,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Publish(
            SandboxOperationRecord.KindPressKey,
            null,
            null,
            null,
            key,
            Modifier: null,
            $"[沙箱记录] 按键 {SandboxOperationRecord.KeyName(key)}"));
    }

    public Task<ActionResult> ClickWithModifierAsync(
        ClickTarget target,
        InputKey modifier,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var center = target.ClientBounds.Center;
        return Task.FromResult(Publish(
            SandboxOperationRecord.KindClickWithModifier,
            center,
            null,
            null,
            null,
            modifier,
            $"[沙箱记录] {SandboxOperationRecord.KeyName(modifier).ToUpperInvariant()}+" +
            $"点击 {target.DisplayName} @({center.X},{center.Y})"));
    }

    private ActionResult Publish(
        string kind,
        PixelPoint? primary,
        int? ToX,
        int? ToY,
        InputKey? Key,
        InputKey? Modifier,
        string message)
    {
        var record = new SandboxOperationRecord(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.Now,
            kind,
            primary?.X,
            primary?.Y,
            ToX,
            ToY,
            Key is null ? null : SandboxOperationRecord.KeyName(Key.Value),
            Modifier is null ? null : SandboxOperationRecord.KeyName(Modifier.Value),
            message);
        _sink?.Record(record);
        return ActionResult.Success(message);
    }
}
