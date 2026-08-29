using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 全量参考图识别报告导出（用户 2026-08-07）：跑每张参考图完整识别，
/// 导出结构化 JSON（阵容/装备/羁绊/库存/金币/星级/应援/猎星人），
/// 供 python 生成桌面 HTML 详细报告。
/// </summary>
public sealed class FullRecognitionReportExportTests
{
    private readonly ITestOutputHelper _output;

    public FullRecognitionReportExportTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public async Task ExportAllReferenceRecognitionToJson()
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

        var charNames = gameData.CurrencyWarsCharacters
            .ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        // 特殊单位名字映射（2026-08-08）：佩佩模板 id 不在角色目录
        charNames["special_unit_peipei"] = "佩佩";
        charNames["bench_special_privilege_armament_box"] = "特权武装箱";
        var bondNames = GameDataCatalog.BondCatalog
            .ToDictionary(b => b.Id, b => b.Name, StringComparer.Ordinal);
        var equipNames = LoadEquipmentNames(dataDirectory);

        var fixtures = Directory.GetFiles(
            Path.Combine(
                RepositoryRoot,
                "tests",
                "CurrencyWarsAssistant.Tests",
                "Fixtures",
                "PageReplay"),
            "user_ref_*.png")
            .OrderBy(f => f)
            .Append(Path.Combine(
                RepositoryRoot,
                "tests",
                "CurrencyWarsAssistant.Tests",
                "Fixtures",
                "PageReplay",
                "prep_3_7_142723217.png"))
            .ToArray();

        var report = new ReportRoot();
        foreach (var file in fixtures)
        {
            var frame = CaptureFrameLoader.LoadFile(file);
            var state = await analyzer.AnalyzeAsync(
                frame,
                "preparation_generic",
                $"fixture:{Path.GetFileName(file)}",
                EmptySnapshot(frame.CapturedAt),
                CancellationToken.None);

            var entry = new ReportEntry
            {
                FileName = Path.GetFileName(file),
                FrameWidth = frame.Width,
                FrameHeight = frame.Height
            };

            if (string.Equals(
                    Path.GetFileName(file),
                    "user_ref_inventory_1_9_seven_items.png",
                    StringComparison.Ordinal))
            {
                Assert.Equal(
                    new[]
                    {
                        "currency_wars_equipment_153",
                        "currency_wars_equipment_045",
                        "currency_wars_equipment_042",
                        "currency_wars_equipment_045",
                        "currency_wars_equipment_041",
                        "currency_wars_equipment_038",
                        "currency_wars_equipment_133"
                    },
                    state.InventorySlots.Value!.Select(item => item.ItemId));
                Assert.Contains(
                    "currency_wars_equipment_038",
                    state.SpecialItemIds.Value!);
                Assert.Contains(
                    "currency_wars_equipment_153",
                    state.SpecialItemIds.Value!);
            }
            // 阵容
            foreach (var s in state.Formation.Value ?? [])
            {
                entry.Formation.Add(new SlotInfo
                {
                    Zone = s.Zone.ToString(),
                    SlotIndex = s.SlotIndex,
                    CharacterId = s.CharacterId,
                    CharacterName = s.CharacterId is not null
                        ? charNames.GetValueOrDefault(s.CharacterId, s.CharacterId)
                        : null,
                    Confidence = Math.Round(s.Confidence, 3),
                    StarLevel = s.StarLevel,
                    IsCheered = s.IsCheered,
                    IsHunterStar = s.IsHunterStar,
                    // 装备显示（2026-08-08 修复"识别A实际B"根因）：共享图标组
                    //（同一图标多个装备，如 圣杯/分裂•圣杯/诅咒•圣杯 字节相同）
                    // 视觉无法区分，EquipmentId 恒为组内字典序第一个 canonical——
                    // 报告必须展示组内全部候选，否则种类必错。
                    Equipment = (s.EquipmentSlots ?? [])
                        .Where(slot =>
                            slot.Occupancy == EquipmentSlotOccupancy.Equipped &&
                            slot.EquipmentId is not null)
                        .Select(slot => new EquipInfo
                        {
                            Id = slot.EquipmentId!,
                            Name = equipNames.GetValueOrDefault(
                                slot.EquipmentId!,
                                slot.EquipmentId!),
                            Candidates = (slot.CandidateEquipmentIds ?? [])
                                .Where(id => !string.Equals(
                                    id,
                                    slot.EquipmentId,
                                    StringComparison.Ordinal))
                                .Select(id => equipNames.GetValueOrDefault(id, id))
                                .ToList(),
                            Ambiguous =
                                (slot.CandidateEquipmentIds?.Count ?? 0) > 1
                        })
                        .ToList(),
                    SpecialEquipment = (s.SpecialEquipmentIds ?? []).Select(id =>
                        new EquipInfo { Id = id, Name = equipNames.GetValueOrDefault(id, id) }).ToList(),
                    Failed = s.CharacterId is null ||
                             s.CharacterId.StartsWith("unknown-formation-unit", StringComparison.Ordinal)
                });
            }

            // 羁绊
            foreach (var syn in state.ActiveSynergies.Value ?? [])
            {
                entry.Synergies.Add(new SynergyInfo
                {
                    Name = syn.SynergyId,
                    ActiveCount = syn.ActiveCount,
                    NextThreshold = syn.NextThreshold
                });
            }

            // 库存/装备栏
            foreach (var inv in state.InventorySlots.Value ?? [])
            {
                entry.Inventory.Add(new InvInfo
                {
                    SlotIndex = inv.SlotIndex,
                    Occupancy = inv.Occupancy.ToString(),
                    ItemId = inv.ItemId,
                    ItemName = inv.ItemId is not null
                        ? equipNames.GetValueOrDefault(inv.ItemId, inv.ItemId)
                        : null
                });
            }

            // 金币/经济
            if (state.CumulativeSpend.Value is { } spend)
            {
                entry.CumulativeSpend = spend;
            }

            if (state.Interest.Value is { } interest)
            {
                entry.Interest = interest;
            }

            // 节点/难度/等级/商店/扳手（2026-08-07：这些字段原版就在识别，
            // 报告此前未导出导致误以为丢失）
            entry.NodeId = state.NodeId.Value;
            entry.Difficulty = state.EnemyDifficulty.Value;
            entry.Level = state.PlayerProgress.Value?.Level;
            entry.Population = state.Population.Value;
            entry.Experience = state.PlayerProgress.Value?.Experience;
            entry.StoreLevel = state.StoreLevel.Value;
            entry.DismantleTools = state.DismantleToolCount.Value;
            entry.Health = state.Health.Value;

            report.Entries.Add(entry);
            _output.WriteLine($"导出 {entry.FileName}: 阵容 {entry.Formation.Count} 槽, " +
                              $"羁绊 {entry.Synergies.Count}, 库存 {entry.Inventory.Count}");
        }

