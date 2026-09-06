namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 指令分发器：按指令段纯路由（I=1xx→识别层、A=2xx→操作层、M=3xx→宏层）。
/// 无重试（重试下沉在各适配器与既有组件内部）、无业务分支、无日志（黑匣子后补）。
/// </summary>
public sealed class GrailCommandDispatcher(
    IGrailCommandHandler recognition,
    IGrailCommandHandler operation,
    IGrailCommandHandler macro)
{
    public async Task<GrailCommandResult> DispatchAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        return command.Kind switch
        {
            >= GrailCommandKind.I1 and <= GrailCommandKind.I10 =>
                await recognition.HandleAsync(command, context, cancellationToken),
            // A16（1.2.117 热修：路由段漏含 A16，指令通道上从未到达操作层——单测直调操作层没覆盖到分发器，通宵首验实锤）。
            >= GrailCommandKind.A1 and <= GrailCommandKind.A16 =>
                await operation.HandleAsync(command, context, cancellationToken),
            >= GrailCommandKind.M1 and <= GrailCommandKind.M8 =>
                await DispatchMacroAsync(command, context, cancellationToken),
            _ => GrailCommandResult.Fail(command.Kind, $"未知指令类别 {command.Kind}。"),
        };
    }

    private async Task<GrailCommandResult> DispatchMacroAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // M8=新对局边界：A1 的上场槽账与执行器账一样跨局作废
        //（2026-09-02 跨局污染复盘）。宏层自身够不到操作层账本，分发器是唯一
        // 同时持有两者且所有指令流量必经的汇集点。
        if (command.Kind == GrailCommandKind.M8 &&
            operation is GrailOperationCommands concreteOperation)
        {
            concreteOperation.ResetDeploymentCounters();
        }

        return await macro.HandleAsync(command, context, cancellationToken);
    }
}
