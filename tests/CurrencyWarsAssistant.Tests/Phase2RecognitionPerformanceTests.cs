using System.Diagnostics;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 识别性能基线测试（2026-08-06 会话 V）：
/// 用真实 OCR（PpOcrOfflineOcr）+ 真实帧测量"识别一帧画面"的耗时，
/// 判断 0.2.831 实机"记录器 08:11:13 后停止"是否与识别耗时有关。
/// </summary>
public sealed class Phase2RecognitionPerformanceTests(
    ITestOutputHelper output)
{
    [Theory(Skip = "性能基线测试：显式运行（dotnet test --filter FullyQualifiedName~Phase2RecognitionPerformanceTests）")]
    [InlineData("phase2-2026-07-28/130104.png", "battle_generic")]
    [InlineData("PageReplay/preparation_1_3_stable_2559x1439.png", "preparation_generic")]
    [InlineData("PageReplay/challenge_success_1_1.jpg", "challenge_success")]
    [InlineData("PageReplay/preparation_1_1_cloudgame_2559x1439.png", "preparation_generic")]
    public async Task MeasureFullAnalysisTimePerFrame(
        string fixturePath,
        string pageId)
    {
        var characterRecognizer = new EmptyCharacterRecognizer();
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var ocr = new PpOcrOfflineOcr(Path.Combine(
            RepositoryRoot,
            "data",
            "ocr",
            "rapidocr",
            "PP-OCRv6_rec_small.onnx"));
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            [],
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            ocr,
            gameData,
            new WindowsOfflineOcr("en-US"));

        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            fixturePath));
        var snapshot = new RunSnapshot
        {
            RunId = "perf-test",
            AsOf = frame.CapturedAt
        };

        // 预热一次（OCR 会话/模板缓存）
        await analyzer.AnalyzeAsync(
            frame,
            pageId,
            "fixture:warmup",
            snapshot,
            CancellationToken.None);

        // 正式测量 3 次
        var times = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            await analyzer.AnalyzeAsync(
                frame,
                pageId,
                "fixture:measure",
                snapshot,
                CancellationToken.None);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
        }

        output.WriteLine(
            $"{fixturePath} ({frame.Width}x{frame.Height}): " +
            $"耗时 = {string.Join(", ", times)} ms；" +
            $"平均 = {times.Average():F0} ms");

        // 基线：一帧完整识别（含 OCR）平均应明显低于 10 秒；
        // 若接近/超过说明识别本身是瓶颈（0.2.831 记录器停止的嫌疑之一）。
        Assert.True(
            times.Average() < 10_000,
            $"识别耗时异常：{times.Average():F0} ms/帧");
    }

    private sealed class EmptyCharacterRecognizer : ICharacterCardRecognizer
    {
        public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            IReadOnlyList<PixelRect> referenceSlots) =>
            [];
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
