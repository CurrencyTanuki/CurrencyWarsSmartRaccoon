using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 回归测试（2026-08-05 会话 T，步骤 1）：
/// 商店弹窗页（reward_shop）用紧凑布局坐标（RewardShopCharacterSlots1920，
/// front≈(0.380,0.383)）识别阵容；备战页用标准坐标（PreparationCharacterSlots1920，
/// front≈(0.355,0.305)）。此前 tracker 按 (Zone, SlotIndex) 把商店帧 formation 与
/// 备战帧合并，紧凑坐标槽位混入备战数据 → 阵容/装备永远 Unknown（实测
/// run-20260805-023158：1-3+ 备战快照 boardCharacterIds 全 unknown，帧证据
/// 183236 备战帧 formation 槽位坐标仍为商店坐标 (730,414)）。
/// 修复：商店帧不参与 formation 合并（保留备战最后可靠值或置空 Unknown）。
/// </summary>
public sealed class Phase2FormationRewardShopIsolationTests
{
    [Fact]
    public void RewardShopCompactSlotsDoNotPolluteSubsequentPreparationFormation()
    {
        var tracker = new Phase2OperationalStateTracker();

        // 商店帧：紧凑坐标槽位（front slot0 在 (0.380,0.383)），整体 Unknown
        tracker.Observe(State(
            "reward_shop",
            Phase2PageFamily.Supply,
            UnknownFormation(
                [Slot(FormationZone.Front, 0, "unknown-formation-unit-Front-1", 0.380, 0.383, 0.2)])));

        // 备战帧：标准坐标槽位（front slot0 在 (0.355,0.305)，back 在 (0.297,0.556)），
        // 整体 Unknown（个别槽位不确定，模拟实机）
        var afterPrep = tracker.Observe(State(
            "preparation_generic",
            Phase2PageFamily.Preparation,
            UnknownFormation(
                [
                    Slot(FormationZone.Front, 0, "currency_wars_character_04", 0.355, 0.305, 0.62),
                    Slot(FormationZone.Back, 6, "currency_wars_character_02", 0.297, 0.556, 0.70)
                ]))).Current;

        var formation = afterPrep.Formation.Value ?? [];
        var shopCoordinateSlots = formation
            .Where(item => (item.CardRegion?.X ?? -1) is >= 0.370 and <= 0.395 &&
                           (item.CardRegion?.Y ?? -1) is >= 0.360 and <= 0.410)
            .ToArray();
        Assert.Empty(shopCoordinateSlots);

        // 备战坐标槽位应保留
        Assert.Contains(
            formation,
            item => (item.CardRegion?.X ?? -1) is >= 0.34 and <= 0.37 &&
                    (item.CardRegion?.Y ?? -1) is >= 0.29 and <= 0.32);
        Assert.Contains(
            formation,
            item => (item.CardRegion?.X ?? -1) is >= 0.28 and <= 0.31 &&
                    (item.CardRegion?.Y ?? -1) is >= 0.54 and <= 0.57);
    }

    [Fact]
    public void RewardShopFrameKeepsPreviousPreparationFormationAsStale()
    {
        var tracker = new Phase2OperationalStateTracker();

        // 先有备战帧（标准坐标 Known）——连续 2 帧确认
        tracker.Observe(State(
            "preparation_generic",
            Phase2PageFamily.Preparation,
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [Slot(FormationZone.Front, 0, "currency_wars_character_04", 0.355, 0.305, 0.75)],
                0.75)));
        tracker.Observe(State(
            "preparation_generic",
            Phase2PageFamily.Preparation,
            Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [Slot(FormationZone.Front, 0, "currency_wars_character_04", 0.355, 0.305, 0.75)],
                0.75)));

        // 商店帧：formation 不应被商店紧凑坐标替换，应保留备战值（stale）
        var afterShop = tracker.Observe(State(
            "reward_shop",
            Phase2PageFamily.Supply,
            UnknownFormation(
                [Slot(FormationZone.Front, 0, "unknown-formation-unit-Front-1", 0.380, 0.383, 0.2)]))).Current;

        var formation = afterShop.Formation.Value ?? [];
        Assert.NotEmpty(formation);
        Assert.Contains(
            formation,
            item => (item.CardRegion?.X ?? -1) is >= 0.34 and <= 0.37 &&
                    (item.CardRegion?.Y ?? -1) is >= 0.29 and <= 0.32);
        Assert.DoesNotContain(
            formation,
            item => (item.CardRegion?.X ?? -1) is >= 0.370 and <= 0.395 &&
                    (item.CardRegion?.Y ?? -1) is >= 0.360 and <= 0.410);
    }

    [Fact]
    public void ConfirmedRewardShopFramesDoNotPollutePreparationFormation()
    {
        var tracker = new Phase2OperationalStateTracker();

        // 连续 2 帧商店（确认进入 MergeConfirmedFields）——实测 023158 复现路径：
        // 商店帧确认后紧凑坐标槽位进入 _lastConfirmedState，后续备战帧合并被带进。
        var shopState = State(
            "reward_shop",
            Phase2PageFamily.Supply,
            UnknownFormation(
                [Slot(FormationZone.Front, 0, "unknown-formation-unit-Front-1", 0.380, 0.383, 0.2)]));
        tracker.Observe(shopState);
        tracker.Observe(shopState);

        // 备战帧（标准坐标，Unknown 含个别不确定槽位）
        var afterPrep = tracker.Observe(State(
            "preparation_generic",
            Phase2PageFamily.Preparation,
            UnknownFormation(
                [
                    Slot(FormationZone.Front, 0, "currency_wars_character_04", 0.355, 0.305, 0.62),
                    Slot(FormationZone.Back, 6, "currency_wars_character_02", 0.297, 0.556, 0.70)
                ]))).Current;

        var formation = afterPrep.Formation.Value ?? [];
        Assert.DoesNotContain(
            formation,
            item => (item.CardRegion?.X ?? -1) is >= 0.370 and <= 0.395 &&
                    (item.CardRegion?.Y ?? -1) is >= 0.360 and <= 0.410);
        // 备战坐标槽位保留
        Assert.Contains(
            formation,
            item => (item.CardRegion?.X ?? -1) is >= 0.34 and <= 0.37 &&
                    (item.CardRegion?.Y ?? -1) is >= 0.29 and <= 0.32);
    }

    private static Phase2OperationalState State(
        string pageId,
        Phase2PageFamily family,
        Observation<IReadOnlyList<FormationCharacterState>> formation) => new()
    {
        PageFamily = family,
        PageId = pageId,
        NodeId = Observation<string>.Known("1-3", 0.9),
        Formation = formation
    };

    private static Observation<IReadOnlyList<FormationCharacterState>> UnknownFormation(
        IReadOnlyList<FormationCharacterState> slots) => new()
    {
        Status = ObservationStatus.Unknown,
        Value = slots,
        Confidence = 0,
        Uncertainty =
        [
            "阵容包含未识别角色或特殊占用单位；已识别槽位仍作为残缺证据保留。"
        ]
    };

    private static FormationCharacterState Slot(
        FormationZone zone,
        int slotIndex,
        string characterId,
        double x,
        double y,
        double confidence) => new(
        zone,
        slotIndex,
        characterId,
        1,
        "前后台",
        [],
        confidence,
        new EvidenceReference(
            "fixture:formation",
            $"vision:formation:{zone}:{slotIndex}",
            "slot",
            DateTimeOffset.UtcNow,
            confidence),
        CanDriveDecisions: confidence >= 0.5,
        CardRegion: new RelativeRegion(x, y, 0.06, 0.13));
}
