using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 000032 实机 ground truth 断言测试（2026-08-08 建立，用户验收标准：
/// "识别对"而非"识别到"）。
/// ground truth 来源：用户验收说明 + qwen 视觉中转 + 像素标定。
/// 000032（2-2 备战）：血量 98、商店 Lv.7、人口 8/8、前台 4 卡含火花、
/// 后台含佩佩（特殊单位）、bench 4 卡、羁绊 Next 有值或"已满级"。
/// </summary>
public sealed class UserReferenceGroundTruthTests
{
    private readonly ITestOutputHelper _output;

    public UserReferenceGroundTruthTests(ITestOutputHelper output) => _output = output;

    private static Phase2OperationalState Analyze032()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000032_prep22.png"));
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        using var horizontalSpecialUnitRecognizer =
            new OpenCvHorizontalSpecialUnitRecognizer(Path.Combine(
                dataDirectory,
                "character-card-templates",
                "special_unit_peipei__horizontal-face.png"));
        // 2026-08-17：OCR 换 PpOcr（与生产一致）——Windows OCR 读
        // 商店等级 legacy 区读不出，StoreLevelRecognizesAs7 曾失败。
        using var ppOcr = new PpOcrOfflineOcr(
            Path.Combine(
                AppContext.BaseDirectory, "data", "ocr", "rapidocr",
                "PP-OCRv6_rec_small.onnx"),
            maximumConcurrency: 2);
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
            ppOcr,
            gameData,
            ppOcr,
            horizontalSpecialUnitRecognizer:
                horizontalSpecialUnitRecognizer);
        var state = analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:user_ref_000032",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None).GetAwaiter().GetResult();
        return state;
    }

    [Fact]
    public void HealthRecognizesAs98()
    {
        var state = Analyze032();
        Assert.Equal(98, state.Health.Value);
    }

    [Fact]
    public void PopulationRecognizesAs8()
    {
        var state = Analyze032();
        Assert.Equal(8, state.Population.Value);
    }

    [Fact]
    public void StoreLevelRecognizesAs7()
    {
        var state = Analyze032();
        var storeLevel = state.StoreLevel.Value;
        _output.WriteLine($"StoreLevel = {(storeLevel is { } sl ? sl.ToString() : "null")} " +
            $"(status {state.StoreLevel.Status})");
        // 用户实机 000032 商店 Lv.7（qwen 视觉 + 像素标定）
        Assert.Equal(7, state.StoreLevel.Value);
    }

    [Fact]
    public void FrontContainsSpark()
    {
        var state = Analyze032();
        var front = (state.Formation.Value ?? [])
            .Where(s => s.Zone == FormationZone.Front &&
                        !string.IsNullOrWhiteSpace(s.CharacterId) &&
                        !s.CharacterId.StartsWith(
                            "unknown-formation-unit",
                            StringComparison.Ordinal))
            .Select(s => s.CharacterId)
            .ToArray();
        _output.WriteLine($"Front: {string.Join(", ", front)}");
        // 火花 = currency_wars_character_09（用户验收：前台火花无法识别，
        // 修复后必须识别）
        Assert.Contains("currency_wars_character_09", front);
    }

    [Fact]
    public void BackContainsPeipei()
    {
        var state = Analyze032();
        var back = (state.Formation.Value ?? [])
            .Where(s => s.Zone == FormationZone.Back &&
                        !string.IsNullOrWhiteSpace(s.CharacterId) &&
                        !s.CharacterId.StartsWith(
                            "unknown-formation-unit",
                            StringComparison.Ordinal))
            .Select(s => s.CharacterId)
            .ToArray();
        _output.WriteLine($"Back: {string.Join(", ", back)}");
        // 佩佩 = special_unit_peipei（特殊单位，无星级）
        Assert.Contains("special_unit_peipei", back);
    }

    [Fact]
    public void BackPeipeiOnlyNoHorizontalSpecialMember()
    {
        var state = Analyze032();
        var peipei = (state.Formation.Value ?? [])
            .Where(item => item.CharacterId == "special_unit_peipei")
            .ToArray();

        // 用户方案（2026-08-18）：佩佩/狸猫只可能出现在后台。修复后不再有
        // 横向识别器在右上角(340,295)阿哈/命途位置误判出的 zone=Special 佩佩。
        Assert.Multiple(
            () => Assert.Single(peipei),
            () => Assert.Equal(
                FormationZone.Back,
                Assert.Single(peipei).Zone),
            () => Assert.DoesNotContain(
                peipei,
                item => item.Zone == FormationZone.Special));
    }

    [Fact]
    public void BenchRecognizesFourCards()
    {
        var state = Analyze032();
        var bench = (state.Formation.Value ?? [])
            .Where(s => s.Zone == FormationZone.Bench &&
                        !string.IsNullOrWhiteSpace(s.CharacterId) &&
                        !s.CharacterId.StartsWith(
                            "unknown-formation-unit",
                            StringComparison.Ordinal))
            .ToArray();
        _output.WriteLine($"Bench: {bench.Length} 卡");
        // 000032 bench 实机 4 卡（爻光/刃/开拓者/缇宝，像素验证 4 槽有内容）
        Assert.Equal(4, bench.Length);
    }

    [Fact]
    public void SynergyNextThresholdPopulated()
    {
        var state = Analyze032();
        var synergies = state.ActiveSynergies.Value ?? [];
        foreach (var s in synergies)
        {
            _output.WriteLine($"{s.SynergyId} active={s.ActiveCount} next={s.NextThreshold?.ToString() ?? "已满级"}");
        }

        // 欢愉 tier [3,4,5,7]，ActiveCount=5（含火花 09 的欢愉羁绊，
        // 2026-08-08 火花模板修复后欢愉 4→5）→ Next=7
        var huanYu = synergies.FirstOrDefault(s =>
            (s.SynergyId ?? string.Empty).EndsWith("欢愉", StringComparison.Ordinal));
        Assert.NotNull(huanYu);
        Assert.Equal(5, huanYu.ActiveCount);
        Assert.Equal(7, huanYu.NextThreshold);
        // 头号玩家 tier [1] → 已满级（Next=null 是正确语义，渲染"已满级"）
        var gamer = synergies.FirstOrDefault(s =>
            (s.SynergyId ?? string.Empty).EndsWith("头号玩家", StringComparison.Ordinal));
        Assert.NotNull(gamer);
        Assert.Equal(1, gamer.ActiveCount);
        Assert.Null(gamer.NextThreshold);
    }

    [Fact]
    public void SlotIndicesAreCorrect()
    {
        var state = Analyze032();
        var front = (state.Formation.Value ?? [])
            .Where(s => s.Zone == FormationZone.Front)
            .Select(s => s.SlotIndex)
            .OrderBy(i => i)
            .ToArray();
        // 前台 4 槽索引 0-3（应援路径 SlotIndex 修复）
        Assert.Equal(new[] { 0, 1, 2, 3 }, front);
    }

    [Fact]
    public void BackStarLevelsMatchGroundTruth()
    {
        var state = Analyze032();
        var back = (state.Formation.Value ?? [])
            .Where(s => s.Zone == FormationZone.Back)
            .ToDictionary(s => s.CharacterId ?? string.Empty, s => s.StarLevel);
        foreach (var kv in back)
        {
            _output.WriteLine($"Back {kv.Key} star={kv.Value?.ToString() ?? "null"}");
        }

        // 用户 2026-08-08 实机 ground truth：后台除爻光（2 星）外全 1 星，
        // 佩佩（特殊单位）无星级
        Assert.Equal(1, back.GetValueOrDefault("currency_wars_character_02")); // 千冶•刃
        Assert.Equal(1, back.GetValueOrDefault("currency_wars_character_trailblazer")); // 开拓者
        Assert.Equal(2, back.GetValueOrDefault("currency_wars_character_10")); // 爻光
        Assert.Null(back.GetValueOrDefault("special_unit_peipei")); // 佩佩无星级
    }

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(GameDataCatalog gameData)
    {
        var directory = Path.Combine(RepositoryRoot, "data", "4.4", "character-card-templates");
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

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "ground-truth-032",
        AsOf = asOf
    };
}
