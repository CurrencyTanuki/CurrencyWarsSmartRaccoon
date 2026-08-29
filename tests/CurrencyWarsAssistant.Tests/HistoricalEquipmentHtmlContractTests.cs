using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.App;

namespace CurrencyWarsAssistant.Tests;

public sealed class HistoricalEquipmentHtmlContractTests
{
    [Fact]
    public async Task CanonicalArchiveRoundTripsCurrentCostIntoSharedRenderer()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"currencywars-cost-report-{Guid.NewGuid():N}");
        const string runId = "run-current-cost-contract";
        var runDirectory = Path.Combine(tempRoot, runId);
        Directory.CreateDirectory(runDirectory);

        try
        {
            var formation = new FormationCharacterState(
                FormationZone.Front,
                0,
                "currency_wars_character_05",
                2,
                "前台",
                [],
                0.90,
                new EvidenceReference("fixture:cost", "front:0"),
                EquipmentSlots:
                [
                    new CharacterEquipmentSlotState(
                        0,
                        EquipmentSlotOccupancy.Equipped,
                        "currency_wars_equipment_105",
                        [
                            "currency_wars_equipment_066",
                            "currency_wars_equipment_105"
                        ],
                        0.90,
                        new RelativeRegion(0.1, 0.1, 0.1, 0.1),
                        new EvidenceReference("fixture:privilege", "front:0:eq:0"),
                        IsPrivileged: true)
                ],
                CurrentCost: 5);
            var archive = new CompletedRunRecord
            {
                RunId = runId,
                CompletedAt = DateTimeOffset.Parse(
                    "2026-08-09T16:00:00+08:00"),
                CompletionPageId = "challenge_success",
                CompletionNodeId = "3-7",
                Nodes =
                [
                    new CompletedRunNodeRecord(
                        "3-7",
                        null,
                        new Phase2OperationalState
                        {
                            Formation = Observation<
                                IReadOnlyList<FormationCharacterState>>.Known(
                                [formation],
                                0.90)
                        },
                        null,
                        null,
                        null)
                ]
            };
            var json = AdvisorJson.Serialize(archive);
            var roundTrip = AdvisorJson.Deserialize<CompletedRunRecord>(json);
            var restored = Assert.Single(Assert.Single(roundTrip.Nodes)
                .FinalPreparationState!.Formation.Value!);
            Assert.Equal(5, restored.CurrentCost);
            Assert.True(Assert.Single(restored.FinalEquipmentSlots).IsPrivileged);

            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "completed-run.v1.json"),
                json);
            var htmlPath = Path.Combine(tempRoot, "current-cost.html");
            var renderedPath = await ReportHtmlRenderer.GenerateFromAsync(
                tempRoot,
                runId,
                htmlPath);

            Assert.Equal(htmlPath, renderedPath);
            Assert.Contains(
                "5费",
                await File.ReadAllTextAsync(htmlPath),
                StringComparison.Ordinal);
            Assert.Contains(
                "特权",
                await File.ReadAllTextAsync(htmlPath),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedArchiveAndRealtimeReportRendersPlayerFacingEquipmentContract(
        bool pascalCaseRealtime)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"currencywars-equipment-report-{Guid.NewGuid():N}");
        var runId = pascalCaseRealtime
            ? "run-equipment-realtime-contract"
            : "run-equipment-archive-contract";
        var runDirectory = Path.Combine(tempRoot, runId);
        Directory.CreateDirectory(runDirectory);

        try
        {
            var archivePath = Path.Combine(runDirectory, "completed-run.v1.json");
            await File.WriteAllTextAsync(
                archivePath,
                CreatePayload(pascalCaseRealtime));

            var htmlPath = Path.Combine(tempRoot, $"{runId}.html");
            var renderedPath = await ReportHtmlRenderer.GenerateFromAsync(
                tempRoot,
                runId,
                htmlPath);

            Assert.Equal(htmlPath, renderedPath);
            var html = await File.ReadAllTextAsync(htmlPath);

            Assert.DoesNotContain("火力风暴潮", html, StringComparison.Ordinal);
            Assert.Contains("privilege-badge", html, StringComparison.Ordinal);
            Assert.Contains("currency_wars_equipment_066.png", html, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "候选：高周波电锯 / 未知装备（待核对）",
                html,
                StringComparison.Ordinal);
            Assert.DoesNotContain("未知装备（待核对）", html, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "equipment_private_missing",
                html,
                StringComparison.Ordinal);

            const string specialEquipment =
                "特殊装备：病毒防火墙（版本不区分）";
            Assert.Equal(0, CountOccurrences(html, specialEquipment));

            Assert.Contains(">后台</span>", html, StringComparison.Ordinal);
            Assert.Contains("special_unit_peipei__", html, StringComparison.Ordinal);
            Assert.DoesNotContain("佩佩", html, StringComparison.Ordinal);
            Assert.Contains("5费", html, StringComparison.Ordinal);
            Assert.Contains("4费", html, StringComparison.Ordinal);
            Assert.Contains("★ 新获得：", html, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "★ 新获得：拆装扳手",
                html,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "★ 新获得：special_item_007",
                html,
                StringComparison.Ordinal);
            Assert.Contains("special_item_007.png", html, StringComparison.Ordinal);
            Assert.True(
                html.IndexOf(">后台</span>", StringComparison.Ordinal) <
                html.IndexOf("special_unit_peipei__", StringComparison.Ordinal));

            // Protect the existing historical summary for both serializers.
            Assert.Contains(">104</span>", html, StringComparison.Ordinal);
            Assert.Contains(">98</span>", html, StringComparison.Ordinal);
            Assert.Contains(">Lv8</span>", html, StringComparison.Ordinal);
            Assert.Contains(">8</span>", html, StringComparison.Ordinal);
            Assert.Contains("12.3", html, StringComparison.Ordinal);
            Assert.Contains(
                "能量 5/7 · 第2档",
                html,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "unknown-formation-unit-front-9",
                html,
                StringComparison.Ordinal);
            Assert.Contains(
                ".unit { display:inline-block;",
                html,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RealtimeReport_DoesNotGuessMultiCostWithoutCurrentCost()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"currencywars-multi-cost-report-{Guid.NewGuid():N}");
        const string runId = "run-multi-cost-contract";
        var runDirectory = Path.Combine(tempRoot, runId);
        Directory.CreateDirectory(runDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "completed-run.v1.json"),
                CreatePayload(
                    pascalCase: false,
                    includeSilverWolfCurrentCost: false));
            var htmlPath = Path.Combine(tempRoot, "multi-cost.html");

            await ReportHtmlRenderer.GenerateFromAsync(
                tempRoot,
                runId,
                htmlPath);
            var html = await File.ReadAllTextAsync(htmlPath);

            Assert.Contains("4费", html, StringComparison.Ordinal);
            Assert.DoesNotContain("3费", html, StringComparison.Ordinal);
            Assert.DoesNotContain("5费", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpecialEquipmentItemChangesRenderRealImagesWithoutVisibleIds(
        bool used)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"currencywars-special-item-report-{Guid.NewGuid():N}");
        var runId = used ? "run-special-item-used" : "run-special-item-gained";
        var runDirectory = Path.Combine(tempRoot, runId);
        Directory.CreateDirectory(runDirectory);

        try
        {
            const string specialItemId = "currency_wars_equipment_153";
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "completed-run.v1.json"),
                CreatePayload(
                    pascalCase: false,
                    specialItemIds: used ? [] : [specialItemId],
                    previousSpecialItemIds: used ? [specialItemId] : null));
            var htmlPath = Path.Combine(tempRoot, $"{runId}.html");

            await ReportHtmlRenderer.GenerateFromAsync(
                tempRoot,
                runId,
                htmlPath);
            var html = await File.ReadAllTextAsync(htmlPath);
            var changeLabel = used ? "✗ 已使用：" : "★ 新获得：";

            Assert.Contains(changeLabel, html, StringComparison.Ordinal);
            Assert.Contains(
                "currency_wars_equipment_icons",
                html,
                StringComparison.Ordinal);
            Assert.Contains(
                "currency_wars_equipment_153.png",
                html,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"{changeLabel}{specialItemId}",
                html,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string CreatePayload(
        bool pascalCase,
        bool includeSilverWolfCurrentCost = true,
        IReadOnlyList<string>? specialItemIds = null,
        IReadOnlyList<string>? previousSpecialItemIds = null)
    {
        string Key(string pascal, string camel) => pascalCase ? pascal : camel;

        Dictionary<string, object?> Observation(object value) => new()
        {
            [Key("Status", "status")] = pascalCase ? "Known" : "known",
            [Key("Value", "value")] = value
        };

        Dictionary<string, object?> EquipmentSlot(
            int slotIndex,
            string occupancy,
            string? equipmentId = null,
            IReadOnlyList<string>? candidates = null,
            bool? isPrivileged = null) => new()
        {
            [Key("SlotIndex", "slotIndex")] = slotIndex,
            [Key("Occupancy", "occupancy")] = occupancy,
            [Key("EquipmentId", "equipmentId")] = equipmentId,
            [Key("IsPrivileged", "isPrivileged")] = isPrivileged,
            [Key("CandidateEquipmentIds", "candidateEquipmentIds")] =
                candidates ?? []
        };

        Dictionary<string, object?> SpecialEquipment(
            string occupancy,
            string? equipmentId = null,
            IReadOnlyList<string>? candidates = null) => new()
        {
            [Key("Occupancy", "occupancy")] = occupancy,
            [Key("EquipmentId", "equipmentId")] = equipmentId,
            [Key("CandidateEquipmentIds", "candidateEquipmentIds")] =
                candidates ?? [],
            [Key("CandidateDisplayNames", "candidateDisplayNames")] =
                Array.Empty<string>()
        };

        Dictionary<string, object?> Character(
            string zone,
            string characterId,
            IReadOnlyList<Dictionary<string, object?>>? equipmentSlots = null,
            Dictionary<string, object?>? specialEquipment = null) => new()
        {
            [Key("Zone", "zone")] = pascalCase
                ? char.ToUpperInvariant(zone[0]) + zone[1..]
                : zone,
            [Key("CharacterId", "characterId")] = characterId,
            [Key("StarLevel", "starLevel")] = characterId ==
                "special_unit_peipei" ? null : 2,
            [Key("CurrentCost", "currentCost")] =
                includeSilverWolfCurrentCost && characterId ==
                "currency_wars_character_05" ? 5 : null,
            [Key("EquipmentSlots", "equipmentSlots")] = equipmentSlots ?? [],
            [Key("SpecialEquipment", "specialEquipment")] = specialEquipment
        };

        var formation = new object[]
        {
            Character(
                "front",
                "currency_wars_character_05",
                [
                    EquipmentSlot(
                        0,
                        pascalCase ? "Equipped" : "equipped",
                        "currency_wars_equipment_066",
                        isPrivileged: true),
                    EquipmentSlot(
                        1,
                        pascalCase ? "Unknown" : "unknown",
                        candidates:
                        [
                            "currency_wars_equipment_083",
                            "equipment_private_missing"
                        ]),
                    EquipmentSlot(2, pascalCase ? "Empty" : "empty")
                ],
                SpecialEquipment(
                    pascalCase ? "Unknown" : "unknown",
                    candidates:
                    [
                        "currency_wars_equipment_034",
                        "currency_wars_equipment_035"
                    ])),
            Character(
                "front",
                "currency_wars_character_09",
                specialEquipment: SpecialEquipment(
                    pascalCase ? "Equipped" : "equipped",
                    "currency_wars_equipment_034")),
            Character("front", "unknown-formation-unit-front-9"),
            // 用户方案（2026-08-18）：佩佩/狸猫只可能出现在后台。zone 用 Back
            // 表示合法后台佩佩（应渲染图标到后台区）；zone=Special 只会是横向
            // 误判(阿哈/命途位)成员，不再当作角色渲染。
            Character("back", "special_unit_peipei")
        };

        var snapshot = new Dictionary<string, object?>
        {
            [Key("Economy", "economy")] = Observation(104),
            [Key("Health", "health")] = Observation(98),
            [Key("StoreLevel", "storeLevel")] = Observation(8),
            [Key("EquipmentIds", "equipmentIds")] = Observation(
                new[] { "currency_wars_equipment_066" }),
            [Key("SpecialItemIds", "specialItemIds")] = Observation(
                specialItemIds ?? ["special_item_007"])
        };
        var state = new Dictionary<string, object?>
        {
            [Key("Population", "population")] = Observation(8),
            [Key("Formation", "formation")] = Observation(formation),
            [Key("ActiveSynergies", "activeSynergies")] = Observation(
                new object[]
                {
                    new Dictionary<string, object?>
                    {
                        [Key("SynergyId", "synergyId")] = "bond_能量",
                        [Key("ActiveCount", "activeCount")] = 5,
                        [Key("NextThreshold", "nextThreshold")] = 7
                    }
                })
        };
        var battle = new Dictionary<string, object?>
        {
            [Key("TotalDamage", "totalDamage")] = 123_456
        };
        var node = new Dictionary<string, object?>
        {
            [Key("NodeId", "nodeId")] = "3-7",
            [Key("FinalPreparationSnapshot", "finalPreparationSnapshot")] = snapshot,
            [Key("FinalPreparationState", "finalPreparationState")] = state,
            [Key("FinalBattle", "finalBattle")] = battle
        };
        var nodes = new List<Dictionary<string, object?>>();
        if (previousSpecialItemIds is not null)
        {
            var previousSnapshot = new Dictionary<string, object?>(snapshot)
            {
                [Key("SpecialItemIds", "specialItemIds")] = Observation(
                    previousSpecialItemIds)
            };
            nodes.Add(new Dictionary<string, object?>
            {
                [Key("NodeId", "nodeId")] = "3-6",
                [Key("FinalPreparationSnapshot", "finalPreparationSnapshot")] =
                    previousSnapshot,
                [Key("FinalPreparationState", "finalPreparationState")] = state,
                [Key("FinalBattle", "finalBattle")] = battle
            });
        }
        nodes.Add(node);
        var payload = new Dictionary<string, object?>
        {
            [Key("SchemaVersion", "schemaVersion")] = "1.0.0",
            [Key("RunId", "runId")] = "run-equipment-contract",
            [Key("CompletedAt", "completedAt")] = "2026-08-09T16:00:00+08:00",
            [Key("CompletionNodeId", "completionNodeId")] = "3-7",
            [Key("Nodes", "nodes")] = nodes
        };

        return JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += needle.Length;
        }

        return count;
    }
}
