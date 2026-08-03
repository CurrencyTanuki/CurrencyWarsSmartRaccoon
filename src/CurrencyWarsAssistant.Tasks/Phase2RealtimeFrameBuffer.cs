using System.Numerics;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

internal enum Phase2FrameChangeKind
{
    Unchanged,
    RegionalChange,
    SceneTransition
}

internal sealed record Phase2FrameSignature(
    ulong FullFrame,
    ulong Top,
    ulong Center,
    ulong Left,
    ulong Right,
    ulong Bottom)
{
    public double DifferenceRatio(Phase2FrameSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var changedBits = BitOperations.PopCount(FullFrame ^ other.FullFrame) +
                          BitOperations.PopCount(Top ^ other.Top) +
                          BitOperations.PopCount(Center ^ other.Center) +
                          BitOperations.PopCount(Left ^ other.Left) +
                          BitOperations.PopCount(Right ^ other.Right) +
                          BitOperations.PopCount(Bottom ^ other.Bottom);
        return changedBits / (64d * 6d);
    }
}

internal sealed record Phase2BufferedFrame(
    long Sequence,
    CaptureFrame Frame,
    Phase2FrameSignature Signature,
    Phase2FrameChangeKind ChangeKind,
    bool IsReliable);

internal sealed class Phase2RealtimeFrameBuffer
{
    private const double UnchangedThreshold = 0.035;
    private const double SceneTransitionThreshold = 0.20;
    private readonly object gate = new();
    private readonly Queue<Phase2BufferedFrame> frames;
    private readonly int capacity;
    private long sequence;

    public Phase2RealtimeFrameBuffer(int capacity = 12)
    {
        if (capacity is < 3 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        frames = new Queue<Phase2BufferedFrame>(capacity);
    }

    public Phase2BufferedFrame Add(CaptureFrame frame, bool isReliable)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var signature = CreateSignature(frame);
        lock (gate)
        {
            var previous = frames.Count == 0 ? null : frames.Last();
            var changeKind = previous is null
                ? Phase2FrameChangeKind.SceneTransition
                : Classify(signature.DifferenceRatio(previous.Signature));
            var buffered = new Phase2BufferedFrame(
                ++sequence,
                frame,
                signature,
                changeKind,
                isReliable &&
                (previous is null ||
                 changeKind != Phase2FrameChangeKind.SceneTransition));
            frames.Enqueue(buffered);
            while (frames.Count > capacity)
            {
                frames.Dequeue();
            }

            return buffered;
        }
    }

    public IReadOnlyList<Phase2BufferedFrame> LockLatestReliable(int count = 3)
    {
        if (count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (gate)
        {
            return frames
                .Where(item => item.IsReliable)
                .TakeLast(count)
                .ToArray();
        }
    }

    public IReadOnlyList<Phase2BufferedFrame> LockLatestStableCandidates(
        int count = 3)
    {
        if (count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (gate)
        {
            var transitionSequence = frames.Count > 0 &&
                                     frames.Last().ChangeKind ==
                                     Phase2FrameChangeKind.SceneTransition
                ? frames.Last().Sequence
                : long.MaxValue;
            return frames
                .Where(item =>
                    item.Sequence < transitionSequence &&
                    item.ChangeKind != Phase2FrameChangeKind.SceneTransition)
                .TakeLast(count)
                .ToArray();
        }
    }

    public IReadOnlyList<Phase2BufferedFrame> LockLatestPredecessors(
        int count = 2)
    {
        if (count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (gate)
        {
            return frames.Count <= 1
                ? []
                : frames.Take(frames.Count - 1).TakeLast(count).ToArray();
        }
    }

    public Phase2BufferedFrame? Latest
    {
        get
        {
            lock (gate)
            {
                return frames.Count == 0 ? null : frames.Last();
            }
        }
    }

    internal static Phase2FrameSignature CreateSignature(CaptureFrame frame) =>
        new(
            PerceptualHash(frame, 0.00, 0.00, 1.00, 1.00),
            PerceptualHash(frame, 0.15, 0.00, 0.70, 0.22),
            PerceptualHash(frame, 0.24, 0.20, 0.52, 0.48),
            PerceptualHash(frame, 0.00, 0.12, 0.24, 0.72),
            PerceptualHash(frame, 0.76, 0.12, 0.24, 0.72),
            PerceptualHash(frame, 0.12, 0.74, 0.76, 0.26));

    private static Phase2FrameChangeKind Classify(double differenceRatio) =>
        differenceRatio < UnchangedThreshold
            ? Phase2FrameChangeKind.Unchanged
            : differenceRatio >= SceneTransitionThreshold
                ? Phase2FrameChangeKind.SceneTransition
                : Phase2FrameChangeKind.RegionalChange;

    private static ulong PerceptualHash(
        CaptureFrame frame,
        double normalizedX,
        double normalizedY,
        double normalizedWidth,
        double normalizedHeight)
    {
        var x = Math.Clamp(
            (int)Math.Round(normalizedX * frame.Width),
            0,
            frame.Width - 1);
        var y = Math.Clamp(
            (int)Math.Round(normalizedY * frame.Height),
            0,
            frame.Height - 1);
        var width = Math.Max(
            2,
            Math.Min(
                frame.Width - x,
                (int)Math.Round(normalizedWidth * frame.Width)));
        var height = Math.Max(
            2,
            Math.Min(
                frame.Height - y,
                (int)Math.Round(normalizedHeight * frame.Height)));
        Span<int> samples = stackalloc int[64];
        var sample = 0;
        for (var sampleY = 0; sampleY < 8; sampleY++)
        {
            var pixelY = y + Math.Min(
                height - 1,
                (int)Math.Round(sampleY * (height - 1) / 7d));
            for (var sampleX = 0; sampleX < 8; sampleX++)
            {
                var pixelX = x + Math.Min(
                    width - 1,
                    (int)Math.Round(sampleX * (width - 1) / 7d));
                samples[sample++] = Luminance(frame, pixelX, pixelY);
            }
        }

        var average = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            average += samples[index];
        }

        average /= samples.Length;
        ulong hash = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            if (samples[index] >= average)
            {
                hash |= 1UL << index;
            }
        }

        return hash;
    }

    private static int Luminance(CaptureFrame frame, int x, int y)
    {
        var offset = y * frame.Stride + x * 4;
        return (frame.BgraPixels[offset] * 29 +
                frame.BgraPixels[offset + 1] * 150 +
                frame.BgraPixels[offset + 2] * 77) >> 8;
    }
}
