namespace CurrencyWarsAssistant.Game;

public sealed record ObservedItem(
    string Id,
    string DisplayName,
    double Confidence);

public sealed record OpeningObservation(
    ObservedItem? InvestmentEnvironment,
    IReadOnlyList<ObservedItem> Competitors,
    IReadOnlyList<ObservedItem> EnemyModifiers,
    double PageConfidence,
    DateTimeOffset CapturedAt)
{
    public IReadOnlySet<string> CompetitorIds =>
        Competitors.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> ModifierIds =>
        EnemyModifiers.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public enum OpeningDecisionKind
{
    Keep,
    Reroll,
    Review
}

public sealed record OpeningDecision(
    OpeningDecisionKind Kind,
    IReadOnlyList<string> Reasons);
