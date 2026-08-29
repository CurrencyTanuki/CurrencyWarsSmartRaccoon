using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class HorizontalSpecialUnitRecognitionTests
{
    [Theory]
    [InlineData("p37_1080p_clean.png", 1920, 1080)]
    [InlineData("prep_3_7_142723217.png", 2560, 1440)]
    public void HorizontalPeipeiIsRecognizedAtSupportedResolutions(
        string fixtureName,
        int expectedWidth,
        int expectedHeight)
    {
        var frame = LoadFixture(fixtureName);
        using var recognizer = CreateRecognizer();

        var match = Assert.Single(recognizer.Recognize(frame));

        Assert.Multiple(
            () => Assert.Equal(expectedWidth, frame.Width),
            () => Assert.Equal(expectedHeight, frame.Height),
            () => Assert.Equal(
                OpenCvHorizontalSpecialUnitRecognizer.PeipeiId,
                match.SpecialUnitId),
            () => Assert.Equal("佩佩", match.DisplayName),
            () => Assert.Equal(340, match.ReferenceBounds.X),
            () => Assert.Equal(295, match.ReferenceBounds.Y),
            () => Assert.Equal(240, match.ReferenceBounds.Width),
            () => Assert.Equal(90, match.ReferenceBounds.Height),
            () => Assert.True(
                match.Confidence >= 0.75,
                $"横向佩佩置信度不足：{match.Confidence:F6}"));
    }

    [Theory]
    [InlineData("preparation_1_3_stable_2559x1439.png")]
    [InlineData("preparation_nine_cards_low_confidence_2048x1152.png")]
    public void FramesWithoutHorizontalPeipeiRemainNegative(string fixtureName)
    {
        var frame = LoadFixture(fixtureName);
        using var recognizer = CreateRecognizer();

        Assert.Empty(recognizer.Recognize(frame));
    }

    [Fact]
    public void TrackerPreservesRecognizedSpecialIdentityWithoutDecisionAuthority()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var evidence = new EvidenceReference(
            "fixture:horizontal-peipei-tracker",
            "vision:formation:Special:0",
            "佩佩",
            capturedAt,
            0.99);
        var input = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Preparation,
            PageId = "preparation_generic",
            NodeId = Observation<string>.Known(
                "3-7",
                1,
                [evidence],
                capturedAt),
            Formation = Observation<IReadOnlyList<FormationCharacterState>>.Known(
                [
                    new FormationCharacterState(
                        FormationZone.Special,
                        0,
                        OpenCvHorizontalSpecialUnitRecognizer.PeipeiId,
                        null,
                        "special-unit",
                        [],
                        0.99,
                        evidence,
                        CanDriveDecisions: false)
                ],
                0.99,
                [evidence],
                capturedAt)
        };

        var tracked = new Phase2OperationalStateTracker().Observe(input).Current;
        var peipei = Assert.Single(tracked.Formation.Value!);

        Assert.Multiple(
            () => Assert.Equal(FormationZone.Special, peipei.Zone),
            () => Assert.Equal(
                OpenCvHorizontalSpecialUnitRecognizer.PeipeiId,
                peipei.CharacterId),
            () => Assert.False(peipei.CanDriveDecisions));
    }

    private static OpenCvHorizontalSpecialUnitRecognizer CreateRecognizer() =>
        new(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4",
            "character-card-templates",
            "special_unit_peipei__horizontal-face.png"));

    private static CaptureFrame LoadFixture(string fileName) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            fileName));

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));
}
