using System.IO;
using System.Text.Json;

namespace CurrencyWarsAssistant.Vision;

public sealed class GamePageDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public int Priority { get; init; }
    public int? MinimumAnchorMatches { get; init; }
    public required IReadOnlyList<TemplateDefinition> Anchors { get; init; }
}

public sealed record PageClassificationResult(
    string PageId,
    string DisplayName,
    double Confidence,
    IReadOnlyList<TemplateMatchResult> AnchorMatches);

public interface IGamePageClassifier
{
    PageClassificationResult? Classify(CaptureFrame frame);
}

public sealed record PageAnchorDiagnostic(
    string PageId,
    string AnchorId,
    double Confidence,
    double Threshold);

public interface IGamePageClassifierDiagnostics
{
    IReadOnlyList<PageAnchorDiagnostic> LastDiagnostics { get; }
}

public sealed class TemplateGamePageClassifier(
    ITemplateMatcher templateMatcher,
    IReadOnlyList<GamePageDefinition> pages) :
    IGamePageClassifier,
    IGamePageClassifierDiagnostics
{
    public IReadOnlyList<PageAnchorDiagnostic> LastDiagnostics { get; private set; } = [];

    public PageClassificationResult? Classify(CaptureFrame frame)
    {
        PageClassificationResult? best = null;
        var bestPriority = int.MinValue;
        var diagnostics = new List<PageAnchorDiagnostic>();
        var allAnchors = pages.SelectMany(page => page.Anchors).ToArray();
        var probeByAnchor = new Dictionary<
            TemplateDefinition,
            TemplateMatchResult?>();
        if (templateMatcher is IBatchTemplateMatcher batchMatcher)
        {
            var probes = batchMatcher.ProbeMany(frame, allAnchors);
            for (var index = 0; index < allAnchors.Length; index++)
            {
                probeByAnchor[allAnchors[index]] = probes[index];
            }
        }
        else
        {
            foreach (var anchor in allAnchors)
            {
                probeByAnchor[anchor] = templateMatcher.Probe(frame, anchor);
            }
        }

        foreach (var page in pages)
        {
            if (page.Anchors.Count == 0)
            {
                continue;
            }

            var minimumAnchorMatches =
                page.MinimumAnchorMatches ?? page.Anchors.Count;
            var matches = new List<TemplateMatchResult>(page.Anchors.Count);
            foreach (var anchor in page.Anchors)
            {
                var probe = probeByAnchor[anchor];
                diagnostics.Add(new PageAnchorDiagnostic(
                    page.Id,
                    anchor.Id,
                    probe?.Confidence ?? 0,
                    anchor.Threshold));
                if (probe is null || probe.Confidence < anchor.Threshold)
                {
                    if (minimumAnchorMatches == page.Anchors.Count)
                    {
                        matches.Clear();
                        break;
                    }

                    continue;
                }

                matches.Add(probe);
            }

            if (matches.Count < minimumAnchorMatches)
            {
                continue;
            }

            var confidence = matches.Average(match => match.Confidence);
            if (best is null ||
                page.Priority > bestPriority ||
                (page.Priority == bestPriority && confidence > best.Confidence))
            {
                best = new PageClassificationResult(
                    page.Id,
                    page.DisplayName,
                    confidence,
                    matches);
                bestPriority = page.Priority;
            }
        }

        LastDiagnostics = diagnostics;
        return best;
    }
}

public sealed class GamePageRecognitionConfig
{
    public int SchemaVersion { get; init; } = 1;
    public required IReadOnlyList<GamePageDefinition> Pages { get; init; }

    public static GamePageRecognitionConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var json = File.ReadAllText(fullPath);
        var config = JsonSerializer.Deserialize<GamePageRecognitionConfig>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? throw new InvalidDataException($"无法读取页面识别配置：{fullPath}");

        var configDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException($"配置文件没有父目录：{fullPath}");
        var pageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedPages = new List<GamePageDefinition>(config.Pages.Count);
        foreach (var page in config.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Id) || !pageIds.Add(page.Id))
            {
                throw new InvalidDataException($"页面 ID 为空或重复：{page.Id}");
            }

            if (page.Anchors.Count == 0)
            {
                throw new InvalidDataException($"页面至少需要一个识别锚点：{page.Id}");
            }
            if (page.MinimumAnchorMatches is < 1 ||
                page.MinimumAnchorMatches > page.Anchors.Count)
            {
                throw new InvalidDataException(
                    $"页面 {page.Id} 的最少锚点数必须介于 1 和 " +
                    $"{page.Anchors.Count} 之间。");
            }

            var anchorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resolvedAnchors = new List<TemplateDefinition>(page.Anchors.Count);
            foreach (var anchor in page.Anchors)
            {
                if (string.IsNullOrWhiteSpace(anchor.Id) || !anchorIds.Add(anchor.Id))
                {
                    throw new InvalidDataException(
                        $"页面 {page.Id} 的锚点 ID 为空或重复：{anchor.Id}");
                }

                var templatePath = Path.IsPathFullyQualified(anchor.File)
                    ? anchor.File
                    : Path.GetFullPath(Path.Combine(configDirectory, anchor.File));
                resolvedAnchors.Add(new TemplateDefinition
                {
                    Id = anchor.Id,
                    DisplayName = anchor.DisplayName,
                    File = templatePath,
                    SearchRegion = anchor.SearchRegion,
                    Threshold = anchor.Threshold,
                    Grayscale = anchor.Grayscale,
                    EdgeDetection = anchor.EdgeDetection
                });
            }

            resolvedPages.Add(new GamePageDefinition
            {
                Id = page.Id,
                DisplayName = page.DisplayName,
                Priority = page.Priority,
                MinimumAnchorMatches = page.MinimumAnchorMatches,
                Anchors = resolvedAnchors
            });
        }

        return new GamePageRecognitionConfig
        {
            SchemaVersion = config.SchemaVersion,
            Pages = resolvedPages
        };
    }
}
