using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class HistoricalPopulationHtmlTests
{
    [Fact]
    public async Task SharedWebViewReportRendersKnownPopulationFromPersistedState()
    {
        var (persistedJson, html) = await RenderReportAsync(population: 8);

        Assert.Contains("\"population\"", persistedJson, StringComparison.Ordinal);
        Assert.Contains("\"value\": 8", persistedJson, StringComparison.Ordinal);

        // Existing snapshot-backed fields protect the surrounding summary.
        Assert.Contains(
            "stat-label\">血量</span><span class=\"stat-value\">98</span>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "stat-label\">商店</span><span class=\"stat-value\">Lv7</span>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "stat-label\">人口</span><span class=\"stat-value\">8</span>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "stat-label\">行动</span><span class=\"stat-value\">1</span>",
            html,
            StringComparison.Ordinal);
        var theoryIndex = html.IndexOf(
            "stat-label\">阵容理论出伤极限</span><span class=\"stat-value\">4.5万</span>",
            StringComparison.Ordinal);
        var goldIndex = html.IndexOf(
            "stat-label\">金币</span>",
            StringComparison.Ordinal);
        Assert.True(theoryIndex >= 0 && theoryIndex < goldIndex);
        Assert.Contains(
            "chart-title\">阵容理论出伤极限</div>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "chart-title\">血量</div>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "stat-label\">结算</span>",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedWebViewReportKeepsNodeReadableWhenPopulationIsUnknown()
    {
        var (persistedJson, html) = await RenderReportAsync(population: null);

        Assert.Contains("\"population\"", persistedJson, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"unknown\"", persistedJson, StringComparison.Ordinal);
        Assert.Contains("class=\"node-id\">2-2</span>", html, StringComparison.Ordinal);
        Assert.Contains(
            "stat-label\">血量</span><span class=\"stat-value\">98</span>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "stat-label\">商店</span><span class=\"stat-value\">Lv7</span>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "stat-label\">人口</span><span class=\"stat-value\">未记录</span>",
            html,
            StringComparison.Ordinal);
    }

    private static async Task<(string PersistedJson, string Html)> RenderReportAsync(
        int? population)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"currencywars-population-report-{Guid.NewGuid():N}");
        var runId = "run-population-html-contract";
        var runDirectory = Path.Combine(tempRoot, runId);
        Directory.CreateDirectory(runDirectory);

        try
        {
            var observedAt = DateTimeOffset.Parse("2026-08-08T23:52:39+08:00");
            var state = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Preparation,
                PageId = "preparation_generic",
                NodeId = Observation<string>.Known("2-2", 0.99, observedAt: observedAt),
                Health = Observation<int>.Known(98, 0.99, observedAt: observedAt),
                StoreLevel = Observation<int>.Known(7, 0.99, observedAt: observedAt),
                Population = population is { } value
                    ? Observation<int>.Known(value, 0.99, observedAt: observedAt)
                    : Observation<int>.Unknown("population not visible")
            };
            var snapshot = new RunSnapshot
            {
                RunId = runId,
                AsOf = observedAt,
                Stage = Observation<string>.Known("2-2", 0.99, observedAt: observedAt),
                Health = state.Health,
                StoreLevel = state.StoreLevel
            };
            var battle = new FinalNodeBattleState(
                "2-2",
                [],
                12_345,
                RemainingActionValueState.Create(0, 1),
                observedAt,
                new EvidenceReference(
                    "fixture:historical-action",
                    "fixture:historical-action",
                    CapturedAt: observedAt,
                    Confidence: 0.99),
                GoldReward: 62,
                TheoreticalDamageLimit: 45_000);
            var archive = new CompletedRunRecord
            {
                RunId = runId,
                CompletedAt = observedAt,
                CompletionPageId = "challenge_success",
                CompletionNodeId = "2-2",
                LastSnapshot = snapshot,
                LastOperationalState = state,
                Nodes =
                [
                    new CompletedRunNodeRecord(
                        "2-2",
                        snapshot,
                        state,
                        battle,
                        "fixture:user_ref_000032",
                        null)
                ]
            };
            var archivePath = Path.Combine(runDirectory, "completed-run.v1.json");
            await File.WriteAllTextAsync(archivePath, AdvisorJson.Serialize(archive));

            var persistedJson = await File.ReadAllTextAsync(archivePath);

            var htmlPath = Path.Combine(tempRoot, "population-report.html");
            var renderedPath = await ReportHtmlRenderer.GenerateFromAsync(
                tempRoot,
                runId,
                htmlPath);
            Assert.Equal(htmlPath, renderedPath);
            var html = await File.ReadAllTextAsync(htmlPath);
            return (persistedJson, html);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
