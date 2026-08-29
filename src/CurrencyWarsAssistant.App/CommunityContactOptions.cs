using System.IO;
using System.Text.Json;

namespace CurrencyWarsAssistant.App;

public sealed record CommunityContactOptions
{
    public int SchemaVersion { get; init; } = 1;
    public string QqGroup { get; init; } = "待补充";
    public string OfficialWebsite { get; init; } = "待补充";
    public string SourceRepository { get; init; } = "待补充";
    public string IssueTracker { get; init; } = "待补充";

    public string QqGroupDisplay => Normalize(QqGroup);
    public string OfficialWebsiteDisplay => Normalize(OfficialWebsite);
    public string SourceRepositoryDisplay => Normalize(SourceRepository);
    public string IssueTrackerDisplay => Normalize(IssueTracker);

    public static CommunityContactOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CommunityContactOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<CommunityContactOptions>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       }) ?? new CommunityContactOptions();
        }
        catch (JsonException)
        {
            return new CommunityContactOptions();
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "待补充" : value.Trim();
}
