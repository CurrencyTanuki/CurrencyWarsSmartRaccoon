using System.Diagnostics;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// Shares one immutable recognition warm-up across application startup, live
/// collection and headless replay. It never captures the screen or sends input.
/// </summary>
public sealed class Phase2RecognitionWarmUpService
{
    private readonly Phase2OfflineOcrSet ocr;
    private readonly ITemplateMatcher pageMatcher;
    private readonly IReadOnlyList<GamePageDefinition> pages;
    private readonly ICharacterCardRecognizer characterRecognizer;
    private readonly IReadOnlyList<CharacterCardTemplateDefinition>
        characterTemplates;
    private readonly IPhase2IconRecognizer iconRecognizer;
    private readonly IReadOnlyList<Phase2IconTemplateDefinition> iconTemplates;
    private readonly Lazy<Task> warmUp;

    public Phase2RecognitionWarmUpService(
        Phase2OfflineOcrSet ocr,
        ITemplateMatcher pageMatcher,
        IReadOnlyList<GamePageDefinition> pages,
        ICharacterCardRecognizer characterRecognizer,
        IReadOnlyList<CharacterCardTemplateDefinition> characterTemplates,
        IPhase2IconRecognizer iconRecognizer,
        IReadOnlyList<Phase2IconTemplateDefinition> iconTemplates)
    {
        this.ocr = ocr;
        this.pageMatcher = pageMatcher;
        this.pages = pages;
        this.characterRecognizer = characterRecognizer;
        this.characterTemplates = characterTemplates;
        this.iconRecognizer = iconRecognizer;
        this.iconTemplates = iconTemplates;
        warmUp = new Lazy<Task>(
            WarmUpCoreAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public TimeSpan? Elapsed { get; private set; }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return cancellationToken.CanBeCanceled
            ? warmUp.Value.WaitAsync(cancellationToken)
            : warmUp.Value;
    }

    private async Task WarmUpCoreAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var work = new List<Task>
        {
            ocr.WarmUpAsync(CancellationToken.None)
        };
        if (pageMatcher is OpenCvTemplateMatcher openCvPageMatcher)
        {
            work.Add(openCvPageMatcher.WarmUpAsync(
                pages.SelectMany(page => page.Anchors).ToArray(),
                CancellationToken.None));
        }

        if (characterRecognizer is OpenCvCharacterCardRecognizer
            openCvCharacterRecognizer)
        {
            work.Add(openCvCharacterRecognizer.WarmUpAsync(
                characterTemplates,
                CancellationToken.None));
        }

        if (iconRecognizer is OpenCvPhase2IconRecognizer openCvIconRecognizer)
        {
            work.Add(openCvIconRecognizer.WarmUpAsync(
                iconTemplates,
                CancellationToken.None));
        }

        await Task.WhenAll(work).ConfigureAwait(false);
        stopwatch.Stop();
        Elapsed = stopwatch.Elapsed;
    }
}
