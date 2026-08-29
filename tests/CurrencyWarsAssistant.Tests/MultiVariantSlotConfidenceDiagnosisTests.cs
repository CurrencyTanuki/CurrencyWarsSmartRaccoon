using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 多变体模板后真实帧槽位置信度诊断（2026-08-07）：
/// 输出 preparation_white_failure / feixiao_baie_confusion / nine_cards_low_confidence
/// 三帧的槽位 conf/runner-up，用于更新过时断言（新模板集引入新 runner-up）。
/// </summary>
public sealed class MultiVariantSlotConfidenceDiagnosisTests
{
    [Fact]
    public void DumpAffectedFrameSlotScores()
    {
        var templates = LoadTemplates();
        using var recognizer = new OpenCvCharacterCardRecognizer(
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
                ]);
        var cases = new[]
        {
            "preparation_white_failure_2048x1152.png",
            "preparation_feixiao_baie_confusion_2559x1439.png",
            "preparation_nine_cards_low_confidence_2048x1152.png",
        };
        foreach (var file in cases)
        {
            Console.WriteLine($"=== {file} ===");
            var frame = LoadFrame(Path.Combine(FixtureDirectory, file));
            var results = recognizer.Recognize(frame, templates, BenchSlots);
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                Console.WriteLine(
                    $"  [{i}] state={r.State} char={r.DisplayName} " +
                    $"conf={r.Confidence:F3} runner={r.RunnerUpDisplayName} " +
                    $"runnerConf={r.RunnerUpConfidence:F3}");
            }
        }
    }

    private static readonly IReadOnlyList<PixelRect> BenchSlots =
        [
            new(383, 844, 114, 137),
            new(506, 844, 119, 137),
            new(633, 844, 117, 137),
            new(759, 844, 114, 137),
            new(883, 844, 116, 137),
            new(1005, 844, 116, 137),
            new(1128, 844, 116, 137),
            new(1250, 844, 116, 137),
            new(1374, 844, 116, 137),
        ];

    [Fact]
    public void DumpShop14Formation()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = LoadTemplates();
        using var recognizer = new OpenCvCharacterCardRecognizer(
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
                ]);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "preparation-shop-1-4.png"));
        // board 槽位（PreparationCharacterSlots1920 全 10 槽）
        var allSlots = new PixelRect[]
        {
            new(681, 329, 128, 140), new(827, 329, 122, 140),
            new(972, 329, 120, 140), new(1114, 329, 120, 140),
            new(535, 600, 140, 145), new(687, 600, 130, 145),
            new(829, 600, 130, 145), new(966, 600, 130, 145),
            new(1108, 600, 130, 145), new(1258, 600, 130, 145),
        };
        var results = recognizer.Recognize(frame, templates, allSlots);
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.State != CharacterCardSlotState.Empty)
            {
                Console.WriteLine(
                    $"  [{i}] state={r.State} char={r.DisplayName} " +
                    $"conf={r.Confidence:F3} runner={r.RunnerUpDisplayName}");
            }
        }
    }

    private static IReadOnlyList<CharacterCardTemplateDefinition> LoadTemplates()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templateDirectory = Path.Combine(
            dataDirectory,
            "character-card-templates");
        return gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                templateDirectory,
                $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(
                    character.Id,
                    character.Name,
                    file)))
            .ToList();
    }

    private static CaptureFrame LoadFrame(string path) =>
        CaptureFrameLoader.LoadFile(path);

    private static string FixtureDirectory =>
        Path.Combine(RepositoryRoot, "tests", "CurrencyWarsAssistant.Tests",
            "Fixtures", "PageReplay");

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
