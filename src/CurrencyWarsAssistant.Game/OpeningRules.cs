namespace CurrencyWarsAssistant.Game;

public sealed class OpeningRuleSet
{
    public double MinimumConfidence { get; init; } = 0.88;
    public bool KeepUnknownInvestmentEnvironment { get; init; }
    public HashSet<string> AcceptedInvestmentEnvironments { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RejectedInvestmentEnvironments { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RejectedCompetitors { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RejectedEnemyModifiers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<EnemyCombinationRule> RejectedEnemyCombinations { get; init; } = [];
}

public sealed class EnemyCombinationRule
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public HashSet<string> RequiredCompetitors { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RequiredModifiers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OpeningRuleEvaluator
{
    public OpeningDecision Evaluate(OpeningObservation observation, OpeningRuleSet rules)
    {
        var reasons = new List<string>();

        if (observation.PageConfidence < rules.MinimumConfidence)
        {
            reasons.Add($"开局页面置信度过低：{observation.PageConfidence:P0}");
        }

        if (observation.InvestmentEnvironment is null)
        {
            if (!rules.KeepUnknownInvestmentEnvironment)
            {
                reasons.Add("未识别到开局投资环境");
            }
        }
        else if (observation.InvestmentEnvironment.Confidence < rules.MinimumConfidence)
        {
            reasons.Add(
                $"投资环境“{observation.InvestmentEnvironment.DisplayName}”置信度过低：" +
                $"{observation.InvestmentEnvironment.Confidence:P0}");
        }

        var uncertainCompetitors = observation.Competitors
            .Where(item => item.Confidence < rules.MinimumConfidence)
            .Select(item => item.DisplayName)
            .ToArray();
        if (uncertainCompetitors.Length > 0)
        {
            reasons.Add($"竞争对手识别不稳定：{string.Join("、", uncertainCompetitors)}");
        }

        var uncertainModifiers = observation.EnemyModifiers
            .Where(item => item.Confidence < rules.MinimumConfidence)
            .Select(item => item.DisplayName)
            .ToArray();
        if (uncertainModifiers.Length > 0)
        {
            reasons.Add($"敌人词条识别不稳定：{string.Join("、", uncertainModifiers)}");
        }

        if (reasons.Count > 0)
        {
            return new OpeningDecision(OpeningDecisionKind.Review, reasons);
        }

        var rejectedCompetitor = observation.Competitors.FirstOrDefault(
            item => rules.RejectedCompetitors.Contains(item.Id));
        if (rejectedCompetitor is not null)
        {
            return new OpeningDecision(
                OpeningDecisionKind.Reroll,
                [$"命中需要刷掉的竞争对手：{rejectedCompetitor.DisplayName}"]);
        }

        var rejectedModifier = observation.EnemyModifiers.FirstOrDefault(
            item => rules.RejectedEnemyModifiers.Contains(item.Id));
        if (rejectedModifier is not null)
        {
            return new OpeningDecision(
                OpeningDecisionKind.Reroll,
                [$"命中需要刷掉的敌人词条：{rejectedModifier.DisplayName}"]);
        }

        foreach (var combination in rules.RejectedEnemyCombinations)
        {
            if (combination.RequiredCompetitors.IsSubsetOf(observation.CompetitorIds) &&
                combination.RequiredModifiers.IsSubsetOf(observation.ModifierIds))
            {
                return new OpeningDecision(
                    OpeningDecisionKind.Reroll,
                    [$"命中需要刷掉的敌人组合：{combination.DisplayName}"]);
            }
        }

        var investment = observation.InvestmentEnvironment;
        if (investment is null)
        {
            return rules.KeepUnknownInvestmentEnvironment
                ? new OpeningDecision(OpeningDecisionKind.Keep, ["允许保留未知投资环境"])
                : new OpeningDecision(OpeningDecisionKind.Review, ["投资环境未知"]);
        }

        if (rules.RejectedInvestmentEnvironments.Contains(investment.Id))
        {
            return new OpeningDecision(
                OpeningDecisionKind.Reroll,
                [$"投资环境不符合要求：{investment.DisplayName}"]);
        }

        if (rules.AcceptedInvestmentEnvironments.Count > 0 &&
            !rules.AcceptedInvestmentEnvironments.Contains(investment.Id))
        {
            return new OpeningDecision(
                OpeningDecisionKind.Reroll,
                [$"投资环境不在保留列表中：{investment.DisplayName}"]);
        }

        return new OpeningDecision(
            OpeningDecisionKind.Keep,
            [$"投资环境符合要求：{investment.DisplayName}", "未命中敌人负面规则"]);
    }
}
