namespace CurrencyWarsAssistant.Game;

/// <summary>
/// The user's desired treatment of an opening item or a multi-item combination.
/// </summary>
public enum OpeningFilterState
{
    Ignore,
    Require,
    Reject
}

public enum OpeningConditionKind
{
    InvestmentEnvironment,
    Competitor,
    EnemyModifier,
    Combination,
    Profile
}

/// <summary>
/// A recognition-independent snapshot. Creating this record means the caller
/// considers the three recognition stages complete enough to evaluate.
/// </summary>
public sealed record OpeningSnapshot(
    IReadOnlyList<string> InvestmentEnvironmentIds,
    IReadOnlyList<string> CompetitorIds,
    IReadOnlyList<string> EnemyModifierIds);

public sealed record OpeningItemFilter(
    string Id,
    string DisplayName,
    OpeningFilterState State);

/// <summary>
/// A combination is hit only when every configured ID in all three categories
/// is present in the snapshot.
/// </summary>
public sealed class OpeningCombinationFilter
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public OpeningFilterState State { get; init; }
    public IReadOnlyList<string> InvestmentEnvironmentIds { get; init; } = [];
    public IReadOnlyList<string> CompetitorIds { get; init; } = [];
    public IReadOnlyList<string> EnemyModifierIds { get; init; } = [];
}

/// <summary>
/// One complete user plan.  Categories are ANDed; positive choices inside a
/// category are ORed; rejected choices must all be absent.  Multiple enabled
/// profiles are ORed by <see cref="OpeningFilterEvaluator"/>.
/// </summary>
public sealed class OpeningFilterProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; } = true;
    public IReadOnlyList<string> AcceptedInvestmentEnvironmentIds
    {
        get;
        init;
    } = [];
    public IReadOnlyList<string> RequiredCompetitorIds { get; init; } = [];
    public IReadOnlyList<string> RejectedCompetitorIds { get; init; } = [];
    public IReadOnlyList<string> RequiredEnemyModifierIds { get; init; } = [];
    public IReadOnlyList<string> RejectedEnemyModifierIds { get; init; } = [];
    public IReadOnlyList<string> PreferredInvestmentStrategyIds
    {
        get;
        init;
    } = [];
}

public sealed class OpeningFilterSet
{
    public IReadOnlyList<OpeningItemFilter> InvestmentEnvironments { get; init; } = [];
    public IReadOnlyList<OpeningItemFilter> Competitors { get; init; } = [];
    public IReadOnlyList<OpeningItemFilter> EnemyModifiers { get; init; } = [];
    public IReadOnlyList<OpeningCombinationFilter> Combinations { get; init; } = [];
    public IReadOnlyList<OpeningFilterProfile> Profiles { get; init; } = [];
}

public sealed record OpeningConditionOutcome(
    string Id,
    string DisplayName,
    OpeningConditionKind Kind,
    OpeningFilterState State,
    string Reason);

public sealed record OpeningFilterEvaluation(
    bool Matched,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<OpeningConditionOutcome> MatchedConditions,
    IReadOnlyList<OpeningConditionOutcome> ViolatedConditions,
    IReadOnlyList<string>? MatchedProfileIds = null)
{
    public IReadOnlyList<string> EffectiveMatchedProfileIds =>
        MatchedProfileIds ?? [];
}

