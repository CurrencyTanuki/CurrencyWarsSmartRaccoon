using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed record OpeningPageRegions(
    int ReferenceWidth,
    int ReferenceHeight,
    IReadOnlyList<PixelRect> EnemyFactionNames,
    PixelRect EnemyModifierStrip,
    IReadOnlyList<PixelRect> InvestmentEnvironmentNames)
{
    public static OpeningPageRegions Standard1920x1080 { get; } = new(
        1920,
        1080,
        [
            new PixelRect(125, 700, 260, 65),
            new PixelRect(430, 700, 260, 65),
            new PixelRect(738, 700, 255, 65)
        ],
        // Modifier chips are laid out by content width. Cover the full row,
        // including the longest possible four-name combination, rather than
        // assuming later chips remain at fixed horizontal slots.
        new PixelRect(240, 930, 1000, 90),
        [
            new PixelRect(285, 375, 300, 50),
            new PixelRect(800, 375, 320, 50),
            new PixelRect(1320, 375, 310, 50)
        ]);
}

public sealed record RecognizedOpeningItem(
    int Slot,
    string RawText,
    ObservedItem? Item);

public sealed record EnemyOverviewReadResult(
    IReadOnlyList<RecognizedOpeningItem> Competitors,
    IReadOnlyList<RecognizedOpeningItem> EnemyModifiers)
{
    public IReadOnlyList<ObservedItem> RecognizedCompetitors =>
        Competitors
            .Select(value => value.Item)
            .OfType<ObservedItem>()
            .ToArray();

    public IReadOnlyList<ObservedItem> RecognizedEnemyModifiers =>
        EnemyModifiers
            .Select(value => value.Item)
            .OfType<ObservedItem>()
            .ToArray();

    public bool IsComplete =>
        Competitors.Count == 3 &&
        EnemyModifiers.Count == 4 &&
        Competitors.All(value => value.Item is not null) &&
        EnemyModifiers.All(value => value.Item is not null);
}

public sealed record InvestmentEnvironmentReadResult(
    IReadOnlyList<RecognizedOpeningItem> Options)
{
    public IReadOnlyList<ObservedItem> InvestmentEnvironments =>
        Options
            .Select(value => value.Item)
            .OfType<ObservedItem>()
            .ToArray();

    public bool IsComplete =>
        Options.Count == 3 && Options.All(value => value.Item is not null);
}

public interface IOcrOpeningPageReader
{
    ValueTask<EnemyOverviewReadResult> ReadEnemyOverviewAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken);

    ValueTask<InvestmentEnvironmentReadResult> ReadInvestmentEnvironmentsAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken);
}

