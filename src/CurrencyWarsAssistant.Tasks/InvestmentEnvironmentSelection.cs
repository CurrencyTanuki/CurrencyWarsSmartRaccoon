namespace CurrencyWarsAssistant.Tasks;

public static class InvestmentEnvironmentSelection
{
    public static RecognizedOpeningItem? FindPreferredOption(
        InvestmentEnvironmentReadResult environments,
        IReadOnlySet<string> preferredIds)
    {
        ArgumentNullException.ThrowIfNull(environments);
        ArgumentNullException.ThrowIfNull(preferredIds);

        if (preferredIds.Count == 0)
        {
            return environments.Options
                .Where(option => option.Item is not null)
                .OrderBy(option => option.Slot)
                .FirstOrDefault();
        }

        return environments.Options
            .Where(option =>
                option.Item is not null &&
                preferredIds.Contains(option.Item.Id))
            .OrderBy(option => option.Slot)
            .FirstOrDefault();
    }
}
