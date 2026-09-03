using CurrencyWarsAssistant.Vision;
using System;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 临时审计（2026-09-03 用户问询）：用生产加载器加载 data/4.4 图标模板，
/// 验证备战席特殊物品（三档武装箱+三档聘用书+冶金炉）模板的实际存活情况。
/// 审计完成后删除。
/// </summary>
public sealed class BenchSpecialItemTemplateAuditTests
{
    private static readonly string[] RequiredIds =
    [
        "currency_wars_equipment_156", // 特权武装箱
        "currency_wars_equipment_157", // 简易武装箱
        "currency_wars_equipment_158", // 进阶武装箱
        "special_item_020",            // 3费聘用书
        "special_item_021",            // 4费聘用书
        "special_item_022",            // 5费聘用书
        "special_item_010",            // 冶金炉
    ];

    private static string DataDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "data", "4.4");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir ?? string.Empty, "data", "4.4");
    }

    [Fact]
    public void BenchSpecialItemTemplates_Audit()
    {
        var templates = Phase2IconTemplateCatalog.Load(DataDirectory());
        var bench = templates
            .Where(item => (item.CandidateIds ?? [item.Id])
                .Any(id => RequiredIds.Contains(id, StringComparer.Ordinal)))
            .Select(item => (
                Id: item.Id,
                Candidates: string.Join("+", item.CandidateIds ?? [item.Id]),
                Category: item.Category,
                MinConfidence: item.MinimumConfidence,
                Mode: item.ComparisonMode.ToString()))
            .OrderBy(item => item.Id)
            .ToList();

        var report = "存活模板：\n" + string.Join(
            "\n",
            bench.Select(b => $"  id={b.Id} candidates=[{b.Candidates}] category={b.Category} min={b.MinConfidence} mode={b.Mode}"));

        // 每个必需 id 至少要有一个模板的 Id 或 CandidateIds 覆盖。
        var missing = RequiredIds
            .Where(id => !bench.Any(b =>
                b.Id == id ||
                (b.Candidates ?? "").Contains(id, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"缺失模板：{string.Join("、", missing)}\n{report}");
    }
}
