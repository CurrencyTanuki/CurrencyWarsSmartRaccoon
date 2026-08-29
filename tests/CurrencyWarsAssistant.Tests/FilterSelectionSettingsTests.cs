using CurrencyWarsAssistant.App;

namespace CurrencyWarsAssistant.Tests;

public sealed class FilterSelectionSettingsTests
{
    [Fact]
    public void OpeningFilterSelectionsSurviveCaptureAndRestore()
    {
        var original = new[]
        {
            Item("environment_a", supportsForbidden: false,
                AppFilterSelectionMode.Required),
            Item("environment_b", supportsForbidden: false,
                AppFilterSelectionMode.Unrestricted),
            Item("competitor_a", supportsForbidden: true,
                AppFilterSelectionMode.Forbidden)
        };

        var saved = FilterSelectionSettings.Capture(original);
        var restored = new[]
        {
            Item("environment_a", supportsForbidden: false),
            Item("environment_b", supportsForbidden: false,
                AppFilterSelectionMode.Required),
            Item("competitor_a", supportsForbidden: true)
        };
        FilterSelectionSettings.Apply(saved, restored);

        Assert.Collection(
            restored,
            item => Assert.Equal(
                AppFilterSelectionMode.Required,
                item.SelectionMode),
            item => Assert.Equal(
                AppFilterSelectionMode.Unrestricted,
                item.SelectionMode),
            item => Assert.Equal(
                AppFilterSelectionMode.Forbidden,
                item.SelectionMode));
    }

    [Fact]
    public void MissingLegacyFieldsDoNotOverwriteCurrentDefaults()
    {
        var current = new[]
        {
            Item("environment_a", supportsForbidden: false,
                AppFilterSelectionMode.Required)
        };

        FilterSelectionSettings.Apply(null, current);

        Assert.Equal(
            AppFilterSelectionMode.Required,
            current[0].SelectionMode);
    }

    [Fact]
    public void NineSelectedEnvironmentsRemainNineAfterRestart()
    {
        var original = Enumerable.Range(1, 83)
            .Select(index => Item(
                $"environment_{index}",
                supportsForbidden: false,
                index <= 9
                    ? AppFilterSelectionMode.Required
                    : AppFilterSelectionMode.Unrestricted))
            .ToArray();

        var saved = FilterSelectionSettings.Capture(original);
        var restarted = Enumerable.Range(1, 83)
            .Select(index => Item(
                $"environment_{index}",
                supportsForbidden: false))
            .ToArray();
        FilterSelectionSettings.Apply(saved, restarted);

        Assert.Equal(9, saved.Count);
        Assert.Equal(
            9,
            restarted.Count(item =>
                item.SelectionMode == AppFilterSelectionMode.Required));
        Assert.Equal(
            AppFilterSelectionMode.Unrestricted,
            restarted[9].SelectionMode);
    }

    private static FilterItemViewModel Item(
        string id,
        bool supportsForbidden,
        AppFilterSelectionMode mode =
            AppFilterSelectionMode.Unrestricted)
    {
        var item = new FilterItemViewModel(
            id,
            id,
            "test",
            supportsForbidden);
        item.SelectionMode = mode;
        return item;
    }
}
