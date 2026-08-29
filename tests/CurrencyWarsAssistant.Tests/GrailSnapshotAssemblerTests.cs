using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 2a 适配层测试：GrailRunStateHolder（编排状态+last-known缓存）与
/// GrailSnapshotAssembler（识别产出 → GrailRunSnapshot）。
/// </summary>
public sealed class GrailSnapshotAssemblerTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly GameDataCatalog GameData = GameDataCatalogLoader.Load(
        Path.Combine(RepositoryRoot, "data", "4.4"));

    private static readonly DateTimeOffset Now = new(2026, 8, 29, 15, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private static readonly EvidenceReference Evidence = new(
        "fixture:grail-assembler",
        "grail-assembler-test",
        "trusted fixture",
        DateTimeOffset.UtcNow,
        0.95);

    private static FormationCharacterState Slot(
        FormationZone zone,
        string characterId,
        IReadOnlyList<CharacterEquipmentSlotState>? equipmentSlots = null) =>
        new(
            zone,
            0,
            characterId,
            StarLevel: 1,
            Standing: "front",
            EquipmentIds: [],
            Confidence: 0.95,
            Evidence,
            EquipmentSlots: equipmentSlots);

    private static CharacterEquipmentSlotState StarBadgeSlot() => new(
        0,
        EquipmentSlotOccupancy.Equipped,
        GrailSnapshotAssembler.StarBadgeEquipmentId,
        [],
        0.9,
        new RelativeRegion(0, 0, 0.1, 0.1),
        Evidence);

    private static Phase2OperationalState State(
        Observation<int>? health = null,
        Observation<int>? population = null,
        IReadOnlyList<FormationCharacterState>? formation = null,
        IReadOnlyList<InventorySlotState>? inventory = null) =>
        new()
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            Health = health ?? Observation<int>.Unknown("not observed"),
            Population = population ?? Observation<int>.Unknown("not observed"),
            Formation = formation is null
                ? Observation<IReadOnlyList<FormationCharacterState>>.Unknown("not observed")
                : Observation<IReadOnlyList<FormationCharacterState>>.Known(formation, 0.95, evidence: [Evidence]),
            InventorySlots = inventory is null
                ? Observation<IReadOnlyList<InventorySlotState>>.Unknown("not observed")
                : Observation<IReadOnlyList<InventorySlotState>>.Known(inventory, 0.9, evidence: [Evidence]),
        };

    private static RunSnapshot EconomyOnly(int gold, IReadOnlyList<string>? shopIds = null) => new()
    {
        RunId = "grail-assembler-test-run",
        AsOf = Now,
        Economy = Observation<int>.Known(gold, 0.9),
        ShopCharacterIds = shopIds is null
            ? Observation<IReadOnlyList<string>>.Unknown("not observed")
            : Observation<IReadOnlyList<string>>.Known(shopIds, 0.9),
    };

    private static InventorySlotState BadgeInventorySlot(int index) => new(
        index,
        EquipmentSlotOccupancy.Equipped,
        InventoryItemKind.AdvancedEquipment,
        GrailSnapshotAssembler.StarBadgeEquipmentId,
        [],
        0.9,
        new RelativeRegion(0, 0, 0.05, 0.05),
        Evidence);

    // ---------- 组装器：羁绊成员只计上场（前台/后台），5费/昔涟含备战席 ----------

    [Fact]
    public void BondMembers_CountDeployedOnly_BenchExcluded()
    {
        var holder = new GrailRunStateHolder();
        var state = State(formation:
        [
            Slot(FormationZone.Front, "currency_wars_character_04"),   // 远坂凛
            Slot(FormationZone.Back, "currency_wars_character_03"),    // 吉尔伽美什
            Slot(FormationZone.Bench, "currency_wars_character_33"),   // Saber（备战席不计档位）
        ]);
        var snapshot = GrailSnapshotAssembler.Assemble(
            state, null, GameData, holder, GrailUserGoal.Single, Now, StaleAfter);

        Assert.Equal(2, snapshot.DeployedBondMembers.Count);
        Assert.Contains("远坂凛", snapshot.DeployedBondMembers);
        Assert.DoesNotContain("Saber", snapshot.DeployedBondMembers);
        // 但 Saber 已拥有（OwnedCharacterNames 含全部识别到的角色）
        Assert.Contains("Saber", snapshot.OwnedCharacterNames);
    }

    [Fact]
    public void FiveCostAndXilian_CountBenchToo()
    {
        var holder = new GrailRunStateHolder();
        var state = State(formation:
        [
            Slot(FormationZone.Bench, "currency_wars_character_19"),   // 昔涟（5费）
            Slot(FormationZone.Bench, "currency_wars_character_20"),   // 瓦尔特（5费，067附赠情形）
        ]);
        var snapshot = GrailSnapshotAssembler.Assemble(
            state, null, GameData, holder, GrailUserGoal.All, Now, StaleAfter);

        Assert.True(snapshot.HasFiveCostBody);
        Assert.True(snapshot.XilianOnField);
        Assert.Empty(snapshot.DeployedBondMembers);
    }

    [Fact]
    public void BadgeCarriers_CountedOnlyForDeployedNonMembers()
    {
        var holder = new GrailRunStateHolder();
        var state = State(formation:
        [
            Slot(FormationZone.Front, "currency_wars_character_04", [StarBadgeSlot()]),  // 凛带徽：成员本身，不另计
            Slot(FormationZone.Back, "currency_wars_character_59", [StarBadgeSlot()]),   // 万狼带徽：非成员携带者 +1
        ]);
        var snapshot = GrailSnapshotAssembler.Assemble(
            state, null, GameData, holder, GrailUserGoal.Single, Now, StaleAfter);

        Assert.Equal(1, snapshot.BadgeCarrierNonMembers);
        Assert.Equal(2, snapshot.TotalStarBadgesObtained); // 2 枚都在场上角色身上
        Assert.Equal(0, snapshot.UncarriedStarBadges);
    }

    [Fact]
    public void InventoryBadges_CountAsUncarried_AndTriggerMemberAvailable()
    {
        var holder = new GrailRunStateHolder();
        var state = State(formation: [Slot(FormationZone.Front, "currency_wars_character_04")],
            inventory: [BadgeInventorySlot(0), BadgeInventorySlot(1)]);
        var snapshot = GrailSnapshotAssembler.Assemble(
            state, null, GameData, holder, GrailUserGoal.Single, Now, StaleAfter);

        Assert.Equal(2, snapshot.UncarriedStarBadges);
        Assert.Equal(2, snapshot.TotalStarBadgesObtained);
        Assert.True(snapshot.NewBondMemberAvailable);
    }

    [Fact]
    public void SellablePool_RespectsKeepLine()
    {
        var holder = new GrailRunStateHolder();
        var state = State(formation:
        [
            Slot(FormationZone.Front, "currency_wars_character_04"),
            // 备战席 3 个非命杯非5费角色
            Slot(FormationZone.Bench, "currency_wars_character_59"),
            Slot(FormationZone.Bench, "currency_wars_character_38"),
            Slot(FormationZone.Bench, "currency_wars_character_43"),
        ]);
        var noBadges = GrailSnapshotAssembler.Assemble(
            state, null, GameData, holder, GrailUserGoal.Single, Now, StaleAfter);
        Assert.Equal(3, noBadges.SellableBeyondKeepLineCount); // 0 徽全卖

        var holder2 = new GrailRunStateHolder();
        var withTwoBadges = GrailSnapshotAssembler.Assemble(
            State(formation:
            [
                Slot(FormationZone.Front, "currency_wars_character_04"),
                Slot(FormationZone.Bench, "currency_wars_character_59"),
                Slot(FormationZone.Bench, "currency_wars_character_38"),
                Slot(FormationZone.Bench, "currency_wars_character_43"),
            ], inventory: [BadgeInventorySlot(0), BadgeInventorySlot(1)]),
            null, GameData, holder2, GrailUserGoal.Single, Now, StaleAfter);
        Assert.Equal(1, withTwoBadges.SellableBeyondKeepLineCount); // 2 徽留 2，卖 1

        // 5 费在备战席绝不入可卖池
        var holder3 = new GrailRunStateHolder();
        var withFiveCost = GrailSnapshotAssembler.Assemble(
            State(formation:
            [
                Slot(FormationZone.Bench, "currency_wars_character_19"),
                Slot(FormationZone.Bench, "currency_wars_character_59"),
            ]),
            null, GameData, holder3, GrailUserGoal.All, Now, StaleAfter);
        Assert.Equal(1, withFiveCost.SellableBeyondKeepLineCount); // 昔涟受保护，只剩万敌
    }

    // ---------- 组装器：血量/人口/金币的 Known→缓存→陈旧度链 ----------

    [Fact]
    public void Health_Known_CapturedThenStales()
    {
        var holder = new GrailRunStateHolder();
        var fresh = GrailSnapshotAssembler.Assemble(
            State(health: Observation<int>.Known(95, 0.95), population: Observation<int>.Known(4, 0.95)),
            EconomyOnly(30), GameData, holder, GrailUserGoal.Single, Now, StaleAfter);
        Assert.Equal(95, fresh.TeamHealth);
        Assert.Equal(4, fresh.Population);
        Assert.Equal(30, fresh.Gold);

        // 弹框期：当帧全 Unknown → 取缓存
        var duringDialog = GrailSnapshotAssembler.Assemble(
            State(), null, GameData, holder, GrailUserGoal.Single, Now.AddSeconds(3), StaleAfter);
        Assert.Equal(95, duringDialog.TeamHealth);
        Assert.Equal(30, duringDialog.Gold);

        // 超龄 → 按未识别（null/默认），决策器走防御口径
        var stale = GrailSnapshotAssembler.Assemble(
            State(), null, GameData, holder, GrailUserGoal.Single, Now.AddSeconds(30), StaleAfter);
        Assert.Null(stale.TeamHealth);
        Assert.Equal(GrailRunSnapshot.BasePopulation, stale.Population);
        Assert.Equal(0, stale.Gold);
    }

    [Fact]
    public void Health_OutOfTrackerRange_Ignored()
    {
        var holder = new GrailRunStateHolder();
        var snapshot = GrailSnapshotAssembler.Assemble(
            State(health: Observation<int>.Known(150, 0.95)),
            null, GameData, holder, GrailUserGoal.Single, Now, StaleAfter);
        Assert.Null(snapshot.TeamHealth); // tracker 只认 0..100，噪声不进缓存
    }

    // ---------- 组装器：商店 sighting 与 釜自带 5 费 ----------

    [Fact]
    public void ShopSighting_UnownedBondMember_MarksMemberAvailable()
    {
        var holder = new GrailRunStateHolder();
        var snapshot = GrailSnapshotAssembler.Assemble(
            State(formation: [Slot(FormationZone.Front, "currency_wars_character_04")]),
            EconomyOnly(20, shopIds: ["currency_wars_character_33"]), // 商店出现 Saber（未拥有）
            GameData, holder, GrailUserGoal.Single, Now, StaleAfter);

        Assert.True(snapshot.NewBondMemberAvailable);
    }

    [Fact]
    public void CauldronSelected_ImpliesFiveCostBody_EvenWithoutRecognition()
    {
        var holder = new GrailRunStateHolder();
        var response = new GrailTrialResponse(
            GrailTrialResponseKind.SelectCauldronContinue,
            GrailTrialSide.Left,
            "无限之釜",
            OpenLettersAfter: false,
            "test",
            "F8→F17");
        holder.ApplyTrialResponse(response, healthAtSelection: null);

        var snapshot = GrailSnapshotAssembler.Assemble(
            State(formation: [Slot(FormationZone.Front, "currency_wars_character_04")]),
            null, GameData, holder, GrailUserGoal.All, Now, StaleAfter);

        Assert.True(snapshot.InfiniteCauldronSelected);
        Assert.True(snapshot.HasFiveCostBody); // 釜=全三星圣杯角色，选择即产生 5 费本体
        Assert.Equal(1, snapshot.WishesResponded);
    }

    // ---------- 状态持有器 ----------

    [Fact]
    public void Holder_TrialResponseLifecycle()
    {
        var holder = new GrailRunStateHolder();
        var miracle = new GrailTrialResponse(
            GrailTrialResponseKind.SelectMiracleCompensation,
            GrailTrialSide.Left,
            "令咒决议·奇迹代偿",
            OpenLettersAfter: false,
            "test",
            "F14");
        holder.ApplyTrialResponse(miracle, healthAtSelection: 92);

        var letterTrial = new GrailTrialResponse(
            GrailTrialResponseKind.SelectLetterTrial,
            GrailTrialSide.Right,
            "令咒决议·回路过载",
            OpenLettersAfter: true,
            "test",
            "F11");
        holder.ApplyTrialResponse(letterTrial, healthAtSelection: 90);

        var (wishes, obtained, opened, miracleSelected, miracleHealth, cauldron, givenUp, newMember) =
            holder.PeekEventState();
        Assert.Equal(2, wishes);
        Assert.Equal(2, obtained);
        Assert.Equal(0, opened);
        Assert.True(miracleSelected);
        Assert.Equal(92, miracleHealth); // 选择时血量取证
        Assert.False(cauldron);
        Assert.False(givenUp);
        Assert.False(newMember);

        holder.MarkLetterOpened();
        holder.MarkLetterOpened();
        (_, _, opened, _, _, _, _, _) = holder.PeekEventState();
        Assert.Equal(2, opened);
    }

    [Fact]
    public void Holder_FiveBondLatch_IsPermanent()
    {
        var holder = new GrailRunStateHolder();
        holder.GiveUpFiveBond();
        Assert.True(holder.PeekEventState().FiveBondGivenUp);
    }
}
