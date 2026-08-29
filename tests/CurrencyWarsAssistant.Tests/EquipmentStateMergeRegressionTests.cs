using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class EquipmentStateMergeRegressionTests
{
    [Fact]
    public void InventoryMerge_PreservesBestFactPerSlotAndRoundTripsSnapshot()
    {
        var previous = Observation<IReadOnlyList<InventorySlotState>>.Known(
            [
                InventorySlot(0, EquipmentSlotOccupancy.Equipped,
                    InventoryItemKind.SimpleEquipment, "equipment-a", 0.95),
                InventorySlot(1, EquipmentSlotOccupancy.Empty,
                    InventoryItemKind.Unknown, null, 0.92)
            ],
            0.94);
        var current = new Observation<IReadOnlyList<InventorySlotState>>
        {
            Status = ObservationStatus.Unknown,
            Value =
            [
                InventorySlot(0, EquipmentSlotOccupancy.Unknown,
                    InventoryItemKind.SimpleEquipment, null, 0.99,
                    ["equipment-b"]),
                InventorySlot(1, EquipmentSlotOccupancy.Equipped,
                    InventoryItemKind.AdvancedEquipment, "equipment-c", 0.98)
            ],
            Uncertainty = ["partial inventory"]
        };

        var merged = RunCheckpointFactory.MergeInventoryObservations(
            previous,
            current);

        Assert.Equal(ObservationStatus.Known, merged.Status);
        Assert.Equal("equipment-a", merged.Value![0].ItemId);
        Assert.Equal("equipment-c", merged.Value[1].ItemId);

        var snapshot = new RunSnapshot
        {
            RunId = "run-inventory-roundtrip",
            AsOf = DateTimeOffset.Parse("2026-08-02T01:00:00+08:00"),
            InventorySlots = merged
        };
        var restored = AdvisorJson.Deserialize<RunSnapshot>(
            AdvisorJson.Serialize(snapshot));
        Assert.Equal(
            ["equipment-a", "equipment-c"],
            restored.InventorySlots.Value!.Select(item => item.ItemId));

        const string legacyJson =
            "{\"schemaVersion\":\"1.0.0\",\"runId\":\"legacy\"," +
            "\"asOf\":\"2026-08-02T01:00:00+08:00\"}";
        var legacy = AdvisorJson.Deserialize<RunSnapshot>(legacyJson);
        Assert.Equal(ObservationStatus.Unknown, legacy.InventorySlots.Status);
    }

    [Fact]
    public void FormationMerge_UsesZoneAndCharacterSlotAndKeepsBestEquipmentSlotFacts()
    {
        var previous = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            [
                Character(
                    FormationZone.Front,
                    0,
                    "character-a",
                    0.95,
                    true,
                    [
                        Slot(
                            0,
                            EquipmentSlotOccupancy.Equipped,
                            "equipment-a",
                            0.95,
                            isPrivileged: true),
                        Slot(1, EquipmentSlotOccupancy.Empty, null, 0.90),
                        Slot(2, EquipmentSlotOccupancy.Empty, null, 0.95)
                    ]),
                Character(
                    FormationZone.Bench,
                    2,
                    "unknown-formation-unit-1",
                    0.40,
                    false,
                    [Slot(0, EquipmentSlotOccupancy.Unknown, null, 0.40, ["equipment-z"])],
                    "unknown-formation-unit-1")
            ],
            0.95);
        var current = PartialFormation(
            [
                Character(
                    FormationZone.Front,
                    0,
                    "unknown-formation-unit-2",
                    0.80,
                    false,
                    [
                        Slot(0, EquipmentSlotOccupancy.Equipped, "equipment-b", 0.30),
                        Slot(1, EquipmentSlotOccupancy.Unknown, null, 0.99, ["equipment-q"]),
                        Slot(2, EquipmentSlotOccupancy.Equipped, "equipment-c", 0.98)
                    ],
                    "unknown-formation-unit-2"),
                Character(
                    FormationZone.Back,
                    1,
                    "unknown-formation-unit-3",
                    0.45,
                    false,
                    [Slot(0, EquipmentSlotOccupancy.Occluded, null, 0.45)],
                    "unknown-formation-unit-3")
            ]);

        var merged = RunCheckpointFactory.MergeFormationObservations(
            previous,
            current);

        Assert.Equal(ObservationStatus.Known, merged.Status);
        Assert.Equal(3, merged.Value!.Count);
        var front = Assert.Single(merged.Value, item =>
            item.Zone == FormationZone.Front && item.SlotIndex == 0);
        Assert.Equal("character-a", front.CharacterId);
        Assert.True(front.CanDriveDecisions);
        Assert.Equal(["equipment-c"], front.EquipmentIds);
        var conflicted = Assert.Single(
            front.FinalEquipmentSlots,
            item => item.SlotIndex == 0);
        Assert.Equal(EquipmentSlotOccupancy.Unknown, conflicted.Occupancy);
        Assert.Null(conflicted.EquipmentId);
        Assert.False(conflicted.CanDriveDecisions);
        Assert.Equal(
            ["equipment-a", "equipment-b"],
            conflicted.CandidateEquipmentIds);
        Assert.True(conflicted.IsPrivileged);
        Assert.Equal(
            EquipmentSlotOccupancy.Empty,
            Assert.Single(front.FinalEquipmentSlots, item => item.SlotIndex == 1)
                .Occupancy);
        Assert.Equal(
            "equipment-c",
            Assert.Single(front.FinalEquipmentSlots, item => item.SlotIndex == 2)
                .EquipmentId);
        Assert.Contains(merged.Value, item =>
            item.Zone == FormationZone.Bench &&
            item.SlotIndex == 2 &&
            !item.CanDriveDecisions);
        Assert.Contains(merged.Value, item =>
            item.Zone == FormationZone.Back &&
            item.SlotIndex == 1 &&
            !item.CanDriveDecisions);
    }

    [Fact]
    public void FormationEquipmentIdentity_RequiresASecondMatchingFrameToSwitch()
    {
        var previous = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            [Character(
                FormationZone.Front,
                0,
                "character-a",
                0.95,
                true,
                [Slot(0, EquipmentSlotOccupancy.Equipped, "equipment-a", 0.95)])],
            0.95);
        var changed = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            [Character(
                FormationZone.Front,
                0,
                "character-a",
                0.95,
                true,
                [Slot(0, EquipmentSlotOccupancy.Equipped, "equipment-b", 0.95)])],
            0.95);

        var first = RunCheckpointFactory.MergeFormationObservations(
            previous,
            changed);
        var firstSlot = Assert.Single(
            Assert.Single(first.Value!).FinalEquipmentSlots);
        Assert.Equal(EquipmentSlotOccupancy.Unknown, firstSlot.Occupancy);
        Assert.Null(firstSlot.EquipmentId);
        Assert.False(firstSlot.CanDriveDecisions);
        Assert.Equal(
            ["equipment-a", "equipment-b"],
            firstSlot.CandidateEquipmentIds);

        var second = RunCheckpointFactory.MergeFormationObservations(
            first,
            changed);
        var confirmedSlot = Assert.Single(
            Assert.Single(second.Value!).FinalEquipmentSlots);
        Assert.Equal(EquipmentSlotOccupancy.Equipped, confirmedSlot.Occupancy);
        Assert.Equal("equipment-b", confirmedSlot.EquipmentId);
        Assert.True(confirmedSlot.CanDriveDecisions);
    }

    [Fact]
    public void FormationMerge_PreservesOneEquipmentOccurrencePerOccupiedSlot()
    {
        var previous = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            [
                Character(
                    FormationZone.Front,
                    0,
                    "character-a",
                    0.90,
                    true,
                    [])
            ],
            0.90);
        var current = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            [
                Character(
                    FormationZone.Front,
                    0,
                    "character-a",
                    0.95,
                    true,
                    [
                        Slot(
                            0,
                            EquipmentSlotOccupancy.Equipped,
                            "equipment-same",
                            0.95),
                        Slot(
                            1,
                            EquipmentSlotOccupancy.Equipped,
                            "equipment-same",
                            0.95)
                    ])
            ],
            0.95);

        var merged = RunCheckpointFactory.MergeFormationObservations(
            previous,
            current);
        var character = Assert.Single(merged.Value!);

        Assert.Equal(
            ["equipment-same", "equipment-same"],
            character.EquipmentIds);
        Assert.Equal(
            ["equipment-same", "equipment-same"],
            character.FinalEquipmentSlots.Select(item => item.EquipmentId));

        var partialNextFrame = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            [
                Character(
                    FormationZone.Front,
                    0,
                    "character-a",
                    0.96,
                    true,
                    [])
            ],
            0.96);
        var retained = RunCheckpointFactory.MergeFormationObservations(
            merged,
            partialNextFrame);
        var retainedCharacter = Assert.Single(retained.Value!);
        Assert.Equal(
            ["equipment-same", "equipment-same"],
            retainedCharacter.EquipmentIds);
    }

    [Fact]
    public void FormationMerge_RetainsReadableSpecialEquipmentWhenNextFrameCannotSeeIt()
    {
        var specialEquipment = new CharacterSpecialEquipmentState(
            EquipmentSlotOccupancy.Unknown,
            null,
            [
                "currency_wars_equipment_034",
                "currency_wars_equipment_035"
            ],
            ["病毒防火墙", "病毒防火墙Max"],
            0.73,
            new RelativeRegion(0.35, 0.30, 0.02, 0.03),
            Evidence("special-equipment", 0.73),
            "视觉相同版本不强行区分",
            false);
        var previousCharacter = Character(
            FormationZone.Front,
            0,
            "currency_wars_character_05",
            0.70,
            true,
            [] ) with
        {
            SpecialEquipment = specialEquipment,
            CurrentCost = 5
        };
        var currentCharacter = Character(
            FormationZone.Front,
            0,
            "currency_wars_character_05",
            0.90,
            true,
            []);

        var merged = RunCheckpointFactory.MergeFormationObservations(
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [previousCharacter],
                0.70),
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [currentCharacter],
                0.90));

        var character = Assert.Single(merged.Value!);
        Assert.NotNull(character.SpecialEquipment);
        Assert.Equal(
            specialEquipment.CandidateEquipmentIds,
            character.SpecialEquipment!.CandidateEquipmentIds);
        Assert.Equal(
            specialEquipment.CandidateDisplayNames,
            character.SpecialEquipment.CandidateDisplayNames);
        Assert.Equal(5, character.CurrentCost);
    }

    [Fact]
    public void FormationMerge_DoesNotCarrySilverWolfCostToAnotherCharacter()
    {
        var previous = Character(
            FormationZone.Front,
            0,
            "currency_wars_character_05",
            0.70,
            true,
            []) with { CurrentCost = 5 };
        var current = Character(
            FormationZone.Front,
            0,
            "currency_wars_character_09",
            0.95,
            true,
            []);

        var merged = RunCheckpointFactory.MergeFormationObservations(
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [previous], 0.70),
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [current], 0.95));

        var character = Assert.Single(merged.Value!);
        Assert.Equal("currency_wars_character_09", character.CharacterId);
        Assert.Null(character.CurrentCost);
    }

    [Fact]
    public void FormationMerge_UsesNewestKnownCostForSameSilverWolfIdentity()
    {
        var previous = Character(
            FormationZone.Front,
            0,
            "currency_wars_character_05",
            0.95,
            true,
            []) with { CurrentCost = 4 };
        var current = Character(
            FormationZone.Front,
            0,
            "currency_wars_character_05",
            0.80,
            true,
            []) with { CurrentCost = 5 };

        var merged = RunCheckpointFactory.MergeFormationObservations(
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [previous], 0.95),
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [current], 0.80));

        var character = Assert.Single(merged.Value!);
        Assert.Equal("currency_wars_character_05", character.CharacterId);
        Assert.Equal(5, character.CurrentCost);
    }

    [Fact]
    public void AtomicIdentityRelocationKeepsEquipmentBundleAndSilverWolfCost()
    {
        var previous = Character(
            FormationZone.Front,
            0,
            "currency_wars_character_05",
            0.95,
            true,
            [
                Slot(
                    0,
                    EquipmentSlotOccupancy.Equipped,
                    "currency_wars_equipment_066",
                    0.95)
            ]) with { CurrentCost = 5 };
        var moved = Character(
            FormationZone.Front,
            1,
            "currency_wars_character_05",
            0.90,
            true,
            []);
        var merged = RunCheckpointFactory.MergeFormationObservations(
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [previous], 0.95),
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [moved], 0.90),
            currentSlotObservations:
            [
                new(
                    FormationZone.Front,
                    0,
                    Phase2FormationSlotOccupancy.Empty),
                new(
                    FormationZone.Front,
                    1,
                    Phase2FormationSlotOccupancy.Recognized)
            ],
            observationsAreAtomic: true,
            observationsArePartial: true);

        var character = Assert.Single(merged.Value!);
        Assert.Multiple(
            () => Assert.Equal(FormationZone.Front, character.Zone),
            () => Assert.Equal(1, character.SlotIndex),
            () => Assert.Equal(
                "currency_wars_character_05",
                character.CharacterId),
            () => Assert.Equal(5, character.CurrentCost),
            () => Assert.Equal(
                ["currency_wars_equipment_066"],
                character.EquipmentIds),
            () => Assert.Single(character.FinalEquipmentSlots));
    }
    [Fact]
    public void CheckpointMerge_PreservesPartialUnknownFormationAndRoundTripsOldRecords()
    {
        var now = DateTimeOffset.Parse("2026-08-02T01:00:00+08:00");
        var initial = RunCheckpointFactory.CreateInitial(
            "run-equipment-merge",
            RunEntryMode.DirectRecording,
            now);
        var known = Analysis(
            initial.RunId,
            now,
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [
                    Character(
                        FormationZone.Front,
                        0,
                        "character-a",
                        0.95,
                        true,
                        [Slot(
                            0,
                            EquipmentSlotOccupancy.Equipped,
                            "equipment-a",
                            0.95)]),
                    Character(
                        FormationZone.Bench,
                        1,
                        "unknown-formation-unit-1",
                        0.40,
                        false,
                        [],
                        "unknown-formation-unit-1")
                ],
                0.95));
        var afterKnown = RunCheckpointFactory.FromAnalysis(
            initial,
            known,
            1,
            RunCheckpointLifecycleStatus.Active,
            now);
        var partial = Analysis(
            initial.RunId,
            now.AddSeconds(3),
            PartialFormation(
            [
                Character(
                    FormationZone.Front,
                    0,
                    "unknown-formation-unit-2",
                    0.40,
                    false,
                    [Slot(0, EquipmentSlotOccupancy.Occluded, null, 0.90)],
                    "unknown-formation-unit-2")
            ]));

        var checkpoint = RunCheckpointFactory.FromAnalysis(
            afterKnown,
            partial,
            2,
            RunCheckpointLifecycleStatus.Active,
            now.AddSeconds(3));
        var restored = AdvisorJson.Deserialize<RunCheckpointRecord>(
            AdvisorJson.Serialize(checkpoint));

        Assert.Equal(
            ObservationStatus.Stale,
            restored.LastOperationalState!.Formation.Status);
        var restoredFormation = restored.LastOperationalState.Formation.Value!;
        var front = Assert.Single(restoredFormation, item =>
            item.Zone == FormationZone.Front);
        Assert.Equal("character-a", front.CharacterId);
        Assert.Equal(
            "equipment-a",
            Assert.Single(front.FinalEquipmentSlots).EquipmentId);
        Assert.Contains(restoredFormation, item =>
            item.Zone == FormationZone.Bench && !item.CanDriveDecisions);

        var legacyCharacter = Character(
            FormationZone.Front,
            0,
            "legacy-character",
            0.9,
            true,
            []) with
        {
            EquipmentIds = ["legacy-equipment"],
            EquipmentSlots = null
        };
        var legacy = checkpoint with
        {
            LastOperationalState = checkpoint.LastOperationalState! with
            {
                Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                    [legacyCharacter],
                    0.9)
            }
        };

        var legacyRestored = AdvisorJson.Deserialize<RunCheckpointRecord>(
            AdvisorJson.Serialize(legacy));

        var restoredLegacyCharacter = Assert.Single(
            legacyRestored.LastOperationalState!.Formation.Value!);
        Assert.Equal(["legacy-equipment"], restoredLegacyCharacter.EquipmentIds);
        Assert.Empty(restoredLegacyCharacter.FinalEquipmentSlots);
    }

    [Fact]
    public void PersistentFingerprint_DetectsSlotCandidateDecisionAndSpecialItemChanges()
    {
        var tracker = new Phase2OperationalStateTracker(confirmationFrames: 2);
        var candidateA = State(
            Slot(
                0,
                EquipmentSlotOccupancy.Unknown,
                null,
                0.40,
                ["equipment-a"],
                false),
            ["special-a"]);

        Assert.False(tracker.Observe(candidateA).PersistentStateConfirmed);
        Assert.True(tracker.Observe(candidateA).PersistentStateConfirmed);

        var candidateB = State(
            Slot(
                0,
                EquipmentSlotOccupancy.Unknown,
                null,
                0.40,
                ["equipment-b"],
                false),
            ["special-a"]);
        Assert.False(tracker.Observe(candidateB).PersistentStateConfirmed);
        Assert.True(tracker.Observe(candidateB).PersistentStateConfirmed);

        var decisionCapable = State(
            Slot(
                0,
                EquipmentSlotOccupancy.Unknown,
                null,
                0.40,
                ["equipment-b"],
                true),
            ["special-a"]);
        Assert.False(tracker.Observe(decisionCapable).PersistentStateConfirmed);
        Assert.True(tracker.Observe(decisionCapable).PersistentStateConfirmed);

        var empty = State(
            Slot(0, EquipmentSlotOccupancy.Empty, null, 0.90),
            ["special-a"]);
        Assert.False(tracker.Observe(empty).PersistentStateConfirmed);
        Assert.True(tracker.Observe(empty).PersistentStateConfirmed);

        var changedSpecialItem = State(
            Slot(0, EquipmentSlotOccupancy.Empty, null, 0.90),
            ["special-b"]);
        Assert.False(tracker.Observe(changedSpecialItem).PersistentStateConfirmed);
        Assert.True(tracker.Observe(changedSpecialItem).PersistentStateConfirmed);
    }

    private static ScreenshotAnalysisResult Analysis(
        string runId,
        DateTimeOffset observedAt,
        Observation<IReadOnlyList<FormationCharacterState>> formation) =>
        new()
        {
            AnalysisId = $"analysis-{observedAt:HHmmss}",
            Snapshot = new RunSnapshot
            {
                RunId = runId,
                AsOf = observedAt,
                PageId = Observation<string>.Known("preparation_generic", 1),
                Stage = Observation<string>.Known("1-3", 1)
            },
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Preparation,
                PageId = "preparation_generic",
                NodeId = Observation<string>.Known("1-3", 1),
                Formation = formation
            }
        };

    private static Phase2OperationalState State(
        CharacterEquipmentSlotState slot,
        IReadOnlyList<string> specialItems) =>
        new()
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            NodeId = Observation<string>.Known("1-3", 1),
            Formation = PartialFormation(
            [
                Character(
                    FormationZone.Front,
                    0,
                    "unknown-formation-unit-1",
                    0.40,
                    false,
                    [slot],
                    "unknown-formation-unit-1")
            ]),
            SpecialItemIds = Observation<IReadOnlyList<string>>.Known(
                specialItems,
                0.9)
        };

    private static Observation<IReadOnlyList<FormationCharacterState>>
        PartialFormation(IReadOnlyList<FormationCharacterState> value) =>
        new()
        {
            Status = ObservationStatus.Unknown,
            Value = value,
            Confidence = 0,
            Uncertainty = ["partial formation"]
        };

    private static FormationCharacterState Character(
        FormationZone zone,
        int slotIndex,
        string characterId,
        double confidence,
        bool canDriveDecisions,
        IReadOnlyList<CharacterEquipmentSlotState> slots,
        string? temporaryId = null) =>
        new(
            zone,
            slotIndex,
            characterId,
            null,
            canDriveDecisions ? "front" : "unknown",
            slots
                .Where(item =>
                    item.Occupancy == EquipmentSlotOccupancy.Equipped &&
                    item.EquipmentId is not null)
                .Select(item => item.EquipmentId!)
                .ToArray(),
            confidence,
            Evidence($"character-{zone}-{slotIndex}", confidence),
            temporaryId,
            canDriveDecisions ? [] : [characterId],
            canDriveDecisions ? null : "identity unresolved",
            canDriveDecisions,
            new RelativeRegion(0.1, 0.1, 0.1, 0.1),
            slots);

    private static CharacterEquipmentSlotState Slot(
        int slotIndex,
        EquipmentSlotOccupancy occupancy,
        string? equipmentId,
        double confidence,
        IReadOnlyList<string>? candidates = null,
        bool canDriveDecisions = true,
        bool? isPrivileged = null) =>
        new(
            slotIndex,
            occupancy,
            equipmentId,
            candidates ?? [],
            confidence,
            new RelativeRegion(0.2 + (slotIndex * 0.02), 0.2, 0.02, 0.02),
            Evidence($"equipment-{slotIndex}", confidence),
            occupancy is EquipmentSlotOccupancy.Unknown or
                EquipmentSlotOccupancy.Occluded
                ? "slot unresolved"
                : null,
            canDriveDecisions,
            isPrivileged);

    private static InventorySlotState InventorySlot(
        int slotIndex,
        EquipmentSlotOccupancy occupancy,
        InventoryItemKind kind,
        string? itemId,
        double confidence,
        IReadOnlyList<string>? candidates = null) =>
        new(
            slotIndex,
            occupancy,
            kind,
            itemId,
            candidates ?? [],
            confidence,
            new RelativeRegion(0.9, 0.1 + (slotIndex * 0.08), 0.05, 0.05),
            Evidence($"inventory-{slotIndex}", confidence),
            occupancy is EquipmentSlotOccupancy.Unknown or
                EquipmentSlotOccupancy.Occluded
                ? "slot unresolved"
                : null,
            occupancy is EquipmentSlotOccupancy.Empty or
                EquipmentSlotOccupancy.Equipped);

    private static EvidenceReference Evidence(string id, double confidence) =>
        new(
            $"fixture:{id}",
            $"vision:{id}",
            CapturedAt: DateTimeOffset.Parse("2026-08-02T01:00:00+08:00"),
            Confidence: confidence);
}
