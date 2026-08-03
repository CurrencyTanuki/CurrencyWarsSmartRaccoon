using System.Reflection;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class HistoricalUiFieldCoverageTests
{
    [Fact]
    public void Registry_CoversEveryPublicPropertyOfFinalDataTypes()
    {
        foreach (var modelType in HistoricalUiFieldCoverageRegistry.CoveredModelTypes)
        {
            var publicProperties = modelType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var mappedProperties = HistoricalUiFieldCoverageRegistry.Fields
                .Where(field => field.ModelType == modelType)
                .Select(field => field.PropertyName)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(publicProperties, mappedProperties);
        }
    }

    [Fact]
    public void Registry_HasNoDuplicateOrSilentCoverage()
    {
        var duplicates = HistoricalUiFieldCoverageRegistry.Fields
            .GroupBy(field => (field.ModelType, field.PropertyName))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.ModelType.Name}.{group.Key.PropertyName}")
            .ToArray();

        Assert.Empty(duplicates);
        Assert.All(HistoricalUiFieldCoverageRegistry.Fields, field =>
        {
            Assert.False(string.IsNullOrWhiteSpace(field.UiSection));
            Assert.False(string.IsNullOrWhiteSpace(field.Rationale));
        });
    }

    [Fact]
    public void DetailBuilder_AlwaysShowsRequiredSectionsAndUnknownReasons()
    {
        var builder = new HistoricalDetailPresentationBuilder(
            new GameDataCatalog([], [], [], [], []));
        var now = DateTimeOffset.Parse("2026-08-01T18:00:00+08:00");
        var snapshot = new RunSnapshot
        {
            RunId = "run-coverage",
            AsOf = now,
            Stage = Observation<string>.Known("1-3", 0.95, observedAt: now)
        };
        var state = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            NodeId = Observation<string>.Known("1-3", 0.95, observedAt: now)
        };
        var model = builder.Build(new HistoricalNodeDetailEntry(
            "run-coverage",
            "1-3",
            snapshot,
            state,
            state,
            null,
            new ScreenshotAnalysisResult
            {
                AnalysisId = "analysis-coverage",
                Snapshot = snapshot,
                OperationalState = state,
                UnknownFields = ["economy"]
            },
            now));

        Assert.Equal(
            [
                "节点概况",
                "经济与成长",
                "历史阵容与角色装备",
                "装备栏与局内资源",
                "构筑、羁绊与敌情",
                "最终战斗与表格数据",
                "伤害来源明细",
                "节点事件链",
                "识别状态、原始 OCR 与诊断"
            ],
            model.Sections.Select(section => section.Title));

        var rows = model.Sections.SelectMany(section => section.Fields).ToArray();
        Assert.Contains(rows, row => row.Label == "前台角色" && row.Value == "未记录");
        Assert.Contains(rows, row => row.Label == "后台角色" && row.Value == "未记录");
        Assert.Contains(rows, row => row.Label == "备战席角色" && row.Value == "未记录");
        Assert.Contains(rows, row =>
            row.Label == "已确认负面词条集合" && row.Value == "未记录");
        Assert.Contains(rows, row =>
            row.Label == "负面词条槽位 4" && row.Value == "未记录");
        Assert.Contains(rows, row => row.Label == "最终伤害" && row.Value == "未记录");
        Assert.DoesNotContain(rows, row => row.Value == "0" && row.Meta.Contains("Unknown"));
        Assert.All(
            rows.Where(row => row.Value is "未记录" or "数据不足"),
            row => Assert.False(string.IsNullOrWhiteSpace(row.Meta)));
    }

    [Fact]
    public void DetailBuilder_ShowsEveryCharacterEquipmentSlotWithoutInventingEmptySlots()
    {
        var builder = new HistoricalDetailPresentationBuilder(
            new GameDataCatalog([], [], [], [],
            [
                new CurrencyWarsCharacterData(
                    "character-a",
                    "角色甲",
                    "front",
                    [1],
                    false)
            ]));
        var now = DateTimeOffset.Parse("2026-08-01T18:00:00+08:00");
        var evidence = new EvidenceReference(
            "frame-1",
            "frames/1-3.png#front-0",
            CapturedAt: now,
            Confidence: 0.9);
        var snapshot = new RunSnapshot
        {
            RunId = "run-slots",
            AsOf = now,
            Stage = Observation<string>.Known("1-3", 0.9, observedAt: now)
        };
        var state = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            NodeId = Observation<string>.Known("1-3", 0.9, observedAt: now),
            Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            [
                new FormationCharacterState(
                    FormationZone.Front,
                    0,
                    "character-a",
                    2,
                    "front",
                    ["equipment-a"],
                    0.9,
                    evidence)
            ],
            0.9,
            [evidence],
            now)
        };

        var model = builder.Build(new HistoricalNodeDetailEntry(
            "run-slots",
            "1-3",
            snapshot,
            state,
            state,
            null,
            null,
            now));
        var formation = model.Sections.Single(section =>
            section.Title == "历史阵容与角色装备");

        Assert.Contains(formation.Fields, row =>
            row.Label == "前台 1 · 装备槽 1" && row.Value == "equipment-a");
        Assert.Contains(formation.Fields, row =>
            row.Label == "前台 1 · 装备槽 2" &&
            row.Value == "未记录" &&
            row.Meta.Contains("不能把缺失当作空槽"));
        Assert.Contains(formation.Fields, row =>
            row.Label == "前台 1 · 装备槽 3" && row.Value == "未记录");
    }

    [Fact]
    public void DetailBuilder_ProjectsInventoryEvidenceArchiveFilesAndAffixSlots()
    {
        var builder = new HistoricalDetailPresentationBuilder(
            new GameDataCatalog([], [], [], [], []));
        var now = DateTimeOffset.Parse("2026-08-01T18:00:00+08:00");
        var region = new RelativeRegion(0.8, 0.2, 0.05, 0.08);
        var evidence = new EvidenceReference(
            "frame-ui-contract",
            "frames/1-3.png#inventory-0",
            "真实节点裁剪",
            now,
            0.91);
        var snapshot = new RunSnapshot
        {
            RunId = "run-ui-contract",
            AsOf = now,
            Stage = Observation<string>.Known("1-3", 0.95, observedAt: now)
        };
        var state = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            NodeId = Observation<string>.Known("1-3", 0.95, observedAt: now),
            InventorySlots = Observation<IReadOnlyList<InventorySlotState>>.Known(
            [
                new InventorySlotState(
                    0,
                    EquipmentSlotOccupancy.Unknown,
                    InventoryItemKind.AdvancedEquipment,
                    null,
                    ["equipment-candidate"],
                    0.61,
                    region,
                    evidence,
                    "相似图标无法唯一确认",
                    false)
            ],
            0.61,
            [evidence],
            now),
            NegativeAffixIds = new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = ["affix-confirmed"],
                Confidence = 0.5,
                Evidence = [evidence],
                Uncertainty = ["其余槽位未确认"],
                ObservedAt = now
            },
            NamedContent =
            [
                new Phase2NamedContentRecognition(
                    Phase2NamedContentKind.NegativeAffix,
                    "NegativeAffix-3",
                    ObservationStatus.Known,
                    "affix-confirmed",
                    "已确认词条",
                    [],
                    0.91,
                    region,
                    Phase2RecognitionEvidenceKind.Icon,
                    ["affix-confirmed"],
                    [],
                    evidence)
            ],
            PendingIcons =
            [
                new PendingIconObservation(
                    PendingIconCategory.NegativeAffix,
                    "NegativeAffix-1",
                    region,
                    null,
                    0.42,
                    evidence,
                    "Unknown",
                    ["affix-a", "affix-b"],
                    CropFile: "crops/affix-1.png")
            ]
        };

        var model = builder.Build(new HistoricalNodeDetailEntry(
            "run-ui-contract",
            "1-3",
            snapshot,
            state,
            state,
            null,
            null,
            now,
            "analysis/preparation-1-3.json",
            "analysis/final-battle-1-3.json"));
        var rows = model.Sections.SelectMany(section => section.Fields).ToArray();

        Assert.Contains(rows, row =>
            row.Label == "背包槽位 1" &&
            row.Value == "识别失败" &&
            row.Meta == "未识别");
        Assert.Contains(rows, row =>
            row.Label == "备战分析文件" &&
            row.Value == "analysis/preparation-1-3.json");
        Assert.Contains(rows, row =>
            row.Label == "最终战斗文件" &&
            row.Value == "analysis/final-battle-1-3.json");
        Assert.Contains(rows, row =>
            row.Label == "已确认负面词条集合" &&
            row.Value.Contains("affix-confirmed"));
        Assert.Contains(rows, row =>
            row.Label == "负面词条槽位 1" &&
            row.Value == "未记录" &&
            row.Meta == "未识别");
        Assert.Contains(rows, row =>
            row.Label == "负面词条槽位 3" &&
            row.Value.Contains("affix-confirmed"));
        Assert.DoesNotContain(rows, row => row.Label == "负面词条 1");
    }
}
