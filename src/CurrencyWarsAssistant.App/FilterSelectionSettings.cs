namespace CurrencyWarsAssistant.App;

internal sealed record FilterSelectionSnapshot(
    string Id,
    AppFilterSelectionMode SelectionMode);

internal static class FilterSelectionSettings
{
    public static IReadOnlyList<FilterSelectionSnapshot> Capture(
        IEnumerable<FilterItemViewModel> items) =>
        items
            .Where(item =>
                item.SelectionMode !=
                AppFilterSelectionMode.Unrestricted)
            .Select(item => new FilterSelectionSnapshot(
                item.Id,
                item.SelectionMode))
            .ToArray();

    public static void Apply(
        IReadOnlyList<FilterSelectionSnapshot>? saved,
        IEnumerable<FilterItemViewModel> items)
    {
        // Missing fields come from pre-fix settings files.  Leave the current
        // defaults intact so old files remain readable; a present empty list
        // explicitly means that every item is unrestricted.
        if (saved is null)
        {
            return;
        }

        var modesById = saved
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().SelectionMode,
                StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            item.SelectionMode = modesById.GetValueOrDefault(
                item.Id,
                AppFilterSelectionMode.Unrestricted);
        }
    }
}
