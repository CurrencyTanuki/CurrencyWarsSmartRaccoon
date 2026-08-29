using System.Globalization;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

internal sealed record PreparationEconomyCandidate(
    string Source,
    int Value,
    double Confidence,
    int DigitCount);

internal sealed record PreparationEconomyResolution(
    int Value,
    double Confidence,
    string Rule);

/// <summary>
/// Resolves the overlapping economy crops without treating either crop as
/// inherently trustworthy. A value is emitted only when the candidates supply
/// positive structural evidence or a strict majority of independent readers.
/// </summary>
internal static class PreparationEconomyCandidateResolver
{
    internal const string CompleteTemplateRule = "complete-template";
    internal const string CompleteGlyphContainmentRule =
        "complete-glyph-containment";
    internal const string StrictMajorityRule = "strict-independent-majority";

    internal static PreparationEconomyResolution? ResolveTemplateCandidates(
        IEnumerable<PreparationEconomyCandidate> source)
    {
        var candidates = source.ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (candidates.Select(item => item.Value).Distinct().Count() == 1 &&
            candidates.Any(item => item.DigitCount >= 2))
        {
            var selected = candidates
                .OrderByDescending(item => item.Confidence)
                .First();
            return new PreparationEconomyResolution(
                selected.Value,
                Math.Min(0.72, selected.Confidence),
                CompleteTemplateRule);
        }

        var glyphRanked = candidates
            .OrderByDescending(item => item.DigitCount)
            .ThenByDescending(item => item.Confidence)
            .ToArray();
        if (glyphRanked.Length < 2 ||
            glyphRanked[0].DigitCount <= glyphRanked[1].DigitCount)
        {
            return null;
        }

        var complete = glyphRanked[0].Value.ToString(CultureInfo.InvariantCulture);
        var clipped = glyphRanked[1].Value.ToString(CultureInfo.InvariantCulture);
        if (!complete.EndsWith(clipped, StringComparison.Ordinal) &&
            !complete.StartsWith(clipped, StringComparison.Ordinal))
        {
            return null;
        }

        return new PreparationEconomyResolution(
            glyphRanked[0].Value,
            Math.Min(
                0.70,
                Math.Max(glyphRanked[0].Confidence, glyphRanked[1].Confidence)),
            CompleteGlyphContainmentRule);
    }

    internal static PreparationEconomyResolution? ResolveStrictMajority(
        IEnumerable<PreparationEconomyCandidate> source)
    {
        var ranked = source
            .GroupBy(item => item.Value)
            .Select(group => new
            {
                Value = group.Key,
                Votes = group.Select(item => item.Source).Distinct().Count(),
                Confidence = group.Max(item => item.Confidence)
            })
            .OrderByDescending(item => item.Votes)
            .ThenByDescending(item => item.Confidence)
            .ToArray();
        if (ranked.Length == 0 ||
            (ranked.Length > 1 && ranked[0].Votes <= ranked[1].Votes))
        {
            return null;
        }

        return new PreparationEconomyResolution(
            ranked[0].Value,
            Math.Min(0.72, ranked[0].Confidence),
            StrictMajorityRule);
    }
}

/// <summary>
/// Maintains one accepted economy value per run/node. A different recognized
/// value must be repeated by two consecutive sampled frames before it replaces
/// that accepted value. Frames which intentionally skip economy OCR do not
/// count as samples; a confirmed node change resets all pending state.
/// </summary>
internal sealed class PreparationEconomyStabilizer
{
    private readonly object _sync = new();
    private readonly Dictionary<string, NodeEconomyState> _states =
        new(StringComparer.Ordinal);

    internal Observation<int> Observe(
        string runId,
        Observation<string> node,
        Observation<int> current,
        bool wasSampled,
        DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(current);

        lock (_sync)
        {
            if (!_states.TryGetValue(runId, out var state))
            {
                if (node.Status != ObservationStatus.Known ||
                    string.IsNullOrWhiteSpace(node.Value))
                {
                    return current;
                }

                state = new NodeEconomyState(node.Value);
                _states.Add(runId, state);
            }
            else if (node.Status == ObservationStatus.Known &&
                     !string.IsNullOrWhiteSpace(node.Value) &&
                     !string.Equals(
                         state.NodeId,
                         node.Value,
                         StringComparison.OrdinalIgnoreCase))
            {
                state = new NodeEconomyState(node.Value);
                _states[runId] = state;
            }
            else if (node.Status == ObservationStatus.Stale &&
                     !string.IsNullOrWhiteSpace(node.Value) &&
                     !string.Equals(
                         state.NodeId,
                         node.Value,
                         StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
            else if (node.Status is not (
                         ObservationStatus.Known or ObservationStatus.Stale))
            {
                return current;
            }

            if (current.Status != ObservationStatus.Known)
            {
                if (wasSampled)
                {
                    state.ClearPending();
                }

                return state.Accepted is null
                    ? current
                    : RetainAccepted(
                        state.Accepted,
                        current,
                        observedAt,
                        wasSampled
                            ? "当前金币采样没有形成可靠结论；沿用当前节点已确认值。"
                            : "当前帧未执行金币识别；沿用当前节点已确认值。");
            }

            if (state.Accepted is null)
            {
                state.Accepted = current;
                state.ClearPending();
                return current;
            }

            if (state.Accepted.Value == current.Value)
            {
                state.Accepted = current;
                state.ClearPending();
                return current;
            }

            if (state.PendingValue == current.Value)
            {
                state.PendingCount++;
            }
            else
            {
                state.PendingValue = current.Value;
                state.PendingCount = 1;
            }

            if (state.PendingCount < 2)
            {
                return RetainAccepted(
                    state.Accepted,
                    current,
                    observedAt,
                    $"新金币候选 {current.Value} 与当前节点已确认值 " +
                    $"{state.Accepted.Value} 不同；等待第 2 个连续采样确认。");
            }

            var confirmed = current with
            {
                Uncertainty = current.Uncertainty
                    .Append("不同金币值已由 2 个连续采样确认。")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
            state.Accepted = confirmed;
            state.ClearPending();
            return confirmed;
        }
    }

    private static Observation<int> RetainAccepted(
        Observation<int> accepted,
        Observation<int> current,
        DateTimeOffset observedAt,
        string reason) =>
        accepted with
        {
            Evidence = accepted.Evidence
                .Concat(current.Evidence)
                .Distinct()
                .ToArray(),
            Uncertainty = accepted.Uncertainty
                .Concat(current.Uncertainty)
                .Append(reason)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            ObservedAt = observedAt
        };

    private sealed class NodeEconomyState(string nodeId)
    {
        internal string NodeId { get; } = nodeId;
        internal Observation<int>? Accepted { get; set; }
        internal int? PendingValue { get; set; }
        internal int PendingCount { get; set; }

        internal void ClearPending()
        {
            PendingValue = null;
            PendingCount = 0;
        }
    }
}
