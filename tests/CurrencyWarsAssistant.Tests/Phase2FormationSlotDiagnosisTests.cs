using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 阵容槽位识别诊断（2026-08-06 会话 Z2）：
/// 用 run-20260806-125919 的真实 1-3 备战帧，输出每个角色槽位的
/// 最佳候选/亚军置信度，定位"槽位 conf 0.38-0.57 全部 Uncertain"根因
/// （瓦尔特识别/羁绊/阵容显示共同依赖槽位识别成功）。
/// </summary>
public sealed class Phase2FormationSlotDiagnosisTests
{
    [Fact]
    public async Task DiagnosePreparationSlotConfidence()
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
            "walter_diag_prep_1_3.png"));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:walter-diag",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var formation = state.Formation.Value ?? [];
        Assert.NotEmpty(formation);
        foreach (var slot in formation)
        {
            System.Console.WriteLine(
                $"SLOT {slot.Zone}#{slot.SlotIndex} char={slot.CharacterId} conf={slot.Confidence:F2} " +
                $"candidates={string.Join(",", (slot.CandidateCharacterIds ?? []).Take(3))}");
        }

        // 阈值放宽回归（0.58→0.55）：实测 conf 0.56-0.57 的 Bench 槽位
        // 必须识别为最佳候选（character_60），不再整条 Unknown。
        var bench0 = formation.FirstOrDefault(item =>
            item.Zone == FormationZone.Bench && item.SlotIndex == 0);
        var bench1 = formation.FirstOrDefault(item =>
            item.Zone == FormationZone.Bench && item.SlotIndex == 1);
        Assert.NotNull(bench0);
        Assert.NotNull(bench1);
        Assert.Equal("currency_wars_character_60", bench0.CharacterId);
        Assert.Equal("currency_wars_character_60", bench1.CharacterId);

        // 羁绊累加（用户 2026-08-06）：阵容角色 → 各角色 bonds → 累加
        // ActiveCount。阵容识别成功时应产生 Known 的累加羁绊（不再仅图标识别）。
        var synergies = state.ActiveSynergies;
        Assert.Equal(ObservationStatus.Known, synergies.Status);
        Assert.NotNull(synergies.Value);
        Assert.NotEmpty(synergies.Value);
        foreach (var synergy in synergies.Value)
        {
            System.Console.WriteLine(
                $"SYNERGY {synergy.SynergyId} active={synergy.ActiveCount} threshold={synergy.NextThreshold}");
            Assert.True(synergy.ActiveCount >= 1);
        }
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "slot-diagnosis",
        AsOf = asOf
    };

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(GameDataCatalog gameData)
    {
        var directory = Path.Combine(RepositoryRoot, "data", "4.4", "character-card-templates");
        // 同角色可有多张卡面模板（开拓者记忆/欢愉、银狼变费 3/4/5 费背景色
        // 不同）——全部加载，匹配时取最高分（用户 2026-08-07 实机变体）。
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
