using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// P1-A（2026-09-09 修复批）：A4 星徽装配幂等预查的控制器层行为单测。
/// G15 局实锤：I7 报未携带但目标实际已带徽→A4 重拖→游戏横幅「无法穿戴相同羁绊的
/// 星徽」×2。守卫判定=拖拽前查账本（按名携带者∪挂起槽位），名字佐证优先用实时
/// 画面读数、退而用引擎台账期望名；无佐证的挂起槽位绝不盲拖。
/// </summary>
public sealed class GrailBadgeAssemblyGuardTests
{
    private static readonly IReadOnlySet<string> Carriers = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase) { "远坂凛" };

    private static readonly IReadOnlySet<string> PendingSlots =
        new HashSet<string>(StringComparer.Ordinal) { "front:1" };

    [Fact]
    public void LedgerEmpty_ProceedsToDrag()
    {
        var (outcome, name) = GrailBadgeAssemblyGuard.Decide(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal),
            "front:0", "黑塔", null);
        Assert.Equal(GrailBadgeAssemblyPrecheckOutcome.ProceedToDrag, outcome);
        Assert.Null(name);
    }

    [Fact]
    public void LiveOccupantIsCarrier_IdempotentAlreadyCarries()
    {
        var (outcome, name) = GrailBadgeAssemblyGuard.Decide(
            Carriers, new HashSet<string>(StringComparer.Ordinal),
            "front:0", "远坂凛", null);
        Assert.Equal(GrailBadgeAssemblyPrecheckOutcome.AlreadyCarries, outcome);
        Assert.Equal("远坂凛", name);
    }

    [Fact]
    public void ExpectedNameIsCarrier_IdempotentAlreadyCarries()
    {
        // 实时读不可读时台账期望名仍可佐证（弱佐证）。
        var (outcome, name) = GrailBadgeAssemblyGuard.Decide(
            Carriers, new HashSet<string>(StringComparer.Ordinal),
            "front:0", null, "远坂凛");
        Assert.Equal(GrailBadgeAssemblyPrecheckOutcome.AlreadyCarries, outcome);
        Assert.Equal("远坂凛", name);
    }

    [Fact]
    public void PendingSlotWithLiveName_PromotesCarrier()
    {
        var (outcome, name) = GrailBadgeAssemblyGuard.Decide(
            Carriers, PendingSlots, "front:1", "黑塔", null);
        Assert.Equal(GrailBadgeAssemblyPrecheckOutcome.PromotePendingSlot, outcome);
        Assert.Equal("黑塔", name);
    }

    [Fact]
    public void PendingSlotWithExpectedNameOnly_PromotesCarrier()
    {
        var (outcome, name) = GrailBadgeAssemblyGuard.Decide(
            Carriers, PendingSlots, "front:1", null, "黑塔");
        Assert.Equal(GrailBadgeAssemblyPrecheckOutcome.PromotePendingSlot, outcome);
        Assert.Equal("黑塔", name);
    }

    [Fact]
    public void PendingSlotWithoutAnyName_UncertainNoDrag()
    {
        var (outcome, name) = GrailBadgeAssemblyGuard.Decide(
            Carriers, PendingSlots, "front:1", null, null);
        Assert.Equal(GrailBadgeAssemblyPrecheckOutcome.UncertainNoDrag, outcome);
        Assert.Null(name);
    }

    [Fact]
    public void CarrierLookup_IsCaseInsensitive()
    {
        var (outcome, _) = GrailBadgeAssemblyGuard.Decide(
            Carriers, new HashSet<string>(StringComparer.Ordinal),
            "front:0", "远坂凛", null);
        Assert.Equal(GrailBadgeAssemblyPrecheckOutcome.AlreadyCarries, outcome);
    }

    [Theory]
    [InlineData("无法穿戴相同羁绊的星徽", true)]
    [InlineData("战斗中 无法穿戴 相同羁绊 的星徽", true)]
    [InlineData("不能装备相同羁绊的星徽", true)]
    [InlineData("获得星徽", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RejectionBannerText_MatchesOnlyRejectionWording(string? text, bool expected)
    {
        Assert.Equal(expected, GrailBadgeAssemblyGuard.IsBadgeRejectionBannerText(text));
    }
}
