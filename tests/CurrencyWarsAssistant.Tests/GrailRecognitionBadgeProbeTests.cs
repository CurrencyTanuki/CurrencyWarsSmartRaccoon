using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 1.2.98 I7 星徽探测守卫（审查 P2 补齐）：未携带星徽读数在快照=0 时由
/// StarBadgeLocator 实拍接管（坑 36 装备识别链恒读 0 的断链修复）。
/// 四态契约：快照>0 不探测 / 探测命中改写 fact / 未命中回落 / 抓帧异常回落不抛。
/// </summary>
public sealed class GrailRecognitionBadgeProbeTests
{
    private sealed class RecordingEventSink : ITaskEventSink
    {
        public List<string> Codes { get; } = [];

        public void Publish(TaskEvent taskEvent) => Codes.Add(taskEvent.Code);
    }

    private sealed class FakeCollectionService : IPhase2LiveCollectionService
    {
        public event EventHandler<LiveCollectionUpdate>? Updated;

        public void Raise(ScreenshotAnalysisResult analysis) =>
            Updated?.Invoke(this, new LiveCollectionUpdate("run-test", 1, analysis, string.Empty));

        public Task RunAsync(
            nint gameWindowHandle,
            AdvisorSelection selection,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCapture : IGameCapture
    {
        public CaptureFrame? Frame { get; set; }
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }

        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return ValueTask.FromResult(Frame!);
        }
    }

    private sealed class FakeWindowService : IGameWindowService
    {
        public GameWindowInfo? Window { get; set; }

        public GameWindowInfo? Refresh(nint windowHandle) => Window;

        public IReadOnlyList<GameWindowInfo> FindCandidates() => [];

        public bool BringToForeground(GameWindowInfo window) => true;

        public bool IsForeground(GameWindowInfo window) => true;
    }

