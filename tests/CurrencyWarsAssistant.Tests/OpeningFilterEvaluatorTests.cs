using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tests;

public sealed class OpeningFilterEvaluatorTests
{
    private readonly OpeningFilterEvaluator evaluator = new();

    [Fact]
    public void EmptyFiltersMatchAnyCompleteSnapshot()
    {
        var result = evaluator.Evaluate(
            Snapshot(["environment_a"], ["competitor_a"], ["modifier_a"]),
            new OpeningFilterSet());

        Assert.True(result.Matched);
        Assert.Empty(result.MatchedConditions);
        Assert.Empty(result.ViolatedConditions);
        Assert.Contains(result.Reasons, value => value.Contains("未设置"));
    }

    [Fact]
    public void InvestmentPreferencesAreOrWhileEnemyRequirementsRemainAnd()
    {
        var filters = new OpeningFilterSet
        {
            InvestmentEnvironments =
            [
                Item("environment_a", OpeningFilterState.Require),
                Item("environment_b", OpeningFilterState.Require)
            ],
            Competitors = [Item("competitor_a", OpeningFilterState.Require)],
            EnemyModifiers = [Item("modifier_a", OpeningFilterState.Require)]
        };

        var result = evaluator.Evaluate(
            Snapshot(
                ["environment_a", "environment_b"],
                ["competitor_a"],
                ["modifier_a"]),
            filters);

        Assert.True(result.Matched);
        Assert.Equal(3, result.MatchedConditions.Count);
        Assert.Empty(result.ViolatedConditions);
    }

