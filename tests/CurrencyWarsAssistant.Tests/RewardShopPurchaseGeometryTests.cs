using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class RewardShopPurchaseGeometryTests
{
    public static TheoryData<int, int> Resolutions => new()
    {
        { 1280, 720 },
        { 1600, 900 },
        { 1920, 1080 },
        { 2048, 1152 },
        { 2560, 1440 },
        { 3840, 2160 }
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void FirstThreeSlotPointsStayInsideTheirCardBounds(
        int width,
        int height)
    {
        PixelPoint? previous = null;
        for (var slot = 0; slot < 3; slot++)
        {
            var point = RewardStageAutomationController
                .GetShopCardClientPoint(slot, width, height);
            var bounds = RewardStageAutomationController
                .GetShopCardVisualBounds(slot, width, height);

            Assert.InRange(point.X, bounds.X, bounds.Right - 1);
            Assert.InRange(point.Y, bounds.Y, bounds.Bottom - 1);
            if (previous is not null)
            {
                Assert.True(point.X > previous.Value.X);
                Assert.Equal(previous.Value.Y, point.Y);
            }

            previous = point;
        }
    }

    [Fact]
    public void CurrentLiveResolutionMapsFirstSlotToObservedClientPoint()
    {
        Assert.Equal(
            new PixelPoint(647, 233),
            RewardStageAutomationController.GetShopCardClientPoint(
                0,
                2560,
                1440));
    }
}
