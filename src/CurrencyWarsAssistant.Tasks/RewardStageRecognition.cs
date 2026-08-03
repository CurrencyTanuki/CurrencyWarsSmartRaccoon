using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed record RewardShopSlot(
    int Slot,
    CurrencyWarsCharacterData? Character,
    string RawText,
    double Confidence);

public static class RewardOcrTextVariants
{
    public static IReadOnlyList<string> Expand(OcrTextResult recognized) =>
        recognized.Lines
            .Prepend(recognized.Text)
            .Where(item =>
                GameDataNameMatcher.Normalize(item).Length >= 2)
            .SelectMany(item =>
            {
                var corrected = item
                    .Replace('釒', '金')
                    .Replace('．', '•')
                    .Replace('·', '•');
                return string.Equals(
                        corrected,
                        item,
                        StringComparison.Ordinal)
                    ? [item]
                    : new[] { item, corrected };
            })
            .DistinctBy(GameDataNameMatcher.Normalize)
            .ToArray();
}

internal static class RewardRecognitionSupport
{
    public static NameMatch<T>? FindBest<T>(
        GameDataNameMatcher matcher,
        IEnumerable<string> variants,
        IEnumerable<T> candidates,
        Func<T, string> nameSelector,
        double minimumConfidence) =>
        variants
            .Select(text => matcher.FindBest(
                text,
                candidates,
                nameSelector,
                minimumConfidence))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();

    public static PixelRect Scale(
        PixelRect source,
        CaptureFrame frame) =>
        new(
            (int)Math.Round(source.X * frame.Width / 1920d),
            (int)Math.Round(source.Y * frame.Height / 1080d),
            (int)Math.Round(source.Width * frame.Width / 1920d),
            (int)Math.Round(source.Height * frame.Height / 1080d));
}

