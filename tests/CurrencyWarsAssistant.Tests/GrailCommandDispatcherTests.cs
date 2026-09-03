using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 指令集 v4.3.1 适配层测试：分发器按段路由、指令清单与文档一致、
/// 无底层实现的指令走桩路径（绝不触碰组件、绝不编造坐标）。
/// 决策层零改动的边界由「识别/操作/宏只被路由、不做业务」保证。
/// </summary>
public class GrailCommandDispatcherTests
{
    private sealed class RecordingHandler(GrailCommandResult result) : IGrailCommandHandler
    {
        public List<GrailCommandKind> Received { get; } = [];

        public Task<GrailCommandResult> HandleAsync(
            GrailCommand command,
            GrailCommandContext context,
            CancellationToken cancellationToken)
        {
            Received.Add(command.Kind);
            return Task.FromResult(result);
        }
    }

    private static GrailCommandContext Context() => new(1, "preparation_generic", GrailUserGoal.Single);

    [Fact]
    public async Task Routes_By_Kind_Segment()
    {
        var recognition = new RecordingHandler(GrailCommandResult.Ok(GrailCommandKind.I1));
        var operation = new RecordingHandler(GrailCommandResult.Ok(GrailCommandKind.A1));
        var macro = new RecordingHandler(GrailCommandResult.Ok(GrailCommandKind.M1));
        var dispatcher = new GrailCommandDispatcher(recognition, operation, macro);

        await dispatcher.DispatchAsync(new GrailCommand(GrailCommandKind.I5), Context(), CancellationToken.None);
        await dispatcher.DispatchAsync(new GrailCommand(GrailCommandKind.A6), Context(), CancellationToken.None);
        await dispatcher.DispatchAsync(new GrailCommand(GrailCommandKind.M3), Context(), CancellationToken.None);
        await dispatcher.DispatchAsync(new GrailCommand(GrailCommandKind.I10), Context(), CancellationToken.None);
        await dispatcher.DispatchAsync(new GrailCommand(GrailCommandKind.A14), Context(), CancellationToken.None);
        await dispatcher.DispatchAsync(new GrailCommand(GrailCommandKind.M8), Context(), CancellationToken.None);

        Assert.Equal(new[] { GrailCommandKind.I5, GrailCommandKind.I10 }, recognition.Received);
        Assert.Equal(new[] { GrailCommandKind.A6, GrailCommandKind.A14 }, operation.Received);
        Assert.Equal(new[] { GrailCommandKind.M3, GrailCommandKind.M8 }, macro.Received);
    }

    [Fact]
    public async Task Unknown_Kind_Fails_Without_Routing()
    {
        var recognition = new RecordingHandler(GrailCommandResult.Ok(GrailCommandKind.I1));
        var operation = new RecordingHandler(GrailCommandResult.Ok(GrailCommandKind.A1));
        var macro = new RecordingHandler(GrailCommandResult.Ok(GrailCommandKind.M1));
        var dispatcher = new GrailCommandDispatcher(recognition, operation, macro);

        var result = await dispatcher.DispatchAsync(
            new GrailCommand((GrailCommandKind)999), Context(), CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Empty(recognition.Received);
        Assert.Empty(operation.Received);
        Assert.Empty(macro.Received);
    }

    [Fact]
    public void Command_Catalog_Matches_v431()
    {
        // I×10 + A×14（A8 已删；A15=简易装备选择，2026-09-03 实测新增） + M×8 = 32。
        var values = Enum.GetValues<GrailCommandKind>();
        Assert.Equal(32, values.Length);
        Assert.False(Enum.IsDefined(typeof(GrailCommandKind), (GrailCommandKind)208), "A8 应已删除。");
    }

    [Theory]
    [InlineData(GrailCommandKind.A2)]
    [InlineData(GrailCommandKind.A7)]
    [InlineData(GrailCommandKind.A11)]
    [InlineData(GrailCommandKind.A12)]
    [InlineData(GrailCommandKind.A13)]
    [InlineData(GrailCommandKind.A14)]
    public async Task Operation_Stub_Paths_Fail_Without_Touching_Components(GrailCommandKind kind)
    {
        // 组件全部传 null：桩路径必须在不触碰任何组件的情况下返回失败事实。
        var commands = new GrailOperationCommands(null!, null!, null!, null!);

        var result = await commands.HandleAsync(new GrailCommand(kind), Context(), CancellationToken.None);

        Assert.Null(result.Payload);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task Macro_Stub_Paths_Fail_Without_Touching_Components()
    {
        var macros = new GrailMacroCommands(null!, null!, null!, null!, null!);

        foreach (var kind in new[] { GrailCommandKind.M6 })
        {
            var result = await macros.HandleAsync(new GrailCommand(kind), Context(), CancellationToken.None);
            Assert.Null(result.Payload);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
    }

    [Fact]
    public async Task Recognition_Returns_Unknown_Page_Fact_Without_Frames()
    {
        var recognition = new GrailRecognitionCommands(
            new GrailRecognitionListener(null!), new GrailRunStateHolder(), null!, null!, null!, null!);

        var result = await recognition.HandleAsync(
            new GrailCommand(GrailCommandKind.I1), Context(), CancellationToken.None);

        var fact = Assert.IsType<GrailPageFact>(result.Payload);
        Assert.Null(fact.PageId);
        Assert.False(fact.WishDialogOpen);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Recognition_I4_Fails_On_NonPreparation_Frame()
    {
        // 无帧=非备战页：I4 门禁必须拦截，不得产出空阵容假事实。
        var recognition = new GrailRecognitionCommands(
            new GrailRecognitionListener(null!), new GrailRunStateHolder(), null!, null!, null!, null!);

        var result = await recognition.HandleAsync(
            new GrailCommand(GrailCommandKind.I4), Context(), CancellationToken.None);

        Assert.Null(result.Payload);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task Operation_A1_Without_Payload_Fails_Without_Touching_Components()
    {
        var commands = new GrailOperationCommands(null!, null!, null!, null!);

        var result = await commands.HandleAsync(new GrailCommand(GrailCommandKind.A1), Context(), CancellationToken.None);

        Assert.NotNull(result.Error);
    }
}
