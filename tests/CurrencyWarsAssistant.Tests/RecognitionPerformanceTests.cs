using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 识别性能实测（用户 2026-08-07）：单帧 AnalyzeAsync 耗时 + 内存。
/// </summary>
public sealed class RecognitionPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public RecognitionPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact(Skip = "Legacy non-production Windows OCR benchmark; validate production performance separately with App --phase2-batch-test evidence.")]
    public async Task MeasureSingleFrameRecognitionTimeAndMemory()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05",
                ]),
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"),
            gameData,
            new WindowsOfflineOcr("en-US"));

        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        _output.WriteLine($"帧尺寸: {frame.Width}x{frame.Height} ({frame.Width * frame.Height * 4 / 1024 / 1024} MB Bgra)");

        // 预热一次（加载模板缓存等）
        await analyzer.AnalyzeAsync(
            frame, "preparation_generic", "fixture:warmup",
            EmptySnapshot(frame.CapturedAt), CancellationToken.None);

        var times = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var before = GC.GetTotalMemory(forceFullCollection: false);
            await analyzer.AnalyzeAsync(
                frame, "preparation_generic", $"fixture:perf-{i}",
                EmptySnapshot(frame.CapturedAt), CancellationToken.None);
            sw.Stop();
            var after = GC.GetTotalMemory(forceFullCollection: false);
            times.Add(sw.Elapsed.TotalSeconds);
            _output.WriteLine(
                $"第 {i + 1} 帧: {sw.Elapsed.TotalSeconds:F2}s, " +
                $"GC 内存增量 {(after - before) / 1024 / 1024:F1} MB");
        }

        var avg = times.Average();
        var min = times.Min();
        _output.WriteLine(
            $"平均 {avg:F2}s/帧 | 最快 {min:F2}s | 实时流 2s/帧间隔 => " +
            $"{(avg <= 2 ? "可实时（<2s）" : $"超实时（>{2}s，会堆积）")}");
        Assert.True(min < 3, $"单帧识别过慢：{min:F2}s");
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "perf",
        AsOf = asOf
    };

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(GameDataCatalog gameData)
    {
        var directory = Path.Combine(RepositoryRoot, "data", "4.4", "character-card-templates");
        return gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                directory,
                $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(
                    character.Id,
                    character.Name,
                    file)))
            .ToList();
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
