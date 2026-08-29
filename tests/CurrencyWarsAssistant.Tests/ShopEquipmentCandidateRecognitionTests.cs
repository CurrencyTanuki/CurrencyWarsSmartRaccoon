using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

public sealed class ShopEquipmentCandidateRecognitionTests
{
    private readonly ITestOutputHelper _output;

    public ShopEquipmentCandidateRecognitionTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void VisibleShopEquipmentMatchesTheFirestormSharedIconGroup()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "preparation-shop-1-4.png"));
        var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
            Phase2RecognitionRegions.RewardShopCharacterSlots1920[0],
            compactFrontLayout: true);
        using var recognizer = new OpenCvPhase2IconRecognizer();
        var templates = Phase2IconTemplateCatalog.Load(dataDirectory);

        var result = Assert.Single(recognizer.Recognize(
            frame,
            "advanced-equipment",
            [regions[1]],
            templates));
        foreach (var candidate in result.RankedCandidates ?? [])
        {
            _output.WriteLine(
                $"{candidate.TemplateId}: {candidate.Confidence:F6}; " +
                $"exact={candidate.ResolvesExactIdentity}; " +
                $"candidates=[{string.Join('|', candidate.CandidateTemplateIds)}]");
        }

        // Firestorm and its privileged form intentionally share one icon, so
        // the safe result remains Unknown while preserving both visual IDs.
        Assert.False(result.IsKnown);
        Assert.Equal(
            new[]
            {
                "currency_wars_equipment_066",
                "currency_wars_equipment_105"
            },
            result.CandidateTemplateIds);
    }

    [Fact]
    public void CompactRegionsPreserveOccupiedAndEmptySlotsAcrossNativeFrames()
    {
        var fixtureDirectory = Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "video_prep");
        var fixturePaths = Enumerable.Range(0, 6)
            .Select(index => Path.Combine(fixtureDirectory, $"prep_{index}.png"))
            .ToArray();
        Assert.All(fixturePaths, path => Assert.True(File.Exists(path), path));

        var foregroundMethod = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "HasCenteredEquipmentForeground",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        Assert.NotNull(foregroundMethod);
        var expectedForeground = new[]
        {
            new[] { true, true, true },
            new[] { false, true, false },
            new[] { false, true, false },
            new[] { false, true, false }
        };

        foreach (var fixturePath in fixturePaths)
        {
            var frame = CaptureFrameLoader.LoadFile(fixturePath);
            Assert.Equal((1920, 1080), (frame.Width, frame.Height));
            for (var characterIndex = 0;
                 characterIndex < expectedForeground.Length;
                 characterIndex++)
            {
                var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
                    Phase2RecognitionRegions.RewardShopCharacterSlots1920[characterIndex],
                    compactFrontLayout: true);
                Assert.Equal(
                    expectedForeground[characterIndex],
                    regions.Select(region =>
                        (bool)foregroundMethod!.Invoke(null, [frame, region])!));
            }
        }
    }

    [Theory]
    [InlineData("shop_open_1_1.jpg", 1920, 1080, 3)]
    [InlineData("shop_open_1_2.jpg", 1920, 1080, 3)]
    [InlineData("reward_shop_after_two_purchases.jpg", 1920, 1080, 1)]
    [InlineData("reward_shop_after_two_purchases_2048x1152.png", 2559, 1439, 1)]
    public void CompactRegionsStayEmptyOnOtherNativeShopCaptures(
        string fixtureName,
        int expectedWidth,
        int expectedHeight,
        int occupiedFrontCount)
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            fixtureName));
        Assert.Equal((expectedWidth, expectedHeight), (frame.Width, frame.Height));
        var foregroundMethod = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "HasCenteredEquipmentForeground",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);
        Assert.NotNull(foregroundMethod);

        // Equipment regions are only evaluated for recognized character cards.
        // Empty card placeholders may contain decorative strokes and are outside
        // that production call path, so this checks the visibly occupied prefix.
        foreach (var characterBounds in
                 Phase2RecognitionRegions.RewardShopCharacterSlots1920
                     .Take(occupiedFrontCount))
        {
            var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
                characterBounds,
                compactFrontLayout: true);
            Assert.All(
                regions,
                region => Assert.False(
                    (bool)foregroundMethod!.Invoke(null, [frame, region])!));
        }
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
}
