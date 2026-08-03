using System.Text.Json;
using CurrencyWarsAssistant.App;

namespace CurrencyWarsAssistant.Tests;

public sealed class SpecialCombinationSettingsTests
{
    [Fact]
    public void RequiredRuleKeepsMeaningAcrossDisableEnableAndJsonRoundTrip()
    {
        var source = new SpecialCombinationViewModel(
            "required combo",
            AppFilterSelectionMode.Required,
            "environment_a",
            "competitor_a",
            "affix_a",
            false,
            "custom_stable_id");

        source.IsRuleEnabled = false;
        source.IsRuleEnabled = true;
        var json = JsonSerializer.Serialize(
            SpecialCombinationSettings.Capture([source]));
        var persisted = JsonSerializer.Deserialize<SpecialCombinationSetting[]>(
            json)!;
        var restored = Assert.Single(
            SpecialCombinationSettings.Restore(persisted));

        Assert.Equal(AppFilterSelectionMode.Required, restored.Condition);
        Assert.Equal("custom_stable_id", restored.Id);
        Assert.Contains("保留", restored.RuleStateLabel);
    }

    [Fact]
    public void NewCustomRuleGetsStableNonEmptyId()
    {
        var source = new SpecialCombinationViewModel(
            "custom",
            AppFilterSelectionMode.Forbidden,
            "",
            "a",
            "b",
            false);

        var first = SpecialCombinationSettings.Capture([source])[0].Id;
        var second = SpecialCombinationSettings.Capture([source])[0].Id;

        Assert.StartsWith("custom_", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void DuplicateIdsAreCollapsedWhenSettingsAreCapturedAndRestored()
    {
        var first = new SpecialCombinationViewModel(
            "built in",
            AppFilterSelectionMode.Forbidden,
            "",
            "a",
            "b",
            true,
            "death_dragon_plus_extra_strike");
        var duplicate = new SpecialCombinationViewModel(
            "duplicate",
            AppFilterSelectionMode.Forbidden,
            "",
            "a",
            "b",
            true,
            "death_dragon_plus_extra_strike");

        var captured = Assert.Single(
            SpecialCombinationSettings.Capture([first, duplicate]));
        var restored = Assert.Single(
            SpecialCombinationSettings.Restore([captured, captured]));

        Assert.Equal("death_dragon_plus_extra_strike", restored.Id);
    }
}