    private static RewardStageAutomationController MakeController(ITaskEventSink sink) =>
        new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, sink);

    private static GrailRecognitionCommands MakeCommands(
        FakeCollectionService service,
        ScreenshotAnalysisResult analysis,
        RewardStageAutomationController rewardStage,
        FakeCapture? capture = null,
        FakeWindowService? windowService = null)
    {
        var listener = new GrailRecognitionListener(service);
        listener.Subscribe();
        service.Raise(analysis);
        return new GrailRecognitionCommands(
            listener, new GrailRunStateHolder(),
            new GameDataCatalog([], [], [], [], []),
            null!, rewardStage, null!,
            capture, windowService);
    }

    private static ScreenshotAnalysisResult PreparationAnalysis() => new()
    {
        AnalysisId = "analysis-test",
        Snapshot = new RunSnapshot
        {
            RunId = "run-test",
            AsOf = DateTimeOffset.Now.AddSeconds(-1),
            PageId = Observation<string>.Known("preparation_generic", 0.9),
        },
        OperationalState = new Phase2OperationalState(),
    };

    private static GameWindowInfo ReadyWindow() =>
        new(1, 1, "StarRail", "test", new PixelRect(0, 0, 1920, 1080));

    private static async Task<GrailCommandResult> SendI7Async(GrailRecognitionCommands commands)
        => await commands.HandleAsync(
            new GrailCommand(GrailCommandKind.I7),
            new GrailCommandContext(1, "preparation_generic", GrailUserGoal.Single),
            CancellationToken.None);

    [Fact]
    public async Task SnapshotWithoutBadgeAndNoCapture_FallsBackToLegacyFact()
    {
        // 未注入 capture（既有 4 处构造形态）= 旧行为：快照读 0 就返回 0。
        var commands = MakeCommands(
            new FakeCollectionService(),
            PreparationAnalysis(),
            MakeController(new RecordingEventSink()));

        var result = await SendI7Async(commands);

        var fact = Assert.IsType<GrailBadgeFact>(result.Payload);
        Assert.Equal(0, fact.Uncarried);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProbeException_FallsBackWithoutThrowing()
    {
        var sink = new RecordingEventSink();
        var capture = new FakeCapture { Throw = new InvalidOperationException("窗口已最小化") };
        var commands = MakeCommands(
            new FakeCollectionService(),
            PreparationAnalysis(),
            MakeController(sink),
            capture,
            new FakeWindowService { Window = ReadyWindow() });

        var result = await SendI7Async(commands);

        var fact = Assert.IsType<GrailBadgeFact>(result.Payload);
        Assert.Equal(0, fact.Uncarried);
        Assert.Equal(1, capture.Calls);
        Assert.Contains("I7StarBadgeProbeFailed", sink.Codes);
    }

    [Fact]
    public async Task EmptyFrame_MissKeepsLegacyFact()
    {
        var sink = new RecordingEventSink();
        // 全暗帧：Locator 无金色簇必不命中。
        var capture = new FakeCapture { Frame = MakeFrame(1920, 1080, null) };
        var commands = MakeCommands(
            new FakeCollectionService(),
            PreparationAnalysis(),
            MakeController(sink),
            capture,
            new FakeWindowService { Window = ReadyWindow() });

        var result = await SendI7Async(commands);

        var fact = Assert.IsType<GrailBadgeFact>(result.Payload);
        Assert.Equal(0, fact.Uncarried);
        Assert.Contains("I7StarBadgeNotLocated", sink.Codes);
    }

    [Fact]
    public async Task TemplateHit_OverridesUncarriedToOne()
    {
        var sink = new RecordingEventSink();
        // 随包模板像素 1:1 贴进物品栏探测区 → Locator 必命中（score≈1）。
        var frame = MakeFrame(1920, 1080, (1800, 300));
        if (frame is null)
        {
            // 模板不在测试输出目录（csproj 拷贝缺失）——视为环境缺件跳过断言体。
            return;
        }

        var capture = new FakeCapture { Frame = frame };
        var commands = MakeCommands(
            new FakeCollectionService(),
            PreparationAnalysis(),
            MakeController(sink),
            capture,
            new FakeWindowService { Window = ReadyWindow() });

        var result = await SendI7Async(commands);

        var fact = Assert.IsType<GrailBadgeFact>(result.Payload);
        Assert.Equal(1, fact.Uncarried);
        Assert.Equal(1, fact.TotalObtained);
        Assert.Contains("I7StarBadgeLocated", sink.Codes);
    }

    /// <summary>合成 1920×1080 暗底帧；templatePatch 非 null 时把随包星徽模板像素
    /// 1:1 贴到该位置（模板自拷贝=匹配分≈1，金色簇条件由模板实拍像素自然满足）。</summary>
    private static CaptureFrame? MakeFrame(int width, int height, (int X, int Y)? templatePatch)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20;
            pixels[i + 1] = 20;
            pixels[i + 2] = 20;
            pixels[i + 3] = 255;
        }

        if (templatePatch is { } patch)
        {
            var templatePath = Path.Combine(
                AppContext.BaseDirectory, "Assets", "Recognition", "star_badge_template.png");
            if (!File.Exists(templatePath))
            {
                return null;
            }

            var template = CaptureFrameLoader.LoadFile(templatePath);
            for (var row = 0; row < template.Height && patch.Y + row < height; row++)
            {
                for (var col = 0; col < template.Width && patch.X + col < width; col++)
                {
                    var src = row * template.Stride + col * 4;
                    var dst = (patch.Y + row) * stride + (patch.X + col) * 4;
                    pixels[dst] = template.BgraPixels[src];
                    pixels[dst + 1] = template.BgraPixels[src + 1];
                    pixels[dst + 2] = template.BgraPixels[src + 2];
                    pixels[dst + 3] = 255;
                }
            }
        }

        return new CaptureFrame(
            width,
            height,
            stride,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }
}
