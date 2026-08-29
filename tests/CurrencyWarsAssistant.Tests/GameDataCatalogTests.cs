using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tests;

public sealed class GameDataCatalogTests
{
    [Fact]
    public void LoadsCompleteVersion44DataSet()
    {
        var dataDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../data/4.4"));

        var catalog = GameDataCatalogLoader.Load(dataDirectory);

        Assert.Equal(83, catalog.InvestmentEnvironments.Count);
        Assert.Equal(334, catalog.InvestmentStrategies.Count);
        Assert.Equal(51, catalog.EnemyAffixes.Count);
        Assert.Equal(20, catalog.Competitors.Count);
        Assert.Equal(71, catalog.CurrencyWarsCharacters.Count);
        Assert.Equal(
            "investment_environment_036",
            catalog.InvestmentEnvironmentsByName["敌后破坏"].Id);
        Assert.Equal(
            "enemy_affix_t2_16",
            catalog.EnemyAffixesByName["额外打击"].Id);
        Assert.Equal(
            "competitor_12",
            catalog.CompetitorsByName["灰手生命科技"].Id);
        Assert.Equal(
            [3, 4, 5],
            catalog.CurrencyWarsCharactersByName["银狼LV.999"].Costs);
        Assert.Equal(
            "前台",
            catalog.CurrencyWarsCharactersByName["银狼LV.999"].Position);
    }

    [Fact]
    public void FirstPlaneInvestmentStrategyPoolContainsOnlyEligibleEntries()
    {
        var dataDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../data/4.4"));
        var catalog = GameDataCatalogLoader.Load(dataDirectory);

        var firstPlaneStrategies = catalog.InvestmentStrategies
            .Where(strategy => strategy.AvailablePlanes.Contains(1))
            .ToArray();

        Assert.Equal(244, firstPlaneStrategies.Length);
        Assert.All(
            firstPlaneStrategies,
            strategy => Assert.Contains(1, strategy.AvailablePlanes));
    }

    [Fact]
    public void InvestmentStrategyVersionsMatchBwWikiAppendBatches()
    {
        var dataDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../data/4.4"));
        var catalog = GameDataCatalogLoader.Load(dataDirectory);

        var version44 = catalog.InvestmentStrategies
            .Where(item =>
                InvestmentStrategyVersionCatalog.GetIntroducedVersion(
                    item.Id) == "4.4")
            .ToArray();
        var version42 = catalog.InvestmentStrategies
            .Where(item =>
                InvestmentStrategyVersionCatalog.GetIntroducedVersion(
                    item.Id) == "4.2")
            .ToArray();

        Assert.Equal(19, version44.Length);
        Assert.Equal(27, version42.Length);
        Assert.Contains(version44, item => item.Name == "命运圣杯星徽");
        Assert.Contains(version42, item => item.Name == "按劳分配");
    }
}
