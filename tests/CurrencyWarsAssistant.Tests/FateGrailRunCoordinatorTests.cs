using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」决策驱动协调器单测（fake 执行器记录动作序列）。</summary>
public sealed class FateGrailRunCoordinatorTests
{
    private sealed class FakeExecutor : FateGrailRunCoordinator.IActionExecutor
    {
        public List<(FateGrailRunEngine.Action Action, string Node)> Calls { get; } = new();
        public bool Result { get; set; } = true;
        public Task<bool> ExecuteAsync(
            FateGrailRunEngine.Action action, string stepNode, string message,
            FateGrailRunEngine.TrialSide? trialToChoose,
            CancellationToken cancellationToken)
        {
            Calls.Add((action, stepNode));
            return Task.FromResult(Result);
        }
    }

    private static FateGrailRunEngine.Snapshot ViableSnapshot(
        FateGrailRunEngine.UserGoal goal = FateGrailRunEngine.UserGoal.AnyOne)
        => new(
            EnvironmentId: FateGrailRunEngine.EnvironmentHeroArrival,
            Hp: 94, Gold: 50,
            OwnedMembers: new HashSet<string>(
                ["远坂凛", "吉尔伽美什", "Saber"], StringComparer.OrdinalIgnoreCase),
            AvailableStrategyIds: new HashSet<string>(
                [InvestmentStrategyPicker.PurchaseSpecialistColor], StringComparer.OrdinalIgnoreCase),
            Goal: goal,
            Line: FateGrailRunEngine.EndLine.MiracleCompensation,
            HasBody5Cost: true, HasStarBadge: true, BondTier: 4);

    [Fact]
    public async Task AcknowledgedSnapshot_MissingStoreMember_DispatchesBuyStoreMembers()
    {
        // 只持有远坂凛、缺其余商店成员 -> 引擎应分发 BuyStoreMembers（非 None 动作）
        var executor = new FakeExecutor();
        var coord = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                ViableSnapshot().with_owned()),
            executor);
        var outcome = await coord.RunAsync(FateGrailRunEngine.UserGoal.AnyOne, maxDecisions: 10);
        Assert.Contains(executor.Calls,
            c => c.Action == FateGrailRunEngine.Action.BuyStoreMembers);
        Assert.False(outcome is FateGrailRunCoordinator.Outcome.Achieved);
    }

    [Fact]
    public async Task RerollSnapshot_ReturnsRerollRequested()
    {
        // 不可推进环境 -> 引擎判 Reroll
        var coord = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                ViableSnapshot().with_environment("investment_environment_999")),
            new FakeExecutor());
        var outcome = await coord.RunAsync(FateGrailRunEngine.UserGoal.AnyOne, maxDecisions: 20);
        Assert.IsType<FateGrailRunCoordinator.Outcome.RerollRequested>(outcome);
    }

    [Fact]
    public async Task CauldronAchieved_DispatchesSideSelectionBeforeAchieved()
    {
        // （N1 回归）无限之釜浮现 -> 引擎发 ChoosePassiveTrial（Done 步）：
        // 协调器必须先执行点选动作（领取三星 Archer）再收工，不能不点选就虚报达成。
        var executor = new FakeExecutor();
        var coord = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                ViableSnapshot().cauldron_on()),
            executor);
        var outcome = await coord.RunAsync(FateGrailRunEngine.UserGoal.AnyOne, maxDecisions: 10);
        Assert.IsType<FateGrailRunCoordinator.Outcome.Achieved>(outcome);
        Assert.Contains(executor.Calls,
            c => c.Action == FateGrailRunEngine.Action.ChoosePassiveTrial && c.Node == "P");
    }

    [Fact]
    public async Task NullSnapshot_RetriesAndStillDrives()
    {
        var calls = 0;
        var executor = new FakeExecutor();
        var coord = new FateGrailRunCoordinator(
            _ =>
            {
                calls++;
                // 第一帧 null（重试），第二帧起给可达成快照
                return Task.FromResult<FateGrailRunEngine.Snapshot?>(
                    calls == 1 ? null : ViableSnapshot().cauldron_on());
            },
            executor);
        var outcome = await coord.RunAsync(FateGrailRunEngine.UserGoal.AnyOne, maxDecisions: 10);
        Assert.IsType<FateGrailRunCoordinator.Outcome.Achieved>(outcome);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task NoExecutor_NonNoneAction_Throws()
    {
        // 缺商店成员 -> 引擎要 BuyStoreMembers（非 None）；无执行器 -> 抛 InvalidOperationException
        var coord = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                ViableSnapshot().with_owned()),
            executor: null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coord.RunAsync(FateGrailRunEngine.UserGoal.AnyOne, maxDecisions: 10));
    }

    [Fact]
    public async Task MaxDecisions_Exhausted_ReturnsExhausted()
    {
        // 快照始终给"需要祈愿择优选择"的持续推进但永不达成 -> 达到 maxDecisions 返回 Exhausted
        // 用 BuyStoreMembers 缺成员场景 + 永不补成员 + 执行器每次 no-op true
        var executor = new FakeExecutor { Result = true };
        var snapshot = ViableSnapshot().with_owned();
        var coord = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(snapshot),
            executor);
        var outcome = await coord.RunAsync(FateGrailRunEngine.UserGoal.AnyOne, maxDecisions: 3);
        Assert.IsType<FateGrailRunCoordinator.Outcome.Exhausted>(outcome);
    }

    [Fact]
    public async Task DoneStep_WithAction_ExecutesActionBeforeAchieved()
    {
        // 回归（评审阻断1）：N1 奇迹代偿 Done 步还带 ChooseMiracleCompensation 动作，
        // 协调器必须先执行该动作（点试炼/扣血/8投影）再收工；不能虚报成功不执行。
        var executor = new FakeExecutor { Result = true };
        var coord = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                ViableSnapshot().with_trial_left(
                    "令人决议·奇迹代偿：扣88血")),
            executor);
        var outcome = await coord.RunAsync(FateGrailRunEngine.UserGoal.AnyOne, maxDecisions: 10);
        Assert.IsType<FateGrailRunCoordinator.Outcome.Achieved>(outcome);
        Assert.Contains(
            executor.Calls,
            call => call.Action == FateGrailRunEngine.Action.ChooseMiracleCompensation);
    }
}

internal static class FateGrailRunCoordinatorTestSnapshotB
{
    // 便捷构造器：在某基础快照上派生变体（模拟"执行器推进后"的状态）。
    public static FateGrailRunEngine.Snapshot with_owned(this FateGrailRunEngine.Snapshot s)
        => s with { OwnedMembers = new HashSet<string>(["远坂凛"], StringComparer.OrdinalIgnoreCase) };

    public static FateGrailRunEngine.Snapshot with_environment(this FateGrailRunEngine.Snapshot s, string env)
        => s with { EnvironmentId = env };

    public static FateGrailRunEngine.Snapshot cauldron_on(this FateGrailRunEngine.Snapshot s)
        => s with { LeftTrial = "诅咒·无限之釜：直接获得1个三星5费Archer" };

    public static FateGrailRunEngine.Snapshot with_trial_left(this FateGrailRunEngine.Snapshot s, string leftTrial)
        => s with { LeftTrial = leftTrial };
}
