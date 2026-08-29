using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 装备识别诊断（用户 2026-08-07：装备无法识别）：
/// 对 3-7 帧每个槽位，检查 3 个装备区的 HasDetailedForeground 判定 +
/// 区域坐标，定位是"区域不对"还是"图标匹配失败"。
/// </summary>
public sealed class EquipmentRecognitionDiagnosisTests
{
    private readonly ITestOutputHelper _output;

    public EquipmentRecognitionDiagnosisTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void DiagnoseEquipmentRegionsOnRealFrame()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));

        // 检查 Front#0 槽位的 3 个装备区（compactFrontLayout=true 时 y=0.82H）
        var frontSlot = Phase2RecognitionRegions.PreparationCharacterSlots1920[0];
        _output.WriteLine($"Front#0 槽位: ({frontSlot.X},{frontSlot.Y}) {frontSlot.Width}x{frontSlot.Height}");
        var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
            frontSlot,
            compactFrontLayout: true);
        foreach (var (r, i) in regions.Select((r, i) => (r, i)))
        {
            var px = r.ToPixels(frame.Width, frame.Height);
            _output.WriteLine(
                $"  装备区[{i}]: normalized=({r.X:F3},{r.Y:F3},{r.Width:F3},{r.Height:F3}) " +
                $"px=({px.X},{px.Y},{px.Width}x{px.Height})");
        }

        // 用反射调 HasDetailedForeground（private static）
        var method = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "HasDetailedForeground",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        foreach (var (r, i) in regions.Select((r, i) => (r, i)))
        {
            var has = (bool)method!.Invoke(null, [frame, r])!;
            _output.WriteLine($"  装备区[{i}] 前景检测: {has}");
        }

        // 3-7 帧 Front#0 装备区：直接调 HasDetailedForeground 看阈值
        var method2 = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "HasDetailedForeground",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        _output.WriteLine("--- 3-7 帧 Front#0 装备区前景 ---");
        foreach (var (r, i) in regions.Select((r, i) => (r, i)))
        {
            var px = r.ToPixels(frame.Width, frame.Height);
            var has = (bool)method2!.Invoke(null, [frame, r])!;
            _output.WriteLine($"  装备区[{i}] px=({px.X},{px.Y},{px.Width}x{px.Height}) 前景={has}");
        }

        // 同样检查非 compact（y=0.96H）区域
        _output.WriteLine("--- 非 compact (y=0.96H) ---");
        var regions2 = Phase2RecognitionRegions.CharacterEquipmentSlots(
            frontSlot,
            compactFrontLayout: false);
        foreach (var (r, i) in regions2.Select((r, i) => (r, i)))
        {
            var has = (bool)method!.Invoke(null, [frame, r])!;
            _output.WriteLine($"  装备区[{i}] y={r.Y:F3} 前景: {has}");
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2048, 1152)]
    public void CenteredEquipmentForegroundRetainsVisibleIconsAfterScalingSmoke(
        int width,
        int height)
    {
        var method = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "HasCenteredEquipmentForeground",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // This is interpolation/scale smoke coverage, not native-resolution
        // ground truth. Native 1920x1080 and 2560x1440 captures are asserted in
        // dedicated tests below; the fixture set has no native 2048x1152 frame.
        var standardFrame = LoadScaledFrame(
            Path.Combine("PageReplay", "prep_3_7_142723217.png"),
            width,
            height);
        AssertStandardPreparationIconsRemainVisible(standardFrame, method!);

        var userReference = LoadScaledFrame(
            Path.Combine("PageReplay", "user_ref_000032_prep22.png"),
            width,
            height);
        var silverWolfItems = Phase2RecognitionRegions.CharacterEquipmentSlots(
            Phase2RecognitionRegions.PreparationCharacterSlots1920[1]);
        Assert.True((bool)method!.Invoke(null, [userReference, silverWolfItems[1]])!);
        Assert.True((bool)method!.Invoke(null, [userReference, silverWolfItems[2]])!);
    }

    [Fact]
    public void CenteredEquipmentForegroundMatchesNative1920CompactCaptureEvidence()
    {
        var method = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "HasCenteredEquipmentForeground",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // User-recorded native 1080p frame. The expanded shop uses compact
        // Front-row bounds. These labels were checked visually and are stable
        // across all six consecutive video_prep frames.
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "video_prep",
            "prep_5.png"));
        Assert.Equal((1920, 1080), (frame.Width, frame.Height));

        var expectedByFrontSlot = new[]
        {
            new[] { true, true, true },
            new[] { false, true, false },
            new[] { false, true, false },
            new[] { false, true, false }
        };
        for (var slotIndex = 0; slotIndex < expectedByFrontSlot.Length; slotIndex++)
        {
            var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
                Phase2RecognitionRegions.RewardShopCharacterSlots1920[slotIndex],
                compactFrontLayout: true);
            Assert.Equal(
                expectedByFrontSlot[slotIndex],
                regions.Select(region =>
                    (bool)method!.Invoke(null, [frame, region])!));
        }
    }

    [Fact]
    public void CenteredEquipmentForegroundKeepsNative2560FrontAndBackIcons()
    {
        var method = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "HasCenteredEquipmentForeground",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        Assert.Equal((2560, 1440), (frame.Width, frame.Height));
        AssertStandardPreparationIconsRemainVisible(frame, method!);
    }

    private static void AssertStandardPreparationIconsRemainVisible(
        CaptureFrame frame,
        System.Reflection.MethodInfo method)
    {
        var standardFront = Phase2RecognitionRegions.CharacterEquipmentSlots(
            Phase2RecognitionRegions.PreparationCharacterSlots1920[0]);
        Assert.All(
            standardFront,
            region => Assert.True(
                (bool)method.Invoke(null, [frame, region])!));

        // Production replaces the six legacy Back bounds with the detected
        // six-slot layout, then concatenates them after the four Front slots.
        var back = Phase2RecognitionRegions.BackCharacterSlots1920(6);
        var threeItemBack = Phase2RecognitionRegions.CharacterEquipmentSlots(back[2]);
        Assert.All(
            threeItemBack,
            region => Assert.True(
                (bool)method.Invoke(null, [frame, region])!));
        foreach (var backIndex in new[] { 3, 4 })
        {
            var middleItem = Phase2RecognitionRegions.CharacterEquipmentSlots(
                back[backIndex])[1];
            Assert.True((bool)method.Invoke(null, [frame, middleItem])!);
        }
    }

    private static CaptureFrame LoadScaledFrame(
        string relativePath,
        int width,
        int height)
    {
        using var source = Cv2.ImRead(
            Path.Combine(
                RepositoryRoot,
                "tests",
                "CurrencyWarsAssistant.Tests",
                "Fixtures",
                relativePath),
            ImreadModes.Color);
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size(width, height));
        using var bgra = new Mat();
        Cv2.CvtColor(scaled, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Cols,
            bgra.Rows,
            checked(bgra.Cols * 4),
            pixels,
            new PixelRect(0, 0, bgra.Cols, bgra.Rows),
            DateTimeOffset.UtcNow);
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
