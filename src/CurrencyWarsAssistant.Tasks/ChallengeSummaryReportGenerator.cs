using System.Text;
using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

public interface IChallengeSummaryReportGenerator
{
    Task<string> GenerateAsync(
        string runDirectory,
        CompletedRunRecord run,
        CancellationToken cancellationToken);

    Task<string> GenerateFromArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken);
}

public sealed class ChallengeSummaryReportGenerator :
    IChallengeSummaryReportGenerator
{
    private readonly ChallengeReportModelBuilder _modelBuilder;
    private readonly ChallengeReportHtmlRenderer _htmlRenderer;
    private readonly ChallengeReportMarkdownRenderer _markdownRenderer;

    public ChallengeSummaryReportGenerator(GameDataCatalog? catalog = null)
    {
        _modelBuilder = new ChallengeReportModelBuilder(catalog);
        _htmlRenderer = new ChallengeReportHtmlRenderer(
            _modelBuilder,
            new ChallengeReportAssetCatalog());
        _markdownRenderer = new ChallengeReportMarkdownRenderer(_modelBuilder);
    }

    public async Task<string> GenerateFromArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(archivePath);
        var json = await File.ReadAllTextAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        var archive = ChallengeRunArchiveReader.Read(json);
        return await GenerateCoreAsync(
                Path.GetDirectoryName(fullPath)!,
                archive.Run,
                archive.ExtensionFields,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<string> GenerateAsync(
        string runDirectory,
        CompletedRunRecord run,
        CancellationToken cancellationToken) =>
        GenerateCoreAsync(runDirectory, run, [], cancellationToken);

    private async Task<string> GenerateCoreAsync(
        string runDirectory,
        CompletedRunRecord run,
        IReadOnlyList<ChallengeReportExtensionField> extensions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentNullException.ThrowIfNull(run);
        var fullDirectory = Path.GetFullPath(runDirectory);
        var reportDirectory = Path.Combine(fullDirectory, "reports");
        Directory.CreateDirectory(reportDirectory);
        var model = _modelBuilder.Build(run, extensions);
        var htmlPath = Path.Combine(reportDirectory, "challenge-summary.html");
        var markdownPath = Path.Combine(reportDirectory, "challenge-summary.md");
        await WriteAtomicallyAsync(
                htmlPath,
                _htmlRenderer.Render(model),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteAtomicallyAsync(
                markdownPath,
                _markdownRenderer.Render(model),
                cancellationToken)
            .ConfigureAwait(false);
        return htmlPath;
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
