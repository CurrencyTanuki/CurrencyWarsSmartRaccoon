using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed record OpeningReadResult(
    bool IsOpeningPage,
    OpeningObservation? Observation);

public interface IOpeningReader
{
    ValueTask<OpeningReadResult> ReadAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken);
}

public sealed class TemplateOpeningReader(
    ITemplateMatcher matcher,
    OpeningRecognitionConfig config) : IOpeningReader
{
    public ValueTask<OpeningReadResult> ReadAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = matcher.Find(frame, config.OpeningPageAnchor);
        if (page is null)
        {
            return ValueTask.FromResult(new OpeningReadResult(false, null));
        }

        var investments = config.InvestmentEnvironments
            .Select(template => matcher.Find(frame, template))
            .Where(match => match is not null)
            .Cast<TemplateMatchResult>()
            .OrderByDescending(match => match.Confidence)
            .ToArray();

        var competitors = config.Competitors
            .Select(template => matcher.Find(frame, template))
            .Where(match => match is not null)
            .Cast<TemplateMatchResult>()
            .OrderByDescending(match => match.Confidence)
            .Select(match => new ObservedItem(
                match.Id,
                match.DisplayName,
                match.Confidence))
            .ToArray();

        var modifiers = config.EnemyModifiers
            .Select(template => matcher.Find(frame, template))
            .Where(match => match is not null)
            .Cast<TemplateMatchResult>()
            .OrderByDescending(match => match.Confidence)
            .Select(match => new ObservedItem(
                match.Id,
                match.DisplayName,
                match.Confidence))
            .ToArray();

        var investment = investments.FirstOrDefault();
        var observation = new OpeningObservation(
            investment is null
                ? null
                : new ObservedItem(
                    investment.Id,
                    investment.DisplayName,
                    investment.Confidence),
            competitors,
            modifiers,
            page.Confidence,
            frame.CapturedAt);

        return ValueTask.FromResult(new OpeningReadResult(true, observation));
    }
}