        var outputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "cwt_recognition_report_data.json");
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        _output.WriteLine($"报告数据已写入 {outputPath}");
        Assert.NotEmpty(report.Entries);
    }

    private static IReadOnlyDictionary<string, string> LoadEquipmentNames(
        string dataDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(
                dataDirectory,
                "..",
                "runtime",
                "1.0.0",
                "4.4",
                "equipment",
                "equipment.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var item in doc.RootElement.GetProperty("records").EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    var name = item.GetProperty("name").GetString();
                    if (id is not null && name is not null)
                    {
                        result[id] = name;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 名称缺失时用 id 展示
        }

        return result;
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "report-export",
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

    private sealed class ReportRoot
    {
        public List<ReportEntry> Entries { get; set; } = [];
    }

    private sealed class ReportEntry
    {
        public string FileName { get; set; } = "";
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public List<SlotInfo> Formation { get; set; } = [];
        public List<SynergyInfo> Synergies { get; set; } = [];
        public List<InvInfo> Inventory { get; set; } = [];
        public long? CumulativeSpend { get; set; }
        public int? Interest { get; set; }
        public string? NodeId { get; set; }
        public int? Difficulty { get; set; }
        public int? Level { get; set; }
        public int? Population { get; set; }
        public int? Experience { get; set; }
        public int? StoreLevel { get; set; }
        public int? DismantleTools { get; set; }
        public int? Health { get; set; }
    }

    private sealed class SlotInfo
    {
        public string Zone { get; set; } = "";
        public int SlotIndex { get; set; }
        public string? CharacterId { get; set; }
        public string? CharacterName { get; set; }
        public double Confidence { get; set; }
        public int? StarLevel { get; set; }
        public bool IsCheered { get; set; }
        public bool IsHunterStar { get; set; }
        public bool Failed { get; set; }
        public List<EquipInfo> Equipment { get; set; } = [];
        public List<EquipInfo> SpecialEquipment { get; set; } = [];
    }

    private sealed class EquipInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Candidates { get; set; } = [];
        public bool Ambiguous { get; set; }
    }

    private sealed class SynergyInfo
    {
        public string? Name { get; set; }
        public int? ActiveCount { get; set; }
        public int? NextThreshold { get; set; }
    }

    private sealed class InvInfo
    {
        public int SlotIndex { get; set; }
        public string Occupancy { get; set; } = "";
        public string? ItemId { get; set; }
        public string? ItemName { get; set; }
    }
}

