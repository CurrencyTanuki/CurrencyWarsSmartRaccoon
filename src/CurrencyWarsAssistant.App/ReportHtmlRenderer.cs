using System.Diagnostics;
using System.IO;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// 生成对局报告 HTML（调用 gen_report.py——与 HTML 报告原型同一套逻辑）。
/// 供"历史对局"与"对局历史详细信息"窗口共用。
/// </summary>
internal static class ReportHtmlRenderer
{
    /// <summary>
    /// 为指定存档生成报告 HTML，返回 HTML 文件路径（失败返回 null）。
    /// </summary>
    public static async Task<string?> GenerateAsync(string runId)
    {
        var runsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CurrencyWarsSmartRaccoon",
            "runs");
        // 输出路径带存档 ID：不同存档不同 HTML 文件，避免 WebView2
        // 对同一 URL 的缓存导致切换存档后页面不刷新。
        var safeRunId = string.Concat(runId.Where(char.IsLetterOrDigit).Take(32));
        var outHtml = Path.Combine(Path.GetTempPath(), $"currencywars-report-{safeRunId}.html");
        return await GenerateFromAsync(runsDir, runId, outHtml);
    }

    /// <summary>
    /// 指定存档目录/存档 ID/输出路径生成报告 HTML（实时报告也走此路径）。
    /// </summary>
    public static async Task<string?> GenerateFromAsync(
        string runsDir,
        string runId,
        string outHtml)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "gen_report.py");
        var python = Path.Combine(
            AppContext.BaseDirectory,
            "runtime",
            "python",
            "python.exe");
        if (!File.Exists(script) || !File.Exists(python))
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(runsDir);
        psi.ArgumentList.Add(runId);
        psi.ArgumentList.Add(outHtml);
        psi.Environment["PYTHONNOUSERSITE"] = "1";
        psi.Environment["PYTHONUTF8"] = "1";

        try
        {
            if (File.Exists(outHtml))
            {
                File.Delete(outHtml);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0 && File.Exists(outHtml)
                ? outHtml
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
