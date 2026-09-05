using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>和平手枪042 + 后台 关键帧回放验证（用户指定判断是否修好）。</summary>
public sealed class KeyframeReplayVerifyTests
{
    // 2026-09-05：取证帧源已随磁盘卫生清理（runs/run-20260818-163242），回放验证无数据必挂；
    // 恢复数据或改指新取证目录时移除本 Skip。
    [Fact(Skip = "2026-09-05 取证帧已清理（run-20260818-163242）：关键帧回放验证数据缺席时跳过")]
    public async Task ReplayPeaceGunAndBack()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        using var ppOcr = new PpOcrOfflineOcr(Path.Combine(
            dataDirectory, "..", "ocr", "rapidocr", "PP-OCRv6_rec_small.onnx"), maximumConcurrency: 1);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                candidateLimit: 32,
                lenientLeadOverCharacterIds: ["currency_wars_character_40", "currency_wars_character_56", "currency_wars_character_72", "currency_wars_character_trailblazer"],
                lenientConfidenceCharacterIds: ["currency_wars_character_05", "currency_wars_character_23"]),
            LoadTemplates(gameData), iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"), gameData,
            new WindowsOfflineOcr("en-US"), storeLevelOcr: ppOcr);
        var screens = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CurrencyWarsSmartRaccoon", "runs", "run-20260818-163242", "screenshots");
        var outLines = new List<string>();
        foreach (var shot in new[] { "20260818-084639619.png", "20260818-084211147.png" })
        {
            var frame = CaptureFrameLoader.LoadFile(Path.Combine(screens, shot));
            var state = await analyzer.AnalyzeAsync(frame, "preparation_generic",
                "kf:" + shot, EmptySnapshot(frame.CapturedAt), CancellationToken.None);
            outLines.Add($"==== {shot} ====");
            outLines.Add($"  population={state.Population?.Status}:{state.Population?.Value} storeLevel={state.StoreLevel?.Status}:{state.StoreLevel?.Value}");
            var simple = state.SimpleEquipmentIds?.Value ?? [];
            outLines.Add($"  SimpleEquipment={string.Join(",", simple)}  gun042 count={simple.Count(x => x == "currency_wars_equipment_042")}");
            var formation = state.Formation.Value ?? [];
            foreach (var zone in new[] { FormationZone.Front, FormationZone.Back, FormationZone.Bench })
            {
                var inZone = formation
                    .Where(x => x.Zone == zone)
                    .OrderBy(x => x.SlotIndex)
                    .ToList();
                outLines.Add($"  {zone}({inZone.Count}): " + string.Join(",", inZone.Select(x => $"{x.SlotIndex}={x.CharacterId}:{x.Confidence:F2}")));
            }
            var hasTrailblazer = formation.Any(x =>
                x.CharacterId is not null &&
                x.CharacterId.Contains("trailblazer", StringComparison.OrdinalIgnoreCase));
            outLines.Add($"  hasTrailblazer={hasTrailblazer}");
        }
        System.IO.File.WriteAllLines(
            Path.Combine(repositoryRoot, "tests", "CurrencyWarsAssistant.Tests", "keyframe_replay.txt"), outLines);
    }
    static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new() { RunId = "kf", AsOf = asOf };
    static IReadOnlyList<CharacterCardTemplateDefinition> LoadTemplates(GameDataCatalog g)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dir = Path.Combine(repo, "data", "4.4", "character-card-templates");
        return g.CurrencyWarsCharacters.SelectMany(ch =>
            Directory.GetFiles(dir, $"{ch.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(ch.Id, ch.Name, f))).ToList();
    }
}
