using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2PageStructuralEvidenceTests
{
    private readonly ITestOutputHelper _output;

    public Phase2PageStructuralEvidenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Theory]
    [InlineData("130104.png")]
    [InlineData("130123.png")]
    [InlineData("132328.png")]
    public void ActionTimelineProvidesBattleOnlyStructuralEvidence(string fileName)
    {
        var frame = LoadReference(fileName);
        var templates = Phase2IconTemplateCatalog.Load(Path.Combine(
            RepositoryRoot,
            "data",
            "4.4"));

        var result = Phase2ActionIndicatorLocator.Locate(frame, templates);
        Assert.NotNull(result);
        _output.WriteLine(
            $"{fileName}: {result!.TemplateId}, {result.Confidence:F3}, " +
            $"({result.Region.X},{result.Region.Y})");
    }

    private static CaptureFrame LoadReference(string fileName) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-2026-07-28",
            fileName));
}
