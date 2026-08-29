using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 真实帧回放测试（2026-08-06 会话 X）：
/// 用 run-20260806-125919（用户 0.2.833 实机）的备战分析帧喂给
/// HistoricalDashboardProjection，验证实时 DetailNodes 的 LatestSnapshot
/// 是否携带金币/血量等备战数据——根因排查"历史节点详情全未记录"。
/// </summary>
public sealed class Phase2RealtimeDetailProjectionTests
{
    [Fact]
    public void LivePreparationFramesCarryEconomyAndHealthIntoDetailNodes()
    {
        var projection = new HistoricalDashboardProjection();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(
                         RepositoryRoot,
                         "tests",
                         "CurrencyWarsAssistant.Tests",
                         "Fixtures",
                         "PageReplay"),
                     "realtime-live-*.json")
                 .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            var json = File.ReadAllText(file);
            var analysis = AdvisorJson.Deserialize<ScreenshotAnalysisResult>(json);
            Assert.NotNull(analysis);
            // 历史记录器的低置信数值必须由连续帧确认；真实采集会持续观察
            // 同一备战画面。重复回放同一证据，锁定“单帧不猜、两帧确认”。
            projection.Observe("run-realtime-test", analysis);
            projection.Observe("run-realtime-test", analysis);
        }

        var details = projection.Current.DetailNodes;
        Assert.NotEmpty(details);

        foreach (var nodeId in new[] { "1-2", "1-3", "1-4", "1-8" })
        {
            var detail = details.FirstOrDefault(item =>
                string.Equals(item.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(detail);
            Assert.NotNull(detail.LatestSnapshot);
            // 血量：各备战帧 health 全部 known，合并后应 Known
            Assert.Equal(
                ObservationStatus.Known,
                detail.LatestSnapshot!.Health.Status);
            // 金币：该节点只要有任一帧 economy known 就应 Known
            Assert.Equal(
                ObservationStatus.Known,
                detail.LatestSnapshot.Economy.Status);
            // 阵容：备战帧识别到的槽位应保留在 LatestPreparationState.Formation
            var formation = detail.LatestPreparationState?.Formation;
            Assert.NotNull(formation);
            Assert.NotNull(formation!.Value);
            Assert.NotEmpty(formation.Value);
        }
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
