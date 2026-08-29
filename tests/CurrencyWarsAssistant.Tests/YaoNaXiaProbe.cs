using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 01433 那可夏（第 1 格）识别诊断：warp 帧 vs 原帧，输出匹配状态/
/// 置信度/失败原因/候选。定位 conf 0.591 与正式路径 unknown 的差异。
/// </summary>
public sealed class YaoNaXiaProbe(ITestOutputHelper output)
{
    [Fact]
    public void DiagnoseYaoNaXiaSlot()
    {
        var path = @"C:\Users\zzz81\AppData\Local\CurrencyWarsSmartRaccoon\debug-evidence\20260812-084051\frames\01433-full-011041300-20260812-011041300_png.png";
        if (!File.Exists(path))
        {
            output.WriteLine("missing");
            return;
        }

        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repoRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templateDirectory = Path.Combine(dataDirectory, "character-card-templates");
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                    templateDirectory, $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, file,
                    CharacterCardTemplateKind.Character)))
            .ToList();
        foreach (var file in Directory.GetFiles(templateDirectory, "special_unit_*__default.png"))
        {
            var stem = Path.GetFileName(file);
            var id = stem[..stem.IndexOf("__", StringComparison.Ordinal)];
            templates.Add(new CharacterCardTemplateDefinition(
                id, id, file, CharacterCardTemplateKind.SpecialOccupied));
        }

        using var recognizer = new OpenCvCharacterCardRecognizer(
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
                "currency_wars_character_23",
            ]);

        var frame = CaptureFrameLoader.LoadFile(path);

        // warp 路径
        var (warped, warpSlots) = BackRowPerspectiveGeometry.WarpBackRow(frame, 7);
        var warpedResults = recognizer.Recognize(
            warped, templates, [warpSlots[0]], CharacterCardRecognitionOptions.Standard);
        output.WriteLine("=== warp 帧那可夏槽位 ===");
        foreach (var r in warpedResults)
        {
            output.WriteLine(
                $"  state={r.State} char={r.CharacterId ?? "?"} conf={r.Confidence:F4} " +
                $"star={r.StarLevel});" + "");
        }

        // 原帧路径（实验一致）
        var origSlots = Phase2RecognitionRegions.BackCharacterSlots1920(7);
        var origResults = recognizer.Recognize(
            frame, templates, [origSlots[0]], CharacterCardRecognitionOptions.Standard);
        output.WriteLine("=== 原帧那可夏槽位 ===");
        foreach (var r in origResults)
        {
            output.WriteLine(
                $"  state={r.State} char={r.CharacterId ?? "?"} conf={r.Confidence:F4} " +
                $"star={r.StarLevel});" + "");
        }
    }
}
