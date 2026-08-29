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

    [Fact]
    public void CalibrationWindowKeepsConfirmButtonVisibleOnAnyDpi()
    {
        // 回归防护（0.2.815 定位窗口按钮消失）：窗口不得用固定高度把
        // 底部"确认游戏区域"按钮挤出可视区；高度必须自适应内容。
        var xaml = ReadAppFile("GameAreaCalibrationWindow.xaml");

        Assert.Contains("确认游戏区域", xaml, StringComparison.Ordinal);
        Assert.Contains("SizeToContent=\"Height\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"760\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowKeepsCalibrationEntryButtonVisible()
    {
        // 回归防护（0.2.815 主界面"定位画面"按钮消失）：超长窗口标题
        // 不得把定位入口按钮挤出可视区——游戏窗口下拉框必须限制宽度，
        // 弹性列必须放在按钮之后。
        var xaml = ReadAppFile("MainWindow.xaml");

        // 定位按钮必须存在
        Assert.Contains(
            "Content=\"定位画面\"",
            xaml,
            StringComparison.Ordinal);
        // 游戏窗口下拉框必须限制宽度（超长标题不得撑爆 Auto 列）
        Assert.Contains(
            "MaxWidth=\"320\"",
            xaml,
            StringComparison.Ordinal);
        // 定位按钮必须在弹性列之前：弹性列（*）应位于配置行 Grid 的
        // 最后一个 ColumnDefinition，否则按钮会被压缩到不可见。
        var windowCombo = xaml.IndexOf(
            "ItemsSource=\"{Binding Windows}\"",
            StringComparison.Ordinal);
        var buttonIndex = xaml.IndexOf(
            "Content=\"定位画面\"",
            StringComparison.Ordinal);
        Assert.True(windowCombo > 0 && buttonIndex > windowCombo);
        // 取配置行 Grid 的 ColumnDefinitions 块（从窗口 ComboBox 前一个
        // Grid 开始到定位按钮结束）
        var gridStart = xaml.LastIndexOf(
            "<Grid.ColumnDefinitions>",
            windowCombo,
            StringComparison.Ordinal);
        var gridEnd = xaml.IndexOf(
            "</Grid.ColumnDefinitions>",
            gridStart,
            StringComparison.Ordinal);
        var columnBlock = xaml[gridStart..gridEnd];
        var starIndex = columnBlock.IndexOf(
            "Width=\"*\"",
            StringComparison.Ordinal);
        Assert.True(starIndex >= 0, "配置行必须有弹性列（*）吸收剩余空间");
        // * 列必须是最后一个 ColumnDefinition（在按钮列之后）
        var starLine = columnBlock[starIndex..];
        var afterStar = starLine[starLine.IndexOf('>')..];
        Assert.DoesNotContain(
            "ColumnDefinition",
            afterStar,
            StringComparison.Ordinal);
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
