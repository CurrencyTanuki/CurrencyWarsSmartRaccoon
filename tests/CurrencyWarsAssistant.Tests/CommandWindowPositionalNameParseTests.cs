using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// A2/A4 位置语义指令的可选期望角色名解析（2026-09-09 修复批，P1-C 后续批）：
/// `A2 前台 2 黑塔` / `A4 前台 1 远坂凛` ——期望名进 GrailPositionArgs.ExpectedCharacterName，
/// 供操作层拖前身份比对（防误卖）与 A4 幂等预查按名佐证；省略时=null，与旧语法完全兼容。
/// </summary>
public sealed class CommandWindowPositionalNameParseTests
{
    [Fact]
    public void A2_WithExpectedName_ParsesIntoPayload()
    {
        var ok = CommandTestWindow.TryParseCommand(
            new[] { "A2", "前台", "2", "黑塔" },
            out var command, out var error);
        Assert.True(ok, error);
        var args = Assert.IsType<GrailPositionArgs>(command!.Payload);
        Assert.Equal(PreparationLane.Front, args.Lane);
        Assert.Equal(1, args.SlotIndex);
        Assert.Equal("黑塔", args.ExpectedCharacterName);
    }

    [Fact]
    public void A2_WithoutExpectedName_RemainsNull()
    {
        var ok = CommandTestWindow.TryParseCommand(
            new[] { "A2", "后台", "6" },
            out var command, out var error);
        Assert.True(ok, error);
        var args = Assert.IsType<GrailPositionArgs>(command!.Payload);
        Assert.Equal(PreparationLane.Back, args.Lane);
        Assert.Equal(5, args.SlotIndex);
        Assert.Null(args.ExpectedCharacterName);
    }

    [Fact]
    public void A4_WithExpectedName_ParsesIntoPayload()
    {
        var ok = CommandTestWindow.TryParseCommand(
            new[] { "A4", "前台", "1", "远坂凛" },
            out var command, out var error);
        Assert.True(ok, error);
        var args = Assert.IsType<GrailPositionArgs>(command!.Payload);
        Assert.Equal(PreparationLane.Front, args.Lane);
        Assert.Equal(0, args.SlotIndex);
        Assert.Equal("远坂凛", args.ExpectedCharacterName);
    }

    [Fact]
    public void A2_SlotOutOfRange_StillRejected()
    {
        var ok = CommandTestWindow.TryParseCommand(
            new[] { "A2", "前台", "9", "黑塔" },
            out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }
}
