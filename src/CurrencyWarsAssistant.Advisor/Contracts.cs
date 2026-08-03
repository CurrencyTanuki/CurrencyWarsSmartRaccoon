using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurrencyWarsAssistant.Advisor;

public static class AdvisorContractVersions
{
    public const string Current = "1.0.0";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservationStatus
{
    Known,
    Unknown,
    Conflict,
    Stale
}

public sealed record EvidenceReference(
    string SourceId,
    string Locator,
    string? Summary = null,
    DateTimeOffset? CapturedAt = null,
    double? Confidence = null);

public sealed record Observation<T>
{
    public required ObservationStatus Status { get; init; }
    public T? Value { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = [];
    public IReadOnlyList<string> Uncertainty { get; init; } = [];
    public DateTimeOffset? ObservedAt { get; init; }

    public static Observation<T> Known(
        T value,
        double confidence,
        IEnumerable<EvidenceReference>? evidence = null,
        DateTimeOffset? observedAt = null) =>
        new()
        {
            Status = ObservationStatus.Known,
            Value = value,
            Confidence = Math.Clamp(confidence, 0, 1),
            Evidence = evidence?.ToArray() ?? [],
            ObservedAt = observedAt
        };

    public static Observation<T> Unknown(
        string reason,
        IEnumerable<EvidenceReference>? evidence = null,
        DateTimeOffset? observedAt = null) =>
        new()
        {
            Status = ObservationStatus.Unknown,
            Confidence = 0,
            Evidence = evidence?.ToArray() ?? [],
            Uncertainty = [reason],
            ObservedAt = observedAt
        };

    public static Observation<T> Conflict(
        IEnumerable<string> reasons,
        IEnumerable<EvidenceReference>? evidence = null,
        DateTimeOffset? observedAt = null) =>
        new()
        {
            Status = ObservationStatus.Conflict,
            Confidence = 0,
            Evidence = evidence?.ToArray() ?? [],
            Uncertainty = reasons.Distinct(StringComparer.Ordinal).ToArray(),
            ObservedAt = observedAt
        };

    public static Observation<T> Stale(
        T? lastValue,
        string reason,
        IEnumerable<EvidenceReference>? evidence = null,
        DateTimeOffset? observedAt = null) =>
        new()
        {
            Status = ObservationStatus.Stale,
            Value = lastValue,
            Confidence = 0,
            Evidence = evidence?.ToArray() ?? [],
            Uncertainty = [reason],
            ObservedAt = observedAt
        };

    public Observation<T> EnsureValid()
    {
        if (Status == ObservationStatus.Known && Value is null)
        {
            throw new InvalidDataException("Known observation must contain a value.");
        }

        if (Status != ObservationStatus.Known && Uncertainty.Count == 0)
        {
            throw new InvalidDataException("Non-known observation must explain its uncertainty.");
        }

        if (Confidence is < 0 or > 1)
        {
            throw new InvalidDataException("Observation confidence must be between 0 and 1.");
        }

        return this;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunEventType
{
    RunStarted,
    PageObserved,
    StageObserved,
    NodeStarted,
    NodeCompleted,
    NodeDamageObserved,
    EconomyObserved,
    CumulativeSpendObserved,
    HealthObserved,
    ActionPointsObserved,
    BoardObserved,
    BenchObserved,
    ShopObserved,
    LineupObserved,
    SynergiesObserved,
    InvestmentEnvironmentObserved,
    InvestmentStrategyObserved,
    EquipmentObserved,
    SpecialItemObserved,
    ExpertAdvisorObserved,
    EnemyObserved,
    RewardObserved,
    RecommendationIssued,
    RunCompleted
}

public sealed record RunEvent
{
    public string SchemaVersion { get; init; } = AdvisorContractVersions.Current;
    public required string EventId { get; init; }
    public required string RunId { get; init; }
    public required RunEventType EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required string SourceAdapter { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyList<string> Uncertainty { get; init; } = [];
    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = [];
    public required JsonElement Payload { get; init; }
}

public sealed record NodeRecord
{
    public string SchemaVersion { get; init; } = AdvisorContractVersions.Current;
    public required string NodeId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public Observation<long> Damage { get; init; } = Observation<long>.Unknown("not observed");
    public Observation<int> Economy { get; init; } = Observation<int>.Unknown("not observed");
    public Observation<int> RemainingActionPoints { get; init; } = Observation<int>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> LineupIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public IReadOnlyList<string> EventIds { get; init; } = [];
}

public sealed record RunSnapshot
{
    public string SchemaVersion { get; init; } = AdvisorContractVersions.Current;
    public required string RunId { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    public Observation<string> PageId { get; init; } = Observation<string>.Unknown("not observed");
    public Observation<string> Stage { get; init; } = Observation<string>.Unknown("not observed");
    public Observation<int> Economy { get; init; } = Observation<int>.Unknown("not observed");
    public Observation<int> CumulativeSpend { get; init; } = Observation<int>.Unknown("history unavailable");
    public Observation<int> Health { get; init; } = Observation<int>.Unknown("not observed");
    public Observation<int> ActionPoints { get; init; } = Observation<int>.Unknown("not observed");
    public Observation<long> CurrentNodeDamage { get; init; } = Observation<long>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> BoardCharacterIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> BenchCharacterIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> ShopCharacterIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> LineupIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> SynergyIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<string> InvestmentEnvironmentId { get; init; } =
        Observation<string>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> InvestmentStrategyIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> EquipmentIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> SpecialItemIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<InventorySlotState>> InventorySlots { get; init; } =
        Observation<IReadOnlyList<InventorySlotState>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> ExpertAdvisorIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public Observation<IReadOnlyList<string>> EnemyIds { get; init; } =
        Observation<IReadOnlyList<string>>.Unknown("not observed");
    public IReadOnlyList<NodeRecord> Nodes { get; init; } = [];
    public IReadOnlyList<string> AppliedEventIds { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriState
{
    True,
    False,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnknownPolicy
{
    Reject,
    AcceptWithPenalty,
    RequireReview
}

public sealed record GuideCondition(
    string Field,
    string Operator,
    IReadOnlyList<string> ExpectedValues,
    UnknownPolicy UnknownPolicy = UnknownPolicy.RequireReview,
    string? SourceId = null,
    string? Locator = null);

public sealed record GuideRule(
    string RuleId,
    string Title,
    string Action,
    IReadOnlyList<GuideCondition> Conditions,
    IReadOnlyList<string> Benefits,
    IReadOnlyList<string> Costs,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> InvalidatesWhen,
    IReadOnlyList<EvidenceReference> Sources);

public sealed record GuideSourceRecord(
    string SourceId,
    string Title,
    string Author,
    string Platform,
    Uri Url,
    DateTimeOffset PublishedAt,
    DateTimeOffset AccessedAt,
    string ContentType,
    string ApplicableGameVersion,
    string CopyrightStatus);

public sealed record ArchetypeSignals(
    IReadOnlyList<string> CoreCharacterIds,
    IReadOnlyList<string> OptionalCharacterIds,
    IReadOnlyList<string> SynergyIds);

public sealed record GuidePlaybook
{
    public string SchemaVersion { get; init; } = AdvisorContractVersions.Current;
    public required string GuideId { get; init; }
    public required string Title { get; init; }
    public required string ArchetypeId { get; init; }
    public required string ArchetypeName { get; init; }
    public required string ApplicableGameVersion { get; init; }
    public required IReadOnlyList<string> GoalIds { get; init; }
    public required ArchetypeSignals Signals { get; init; }
    public IReadOnlyList<GuideCondition> ProhibitedConditions { get; init; } = [];
    public IReadOnlyList<GuideRule> Rules { get; init; } = [];
    public IReadOnlyList<GuideSourceRecord> Sources { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdvisorMode
{
    Auto,
    GuideLocked,
    ArchetypeLocked
}

public sealed record AdvisorSelection(
    AdvisorMode Mode,
    string GoalId,
    string GameVersion,
    string? LockedGuideId = null,
    string? LockedArchetypeId = null);

public sealed record ScoreComponent(
    string Name,
    double Score,
    double Weight,
    string Explanation);

public sealed record GuideMatch
{
    public required string GuideId { get; init; }
    public required string ArchetypeId { get; init; }
    public required string ArchetypeName { get; init; }
    public required bool Eligible { get; init; }
    public required double Score { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyList<ScoreComponent> Components { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> MissingInformation { get; init; } = [];
}

public sealed record Recommendation
{
    public required string RecommendationId { get; init; }
    public required string GuideId { get; init; }
    public required int Priority { get; init; }
    public required string Action { get; init; }
    public required bool IsNoAction { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyList<string> Benefits { get; init; } = [];
    public IReadOnlyList<string> Costs { get; init; } = [];
    public IReadOnlyList<string> Risks { get; init; } = [];
    public IReadOnlyList<string> Preconditions { get; init; } = [];
    public IReadOnlyList<string> InvalidatesWhen { get; init; } = [];
    public IReadOnlyList<string> MissingInformation { get; init; } = [];
    public IReadOnlyList<EvidenceReference> Sources { get; init; } = [];
}

public sealed record AdviceResult(
    IReadOnlyList<GuideMatch> Matches,
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<string> Warnings);

public sealed record ScreenshotAnalysisResult
{
    public string SchemaVersion { get; init; } = AdvisorContractVersions.Current;
    public string? ApplicationVersion { get; init; }
    public required string AnalysisId { get; init; }
    public required RunSnapshot Snapshot { get; init; }
    public IReadOnlyList<GuideMatch> RouteCandidates { get; init; } = [];
    public IReadOnlyList<Recommendation> Recommendations { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> UnknownFields { get; init; } = [];
    public Phase2OperationalState? OperationalState { get; init; }
}
