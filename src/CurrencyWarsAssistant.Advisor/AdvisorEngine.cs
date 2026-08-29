namespace CurrencyWarsAssistant.Advisor;

public sealed record ConditionEvaluation(
    TriState Result,
    UnknownPolicy UnknownPolicy,
    string Explanation,
    string Field);

public sealed class ConditionEvaluator
{
    public ConditionEvaluation Evaluate(
        GuideCondition condition,
        RunSnapshot snapshot) => condition.Field.ToLowerInvariant() switch
    {
        "page" => Scalar(condition, snapshot.PageId),
        "stage" => Scalar(condition, snapshot.Stage),
        "investment_environment" =>
            Scalar(condition, snapshot.InvestmentEnvironmentId),
        "board" => Set(condition, snapshot.BoardCharacterIds),
        "bench" => Set(condition, snapshot.BenchCharacterIds),
        "shop" => Set(condition, snapshot.ShopCharacterIds),
        "lineup" => Set(condition, snapshot.LineupIds),
        "synergy" => Set(condition, snapshot.SynergyIds),
        "investment_strategy" => Set(
            condition,
            snapshot.InvestmentStrategyIds),
        "equipment" => Set(condition, snapshot.EquipmentIds),
        "special_item" => Set(condition, snapshot.SpecialItemIds),
        "expert_advisor" => Set(condition, snapshot.ExpertAdvisorIds),
        "enemy" => Set(condition, snapshot.EnemyIds),
        "cumulative_spend" => Number(condition, snapshot.CumulativeSpend),
        "economy" => Number(condition, snapshot.Economy),
        "health" => Number(condition, snapshot.Health),
        "action_points" => Number(condition, snapshot.ActionPoints),
        "damage" => Number(condition, snapshot.CurrentNodeDamage),
        _ => Unknown(condition, $"unsupported condition field '{condition.Field}'")
    };

