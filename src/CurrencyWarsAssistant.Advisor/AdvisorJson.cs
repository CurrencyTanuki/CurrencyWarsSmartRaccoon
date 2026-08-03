using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurrencyWarsAssistant.Advisor;

public static class AdvisorJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions(Options) { WriteIndented = indented });

    public static T Deserialize<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        EnsureCurrentVersion(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException(
                $"Unable to deserialize {typeof(T).Name}.");
    }

    public static void EnsureCurrentVersion(JsonElement root)
    {
        if (!root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("schemaVersion is required.");
        }

        if (!string.Equals(
                version.GetString(),
                AdvisorContractVersions.Current,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported schemaVersion '{version.GetString()}'; " +
                $"expected '{AdvisorContractVersions.Current}'.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class GuideRepository
{
    public IReadOnlyList<GuidePlaybook> LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            return [];
        }

        var guides = Directory
            .EnumerateFiles(fullDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => LoadFile(path))
            .ToArray();
        var duplicate = guides
            .GroupBy(guide => guide.GuideId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Duplicate guideId '{duplicate.Key}' in {fullDirectory}.");
        }

        return guides;
    }

    public GuidePlaybook LoadFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var guide = AdvisorJson.Deserialize<GuidePlaybook>(
            File.ReadAllText(fullPath));
        Validate(guide, fullPath);
        return guide;
    }

    private static void Validate(GuidePlaybook guide, string path)
    {
        if (string.IsNullOrWhiteSpace(guide.GuideId) ||
            string.IsNullOrWhiteSpace(guide.Title) ||
            string.IsNullOrWhiteSpace(guide.ArchetypeId) ||
            guide.GoalIds.Count == 0 ||
            guide.Rules.Count == 0 ||
            guide.Sources.Count == 0)
        {
            throw new InvalidDataException(
                $"Guide is missing required runtime data: {path}");
        }

        if (!Version.TryParse(guide.ApplicableGameVersion, out _))
        {
            throw new InvalidDataException(
                $"Guide has invalid game version: {path}");
        }

        var sourceIds = guide.Sources
            .Select(source => source.SourceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in guide.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RuleId) ||
                string.IsNullOrWhiteSpace(rule.Action) ||
                rule.Sources.Count == 0 ||
                rule.Sources.Any(source => !sourceIds.Contains(source.SourceId)))
            {
                throw new InvalidDataException(
                    $"Guide rule '{rule.RuleId}' has invalid source references: {path}");
            }
        }
    }
}
