using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class CurrencyWarsNavigationConfigTests
{
    [Fact]
    public void Load_RepositoryConfig_ProvidesCompleteSafeFlow()
    {
        var path = FindRepositoryFile("config", "navigation-flow.json");

        var config = CurrencyWarsNavigationConfig.Load(path);

        Assert.Equal(1920, config.ReferenceWidth);
        Assert.Equal(1080, config.ReferenceHeight);
        Assert.Equal(14, config.Steps.Count);
        Assert.Contains(config.Steps, step => step.PageId == "normal_hud");
        var preparation = Assert.Single(
            config.Steps,
            step => step.PageId == "preparation_1_1");
        Assert.True(preparation.Terminal);
        Assert.Empty(preparation.Actions);
        Assert.Contains(
            config.Steps,
            step => step.PageId == "investment_strategy");
        Assert.Contains(
            config.Steps,
            step => step.PageId == "preparation_1_2" &&
                    step.Terminal);

        var modeSelection = Assert.Single(
            config.Steps,
            step => step.PageId == "mode_selection");
        Assert.Equal(3, modeSelection.Actions.Count);
        Assert.Equal(
            CurrencyWarsGameMode.Standard,
            modeSelection.Actions[0].RequiredGameMode);
        Assert.Equal(
            CurrencyWarsGameMode.Overclock,
            modeSelection.Actions[1].RequiredGameMode);
        Assert.Null(modeSelection.Actions[2].RequiredGameMode);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
