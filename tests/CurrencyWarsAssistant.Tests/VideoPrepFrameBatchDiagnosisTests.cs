using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 视频实战备战帧批量识别（用户 2026-08-07 提供 D:\2026-08-06 22-06-16.mp4，
/// t=478-483 备战页 6 连续帧）：对比帧间各槽位识别结果，
/// 验证"动画帧失败 → 静止帧恢复"的降频场景。
/// </summary>
public sealed class VideoPrepFrameBatchDiagnosisTests
{
    private readonly ITestOutputHelper _output;

    public VideoPrepFrameBatchDiagnosisTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public async Task DiagnoseVideoPrepFramesAcrossTime()
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

        var directory = Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "video_prep");
        foreach (var file in Directory.GetFiles(directory, "prep_*.png")
                     .OrderBy(f => f))
        {
            var frame = CaptureFrameLoader.LoadFile(file);
            var state = await analyzer.AnalyzeAsync(
                frame,
                "preparation_generic",
                $"fixture:{Path.GetFileName(file)}",
                EmptySnapshot(frame.CapturedAt),
                CancellationToken.None);
            var formation = state.Formation.Value ?? [];
            var ok = formation.Count(s =>
                s.CharacterId is not null &&
                !s.CharacterId.StartsWith(
                    "unknown-formation-unit",
                    StringComparison.Ordinal));
            var fail = formation.Count - ok;
            _output.WriteLine(
                $"{Path.GetFileName(file)}: {ok}/{formation.Count} 成功, {fail} 失败");
            foreach (var s in formation.Where(s =>
                         s.CharacterId is null ||
                         s.CharacterId.StartsWith(
                             "unknown-formation-unit",
                             StringComparison.Ordinal)))
            {
                _output.WriteLine(
                    $"  失败 {s.Zone}#{s.SlotIndex} conf={s.Confidence:F3}");
            }
        }
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "video-prep-diagnosis",
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