public sealed class RewardShopReader(
    IOfflineOcr ocr,
    GameDataCatalog gameData,
    GameDataNameMatcher? matcher = null)
{
    private static readonly IReadOnlyList<PixelRect> NameRegions =
    [
        new(360, 265, 230, 65),
        new(625, 265, 230, 65),
        new(890, 265, 230, 65),
        new(1155, 265, 230, 65),
        new(1420, 265, 230, 65)
    ];

    private readonly GameDataNameMatcher _matcher =
        matcher ?? new GameDataNameMatcher();
    private readonly CurrencyWarsCharacterData[] _characters =
        gameData.CurrencyWarsCharacters.ToArray();
    private readonly string[] _bondNames =
        gameData.CurrencyWarsCharacters
            .SelectMany(item => item.BondNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<IReadOnlyList<RewardShopSlot>> ReadAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        var results = new List<RewardShopSlot>(NameRegions.Count);
        for (var index = 0; index < NameRegions.Count; index++)
        {
            results.Add(await ReadSlotAsync(
                frame,
                index,
                cancellationToken));
        }

        return results;
    }

    private async Task<RewardShopSlot> ReadSlotAsync(
        CaptureFrame frame,
        int index,
        CancellationToken cancellationToken)
    {
        var source = NameRegions[index];
        var nameRegions = new[]
        {
            source,
            // The first fallback covers only the gray character-name strip.
            // The former +8/52 region still included the final bond row, so
            // Windows OCR combined large name glyphs with unrelated text.
            new PixelRect(source.X, source.Y + 23, 180, 37),
            new PixelRect(source.X - 6, source.Y + 14, 185, 44)
        };
        var allVariants = new List<string>();
        foreach (var sourceRegion in nameRegions)
        {
            var recognized = await ocr
                .RecognizeAsync(
                    frame,
                    RewardRecognitionSupport.Scale(sourceRegion, frame),
                    cancellationToken)
                .ConfigureAwait(false);
            allVariants.AddRange(RewardOcrTextVariants.Expand(recognized));
            var best = FindBestCharacter(allVariants);
            if (best is not null)
            {
                return new RewardShopSlot(
                    index,
                    best.Value,
                    string.Join(" / ", allVariants.Distinct()),
                    best.Confidence);
            }
        }

        var bondRegion = new PixelRect(source.X, 165, 230, 125);
        var bondText = await ocr
            .RecognizeAsync(
                frame,
                RewardRecognitionSupport.Scale(bondRegion, frame),
                cancellationToken)
            .ConfigureAwait(false);
        var bondVariants = RewardOcrTextVariants.Expand(bondText);
        allVariants.AddRange(bondVariants.Select(item => $"羁绊:{item}"));
        var bondMatch = FindUniqueCharacterByBonds(bondVariants) ??
                        FindUniqueRepeatedNameByShapeAndBond(
                            allVariants,
                            bondVariants);
        return new RewardShopSlot(
            index,
            bondMatch?.Character,
            string.Join(" / ", allVariants.Distinct()),
            bondMatch?.Confidence ?? 0);
    }

    private NameMatch<CurrencyWarsCharacterData>? FindBestCharacter(
        IEnumerable<string> variants) =>
        RewardRecognitionSupport.FindBest(
            _matcher,
            variants,
            _characters,
            item => item.Name,
            0.70);

    private (CurrencyWarsCharacterData Character, double Confidence)?
        FindUniqueCharacterByBonds(IEnumerable<string> variants)
    {
        var recognizedBonds = FindRecognizedBonds(variants);
        if (recognizedBonds.Length < 2)
        {
            return null;
        }

        var ranked = _characters
            .Select(character => new
            {
                Character = character,
                Matches = recognizedBonds
                    .Where(match => character.BondNames.Contains(
                        match.Value,
                        StringComparer.OrdinalIgnoreCase))
                    .ToArray()
            })
            .Where(item => item.Matches.Length >= 2)
            .OrderByDescending(item => item.Matches.Length)
            .ToArray();
        if (ranked.Length == 0 ||
            (ranked.Length > 1 &&
             ranked[0].Matches.Length == ranked[1].Matches.Length))
        {
            return null;
        }

        return (
            ranked[0].Character,
            ranked[0].Matches.Average(item => item.Confidence));
    }

    private (CurrencyWarsCharacterData Character, double Confidence)?
        FindUniqueRepeatedNameByShapeAndBond(
            IEnumerable<string> nameVariants,
            IEnumerable<string> bondVariants)
    {
        // Windows OCR can consistently retain only the repeated lower
        // component of a complex two-glyph name (for example 藿藿 -> 隹隹).
        // Treat that repeated Han shape as evidence only when one recognized
        // bond leaves exactly one repeated-name character in the catalog.
        // This is deliberately not a general fuzzy guess.
        if (!nameVariants.Any(IsRepeatedHanPair))
        {
            return null;
        }

        var recognizedBonds = FindRecognizedBonds(bondVariants);
        if (recognizedBonds.Length == 0)
        {
            return null;
        }

        var candidates = _characters
            .Where(character => IsRepeatedHanPair(character.Name))
            .Select(character => new
            {
                Character = character,
                SupportingBonds = recognizedBonds
                    .Where(match => character.BondNames.Contains(
                        match.Value,
                        StringComparer.OrdinalIgnoreCase))
                    .ToArray()
            })
            .Where(item => item.SupportingBonds.Length > 0)
            .ToArray();
        if (candidates.Length != 1)
        {
            return null;
        }

        return (
            candidates[0].Character,
            Math.Min(
                0.85,
                0.70 + candidates[0].SupportingBonds
                    .Average(item => item.Confidence) * 0.15));
    }

    private NameMatch<string>[] FindRecognizedBonds(
        IEnumerable<string> variants) =>
        variants
            .Select(text => _matcher.FindBest(
                text,
                _bondNames,
                item => item,
                0.72))
            .Where(item => item is not null)
            .Select(item => item!)
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .ToArray();

    private static bool IsRepeatedHanPair(string value)
    {
        var normalized = GameDataNameMatcher.Normalize(value);
        return normalized.Length == 2 &&
               normalized[0] == normalized[1] &&
               normalized[0] is >= '\u3400' and <= '\u9fff';
    }

}

public sealed record InvestmentStrategySlot(
    int Slot,
    InvestmentStrategyData? Strategy,
    string RawText,
    double Confidence);

public sealed class RewardShopRecognitionAccumulator(
    int slotCount = 5,
    IReadOnlySet<int>? ignoredSlots = null)
{
    private readonly RewardShopSlot?[] _stable = new RewardShopSlot?[slotCount];
    private readonly RewardShopSlot?[] _best = new RewardShopSlot?[slotCount];
    private readonly string?[] _previousIds = new string?[slotCount];
    private readonly int[] _consecutiveCounts = new int[slotCount];

    public bool IsComplete => Enumerable.Range(0, _stable.Length).All(
        index => ignoredSlots?.Contains(index) == true ||
                 _stable[index]?.Character is not null);

    public void Observe(IReadOnlyList<RewardShopSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count != _stable.Length)
        {
            throw new ArgumentException(
                $"Expected {_stable.Length} shop slots, received {slots.Count}.",
                nameof(slots));
        }

        foreach (var slot in slots)
        {
            if (slot.Slot < 0 || slot.Slot >= _stable.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(slots));
            }

            if (_best[slot.Slot] is null ||
                slot.Confidence > _best[slot.Slot]!.Confidence)
            {
                _best[slot.Slot] = slot;
            }

            var id = slot.Character?.Id;
            if (id is null)
            {
                _previousIds[slot.Slot] = null;
                _consecutiveCounts[slot.Slot] = 0;
                continue;
            }

            if (_stable[slot.Slot]?.Character is { } stableCharacter &&
                !string.Equals(
                    stableCharacter.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                _stable[slot.Slot] = null;
            }

            if (string.Equals(
                    _previousIds[slot.Slot],
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveCounts[slot.Slot]++;
            }
            else
            {
                _previousIds[slot.Slot] = id;
                _consecutiveCounts[slot.Slot] = 1;
            }

            if (_consecutiveCounts[slot.Slot] >= 2)
            {
                _stable[slot.Slot] = slot;
            }
        }
    }

    public IReadOnlyList<RewardShopSlot> Snapshot() =>
        Enumerable.Range(0, _stable.Length)
            .Select(index =>
                _stable[index] ?? new RewardShopSlot(
                    index,
                    null,
                    _best[index]?.RawText ?? string.Empty,
                    0))
            .ToArray();
}

