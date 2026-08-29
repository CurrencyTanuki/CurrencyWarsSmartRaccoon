namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 调试复盘证据收集器（设置页开关，默认关闭）。
/// 开启后保存完善的复盘证据到
/// %LocalAppData%/CurrencyWarsSmartRaccoon/debug-evidence/&lt;会话时间戳&gt;/：
///   - frames/：全量帧 + 增量帧截图（PNG，含页面分类/识别上下文）
///   - analyses/：每次识别结果 JSON（页面分类诊断 classifier-miss、字段状态、
///     识别耗时 perf）
/// 刷开局动作/输入事件由软件运行日志（logs/test-session-*.jsonl）独立保存，
/// 本收集器只补帧与识别结果，避免证据不足无法判断 bug 根因。
/// </summary>
public static class DebugEvidenceCollector
{
    private static readonly object Sync = new();
    private static string? _archiveDirectory;
    private static int _frameSequence;

    /// <summary>调试证据保存开关（MainViewModel 从设置页驱动，默认 false）。</summary>
    public static bool Enabled { get; set; }

    /// <summary>确保归档目录存在（首次启用时创建会话子目录）。</summary>
    public static string? EnsureArchive(out string? error)
    {
        error = null;
        if (!Enabled)
        {
            return null;
        }

        lock (Sync)
        {
            if (_archiveDirectory is null)
            {
                try
                {
                    var root = Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "CurrencyWarsSmartRaccoon",
                        "debug-evidence",
                        DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.CreateDirectory(Path.Combine(root, "frames"));
                    Directory.CreateDirectory(Path.Combine(root, "analyses"));
                    _archiveDirectory = root;
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    return null;
                }
            }

            return _archiveDirectory;
        }
    }

    /// <summary>保存一帧截图（全量帧/增量帧）。</summary>
    public static void SaveFrame(
        CurrencyWarsAssistant.Vision.CaptureFrame frame,
        string category,
        string? context = null)
    {
        if (!Enabled || frame is null)
        {
            return;
        }

        var directory = EnsureArchive(out _);
        if (directory is null)
        {
            return;
        }

        try
        {
            var sequence = Interlocked.Increment(ref _frameSequence);
            var name =
                $"{sequence:D5}-{category}-{frame.CapturedAt:HHmmssfff}" +
                (string.IsNullOrWhiteSpace(context) ? string.Empty : $"-{Sanitize(context)}") +
                ".png";
            frame.SavePng(Path.Combine(directory, "frames", name));
        }
        catch
        {
            // 证据保存失败不影响识别主流程。
        }
    }

    /// <summary>保存一次识别结果 JSON（含页面/字段/诊断/耗时）。</summary>
    public static void SaveAnalysis(
        CurrencyWarsAssistant.Advisor.ScreenshotAnalysisResult analysis,
        string category,
        TimeSpan elapsed,
        string? context = null)
    {
        if (!Enabled || analysis is null)
        {
            return;
        }

        var directory = EnsureArchive(out _);
        if (directory is null)
        {
            return;
        }

        try
        {
            var sequence = Interlocked.Increment(ref _frameSequence);
            var name =
                $"{sequence:D5}-{category}-{DateTime.Now:HHmmssfff}" +
                (string.IsNullOrWhiteSpace(context) ? string.Empty : $"-{Sanitize(context)}") +
                ".json";
            var snapshot = new
            {
                analysis.Snapshot,
                analysis.OperationalState,
                analysis.Warnings,
                analysis.UnknownFields,
                ElapsedMilliseconds = elapsed.TotalMilliseconds,
                CapturedAt = DateTimeOffset.Now
            };
            File.WriteAllText(
                Path.Combine(directory, "analyses", name),
                System.Text.Json.JsonSerializer.Serialize(
                    snapshot,
                    CurrencyWarsAssistant.Advisor.AdvisorJson.Options));
        }
        catch
        {
            // 证据保存失败不影响识别主流程。
        }
    }

    /// <summary>保存一条额外备注（如刷开局动作、识别失败说明）。</summary>
    public static void SaveNote(string category, string message)
    {
        if (!Enabled)
        {
            return;
        }

        var directory = EnsureArchive(out _);
        if (directory is null)
        {
            return;
        }

        try
        {
            var notesPath = Path.Combine(directory, "notes.jsonl");
            File.AppendAllText(
                notesPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        Time = DateTimeOffset.Now,
                        Category = category,
                        Message = message
                    },
                    CurrencyWarsAssistant.Advisor.AdvisorJson.Options) +
                Environment.NewLine);
        }
        catch
        {
            // 证据保存失败不影响识别主流程。
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_'));
}
