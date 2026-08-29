using System.Globalization;
using System.Text.Json;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

internal sealed record ChallengeRunArchiveReadResult(
    CompletedRunRecord Run,
    IReadOnlyList<ChallengeReportExtensionField> ExtensionFields);

internal static class ChallengeRunArchiveReader
{
    public static ChallengeRunArchiveReadResult Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var extensions = ChallengeReportModelBuilder.ReadExtensions(document).ToList();
        try
        {
            return new ChallengeRunArchiveReadResult(
                AdvisorJson.Deserialize<CompletedRunRecord>(json),
                extensions);
        }
        catch (JsonException)
        {
            var root = document.RootElement;
            var uncertainty = ReadStringArray(root, "uncertainty").ToList();
            var completedAt = ReadDateTimeOffset(root, "completedAt", extensions, uncertainty);
            var nodes = ReadNodes(root, extensions, uncertainty);
            var run = new CompletedRunRecord
            {
                SchemaVersion = ReadString(root, "schemaVersion") ?? "unknown",
                ArchiveVersion = ReadInt32(root, "archiveVersion") ?? 0,
                RunId = ReadString(root, "runId") ?? "unknown-run",
                CompletedAt = completedAt ?? DateTimeOffset.MinValue,
                IsFinal = ReadBoolean(root, "isFinal") ?? false,
                CompletionPageId = ReadString(root, "completionPageId") ?? "unknown",
                CompletionNodeId = ReadString(root, "completionNodeId") ?? "unknown",
                CompletionScreenshotFile = ReadString(root, "completionScreenshotFile"),
                RatingText = ReadString(root, "ratingText"),
                LastSnapshot = ReadObject<RunSnapshot>(root, "lastSnapshot", extensions, uncertainty),
                LastOperationalState = ReadObject<Phase2OperationalState>(root, "lastOperationalState", extensions, uncertainty),
                Nodes = nodes,
                SourceAnalysisFiles = ReadStringArray(root, "sourceAnalysisFiles"),
                Uncertainty = uncertainty
                    .Append("封存文件包含格式异常字段；报告已保留可读取内容并将异常字段列入附录。")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
            return new ChallengeRunArchiveReadResult(run, extensions);
        }
    }

    private static IReadOnlyList<CompletedRunNodeRecord> ReadNodes(
        JsonElement root,
        ICollection<ChallengeReportExtensionField> extensions,
        ICollection<string> uncertainty)
    {
        if (!root.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<CompletedRunNodeRecord>();
        var index = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            try
            {
                var parsed = node.Deserialize<CompletedRunNodeRecord>(AdvisorJson.Options);
                if (parsed is not null)
                {
                    result.Add(parsed);
                    index++;
                    continue;
                }
            }
            catch (JsonException)
            {
                // Degrade this node below while retaining its raw payload.
            }

            var nodeId = node.ValueKind == JsonValueKind.Object &&
                         node.TryGetProperty("nodeId", out var id) &&
                         id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? $"unknown-{index + 1}"
                : $"unknown-{index + 1}";
            result.Add(new CompletedRunNodeRecord(
                nodeId,
                null,
                null,
                null,
                null,
                null));
            extensions.Add(new ChallengeReportExtensionField(
                $"malformed.nodes[{nodeId}]",
                Compact(node)));
            uncertainty.Add($"节点 {nodeId} 的部分字段格式异常；已保留节点占位和原始 JSON。" );
            index++;
        }

        return result;
    }

    private static T? ReadObject<T>(
        JsonElement root,
        string propertyName,
        ICollection<ChallengeReportExtensionField> extensions,
        ICollection<string> uncertainty)
        where T : class
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return value.Deserialize<T>(AdvisorJson.Options);
        }
        catch (JsonException)
        {
            extensions.Add(new ChallengeReportExtensionField(
                $"malformed.{propertyName}",
                Compact(value)));
            uncertainty.Add($"字段 {propertyName} 格式异常，未用于评价。" );
            return null;
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(
        JsonElement root,
        string propertyName,
        ICollection<ChallengeReportExtensionField> extensions,
        ICollection<string> uncertainty)
    {
        var raw = ReadString(root, propertyName);
        if (raw is not null && DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value))
        {
            return value;
        }

        if (root.TryGetProperty(propertyName, out var malformed))
        {
            extensions.Add(new ChallengeReportExtensionField(
                $"malformed.{propertyName}",
                Compact(malformed)));
            uncertainty.Add($"字段 {propertyName} 格式异常，报告显示为未记录。" );
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static bool? ReadBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static string Compact(JsonElement value)
    {
        var json = value.GetRawText();
        return json.Length <= 800 ? json : json[..800] + "…";
    }
}
