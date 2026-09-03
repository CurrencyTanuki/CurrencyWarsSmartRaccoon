using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 星徽账本真实帧端到端探针（2026-09-03 晚用户拍板方案的验收测试）：
/// 用真实备战帧（prep_1_6，爻光在 F2）走完整 Phase2 识别管线产出 OperationalState，
/// 再模拟 A4 装配记账（RecordBadgeEquippedAtSlot），断言组装器按"识别∪账本"并集
/// 把爻光判为星徽携带者并计入羁绊——真实识别输出（该帧爻光装备=064/066，无 001 徽章）
/// 漏读徽章时账本必须兜底，决策层羁绊计数不依赖装备识别。
/// </summary>
public sealed class StarBadgeLedgerRealFrameTests
{
    private readonly ITestOutputHelper _output;

    public StarBadgeLedgerRealFrameTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public async Task RealFrameWithLedgerEntry_BadgeCarrierCountedDespiteEquipmentMiss()
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

        var economySnapshot = new RunSnapshot
        {
            RunId = "badge-ledger-real-frame",
            AsOf = frame.CapturedAt
        };
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:badge-ledger-real-frame",
            economySnapshot,
            CancellationToken.None);

        // 真实识别前提：爻光在 F2（slotIndex 1），且该帧识别的装备里没有星徽 001
        //（prep_1_6 实拍=064/066 两件；有 001 反而说明夹具选错）。
        var yaoGuang = Assert.Single((state.Formation.Value ?? []).Where(item =>
            item.Zone == FormationZone.Front &&
            item.SlotIndex == 1 &&
            item.CharacterId == "currency_wars_character_10"));
        Assert.DoesNotContain(yaoGuang.EquipmentSlots ?? [],
            equipment => string.Equals(
                equipment.EquipmentId,
                GrailSnapshotAssembler.StarBadgeEquipmentId,
                StringComparison.OrdinalIgnoreCase));

        // 模拟 A4 成功：账本记 F2（位置语义，角色名未知）
        var holder = new GrailRunStateHolder();
        holder.RecordBadgeEquippedAtSlot(GrailSnapshotAssembler.BadgeLedgerSlotKey(FormationZone.Front, 1));

        var snapshot = GrailSnapshotAssembler.Assemble(
            state,
            economySnapshot,
            gameData,
            holder,
            GrailUserGoal.Single,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(15));

        _output.WriteLine($"场上明细=[{string.Join("、", snapshot.DeployedCharacterDetails)}]");
        _output.WriteLine($"携带者数={snapshot.BadgeCarrierNonMembers} 星徽总数={snapshot.TotalStarBadgesObtained} 羁绊计数={snapshot.BondMemberCount}");

        // 账本兜底：爻光被判为携带者（装备识别没读到 001 也要算）
        Assert.Contains("爻光", snapshot.OwnedCharacterNames);
        Assert.Equal(1, snapshot.BadgeCarrierNonMembers);
        Assert.Equal(1, snapshot.TotalStarBadgesObtained);
        Assert.Equal(1, snapshot.BondMemberCount); // 爻光非命杯成员，携带者+1
        Assert.Contains(snapshot.DeployedCharacterDetails, item => item.Contains("爻光") && item.Contains("星徽"));
        // 提升为按名携带（换槽后徽随人走）
        var (carriers, pending) = holder.PeekBadgeLedger();
        Assert.Contains("爻光", carriers);
        Assert.Empty(pending);
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