public sealed class OcrOpeningPageReader(
    IOfflineOcr ocr,
    GameDataCatalog catalog,
    OpeningPageRegions? regions = null,
    GameDataNameMatcher? matcher = null) : IOcrOpeningPageReader
{
    private const int EnemyModifierCount = 4;
    private readonly OpeningPageRegions regions =
        regions ?? OpeningPageRegions.Standard1920x1080;
    private readonly GameDataNameMatcher matcher = matcher ?? new GameDataNameMatcher();

    public async ValueTask<EnemyOverviewReadResult> ReadEnemyOverviewAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        var competitors = await ReadRegionsAsync(
            frame,
            regions.EnemyFactionNames,
            catalog.Competitors,
            value => value.Name,
            value => value.Id,
            0.68,
            null,
            false,
            cancellationToken).ConfigureAwait(false);
        var modifiers = await ReadEnemyModifierStripAsync(
            frame,
            cancellationToken).ConfigureAwait(false);

        return new EnemyOverviewReadResult(competitors, modifiers);
    }

    private async ValueTask<IReadOnlyList<RecognizedOpeningItem>>
        ReadEnemyModifierStripAsync(
            CaptureFrame frame,
            CancellationToken cancellationToken)
    {
        var strip = Scale(
            regions.EnemyModifierStrip,
            frame.Width,
            frame.Height);
        var region = TightenEnemyModifierStripToContent(frame, strip);
        var recognized = await ocr
            .RecognizeAsync(frame, region, cancellationToken)
            .ConfigureAwait(false);
        var variants = GetRecognizedVariants([recognized]);

        // Windows OCR commonly returns both the whole chip row and individual
        // chip lines. First split an exact whole-row result by canonical names
        // and retain their left-to-right positions.
        foreach (var text in variants)
        {
            var normalized = GameDataNameMatcher.Normalize(text);
            var exactMatches = catalog.EnemyAffixes
                .Select(value => new
                {
                    Value = value,
                    Name = GameDataNameMatcher.Normalize(value.Name)
                })
                .Select(value => new
                {
                    value.Value,
                    value.Name,
                    Position = normalized.IndexOf(
                        value.Name,
                        StringComparison.Ordinal)
                })
                .Where(value => value.Position >= 0)
                .OrderBy(value => value.Position)
                .ToArray();
            if (exactMatches.Length == EnemyModifierCount &&
                exactMatches.Select(value => value.Value.Id).Distinct().Count() ==
                EnemyModifierCount)
            {
                return exactMatches
                    .Select((value, slot) => new RecognizedOpeningItem(
                        slot,
                        value.Value.Name,
                        new ObservedItem(
                            value.Value.Id,
                            value.Value.Name,
                            1)))
                    .ToArray();
            }
        }

        // If the whole line contains OCR noise, use the OCR engine's separated
        // line variants and the existing conservative fuzzy matcher.
        var matches = variants
            .Select(text => new
            {
                Text = text,
                Match = FindAcceptedMatch(
                    [text],
                    catalog.EnemyAffixes,
                    value => value.Name,
                    value => value.Id,
                    0.72)
            })
            .Where(value =>
                value.Match is not null &&
                GameDataNameMatcher.Normalize(value.Text).Length <=
                GameDataNameMatcher.Normalize(value.Match.CanonicalName).Length + 2)
            .DistinctBy(value => value.Match!.Value.Id)
            .Take(EnemyModifierCount + 1)
            .ToArray();
        if (matches.Length != EnemyModifierCount)
        {
            // A native 2560-wide WGC frame can leave short, widely spaced,
            // low-contrast labels too small for Windows OCR when the entire
            // strip must fit under its maximum input dimension. The whole-row
            // result may also contain non-empty garbage or only a partial set;
            // neither is useful, but both previously suppressed the fallback.
            // Retry overlapping windows whenever the row did not form a safe
            // 4/4 result. These are not four fixed slots: their count and
            // positions follow the actual capture scale.
            return await ReadEnemyModifierSegmentsAsync(
                    frame,
                    strip,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return matches
            .Select((value, slot) => new RecognizedOpeningItem(
                slot,
                value.Text,
                new ObservedItem(
                    value.Match!.Value.Id,
                    value.Match.Value.Name,
                    value.Match.Confidence)))
            .ToArray();
    }

    private async ValueTask<IReadOnlyList<RecognizedOpeningItem>>
        ReadEnemyModifierSegmentsAsync(
            CaptureFrame frame,
            PixelRect strip,
            CancellationToken cancellationToken)
    {
        var located = new List<LocatedModifierMatch>();
        foreach (var segment in CreateOverlappingSegments(strip))
        {
            var recognized = await ocr
                .RecognizeAsync(frame, segment, cancellationToken)
                .ConfigureAwait(false);
            foreach (var text in GetRecognizedVariants([recognized]))
            {
                var normalized = GameDataNameMatcher.Normalize(text);
                var exact = catalog.EnemyAffixes
                    .Select(value => new
                    {
                        Value = value,
                        Name = GameDataNameMatcher.Normalize(value.Name)
                    })
                    .Select(value => new
                    {
                        value.Value,
                        value.Name,
                        Position = normalized.IndexOf(
                            value.Name,
                            StringComparison.Ordinal)
                    })
                    .Where(value => value.Position >= 0)
                    .ToArray();
                if (exact.Length > 0)
                {
                    located.AddRange(
                        exact.Select(value => new LocatedModifierMatch(
                            value.Value.Id,
                            value.Value.Name,
                            value.Value.Name,
                            1,
                            segment.X +
                            (value.Position + value.Name.Length / 2d) /
                            Math.Max(1, normalized.Length) * segment.Width)));
                    continue;
                }

                var match = FindAcceptedMatch(
                    [text],
                    catalog.EnemyAffixes,
                    value => value.Name,
                    value => value.Id,
                    0.72);
                if (match is null ||
                    normalized.Length >
                    GameDataNameMatcher.Normalize(match.CanonicalName).Length + 2)
                {
                    continue;
                }

                located.Add(new LocatedModifierMatch(
                    match.Value.Id,
                    match.CanonicalName,
                    text,
                    match.Confidence,
                    segment.X + segment.Width / 2d));
            }
        }

        var distinct = located
            .GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Best = group
                    .OrderByDescending(value => value.Confidence)
                    .First(),
                Position = group.Average(value => value.HorizontalPosition)
            })
            .ToArray();
        if (distinct.Length != EnemyModifierCount)
        {
            return [];
        }

        return distinct
            .OrderBy(value => value.Position)
            .Select((value, slot) => new RecognizedOpeningItem(
                slot,
                value.Best.RawText,
                new ObservedItem(
                    value.Best.Id,
                    value.Best.DisplayName,
                    value.Best.Confidence)))
            .ToArray();
    }

    private static IReadOnlyList<PixelRect> CreateOverlappingSegments(
        PixelRect strip)
    {
        var windowWidth = Math.Clamp(
            (int)Math.Round(strip.Width * 0.36),
            1,
            strip.Width);
        var step = Math.Max(1, windowWidth / 2);
        var segments = new List<PixelRect>();
        for (var x = strip.X;
             x + windowWidth <= strip.Right;
             x += step)
        {
            segments.Add(new PixelRect(
                x,
                strip.Y,
                windowWidth,
                strip.Height));
        }

        var finalX = strip.Right - windowWidth;
        if (segments.Count == 0 || segments[^1].X != finalX)
        {
            segments.Add(new PixelRect(
                finalX,
                strip.Y,
                windowWidth,
                strip.Height));
        }

        return segments;
    }

    private static PixelRect TightenEnemyModifierStripToContent(
        CaptureFrame frame,
        PixelRect strip)
    {
        if (strip.Width < 1 ||
            strip.Height < 1 ||
            frame.Stride < frame.Width * 4 ||
            frame.BgraPixels.Length < frame.Stride * frame.Height)
        {
            return strip;
        }

        // Keep one dynamic row ROI, but remove unused space at its right edge
        // before Windows OCR applies its input-size cap. The neutral gray/white
        // glyphs and chip outlines are intentionally selected here; the red
        // page artwork is ignored. No fixed chip positions are assumed.
        var scanTop = strip.Y + Math.Max(0, strip.Height / 4);
        var scanBottom = Math.Min(
            strip.Bottom,
            strip.Y + Math.Max(1, strip.Height * 9 / 10));
        var minimumNeutralPixels = Math.Max(2, (scanBottom - scanTop) / 24);
        var activeColumns = new bool[strip.Width];
        for (var localX = 0; localX < strip.Width; localX++)
        {
            var x = strip.X + localX;
            var neutralPixels = 0;
            for (var y = scanTop; y < scanBottom; y++)
            {
                var offset = checked(y * frame.Stride + x * 4);
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                var maximum = Math.Max(red, Math.Max(green, blue));
                var minimum = Math.Min(red, Math.Min(green, blue));
                var luminance = (red * 77 + green * 150 + blue * 29) >> 8;
                if (maximum - minimum <= 44 && luminance >= 78)
                {
                    neutralPixels++;
                    if (neutralPixels >= minimumNeutralPixels)
                    {
                        activeColumns[localX] = true;
                        break;
                    }
                }
            }
        }

        var smoothingRadius = Math.Max(2, strip.Width / 160);
        var first = -1;
        var last = -1;
        for (var localX = 0; localX < activeColumns.Length; localX++)
        {
            var start = Math.Max(0, localX - smoothingRadius);
            var end = Math.Min(activeColumns.Length - 1, localX + smoothingRadius);
            var nearbyActive = false;
            for (var neighbor = start; neighbor <= end; neighbor++)
            {
                if (!activeColumns[neighbor])
                {
                    continue;
                }

                nearbyActive = true;
                break;
            }

            if (!nearbyActive)
            {
                continue;
            }

            first = first < 0 ? localX : first;
            last = localX;
        }

        if (first < 0 || last - first + 1 < strip.Width / 5)
        {
            return strip;
        }

        var padding = Math.Max(8, strip.Width / 50);
        var left = Math.Max(strip.X, strip.X + first - padding);
        var right = Math.Min(strip.Right, strip.X + last + 1 + padding);
        return right > left
            ? new PixelRect(left, strip.Y, right - left, strip.Height)
            : strip;
    }

    private sealed record LocatedModifierMatch(
        string Id,
        string DisplayName,
        string RawText,
        double Confidence,
        double HorizontalPosition);

    public async ValueTask<InvestmentEnvironmentReadResult>
        ReadInvestmentEnvironmentsAsync(
            CaptureFrame frame,
            CancellationToken cancellationToken)
    {
        var options = await ReadRegionsAsync(
            frame,
            regions.InvestmentEnvironmentNames,
            catalog.InvestmentEnvironments,
            value => value.Name,
            value => value.Id,
            0.68,
            value => value.Effect,
            true,
            cancellationToken).ConfigureAwait(false);
        return new InvestmentEnvironmentReadResult(options);
    }

    private async ValueTask<IReadOnlyList<RecognizedOpeningItem>> ReadRegionsAsync<T>(
        CaptureFrame frame,
        IReadOnlyList<PixelRect> sourceRegions,
        IEnumerable<T> candidates,
        Func<T, string> nameSelector,
        Func<T, string> idSelector,
        double minimumConfidence,
        Func<T, string>? contextSelector,
        bool retryEmptyWithExpandedContext,
        CancellationToken cancellationToken)
    {
        var results = new List<RecognizedOpeningItem>(sourceRegions.Count);
        for (var index = 0; index < sourceRegions.Count; index++)
        {
            var region = Scale(sourceRegions[index], frame.Width, frame.Height);
            var recognized = await ocr
                .RecognizeAsync(frame, region, cancellationToken)
                .ConfigureAwait(false);
            var recognizedResults = new List<OcrTextResult> { recognized };
            var recognizedVariants = GetRecognizedVariants(recognizedResults);
            var match = FindAcceptedMatch(
                recognizedVariants,
                candidates,
                nameSelector,
                idSelector,
                minimumConfidence);
            if (retryEmptyWithExpandedContext && match is null)
            {
                var expandedRegion = ExpandForTextContext(
                    region,
                    frame.Width,
                    frame.Height);
                recognizedResults.Add(await ocr
                    .RecognizeAsync(frame, expandedRegion, cancellationToken)
                    .ConfigureAwait(false));
                recognizedVariants = GetRecognizedVariants(recognizedResults);
                match = FindAcceptedMatch(
                    recognizedVariants,
                    candidates,
                    nameSelector,
                    idSelector,
                    minimumConfidence);
                if (match is null && contextSelector is not null)
                {
                    match = FindAcceptedMatch(
                        recognizedVariants,
                        candidates,
                        contextSelector,
                        idSelector,
                        minimumConfidence);
                }
            }

            var item = match is null
                ? null
                : new ObservedItem(
                    idSelector(match.Value),
                    nameSelector(match.Value),
                    match.Confidence);
            var rawText = string.Join(
                " / ",
                recognizedVariants.DefaultIfEmpty(
                    recognizedResults[0].Text));
            results.Add(new RecognizedOpeningItem(index, rawText, item));
        }

        return results;
    }

    private NameMatch<T>? FindAcceptedMatch<T>(
        IReadOnlyList<string> recognizedVariants,
        IEnumerable<T> candidates,
        Func<T, string> matchTextSelector,
        Func<T, string> idSelector,
        double minimumConfidence)
    {
            var groupedMatches = recognizedVariants
                .Select(text => matcher.FindBest(
                    text,
                    candidates,
                    matchTextSelector,
                    minimumConfidence))
                .Where(match => match is not null)
                .Select(match => match!)
                .GroupBy(match => idSelector(match.Value))
                .Select(group => new
                {
                    Support = group.Count(),
                    Best = group
                        .OrderByDescending(match => match.Confidence)
                        .First()
                })
                .OrderByDescending(group => group.Support)
                .ThenByDescending(group => group.Best.Confidence)
                .ToArray();
            var winningGroup = groupedMatches.FirstOrDefault();
            var isAmbiguous = winningGroup is not null &&
                              groupedMatches.Length > 1 &&
                              winningGroup.Support == groupedMatches[1].Support &&
                              winningGroup.Best.Confidence -
                              groupedMatches[1].Best.Confidence < 0.08;
            var recognizedName = winningGroup is null
                ? string.Empty
                : GameDataNameMatcher.Normalize(
                    winningGroup.Best.RecognizedText);
            var canonicalName = winningGroup is null
                ? string.Empty
                : GameDataNameMatcher.Normalize(
                    winningGroup.Best.CanonicalName);
            var isUniqueEdgeFragment =
                groupedMatches.Length == 1 &&
                recognizedName.Length >= 3 &&
                recognizedName.Length * 4 >= canonicalName.Length * 3 &&
                (canonicalName.StartsWith(
                     recognizedName,
                     StringComparison.Ordinal) ||
                 canonicalName.EndsWith(
                     recognizedName,
                     StringComparison.Ordinal));
            var isSingleEdgeGlyphError =
                groupedMatches.Length == 1 &&
                recognizedName.Length >= 4 &&
                recognizedName.Length == canonicalName.Length &&
                (recognizedName[1..].Equals(
                     canonicalName[1..],
                     StringComparison.Ordinal) ||
                 recognizedName[..^1].Equals(
                     canonicalName[..^1],
                     StringComparison.Ordinal));
            return !isAmbiguous &&
                   winningGroup is not null &&
                   ((winningGroup.Support >= 2 &&
                     (recognizedName.Length >= 3 ||
                      recognizedName.Equals(
                          canonicalName,
                          StringComparison.Ordinal))) ||
                    (winningGroup.Best.Confidence >= 0.82 &&
                     recognizedName.Length >= canonicalName.Length) ||
                    (winningGroup.Best.Confidence >= 0.94 &&
                     isUniqueEdgeFragment) ||
                    (winningGroup.Best.Confidence >= 0.72 &&
                     isSingleEdgeGlyphError))
                ? winningGroup.Best
                : null;
    }

    private static string[] GetRecognizedVariants(
        IEnumerable<OcrTextResult> results) =>
        results
            .SelectMany(result => result.Lines.Prepend(result.Text))
                .Where(text => GameDataNameMatcher.Normalize(text).Length >= 2)
                .DistinctBy(GameDataNameMatcher.Normalize)
                .ToArray();

    private static PixelRect ExpandForTextContext(
        PixelRect region,
        int frameWidth,
        int frameHeight)
    {
        var horizontalPadding = region.Width / 3;
        var topPadding = region.Height / 3;
        return Bound(
            new PixelRect(
                region.X - horizontalPadding,
                region.Y - topPadding,
                region.Width + horizontalPadding * 2,
                region.Height * 3),
            frameWidth,
            frameHeight);
    }

    private PixelRect Scale(PixelRect source, int targetWidth, int targetHeight)
    {
        var horizontalScale = targetWidth / (double)regions.ReferenceWidth;
        var verticalScale = targetHeight / (double)regions.ReferenceHeight;
        return new PixelRect(
            (int)Math.Round(source.X * horizontalScale),
            (int)Math.Round(source.Y * verticalScale),
            (int)Math.Round(source.Width * horizontalScale),
            (int)Math.Round(source.Height * verticalScale));
    }

    private static PixelRect Bound(
        PixelRect region,
        int width,
        int height)
    {
        var x = Math.Clamp(region.X, 0, width);
        var y = Math.Clamp(region.Y, 0, height);
        var right = Math.Clamp(region.Right, x, width);
        var bottom = Math.Clamp(region.Bottom, y, height);
        return new PixelRect(x, y, right - x, bottom - y);
    }
}
