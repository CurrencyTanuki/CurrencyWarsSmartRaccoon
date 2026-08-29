using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 性能核实：对 RepeatedPreparation 同款帧(132307.png)跑非增量全量识别，
// bash 设置 CURRENCY_WARS_PHASE2_TIMING=1 后，state.Diagnostics 会含 perf: 分环节耗时。
public sealed class PerfTimingProbe
{
    [Fact]
    public async Task Run()
    {
        var root = @"D:\CWAFix-20260814";
        using var characterRecognizer = new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40", "currency_wars_character_56",
                    "currency_wars_character_72", "currency_wars_character_trailblazer",
                ]);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dataDirectory = System.IO.Path.Combine(root, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans", OfflineOcrRecognitionMode.Fast, maximumConcurrency: 4),
            gameData,
            new WindowsOfflineOcr("en-US", OfflineOcrRecognitionMode.Fast, maximumConcurrency: 4),
            enableRobustFallback: false);
        var frame = CaptureFrameLoader.LoadFile(
            System.IO.Path.Combine(root, "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "phase2-2026-07-28", "132307.png"));
        var snapshot = new RunSnapshot { RunId = "perf-probe", AsOf = frame.CapturedAt };
        await analyzer.AnalyzeAsync(frame, "preparation_1_1", "test:warm-up", snapshot, default);
        for (var i = 0; i < 2; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var st = await analyzer.AnalyzeAsync(frame, "preparation_1_1", $"test:perf:{i}", snapshot, default);
            sw.Stop();
            System.Console.WriteLine($"[probe] iter{i} total {sw.Elapsed.TotalMilliseconds:F1} ms");
            foreach (var d in st.Diagnostics ?? [])
                if (d != null && d.ToString() is string s && s.Contains("perf", System.StringComparison.OrdinalIgnoreCase))
                    System.Console.WriteLine("  DIAG: " + s);
        }
    }

    static IReadOnlyList<CharacterCardTemplateDefinition> LoadCharacterTemplates(GameDataCatalog gameData)
    {
        var directory = System.IO.Path.Combine(@"D:\CWAFix-20260814", "data", "4.4", "character-card-templates");
        return gameData.CurrencyWarsCharacters
            .SelectMany(character => System.IO.Directory.GetFiles(directory, $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(character.Id, character.Name, file)))
            .ToList();
    }
}
