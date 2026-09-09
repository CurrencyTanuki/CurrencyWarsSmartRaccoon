using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 临时诊断探针（2026-09-09，DECIDE 级全链堵点定位）：按 Phase2LiveCollectionService
/// 的构造方式组装 窗口+捕获+分析器+快速分类器+管线，其中捕获=沙箱 FileSequenceGameCapture
/// 同款（固定返回一张备战帧）。用完即删。
/// </summary>
public sealed class SandboxPipelineProbeTests
{
    [Fact]
    public async Task Pipeline_YieldsFirstUpdate_OnFileSequenceCapture()
    {
        var root = RepositoryRoot;
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            root, "config", "page-recognition.1920x1080.json"));
        var catalog = GameDataCatalogLoader.Load(DataDirectory);
        using var matcher = new OpenCvTemplateMatcher();
        var pageClassifier = new TemplateGamePageClassifier(matcher, config.Pages);
        var fast = new Phase2FastPageClassifier(matcher, config.Pages);

        var windowService = new StubWindowService("probe");
        var prepFrame = Path.Combine(root,
            "tests", "CurrencyWarsAssistant.Tests",
            "Fixtures", "PageReplay", "preparation_1_3_after_shop_2559x1439.png");
        var capture = new FileSequenceGameCapture(
            () => CaptureFrameLoader.LoadFile(prepFrame));

        using var characterRecognizer = new OpenCvCharacterCardRecognizer();
        var characterTemplates = LoadCharacterTemplates(catalog);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var iconTemplates = Phase2IconTemplateCatalog.Load(DataDirectory);
        var ocr = new WindowsOfflineOcr();
        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var operational = new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer,
            characterTemplates,
            iconRecognizer,
            iconTemplates,
            ocr,
            catalog,
            new WindowsOfflineOcr("en-US"),
            pageClassifier: pageClassifier,
            enableRobustFallback: false);
        var analyzer = new CurrencyWarsSituationScreenshotAnalyzer(
            pageClassifier,
            characterRecognizer,
            characterTemplates,
            goldRecognizer,
            LoadGoldDigitTemplates(),
            new OcrOpeningPageReader(ocr, catalog),
            new RewardShopReader(ocr, catalog),
            ocr,
            catalog,
            new GuideRepository(),
            new AdvisorEngine(),
            GuideDirectory,
            operational,
            new WindowsOfflineOcr("en-US"),
            Phase2IconTemplateCatalog.Load(DataDirectory));

        var pipeline = new Phase2RealtimeRecognitionPipeline(
            windowService,
            capture,
            analyzer,
            fast);

        var selection = new AdvisorSelection(AdvisorMode.Auto, "stable", "4.4");
        await using var enumerator = pipeline.RunAsync(
            StubWindowService.SandboxWindowHandle,
            selection,
            () => "probe",
            CancellationToken.None).GetAsyncEnumerator();

        var moveNext = enumerator.MoveNextAsync().AsTask();
        var completed = await Task.WhenAny(moveNext, Task.Delay(TimeSpan.FromSeconds(90)));
        Assert.True(completed == moveNext, "管线 90 秒内没有产出任何更新（复现沙箱冻结）");
        var has = await moveNext;
        Assert.True(has, "管线枚举已结束且无更新");
    }

    private static IReadOnlyList<CharacterCardTemplateDefinition> LoadCharacterTemplates(
        GameDataCatalog catalog)
    {
        var directory = Path.Combine(DataDirectory, "character-card-templates");
        var templates = catalog.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                directory,
                $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(
                    character.Id,
                    character.Name,
                    file)))
            .ToList();
        var armament = Path.Combine(directory, "bench_special_privilege_armament_box.png");
        if (File.Exists(armament))
        {
            templates.Add(new CharacterCardTemplateDefinition(
                "bench_special_privilege_armament_box",
                "特权武装箱",
                armament,
                CharacterCardTemplateKind.SpecialOccupied));
        }
        return templates;
    }

    private static IReadOnlyList<GoldDigitTemplateDefinition> LoadGoldDigitTemplates()
    {
        var directory = Path.Combine(DataDirectory, "gold-digit-templates");
        return new[] { 3, 7 }
            .Select(digit => new GoldDigitTemplateDefinition(
                digit,
                Path.Combine(directory, $"digit_{digit}.png")))
            .ToArray();
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string DataDirectory => Path.Combine(RepositoryRoot, "data", "4.4");

    private static string GuideDirectory => Path.Combine(
        RepositoryRoot, "data", "advisor", "1.0.0", "4.4", "guides");
}
