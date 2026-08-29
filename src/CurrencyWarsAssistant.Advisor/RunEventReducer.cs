using System.Text.Json;

namespace CurrencyWarsAssistant.Advisor;

public sealed record RunReductionResult(
    RunSnapshot Snapshot,
    IReadOnlyList<RunEvent> CanonicalEvents);

public sealed class RunEventReducer
{
    public RunReductionResult Reduce(IEnumerable<RunEvent> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var all = input.ToArray();
        if (all.Length == 0)
        {
            throw new ArgumentException(
                "At least one event is required.",
                nameof(input));
        }

        var runIds = all.Select(value => value.RunId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runIds.Length != 1)
        {
            throw new InvalidDataException(
                "A reduction may contain exactly one runId.");
        }

        var diagnostics = new List<string>();
        var canonical = new List<RunEvent>();
        foreach (var group in all.GroupBy(value => value.EventId, StringComparer.Ordinal))
        {
            var representations = group
                .Select(value => JsonSerializer.Serialize(value, AdvisorJson.Options))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (representations.Length != 1)
            {
                diagnostics.Add(
                    $"eventId {group.Key} has conflicting append-only " +
                    "representations and was ignored.");
                continue;
            }

            canonical.Add(group.First());
        }

        canonical = canonical
            .OrderBy(value => value.OccurredAt)
            .ThenBy(value => value.ObservedAt)
            .ThenBy(value => value.EventId, StringComparer.Ordinal)
            .ToList();

        Observation<string>? page = null;
        Observation<string>? stage = null;
        Observation<int>? economy = null;
        Observation<int>? spend = null;
        Observation<int>? health = null;
        Observation<int>? actionPoints = null;
        Observation<long>? damage = null;
        Observation<IReadOnlyList<string>>? board = null;
        Observation<IReadOnlyList<string>>? bench = null;
        Observation<IReadOnlyList<string>>? shop = null;
        Observation<IReadOnlyList<string>>? lineup = null;
        Observation<IReadOnlyList<string>>? synergies = null;
        Observation<string>? environment = null;
        Observation<IReadOnlyList<string>>? strategies = null;
        Observation<IReadOnlyList<string>>? equipment = null;
        Observation<IReadOnlyList<string>>? specialItems = null;
        Observation<IReadOnlyList<string>>? expertAdvisors = null;
        Observation<IReadOnlyList<string>>? enemies = null;
        var nodes = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        string? activeNodeId = null;

        foreach (var runEvent in canonical)
        {
            Validate(runEvent);
            switch (runEvent.EventType)
            {
                case RunEventType.PageObserved:
                    page = MergeTemporal(page, Read<string>(runEvent));
                    break;
                case RunEventType.StageObserved:
                    stage = MergeTemporal(stage, Read<string>(runEvent));
                    break;
                case RunEventType.EconomyObserved:
                    economy = MergeTemporal(economy, Read<int>(runEvent));
                    UpdateNodeEconomy(nodes, activeNodeId, runEvent);
                    break;
                case RunEventType.CumulativeSpendObserved:
                    spend = MergeTemporal(spend, Read<int>(runEvent));
                    break;
                case RunEventType.HealthObserved:
                    health = MergeTemporal(health, Read<int>(runEvent));
                    break;
                case RunEventType.ActionPointsObserved:
                    actionPoints = MergeTemporal(actionPoints, Read<int>(runEvent));
                    UpdateNodeActionPoints(nodes, activeNodeId, runEvent);
                    break;
                case RunEventType.NodeDamageObserved:
                    damage = MergeTemporal(damage, Read<long>(runEvent));
                    UpdateNodeDamage(nodes, activeNodeId, runEvent, diagnostics);
                    break;
                case RunEventType.BoardObserved:
                    board = MergeTemporal(board, ReadList(runEvent));
                    break;
                case RunEventType.BenchObserved:
                    bench = MergeTemporal(bench, ReadList(runEvent));
                    break;
                case RunEventType.ShopObserved:
                    shop = MergeTemporal(shop, ReadList(runEvent));
                    break;
                case RunEventType.LineupObserved:
                    lineup = MergeTemporal(lineup, ReadList(runEvent));
                    UpdateNodeLineup(nodes, activeNodeId, runEvent);
                    break;
                case RunEventType.SynergiesObserved:
                    synergies = MergeTemporal(synergies, ReadList(runEvent));
                    break;
                case RunEventType.InvestmentEnvironmentObserved:
                    environment = MergeTemporal(environment, Read<string>(runEvent));
                    break;
                case RunEventType.InvestmentStrategyObserved:
                    strategies = MergeTemporal(strategies, ReadList(runEvent));
                    break;
                case RunEventType.EquipmentObserved:
                    equipment = MergeTemporal(equipment, ReadList(runEvent));
                    break;
                case RunEventType.SpecialItemObserved:
                    specialItems = MergeTemporal(specialItems, ReadList(runEvent));
                    break;
                case RunEventType.ExpertAdvisorObserved:
                    expertAdvisors = MergeTemporal(expertAdvisors, ReadList(runEvent));
                    break;
                case RunEventType.EnemyObserved:
                    enemies = MergeTemporal(enemies, ReadList(runEvent));
                    break;
                case RunEventType.NodeStarted:
                    activeNodeId = StartNode(nodes, activeNodeId, runEvent);
                    break;
                case RunEventType.NodeCompleted:
                case RunEventType.RunCompleted:
                    CompleteActiveNode(nodes, activeNodeId, runEvent.OccurredAt);
                    if (runEvent.EventType == RunEventType.NodeCompleted)
                    {
                        activeNodeId = null;
                    }
                    break;
            }
        }

        var snapshot = new RunSnapshot
        {
            RunId = runIds[0],
            AsOf = canonical.Count == 0
                ? all.Max(value => value.ObservedAt)
                : canonical.Max(value => value.ObservedAt),
            PageId = page ?? Observation<string>.Unknown("page not observed"),
            Stage = stage ?? Observation<string>.Unknown("stage not observed"),
            Economy = economy ?? Observation<int>.Unknown("economy not observed"),
            CumulativeSpend = spend ?? Observation<int>.Unknown("history unavailable"),
            Health = health ?? Observation<int>.Unknown("health not observed"),
            ActionPoints = actionPoints ?? Observation<int>.Unknown("action points not observed"),
            CurrentNodeDamage = damage ?? Observation<long>.Unknown("damage not observed"),
            BoardCharacterIds = board ?? Observation<IReadOnlyList<string>>.Unknown("board not observed"),
            BenchCharacterIds = bench ?? Observation<IReadOnlyList<string>>.Unknown("bench not observed"),
            ShopCharacterIds = shop ?? Observation<IReadOnlyList<string>>.Unknown("shop not observed"),
            LineupIds = lineup ?? Observation<IReadOnlyList<string>>.Unknown("lineup not observed"),
            SynergyIds = synergies ?? Observation<IReadOnlyList<string>>.Unknown("synergies not observed"),
            InvestmentEnvironmentId = environment ?? Observation<string>.Unknown("environment not observed"),
            InvestmentStrategyIds = strategies ?? Observation<IReadOnlyList<string>>.Unknown("strategies not observed"),
            EquipmentIds = equipment ?? Observation<IReadOnlyList<string>>.Unknown("equipment not observed"),
            SpecialItemIds = specialItems ?? Observation<IReadOnlyList<string>>.Unknown("special items not observed"),
            ExpertAdvisorIds = expertAdvisors ?? Observation<IReadOnlyList<string>>.Unknown("expert advisors not observed"),
            EnemyIds = enemies ?? Observation<IReadOnlyList<string>>.Unknown("enemies not observed"),
            Nodes = nodes.Values
                .OrderBy(value => value.StartedAt)
                .Select(value => value.ToRecord())
                .ToArray(),
            AppliedEventIds = canonical.Select(value => value.EventId).ToArray(),
            Diagnostics = diagnostics
        };
        return new RunReductionResult(snapshot, canonical);
    }

