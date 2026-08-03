using System.IO;
using System.Text.Json;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// 从正在实时记录的节点数据（DetailedHistoryNodes）生成
/// completed-run 结构 JSON，供 gen_report.py 渲染实时对局报告。
/// 只有已打完的节点（FinalBattle 非空）会被包含；未打节点不显示。
/// </summary>
internal static class RealTimeReportBuilder
{
    /// <summary>
    /// 生成实时报告 JSON 文件，返回其目录（失败返回 null）。
    /// </summary>
    public static string? BuildReportDirectory(MainViewModel viewModel)
    {
        var entries = viewModel.RealtimeNodeEntries
            .Where(node => node.FinalBattle is not null)
            .ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        var runId = entries[0].RunId;
        string? environment = null;
        var affixes = new List<string>();
        var strategies = new List<string>();
        var enemies = new List<string>();
        var advisors = new List<string>();
        foreach (var entry in entries)
        {
            var snapshot = entry.LatestSnapshot;
            if (snapshot is null)
            {
                continue;
            }

            environment ??= Known(snapshot.InvestmentEnvironmentId);
            var state = entry.LatestPreparationState ?? entry.LatestState;
            if (state is not null)
            {
                Merge(affixes, KnownList(state.NegativeAffixIds));
            }
            Merge(strategies, KnownList(snapshot.InvestmentStrategyIds));
            Merge(enemies, KnownList(snapshot.EnemyIds));
            Merge(advisors, KnownList(snapshot.ExpertAdvisorIds));
        }

        var nodes = entries.Select(entry => new
        {
            NodeId = entry.NodeId,
            StartedAt = entry.UpdatedAt,
            EndedAt = entry.UpdatedAt,
            IsFinalized = true,
            IsComplete = entry.FinalBattle?.IsComplete ?? false,
            FinalPreparationSnapshot = entry.LatestSnapshot,
            FinalPreparationState = entry.LatestPreparationState,
            FinalBattle = entry.FinalBattle,
            PreparationAnalysisFile = entry.PreparationAnalysisFile,
            FinalBattleFile = entry.FinalBattleFile,
            AppliedEventIds = Array.Empty<string>(),
            Diagnostics = Array.Empty<string>(),
        }).ToArray();

        var cr = new
        {
            SchemaVersion = "1.0.0",
            ArchiveVersion = 1,
            RunId = runId,
            CompletedAt = DateTimeOffset.Now,
            IsFinal = false,
            CompletionPageId = (string?)null,
            CompletionNodeId = (string?)null,
            CompletionScreenshotFile = (string?)null,
            RatingText = (string?)null,
            IdentityEvidence = new
            {
                InvestmentEnvironmentId = environment,
                InvestmentStrategyIds = strategies,
                EnemyAffixIds = affixes,
                EnemyIds = enemies,
                ExpertAdvisorIds = advisors,
            },
            Nodes = nodes,
            SourceAnalysisFiles = Array.Empty<string>(),
            SourceRevision = "realtime",
            Uncertainty = Array.Empty<string>(),
            LastSnapshot = (object?)null,
            LastOperationalState = (object?)null,
        };

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "cwt-realtime", runId);
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(cr, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            File.WriteAllText(Path.Combine(directory, "completed-run.v1.json"), json);
            return directory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Known(CurrencyWarsAssistant.Advisor.Observation<string>? observation) =>
        observation?.Status == CurrencyWarsAssistant.Advisor.ObservationStatus.Known
            ? observation.Value
            : null;

    private static IReadOnlyList<string> KnownList(
        CurrencyWarsAssistant.Advisor.Observation<IReadOnlyList<string>>? observation) =>
        observation?.Status == CurrencyWarsAssistant.Advisor.ObservationStatus.Known
            ? observation.Value ?? []
            : [];

    private static void Merge(List<string> target, IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            if (!target.Contains(value))
            {
                target.Add(value);
            }
        }
    }
}
