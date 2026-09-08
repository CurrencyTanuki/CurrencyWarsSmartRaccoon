using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// P1-C（2026-09-08 下午批）：A2 卖出拖前身份比对的名字匹配语义。
/// 背景：引擎台账说该槽是「黑塔」、槽位识别读出别的名字时必须拒绝拖拽防误卖
/// （G06 画面实锤：模型漂移把无辜卡拖走）。匹配规则=中点/空白规范化后全等
/// ——刻意不用前缀包含：「姬子」与「姬子•启行」是不同角色，前缀包含会放行误卖。
/// </summary>
public sealed class GrailSaleIdentityMatcherTests
{
    [Theory]
    [InlineData("黑塔", "黑塔")]
    [InlineData("黑塔", "黑塔 ")]
    [InlineData("丹恒·腾荒", "丹恒腾荒")]
    [InlineData("姬子•启行", "姬子启行")]
    [InlineData("远坂凛", "远坂凛")]
    public void NameMatches_AcceptsSameCharacter(string expected, string recognized)
    {
        Assert.True(GrailSaleIdentityMatcher.NameMatches(expected, recognized));
    }

    [Theory]
    [InlineData("黑塔", "艾丝妲")]
    [InlineData("黑塔", "翡翠")]
    [InlineData("远坂凛", "吉尔伽美什")]
    public void NameMatches_RejectsDifferentCharacters(string expected, string recognized)
    {
        Assert.False(GrailSaleIdentityMatcher.NameMatches(expected, recognized));
    }

    [Theory]
    [InlineData("姬子", "姬子•启行")]   // 不同角色（_42 vs _01）——前缀包含必须判否
    [InlineData("银狼", "银狼LV.999")]  // 同上（用户拍板：一律按不同角色）
    public void NameMatches_RejectsPrefixOfDifferentCharacter(
        string expected, string recognized)
    {
        Assert.False(GrailSaleIdentityMatcher.NameMatches(expected, recognized));
    }

    [Fact]
    public void NameMatches_EmptyNames_DoNotReject()
    {
        // 空名=无法比对，调用方据此走放行分支（识别抖动不卡死卖出链），
        // 匹配器对空名的契约=不拒绝。
        Assert.True(GrailSaleIdentityMatcher.NameMatches(string.Empty, "黑塔"));
        Assert.True(GrailSaleIdentityMatcher.NameMatches("黑塔", " "));
    }
}
