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

/// <summary>
/// 自动化组件（奖励关/备战/祈愿/聘用书）专用的页面分类器标记：
/// 实例只装载自动化所需页面的锚点模板，单次分类成本约为全量分类器的 1/2.5。
/// 页面判定语义与全量分类器一致，仅覆盖自动化会检查的页面。
/// </summary>
public interface IAutomationPageClassifier : IGamePageClassifier
{
}

/// <summary>自动化组件需要识别的页面集合（全部页面定义的子集）。</summary>
public static class AutomationPageIds
{
    public static readonly IReadOnlySet<string> Ids = new HashSet<string>(StringComparer.Ordinal)
    {
        "currency_wars_home",
        "preparation_generic",
        // 补审 P3 清理（1.2.69）：preparation_1_1/1_2 曾列于此，但 JSON 识别表
        // （page-recognition.1920x1080.json）从无这两个页面定义——Create() 筛选
        // 永远为空选（永假条目）。未来若识别表新增节点级备战页定义，需同步加回。
        "reward_shop",
        "reward_battle",
        "reward_battle_pause",
        "battle_generic",
        "incomplete_lineup_prompt",
        "uncompleted_battle_prompt",
        "challenge_success",
        "challenge_failed",
        "challenge_health_depleted",
        "investment_environment",
        "investment_strategy",
        "wish_trial_selection",
        "companion_selection",
    };

    /// <summary>从全量页面定义中筛出自动化子集并构造分类器。</summary>
    public static TemplateGamePageClassifier Create(
        ITemplateMatcher templateMatcher,
        IReadOnlyList<GamePageDefinition> pages)
    {
        var selected = pages
            .Where(page => Ids.Contains(page.Id))
            .ToArray();
        return new TemplateGamePageClassifier(templateMatcher, selected);
    }
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
    IAutomationPageClassifier,
    IGamePageClassifierDiagnostics
{
    public IReadOnlyList<PageAnchorDiagnostic> LastDiagnostics { get; private set; } = [];

    public PageClassificationResult? Classify(CaptureFrame frame)
    {
        PageClassificationResult? best = null;
        var bestPriority = int.MinValue;
        var diagnostics = new List<PageAnchorDiagnostic>();

        // 1.2.77 CPU 优化：两级探针（coarse-to-fine）。第一级只探每页的"必要代表
        // 锚点集"（页达标则代表集至少一个达标：页达标要求 ≥MinimumAnchorMatches
        // 个锚点达标，故任意 Count-Matches+1 个锚点中必有达标者，取阈值最高者），
        // 快速排除不可能页；第二级仅对候选页补探其余锚点做完整判定。判定语义精确
        // 等价，锚点匹配次数显著下降（普通 CPU 减负的核心手段之一）。
        var probeByAnchor = new Dictionary<
            TemplateDefinition,
            TemplateMatchResult?>();
        void ProbeAll(IReadOnlyList<TemplateDefinition> anchors)
        {
            var pending = anchors.Where(anchor => !probeByAnchor.ContainsKey(anchor)).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            if (templateMatcher is IBatchTemplateMatcher batchMatcher)
            {
                var probes = batchMatcher.ProbeMany(frame, pending);
                for (var index = 0; index < pending.Count; index++)
                {
                    probeByAnchor[pending[index]] = probes[index];
                }
            }
            else
            {
                foreach (var anchor in pending)
                {
                    probeByAnchor[anchor] = templateMatcher.Probe(frame, anchor);
                }
            }
        }

        var candidatePages = new List<GamePageDefinition>();
        foreach (var page in pages)
        {
            if (page.Anchors.Count == 0)
            {
                continue;
            }

            var minimumAnchorMatches =
                page.MinimumAnchorMatches ?? page.Anchors.Count;
            var required = page.Anchors.Count - minimumAnchorMatches + 1;
            var representativeAnchors = page.Anchors
                .OrderByDescending(anchor => anchor.Threshold)
                .Take(required)
                .ToList();
            ProbeAll(representativeAnchors);
            var representativeHit = representativeAnchors.Any(anchor =>
            {
                var probe = probeByAnchor.GetValueOrDefault(anchor);
                return probe is not null && probe.Confidence >= anchor.Threshold;
            });
            if (representativeHit)
            {
                candidatePages.Add(page);
            }
        }

        ProbeAll(candidatePages
            .SelectMany(page => page.Anchors)
            .ToList());

        foreach (var page in candidatePages)
        {
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
