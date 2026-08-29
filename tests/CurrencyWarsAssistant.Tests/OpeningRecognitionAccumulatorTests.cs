using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class OpeningRecognitionAccumulatorTests
{
    [Fact]
    public void SlotsMayStabilizeAcrossDifferentFrames()
    {
        var accumulator = new OpeningRecognitionAccumulator(3);

        accumulator.Observe(
        [
            Item(0, "a"),
            Item(1, "b"),
            Missing(2)
        ]);
        accumulator.Observe(
        [
            Item(0, "a"),
            Missing(1),
            Item(2, "c")
        ]);
        accumulator.Observe(
        [
            Missing(0),
            Item(1, "b"),
            Item(2, "c")
        ]);

        Assert.True(accumulator.TryBuild(out var result));
        Assert.Equal(["a", "b", "c"], result.Select(value => value.Item!.Id));
    }

    [Fact]
    public void TiedCandidatesKeepSlotUnresolved()
    {
        var accumulator = new OpeningRecognitionAccumulator(1);
        accumulator.Observe([Item(0, "a")]);
        accumulator.Observe([Item(0, "a")]);
        accumulator.Observe([Item(0, "b")]);
        accumulator.Observe([Item(0, "b")]);

        Assert.False(accumulator.TryBuild(out _));
        Assert.Equal(0, accumulator.ConfirmedSlotCount);
    }

    [Fact]
    public void LeadingCandidateWinsAfterAdditionalEvidence()
    {
        var accumulator = new OpeningRecognitionAccumulator(1);
        accumulator.Observe([Item(0, "a")]);
        accumulator.Observe([Item(0, "b")]);
        accumulator.Observe([Item(0, "a")]);

        Assert.True(accumulator.TryBuild(out var result));
        Assert.Equal("a", result.Single().Item!.Id);
    }

    [Fact]
    public void BestEffortKeepsStableSlotsAndLeavesUncertainSlotsEmpty()
    {
        var accumulator = new OpeningRecognitionAccumulator(3);
        accumulator.Observe([Item(0, "a"), Item(1, "b"), Missing(2)]);
        accumulator.Observe([Item(0, "a"), Item(1, "c"), Missing(2)]);

        var result = accumulator.BuildBestEffort(
            [Item(0, "a"), Item(1, "c"), Missing(2)]);

        Assert.Equal("a", result[0].Item!.Id);
        Assert.Null(result[1].Item);
        Assert.Null(result[2].Item);
    }

    [Fact]
    public void BestEffortKeepsSingleCompleteFrameWhenTransitionFramesAreEmpty()
    {
        var accumulator = new OpeningRecognitionAccumulator(4);
        accumulator.Observe(
        [
            Item(0, "a"),
            Item(1, "b"),
            Item(2, "c"),
            Item(3, "d")
        ]);
        accumulator.Observe(
        [
            Missing(0),
            Missing(1),
            Missing(2),
            Missing(3)
        ]);

        var result = accumulator.BuildBestEffort(
        [
            Missing(0),
            Missing(1),
            Missing(2),
            Missing(3)
        ]);

        Assert.Equal(
            ["a", "b", "c", "d"],
            result.Select(value => value.Item!.Id));
    }

    private static RecognizedOpeningItem Item(int slot, string id) =>
        new(slot, id, new ObservedItem(id, id, 0.9));

    private static RecognizedOpeningItem Missing(int slot) =>
        new(slot, "", null);
}
