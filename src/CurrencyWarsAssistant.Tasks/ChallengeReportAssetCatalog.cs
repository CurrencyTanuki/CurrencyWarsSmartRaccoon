using System.Text.Json;

namespace CurrencyWarsAssistant.Tasks;

internal sealed class ChallengeReportAssetCatalog
{
    private readonly IReadOnlyDictionary<string, string> _characterAvatarPaths;

    public ChallengeReportAssetCatalog(string? dataDirectory = null)
    {
        DataDirectory = ResolveDataDirectory(dataDirectory);
        _characterAvatarPaths = LoadCharacterAvatarPaths(DataDirectory);
    }

    public string? DataDirectory { get; }

    public string? GetCharacterAvatarDataUri(string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId) ||
            DataDirectory is null ||
            !_characterAvatarPaths.TryGetValue(characterId, out var relativePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(DataDirectory, relativePath));
        if (!File.Exists(fullPath))
        {
            fullPath = Path.GetFullPath(Path.Combine(
                DataDirectory,
                "character-small-avatar-templates",
                Path.GetFileName(relativePath)));
        }

        if (!fullPath.StartsWith(DataDirectory, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var mime = Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png"
            };
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(fullPath))}";
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ResolveDataDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(Path.Combine(full, "currency-wars-characters.json")))
            {
                return full;
            }
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "data", "4.4");
            if (File.Exists(Path.Combine(candidate, "currency-wars-characters.json")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> LoadCharacterAvatarPaths(
        string? dataDirectory)
    {
        if (dataDirectory is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(dataDirectory, "currency-wars-characters.json")));
            if (!document.RootElement.TryGetProperty("characters", out var characters) ||
                characters.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var character in characters.EnumerateArray())
            {
                if (!character.TryGetProperty("id", out var idProperty) ||
                    string.IsNullOrWhiteSpace(idProperty.GetString()) ||
                    !character.TryGetProperty("avatar_paths", out var paths) ||
                    paths.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var path in paths.EnumerateArray())
                {
                    if (!path.TryGetProperty("small_avatar", out var small) ||
                        small.ValueKind != JsonValueKind.Object ||
                        !small.TryGetProperty("local_path", out var localPath) ||
                        string.IsNullOrWhiteSpace(localPath.GetString()))
                    {
                        continue;
                    }

                    result.TryAdd(idProperty.GetString()!, localPath.GetString()!);
                    break;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