public enum RewardShopPurchaseVerificationStatus
{
    Pending,
    Confirmed,
    NotPurchased
}

public sealed class RewardShopPurchaseVerificationAccumulator(
    int slotIndex,
    string expectedCharacterId,
    int requiredConsecutiveFrames = 2)
{
    private int _consecutivePresentFrames;
    private int _consecutiveAbsentFrames;

    public RewardShopPurchaseVerificationStatus Observe(
        IReadOnlyList<RewardShopSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var slot = slots.SingleOrDefault(item => item.Slot == slotIndex);
        var stillPresent = slot?.Character is { } character &&
            string.Equals(
                character.Id,
                expectedCharacterId,
                StringComparison.OrdinalIgnoreCase);
        if (stillPresent)
        {
            _consecutivePresentFrames++;
            _consecutiveAbsentFrames = 0;
            return _consecutivePresentFrames >= requiredConsecutiveFrames
                ? RewardShopPurchaseVerificationStatus.NotPurchased
                : RewardShopPurchaseVerificationStatus.Pending;
        }

        _consecutiveAbsentFrames++;
        _consecutivePresentFrames = 0;
        return _consecutiveAbsentFrames >= requiredConsecutiveFrames
            ? RewardShopPurchaseVerificationStatus.Confirmed
            : RewardShopPurchaseVerificationStatus.Pending;
    }
}

public sealed class InvestmentStrategyPageReader(
    IOfflineOcr ocr,
    GameDataCatalog gameData,
    GameDataNameMatcher? matcher = null)
{
    private static readonly IReadOnlyList<PixelRect> NameRegions =
    [
        new(330, 445, 380, 80),
        new(795, 445, 380, 80),
        new(1260, 445, 380, 80)
    ];

    private readonly InvestmentStrategyData[] _firstPlaneStrategies =
        gameData.InvestmentStrategies
            .Where(item => item.AvailablePlanes.Contains(1))
            .ToArray();
    private readonly GameDataNameMatcher _matcher =
        matcher ?? new GameDataNameMatcher();

    public async Task<IReadOnlyList<InvestmentStrategySlot>> ReadAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        var results = new List<InvestmentStrategySlot>(NameRegions.Count);
        for (var index = 0; index < NameRegions.Count; index++)
        {
            var sourceRegion = NameRegions[index];
            var region = RewardRecognitionSupport.Scale(
                sourceRegion,
                frame);
            var recognized = await ocr
                .RecognizeAsync(frame, region, cancellationToken)
                .ConfigureAwait(false);
            var variants = RewardOcrTextVariants.Expand(recognized).ToList();
            var best = RewardRecognitionSupport.FindBest(
                _matcher,
                variants,
                _firstPlaneStrategies,
                item => item.Name,
                0.68);
            if (best is null)
            {
                var descriptionRegion = new PixelRect(
                    sourceRegion.X - 45,
                    sourceRegion.Y + 65,
                    sourceRegion.Width,
                    220);
                var description = await ocr
                    .RecognizeAsync(
                        frame,
                        RewardRecognitionSupport.Scale(
                            descriptionRegion,
                            frame),
                        cancellationToken)
                    .ConfigureAwait(false);
                var descriptionVariants =
                    RewardOcrTextVariants.Expand(description);
                best = RewardRecognitionSupport.FindBest(
                    _matcher,
                    descriptionVariants,
                    _firstPlaneStrategies,
                    item => item.Effect,
                    0.68);
                variants.AddRange(
                    descriptionVariants.Select(item => $"说明:{item}"));
            }

            results.Add(new InvestmentStrategySlot(
                index,
                best?.Value,
                string.Join(" / ", variants),
                best?.Confidence ?? 0));
        }

        return results;
    }
}