    private static ConditionEvaluation Scalar(
        GuideCondition condition,
        Observation<string> observed)
    {
        if (observed.Status != ObservationStatus.Known)
        {
            return Unknown(condition, $"{condition.Field} is {observed.Status}");
        }

        var result = condition.Operator.ToLowerInvariant() switch
        {
            "equals" => condition.ExpectedValues.Any(value =>
                string.Equals(value, observed.Value, StringComparison.OrdinalIgnoreCase)),
            "not_equals" => condition.ExpectedValues.All(value =>
                !string.Equals(value, observed.Value, StringComparison.OrdinalIgnoreCase)),
            "starts_with" => condition.ExpectedValues.Any(value =>
                observed.Value!.StartsWith(value, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
        return Boolean(condition, result, $"{condition.Field}='{observed.Value}'");
    }

    private static ConditionEvaluation Set(
        GuideCondition condition,
        Observation<IReadOnlyList<string>> observed)
    {
        if (observed.Status != ObservationStatus.Known || observed.Value is null)
        {
            return Unknown(condition, $"{condition.Field} is {observed.Status}");
        }

        var actual = observed.Value.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = condition.Operator.ToLowerInvariant() switch
        {
            "contains_any" => condition.ExpectedValues.Any(actual.Contains),
            "contains_all" => condition.ExpectedValues.All(actual.Contains),
            "contains_none" => condition.ExpectedValues.All(value => !actual.Contains(value)),
            _ => false
        };
        return Boolean(
            condition,
            result,
            $"{condition.Field} contains [{string.Join(", ", observed.Value)}]");
    }

    private static ConditionEvaluation Number(
        GuideCondition condition,
        Observation<int> observed) => NumberCore<int>(
            condition,
            observed.Status,
            observed.Value);

    private static ConditionEvaluation Number(
        GuideCondition condition,
        Observation<long> observed) => NumberCore<long>(
            condition,
            observed.Status,
            observed.Value);

    private static ConditionEvaluation NumberCore<T>(
        GuideCondition condition,
        ObservationStatus status,
        T? actual)
        where T : struct, IConvertible
    {
        if (status != ObservationStatus.Known || actual is null)
        {
            return Unknown(condition, $"{condition.Field} is {status}");
        }

        var expected = condition.ExpectedValues
            .Select(value => decimal.TryParse(value, out var parsed)
                ? parsed
                : (decimal?)null)
            .FirstOrDefault(value => value.HasValue);
        if (!expected.HasValue)
        {
            return Unknown(
                condition,
                $"{condition.Field} has no numeric expected value");
        }

        var actualNumber = Convert.ToDecimal(actual);
        var result = condition.Operator.ToLowerInvariant() switch
        {
            "gte" => actualNumber >= expected.Value,
            "lte" => actualNumber <= expected.Value,
            "equals" => actualNumber == expected.Value,
            _ => false
        };
        return Boolean(
            condition,
            result,
            $"{condition.Field}={actualNumber}, expected " +
            $"{condition.Operator} {expected.Value}");
    }

    private static ConditionEvaluation Boolean(
        GuideCondition condition,
        bool value,
        string details) => new(
            value ? TriState.True : TriState.False,
            condition.UnknownPolicy,
            details,
            condition.Field);

    private static ConditionEvaluation Unknown(
        GuideCondition condition,
        string details) => new(
            TriState.Unknown,
            condition.UnknownPolicy,
            details,
            condition.Field);
}

public sealed class AdvisorEngine
{
    private readonly ConditionEvaluator _conditions = new();

    public AdviceResult Evaluate(
        RunSnapshot snapshot,
        IEnumerable<GuidePlaybook> playbooks,
        AdvisorSelection selection)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(playbooks);
        ArgumentNullException.ThrowIfNull(selection);

        var all = playbooks.ToArray();
        var warnings = new List<string>();
        var candidates = selection.Mode switch
        {
            AdvisorMode.GuideLocked => all.Where(guide => string.Equals(
                guide.GuideId,
                selection.LockedGuideId,
                StringComparison.OrdinalIgnoreCase)).ToArray(),
            AdvisorMode.ArchetypeLocked => all.Where(guide => string.Equals(
                guide.ArchetypeId,
                selection.LockedArchetypeId,
                StringComparison.OrdinalIgnoreCase)).ToArray(),
            _ => all
        };

        if (selection.Mode == AdvisorMode.GuideLocked && candidates.Length == 0)
        {
            warnings.Add("锁定攻略不存在；系统没有自动切换到其他攻略。");
        }
        if (selection.Mode == AdvisorMode.ArchetypeLocked && candidates.Length == 0)
        {
            warnings.Add("锁定流派不存在；系统没有越界推荐其他流派。");
        }

        var matches = candidates
            .Select(guide => Match(snapshot, guide, selection))
            .OrderByDescending(match => match.Eligible)
            .ThenByDescending(match => match.Score)
            .ThenBy(match => match.GuideId, StringComparer.Ordinal)
            .ToArray();
        if (selection.Mode != AdvisorMode.Auto && matches.All(match => !match.Eligible))
        {
            warnings.Add("锁定路线的条件已失效；保持锁定并等待用户确认，不暗中换线。");
        }

        return new AdviceResult(
            matches,
            Recommend(snapshot, all, matches, warnings),
            warnings);
    }

    private GuideMatch Match(
        RunSnapshot snapshot,
        GuidePlaybook guide,
        AdvisorSelection selection)
    {
        if (!guide.GoalIds.Contains(selection.GoalId, StringComparer.OrdinalIgnoreCase))
        {
            return Ineligible(guide, "目标模式不适用");
        }
        if (!VersionCompatible(
                guide.ApplicableGameVersion,
                selection.GameVersion,
                out var versionGap))
        {
            return Ineligible(guide, "游戏版本不兼容");
        }

        foreach (var prohibited in guide.ProhibitedConditions)
        {
            if (_conditions.Evaluate(prohibited, snapshot).Result == TriState.True)
            {
                return Ineligible(guide, "命中攻略禁止条件");
            }
        }

        var components = new List<ScoreComponent>
        {
            new("eligible", 25, 1, "通过目标和版本硬过滤"),
            new(
                "version",
                versionGap == 0 ? 10 : -Math.Min(15, versionGap * 5),
                1,
                versionGap == 0 ? "攻略与当前版本一致" : "攻略版本较旧")
        };
        var missing = new List<string>();
        ScoreSignals(snapshot, guide, components, missing);

        var bestRuleScore = double.NegativeInfinity;
        string? bestRuleExplanation = null;
        foreach (var rule in guide.Rules)
        {
            var evaluations = rule.Conditions
                .Select(condition => _conditions.Evaluate(condition, snapshot))
                .ToArray();
            var score = evaluations.Sum(evaluation => evaluation.Result switch
            {
                TriState.True => 8,
                TriState.False => -10,
                TriState.Unknown when evaluation.UnknownPolicy == UnknownPolicy.Reject => -12,
                TriState.Unknown => -3,
                _ => 0
            });
            if (score > bestRuleScore)
            {
                bestRuleScore = score;
                bestRuleExplanation = rule.Title;
            }
            missing.AddRange(evaluations
                .Where(evaluation => evaluation.Result == TriState.Unknown)
                .Select(evaluation => evaluation.Field));
        }
        components.Add(new(
            "best_rule",
            double.IsNegativeInfinity(bestRuleScore) ? -10 : bestRuleScore,
            1,
            bestRuleExplanation ?? "攻略没有规则"));

        var scoreTotal = Math.Clamp(
            components.Sum(component => component.Score * component.Weight),
            0,
            100);
        var knownSignalCount = CountKnownSignals(snapshot, guide);
        var signalCount = Math.Max(
            1,
            guide.Signals.CoreCharacterIds.Count +
            guide.Signals.OptionalCharacterIds.Count +
            guide.Signals.SynergyIds.Count);
        return new GuideMatch
        {
            GuideId = guide.GuideId,
            ArchetypeId = guide.ArchetypeId,
            ArchetypeName = guide.ArchetypeName,
            Eligible = true,
            Score = scoreTotal,
            Confidence = Math.Clamp(knownSignalCount / (double)signalCount, 0.15, 1),
            Components = components,
            MissingInformation = missing.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static void ScoreSignals(
        RunSnapshot snapshot,
        GuidePlaybook guide,
        ICollection<ScoreComponent> components,
        ICollection<string> missing)
    {
        var lineup = snapshot.LineupIds.Status == ObservationStatus.Known
            ? snapshot.LineupIds.Value?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var synergies = snapshot.SynergyIds.Status == ObservationStatus.Known
            ? snapshot.SynergyIds.Value?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        if (lineup is null)
        {
            missing.Add("lineup");
            components.Add(new("lineup", -8, 1, "阵容未知"));
        }
        else
        {
            var core = guide.Signals.CoreCharacterIds.Count(lineup.Contains);
            var optional = guide.Signals.OptionalCharacterIds.Count(lineup.Contains);
            components.Add(new(
                "lineup",
                core * 18 + optional * 6,
                1,
                $"命中核心角色 {core} 个、可选角色 {optional} 个"));
        }

        if (synergies is null)
        {
            missing.Add("synergy");
            components.Add(new("synergy", -5, 1, "羁绊未知"));
        }
        else
        {
            var count = guide.Signals.SynergyIds.Count(synergies.Contains);
            components.Add(new("synergy", count * 12, 1, $"命中目标羁绊 {count} 个"));
        }
    }

    private IReadOnlyList<Recommendation> Recommend(
        RunSnapshot snapshot,
        IReadOnlyList<GuidePlaybook> playbooks,
        IReadOnlyList<GuideMatch> matches,
        IReadOnlyList<string> globalWarnings)
    {
        if (snapshot.PageId.Status != ObservationStatus.Known ||
            snapshot.Stage.Status != ObservationStatus.Known)
        {
            return [NoAction(
                "页面或阶段尚未可靠识别，暂不生成操作建议。",
                ["page", "stage"])];
        }

        var recommendations = new List<Recommendation>();
        foreach (var match in matches.Where(match => match.Eligible).Take(3))
        {
            var guide = playbooks.Single(value => value.GuideId == match.GuideId);
            foreach (var rule in guide.Rules)
            {
                var evaluations = rule.Conditions
                    .Select(condition => _conditions.Evaluate(condition, snapshot))
                    .ToArray();
                if (evaluations.Any(evaluation => evaluation.Result == TriState.False) ||
                    evaluations.Any(evaluation =>
                        evaluation.Result == TriState.Unknown &&
                        evaluation.UnknownPolicy == UnknownPolicy.Reject))
                {
                    continue;
                }

                var missing = evaluations
                    .Where(evaluation => evaluation.Result == TriState.Unknown)
                    .Select(evaluation => evaluation.Field)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                recommendations.Add(new Recommendation
                {
                    RecommendationId = $"{guide.GuideId}:{rule.RuleId}",
                    GuideId = guide.GuideId,
                    Priority = recommendations.Count + 1,
                    Action = rule.Action,
                    IsNoAction = false,
                    Confidence = Math.Clamp(
                        (match.Score / 100d) * match.Confidence - missing.Length * 0.04,
                        0.05,
                        0.99),
                    Reasons = [rule.Title, .. evaluations.Select(value => value.Explanation)],
                    Benefits = rule.Benefits,
                    Costs = rule.Costs,
                    Risks = rule.Risks,
                    Preconditions = rule.Preconditions,
                    InvalidatesWhen = rule.InvalidatesWhen,
                    MissingInformation = missing,
                    Sources = rule.Sources
                });
                if (recommendations.Count == 3)
                {
                    return recommendations;
                }
            }
        }

        return recommendations.Count > 0
            ? recommendations
            : [NoAction(
                globalWarnings.FirstOrDefault() ??
                "当前证据不足以支持攻略中的具体动作。",
                matches.SelectMany(match => match.MissingInformation)
                    .Distinct(StringComparer.Ordinal))];
    }

    private static int CountKnownSignals(
        RunSnapshot snapshot,
        GuidePlaybook guide)
    {
        var lineup = snapshot.LineupIds.Value ?? [];
        var synergies = snapshot.SynergyIds.Value ?? [];
        return guide.Signals.CoreCharacterIds.Count(id => lineup.Contains(
                   id,
                   StringComparer.OrdinalIgnoreCase)) +
               guide.Signals.OptionalCharacterIds.Count(id => lineup.Contains(
                   id,
                   StringComparer.OrdinalIgnoreCase)) +
               guide.Signals.SynergyIds.Count(id => synergies.Contains(
                   id,
                   StringComparer.OrdinalIgnoreCase));
    }

    private static Recommendation NoAction(
        string reason,
        IEnumerable<string> missing) => new()
    {
        RecommendationId = "no-action",
        GuideId = "none",
        Priority = 1,
        Action = "暂不建议操作",
        IsNoAction = true,
        Confidence = 1,
        Reasons = [reason],
        MissingInformation = missing.Distinct(StringComparer.Ordinal).ToArray(),
        Risks = ["在证据不足时操作可能造成不可逆的资源损失。"],
        InvalidatesWhen = ["获得新的可靠识别证据。"]
    };

    private static GuideMatch Ineligible(
        GuidePlaybook guide,
        string warning) => new()
    {
        GuideId = guide.GuideId,
        ArchetypeId = guide.ArchetypeId,
        ArchetypeName = guide.ArchetypeName,
        Eligible = false,
        Score = 0,
        Confidence = 1,
        Warnings = [warning]
    };

    private static bool VersionCompatible(
        string guideVersion,
        string currentVersion,
        out int gap)
    {
        gap = 0;
        if (!Version.TryParse(guideVersion, out var guide) ||
            !Version.TryParse(currentVersion, out var current) ||
            guide.Major != current.Major ||
            guide > current)
        {
            return false;
        }

        gap = Math.Max(0, current.Minor - guide.Minor);
        return true;
    }
}
