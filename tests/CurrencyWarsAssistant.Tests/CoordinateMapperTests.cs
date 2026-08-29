using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class CoordinateMapperTests
{
    [Fact]
    public void MapsClientCoordinatesToScreenCoordinates()
    {
        var window = new GameWindowInfo(
            1,
            1,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(100, 200, 1920, 1080));

        var point = CoordinateMapper.ClientToScreen(window, new PixelPoint(960, 540));

        Assert.Equal(new PixelPoint(1060, 740), point);
    }

    [Fact]
    public void MapsNormalizedCoordinatesToScreenCoordinates()
    {
        var window = new GameWindowInfo(
            1,
            1,
            "StarRail",
            "崩坏：星穹铁道",
            new PixelRect(100, 200, 1920, 1080));

        var point = CoordinateMapper.NormalizedToScreen(window, 0.5, 0.5);

        Assert.Equal(new PixelPoint(1060, 740), point);
    }
}
