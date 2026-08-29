using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tests;

public sealed class RewardBattlePageStateClassifierTests
{
    [Fact]
    public void AutoBattleRequiresMultiFrameConsensusAndNeverRetogglesAfterConfirmation()
    {
        var first = RewardAutoBattlePolicy.Observe(
            false,
            [AutoBattleVisualState.Enabled]);
        var confirmed = RewardAutoBattlePolicy.Observe(
            false,
            [
                AutoBattleVisualState.Enabled,
                AutoBattleVisualState.Unknown,
                AutoBattleVisualState.Enabled
            ]);
        var transientMiss = RewardAutoBattlePolicy.Observe(
            confirmed.ConfirmedEnabled,
            [
                AutoBattleVisualState.Disabled,
                AutoBattleVisualState.Unknown,
                AutoBattleVisualState.Disabled
            ]);
        var disabled = RewardAutoBattlePolicy.Observe(
            false,
            [
                AutoBattleVisualState.Disabled,
                AutoBattleVisualState.Unknown,
                AutoBattleVisualState.Disabled
            ]);

        Assert.False(first.ConfirmedEnabled);
        Assert.False(first.ShouldPressToggle);
        Assert.Equal(AutoBattleVisualState.Unknown, first.Consensus);
        Assert.True(confirmed.ConfirmedEnabled);
        Assert.True(transientMiss.ConfirmedEnabled);
        Assert.False(transientMiss.ShouldPressToggle);
        Assert.Equal(AutoBattleVisualState.Disabled, disabled.Consensus);
        Assert.True(disabled.ShouldPressToggle);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("investment_environment_059", 3)]
    [InlineData("investment_environment_060", 5)]
    [InlineData("investment_environment_061", 5)]
    public void BattleBudgetUsesOnlyActuallySelectedOverheatedEnvironment(
        string? selectedId,
        int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            RewardBattleTimingPolicy.SelectBattleBudget(selectedId));
    }

    [Fact]
    public void TimeoutOwnershipRequiresSuccessfulInputThenObservedBattle()
    {
        var tracker = new RewardBattleStartOwnershipTracker();

        Assert.False(tracker.IsAuthorized);
        Assert.False(tracker.Observe(RewardBattleFlowState.Battle));
        Assert.False(tracker.IsAuthorized);

        tracker.MarkSuccessfulStartInput();
        Assert.False(tracker.Observe(RewardBattleFlowState.StartingBattle));
        Assert.False(tracker.IsAuthorized);
        Assert.True(tracker.Observe(RewardBattleFlowState.Battle));
        Assert.True(tracker.IsAuthorized);
    }

    [Fact]
    public void ReturningToPreparationClearsPendingTimeoutOwnership()
    {
        var tracker = new RewardBattleStartOwnershipTracker();
        tracker.MarkSuccessfulStartInput();

        Assert.False(tracker.Observe(RewardBattleFlowState.Preparation));
        Assert.False(tracker.Observe(RewardBattleFlowState.Battle));
        Assert.False(tracker.IsAuthorized);
    }

    [Fact]
    public void ThreeRecordedDistinctPlacementsOverrideContradictoryIncompleteStatus()
    {
        static PreparationPlacement Placement(int slot, string name) =>
            new(
                new RecognizedBenchCharacter(
                    slot,
                    new CurrencyWarsCharacterData(
                        name,
                        name,
                        "前台",
                        [1],
                    false),
                    0.99),
                PreparationLane.Front,
                slot);

        Assert.True(
            PreparationPlacementConsistencyPolicy.HasThreeDistinctPlacements(
                [
                    Placement(0, "角色甲"),
                    Placement(1, "角色乙"),
                    Placement(2, "角色丙")
                ]));
        Assert.False(
            PreparationPlacementConsistencyPolicy.HasThreeDistinctPlacements(
                [
                    Placement(0, "角色甲"),
                    Placement(1, "角色甲"),
                    Placement(2, "角色乙")
                ]));
    }

    [Fact]
    public void TwoRecordedPlacementsDegradeToContinuationInsteadOfRequestingStop()
    {
        static PreparationPlacement Placement(int slot, string name) =>
            new(
                new RecognizedBenchCharacter(
                    slot,
                    new CurrencyWarsCharacterData(
                        name,
                        name,
                        "前台",
                        [1],
                        false),
                    0.99),
                PreparationLane.Front,
                slot);

        IReadOnlyList<PreparationPlacement> existing =
            [Placement(0, "角色甲"), Placement(1, "角色乙")];
        var incomplete = new PreparationBoardResult(
            PreparationBoardStatus.NoEligibleCharacter,
            [],
            [Placement(0, "角色甲")],
            "规则内没有第三名候选");

        var continuation = PreparationPlacementConsistencyPolicy
            .CreateDegradedContinuation(incomplete, existing);

        Assert.True(continuation.Succeeded);
        Assert.False(continuation.ShouldReroll);
        Assert.Equal(2, continuation.Placements.Count);
    }

    [Fact]
    public void ClassifiesEverySupportedBattleTransition()
    {
        (string? Current, string Preparation, string Post,
            RewardBattlePageState Expected)[] cases =
        [
            ("preparation_1_1", "preparation_1_1", "reward_shop",
                RewardBattlePageState.Preparation),
            ("reward_battle", "preparation_1_1", "reward_shop",
                RewardBattlePageState.Battle),
            ("challenge_success", "preparation_1_1", "reward_shop",
                RewardBattlePageState.Success),
            ("reward_shop", "preparation_1_1", "reward_shop",
                RewardBattlePageState.ExpectedPostBattle),
            ("investment_strategy", "preparation_1_2", "investment_strategy",
                RewardBattlePageState.ExpectedPostBattle),
            (null, "preparation_1_1", "reward_shop",
                RewardBattlePageState.Unknown),
            ("currency_wars_home", "preparation_1_1", "reward_shop",
                RewardBattlePageState.Unknown)
        ];
        foreach (var test in cases)
        {
            Assert.Equal(
                test.Expected,
                RewardBattlePageStateClassifier.Classify(
                    test.Current,
                    test.Preparation,
                    test.Post));
        }
    }

    [Fact]
    public void StateMachineKeepsAnimationContextAndAcceptsBattleOrInstantKill()
    {
        var battleMachine = StartBattleMachine();
        var animation = battleMachine.Observe(null, []);
        var battle = battleMachine.Observe("reward_battle", []);
        battleMachine.Apply(battle);
        var ultimateAnimation = battleMachine.Observe(null, []);
        var ultimateFalsePositive = battleMachine.Observe(
            "preparation_1_1",
            []);
        var instantKillMachine = StartBattleMachine();
        var instantKill = instantKillMachine.Observe("challenge_success", []);

        Assert.True(animation.Allowed);
        Assert.Equal(RewardBattleFlowState.StartingBattle, animation.State);
        Assert.True(battle.Allowed);
        Assert.Equal(RewardBattleFlowState.Battle, battle.State);
        Assert.True(ultimateAnimation.Allowed);
        Assert.Equal(RewardBattlePageState.Unknown, ultimateAnimation.Observation);
        Assert.Equal(RewardBattleFlowState.Battle, ultimateAnimation.State);
        Assert.True(ultimateFalsePositive.Allowed);
        Assert.Equal(
            RewardBattlePageState.Unknown,
            ultimateFalsePositive.Observation);
        Assert.Equal(
            RewardBattleFlowState.Battle,
            ultimateFalsePositive.State);
        Assert.True(instantKill.Allowed);
        Assert.Equal(RewardBattleFlowState.Success, instantKill.State);
    }

    [Fact]
    public void StateMachineOnlyAllowsExpectedPostPageAfterContinue()
    {
        (string Preparation, string Post)[] cases =
        [
            ("preparation_1_1", "reward_shop"),
            ("preparation_1_2", "investment_strategy")
        ];
        foreach (var test in cases)
        {
            var expectedMachine = new RewardBattleStateMachine(
                test.Preparation,
                test.Post);
            expectedMachine.Apply(
                expectedMachine.Observe("challenge_success", []));
            Assert.True(expectedMachine.TryContinueChallenge());

            var expected = expectedMachine.Observe(test.Post, []);

            var invalidMachine = new RewardBattleStateMachine(
                test.Preparation,
                test.Post);
            invalidMachine.Apply(
                invalidMachine.Observe("challenge_success", []));
            invalidMachine.TryContinueChallenge();
            var impossible = invalidMachine.Observe(
                "investment_environment",
                []);

            Assert.True(expected.Allowed);
            Assert.Equal(
                RewardBattleFlowState.ExpectedPostBattle,
                expected.State);
            Assert.False(impossible.Allowed);
            Assert.Equal(
                RewardBattleFlowState.ContinuingAfterSuccess,
                impossible.State);
        }
    }

    [Fact]
    public void StateMachineReturnsToPreparationWhenStartClickDidNotLeave()
    {
        var machine = StartBattleMachine();

        var result = machine.Observe("preparation_1_1", []);

        Assert.True(result.Allowed);
        Assert.Equal(RewardBattleFlowState.Preparation, result.State);
    }

    [Fact]
    public void StateMachineUsesStrongStatusEvidenceOnlyInBattleContext()
    {
        PageAnchorDiagnostic[] diagnostics =
        [
            new("reward_battle", "reward_battle_status_bar", 0.7853, 0.90),
            new("reward_shop", "reward_shop_refresh_panel", 0.4181, 0.86)
        ];
        var afterStart = StartBattleMachine().Observe(null, diagnostics);
        var withoutContext = new RewardBattleStateMachine(
            "preparation_1_1",
            "reward_shop").Observe(null, diagnostics);

        Assert.Equal("reward_battle", afterStart.PageId);
        Assert.True(afterStart.UsedContextualBattleEvidence);
        Assert.Null(withoutContext.PageId);
        Assert.False(withoutContext.UsedContextualBattleEvidence);
    }

    [Fact]
    public void StateMachineRejectsWeakAmbiguousAndUnrelatedEvidence()
    {
        var weak = StartBattleMachine().Observe(
            null,
            [
                new("reward_battle", "status", 0.73, 0.90),
                new("reward_shop", "shop", 0.20, 0.86)
            ]);
        var ambiguous = StartBattleMachine().Observe(
            null,
            [
                new("reward_battle", "status", 0.80, 0.90),
                new("reward_shop", "shop", 0.70, 0.86)
            ]);
        var unrelated = StartBattleMachine().Observe(
            "investment_environment",
            [new("reward_battle", "status", 0.99, 0.90)]);

        Assert.Null(weak.PageId);
        Assert.False(weak.UsedContextualBattleEvidence);
        Assert.Null(ambiguous.PageId);
        Assert.False(ambiguous.UsedContextualBattleEvidence);
        Assert.False(unrelated.Allowed);
    }

    private static RewardBattleStateMachine StartBattleMachine()
    {
        var machine = new RewardBattleStateMachine(
            "preparation_1_1",
            "reward_shop");
        machine.Apply(machine.Observe("preparation_1_1", []));
        Assert.True(machine.TryStartBattle());
        return machine;
    }
}
