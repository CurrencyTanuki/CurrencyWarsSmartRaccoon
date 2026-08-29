using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class PrivilegeEquipmentOverlayRealFrameTests
{
    [Fact]
    public void PrivilegeCatalog_AllThirtySixVisibleAndLockedIconsHaveOverlay()
    {
        var first = Load("privilege_equipment_catalog_01_1745x951.png");
        var second = Load("privilege_equipment_catalog_02_1696x929.png");
        var regions = Grid(first, [79, 315, 549, 783, 1016, 1250, 1484],
                [77, 392, 701], 136, 136)
            .Concat(Grid(second, [59, 292, 526, 760, 993, 1227, 1460],
                [66, 376], 130, 130))
            .Concat(Grid(second, [59], [687], 130, 130))
            .ToArray();

        Assert.Equal(36, regions.Length);
        Assert.All(regions, item => Assert.True(
            Phase2OperationalScreenshotAnalyzer.DetectPrivilegeEquipmentOverlay(
                item.Frame,
                item.Region),
            $"Privilege overlay missed at {item.Region}."));
    }

    [Fact]
    public void OrdinaryEquipmentRealFrame_HasNoPrivilegeOverlay()
    {
        var frame = Load("prep_1_6_two_equipment_2559x1439.png");
        var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
            Phase2RecognitionRegions.PreparationCharacterSlots1920[1],
            occupiedSlotCount: 2);

        Assert.All(regions, region => Assert.False(
            Phase2OperationalScreenshotAnalyzer.DetectPrivilegeEquipmentOverlay(
                frame,
                region)));
    }

    private static IEnumerable<(CaptureFrame Frame, NormalizedRect Region)> Grid(
        CaptureFrame frame,
        IReadOnlyList<int> xCoordinates,
        IReadOnlyList<int> yCoordinates,
        int width,
        int height)
    {
        foreach (var y in yCoordinates)
        {
            foreach (var x in xCoordinates)
            {
                yield return (frame, new NormalizedRect(
                    x / (double)frame.Width,
                    y / (double)frame.Height,
                    width / (double)frame.Width,
                    height / (double)frame.Height));
            }
        }
    }

    private static CaptureFrame Load(string name) => CaptureFrameLoader.LoadFile(
        Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            name));

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));
}
