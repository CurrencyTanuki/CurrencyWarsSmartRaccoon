using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 羁绊等级阈值（几人口激活几级）来自数据库 tier_effects（用户 2026-08-07
/// 要求）：验证 bond_catalog 加载 + 星核猎手 2/3/4 人门槛。
/// </summary>
public sealed class BondTierCatalogTests
{
    [Fact]
    public void LoadsBondCatalogWithTierEffects()
    {
        var directory = Path.Combine(RepositoryRoot, "data", "4.4");
        GameDataCatalogLoader.Load(directory);
        var catalog = GameDataCatalog.BondCatalog;
        Assert.NotEmpty(catalog);

        // 星核猎手（银狼所在羁绊）：2 人激活 1 级、3 人 2 级、4 人 3 级
        var stelleron = catalog.FirstOrDefault(
            item => item.Name.Contains("星核猎手", StringComparison.Ordinal));
        Assert.NotNull(stelleron);
        var tiers = stelleron!.TierEffects?
            .Select(t => t.RequiredMembers)
            .OrderBy(v => v)
            .ToArray() ?? [];
        Assert.NotEmpty(tiers);
        Assert.Equal(2, tiers[0]);
        Console.WriteLine(
            $"星核猎手 tier 人口门槛: [{string.Join(",", tiers)}]");
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
