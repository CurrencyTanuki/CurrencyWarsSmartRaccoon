using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class GameDataNameMatcherTests
{
    [Fact]
    public void MatchesNoisyOcrText()
    {
        var candidates = new[] { "火线动力机甲", "金血记忆体联盟", "额外打击" };
        (string Recognized, string Expected, double MinimumConfidence)[] cases =
        [
            (" 阵营：火线动力机甲 ", "火线动力机甲", 0.95),
            ("金血记忆体联萌", "金血记忆体联盟", 0.80),
            ("【额外打击】", "额外打击", 0.99)
        ];
        foreach (var test in cases)
        {
            var result = new GameDataNameMatcher().FindBest(
                test.Recognized,
                candidates,
                value => value);

            Assert.NotNull(result);
            Assert.Equal(test.Expected, result.CanonicalName);
            Assert.True(result.Confidence >= test.MinimumConfidence);
        }
    }

    [Fact]
    public void RejectsUnrelatedText()
    {
        var result = new GameDataNameMatcher().FindBest(
            "确认",
            new[] { "白银时代", "敌后破坏" },
            value => value);

        Assert.Null(result);
    }

    [Fact]
    public void MatchesUniqueTruncatedEnemyAffix()
    {
        var candidates = new[]
        {
            "沉重脚步",
            "重症难题",
            "灼热轰炸",
            "第三位面强化",
            "库藏生锈"
        };

        (string Recognized, string Expected)[] cases =
        [
            ("沉 重", "沉重脚步"),
            ("0 重 症", "重症难题"),
            ("多 灼 热 轰", "灼热轰炸")
        ];
        foreach (var test in cases)
        {
            var result = new GameDataNameMatcher().FindBest(
                test.Recognized,
                candidates,
                value => value);

            Assert.NotNull(result);
            Assert.Equal(test.Expected, result.CanonicalName);
        }
    }

    [Fact]
    public void RejectsAmbiguousShortFragment()
    {
        var result = new GameDataNameMatcher().FindBest(
            "额外",
            new[] { "额外打击", "额外护盾" },
            value => value);

        Assert.Null(result);
    }

    [Fact]
    public void MatchesEveryTwoCharacterInvestmentStrategyWithinItsPlane()
    {
        var catalog = LoadCatalog();
        var matcher = new GameDataNameMatcher();
        var shortStrategies = catalog.InvestmentStrategies
            .Where(strategy =>
                GameDataNameMatcher.Normalize(strategy.Name).Length == 2)
            .ToArray();

        Assert.Equal(12, shortStrategies.Length);
        foreach (var strategy in shortStrategies)
        {
            foreach (var plane in strategy.AvailablePlanes)
            {
                var candidates = catalog.InvestmentStrategies
                    .Where(candidate => candidate.AvailablePlanes.Contains(plane));
                var result = matcher.FindBest(
                    strategy.Name,
                    candidates,
                    candidate => candidate.Name,
                    0.68);

                Assert.NotNull(result);
                Assert.Equal(strategy.Id, result.Value.Id);
            }
        }
    }

    [Fact]
    public void InvestmentStrategyNamesAreUnambiguousWithinEachPlane()
    {
        var catalog = LoadCatalog();

        foreach (var plane in Enumerable.Range(1, 3))
        {
            (string Label, Func<InvestmentStrategyData, string> Select)[] keys =
            [
                ("name", strategy => strategy.Name),
                ("effect", strategy => strategy.Effect)
            ];
            foreach (var key in keys)
            {
                var duplicateKeys = catalog.InvestmentStrategies
                    .Where(strategy =>
                        strategy.AvailablePlanes.Contains(plane))
                    .GroupBy(strategy =>
                        GameDataNameMatcher.Normalize(key.Select(strategy)))
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();

                Assert.True(
                    duplicateKeys.Length == 0,
                    $"Plane {plane} has duplicate strategy {key.Label} keys: " +
                    string.Join(", ", duplicateKeys));
            }
        }
    }

    [Fact]
    public void EveryInvestmentEnvironmentDescriptionIdentifiesOneEntry()
    {
        var catalog = LoadCatalog();
        var matcher = new GameDataNameMatcher();

        foreach (var environment in catalog.InvestmentEnvironments)
        {
            var result = matcher.FindBest(
                environment.Effect,
                catalog.InvestmentEnvironments,
                candidate => candidate.Effect,
                0.68);

            Assert.True(
                result is not null,
                $"Description did not uniquely identify {environment.Id} " +
                $"({environment.Name}).");
            Assert.Equal(environment.Id, result.Value.Id);
        }
    }

    private static GameDataCatalog LoadCatalog()
    {
        var dataDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../data/4.4"));
        return GameDataCatalogLoader.Load(dataDirectory);
    }
}
