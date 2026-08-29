using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.App;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2EvidenceReviewTests
{
    private static readonly EvidenceReference Evidence = new(
        "fixture",
        "region",
        CapturedAt: DateTimeOffset.Parse("2026-07-29T09:00:00+08:00"));

    [Fact]
    public void Resolve_OcrIdentifiesAmbiguousVisualCandidate_UsesOcrIdentity()
    {
        var result = Phase2NamedContentEvidenceResolver.Resolve(
            Phase2NamedContentKind.InvestmentStrategy,
            "strategy-1",
            new RelativeRegion(0.4, 0.1, 0.1, 0.1),
            new Phase2OcrNameEvidence(
                "investment_strategy_002",
                "标准策略名",
                ["标准策略名"],
                0.94),
            new Phase2IconRecognition(
                0,
                new CurrencyWarsAssistant.Core.PixelRect(0, 0, 100, 100),
                "visual_investment_strategy_duplicate",
                0.88,
                false,
                ["investment_strategy_001", "investment_strategy_002"]),
            Evidence);

        Assert.Equal(ObservationStatus.Known, result.Status);
        Assert.Equal("investment_strategy_002", result.ObjectId);
        Assert.Equal("标准策略名", result.StandardName);
        Assert.Equal(
            Phase2RecognitionEvidenceKind.OcrAndIcon,
            result.EvidenceKind);
    }

    [Fact]
    public void Resolve_OcrAndExactIconDisagree_ReturnsConflict()
    {
        var result = Phase2NamedContentEvidenceResolver.Resolve(
            Phase2NamedContentKind.NegativeAffix,
            "affix-1",
            new RelativeRegion(0.1, 0.1, 0.1, 0.1),
            new Phase2OcrNameEvidence(
                "enemy_affix_001",
                "词条一",
                ["词条一"],
                0.93),
            new Phase2IconRecognition(
                0,
                new CurrencyWarsAssistant.Core.PixelRect(0, 0, 100, 100),
                "enemy_affix_002",
                0.90,
                true,
                ["enemy_affix_002"]),
            Evidence);

        Assert.Equal(ObservationStatus.Conflict, result.Status);
        Assert.Null(result.ObjectId);
        Assert.NotEmpty(result.Conflicts);
    }

    [Fact]
    public void Resolve_IconWithoutText_UsesOnlyPurposeScopedIcon()
    {
        var result = Phase2NamedContentEvidenceResolver.Resolve(
            Phase2NamedContentKind.Synergy,
            "damage-row-2",
            new RelativeRegion(0.8, 0.2, 0.1, 0.1),
            null,
            new Phase2IconRecognition(
                1,
                new CurrencyWarsAssistant.Core.PixelRect(0, 0, 100, 100),
                "bond_能量",
                0.91,
                true,
                ["bond_能量"]),
            Evidence,
            iconOnlyWithoutText: true);

        Assert.Equal(ObservationStatus.Known, result.Status);
        Assert.Equal("bond_能量", result.ObjectId);
        Assert.Equal(
            Phase2RecognitionEvidenceKind.IconOnlyWithoutText,
            result.EvidenceKind);
    }

    [Fact]
    public void BatchCommand_ValidArguments_UsesIndependentDirectories()
    {
        var command = Phase2BatchCommand.Parse(
            [Phase2BatchCommand.Switch, ".\\fixtures", ".\\reports"]);

        Assert.NotNull(command);
        Assert.True(Path.IsPathRooted(command!.SourceDirectory));
        Assert.True(Path.IsPathRooted(command.OutputDirectory));
        Assert.False(command.ContinuousSequence);
        Assert.True(command.WriteAnnotations);
    }

    [Fact]
    public void BatchCommand_ContinuousSequence_PreservesOneRunAcrossFrames()
    {
        var command = Phase2BatchCommand.Parse(
            [
                Phase2BatchCommand.Switch,
                "--input",
                ".\\fixtures",
                "--continuous-sequence",
                "--output",
                ".\\reports"
            ]);

        Assert.NotNull(command);
        Assert.True(command!.ContinuousSequence);
        Assert.True(command.WriteAnnotations);
    }

    [Fact]
    public void BatchCommand_NoAnnotations_DisablesOnlyDiagnosticImages()
    {
        var command = Phase2BatchCommand.Parse(
            [
                Phase2BatchCommand.Switch,
                "--input",
                ".\\fixtures",
                "--output",
                ".\\reports",
                "--no-annotations"
            ]);

        Assert.NotNull(command);
        Assert.False(command!.WriteAnnotations);
        Assert.False(command.ContinuousSequence);
    }

    [Fact]
    public void BatchCommand_NoSwitch_DoesNotChangeNormalStartup()
    {
        Assert.Null(Phase2BatchCommand.Parse([]));
        Assert.Null(Phase2BatchCommand.Parse(["--unrelated"]));
    }

    [Theory]
    [InlineData("--input", ".\\fixtures", "--output", ".\\reports")]
    [InlineData("--output", ".\\reports", "--input", ".\\fixtures")]
    public void BatchCommand_NamedDirectories_AcceptsEitherOrder(
        string firstOption,
        string firstValue,
        string secondOption,
        string secondValue)
    {
        var command = Phase2BatchCommand.Parse(
            [
                Phase2BatchCommand.Switch,
                firstOption,
                firstValue,
                secondOption,
                secondValue
            ]);

        Assert.NotNull(command);
        Assert.Equal(Path.GetFullPath(".\\fixtures"), command.SourceDirectory);
        Assert.Equal(Path.GetFullPath(".\\reports"), command.OutputDirectory);
    }

    [Fact]
    public void DoubleHandsOnKeyboardStrategyDeclaresGemiSpecialUnit()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var gameData = GameDataCatalogLoader.Load(Path.Combine(
            repositoryRoot,
            "data",
            "4.4"));
        var strategy = Assert.Single(gameData.InvestmentStrategies.Where(item =>
            item.Id == "investment_strategy_320"));

        var unit = Assert.Single(
            Phase2OperationalScreenshotAnalyzer.TriggeredSpecialUnits(strategy));

        Assert.Equal("special_unit_gemi_li", unit.Id);
        Assert.Equal("Gemi狸", unit.Name);
        Assert.Contains("Gemi狸", strategy.Effect, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSpecialUnitTriggerIsDataDrivenFromEffect()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var gameData = GameDataCatalogLoader.Load(Path.Combine(
            repositoryRoot,
            "data",
            "4.4"));
        var environment = gameData.InvestmentEnvironments.First(item =>
            item.Effect.Contains("佩佩", StringComparison.Ordinal));

        var units = Phase2OperationalScreenshotAnalyzer
            .TriggeredSpecialUnits(environment)
            .ToArray();

        Assert.Contains(units, item =>
            item.Id == "special_unit_peipei" && item.Name == "佩佩");
    }

    [Fact]
    public void ExactIconWithoutTextIsNormalKnownRecognition()
    {
        var result = Phase2NamedContentEvidenceResolver.Resolve(
            Phase2NamedContentKind.InvestmentStrategy,
            "strategy-1",
            new RelativeRegion(0.45, 0.1, 0.02, 0.04),
            null,
            new Phase2IconRecognition(
                0,
                new CurrencyWarsAssistant.Core.PixelRect(0, 0, 40, 40),
                "investment_strategy_320",
                0.94,
                true,
                ["investment_strategy_320"]),
            Evidence);

        Assert.Equal(ObservationStatus.Known, result.Status);
        Assert.Equal("investment_strategy_320", result.ObjectId);
        Assert.Equal(Phase2RecognitionEvidenceKind.Icon, result.EvidenceKind);
    }
}
