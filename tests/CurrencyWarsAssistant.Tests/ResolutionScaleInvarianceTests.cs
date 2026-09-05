using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 决定性实验（用户 2026-08-07）：同一 3-7 帧，
/// 原始 2K（2560x1440）vs 高质量缩放 1080P（1920x1080），
/// 识别率应一致（分辨率缩放本身无损识别）；
/// 视频帧（OBS 压缩）失败 = 压缩画质而非分辨率。
/// </summary>
public sealed class ResolutionScaleInvarianceTests
{
    private readonly ITestOutputHelper _output;

    public ResolutionScaleInvarianceTests(ITestOutputHelper output)
        => _output = output;

    [Theory]
    // 2026-09-05 环境漂移实锤（非代码回归）：本用例在 08-29 基线代码（17086c3 独立
    // worktree）上同日同挂（silverWolf.CurrentCost=null），而 09-02 全量日志通过；
    // 期间费用识别相关代码（CharacterCostDisplay.cs 等）自 08-29 零改动。判定为宿主
    // 环境漂移，首要嫌疑=Windows OCR 引擎被系统更新改动（2K 缩放路径边际置信度被
    // 打破，1080P 原生路径仍通过；SlotDerive 探针同日 OCR 解析崩溃佐证）。
    // OS 侧核查后移除本 Skip，详见 handoff 同日条目。
    [InlineData("prep_3_7_142723217.png", "2K原始", Skip = "2026-09-05 环境漂移（疑似 Windows OCR 更新）：08-29 基线代码同挂+09-02 通过+相关代码零改动，详见 handoff")]
    [InlineData("p37_1080p_clean.png", "1080P高质量缩放")]
    public async Task RecognizesFormationAtAnyScale(string file, string label)
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
            file));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            $"fixture:{file}",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);
        var formation = state.Formation.Value ?? [];
        var ok = formation.Count(s =>
            s.CharacterId is not null &&
            !s.CharacterId.StartsWith(
                "unknown-formation-unit",
                StringComparison.Ordinal));
        _output.WriteLine($"[{label}] 识别 {ok}/{formation.Count}");
        Assert.True(ok >= 9,
            $"[{label}] 识别率过低：{ok}/{formation.Count}");
        var silverWolf = Assert.Single(formation.Where(s =>
            s.Zone == CurrencyWarsAssistant.Advisor.FormationZone.Front &&
            s.SlotIndex == 0 &&
            s.CharacterId == "currency_wars_character_05"));
        Assert.Equal(2, silverWolf.StarLevel);
        Assert.Equal(5, silverWolf.CurrentCost);
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "res-scale-invariance",
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
