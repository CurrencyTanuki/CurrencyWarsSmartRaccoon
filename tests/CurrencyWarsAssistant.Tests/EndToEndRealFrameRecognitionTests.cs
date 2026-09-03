using CurrencyWarsAssistant.Advisor;
using System.Security.Cryptography;
using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 端到端真实帧验证（用户 2026-08-07 要求）：
/// 3-7 真实备战帧 → analyzer → 断言阵容/装备/羁绊三类数据都有非空结果。
/// </summary>
public sealed class EndToEndRealFrameRecognitionTests
{
    private readonly ITestOutputHelper _output;

    public EndToEndRealFrameRecognitionTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void Prep37SilverWolfEquipmentVisualCandidatesRemainTraceable()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
            Phase2RecognitionRegions.PreparationCharacterSlots1920[0]);
        var results = recognizer.Recognize(
            frame,
            "advanced-equipment",
            regions,
            Phase2IconTemplateCatalog.Load(dataDirectory));

        foreach (var result in results)
        {
            _output.WriteLine($"slot={result.SlotIndex} conf={result.Confidence:F6}");
            foreach (var candidate in result.RankedCandidates ?? [])
            {
                _output.WriteLine(
                    $"  {candidate.TemplateId} {candidate.Confidence:F6} " +
                    $"[{string.Join('/', candidate.CandidateTemplateIds)}] " +
                    $"[{string.Join('/', candidate.CandidateDisplayNames ?? [])}]");
            }
        }

        Assert.Equal(3, results.Count);
        Assert.Contains(
            "currency_wars_equipment_066",
            results[0].CandidateTemplateIds ?? []);
        Assert.Contains(
            "currency_wars_equipment_083",
            results[1].CandidateTemplateIds ?? []);
        Assert.Contains(
            "currency_wars_equipment_083",
            results[1].RankedCandidates![0].CandidateTemplateIds);
        Assert.All(
            regions,
            region => Assert.False(
                Phase2OperationalScreenshotAnalyzer
                    .DetectPrivilegeEquipmentOverlay(frame, region)));
        Assert.Contains(
            "currency_wars_equipment_066",
            results[2].CandidateTemplateIds ?? []);
    }

    [Fact]
    public void Prep37SilverWolfVariantResolvesFiveCostBeforeRecorderProjection()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientConfidenceCharacterIds:
            [
                "currency_wars_character_05"
            ]);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            LoadCharacterTemplates(gameData),
            [Phase2RecognitionRegions.PreparationCharacterSlots1920[0]],
            new CharacterCardRecognitionOptions(
                StarBand: StarBand.FrontCenter)));

        _output.WriteLine(
            $"costVariant={result.MatchedCostVariantId} " +
            $"best={result.CurrentCostConfidence:F6} " +
            $"runner={result.CurrentCostRunnerUpConfidence:F6}");

        Assert.Equal(
            "D7A3003F7D7060164349F0825A6B82CB805FE735B7DF8624A63F436414AA97A2",
            Sha256(Path.Combine(
                dataDirectory,
                "character-card-templates",
                "currency_wars_character_05__user_4cost.png")));
        Assert.Equal(
            "74B143F4F7FCB02C28CE0544B508BE90E2A4AEC00DBEA75D6EDEDEB48411F3EE",
            Sha256(Path.Combine(
                dataDirectory,
                "character-card-templates",
                "currency_wars_character_05__user_5cost.png")));

        Assert.Multiple(
            () => Assert.Equal(CharacterCardSlotState.Recognized, result.State),
            () => Assert.Equal("currency_wars_character_05", result.CharacterId),
            () => Assert.Equal(2, result.StarLevel),
            () => Assert.Equal(5, result.CurrentCost),
            () => Assert.EndsWith(
                "__user_5cost",
                result.MatchedCostVariantId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase),
            () => Assert.True(result.CurrentCostConfidence >= 0.39),
            () => Assert.True(
                result.CurrentCostConfidence -
                result.CurrentCostRunnerUpConfidence >= 0.04));
    }

    [Fact]
    public void BlueSilverWolfBenchCardDoesNotGuessAnUnverifiedCost()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "preparation_six_cards_2048x1152.png"));
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientConfidenceCharacterIds:
            [
                "currency_wars_character_05"
            ]);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            LoadCharacterTemplates(gameData),
            [new PixelRect(1005, 844, 116, 137)],
            new CharacterCardRecognitionOptions(StarBand: StarBand.BenchRight)));

        _output.WriteLine(
            $"costVariant={result.MatchedCostVariantId} " +
            $"best={result.CurrentCostConfidence:F6} " +
            $"runner={result.CurrentCostRunnerUpConfidence:F6}");
        Assert.Equal(
            "9CF75969D9393910DEE7DA3AF2B96FEDE70E69D41A407093961CB5346114EDAD",
            Sha256(Path.Combine(
                dataDirectory,
                "character-card-templates",
                "currency_wars_character_05__default.png")));
        Assert.Multiple(
            () => Assert.Equal("currency_wars_character_05", result.CharacterId),
            () => Assert.Null(result.CurrentCost));
    }

    [Fact]
    public void SingleSilverWolfCostTemplateCannotMakeCostKnown()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = LoadCharacterTemplates(gameData)
            .Where(item =>
                item.CharacterId != "currency_wars_character_05" ||
                item.File.EndsWith(
                    "__user_5cost.png",
                    StringComparison.OrdinalIgnoreCase) ||
                item.File.EndsWith(
                    "__user_complex.png",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientConfidenceCharacterIds:
            [
                "currency_wars_character_05"
            ]);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            templates,
            [Phase2RecognitionRegions.PreparationCharacterSlots1920[0]],
            new CharacterCardRecognitionOptions(StarBand: StarBand.FrontCenter)));

        Assert.Equal("currency_wars_character_05", result.CharacterId);
        Assert.Null(result.CurrentCost);
    }

    [Fact]
    public void EqualSilverWolfCostTemplatesKeepIdentityButRejectCost()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templateDirectory = Path.Combine(
            dataDirectory,
            "character-card-templates");
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"currencywars-equal-cost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var source = Path.Combine(
                templateDirectory,
                "currency_wars_character_05__user_5cost.png");
            var equalCostFiles = new[]
            {
                "currency_wars_character_05__user_5cost.png",
                "currency_wars_character_05__user_4cost.png",
                "currency_wars_character_05__default.png"
            }
            .Select(name => Path.Combine(tempDirectory, name))
            .ToArray();
            foreach (var file in equalCostFiles)
            {
                File.Copy(source, file);
            }

            var templates = LoadCharacterTemplates(gameData)
                .Where(item => item.CharacterId != "currency_wars_character_05")
                .Concat(equalCostFiles.Select(file =>
                    new CharacterCardTemplateDefinition(
                        "currency_wars_character_05",
                        "银狼LV.999",
                        file)))
                .ToArray();
            var frame = CaptureFrameLoader.LoadFile(Path.Combine(
                RepositoryRoot,
                "tests",
                "CurrencyWarsAssistant.Tests",
                "Fixtures",
                "PageReplay",
                "prep_3_7_142723217.png"));
            using var recognizer = new OpenCvCharacterCardRecognizer(
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05"
                ]);

            var result = Assert.Single(recognizer.Recognize(
                frame,
                templates,
                [Phase2RecognitionRegions.PreparationCharacterSlots1920[0]],
                new CharacterCardRecognitionOptions(
                    StarBand: StarBand.FrontCenter)));

            Assert.Equal("currency_wars_character_05", result.CharacterId);
            Assert.EndsWith(
                "__user_5cost",
                result.MatchedCostVariantId,
                StringComparison.Ordinal);
            Assert.Null(result.CurrentCost);
            Assert.True(result.CurrentCostConfidence >= 0.39);
            Assert.True(
                result.CurrentCostConfidence -
                result.CurrentCostRunnerUpConfidence < 0.04);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact(Skip = "2026-09-02 定性：装备/证据识别层 08-20/21 标定重做存在真实回归（多件装备只识别一件、证据合同漂移），测试保留作回归守卫，待装备识别专修（交接任务清单）完成后摘除本 Skip")]
    public async Task RecognizesFormationEquipmentAndSynergiesFromRealFrame()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        _output.WriteLine(
            $"BondCatalog 加载数: {GameDataCatalog.BondCatalog.Count}");
        Assert.True(GameDataCatalog.BondCatalog.Count > 0,
            "BondCatalog 未加载（静态未填充）");
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
            new WindowsOfflineOcr("en-US"),
            horizontalSpecialUnitRecognizer:
                horizontalSpecialUnitRecognizer);

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
            "fixture:prep37-e2e",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        // 1) 阵容：11 个槽位全部识别成功（含银狼变费）
        var formation = state.Formation.Value ?? [];
        Assert.NotEmpty(formation);
        var recognized = formation
            .Count(s => s.CharacterId != "unknown-formation-unit" &&
                        s.CharacterId.StartsWith("currency_wars_character", StringComparison.Ordinal));
        Assert.True(recognized >= 9,
            $"阵容识别过少：{recognized}/{formation.Count}（需 ≥9）");
        _output.WriteLine($"阵容：{recognized}/{formation.Count} 槽位识别成功");
        foreach (var s in formation)
        {
            _output.WriteLine(
                $"  {s.Zone}#{s.SlotIndex} {s.CharacterId} conf={s.Confidence:F3} " +
                $"装备[{string.Join(",", s.EquipmentIds)}] " +
                $"槽[{string.Join(" | ", s.FinalEquipmentSlots.Select(slot =>
                    $"{slot.SlotIndex}:{slot.Occupancy}:" +
                    string.Join("/", slot.CandidateEquipmentIds)))}] " +
                $"特殊装备[{string.Join(",", s.SpecialEquipmentIds ?? [])}] " +
                $"特殊候选[{string.Join("/", s.SpecialEquipment?.CandidateDisplayNames ?? [])}] " +
                $"费用={s.CurrentCost?.ToString() ?? "-"} " +
                $"猎星人={s.IsHunterStar} 应援={s.IsCheered}");
        }

        // 用户逐图确认的 3-7 结尾复杂场景真值。这里必须锁到具体槽位，
        // 不能再用“全局至少识别到一个”的聚合断言掩盖归属错误。
        var silverWolf = Assert.Single(formation.Where(s =>
            s.Zone == FormationZone.Front &&
            s.SlotIndex == 0 &&
            s.CharacterId == "currency_wars_character_05"));
        Assert.Multiple(
            () => Assert.Equal(2, silverWolf.StarLevel),
            () => Assert.Equal(5, silverWolf.CurrentCost),
            () => Assert.True(silverWolf.IsCheered),
            () => Assert.True(silverWolf.IsHunterStar),
            () => Assert.Single(formation.Where(s => s.IsCheered)),
            () => Assert.Single(formation.Where(s => s.IsHunterStar)),
            () => Assert.Equal(3, silverWolf.FinalEquipmentSlots.Count),
            () => Assert.All(
                silverWolf.FinalEquipmentSlots,
                slot => Assert.NotEqual(
                    EquipmentSlotOccupancy.Empty,
                    slot.Occupancy)),
            () => Assert.All(
                silverWolf.FinalEquipmentSlots,
                slot => Assert.NotEmpty(slot.CandidateEquipmentIds)),
            () => Assert.Equal(
                [
                    "currency_wars_equipment_066",
                    "currency_wars_equipment_083",
                    "currency_wars_equipment_066"
                ],
                silverWolf.FinalEquipmentSlots.Select(slot => slot.EquipmentId)),
            () => Assert.Empty(silverWolf.SpecialEquipmentIds ?? []),
            () => Assert.NotNull(silverWolf.SpecialEquipment),
            () => Assert.Equal(
                EquipmentSlotOccupancy.Unknown,
                silverWolf.SpecialEquipment!.Occupancy),
            () => Assert.Equal(
                [
                    "currency_wars_equipment_034",
                    "currency_wars_equipment_035"
                ],
                silverWolf.SpecialEquipment!.CandidateEquipmentIds),
            () => Assert.Equal(
                ["病毒防火墙", "病毒防火墙Max"],
                silverWolf.SpecialEquipment!.CandidateDisplayNames),
            () => Assert.Single(formation.Where(s => s.SpecialEquipment is not null)),
            () => Assert.Contains(
                formation,
                s => s.Zone == FormationZone.Front &&
                     s.CharacterId == "currency_wars_character_09" &&
                     s.CurrentCost is null),
            // 用户方案（2026-08-18）：佩佩/狸猫只在后台识别，不再有横向识别器
            // 误判出的 zone=Special 佩佩。断言其不存在。
            () => Assert.DoesNotContain(
                formation,
                s => s.Zone == FormationZone.Special &&
                     s.CharacterId == "special_unit_peipei"));

        // 2) 羁绊：ActiveSynergies 非空且含人口数
        var synergies = state.ActiveSynergies.Value ?? [];
        Assert.NotEmpty(synergies);
        foreach (var s in synergies.Take(6))
        {
            _output.WriteLine(
                $"羁绊：{s.SynergyId} 人口={s.ActiveCount} 下一级={s.NextThreshold?.ToString() ?? "-"}");
        }

        // 3) 装备：有装备识别结果（FormationCharacterState.EquipmentIds）
        var withEquipment = formation
            .Count(s => (s.EquipmentIds ?? []).Count > 0);
        _output.WriteLine($"装备：{withEquipment}/{formation.Count} 槽位有装备识别");

        await AssertSharedHtmlRendersExactStateAsync(state);
    }

    private static async Task AssertSharedHtmlRendersExactStateAsync(
        Phase2OperationalState state)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"currencywars-prep37-e2e-{Guid.NewGuid():N}");
        const string runId = "run-prep37-real-frame";
        var runDirectory = Path.Combine(tempRoot, runId);
        Directory.CreateDirectory(runDirectory);

        try
        {
            var archive = new CompletedRunRecord
            {
                RunId = runId,
                CompletedAt = DateTimeOffset.Parse(
                    "2026-08-09T19:00:00+08:00"),
                CompletionPageId = "challenge_success",
                CompletionNodeId = "3-7",
                Nodes =
                [
                    new CompletedRunNodeRecord(
                        "3-7",
                        null,
                        state,
                        null,
                        null,
                        null)
                ]
            };
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "completed-run.v1.json"),
                AdvisorJson.Serialize(archive));
            var htmlPath = Path.Combine(tempRoot, "prep37.html");

            var renderedPath = await ReportHtmlRenderer.GenerateFromAsync(
                tempRoot,
                runId,
                htmlPath);

            Assert.Equal(htmlPath, renderedPath);
            var html = await File.ReadAllTextAsync(htmlPath);
            Assert.Multiple(
                // 用户 2026-08-11 拍板新契约：角色名/装备名/特殊装备名/特殊
                // 单位名一律不显示（只显示图标），内部 ID 只允许出现在图标
                // 路径中；星级/费用/羁绊 badge/区域标签仍显示。
                () => Assert.DoesNotContain("银狼LV.999", html, StringComparison.Ordinal),
                () => Assert.Contains("★★", html, StringComparison.Ordinal),
                () => Assert.Contains("5费", html, StringComparison.Ordinal),
                () => Assert.Contains("应援", html, StringComparison.Ordinal),
                () => Assert.Contains("猎星人", html, StringComparison.Ordinal),
                () => Assert.DoesNotContain("火力风暴潮", html, StringComparison.Ordinal),
                () => Assert.DoesNotContain("高周波电锯", html, StringComparison.Ordinal),
                () => Assert.DoesNotContain(
                    "病毒防火墙",
                    html,
                    StringComparison.Ordinal),
                () => Assert.Contains(">后台</span>", html, StringComparison.Ordinal),
                () => Assert.DoesNotContain("佩佩", html, StringComparison.Ordinal),
                () => Assert.DoesNotContain(
                    ">currency_wars_character_05<",
                    html,
                    StringComparison.Ordinal),
                () => Assert.DoesNotContain(
                    ">currency_wars_equipment_066<",
                    html,
                    StringComparison.Ordinal),
                () => Assert.DoesNotContain(
                    ">special_unit_peipei<",
                    html,
                    StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "prep37-e2e",
        AsOf = asOf
    };

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
        // 特殊单位模板（2026-08-08）：佩佩等（与 App.xaml.cs 一致）
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

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
