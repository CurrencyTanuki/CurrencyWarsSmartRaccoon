using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurrencyWarsAssistant.Game;

public sealed record InvestmentEnvironmentData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("related_characters")] IReadOnlyList<string> RelatedCharacters,
    [property: JsonPropertyName("related_equipment")] IReadOnlyList<string> RelatedEquipment,
    [property: JsonPropertyName("source")] string Source);

public sealed record InvestmentStrategyData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rarity")] string Rarity,
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("available_planes")] IReadOnlyList<int> AvailablePlanes,
    [property: JsonPropertyName("source")] string Source);

public static class InvestmentStrategyVersionCatalog
{
    // BWIKI records 27 strategies added in 4.2 and 19 in 4.4.  The cleaned
    // dataset appends these two batches contiguously in source order.
    private const int Version42FirstId = 289;
    private const int Version42LastId = 315;
    private const int Version44FirstId = 316;
    private const int Version44LastId = 334;

    public static string? GetIntroducedVersion(string strategyId)
    {
        var sequence = GetSequence(strategyId);
        return sequence switch
        {
            >= Version44FirstId and <= Version44LastId => "4.4",
            >= Version42FirstId and <= Version42LastId => "4.2",
            _ => null
        };
    }

    public static int GetNewestFirstRank(string strategyId) =>
        GetIntroducedVersion(strategyId) switch
        {
            "4.4" => 2,
            "4.2" => 1,
            _ => 0
        };

    private static int GetSequence(string strategyId)
    {
        var separator = strategyId.LastIndexOf('_');
        return separator >= 0 &&
               int.TryParse(strategyId[(separator + 1)..], out var sequence)
            ? sequence
            : -1;
    }
}

public sealed record EnemyAffixData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tier")] int Tier,
    [property: JsonPropertyName("effect")] string Effect,
    [property: JsonPropertyName("tier_source_file")] string TierSourceFile,
    [property: JsonPropertyName("current_effect_source")] string CurrentEffectSource);

public sealed record CompetitorData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("boss_image")] string BossImage,
    [property: JsonPropertyName("elite_enemy_images")] IReadOnlyList<string> EliteEnemyImages,
    [property: JsonPropertyName("normal_enemy_images")] IReadOnlyList<string> NormalEnemyImages);

public sealed record CurrencyWarsCharacterData(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("position")] string Position,
    [property: JsonPropertyName("costs")] IReadOnlyList<int> Costs,
    [property: JsonPropertyName("expert_advisor_available")] bool ExpertAdvisorAvailable,
    [property: JsonPropertyName("bonds")] IReadOnlyList<string>? Bonds = null)
{
    public IReadOnlyList<string> BondNames => Bonds ?? [];
}

public sealed class GameDataCatalog
{
    public GameDataCatalog(
        IReadOnlyList<InvestmentEnvironmentData> investmentEnvironments,
        IReadOnlyList<InvestmentStrategyData> investmentStrategies,
        IReadOnlyList<EnemyAffixData> enemyAffixes,
        IReadOnlyList<CompetitorData> competitors,
        IReadOnlyList<CurrencyWarsCharacterData> currencyWarsCharacters)
    {
        InvestmentEnvironments = investmentEnvironments;
        InvestmentStrategies = investmentStrategies;
        EnemyAffixes = enemyAffixes;
        Competitors = competitors;
        CurrencyWarsCharacters = currencyWarsCharacters;

        InvestmentEnvironmentsByName = CreateNameIndex(investmentEnvironments, item => item.Name);
        InvestmentStrategiesByName = CreateNameIndex(investmentStrategies, item => item.Name);
        EnemyAffixesByName = CreateNameIndex(enemyAffixes, item => item.Name);
        CompetitorsByName = CreateNameIndex(competitors, item => item.Name);
        CurrencyWarsCharactersByName =
            CreateNameIndex(currencyWarsCharacters, item => item.Name);
    }

