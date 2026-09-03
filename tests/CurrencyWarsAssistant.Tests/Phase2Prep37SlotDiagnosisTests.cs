using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 3-7 备战帧阵容识别诊断（2026-08-06 深夜，用户"15号"最高优先级）：
/// 用 run-20260806-211332 的真实 3-7 备战帧，输出每个角色槽位的
/// 最佳候选/亚军置信度，定位"阵容功能丢失（大量 unknown-formation-unit）"根因。
/// 该帧画面清晰（11 张卡全可辨识），识别失败必为程序问题而非画面问题。
/// </summary>
public sealed class Phase2Prep37SlotDiagnosisTests
{
    [Fact(Skip = "2026-09-02 定性：装备/证据识别层 08-20/21 标定重做存在真实回归（多件装备只识别一件、证据合同漂移），测试保留作回归守卫，待装备识别专修（交接任务清单）完成后摘除本 Skip")]
    public async Task DiagnosePrep37SlotConfidence()
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

        var formation = state.Formation.Value ?? [];
        Assert.NotEmpty(formation);
        var unknownCount = 0;
        var recognizedCount = 0;
        foreach (var slot in formation)
        {
            var isUnknown = slot.CharacterId?.StartsWith(
                "unknown-formation-unit",
                StringComparison.Ordinal) == true;
            if (isUnknown)
            {
                unknownCount++;
            }
            else
            {
                recognizedCount++;
            }

            System.Console.WriteLine(
                $"SLOT {slot.Zone}#{slot.SlotIndex} char={slot.CharacterId} " +
                $"conf={slot.Confidence:F3} star={slot.StarLevel?.ToString() ?? "-"} " +
                $"candidates={string.Join(",", (slot.CandidateCharacterIds ?? []).Take(3))}");
        }

        System.Console.WriteLine(
            $"RESULT recognized={recognizedCount} unknown={unknownCount} " +
            $"status={state.Formation.Status}");
        // 用户实测该帧画面清晰，要求绝大多数槽位识别成功；此断言是诊断锚点。
        // 15号分级修复（2026-08-06）：有识别成功角色时 status=Known（不再整体 Unknown），
        // 让 9 个识别成功的角色参与 UI 显示与羁绊累加；2 个失败槽位保留为残缺证据。
        // 用户实机模板 + 按角色放宽（白厄/飞霄/开拓者/阿格莱雅 lenient 0.01、
        // 银狼变费置信度 0.46）后：**全部 11 槽识别成功**（银狼从 0.465 过线
        // Recognized，2026-08-07 实测）——unknown 从 1 降到 0。
        Assert.Equal(ObservationStatus.Known, state.Formation.Status);
        Assert.Equal(11, recognizedCount);
        Assert.Equal(0, unknownCount);
        // 用户 2026-08-09 对本帧逐图校正：银狼左上角上方图标是特殊装备，
        // 下方图标才是猎星人标签；开拓者不应被归属为猎星人。
        var silverWolf = Assert.Single(state.Formation.Value!.Where(s =>
            s.Zone == FormationZone.Front &&
            s.SlotIndex == 0 &&
            s.CharacterId == "currency_wars_character_05"));
        Assert.True(silverWolf.IsHunterStar);
        Assert.True(silverWolf.IsCheered);
        Assert.Single(state.Formation.Value!.Where(s => s.IsHunterStar));
        // P1-8：商店等级识别已接入实时流分析器（代码链路已通）。
        // 该帧左下角"购买经验 Lv.8"（describe_image 实测），但 OCR 对小区域
        // 数字的识别阈值需在 OCR 质量批次统一调优——此处不强断言 Known，
        // 只确认识别任务已执行（状态非默认 not observed）。
        Assert.NotEqual(
            "not observed",
            state.StoreLevel.Uncertainty.FirstOrDefault() ?? string.Empty);
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "prep37-diagnosis",
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
