using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed class OpeningRecognitionConfig
{
    public required TemplateDefinition OpeningPageAnchor { get; init; }
    public List<TemplateDefinition> InvestmentEnvironments { get; init; } = [];
    public List<TemplateDefinition> Competitors { get; init; } = [];
    public List<TemplateDefinition> EnemyModifiers { get; init; } = [];
    public List<RerollWorkflowStep> RerollSteps { get; init; } = [];
}

public sealed class RerollWorkflowStep
{
    public required string Id { get; init; }
    public required TemplateDefinition Target { get; init; }
    public TemplateDefinition? ExpectedAfter { get; init; }
    public int TimeoutMilliseconds { get; init; } = 8000;
}

public static class RecognitionConfigPathResolver
{
    public static OpeningRecognitionConfig Resolve(
        OpeningRecognitionConfig config,
        string baseDirectory)
    {
        TemplateDefinition ResolveTemplate(TemplateDefinition value) =>
            new()
            {
                Id = value.Id,
                DisplayName = value.DisplayName,
                File = Path.IsPathFullyQualified(value.File)
                    ? value.File
                    : Path.GetFullPath(value.File, baseDirectory),
                SearchRegion = value.SearchRegion,
                Threshold = value.Threshold
            };

        return new OpeningRecognitionConfig
        {
            OpeningPageAnchor = ResolveTemplate(config.OpeningPageAnchor),
            InvestmentEnvironments =
                config.InvestmentEnvironments.Select(ResolveTemplate).ToList(),
            Competitors =
                config.Competitors.Select(ResolveTemplate).ToList(),
            EnemyModifiers =
                config.EnemyModifiers.Select(ResolveTemplate).ToList(),
            RerollSteps = config.RerollSteps.Select(step => new RerollWorkflowStep
            {
                Id = step.Id,
                Target = ResolveTemplate(step.Target),
                ExpectedAfter = step.ExpectedAfter is null
                    ? null
                    : ResolveTemplate(step.ExpectedAfter),
                TimeoutMilliseconds = step.TimeoutMilliseconds
            }).ToList()
        };
    }
}
