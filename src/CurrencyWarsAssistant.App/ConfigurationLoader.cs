using System.IO;
using System.Text.Json;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.App;

internal static class ConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static OpeningRecognitionConfig LoadRecognition(string path)
    {
        var config = Load<OpeningRecognitionConfig>(path);
        return RecognitionConfigPathResolver.Resolve(
            config,
            Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    internal static OpeningRuleSet LoadRules(string path) =>
        Load<OpeningRuleSet>(path);

    internal static CurrencyWarsNavigationConfig LoadNavigation(string path) =>
        CurrencyWarsNavigationConfig.Load(path);

    private static T Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"缺少配置文件：{path}", path);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidDataException($"配置文件内容为空或无效：{path}");
    }
}
