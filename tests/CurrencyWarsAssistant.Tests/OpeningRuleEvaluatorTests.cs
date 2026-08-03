using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tests;

public sealed class OpeningRuleEvaluatorTests
{
    private readonly OpeningRuleEvaluator _evaluator = new();

    [Fact]
    public void RejectsConfiguredCompetitorAndAffixCombination()
    {
        var observation = Observation(
            "investment_good",
            [("competitor_12", "灰手生命科技")],
            ("extra_strike", "额外打击"));
        var rules = new OpeningRuleSet
        {
            AcceptedInvestmentEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "investment_good"
            },
            RejectedEnemyCombinations =
            [
                new EnemyCombinationRule
                {
                    Id = "death_dragon_plus_extra_strike",
                    DisplayName = "死龙 + 额外打击",
                    RequiredCompetitors = new HashSet<string>(
                        ["competitor_12"],
                        StringComparer.OrdinalIgnoreCase),
                    RequiredModifiers = new HashSet<string>(
                        ["extra_strike"],
                        StringComparer.OrdinalIgnoreCase)
                }
            ]
        };

        var result = _evaluator.Evaluate(observation, rules);

        Assert.Equal(OpeningDecisionKind.Reroll, result.Kind);
        Assert.Contains(result.Reasons, reason => reason.Contains("死龙 + 额外打击"));
    }

    [Fact]
    public void DoesNotRejectWhenCombinationIsIncomplete()
    {
        var observation = Observation(
            "investment_good",
            [("competitor_12", "灰手生命科技")]);
        var rules = new OpeningRuleSet
        {
            AcceptedInvestmentEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "investment_good"
            },
            RejectedEnemyCombinations =
            [
                new EnemyCombinationRule
                {
                    Id = "death_dragon_plus_extra_strike",
                    DisplayName = "死龙 + 额外打击",
                    RequiredCompetitors = new HashSet<string>(
                        ["competitor_12"],
                        StringComparer.OrdinalIgnoreCase),
                    RequiredModifiers = new HashSet<string>(
                        ["extra_strike"],
                        StringComparer.OrdinalIgnoreCase)
                }
            ]
        };

        var result = _evaluator.Evaluate(observation, rules);

        Assert.Equal(OpeningDecisionKind.Keep, result.Kind);
    }

    [Fact]
    public void RejectsInvestmentEnvironmentOutsideAllowList()
    {
        var observation = Observation("investment_weak", []);
        var rules = new OpeningRuleSet
        {
            AcceptedInvestmentEnvironments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "investment_strong"
            }
        };

        var result = _evaluator.Evaluate(observation, rules);

        Assert.Equal(OpeningDecisionKind.Reroll, result.Kind);
        Assert.Contains(result.Reasons, reason => reason.Contains("不在保留列表"));
    }

    [Fact]
    public void RequestsReviewForLowConfidence()
    {
        var observation = new OpeningObservation(
            new ObservedItem("investment_good", "优质投资", 0.5),
            [],
            [],
            0.99,
            DateTimeOffset.UtcNow);

        var result = _evaluator.Evaluate(observation, new OpeningRuleSet());

        Assert.Equal(OpeningDecisionKind.Review, result.Kind);
    }

    private static OpeningObservation Observation(
        string investment,
        (string Id, string Name)[] competitors,
        params (string Id, string Name)[] modifiers) =>
        new(
            new ObservedItem(investment, investment, 0.99),
            competitors.Select(item => new ObservedItem(item.Id, item.Name, 0.99)).ToArray(),
            modifiers.Select(item => new ObservedItem(item.Id, item.Name, 0.99)).ToArray(),
            0.99,
            DateTimeOffset.UtcNow);
}
