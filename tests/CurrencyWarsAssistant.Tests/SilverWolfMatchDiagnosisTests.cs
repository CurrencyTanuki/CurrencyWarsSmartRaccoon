using System.Reflection;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 银狼 Front#0 匹配失败根因诊断（2026-08-07）：
/// 1) 银狼 3 个模板（default/4cost/5cost）的 Match 分数各是多少
/// 2) 银狼模板是否进入 Shortlist（前 32）——若没进，Match 分再高也没用
/// 3) 用像素分析确认实机槽位的背景色与哪个模板接近
/// </summary>
public sealed class SilverWolfMatchDiagnosisTests
{
    [Fact]
    public async Task DiagnoseSilverWolfTemplateMatch()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40", // 白厄
                    "currency_wars_character_56", // 飞霄
                    "currency_wars_character_72", // 阿格莱雅（实测与开拓者混淆）
                    "currency_wars_character_trailblazer", // 开拓者
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05", // 银狼LV.999（变费）
                ]),
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"),
            gameData,
            new WindowsOfflineOcr("en-US"));

        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:prep-3-7",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var front0 = state.Formation.Value?
            .FirstOrDefault(s => s.Zone == FormationZone.Front && s.SlotIndex == 0);
        Assert.NotNull(front0);
        Console.WriteLine(
            $"Front#0 char={front0.CharacterId} conf={front0.Confidence:F3} " +
            $"cand={string.Join(",", (front0.CandidateCharacterIds ?? []).Take(5))}");
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "silverwolf-diagnosis",
        AsOf = asOf
    };

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(GameDataCatalog gameData)
    {
        var directory = Path.Combine(RepositoryRoot, "data", "4.4", "character-card-templates");
        return gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                directory,
                $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(
                    character.Id,
                    character.Name,
                    file)))
            .ToList();
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
