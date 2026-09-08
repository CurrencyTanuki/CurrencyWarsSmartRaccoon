namespace CurrencyWarsAssistant.Automation;

/// <summary>
/// 帧沙箱里一次模拟输入的事实记录（RecordingInputController 产出）。
/// 坐标均为窗口客户区坐标（沙箱=1920×1080 标准系）：主点=点击目标包围盒中心
/// /拖拽源包围盒中心；拖拽另有目标点。
/// </summary>
public sealed record SandboxOperationRecord(
    long Sequence,
    DateTimeOffset At,
    string Kind,
    int? X,
    int? Y,
    int? ToX,
    int? ToY,
    string? Key,
    string? Modifier,
    string Message)
{
    public const string KindClick = "click";
    public const string KindDrag = "drag";
    public const string KindPressKey = "presskey";
    public const string KindClickWithModifier = "clickwithmodifier";

    /// <summary>InputKey 的规范小写名（脚本与记录共用，与导航配置键名同族）。</summary>
    public static string KeyName(InputKey key) => key switch
    {
        InputKey.Escape => "escape",
        InputKey.LeftAlt => "leftalt",
        InputKey.V => "v",
        InputKey.Enter => "enter",
        InputKey.F => "f",
        _ => key.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// 沙箱操作接收器：RecordingInputController 每次模拟输入后回调。
/// 帧沙箱裁判（App 层 FrameSandboxPlayer）实现它做脚本匹配/违规判定。
/// </summary>
public interface ISandboxOperationSink
{
    void Record(SandboxOperationRecord operation);
}
