using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// 从正在实时记录的节点数据（DetailedHistoryNodes）生成
/// completed-run 结构 JSON，供 gen_report.py 渲染实时对局报告。
/// 仅包含已经生成真实 FinalBattle 的封存节点。
/// </summary>
internal static class RealTimeReportBuilder
{
    /// <summary>
    /// 生成实时报告 JSON 文件，返回其目录（失败返回 null）。
    /// </summary>
    public static string? BuildReportDirectory(MainViewModel viewModel)
        => BuildReportDirectory(viewModel.RealtimeNodeEntries);

    internal static string? BuildReportDirectory(
        IReadOnlyList<HistoricalNodeDetailEntry> realtimeNodeEntries)
    {
        var reportEntries = SelectReportNodes(realtimeNodeEntries);
        if (reportEntries.Count == 0)
        {
            return null;
        }

        var entries = reportEntries.Select(item => item.Entry).ToList();
        var runId = entries[0].RunId;
        string? environment = null;
        var affixes = new List<string>();
        var strategies = new List<string>();
        var enemies = new List<string>();
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
        }

        var nodes = reportEntries.Select(item => new
        {
            NodeId = item.Entry.NodeId,
            StartedAt = item.Entry.UpdatedAt,
            EndedAt = item.Entry.UpdatedAt,
            IsComplete = item.Entry.FinalBattle?.IsComplete ?? false,
            FinalPreparationSnapshot = item.Entry.LatestSnapshot,
            FinalPreparationState = item.Entry.LatestPreparationState,
            FinalBattle = item.Entry.FinalBattle,
            PreparationAnalysisFile = item.Entry.PreparationAnalysisFile,
            FinalBattleFile = item.Entry.FinalBattleFile,
            AppliedEventIds = Array.Empty<string>(),
            Diagnostics = Array.Empty<string>(),
        }).ToArray();

        var cr = new
        {
            SchemaVersion = "1.0.0",
            ArchiveVersion = 1,
            RunId = runId,
            // 实时报告的内容签名必须只随节点数据变化。使用最新节点证据时间，
            // 避免每10秒轮询仅因当前时钟变化而重写/刷新同一份报告。
            CompletedAt = entries.Max(entry => entry.UpdatedAt),
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
                // 与 AdvisorJson 一致：Observation.Status 序列化为 "known"/"unknown"
                // 字符串（而非数字 0/1）——gen_report.py 的 known_value 按字符串
                // 判断，否则血量/金币/羁绊等所有 Observation 字段在实时报告中
                // 全部显示"未记录"（实测 0.2.833：只有纯数字的伤害能显示）。
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                },
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

    internal static IReadOnlyList<RealTimeReportNode> SelectReportNodes(
        IReadOnlyList<HistoricalNodeDetailEntry> entries) =>
        entries
            .Where(entry => entry.FinalBattle is not null)
            .Select(entry => new RealTimeReportNode(entry))
            .ToArray();

    internal sealed record RealTimeReportNode(
        HistoricalNodeDetailEntry Entry);

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
