using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 血量Δ显示规则测试（用户 2026-08-05 规则）：
/// 完美通关且血量未识别（HealthDelta=null）时登记 +2。
/// </summary>
public sealed class HealthDeltaDisplayTests
{
    [Fact]
    public void PerfectWithUnknownHealthShowsPlusTwo()
    {
        // 2-1/2-4 场景：完美通关√ 但血量未识别 → 血量Δ 显示 +2
        var node = MakeNode(healthDelta: null, clearStatus: NodeClearStatus.Perfect);
        Assert.Equal("+2", MainViewModel.FormatHealthDeltaDisplay(node));
    }

    [Fact]
    public void PerfectWithKnownHealthShowsActualValue()
    {
        // 完美且血量已识别 → 显示实际值（不是 +2）
        Assert.Equal("+2", MainViewModel.FormatHealthDeltaDisplay(
            MakeNode(healthDelta: 2, clearStatus: NodeClearStatus.Perfect)));
        Assert.Equal("+5", MainViewModel.FormatHealthDeltaDisplay(
            MakeNode(healthDelta: 5, clearStatus: NodeClearStatus.Perfect)));
        Assert.Equal("-3", MainViewModel.FormatHealthDeltaDisplay(
            MakeNode(healthDelta: -3, clearStatus: NodeClearStatus.NotPerfect)));
    }

    [Fact]
    public void NonPerfectWithUnknownHealthShowsDash()
    {
        // 非完美且血量未知 → 显示 "—"（不推断）
        var node = MakeNode(healthDelta: null, clearStatus: NodeClearStatus.Unknown);
        Assert.Equal("—", MainViewModel.FormatHealthDeltaDisplay(node));
    }

    [Fact]
    public void HealthDepletedShowsDownArrow()
    {
        var node = MakeNode(healthDelta: null, clearStatus: NodeClearStatus.NotPerfect);
        node = node with { HealthDepleted = true };
        Assert.Equal("↓?", MainViewModel.FormatHealthDeltaDisplay(node));
    }

    [Fact]
    public void Node37WithHighActionShowsPlus42()
    {
        // 用户 2026-08-06 规则：3-7 整局评级页不显示血量Δ，
        // 行动值 ≥100 → +42。
        var node = MakeNode(
            healthDelta: null,
            clearStatus: NodeClearStatus.Perfect,
            nodeId: "3-7",
            action: 163);
        Assert.Equal("+42", MainViewModel.FormatHealthDeltaDisplay(node));
    }

    [Fact]
    public void Node37WithLowActionShowsPlus2()
    {
        var node = MakeNode(
            healthDelta: null,
            clearStatus: NodeClearStatus.Perfect,
            nodeId: "3-7",
            action: 50);
        Assert.Equal("+2", MainViewModel.FormatHealthDeltaDisplay(node));
    }

    [Fact]
    public void Node37BoundaryActionValues()
    {
        // 边界：action=100（>=100 含等号）→ +42；99 → +2；null（?? 0）→ +2
        Assert.Equal("+42", MainViewModel.FormatHealthDeltaDisplay(MakeNode(
            healthDelta: null,
            clearStatus: NodeClearStatus.Perfect,
            nodeId: "3-7",
            action: 100)));
        Assert.Equal("+2", MainViewModel.FormatHealthDeltaDisplay(MakeNode(
            healthDelta: null,
            clearStatus: NodeClearStatus.Perfect,
            nodeId: "3-7",
            action: 99)));
        Assert.Equal("+2", MainViewModel.FormatHealthDeltaDisplay(MakeNode(
            healthDelta: null,
            clearStatus: NodeClearStatus.Perfect,
            nodeId: "3-7",
            action: null)));
    }

    private static HistoricalNodeDashboardEntry MakeNode(
        int? healthDelta,
        NodeClearStatus clearStatus,
        string nodeId = "2-1",
        int? action = 90) =>
        new(
            "run-1",
            nodeId,
            FinalDamage: null,
            RemainingActionValue: action,
            GoldSpentSincePreviousNode: null,
            GoldDeltaSincePreviousNode: null,
            GoldReward: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            IsComplete: true,
            ClearStatus: clearStatus,
            HealthDelta: healthDelta);
}
