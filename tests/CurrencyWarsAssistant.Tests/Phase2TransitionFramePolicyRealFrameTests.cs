using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2TransitionFramePolicyRealFrameTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    [Theory]
    [InlineData("20260801-052238727.png")]
    [InlineData("20260801-052257314.png")]
    [InlineData("20260801-052326804.png")]
    [InlineData("20260801-052327661.png")]
    [InlineData("20260801-052341035.png")]
    [InlineData("20260801-055453028.png")]
    public void MovingUnknownAnimationFramesAreDiscarded(string fileName)
    {
        var frame = Load(fileName);
        var marked = Phase2TransitionFramePolicy.MarkIfApplicable(
            UnknownAnalysis(frame),
            Buffered(frame, Phase2FrameChangeKind.RegionalChange));

        Assert.Equal(Phase2PageFamily.Transition, marked.OperationalState!.PageFamily);
        Assert.Equal("transition_animation", marked.Snapshot.PageId.Value);
        Assert.True(Phase2TransitionFramePolicy.ShouldDiscard(marked));
    }

    [Theory]
    [InlineData("20260801-052327905.png")]
    [InlineData("20260801-052358279.png")]
    public void StaticDarkTransitionFramesAreDiscarded(string fileName)
    {
        var frame = Load(fileName);
        var marked = Phase2TransitionFramePolicy.MarkIfApplicable(
            UnknownAnalysis(frame),
            Buffered(frame, Phase2FrameChangeKind.Unchanged));

        Assert.Equal(Phase2PageFamily.Transition, marked.OperationalState!.PageFamily);
        Assert.True(Phase2TransitionFramePolicy.ShouldDiscard(marked));
    }

    [Fact]
    public void StableUnknownNonDarkFrameRemainsAvailableForFutureRecognition()
    {
        var frame = Load("20260801-052238727.png");
        var preserved = Phase2TransitionFramePolicy.MarkIfApplicable(
            UnknownAnalysis(frame),
            Buffered(frame, Phase2FrameChangeKind.Unchanged));

        Assert.Equal(Phase2PageFamily.Unknown, preserved.OperationalState!.PageFamily);
        Assert.False(Phase2TransitionFramePolicy.ShouldDiscard(preserved));
    }

    [Fact]
    public void CharacterCutInMisclassifiedAsEvidenceFreeBattleIsDiscarded()
    {
        var frame = Load("20260801-055332612.png");
        var raw = UnknownAnalysis(frame) with
        {
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Battle,
                PageId = "unknown"
            }
        };

        var marked = Phase2TransitionFramePolicy.MarkIfApplicable(
            raw,
            Buffered(frame, Phase2FrameChangeKind.RegionalChange));

        Assert.Equal(Phase2PageFamily.Transition, marked.OperationalState!.PageFamily);
        Assert.True(Phase2TransitionFramePolicy.ShouldDiscard(marked));
    }

    [Fact]
    public void ConfirmedPreparationAndSettlementPagesAreNeverDiscarded()
    {
        var preparationFrame = Load("20260801-052317183.png");
        var preparation = UnknownAnalysis(preparationFrame) with
        {
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.Preparation,
                PageId = "preparation_generic",
                NodeId = Observation<string>.Known("1-1", 0.88)
            }
        };
        var preservedPreparation = Phase2TransitionFramePolicy.MarkIfApplicable(
            preparation,
            Buffered(preparationFrame, Phase2FrameChangeKind.SceneTransition));
        Assert.Equal(
            Phase2PageFamily.Preparation,
            preservedPreparation.OperationalState!.PageFamily);
        Assert.False(Phase2TransitionFramePolicy.ShouldDiscard(preservedPreparation));

        var settlementFrame = Load("20260801-052401448.png");
        var settlement = UnknownAnalysis(settlementFrame) with
        {
            Snapshot = EmptySnapshot(settlementFrame.CapturedAt) with
            {
                PageId = Observation<string>.Known(
                    "challenge_success",
                    0.92,
                    observedAt: settlementFrame.CapturedAt)
            },
            OperationalState = new Phase2OperationalState
            {
                PageFamily = Phase2PageFamily.BattleSettlement,
                PageId = "challenge_success"
            }
        };
        var preservedSettlement = Phase2TransitionFramePolicy.MarkIfApplicable(
            settlement,
            Buffered(settlementFrame, Phase2FrameChangeKind.SceneTransition));
        Assert.Equal(
            Phase2PageFamily.BattleSettlement,
            preservedSettlement.OperationalState!.PageFamily);
        Assert.False(Phase2TransitionFramePolicy.ShouldDiscard(preservedSettlement));
    }

    [Theory]
    [InlineData("20260801-055614813.png", 0.817)]
    [InlineData("20260801-055615182.png", 0.803)]
    [InlineData("20260801-055615397.png", 0.816)]
    public void NearThresholdFailureSettlementFramesAreNotDiscarded(
        string fileName,
        double confidence)
    {
        var frame = Load(fileName);
        var raw = UnknownAnalysis(frame) with
        {
            Warnings =
            [
                "recognition:classifier-miss " +
                $"challenge_health_depleted/challenge_ended_title=" +
                $"{confidence:0.000}/0.840"
            ]
        };

        var preserved = Phase2TransitionFramePolicy.MarkIfApplicable(
            raw,
            Buffered(frame, Phase2FrameChangeKind.SceneTransition));

        Assert.Equal(Phase2PageFamily.Unknown, preserved.OperationalState!.PageFamily);
        Assert.False(Phase2TransitionFramePolicy.ShouldDiscard(preserved));
    }

    [Fact]
    public void NearThresholdBattleFrameRemainsAvailableForRecognition()
    {
        var frame = Load("20260801-052355994.png");
        var raw = UnknownAnalysis(frame) with
        {
            Warnings =
            [
                "recognition:classifier-miss " +
                "reward_battle/reward_battle_status_bar=0.841/0.900"
            ]
        };

        var preserved = Phase2TransitionFramePolicy.MarkIfApplicable(
            raw,
            Buffered(frame, Phase2FrameChangeKind.SceneTransition));

        Assert.Equal(Phase2PageFamily.Unknown, preserved.OperationalState!.PageFamily);
        Assert.False(Phase2TransitionFramePolicy.ShouldDiscard(preserved));
    }

    private static ScreenshotAnalysisResult UnknownAnalysis(CaptureFrame frame) => new()
    {
        AnalysisId = "transition-policy-fixture",
        Snapshot = EmptySnapshot(frame.CapturedAt),
        OperationalState = new Phase2OperationalState
        {
            PageFamily = Phase2PageFamily.Unknown,
            PageId = "unknown"
        }
    };

    private static RunSnapshot EmptySnapshot(DateTimeOffset capturedAt) => new()
    {
        RunId = "transition-policy-fixture",
        AsOf = capturedAt
    };

    private static Phase2BufferedFrame Buffered(
        CaptureFrame frame,
        Phase2FrameChangeKind changeKind) => new(
        1,
        frame,
        Phase2RealtimeFrameBuffer.CreateSignature(frame),
        changeKind,
        IsReliable: false);

    private static CaptureFrame Load(string fileName) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-transition-2026-08-01",
            fileName));
}
