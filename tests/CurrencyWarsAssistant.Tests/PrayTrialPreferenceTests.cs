using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「1-3 三星五费」祈愿试炼择优纯逻辑单测。</summary>
public sealed class PrayTrialPreferenceTests
{
    [Fact]
    public void ChoosesCombatTrialOppositeOfForbidden()
    {
        // 左：出售圣杯(禁选)；右：无关键字
        var choice = PrayTrialPreference.Choose(
            "令咒决议：出售所有圣杯角色",
            "祈愿试炼·无名任务");
        Assert.Equal(PrayTrialPreference.TrialChoice.Right, choice);
    }

    [Fact]
    public void ChoosesHighValueTrial_OverPlain()
    {
        var choice = PrayTrialPreference.Choose(
            "祈愿·一些装备",
            "令咒决议：获得8个完美投影仪");
        Assert.Equal(PrayTrialPreference.TrialChoice.Right, choice);
    }

    [Fact]
    public void ChoosesLeft_WhenOnlyLeftValuable()
    {
        var choice = PrayTrialPreference.Choose(
            "令咒决议：5费聘用书",
            "祈愿·无奖励任务");
        Assert.Equal(PrayTrialPreference.TrialChoice.Left, choice);
    }

    [Fact]
    public void Either_WhenBothValuable()
    {
        var choice = PrayTrialPreference.Choose(
            "令咒决议：获得8个完美投影仪",
            "祈愿·直接获得3星圣杯");
        Assert.Equal(PrayTrialPreference.TrialChoice.Either, choice);
    }

    [Fact]
    public void Either_WhenBothPlain()
    {
        var choice = PrayTrialPreference.Choose(
            "祈愿·普通装备",
            "祈愿·随机资源");
        Assert.Equal(PrayTrialPreference.TrialChoice.Either, choice);
    }

    [Fact]
    public void None_WhenBothForbidden()
    {
        var choice = PrayTrialPreference.Choose(
            "诅咒·出售所有圣杯角色",
            "诅咒·拆散命运圣杯羁绊");
        Assert.Equal(PrayTrialPreference.TrialChoice.None, choice);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void NullOrEmpty_Safe_ReturnsEither(string? l, string? r)
    {
        Assert.Equal(PrayTrialPreference.TrialChoice.Either,
            PrayTrialPreference.Choose(l, r));
    }

    [Fact]
    public void ForbiddenSide_Avoided_EvenIfContainsValuableWord()
    {
        // 左文本同时含禁选词"出售"与 high value 词"圣杯"：因 Forbidden 先判，
        // 左为禁选 -> 选右（而非 None/Either）
        var choice = PrayTrialPreference.Choose(
            "诅咒:出售所有圣杯角色以换取资源",
            "祈愿·随机装备");
        Assert.Equal(PrayTrialPreference.TrialChoice.Right, choice);
    }

    // ---------- IsForbidden（N3 修复：供引擎 N2→C2 校验另一侧是否可点） ----------
    [Fact]
    public void IsForbidden_SaleGrailText_True()
    {
        Assert.True(PrayTrialPreference.IsForbidden("出售所有圣杯，换取金币"));
        Assert.True(PrayTrialPreference.IsForbidden("拆散命运圣杯羁绊"));
    }

    [Fact]
    public void IsForbidden_PlainOrNullText_False()
    {
        Assert.False(PrayTrialPreference.IsForbidden("普通试炼A"));
        Assert.False(PrayTrialPreference.IsForbidden(null));
        Assert.False(PrayTrialPreference.IsForbidden(""));
    }
}
