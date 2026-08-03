namespace CurrencyWarsAssistant.App;

public sealed record SpecialCombinationSetting(
    string Id,
    string Name,
    AppFilterSelectionMode Condition,
    string InvestmentEnvironments,
    string Competitors,
    string EnemyAffixes,
    bool IsBuiltIn);

public static class SpecialCombinationSettings
{
    public static IReadOnlyList<SpecialCombinationSetting> Capture(
        IEnumerable<SpecialCombinationViewModel> combinations) =>
        combinations
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SpecialCombinationSetting(
                item.Id,
                item.Name,
                item.Condition,
                item.InvestmentEnvironments,
                item.Competitors,
                item.EnemyAffixes,
                item.IsBuiltIn))
            .ToArray();

    public static IReadOnlyList<SpecialCombinationViewModel> Restore(
        IEnumerable<SpecialCombinationSetting> settings) =>
        settings
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SpecialCombinationViewModel(
                item.Name,
                item.Condition,
                item.InvestmentEnvironments,
                item.Competitors,
                item.EnemyAffixes,
                item.IsBuiltIn,
                item.Id))
            .ToArray();
}
