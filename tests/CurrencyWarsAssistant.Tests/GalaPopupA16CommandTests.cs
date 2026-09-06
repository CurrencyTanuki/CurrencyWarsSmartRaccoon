using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// A16 盛会之星升档选择框消除指令（1.2.115，用户令单功能隔离测试）的契约守卫：
/// 弹框在屏→应答→Ok(Dismissed)；不在屏→Ok(NotOnScreen) 零点击；消除失败→如实 Fail；
/// 未注入处理器→Fail 带指引。A16 绝不弃局、不碰状态机——用户单功能测试场景
/// （游戏停在弹框界面）下唯一允许下发的动作指令。
/// </summary>
public sealed class GalaPopupA16CommandTests
{
    private sealed class StubGalaHandler(GalaBondDismissOutcome outcome) : IGalaBondPopupHandler
    {
        public int Calls { get; private set; }

        public Task<GalaBondDismissOutcome> DismissGalaBondPopupIfUpAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(outcome);
        }
    }

    private static GrailCommandContext Context() =>
        new(123, "preparation_generic", GrailUserGoal.Single);

    [Theory]
    [InlineData(GalaBondDismissOutcome.Dismissed)]
    [InlineData(GalaBondDismissOutcome.NotOnScreen)]
    public async Task A16OutcomeSuccessReturnsOkWithDistinguishingPayload(
        GalaBondDismissOutcome outcome)
    {
        var stub = new StubGalaHandler(outcome);
        var operation = new GrailOperationCommands(null!, null!, null!, null, stub);

        var result = await operation.HandleAsync(
            new GrailCommand(GrailCommandKind.A16),
            Context(),
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Payload);
        Assert.Equal(1, stub.Calls);
        // 回执必须能区分"已消除"与"不在屏"——用户单功能测试的判读依据。
        if (outcome == GalaBondDismissOutcome.NotOnScreen)
        {
            Assert.Contains("不在屏", result.Payload!.ToString());
        }
        else
        {
            Assert.Contains("已应答关闭", result.Payload!.ToString());
        }
    }

    [Fact]
    public async Task A16FailedOutcomeFailsHonestly()
    {
        var stub = new StubGalaHandler(GalaBondDismissOutcome.Failed);
        var operation = new GrailOperationCommands(null!, null!, null!, null, stub);

        var result = await operation.HandleAsync(
            new GrailCommand(GrailCommandKind.A16),
            Context(),
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task A16WithoutInjectedHandlerFailsWithGuidance()
    {
        var operation = new GrailOperationCommands(null!, null!, null!, null, null);

        var result = await operation.HandleAsync(
            new GrailCommand(GrailCommandKind.A16),
            Context(),
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("IGalaBondPopupHandler", result.Error);
        Assert.Equal(0, result.Error!.IndexOf("未注入", StringComparison.Ordinal));
    }
}
