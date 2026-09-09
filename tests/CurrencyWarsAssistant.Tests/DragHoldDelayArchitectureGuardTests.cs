using System.Text.RegularExpressions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 坑44 静态守卫（09-10 夜审 P1-B 沉淀）：拖拽类输入必须带鼠标按压时长。
/// 09-09 夜班实证 137 次部署拖拽 holdMs=0（1.2.62 修复只接了三条快速路径，
/// 主部署/出售首试/N12/场上出售四处漏接）——此类"调用点漏接"最适合源码扫描钉死：
/// Tasks 内任何 DragAsync 调用点的 ActionPolicy 必须显式含 MouseButtonHoldDelay。
/// 免责清单：CurrencyWarsNavigation.cs 的配置驱动 DragAsync（config/navigation-flow.json
/// 当前 0 条 drag 动作=休眠路径，配置动作由 ExecuteActionAsync 处理不经本守卫范围）。
/// </summary>
public class DragHoldDelayArchitectureGuardTests
{
    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string TasksSourceDirectory =>
        Path.Combine(RepositoryRoot, "src", "CurrencyWarsAssistant.Tasks");

    /// <summary>按文件名豁免的调用点（有独立口径或休眠路径，须在此注明理由）。</summary>
    private static readonly string[] ExemptFiles =
    {
        // 导航拖拽走配置动作（navigation-flow.json 当前无 drag 动作=休眠），
        // 且导航拖拽是"界面按钮流"非"卡牌/星徽拖拽"，无坑44 吸附问题。
        "CurrencyWarsNavigation.cs",
    };

    [Fact]
    public void EveryDragAsyncInTasksCarriesMouseButtonHoldDelay()
    {
        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(TasksSourceDirectory, "*.cs"))
        {
            var fileName = Path.GetFileName(file);
            if (ExemptFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("input.DragAsync(", StringComparison.Ordinal))
                {
                    continue;
                }

                // 拖拽调用块通常 ≤ 20 行（ClickTarget+时长+ActionPolicy+cancellationToken）。
                var block = string.Join(
                    Environment.NewLine,
                    lines.Skip(i).Take(24));
                var hasHold = Regex.IsMatch(
                    block,
                    @"MouseButtonHoldDelay\s*=\s*TimeSpan\.FromMilliseconds\((\d{3,})\)");
                if (!hasHold)
                {
                    violations.Add($"{fileName}:{i + 1} DragAsync 未显式设置 MouseButtonHoldDelay（坑44：瞬时拖拽一成一败）");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "发现无按压时长的拖拽调用点（坑44 静态守卫）：\n" + string.Join("\n", violations));
    }
}
