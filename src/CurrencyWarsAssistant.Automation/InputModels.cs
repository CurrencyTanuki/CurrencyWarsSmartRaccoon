using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Automation;

public sealed record ClickTarget(
    string Id,
    string DisplayName,
    GameWindowInfo Window,
    PixelRect ClientBounds);

public sealed class ActionPolicy
{
    public TimeSpan AfterActionDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan PointerSettleDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan MouseButtonHoldDelay { get; init; } = TimeSpan.Zero;
    public int MaximumPointerPlacementAttempts { get; init; } = 1;
    public int PointerArrivalTolerance { get; init; } = 2;
    public bool VerifyPointerArrivalBeforeClick { get; init; }
    public bool VerifyForegroundBeforeClick { get; init; }
    public int MaximumClientSizeDrift { get; init; } = 2;
}

public sealed record InputActionDiagnostic(
    string InputMethod,
    nint WindowHandle,
    PixelRect ClientArea,
    PixelRect TargetClientBounds,
    PixelPoint TargetClientPoint,
    PixelPoint TargetScreenPoint,
    PixelPoint? CursorAfterMove,
    bool ForegroundBeforeSend,
    nint WindowAtTarget,
    int PointerPlacementAttempts,
    uint MoveSendCount,
    uint MouseDownSendCount,
    uint MouseUpSendCount,
    TimeSpan PointerSettleDelay,
    TimeSpan MouseButtonHoldDelay);

public sealed record ActionResult(
    bool Succeeded,
    string Message,
    InputActionDiagnostic? Diagnostic = null)
{
    public static ActionResult Success(
        string message,
        InputActionDiagnostic? diagnostic = null) =>
        new(true, message, diagnostic);

    public static ActionResult Failure(
        string message,
        InputActionDiagnostic? diagnostic = null) =>
        new(false, message, diagnostic);
}

public enum InputKey
{
    Escape,
    LeftAlt,
    V
}

public interface IInputController
{
    Task<ActionResult> ClickAsync(
        ClickTarget target,
        ActionPolicy policy,
        CancellationToken cancellationToken);

    Task<ActionResult> DragAsync(
        ClickTarget source,
        PixelPoint targetClientPoint,
        TimeSpan duration,
        ActionPolicy policy,
        CancellationToken cancellationToken);

    Task<ActionResult> PressKeyAsync(
        GameWindowInfo window,
        InputKey key,
        ActionPolicy policy,
        CancellationToken cancellationToken);

    Task<ActionResult> ClickWithModifierAsync(
        ClickTarget target,
        InputKey modifier,
        ActionPolicy policy,
        CancellationToken cancellationToken);
}
