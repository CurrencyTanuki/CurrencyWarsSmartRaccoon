using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 3-7 备战帧全槽位诊断（2026-08-07 用户要求"搞清楚哪里识别失败/正确"）：
/// 输出全部 board(10)+bench(9) 槽位的坐标、软件识别结果（char/conf/candidates），
/// 供与真实画面逐槽核对。修复前先建立无歧义的事实表。
/// </summary>
public sealed class Phase2Prep37FullSlotDiagnosisTests
{
    [Fact]
    public async Task DumpAllSlotRecognitions()
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

        // board 10 槽（1920 参考坐标）
        var board = new (int Idx, int X, int Y, int W, int H)[]
        {
            (0, 681, 329, 128, 140),
            (1, 827, 329, 122, 140),
            (2, 972, 329, 120, 140),
            (3, 1114, 329, 120, 140),
            (4, 535, 600, 140, 145),
            (5, 687, 600, 130, 145),
            (6, 829, 600, 130, 145),
            (7, 966, 600, 130, 145),
            (8, 1108, 600, 130, 145),
            (9, 1258, 600, 130, 145),
        };
        // bench 9 槽
        var bench = new (int Idx, int X, int Y, int W, int H)[]
        {
            (0, 383, 844, 114, 137),
            (1, 506, 844, 119, 137),
            (2, 633, 844, 117, 137),
            (3, 759, 844, 114, 137),
            (4, 883, 844, 116, 137),
            (5, 1005, 844, 116, 137),
            (6, 1128, 844, 116, 137),
            (7, 1250, 844, 116, 137),
            (8, 1374, 844, 116, 137),
        };

        Console.WriteLine("=== BOARD 槽位（1920 参考坐标）===");
        foreach (var s in board)
        {
            DumpSlot(frame, s.Idx, s.X, s.Y, s.W, s.H, "board");
        }

        Console.WriteLine("=== BENCH 槽位 ===");
        foreach (var s in bench)
        {
            DumpSlot(frame, s.Idx, s.X, s.Y, s.W, s.H, "bench");
        }

        Console.WriteLine("=== 软件最终 Formation ===");
        foreach (var slot in state.Formation.Value ?? [])
        {
            Console.WriteLine(
                $"zone={slot.Zone} idx={slot.SlotIndex} char={slot.CharacterId} " +
                $"conf={slot.Confidence:F3} star={slot.StarLevel?.ToString() ?? "-"} " +
                $"cheered={slot.IsCheered} hunter={slot.IsHunterStar} " +
                $"cand={string.Join(",", (slot.CandidateCharacterIds ?? []).Take(3))}");
        }
    }

    private static void DumpSlot(
        CaptureFrame frame,
        int idx,
        int x,
        int y,
        int w,
        int h,
        string zone)
    {
        var sx = frame.Width / 1920d;
        var sy = frame.Height / 1080d;
        var px = (int)(x * sx);
        var py = (int)(y * sy);
        var pw = (int)(w * sx);
        var ph = (int)(h * sy);
        Console.WriteLine(
            $"SLOT {zone}[{idx}] ref=({x},{y},{w},{h}) px=({px},{py},{pw},{ph})");
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "prep37-full-diagnosis",
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
