using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」整局刷取循环（<see cref="FateGrailRunLoop"/>）单测。</summary>
public sealed class FateGrailRunLoopTests
{
    private sealed class FakeOpeningLoop
    {
        public int Calls { get; private set; }
        public bool Succeed { get; set; }
        public OpeningFilterSet? LastFilters { get; private set; }
        public OpeningRerollLoopOptions? LastOptions { get; private set; }

        public OpeningRerollLoopResult Run(OpeningFilterSet filters, OpeningRerollLoopOptions options)
        {
            Calls++;
            LastFilters = filters;
            LastOptions = options;
            return Succeed
                ? new OpeningRerollLoopResult(
                    OpeningRerollLoopState.Matched,
                    Calls,
                    Snapshot: null,
                    Evaluation: null,
                    Navigation: null,
                    Recovery: null,
                    "matched")
                : new OpeningRerollLoopResult(
                    OpeningRerollLoopState.NavigationFailed,
                    Calls,
                    Snapshot: null,
                    Evaluation: null,
                    Navigation: null,
                    Recovery: null,
                    "navigation failed");
        }
    }

    private static FateGrailRunCoordinator.IActionExecutor NoOpExecutor() =>
        new NoOpFakeExecutor();

    private sealed class NoOpFakeExecutor : FateGrailRunCoordinator.IActionExecutor
    {
        // 生产执行器会真实点选试炼并推进状态；fake 一律返回 true（执行成功），
        // 让协调器在 Done 步执行完动作后正常收工 Achieved。
        public Task<bool> ExecuteAsync(
            FateGrailRunEngine.Action action, string stepNode, string message,
            FateGrailRunEngine.TrialSide? trialToChoose,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private static FateGrailRunEngine.Snapshot AchievedSnapshot() =>
        new(
            EnvironmentId: FateGrailRunEngine.EnvironmentHeroArrival,
            Hp: 94, Gold: 50,
            OwnedMembers: new HashSet<string>(
                ["远坂凛", "吉尔伽美什", "Saber", "Archer"], StringComparer.OrdinalIgnoreCase),
            AvailableStrategyIds: new HashSet<string>(
                [InvestmentStrategyPicker.PurchaseSpecialistColor], StringComparer.OrdinalIgnoreCase),
            Goal: FateGrailRunEngine.UserGoal.AnyOne,
            Line: FateGrailRunEngine.EndLine.MiracleCompensation,
            HasBody5Cost: true, HasStarBadge: true, BondTier: 4,
            FiveCostBodyIds: new HashSet<string>(new[] { "Archer" }))
        {
            // 每步返回谈判快照；一次性 Achieved 用 Step 一步达成需左侧试炼=奇迹代偿。
        };

    [Fact]
    public async Task Achieved_WhenOpeningMatchedAndDecisionAchieved()
    {
        var opening = new FakeOpeningLoop { Succeed = true };
        var coordinator = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                AchievedSnapshot() with { LeftTrial = "令人决议·奇迹代偿：扣88血" }),
            NoOpExecutor());
        var loop = new FateGrailRunLoop(
            (filters, options, ct) =>
            {
                opening.Run(filters, options);
                return Task.FromResult(opening.Succeed
                    ? new OpeningRerollLoopResult(
                        OpeningRerollLoopState.Matched, 1, null, null, null, null, "matched")
                    : new OpeningRerollLoopResult(
                        OpeningRerollLoopState.NavigationFailed, 1, null, null, null, null, "fail"));
            },
            coordinator);

        var result = await loop.RunAsync(
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            maxRounds: 3);

        Assert.True(result.Succeeded);
        Assert.Equal(FateGrailRunLoop.RoundOutcome.Achieved, result.Outcome);
        Assert.Equal(1, result.RoundsPlayed);
    }

    [Fact]
    public async Task Reopens_WhenOpeningNotMatched()
    {
        // 开局循环始终未命中（NavigationFailed）-> 整局循环刷满轮数后以 Reopened 收尾（未达成）。
        var opening = new FakeOpeningLoop { Succeed = false };
        var coordinator = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                AchievedSnapshot()),
            NoOpExecutor());
        var loop = new FateGrailRunLoop(
            (filters, options, ct) =>
            {
                opening.Run(filters, options);
                return Task.FromResult(new OpeningRerollLoopResult(
                    OpeningRerollLoopState.NavigationFailed, opening.Calls,
                    null, null, null, null, "fail"));
            },
            coordinator);

        var result = await loop.RunAsync(
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            maxRounds: 3);

        Assert.False(result.Succeeded);
        Assert.Equal(FateGrailRunLoop.RoundOutcome.Reopened, result.Outcome);
        Assert.Equal(3, result.RoundsPlayed);
        Assert.Equal(3, opening.Calls); // 三局各刷一次开局（未命中即重刷）
    }

    [Fact]
    public async Task Reopens_WhenDecisionRerolls()
    {
        // 开局命中但决策重刷（如 D1 环境不可推进的快照）-> 整局循环重开一局。
        var opening = new FakeOpeningLoop { Succeed = true };
        var coordinator = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                AchievedSnapshot() with { EnvironmentId = "investment_environment_999" }),
            NoOpExecutor());
        var loop = new FateGrailRunLoop(
            (filters, options, ct) =>
            {
                opening.Run(filters, options);
                return Task.FromResult(new OpeningRerollLoopResult(
                    OpeningRerollLoopState.Matched, opening.Calls, null, null, null, null, "matched"));
            },
            coordinator);

        var result = await loop.RunAsync(
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            maxRounds: 3);

        Assert.False(result.Succeeded);
        Assert.Equal(FateGrailRunLoop.RoundOutcome.Reopened, result.Outcome);
        Assert.True(opening.Calls >= 2);
    }
}