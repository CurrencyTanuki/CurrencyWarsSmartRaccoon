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
/// 「识别层 → 决策层」接线器（<see cref="FateGrailSnapshotAssembler"/>）单测。
/// 覆盖用户 2026-08-25 定义：5 费本体在池 = 前台/后台/备战席任一 5 费角色（1 星即可）。
/// </summary>
public sealed class FateGrailSnapshotAssemblerTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly GameDataCatalog GameData = GameDataCatalogLoader.Load(
        Path.Combine(RepositoryRoot, "data", "4.4"));

    private static readonly EvidenceReference Evidence = new(
        "fixture:assembler",
        "assembler-test",
        "trusted fixture",
        DateTimeOffset.UtcNow,
        0.95);

    private static Phase2OperationalState StateWithFormation(
        params (FormationZone Zone, string CharacterId)[] slots) =>
        new()
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            Health = Observation<int>.Known(94, 0.95),
            InvestmentEnvironmentId = Observation<string>.Known(
                FateGrailRunEngine.EnvironmentHeroArrival, 0.95),
            InvestmentStrategyIds = Observation<IReadOnlyList<string>>.Known(
                [InvestmentStrategyPicker.PurchaseSpecialistColor], 0.95),
            Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                slots.Select((slot, index) => new FormationCharacterState(
                    slot.Zone,
                    index,
                    slot.CharacterId,
                    StarLevel: 1,
                    Standing: "front",
                    EquipmentIds: [],
                    Confidence: 0.95,
                    Evidence)).ToArray(),
                0.95,
                evidence: [Evidence]),
        };

    [Fact]
    public void FrontFiveCost_IsBodyInPool()
    {
        // 前台有 Archer（5费）-> 本体在池，FiveCostBodyIds 含 Archer。
        var state = StateWithFormation(
            (FormationZone.Front, "currency_wars_character_14")); // Archer
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne);
        Assert.True(snapshot.HasBody5Cost);
        Assert.Contains("Archer", snapshot.FiveCostBodyIds!);
    }

    [Fact]
    public void BenchFiveCost_IsBodyInPool()
    {
        // 用户 2026-08-25：备战席 5 费也算本体在池。昔涟在备战席 -> 含昔涟。
        var state = StateWithFormation(
            (FormationZone.Bench, "currency_wars_character_19")); // 昔涟
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.All);
        Assert.True(snapshot.HasBody5Cost);
        Assert.Contains("昔涟", snapshot.FiveCostBodyIds!);
    }

    [Fact]
    public void BackFiveCost_IsBodyInPool()
    {
        var state = StateWithFormation(
            (FormationZone.Back, "currency_wars_character_12")); // 布洛妮娅
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne);
        Assert.True(snapshot.HasBody5Cost);
        Assert.Contains("布洛妮娅", snapshot.FiveCostBodyIds!);
    }

    [Fact]
    public void NoFiveCost_NotBodyInPool()
    {
        // 只有 1/2/3 费圣杯成员 -> 无 5 费本体。
        var state = StateWithFormation(
            (FormationZone.Front, "currency_wars_character_04"),  // 远坂凛 1费
            (FormationZone.Front, "currency_wars_character_03"),  // 吉尔伽美什 2费
            (FormationZone.Bench, "currency_wars_character_33")); // Saber 3费
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne);
        Assert.False(snapshot.HasBody5Cost);
        Assert.Empty(snapshot.FiveCostBodyIds!);
    }

    [Fact]
    public void EmptyFormation_NotBodyInPool()
    {
        var state = StateWithFormation();
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne);
        Assert.False(snapshot.HasBody5Cost);
        Assert.Empty(snapshot.FiveCostBodyIds!);
    }

    [Fact]
    public void OwnedMembers_CollectsAllZones()
    {
        // 阵容角色（不分费用）都应进 OwnedMembers，供决策层查缺商店成员。
        var state = StateWithFormation(
            (FormationZone.Front, "currency_wars_character_04"), // 远坂凛
            (FormationZone.Bench, "currency_wars_character_03"), // 吉尔伽美什
            (FormationZone.Back, "currency_wars_character_33")); // Saber
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne);
        Assert.Contains("远坂凛", snapshot.OwnedMembers);
        Assert.Contains("吉尔伽美什", snapshot.OwnedMembers);
        Assert.Contains("Saber", snapshot.OwnedMembers);
    }

    [Fact]
    public void UnknownHealth_UsesZero()
    {
        // 血量未识别时取 0（决策层按 <88 走补血/重刷路径，不臆造血量）。
        var state = StateWithFormation(
            (FormationZone.Front, "currency_wars_character_14")) with
        {
            Health = Observation<int>.Unknown("health 尚未识别", []),
        };
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne);
        Assert.Equal(0, snapshot.Hp);
    }

    [Fact]
    public void XilianAndArcher_CollectsBoth()
    {
        // 前台 Archer + 备战席昔涟 -> 两个名字都进集合（决策层昔涟分叉依赖）。
        var state = StateWithFormation(
            (FormationZone.Front, "currency_wars_character_14"), // Archer
            (FormationZone.Bench, "currency_wars_character_19"));// 昔涟
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.All);
        Assert.Equal(2, snapshot.FiveCostBodyIds!.Count);
        Assert.Contains("Archer", snapshot.FiveCostBodyIds!);
        Assert.Contains("昔涟", snapshot.FiveCostBodyIds!);
    }
    // ---------- BondTier 档位推导（用户 2026-08-25：圣杯成员收集数=羁绊等级） ----------
    [Fact]
    public void BondTier_EqualsCollectedHolyGrailMembers()
    {
        // 凋+闪+Saber（3 名圣杯成员）+无星徽 -> BondTier=3。
        var state = StateWithFormation(
            (FormationZone.Front, "currency_wars_character_04"), // 远坡凛
            (FormationZone.Front, "currency_wars_character_03"), // 吉尔伊美什
            (FormationZone.Bench, "currency_wars_character_33"));// Saber
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne);
        Assert.Equal(3, snapshot.BondTier);
    }

    [Fact]
    public void BondTier_StarBadgeAddsOne()
    {
        // 凋+闪+Saber+Archer（4 名成员）+ 星徽 -> BondTier=5（机会用尽档）。
        var state = StateWithFormation(
            (FormationZone.Front, "currency_wars_character_04"), // 远坡凛
            (FormationZone.Front, "currency_wars_character_03"), // 吉尔伊美什
            (FormationZone.Bench, "currency_wars_character_33"), // Saber
            (FormationZone.Bench, "currency_wars_character_14"));// Archer
        var snapshot = FateGrailSnapshotAssembler.Assemble(
            state, GameData, FateGrailRunEngine.UserGoal.AnyOne,
            hasStarBadge: true);
        Assert.Equal(5, snapshot.BondTier);
    }

}
