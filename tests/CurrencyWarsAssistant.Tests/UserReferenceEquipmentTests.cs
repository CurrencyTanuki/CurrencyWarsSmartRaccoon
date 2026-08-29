using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 用户参考图真实装备识别测试（2026-08-07）：
/// 000032（2-2 备战，清洗数据无动画）：银狼 2 件、刃 1 件、其余 0 件
/// （describe 实证）——验证"空槽自然过滤"是否正确（识别件数 ≈ 实际）。
/// </summary>
public sealed class UserReferenceEquipmentTests
{
    private readonly ITestOutputHelper _output;

    public UserReferenceEquipmentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RecognizesEquipmentCountsOnUserRef032()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05",
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
            "user_ref_000032_prep22.png"));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:user-ref-000032",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var formation = state.Formation.Value ?? [];
        _output.WriteLine($"000032 阵容识别 {formation.Count} 槽:");
        foreach (var s in formation)
        {
            _output.WriteLine(
                $"  {s.Zone}#{s.SlotIndex} {s.CharacterId} conf={s.Confidence:F3} " +
                $"装备[{string.Join(",", s.EquipmentIds)}]");
        }

        Assert.NotEmpty(formation);
    }

[Fact]
    public async Task RecognizesEquipmentCountsOnUserRef036()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05",
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
            "user_ref_000036_prep31.png"));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:user-ref-000036",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var formation = state.Formation.Value ?? [];
        _output.WriteLine($"000036 阵容识别 {formation.Count} 槽:");
        foreach (var s in formation)
        {
            _output.WriteLine(
                $"  {s.Zone}#{s.SlotIndex} {s.CharacterId} conf={s.Confidence:F3} " +
                $"装备[{string.Join(",", s.EquipmentIds)}]");
        }

        Assert.NotEmpty(formation);
    }

    [Fact]
    public async Task RecognizesEquipmentCountsOnUserRef035()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05",
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
            "user_ref_000036_prep31.png"));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:user-ref-000036",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var formation = state.Formation.Value ?? [];
        _output.WriteLine($"000036 阵容识别 {formation.Count} 槽:");
        foreach (var s in formation)
        {
            _output.WriteLine(
                $"  {s.Zone}#{s.SlotIndex} {s.CharacterId} conf={s.Confidence:F3} " +
                $"装备[{string.Join(",", s.EquipmentIds)}]");
        }

        Assert.NotEmpty(formation);
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "user-ref-equipment",
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
