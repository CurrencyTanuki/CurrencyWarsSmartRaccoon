using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class InvestmentEnvironmentSelectionTests
{
    [Fact]
    public void SelectsTheSlotContainingAPreferredEnvironment()
    {
        var environments = Environments(
            ("environment_a", "环境 A"),
            ("environment_b", "命运圣杯契约"),
            ("environment_c", "环境 C"));

        var selected = InvestmentEnvironmentSelection.FindPreferredOption(
            environments,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "environment_b"
            });

        Assert.NotNull(selected);
        Assert.Equal(1, selected.Slot);
        Assert.Equal("命运圣杯契约", selected.Item!.DisplayName);
    }

    [Fact]
    public void ReturnsNullInsteadOfSelectingARandomOptionWhenNoPreferenceMatches()
    {
        var environments = Environments(
            ("environment_a", "环境 A"),
            ("environment_b", "环境 B"),
            ("environment_c", "环境 C"));

        var selected = InvestmentEnvironmentSelection.FindPreferredOption(
            environments,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "environment_missing"
            });

        Assert.Null(selected);
    }

    [Fact]
    public void DefaultsToFirstOptionOnlyWhenTheUserHasNoPreference()
    {
        var environments = Environments(
            ("environment_a", "环境 A"),
            ("environment_b", "环境 B"),
            ("environment_c", "环境 C"));

        var selected = InvestmentEnvironmentSelection.FindPreferredOption(
            environments,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(selected);
        Assert.Equal(0, selected.Slot);
    }

    private static InvestmentEnvironmentReadResult Environments(
        params (string Id, string Name)[] values) =>
        new(
            values
                .Select((value, slot) => new RecognizedOpeningItem(
                    slot,
                    value.Name,
                    new ObservedItem(value.Id, value.Name, 0.95)))
                .ToArray());
}
