using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 动画干扰降频器测试（用户 2026-08-07 周期制方案）：
/// 备战页槽位因动画持续识别失败 → 连续失败达到阈值后进入降频
///（保留旧 Known + 每 5 帧放行）；识别成功立即恢复高频。
/// </summary>
public sealed class FormationSlotFailureThrottleTests
{
    [Fact]
    public void PersistentFailure_DowngradesSlotAndRecoversAfterSuccess()
    {
        var tracker = new Phase2OperationalStateTracker();

        // 第一帧：Front#0 识别成功（银狼），作为旧 Known 基准
        tracker.Observe(
            PreparationWithFormation(
                KnownSlot("Front", 0, "currency_wars_character_05", 0.9)),
            default);
        // 连续失败帧（动画干扰：槽位识别成 unknown-formation-unit）
        for (var i = 0; i < 6; i++)
        {
            tracker.Observe(
                PreparationWithFormation(
                    UnknownSlot("Front", 0)),
                default);
        }

        // 动画结束，恢复识别成功
        var update = tracker.Observe(
            PreparationWithFormation(
                KnownSlot("Front", 0, "currency_wars_character_05", 0.9)),
            default);

        var formation = update.Current.Formation;
        Assert.NotNull(formation);
        Assert.Equal(ObservationStatus.Known, formation.Status);
        var slot = formation.Value?.FirstOrDefault(
            s => s.Zone == FormationZone.Front && s.SlotIndex == 0);
        Assert.NotNull(slot);
        Assert.Equal("currency_wars_character_05", slot.CharacterId);
        // 合并逻辑应保留旧 Known 身份（降频期间不倒退）
        Assert.True(slot.CanDriveDecisions);
    }

    [Fact]
    public void SingleFailureFrame_DoesNotDowngrade()
    {
        var tracker = new Phase2OperationalStateTracker();
        tracker.Observe(
            PreparationWithFormation(
                KnownSlot("Front", 0, "currency_wars_character_05", 0.9)),
            default);
        // 仅 1 帧失败（远低于阈值 3），随后成功——不触发降频
        tracker.Observe(
            PreparationWithFormation(UnknownSlot("Front", 0)),
            default);
        var update = tracker.Observe(
            PreparationWithFormation(
                KnownSlot("Front", 0, "currency_wars_character_05", 0.95)),
            default);
        var formation = update.Current.Formation;
        Assert.Equal(ObservationStatus.Known, formation.Status);
        var slot = formation.Value?.FirstOrDefault(
            s => s.Zone == FormationZone.Front && s.SlotIndex == 0);
        Assert.NotNull(slot);
        Assert.Equal("currency_wars_character_05", slot.CharacterId);
    }

    private static Phase2OperationalState PreparationWithFormation(
        params FormationCharacterState[] slots) => new()
    {
        PageFamily = Phase2PageFamily.Preparation,
        NodeId = Observation<string>.Known("1-1", 0.95),
        Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
            slots,
            0.9)
    };

    private static FormationCharacterState KnownSlot(
        string zone,
        int index,
        string characterId,
        double confidence) => new(
        ParseZone(zone),
        index,
        characterId,
        3,
        "standing",
        ["currency_wars_equipment_001"],
        confidence,
        new EvidenceReference("test", "fixture:throttle"),
        CanDriveDecisions: true);

    private static FormationCharacterState UnknownSlot(
        string zone,
        int index) => new(
        ParseZone(zone),
        index,
        "unknown-formation-unit-Front-" + (index + 1),
        null,
        "unknown",
        [],
        0.1,
        new EvidenceReference("test", "fixture:throttle-fail"),
        CanDriveDecisions: false);

    private static FormationZone ParseZone(string zone) =>
        zone switch
        {
            "Front" => FormationZone.Front,
            "Back" => FormationZone.Back,
            "Bench" => FormationZone.Bench,
            _ => FormationZone.Front
        };
}
