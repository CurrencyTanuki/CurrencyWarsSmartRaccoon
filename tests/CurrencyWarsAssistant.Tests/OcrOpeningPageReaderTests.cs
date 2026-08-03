using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

public sealed class OcrOpeningPageReaderTests
{
    [Fact]
    public async Task PrimaryDynamicStripReadsStressReactionFixtureAfterContentTightening()
    {
        var windowsOcr = new WindowsOfflineOcr();
        Assert.True(
            windowsOcr.IsAvailable,
            "Windows Simplified Chinese OCR is required.");
        var ocr = new RecordingOcr(windowsOcr);
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());
        var frame = LoadFrame(
            Path.Combine(
                FixtureDirectory,
                "enemy_overview_stress_reaction_2048x1152.png"));

        var result = await reader.ReadEnemyOverviewAsync(
            frame,
            CancellationToken.None);

        Assert.True(
            result.IsComplete,
            string.Join(
                " | ",
                result.Competitors
                    .Concat(result.EnemyModifiers)
                    .Select(item =>
                        $"{item.RawText} => {item.Item?.DisplayName ?? "<none>"}")));
        Assert.Equal(
            ["火线动力机甲", "钢铁意志集团", "猎星资本"],
            result.Competitors.Select(value => value.Item?.DisplayName));
        Assert.Equal(
            ["随从强化", "坠入陷阱", "应激反应", "灼热轰炸"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
        Assert.Equal(4, ocr.Regions.Count);
        var untrimmedStrip = new PixelRect(
            (int)Math.Round(240d * frame.Width / 1920),
            (int)Math.Round(930d * frame.Height / 1080),
            (int)Math.Round(1000d * frame.Width / 1920),
            (int)Math.Round(90d * frame.Height / 1080));
        Assert.Equal(untrimmedStrip.Y, ocr.Regions[3].Y);
        Assert.Equal(untrimmedStrip.Height, ocr.Regions[3].Height);
        Assert.True(ocr.Regions[3].Width < untrimmedStrip.Width);
        Assert.True(ocr.Regions[3].Right <= untrimmedStrip.Right);
    }

    [Fact]
    public async Task ReadsShortSpacedAffixesFromLiveEnemyOverviewFixture()
    {
        var ocr = new WindowsOfflineOcr();
        Assert.True(ocr.IsAvailable, "Windows Simplified Chinese OCR is required.");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());
        var frame = LoadFrame(
            Path.Combine(
                FixtureDirectory,
                "enemy_overview_short_affixes_2048x1152.png"));

        var result = await reader.ReadEnemyOverviewAsync(
            frame,
            CancellationToken.None);

        Assert.True(
            result.IsComplete,
            string.Join(
                " | ",
                result.Competitors
                    .Concat(result.EnemyModifiers)
                    .Select(item =>
                        $"{item.RawText} => {item.Item?.DisplayName ?? "<none>"}")));
        Assert.Equal(
            ["火线动力机甲", "造梦兄弟影业", "猎星资本"],
            result.Competitors.Select(value => value.Item?.DisplayName));
        Assert.Equal(
            ["首领强化", "忽快忽慢", "形单影只", "灼热轰炸"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
    }

    [Fact]
    public async Task ReadsShortSpacedAffixesAtNativeCaptureResolution()
    {
        var ocr = new WindowsOfflineOcr();
        Assert.True(ocr.IsAvailable, "Windows Simplified Chinese OCR is required.");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());
        var frame = LoadFrame(
            Path.Combine(
                FixtureDirectory,
                "enemy_overview_short_affixes_2048x1152.png"),
            2560,
            1440);

        var result = await reader.ReadEnemyOverviewAsync(
            frame,
            CancellationToken.None);

        Assert.True(
            result.IsComplete,
            string.Join(
                " | ",
                result.Competitors
                    .Concat(result.EnemyModifiers)
                    .Select(item =>
                        $"{item.RawText} => {item.Item?.DisplayName ?? "<none>"}")));
        Assert.Equal(
            ["首领强化", "忽快忽慢", "形单影只", "灼热轰炸"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
    }

    [Fact]
    public async Task ReadsGrowthWorriesFromLiveEnemyOverviewFixture()
    {
        var ocr = new WindowsOfflineOcr();
        Assert.True(ocr.IsAvailable, "Windows Simplified Chinese OCR is required.");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());
        var frame = LoadFrame(
            Path.Combine(
                FixtureDirectory,
                "enemy_overview_growth_worries_2559x1439.png"));

        var result = await reader.ReadEnemyOverviewAsync(
            frame,
            CancellationToken.None);

        Assert.True(
            result.IsComplete,
            string.Join(
                " | ",
                result.Competitors
                    .Concat(result.EnemyModifiers)
                    .Select(item =>
                        $"{item.RawText} => {item.Item?.DisplayName ?? "<none>"}")));
        Assert.Equal(
            ["虫人兵器", "不死者联盟", "火线动力机甲"],
            result.Competitors.Select(value => value.Item?.DisplayName));
        Assert.Equal(
            ["第三位面强化", "一鼓作气", "成长的烦恼", "开局不利"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
    }

    [Fact]
    public async Task ReadsExistingEnemyOverviewFixtureWithSingleModifierRow()
    {
        var ocr = new WindowsOfflineOcr();
        Assert.True(ocr.IsAvailable, "Windows Simplified Chinese OCR is required.");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());
        var frame = LoadFrame(Path.Combine(FixtureDirectory, "enemy_overview.jpg"));

        var result = await reader.ReadEnemyOverviewAsync(
            frame,
            CancellationToken.None);

        Assert.True(
            result.IsComplete,
            string.Join(
                " | ",
                result.Competitors
                    .Concat(result.EnemyModifiers)
                    .Select(item =>
                        $"{item.RawText} => {item.Item?.DisplayName ?? "<none>"}")));
        Assert.Equal(
            ["火线动力机甲", "金血记忆体联盟", "增熵能源集团"],
            result.Competitors.Select(value => value.Item?.DisplayName));
        Assert.Equal(
            ["随从强化", "榜样激励", "形单影只", "紧急止血"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
    }

    [Fact]
    public async Task SingleModifierRowHandlesContentShiftAcrossFormerSlots()
    {
        var ocr = new ScriptedOcr(
            Exact("虫人兵器"),
            Exact("不死者联盟"),
            Exact("火线动力机甲"),
            new OcrTextResult(
                "第二位面强化 复仇心切 成长的烦恼 开局不利",
                ["第二位面强化", "复仇心切", "成长的烦恼", "开局不利"]));
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadEnemyOverviewAsync(
            CreateFrame(2560, 1440),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["第二位面强化", "复仇心切", "成长的烦恼", "开局不利"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
        Assert.Equal(new PixelRect(320, 1240, 1333, 120), ocr.Regions[^1]);
    }

    [Fact]
    public async Task EmptyWholeRowUsesOverlappingAutomaticSegments()
    {
        var ocr = new ScriptedOcr(
            Exact("火线动力机甲"),
            Exact("造梦兄弟影业"),
            Exact("猎星资本"),
            Exact(string.Empty),
            Exact("首领强化"),
            Exact("首领强化 忽快忽慢"),
            Exact("忽快忽慢 形单影只"),
            Exact("形单影只 灼热轰炸"),
            Exact("灼热轰炸"));
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadEnemyOverviewAsync(
            CreateFrame(2560, 1440),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["首领强化", "忽快忽慢", "形单影只", "灼热轰炸"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
        var strip = new PixelRect(320, 1240, 1333, 120);
        Assert.Equal(strip, ocr.Regions[3]);
        var segments = ocr.Regions.Skip(4).ToArray();
        Assert.Equal(5, segments.Length);
        Assert.All(
            segments,
            segment =>
            {
                Assert.Equal(strip.Y, segment.Y);
                Assert.Equal(strip.Height, segment.Height);
                Assert.True(segment.X >= strip.X);
                Assert.True(segment.Right <= strip.Right);
            });
        Assert.All(
            segments.Zip(segments.Skip(1)),
            pair => Assert.True(pair.First.Right > pair.Second.X));
    }

    [Fact]
    public async Task NoisyWholeRowAlsoUsesOverlappingAutomaticSegments()
    {
        var ocr = new ScriptedOcr(
            Exact("火线动力机甲"),
            Exact("钢铁意志集团"),
            Exact("猎星资本"),
            Exact("画面噪声"),
            Exact("随从强化"),
            Exact("随从强化 坠入陷阱"),
            Exact("坠入陷阱 应激反应"),
            Exact("应激反应 灼热轰炸"),
            Exact("灼热轰炸"));
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadEnemyOverviewAsync(
            CreateFrame(2560, 1440),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["随从强化", "坠入陷阱", "应激反应", "灼热轰炸"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
        Assert.Equal(9, ocr.Regions.Count);
    }

    [Fact]
    public async Task SingleModifierRowRejectsIncompleteModifierSet()
    {
        var ocr = new ScriptedOcr(
            Exact("虫人兵器"),
            Exact("不死者联盟"),
            Exact("火线动力机甲"),
            new OcrTextResult(
                "第二位面强化 复仇心切 成长的烦恼",
                ["第二位面强化", "复仇心切", "成长的烦恼"]),
            Exact(string.Empty),
            Exact(string.Empty),
            Exact(string.Empty),
            Exact(string.Empty),
            Exact(string.Empty));
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadEnemyOverviewAsync(
            CreateFrame(2560, 1440),
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Empty(result.EnemyModifiers);
        Assert.Empty(result.RecognizedEnemyModifiers);
    }

    private static OcrTextResult Exact(string value) =>
        new(value, string.IsNullOrEmpty(value) ? [] : [value]);

    [Fact]
    public async Task ReadsEnemyOverviewAndScalesRegionsToActualFrame()
    {
        var ocr = new ScriptedOcr(
            Exact("阵营 火线动力机甲"),
            Exact("金血记忆体联萌"),
            Exact("增熵能源集团"),
            new OcrTextResult(
                "随从强化 榜样激励 形单影只 紧急止血",
                ["随从强化", "榜样激励", "形单影只", "紧急止血"]));
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadEnemyOverviewAsync(
            CreateFrame(2560, 1440),
            CancellationToken.None);

        Assert.True(
            result.IsComplete,
            string.Join(
                " | ",
                result.Competitors
                    .Concat(result.EnemyModifiers)
                    .Select(item =>
                        $"{item.RawText} => {item.Item?.DisplayName ?? "<none>"}")));
        Assert.Equal(
            ["火线动力机甲", "金血记忆体联盟", "增熵能源集团"],
            result.Competitors.Select(value => value.Item?.DisplayName));
        Assert.Equal(
            ["随从强化", "榜样激励", "形单影只", "紧急止血"],
            result.EnemyModifiers.Select(value => value.Item?.DisplayName));
        Assert.Equal(
            new PixelRect(167, 933, 347, 87),
            ocr.Regions[0]);
    }

    [Fact]
    public async Task ReadsThreeInvestmentEnvironmentOptions()
    {
        var ocr = new StubOcr(
            "量子同频邀请",
            "敌后破坏",
            "白银时代");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadInvestmentEnvironmentsAsync(
            CreateFrame(1920, 1080),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["量子同频邀请", "敌后破坏", "白银时代"],
            result.Options.Select(value => value.Item?.DisplayName));
        Assert.Equal(3, result.InvestmentEnvironments.Count);
    }

    [Fact]
    public async Task ReadsExactTwoCharacterInvestmentEnvironmentName()
    {
        var ocr = new StubOcr(
            "战争边疆",
            "欢愉邀请",
            "尾彩");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadInvestmentEnvironmentsAsync(
            CreateFrame(1920, 1080),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["战争边疆", "欢愉邀请", "尾彩"],
            result.Options.Select(value => value.Item?.DisplayName));
    }

    [Fact]
    public async Task RetriesBlankShortTitleWithExpandedTextContext()
    {
        var ocr = new StubOcr(
            "",
            "蓝海 进入到一个随机投资环境中 开局额外获得6金币",
            "星核猎手契约",
            "战争边疆");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadInvestmentEnvironmentsAsync(
            CreateFrame(1920, 1080),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["蓝海", "星核猎手契约", "战争边疆"],
            result.Options.Select(value => value.Item?.DisplayName));
        Assert.Equal(4, ocr.Regions.Count);
        Assert.True(ocr.Regions[1].Width > ocr.Regions[0].Width);
        Assert.True(ocr.Regions[1].Height > ocr.Regions[0].Height);
    }

    [Fact]
    public async Task RepairsSplitChineseGlyphAndUniqueLeadingGlyphError()
    {
        var ocr = new StubOcr(
            "自 量 邀 讠 青",
            "蓝海",
            "深井角斗场");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadInvestmentEnvironmentsAsync(
            CreateFrame(1920, 1080),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["能量邀请", "蓝海", "深井角斗场"],
            result.Options.Select(value => value.Item?.DisplayName));
    }

    [Fact]
    public async Task UsesUniqueDescriptionWhenTitleRecognitionStillFails()
    {
        var ocr = new StubOcr(
            "无法识别",
            "获得一个【能量星徽】。",
            "蓝海",
            "深井角斗场");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadInvestmentEnvironmentsAsync(
            CreateFrame(1920, 1080),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["能量邀请", "蓝海", "深井角斗场"],
            result.Options.Select(value => value.Item?.DisplayName));
        Assert.Equal(4, ocr.Regions.Count);
    }

    [Fact]
    public async Task IdentifiesBlueSeaFromItsDescriptionWithoutTitleText()
    {
        var ocr = new StubOcr(
            "",
            "进入到一个随机投资环境中，开局额外获得6金币。",
            "能量邀请",
            "深井角斗场");
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadInvestmentEnvironmentsAsync(
            CreateFrame(1920, 1080),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            ["蓝海", "能量邀请", "深井角斗场"],
            result.Options.Select(value => value.Item?.DisplayName));
        Assert.Equal(4, ocr.Regions.Count);
    }

    [Fact]
    public async Task CompletesUniqueThreeOfFourCharacterAffixFragment()
    {
        var ocr = new ScriptedOcr(
            Exact("凛冬经贸联合体"),
            Exact("巨鹿生物制药"),
            Exact("绘师家族产业"),
            new OcrTextResult(
                "第二位面强化 高费审美 变 宝 为 以人为本",
                ["第二位面强化", "高费审美", "变 宝 为", "以人为本"]));
        var reader = new OcrOpeningPageReader(ocr, LoadCatalog());

        var result = await reader.ReadEnemyOverviewAsync(
            CreateFrame(2560, 1440),
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(
            "变宝为废",
            result.EnemyModifiers[2].Item?.DisplayName);
    }

    private static GameDataCatalog LoadCatalog()
    {
        var dataDirectory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../data/4.4"));
        return GameDataCatalogLoader.Load(dataDirectory);
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));

    private static string FixtureDirectory =>
        Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay");

    private static CaptureFrame LoadFrame(
        string path,
        int? targetWidth = null,
        int? targetHeight = null)
    {
        using var bgr = Cv2.ImRead(path, ImreadModes.Color);
        using var resized = new Mat();
        var prepared = bgr;
        if (targetWidth is not null && targetHeight is not null)
        {
            Cv2.Resize(
                bgr,
                resized,
                new Size(targetWidth.Value, targetHeight.Value),
                interpolation: InterpolationFlags.Linear);
            prepared = resized;
        }

        using var bgra = new Mat();
        Cv2.CvtColor(prepared, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Cols,
            bgra.Rows,
            checked(bgra.Cols * 4),
            pixels,
            new PixelRect(0, 0, bgra.Cols, bgra.Rows),
            DateTimeOffset.UtcNow);
    }

    private static CaptureFrame CreateFrame(int width, int height) =>
        new(
            width,
            height,
            width * 4,
            new byte[width * height * 4],
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);

    private sealed class StubOcr(params string[] values) : IOfflineOcr
    {
        private readonly Queue<string> values = new(values);

        public bool IsAvailable => true;
        public List<PixelRect> Regions { get; } = [];

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Regions.Add(region);
            var value = values.Dequeue();
            return ValueTask.FromResult<OcrTextResult>(
                new OcrTextResult(value, [value]));
        }
    }

    private sealed class ScriptedOcr(params OcrTextResult[] values) : IOfflineOcr
    {
        private readonly Queue<OcrTextResult> values = new(values);

        public bool IsAvailable => true;
        public List<PixelRect> Regions { get; } = [];

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Regions.Add(region);
            return ValueTask.FromResult(values.Dequeue());
        }
    }

    private sealed class RecordingOcr(IOfflineOcr inner) : IOfflineOcr
    {
        public bool IsAvailable => inner.IsAvailable;
        public List<PixelRect> Regions { get; } = [];

        public async ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken)
        {
            Regions.Add(region);
            return await inner
                .RecognizeAsync(frame, region, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
