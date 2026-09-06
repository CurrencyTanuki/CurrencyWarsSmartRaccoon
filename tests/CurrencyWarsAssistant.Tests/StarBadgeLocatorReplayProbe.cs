using System.Text;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 星徽定位离线回放探针（1.2.98 星徽链修复的取证/验证工具，非守卫测试）：
/// 对今日 019 局的实拍关键帧跑 StarBadgeLocator，回答"物品栏有星徽但 I7 读 0"场景下
/// Locator 能否命中。默认 Skip（依赖本机取证数据）；需要时去掉 Skip 跑。
/// </summary>
public sealed class StarBadgeLocatorReplayProbe
{
    [Fact(Skip = "取证探针：已跑出 2026-09-06 报告（344 帧 9 HIT，artifacts/star_badge_replay_report.txt）；依赖本机 runs 截图，复验时手动启用")]
    public void ReplayToday019Runs()
    {
        var root = @"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\runs";
        var sessions = Directory.GetDirectories(root, "cmdtest-20260906-*")
            .OrderBy(d => d)
            .ToList();
        var sb = new StringBuilder();
        var hit = 0;
        var total = 0;
        foreach (var session in sessions)
        {
            var shotDir = Path.Combine(session, "screenshots");
            if (!Directory.Exists(shotDir))
            {
                continue;
            }

            var shots = Directory.GetFiles(shotDir, "*.png");
            foreach (var shot in shots)
            {
                var frame = CaptureFrameLoader.LoadFile(shot);
                total++;
                var located = StarBadgeLocator.TryLocate(frame, out var center, out var score);
                if (located)
                {
                    hit++;
                }

                sb.AppendLine($"{Path.GetFileName(session)}/{Path.GetFileName(shot)} {frame.Width}x{frame.Height} => {(located ? $"HIT ({center.X},{center.Y}) score={score:F3}" : $"miss score={score:F3}")}");
            }
        }

        var report = sb.ToString();
        File.WriteAllText(
            @"C:\Users\zzz81\Desktop\货币战争开发包_给另一个AI_20260826\artifacts\star_badge_replay_report.txt",
            report,
            new UTF8Encoding(false));
        Assert.True(total > 0, "未找到截图");
    }
}