    private static void Validate(RunEvent value)
    {
        if (value.SchemaVersion != AdvisorContractVersions.Current)
        {
            throw new InvalidDataException(
                $"Unsupported RunEvent schemaVersion: {value.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(value.EventId) ||
            string.IsNullOrWhiteSpace(value.RunId) ||
            value.ObservedAt < value.OccurredAt ||
            value.Confidence is < 0 or > 1)
        {
            throw new InvalidDataException(
                $"RunEvent '{value.EventId}' is invalid.");
        }
    }

    private static Observation<T> Read<T>(RunEvent runEvent)
    {
        var status = ReadStatus(runEvent.Payload);
        var reasons = ReadStrings(runEvent.Payload, "uncertainty")
            .Concat(runEvent.Uncertainty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (status == ObservationStatus.Known)
        {
            if (!runEvent.Payload.TryGetProperty("value", out var value))
            {
                return Observation<T>.Unknown(
                    "known event payload omitted value",
                    runEvent.Evidence,
                    runEvent.ObservedAt);
            }

            var parsed = value.Deserialize<T>(AdvisorJson.Options);
            return parsed is null
                ? Observation<T>.Unknown(
                    "event value could not be parsed",
                    runEvent.Evidence,
                    runEvent.ObservedAt)
                : Observation<T>.Known(
                    parsed,
                    runEvent.Confidence,
                    runEvent.Evidence,
                    runEvent.ObservedAt);
        }

        var explanation = reasons.Length == 0
            ? $"event status is {status}"
            : string.Join("; ", reasons);
        return status switch
        {
            ObservationStatus.Unknown => Observation<T>.Unknown(
                explanation,
                runEvent.Evidence,
                runEvent.ObservedAt),
            ObservationStatus.Conflict => Observation<T>.Conflict(
                reasons.Length == 0 ? [explanation] : reasons,
                runEvent.Evidence,
                runEvent.ObservedAt),
            ObservationStatus.Stale => Observation<T>.Stale(
                default,
                explanation,
                runEvent.Evidence,
                runEvent.ObservedAt),
            _ => throw new InvalidOperationException()
        };
    }

    private static Observation<IReadOnlyList<string>> ReadList(RunEvent value) =>
        Read<IReadOnlyList<string>>(value);

    internal static Observation<T> MergeTemporal<T>(
        Observation<T>? current,
        Observation<T> incoming)
    {
        incoming.EnsureValid();
        if (current is null)
        {
            return incoming;
        }

        var evidence = current.Evidence.Concat(incoming.Evidence).Distinct().ToArray();
        var incomingAt = incoming.ObservedAt ?? DateTimeOffset.MinValue;
        var currentAt = current.ObservedAt ?? DateTimeOffset.MinValue;
        if (incomingAt < currentAt)
        {
            return current;
        }
        if (incomingAt == currentAt &&
            current.Status == ObservationStatus.Known &&
            incoming.Status == ObservationStatus.Known &&
            !string.Equals(
                JsonSerializer.Serialize(current.Value, AdvisorJson.Options),
                JsonSerializer.Serialize(incoming.Value, AdvisorJson.Options),
                StringComparison.Ordinal))
        {
            return Observation<T>.Conflict(
                ["observations with the same timestamp disagree"],
                evidence,
                incoming.ObservedAt);
        }
        if (incoming.Status == ObservationStatus.Unknown &&
            current.Status == ObservationStatus.Known)
        {
            return Observation<T>.Stale(
                current.Value,
                string.Join("; ", incoming.Uncertainty),
                evidence,
                incoming.ObservedAt);
        }

        return incoming with { Evidence = evidence };
    }

    private static ObservationStatus ReadStatus(JsonElement payload) =>
        payload.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        Enum.TryParse<ObservationStatus>(status.GetString(), true, out var parsed)
            ? parsed
            : ObservationStatus.Known;

    private static IReadOnlyList<string> ReadStrings(
        JsonElement payload,
        string name) =>
        payload.TryGetProperty(name, out var values) &&
        values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToArray()
            : [];

    private static string StartNode(
        IDictionary<string, MutableNode> nodes,
        string? activeNodeId,
        RunEvent runEvent)
    {
        var nodeId = RequiredString(runEvent.Payload, "nodeId");
        CompleteActiveNode(nodes, activeNodeId, runEvent.OccurredAt);
        if (!nodes.TryGetValue(nodeId, out var node))
        {
            node = new MutableNode(nodeId, runEvent.OccurredAt);
            nodes.Add(nodeId, node);
        }
        node.EventIds.Add(runEvent.EventId);
        return nodeId;
    }

    private static void CompleteActiveNode(
        IDictionary<string, MutableNode> nodes,
        string? activeNodeId,
        DateTimeOffset endedAt)
    {
        if (activeNodeId is not null &&
            nodes.TryGetValue(activeNodeId, out var node))
        {
            node.EndedAt ??= endedAt;
        }
    }

    private static void UpdateNodeDamage(
        IDictionary<string, MutableNode> nodes,
        string? activeNodeId,
        RunEvent runEvent,
        ICollection<string> diagnostics)
    {
        var nodeId = OptionalString(runEvent.Payload, "nodeId") ?? activeNodeId;
        if (nodeId is null || !nodes.TryGetValue(nodeId, out var node))
        {
            diagnostics.Add(
                $"eventId {runEvent.EventId} has node damage without a known nodeId.");
            return;
        }
        node.Damage = MergeTemporal(node.Damage, Read<long>(runEvent));
        node.EventIds.Add(runEvent.EventId);
    }

    private static void UpdateNodeEconomy(
        IDictionary<string, MutableNode> nodes,
        string? activeNodeId,
        RunEvent runEvent)
    {
        if (activeNodeId is not null && nodes.TryGetValue(activeNodeId, out var node))
        {
            node.Economy = MergeTemporal(node.Economy, Read<int>(runEvent));
            node.EventIds.Add(runEvent.EventId);
        }
    }

    private static void UpdateNodeActionPoints(
        IDictionary<string, MutableNode> nodes,
        string? activeNodeId,
        RunEvent runEvent)
    {
        if (activeNodeId is not null && nodes.TryGetValue(activeNodeId, out var node))
        {
            node.ActionPoints = MergeTemporal(node.ActionPoints, Read<int>(runEvent));
            node.EventIds.Add(runEvent.EventId);
        }
    }

    private static void UpdateNodeLineup(
        IDictionary<string, MutableNode> nodes,
        string? activeNodeId,
        RunEvent runEvent)
    {
        if (activeNodeId is not null && nodes.TryGetValue(activeNodeId, out var node))
        {
            node.Lineup = MergeTemporal(node.Lineup, ReadList(runEvent));
            node.EventIds.Add(runEvent.EventId);
        }
    }

    private static string RequiredString(JsonElement payload, string name) =>
        OptionalString(payload, name) ??
        throw new InvalidDataException($"RunEvent payload requires {name}.");

    private static string? OptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class MutableNode(string nodeId, DateTimeOffset startedAt)
    {
        public string NodeId { get; } = nodeId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset? EndedAt { get; set; }
        public Observation<long>? Damage { get; set; }
        public Observation<int>? Economy { get; set; }
        public Observation<int>? ActionPoints { get; set; }
        public Observation<IReadOnlyList<string>>? Lineup { get; set; }
        public List<string> EventIds { get; } = [];

        public NodeRecord ToRecord() => new()
        {
            NodeId = NodeId,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            Damage = Damage ?? Observation<long>.Unknown("node damage not observed"),
            Economy = Economy ?? Observation<int>.Unknown("node economy not observed"),
            RemainingActionPoints = ActionPoints ?? Observation<int>.Unknown("node action points not observed"),
            LineupIds = Lineup ?? Observation<IReadOnlyList<string>>.Unknown("node lineup not observed"),
            EventIds = EventIds.Distinct(StringComparer.Ordinal).ToArray()
        };
    }
}
