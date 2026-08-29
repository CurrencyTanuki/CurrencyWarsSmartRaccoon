using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

public sealed class VariableEquipmentLayoutRealFrameTests
{
    private readonly ITestOutputHelper _output;

    public VariableEquipmentLayoutRealFrameTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void Prep16TwoEquipmentCandidateRegions_ContainTheTwoGroundTruthIcons()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_1_6_two_equipment_2559x1439.png"));
        var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
            Phase2RecognitionRegions.PreparationCharacterSlots1920[1],
            occupiedSlotCount: 2);
        using var recognizer = new OpenCvPhase2IconRecognizer();

        var results = recognizer.Recognize(
            frame,
            "advanced-equipment",
            regions,
            Phase2IconTemplateCatalog.Load(dataDirectory));
        foreach (var result in results)
        {
            _output.WriteLine(
                $"slot={result.SlotIndex} confidence={result.Confidence:F6} " +
                $"known={result.IsKnown} " +
                $"candidates=[{string.Join(',', result.CandidateTemplateIds ?? [])}]");
        }

        Assert.Multiple(
            () => Assert.Equal(2, results.Count),
            () => Assert.Contains(
                "currency_wars_equipment_064",
                results[0].CandidateTemplateIds ?? []),
            () => Assert.Contains(
                "currency_wars_equipment_066",
                results[1].CandidateTemplateIds ?? []),
            () => Assert.All(
                regions,
                region => Assert.False(
                    Phase2OperationalScreenshotAnalyzer
                        .DetectPrivilegeEquipmentOverlay(frame, region))));
    }

    [Fact]
    public async Task Prep16TwoEquipmentLayout_RecognizesExactlyTwoCenteredItems()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        using var horizontalSpecialUnitRecognizer =
            new OpenCvHorizontalSpecialUnitRecognizer(Path.Combine(
                dataDirectory,
                "character-card-templates",
                "special_unit_peipei__horizontal-face.png"));
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer"
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05"
                ]),
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"),
            gameData,
            new WindowsOfflineOcr("en-US"),
            horizontalSpecialUnitRecognizer:
                horizontalSpecialUnitRecognizer);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_1_6_two_equipment_2559x1439.png"));

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:prep16-two-equipment",
            new RunSnapshot
            {
                RunId = "prep16-two-equipment",
                AsOf = frame.CapturedAt
            },
            CancellationToken.None);

        var yaoGuang = Assert.Single((state.Formation.Value ?? []).Where(item =>
            item.Zone == FormationZone.Front &&
            item.SlotIndex == 1 &&
            item.CharacterId == "currency_wars_character_10"));
        foreach (var slot in yaoGuang.FinalEquipmentSlots)
        {
            _output.WriteLine(
                $"slot={slot.SlotIndex} occupancy={slot.Occupancy} " +
                $"id={slot.EquipmentId ?? "-"} confidence={slot.Confidence:F6} " +
                $"candidates=[{string.Join(',', slot.CandidateEquipmentIds)}]");
        }

        Assert.Multiple(
            () => Assert.Equal(
                [
                    "currency_wars_equipment_064",
                    "currency_wars_equipment_066"
                ],
                yaoGuang.EquipmentIds),
            () => Assert.Equal(3, yaoGuang.FinalEquipmentSlots.Count),
            () => Assert.Equal(
                EquipmentSlotOccupancy.Equipped,
                yaoGuang.FinalEquipmentSlots[0].Occupancy),
            () => Assert.Equal(
                "currency_wars_equipment_064",
                yaoGuang.FinalEquipmentSlots[0].EquipmentId),
            () => Assert.Equal(
                EquipmentSlotOccupancy.Equipped,
                yaoGuang.FinalEquipmentSlots[1].Occupancy),
            () => Assert.Equal(
                "currency_wars_equipment_066",
                yaoGuang.FinalEquipmentSlots[1].EquipmentId),
            () => Assert.Equal(
                EquipmentSlotOccupancy.Empty,
                yaoGuang.FinalEquipmentSlots[2].Occupancy),
            () => Assert.Null(yaoGuang.FinalEquipmentSlots[2].EquipmentId),
            () => Assert.Empty(
                yaoGuang.FinalEquipmentSlots[2].CandidateEquipmentIds),
            () => Assert.DoesNotContain(
                state.PendingIcons,
                item => item.Category == PendingIconCategory.AdvancedEquipment &&
                        item.RecognizedFields?.GetValueOrDefault(
                            "ownerCharacterId") ==
                        "currency_wars_character_10" &&
                        item.RecognizedFields?.GetValueOrDefault("zone") ==
                        FormationZone.Front.ToString()));
    }

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(GameDataCatalog gameData)
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "data",
            "4.4",
            "character-card-templates");
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                directory,
                $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(
                    character.Id,
                    character.Name,
                    file)))
            .ToList();
        var peipeiFile = Path.Combine(
            directory,
            "special_unit_peipei__default.png");
        if (File.Exists(peipeiFile))
        {
            templates.Add(new CharacterCardTemplateDefinition(
                "special_unit_peipei",
                "佩佩",
                peipeiFile,
                CharacterCardTemplateKind.SpecialOccupied));
        }

        return templates;
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));
}
