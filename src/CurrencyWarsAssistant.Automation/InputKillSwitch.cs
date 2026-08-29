namespace CurrencyWarsAssistant.Automation;

/// <summary>
/// 输入急停闸：置位后 <see cref="Win32InputController"/> 的所有模拟输入立即失败。
/// 「停止」按钮与 Ctrl+Shift+F12 紧急停止置位；重新启动任务时解除。
/// 这是停止语义的最后防线——无论哪个循环忽略取消令牌，都不允许再向游戏发送输入。
/// </summary>
public static class InputKillSwitch
{
    /// <summary>true = 拒绝一切模拟输入。</summary>
    public static volatile bool Armed;
}
