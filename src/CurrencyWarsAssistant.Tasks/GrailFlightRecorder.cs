using System.IO;
using System.Text.Json;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 黑匣子飞行记录器（规格「指令通用规范」条款，handoff 六.2）：
/// 旁路记录每条指令的下发/结果/耗时到 logs/grail-flight-*.jsonl。
/// 纯旁路：写入失败静默吞掉（黑匣子绝不能反过来杀死指令通道），
/// 与事件日志（test-session-*.jsonl）分工——本记录器只管指令流，
/// 供实机验证以日志为准（「已修复=实机日志有执行痕迹」）。
/// </summary>
public sealed class GrailFlightRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly string? _filePath;

    /// <param name="directory">日志目录；缺省=%LOCALAPPDATA%\CurrencyWarsSmartRaccoon\logs（与事件日志同目录）。</param>
    public GrailFlightRecorder(string? directory = null)
    {
        try
        {
            var logDirectory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CurrencyWarsSmartRaccoon",
                "logs");
            Directory.CreateDirectory(logDirectory);
            _filePath = Path.Combine(
                logDirectory,
                $"grail-flight-{DateTimeOffset.Now:yyyyMMdd}.jsonl");
        }
        catch
        {
            // 目录不可用（LOCALAPPDATA 异常/权限等）：降级为禁用——黑匣子绝不反杀指令通道
            //（审查 P2：构造函数经字段初始化器运行，抛出会让整个测试窗口起不来）。
            _filePath = null;
        }
    }

    /// <summary>文件路径；禁用态返回 null。</summary>
    public string? FilePath => _filePath;

    /// <summary>记录一条指令终态（收到回执/异常/急停时各记一行；接收回执不记，避免双写）。
    /// durationMs 哨兵：-1=耗时未知（异常路径）。日期在构造时定格，跨午夜会话落同一文件
    /// （可接受：文件按窗口会话组织，非严格按自然日切分）。</summary>
    public void Record(string command, bool ok, string summary, double durationMilliseconds, string outcome = "terminal")
    {
        if (_filePath is null)
        {
            return; // 禁用态静默
        }

        try
        {
            var line = JsonSerializer.Serialize(
                new GrailFlightEntry(
                    DateTimeOffset.Now,
                    command,
                    ok,
                    summary,
                    Math.Round(durationMilliseconds, 0),
                    outcome),
                JsonOptions);
            lock (_gate)
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 黑匣子写失败不影响指令通道（磁盘满/占用/权限等）。
        }
    }

    private sealed record GrailFlightEntry(
        DateTimeOffset Timestamp,
        string Command,
        bool Ok,
        string Summary,
        double DurationMs,
        string Outcome);
}
