using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// Accumulates OCR evidence independently for every fixed screen slot. A slot is
/// considered stable after the same catalog item has been recognized at least
/// twice and is not tied with another candidate.
/// </summary>
public sealed class OpeningRecognitionAccumulator(int expectedSlotCount)
{
    private readonly Dictionary<int, Dictionary<string, Vote>> _votes = [];

    public int ExpectedSlotCount { get; } = expectedSlotCount > 0
        ? expectedSlotCount
        : throw new ArgumentOutOfRangeException(nameof(expectedSlotCount));

    public int ConfirmedSlotCount =>
        Enumerable.Range(0, ExpectedSlotCount).Count(TryGetWinner);

    public void Observe(IEnumerable<RecognizedOpeningItem> observations)
    {
        foreach (var observation in observations)
        {
            if (observation.Slot < 0 ||
                observation.Slot >= ExpectedSlotCount ||
                observation.Item is null)
            {
                continue;
            }

            if (!_votes.TryGetValue(observation.Slot, out var slotVotes))
            {
                slotVotes = new Dictionary<string, Vote>(
                    StringComparer.OrdinalIgnoreCase);
                _votes.Add(observation.Slot, slotVotes);
            }

            if (!slotVotes.TryGetValue(observation.Item.Id, out var vote))
            {
                vote = new Vote(
                    observation.Item.Id,
                    observation.Item.DisplayName);
                slotVotes.Add(observation.Item.Id, vote);
            }

            vote.Count++;
            vote.ConfidenceTotal += observation.Item.Confidence;
            vote.LatestRawText = observation.RawText;
        }
    }

    public bool TryBuild(
        out IReadOnlyList<RecognizedOpeningItem> stableObservations)
    {
        var result = new List<RecognizedOpeningItem>(ExpectedSlotCount);
        for (var slot = 0; slot < ExpectedSlotCount; slot++)
        {
            if (!TryGetWinner(slot, out var winner))
            {
                stableObservations = [];
                return false;
            }

            result.Add(new RecognizedOpeningItem(
                slot,
                winner.LatestRawText,
                new ObservedItem(
                    winner.Id,
                    winner.DisplayName,
                    winner.ConfidenceTotal / winner.Count)));
        }

        stableObservations = result;
        return true;
    }

    public IReadOnlyList<RecognizedOpeningItem> BuildBestEffort(
        IEnumerable<RecognizedOpeningItem>? latestObservations = null)
    {
        var latestBySlot = (latestObservations ?? [])
            .Where(item =>
                item.Slot >= 0 &&
                item.Slot < ExpectedSlotCount)
            .GroupBy(item => item.Slot)
            .ToDictionary(group => group.Key, group => group.Last());
        var result = new List<RecognizedOpeningItem>(ExpectedSlotCount);
        for (var slot = 0; slot < ExpectedSlotCount; slot++)
        {
            if (TryGetWinner(slot, out var winner) ||
                TryGetUnopposedBestEffortWinner(slot, out winner))
            {
                result.Add(new RecognizedOpeningItem(
                    slot,
                    winner.LatestRawText,
                    new ObservedItem(
                        winner.Id,
                        winner.DisplayName,
                        winner.ConfidenceTotal / winner.Count)));
                continue;
            }

            result.Add(new RecognizedOpeningItem(
                slot,
                latestBySlot.TryGetValue(slot, out var latest)
                    ? latest.RawText
                    : string.Empty,
                null));
        }

        return result;
    }

    private bool TryGetUnopposedBestEffortWinner(int slot, out Vote winner)
    {
        winner = null!;
        if (!_votes.TryGetValue(slot, out var slotVotes))
        {
            return false;
        }

        var ranked = slotVotes.Values
            .OrderByDescending(value => value.Count)
            .ThenByDescending(value => value.ConfidenceTotal)
            .ToArray();
        if (ranked.Length == 0)
        {
            return false;
        }

        // A complete overview frame can be followed immediately by a page
        // transition. Keep its unopposed catalog match as degraded evidence
        // instead of replacing it with later empty OCR frames. Conflicting
        // candidates remain unresolved.
        if (ranked.Length > 1 && ranked[0].Count == ranked[1].Count)
        {
            return false;
        }

        winner = ranked[0];
        return true;
    }

    private bool TryGetWinner(int slot) => TryGetWinner(slot, out _);

    private bool TryGetWinner(int slot, out Vote winner)
    {
        winner = null!;
        if (!_votes.TryGetValue(slot, out var slotVotes))
        {
            return false;
        }

        var ranked = slotVotes.Values
            .OrderByDescending(value => value.Count)
            .ThenByDescending(value => value.ConfidenceTotal)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Count < 2)
        {
            return false;
        }

        if (ranked.Length > 1 && ranked[0].Count == ranked[1].Count)
        {
            return false;
        }

        winner = ranked[0];
        return true;
    }

    private sealed class Vote(string id, string displayName)
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public int Count { get; set; }
        public double ConfidenceTotal { get; set; }
        public string LatestRawText { get; set; } = "";
    }
}
