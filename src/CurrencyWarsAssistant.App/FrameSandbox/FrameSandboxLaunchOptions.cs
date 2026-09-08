using System.IO;

namespace CurrencyWarsAssistant.App.FrameSandbox;

/// <summary>
/// --frame-sandbox 启动参数（与 --command-test 同族的测试台模式）：
///   --frame-sandbox &lt;脚本.json&gt;   必填；帧+期望操作集脚本
///   --frame-sandbox-out &lt;目录&gt;     可选；沙箱产物目录（默认
///     %LOCALAPPDATA%\CurrencyWarsSmartRaccoon\sandbox\run-时间戳，隔离于实局历史）。
/// 支持 "--frame-sandbox 路径" 与 "--frame-sandbox=路径" 两种写法。
/// </summary>
public sealed record FrameSandboxLaunchOptions(
    string ScriptPath,
    string? OutputDirectoryOverride)
{
    private const string ScriptSwitch = "--frame-sandbox";
    private const string OutputSwitch = "--frame-sandbox-out";

    public static FrameSandboxLaunchOptions? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? scriptPath = null;
        string? outputOverride = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, ScriptSwitch, StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith(ScriptSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                // P2-2（对抗审查）：开关出现但读不出路径必须显式报错——静默降级为
                // 正式模式（真实窗口/输入+抢占互斥量挡掉正式实例）比拒绝启动危险得多。
                if (!TryReadSwitchValue(args, ref index, argument, ScriptSwitch, out scriptPath) ||
                    string.IsNullOrWhiteSpace(scriptPath))
                {
                    throw new ArgumentException(
                        "--frame-sandbox 需要跟一个脚本路径：" +
                        "--frame-sandbox <脚本.json> 或 --frame-sandbox=<脚本.json>");
                }
            }
            else if (string.Equals(argument, OutputSwitch, StringComparison.OrdinalIgnoreCase) ||
                     argument.StartsWith(OutputSwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSwitchValue(args, ref index, argument, OutputSwitch, out outputOverride) ||
                    string.IsNullOrWhiteSpace(outputOverride))
                {
                    throw new ArgumentException(
                        "--frame-sandbox-out 需要跟一个目录路径");
                }
            }
        }

        return scriptPath is null
            ? null
            : new FrameSandboxLaunchOptions(scriptPath, outputOverride);
    }

    private static bool TryReadSwitchValue(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        string switchName,
        out string? value)
    {
        if (string.Equals(argument, switchName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 < args.Count)
            {
                value = args[index + 1];
                index++;
                return true;
            }

            value = null;
            return false;
        }

        value = argument[(switchName.Length + 1)..];
        return true;
    }

    public static string DefaultOutputDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.UserDataDirectoryName,
            "sandbox",
            $"run-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
}
