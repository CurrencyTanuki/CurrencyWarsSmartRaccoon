using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class PreparationMoveVerifierTests
{
    private static readonly PixelRect Source =
        new(400, 700, 240, 240);
    private static readonly PixelRect Target =
        new(900, 250, 240, 240);

    [Fact]
    public void DetectsSourceLeavingAndTargetBecomingOccupied()
    {
        var before = CreateFrame();
        Paint(before, Source, 220, 45, 35);
        Paint(before, Target, 24, 34, 70);
        var after = CreateFrame();
        Paint(after, Source, 24, 34, 70);
        Paint(after, Target, 220, 45, 35);

        var result = PreparationMoveVerifier.Compare(
            before,
            after,
            Source,
            Target);

        Assert.True(result.MoveObserved);
        Assert.True(result.SourceChanged);
        Assert.True(result.TargetChanged);
    }

    [Fact]
    public void TreatsIdenticalFramesAsNoMove()
    {
        var before = CreateFrame();
        Paint(before, Source, 220, 45, 35);
        Paint(before, Target, 24, 34, 70);
        var after = before with
        {
            BgraPixels = [.. before.BgraPixels],
            CapturedAt = DateTimeOffset.UtcNow
        };

        var result = PreparationMoveVerifier.Compare(
            before,
            after,
            Source,
            Target);

        Assert.True(result.DefinitelyUnchanged);
        Assert.False(result.MoveObserved);
    }

    [Fact]
    public void CompanionSelectionRequiresHimekoAndAnotherTrainCompanion()
    {
        var himeko = Character(
            "himeko_qixing",
            PreparationCompanionSelectionPolicy.HimekoQixingName,
            "列车同行");
        var trailblazer = Character(
            "trailblazer",
            "开拓者",
            "列车同行",
            "欢愉",
            "能量");
        var unrelated = Character("unrelated", "无关角色", "能量");

        Assert.False(PreparationCompanionSelectionPolicy.CanTrigger([himeko]));
        Assert.False(PreparationCompanionSelectionPolicy.CanTrigger(
            [himeko, unrelated]));
        Assert.True(PreparationCompanionSelectionPolicy.CanTrigger(
            [himeko, trailblazer]));
        Assert.Equal(
            "companion_selection",
            PreparationCompanionSelectionPolicy.PageId);
    }

    private static CaptureFrame CreateFrame()
    {
        const int width = 192;
        const int height = 108;
        const int stride = width * 4;
        return new CaptureFrame(
            width,
            height,
            stride,
            new byte[stride * height],
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }

    private static void Paint(
        CaptureFrame frame,
        PixelRect reference,
        byte red,
        byte green,
        byte blue)
    {
        var left = reference.X * frame.Width /
                   OpenCvTemplateMatcher.ReferenceWidth;
        var top = reference.Y * frame.Height /
                  OpenCvTemplateMatcher.ReferenceHeight;
        var right = reference.Right * frame.Width /
                    OpenCvTemplateMatcher.ReferenceWidth;
        var bottom = reference.Bottom * frame.Height /
                     OpenCvTemplateMatcher.ReferenceHeight;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = y * frame.Stride + x * 4;
                frame.BgraPixels[offset] = blue;
                frame.BgraPixels[offset + 1] = green;
                frame.BgraPixels[offset + 2] = red;
                frame.BgraPixels[offset + 3] = 255;
            }
        }
    }

    private static CurrencyWarsCharacterData Character(
        string id,
        string name,
        params string[] bonds) =>
        new(id, name, "前后台", [1], false, bonds);
}
