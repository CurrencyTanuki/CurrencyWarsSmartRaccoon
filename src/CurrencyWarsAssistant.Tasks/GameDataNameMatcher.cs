using System.Globalization;
using System.Text;

namespace CurrencyWarsAssistant.Tasks;

public sealed record NameMatch<T>(
    T Value,
    string CanonicalName,
    string RecognizedText,
    double Confidence);

public sealed class GameDataNameMatcher
{
    public NameMatch<T>? FindBest<T>(
        string recognizedText,
        IEnumerable<T> candidates,
        Func<T, string> nameSelector,
        double minimumConfidence = 0.58)
    {
        var recognized = Normalize(recognizedText);
        if (recognized.Length == 0)
        {
            return null;
        }

        var matches = new List<NameMatch<T>>();
        foreach (var candidate in candidates)
        {
            var name = nameSelector(candidate);
            var normalizedName = Normalize(name);
            var confidence = Similarity(recognized, normalizedName);
            if (confidence < minimumConfidence)
            {
                continue;
            }

            matches.Add(new NameMatch<T>(
                candidate,
                name,
                recognizedText,
                confidence));
        }

        var ordered = matches
            .OrderByDescending(match => match.Confidence)
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var exactMatches = ordered
            .Where(match =>
                Normalize(match.CanonicalName).Equals(
                    recognized,
                    StringComparison.Ordinal))
            .ToArray();
        if (exactMatches.Length == 1)
        {
            return exactMatches[0];
        }

        if (exactMatches.Length > 1)
        {
            return null;
        }

        // Short OCR fragments are accepted only when they identify one
        // canonical entry clearly. This prevents a generic two-character
        // fragment from being guessed when several names are similar.
        if (ordered.Length > 1 &&
            ordered[0].Confidence - ordered[1].Confidence < 0.08)
        {
            return null;
        }

        return ordered[0];
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber)
            {
                result.Append(char.ToLowerInvariant(character));
            }
        }

        return result
            .ToString()
            .Replace("讠青", "请", StringComparison.Ordinal);
    }

    public static double Similarity(string left, string right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var shorterLength = Math.Min(left.Length, right.Length);
        var longerLength = Math.Max(left.Length, right.Length);
        if (shorterLength >= 2 &&
            (left.Contains(right, StringComparison.Ordinal) ||
             right.Contains(left, StringComparison.Ordinal)))
        {
            return 0.9 + 0.1 * shorterLength / longerLength;
        }

        var distance = LevenshteinDistance(left, right);
        var editSimilarity =
            Math.Max(0, 1 - distance / (double)Math.Max(left.Length, right.Length));
        var commonLength = LongestCommonSubstringLength(left, right);
        var commonCoverage = commonLength / (double)shorterLength;
        var fragmentSimilarity = commonLength >= 2 && commonCoverage >= 0.66
            ? 0.62 + 0.28 * commonCoverage
            : 0;
        return Math.Max(editSimilarity, fragmentSimilarity);
    }

    private static int LongestCommonSubstringLength(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        var longest = 0;
        for (var row = 1; row <= left.Length; row++)
        {
            for (var column = 1; column <= right.Length; column++)
            {
                current[column] = left[row - 1] == right[column - 1]
                    ? previous[column - 1] + 1
                    : 0;
                longest = Math.Max(longest, current[column]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return longest;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