    [Fact]
    public void OnePreferredInvestmentAmongThreeIsEnough()
    {
        var result = evaluator.Evaluate(
            Snapshot(
                ["environment_b", "environment_x", "environment_y"],
                [],
                []),
            new OpeningFilterSet
            {
                InvestmentEnvironments =
                [
                    Item("environment_a", OpeningFilterState.Require),
                    Item("environment_b", OpeningFilterState.Require)
                ]
            });

        Assert.True(result.Matched);
        Assert.Single(result.MatchedConditions);
        Assert.Contains(
            "environment_b",
            result.MatchedConditions.Single().DisplayName,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPreferredInvestmentAmongThreeViolatesFilter()
    {
        var result = evaluator.Evaluate(
            Snapshot(
                ["environment_x", "environment_y", "environment_z"],
                [],
                []),
            new OpeningFilterSet
            {
                InvestmentEnvironments =
                [
                    Item("environment_a", OpeningFilterState.Require),
                    Item("environment_b", OpeningFilterState.Require)
                ]
            });

        Assert.False(result.Matched);
        Assert.Single(result.ViolatedConditions);
    }

    [Fact]
    public void MissingRequiredItemViolatesFilter()
    {
        var result = evaluator.Evaluate(
            Snapshot(["environment_a"], [], []),
            new OpeningFilterSet
            {
                Competitors = [Item("competitor_a", OpeningFilterState.Require)]
            });

        Assert.False(result.Matched);
        var violation = Assert.Single(result.ViolatedConditions);
        Assert.Equal(OpeningConditionKind.Competitor, violation.Kind);
        Assert.Contains("缺少必选", violation.Reason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RejectedItemMustNotBePresent(bool isPresent, bool expectedMatch)
    {
        var result = evaluator.Evaluate(
            Snapshot(
                [],
                isPresent ? ["competitor_a"] : [],
                []),
            new OpeningFilterSet
            {
                Competitors = [Item("competitor_a", OpeningFilterState.Reject)]
            });

        Assert.Equal(expectedMatch, result.Matched);
    }

    [Fact]
    public void RequiredCombinationCanSpanAllThreeCategories()
    {
        var result = evaluator.Evaluate(
            Snapshot(
                ["environment_a"],
                ["competitor_a"],
                ["modifier_a", "modifier_b"]),
            new OpeningFilterSet
            {
                Combinations =
                [
                    Combination(
                        OpeningFilterState.Require,
                        ["environment_a"],
                        ["competitor_a"],
                        ["modifier_a", "modifier_b"])
                ]
            });

        Assert.True(result.Matched);
        Assert.Equal(
            OpeningConditionKind.Combination,
            Assert.Single(result.MatchedConditions).Kind);
    }

    [Fact]
    public void IncompleteRequiredCombinationViolatesFilter()
    {
        var result = evaluator.Evaluate(
            Snapshot(
                ["environment_a"],
                ["competitor_a"],
                ["modifier_a"]),
            new OpeningFilterSet
            {
                Combinations =
                [
                    Combination(
                        OpeningFilterState.Require,
                        ["environment_a"],
                        ["competitor_a"],
                        ["modifier_a", "modifier_b"])
                ]
            });

        Assert.False(result.Matched);
        Assert.Contains(result.Reasons, value => value.Contains("未完整命中必选组合"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RejectedCombinationOnlyViolatesWhenFullyHit(
        bool includeModifier,
        bool expectedMatch)
    {
        var result = evaluator.Evaluate(
            Snapshot(
                [],
                ["competitor_12"],
                includeModifier ? ["extra_strike"] : []),
            new OpeningFilterSet
            {
                Combinations =
                [
                    Combination(
                        OpeningFilterState.Reject,
                        [],
                        ["competitor_12"],
                        ["extra_strike"])
                ]
            });

        Assert.Equal(expectedMatch, result.Matched);
    }

    [Fact]
    public void IgnoredItemsAndCombinationsHaveNoEffect()
    {
        var result = evaluator.Evaluate(
            Snapshot([], [], []),
            new OpeningFilterSet
            {
                InvestmentEnvironments =
                    [Item("missing_environment", OpeningFilterState.Ignore)],
                Combinations =
                [
                    new OpeningCombinationFilter
                    {
                        Id = "empty_ignored",
                        DisplayName = "空组合",
                        State = OpeningFilterState.Ignore
                    }
                ]
            });

        Assert.True(result.Matched);
        Assert.Empty(result.MatchedConditions);
        Assert.Empty(result.ViolatedConditions);
    }

    [Fact]
    public void IdMatchingIsCaseInsensitive()
    {
        var result = evaluator.Evaluate(
            Snapshot(["ENVIRONMENT_A"], [], []),
            new OpeningFilterSet
            {
                InvestmentEnvironments =
                    [Item("environment_a", OpeningFilterState.Require)]
            });

        Assert.True(result.Matched);
    }

    [Fact]
    public void ActiveEmptyCombinationIsRejectedAsInvalidConfiguration()
    {
        var filters = new OpeningFilterSet
        {
            Combinations =
            [
                new OpeningCombinationFilter
                {
                    Id = "empty",
                    DisplayName = "空组合",
                    State = OpeningFilterState.Reject
                }
            ]
        };

        Assert.Throws<ArgumentException>(
            () => evaluator.Evaluate(Snapshot([], [], []), filters));
    }

    [Fact]
    public void ProfilesUseAndAcrossCategoriesAndOrWithinPositiveChoices()
    {
        var filters = new OpeningFilterSet
        {
            Profiles =
            [
                new OpeningFilterProfile
                {
                    Id = "profile_a",
                    DisplayName = "长线利好方案",
                    AcceptedInvestmentEnvironmentIds = ["long_term"],
                    RequiredCompetitorIds = ["competitor_a", "competitor_b"],
                    RejectedEnemyModifierIds = ["hot_bomb", "time_assassin"],
                    PreferredInvestmentStrategyIds =
                        ["chaos", "purchasing_specialist"]
                }
            ]
        };

        var result = evaluator.Evaluate(
            Snapshot(
                ["long_term", "other"],
                ["competitor_b"],
                ["safe_modifier"]),
            filters);

        Assert.True(result.Matched);
        Assert.Equal(["profile_a"], result.EffectiveMatchedProfileIds);
    }

    [Fact]
    public void AnyRejectedProfileItemInvalidatesThatProfile()
    {
        var result = evaluator.Evaluate(
            Snapshot(["long_term"], [], ["time_assassin"]),
            new OpeningFilterSet
            {
                Profiles =
                [
                    new OpeningFilterProfile
                    {
                        Id = "profile_a",
                        DisplayName = "方案A",
                        AcceptedInvestmentEnvironmentIds = ["long_term"],
                        RejectedEnemyModifierIds =
                            ["hot_bomb", "time_assassin"]
                    }
                ]
            });

        Assert.False(result.Matched);
        Assert.Empty(result.EffectiveMatchedProfileIds);
    }

    [Fact]
    public void MultipleProfilesAreOrAlternatives()
    {
        var result = evaluator.Evaluate(
            Snapshot(["environment_b"], [], []),
            new OpeningFilterSet
            {
                Profiles =
                [
                    new OpeningFilterProfile
                    {
                        Id = "profile_a",
                        DisplayName = "方案A",
                        AcceptedInvestmentEnvironmentIds = ["environment_a"]
                    },
                    new OpeningFilterProfile
                    {
                        Id = "profile_b",
                        DisplayName = "方案B",
                        AcceptedInvestmentEnvironmentIds = ["environment_b"]
                    }
                ]
            });

        Assert.True(result.Matched);
        Assert.Equal(["profile_b"], result.EffectiveMatchedProfileIds);
    }

    private static OpeningSnapshot Snapshot(
        string[] environments,
        string[] competitors,
        string[] modifiers) =>
        new(environments, competitors, modifiers);

    private static OpeningItemFilter Item(
        string id,
        OpeningFilterState state) =>
        new(id, id, state);

    private static OpeningCombinationFilter Combination(
        OpeningFilterState state,
        string[] environments,
        string[] competitors,
        string[] modifiers) =>
        new()
        {
            Id = "combination",
            DisplayName = "测试组合",
            State = state,
            InvestmentEnvironmentIds = environments,
            CompetitorIds = competitors,
            EnemyModifierIds = modifiers
        };
}