    public IReadOnlyList<InvestmentEnvironmentData> InvestmentEnvironments { get; }
    public IReadOnlyList<InvestmentStrategyData> InvestmentStrategies { get; }
    public IReadOnlyList<EnemyAffixData> EnemyAffixes { get; }
    public IReadOnlyList<CompetitorData> Competitors { get; }
    public IReadOnlyList<CurrencyWarsCharacterData> CurrencyWarsCharacters { get; }

    public IReadOnlyDictionary<string, InvestmentEnvironmentData> InvestmentEnvironmentsByName
    {
        get;
    }

    public IReadOnlyDictionary<string, InvestmentStrategyData> InvestmentStrategiesByName
    {
        get;
    }

    public IReadOnlyDictionary<string, EnemyAffixData> EnemyAffixesByName { get; }
    public IReadOnlyDictionary<string, CompetitorData> CompetitorsByName { get; }
    public IReadOnlyDictionary<string, CurrencyWarsCharacterData>
        CurrencyWarsCharactersByName { get; }

    private static IReadOnlyDictionary<string, T> CreateNameIndex<T>(
        IEnumerable<T> values,
        Func<T, string> nameSelector) =>
        values.ToDictionary(nameSelector, StringComparer.OrdinalIgnoreCase);
}

public static class GameDataCatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static GameDataCatalog Load(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"游戏数据目录不存在：{fullDirectory}");
        }

        var catalog = new GameDataCatalog(
            LoadFile<InvestmentEnvironmentData>(
                fullDirectory,
                "investment-environments.json"),
            LoadFile<InvestmentStrategyData>(
                fullDirectory,
                "investment-strategies.json"),
            LoadFile<EnemyAffixData>(
                fullDirectory,
                "enemy-affixes.json"),
            LoadFile<CompetitorData>(
                fullDirectory,
                "competitors.json"),
            LoadCharacters(fullDirectory));

        Validate(catalog);
        return catalog;
    }

    private static IReadOnlyList<T> LoadFile<T>(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"缺少游戏数据文件：{path}", path);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<T>>(json, JsonOptions)
               ?? throw new InvalidDataException($"游戏数据文件无效：{path}");
    }

    private static IReadOnlyList<CurrencyWarsCharacterData> LoadCharacters(
        string directory)
    {
        const string fileName = "currency-wars-characters.json";
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"缺少游戏数据文件：{path}", path);
        }

        var export = JsonSerializer.Deserialize<CurrencyWarsCharacterExport>(
            File.ReadAllText(path),
            JsonOptions);
        return export?.Characters
               ?? throw new InvalidDataException($"游戏数据文件无效：{path}");
    }

    private static void Validate(GameDataCatalog catalog)
    {
        RequireCount(catalog.InvestmentEnvironments, 83, "投资环境");
        RequireCount(catalog.InvestmentStrategies, 334, "投资策略");
        RequireCount(catalog.EnemyAffixes, 51, "敌人词缀");
        RequireCount(catalog.Competitors, 20, "竞争对手");
        RequireCount(catalog.CurrencyWarsCharacters, 71, "货币战争角色");

        if (catalog.EnemyAffixes.Any(item => item.Tier is < 1 or > 3))
        {
            throw new InvalidDataException("敌人词缀数据包含无效层级。");
        }

        var validRarities = new HashSet<string>(["银色", "金色", "棱彩"]);
        if (catalog.InvestmentStrategies.Any(item => !validRarities.Contains(item.Rarity)))
        {
            throw new InvalidDataException("投资策略数据包含未知品质。");
        }

        if (catalog.CurrencyWarsCharacters.Any(item =>
                item.Costs.Count == 0 ||
                item.Costs.Any(cost => cost is < 1 or > 5)))
        {
            throw new InvalidDataException("货币战争角色数据包含无效费用。");
        }
    }

    private static void RequireCount<T>(
        IReadOnlyCollection<T> values,
        int expected,
        string displayName)
    {
        if (values.Count != expected)
        {
            throw new InvalidDataException(
                $"{displayName}记录数量错误：预期 {expected}，实际 {values.Count}。");
        }
    }

    private sealed record CurrencyWarsCharacterExport(
        [property: JsonPropertyName("characters")]
        IReadOnlyList<CurrencyWarsCharacterData> Characters);
}