public sealed class OpeningFilterEvaluator
{
    public OpeningFilterEvaluation Evaluate(
        OpeningSnapshot snapshot,
        OpeningFilterSet filters)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filters);

        var environments = ToSet(snapshot.InvestmentEnvironmentIds);
        var competitors = ToSet(snapshot.CompetitorIds);
        var modifiers = ToSet(snapshot.EnemyModifierIds);
        var matched = new List<OpeningConditionOutcome>();
        var violated = new List<OpeningConditionOutcome>();

        EvaluatePreferredInvestmentEnvironments(
            filters.InvestmentEnvironments,
            environments,
            matched,
            violated);
        EvaluateItems(
            filters.Competitors,
            competitors,
            OpeningConditionKind.Competitor,
            "竞争对手",
            matched,
            violated);
        EvaluateItems(
            filters.EnemyModifiers,
            modifiers,
            OpeningConditionKind.EnemyModifier,
            "敌人词条",
            matched,
            violated);

        foreach (var combination in filters.Combinations)
        {
            if (combination.State == OpeningFilterState.Ignore)
            {
                continue;
            }

            EnsureCombinationHasItems(combination);
            var isHit =
                IsSubset(combination.InvestmentEnvironmentIds, environments) &&
                IsSubset(combination.CompetitorIds, competitors) &&
                IsSubset(combination.EnemyModifierIds, modifiers);
            var isSatisfied = combination.State switch
            {
                OpeningFilterState.Require => isHit,
                OpeningFilterState.Reject => !isHit,
                _ => true
            };
            var reason = CombinationReason(combination, isSatisfied);
            var outcome = new OpeningConditionOutcome(
                combination.Id,
                combination.DisplayName,
                OpeningConditionKind.Combination,
                combination.State,
                reason);
            (isSatisfied ? matched : violated).Add(outcome);
        }

        var enabledProfiles = filters.Profiles
            .Where(profile => profile.IsEnabled)
            .ToArray();
        var matchedProfileIds = new List<string>();
        if (enabledProfiles.Length > 0)
        {
            foreach (var profile in enabledProfiles)
            {
                var profileMatched = ProfileMatches(
                    profile,
                    environments,
                    competitors,
                    modifiers);
                var reason = profileMatched
                    ? $"已命中刷取方案：{profile.DisplayName}"
                    : $"未完整命中刷取方案：{profile.DisplayName}";
                var outcome = new OpeningConditionOutcome(
                    profile.Id,
                    profile.DisplayName,
                    OpeningConditionKind.Profile,
                    OpeningFilterState.Require,
                    reason);
                if (profileMatched)
                {
                    matched.Add(outcome);
                    matchedProfileIds.Add(profile.Id);
                }
            }

            if (matchedProfileIds.Count == 0)
            {
                violated.Add(new OpeningConditionOutcome(
                    "opening_profiles",
                    "刷取方案",
                    OpeningConditionKind.Profile,
                    OpeningFilterState.Require,
                    "当前开局没有完整命中任何一组启用的刷取方案"));
            }
        }

        var isMatch = violated.Count == 0;
        IReadOnlyList<string> reasons;
        if (!isMatch)
        {
            reasons = violated.Select(value => value.Reason).ToArray();
        }
        else if (matched.Count > 0)
        {
            reasons = matched.Select(value => value.Reason).ToArray();
        }
        else
        {
            reasons = ["未设置有效筛选条件，当前完整开局符合要求"];
        }

        return new OpeningFilterEvaluation(
            isMatch,
            reasons,
            matched,
            violated,
            matchedProfileIds);
    }

    private static bool ProfileMatches(
        OpeningFilterProfile profile,
        IReadOnlySet<string> environments,
        IReadOnlySet<string> competitors,
        IReadOnlySet<string> modifiers) =>
        IntersectsOrUnrestricted(
            profile.AcceptedInvestmentEnvironmentIds,
            environments) &&
        IntersectsOrUnrestricted(
            profile.RequiredCompetitorIds,
            competitors) &&
        !profile.RejectedCompetitorIds.Any(competitors.Contains) &&
        IntersectsOrUnrestricted(
            profile.RequiredEnemyModifierIds,
            modifiers) &&
        !profile.RejectedEnemyModifierIds.Any(modifiers.Contains);

    private static bool IntersectsOrUnrestricted(
        IReadOnlyList<string> configured,
        IReadOnlySet<string> observed) =>
        configured.Count == 0 || configured.Any(observed.Contains);

    private static void EvaluateItems(
        IReadOnlyList<OpeningItemFilter> filters,
        IReadOnlySet<string> observedIds,
        OpeningConditionKind kind,
        string categoryName,
        ICollection<OpeningConditionOutcome> matched,
        ICollection<OpeningConditionOutcome> violated)
    {
        foreach (var filter in filters)
        {
            if (filter.State == OpeningFilterState.Ignore)
            {
                continue;
            }

            var isPresent = observedIds.Contains(filter.Id);
            var isSatisfied = filter.State switch
            {
                OpeningFilterState.Require => isPresent,
                OpeningFilterState.Reject => !isPresent,
                _ => true
            };
            var reason = ItemReason(filter, categoryName, isSatisfied);
            var outcome = new OpeningConditionOutcome(
                filter.Id,
                filter.DisplayName,
                kind,
                filter.State,
                reason);
            (isSatisfied ? matched : violated).Add(outcome);
        }
    }

    private static void EvaluatePreferredInvestmentEnvironments(
        IReadOnlyList<OpeningItemFilter> filters,
        IReadOnlySet<string> observedIds,
        ICollection<OpeningConditionOutcome> matched,
        ICollection<OpeningConditionOutcome> violated)
    {
        var forbidden = filters
            .Where(value => value.State == OpeningFilterState.Reject)
            .ToArray();
        if (forbidden.Length > 0)
        {
            throw new ArgumentException(
                "投资环境只支持正选，不能配置为排除。",
                nameof(filters));
        }

        var preferred = filters
            .Where(value => value.State == OpeningFilterState.Require)
            .ToArray();
        if (preferred.Length == 0)
        {
            return;
        }

        var offered = preferred
            .Where(value => observedIds.Contains(value.Id))
            .ToArray();
        var isSatisfied = offered.Length > 0;
        var preferredNames = string.Join(
            "、",
            preferred.Select(value => value.DisplayName));
        var reason = isSatisfied
            ? $"三个候选中出现可接受投资环境：{string.Join("、", offered.Select(value => value.DisplayName))}"
            : $"三个候选均不在可接受投资环境列表：{preferredNames}";
        var outcome = new OpeningConditionOutcome(
            "preferred_investment_environments",
            preferredNames,
            OpeningConditionKind.InvestmentEnvironment,
            OpeningFilterState.Require,
            reason);
        (isSatisfied ? matched : violated).Add(outcome);
    }

    private static string ItemReason(
        OpeningItemFilter filter,
        string categoryName,
        bool isSatisfied) =>
        (filter.State, isSatisfied) switch
        {
            (OpeningFilterState.Require, true) =>
                $"已命中必选{categoryName}：{filter.DisplayName}",
            (OpeningFilterState.Require, false) =>
                $"缺少必选{categoryName}：{filter.DisplayName}",
            (OpeningFilterState.Reject, true) =>
                $"未出现排除{categoryName}：{filter.DisplayName}",
            (OpeningFilterState.Reject, false) =>
                $"命中排除{categoryName}：{filter.DisplayName}",
            _ => string.Empty
        };

    private static string CombinationReason(
        OpeningCombinationFilter filter,
        bool isSatisfied) =>
        (filter.State, isSatisfied) switch
        {
            (OpeningFilterState.Require, true) =>
                $"已完整命中必选组合：{filter.DisplayName}",
            (OpeningFilterState.Require, false) =>
                $"未完整命中必选组合：{filter.DisplayName}",
            (OpeningFilterState.Reject, true) =>
                $"未完整命中排除组合：{filter.DisplayName}",
            (OpeningFilterState.Reject, false) =>
                $"完整命中排除组合：{filter.DisplayName}",
            _ => string.Empty
        };

    private static IReadOnlySet<string> ToSet(IEnumerable<string> values) =>
        values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsSubset(
        IEnumerable<string> required,
        IReadOnlySet<string> observed) =>
        required.All(observed.Contains);

    private static void EnsureCombinationHasItems(OpeningCombinationFilter filter)
    {
        if (filter.InvestmentEnvironmentIds.Count == 0 &&
            filter.CompetitorIds.Count == 0 &&
            filter.EnemyModifierIds.Count == 0)
        {
            throw new ArgumentException(
                $"启用的组合筛选“{filter.DisplayName}”至少需要包含一个项目。",
                nameof(filter));
        }
    }
}
