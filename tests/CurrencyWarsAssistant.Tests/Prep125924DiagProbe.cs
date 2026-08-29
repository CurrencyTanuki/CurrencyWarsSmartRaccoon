using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 诊断：加载 125924.png，打印 Formation 每个角色的装备槽。
public sealed class Prep125924DiagProbe
{
    [Fact]
    public async Task Run()
    {
        var root = @"D:\CWAFix-20260814";
        using var characterRecognizer = new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds: ["currency_wars_character_40", "currency_wars_character_56", "currency_wars_character_72", "currency_wars_character_trailblazer"]);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dd = System.IO.Path.Combine(root, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dd);
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dd),
            new WindowsOfflineOcr("zh-Hans", OfflineOcrRecognitionMode.Fast, maximumConcurrency: 4),
            gameData,
            new WindowsOfflineOcr("en-US", OfflineOcrRecognitionMode.Fast, maximumConcurrency: 4),
            enableRobustFallback: false);
        var frame = CaptureFrameLoader.LoadFile(System.IO.Path.Combine(root, "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "phase2-2026-07-28", "132307.png"));
        var st = await analyzer.AnalyzeAsync(frame, "preparation_1_1", "fixture:132307", new RunSnapshot { RunId = "diag132307", AsOf = frame.CapturedAt }, default);
        foreach (var f in st.Formation.Value ?? [])
        {
            foreach (var s in f.FinalEquipmentSlots ?? [])
            {
                System.Console.WriteLine($"[DIAG] zone={f.Zone} slot={f.SlotIndex} char={f.CharacterId} equipIdx={s.SlotIndex} occ={s.Occupancy} id={s.EquipmentId} cand=[{string.Join('|', s.CandidateEquipmentIds ?? [])}] conf={s.Confidence:F3}");
            }
        }
    }

    static IReadOnlyList<CharacterCardTemplateDefinition> LoadCharacterTemplates(GameDataCatalog g)
    {
        var d = System.IO.Path.Combine(@"D:\CWAFix-20260814", "data", "4.4", "character-card-templates");
        return g.CurrencyWarsCharacters.SelectMany(c => System.IO.Directory.GetFiles(d, $"{c.Id}__*.png").Select(f => new CharacterCardTemplateDefinition(c.Id, c.Name, f))).ToList();
    }
}
