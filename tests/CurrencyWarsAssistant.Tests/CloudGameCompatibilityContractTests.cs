using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class CloudGameCompatibilityContractTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(1600, 900)]
    [InlineData(1366, 768)]
    public void CommonSixteenByNineAreasPassStrictValidation(
        int width,
        int height)
    {
        Assert.True(GameAspectRatio.IsSixteenByNine(width, height));
    }

    [Theory]
    [InlineData(1920, 1000)]
    [InlineData(1600, 1000)]
    [InlineData(1280, 800)]
    public void NonSixteenByNineAreasAreRejected(int width, int height)
    {
        Assert.False(GameAspectRatio.IsSixteenByNine(width, height));
    }

    [Fact]
    public void MainUiExposesSourceSelectionAndManualCalibration()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        Assert.Contains("GameSourceOptions", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedGameSource", xaml, StringComparison.Ordinal);
        Assert.Contains("OnCalibrateGameAreaClick", xaml, StringComparison.Ordinal);
        Assert.Contains("定位画面", xaml, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string fileName)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        return File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "CurrencyWarsAssistant.App",
            fileName));
    }
}
