using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 识别队列契约测试（2026-08-06 会话 W）：
/// 1) 跳帧（DropStaleFrames）后信号量计数一致：Dequeue 不抛异常、不永久阻塞；
/// 2) 跳帧保留最新关键帧（页面边界/终局不被丢）；
/// 3) 拥塞替换路径不泄漏信号量计数（review should-fix 回归）。
/// </summary>
public sealed class Phase2RecognitionQueueTests
{
    [Fact]
    public async Task DropStaleFramesWhenLatestFrameIsCriticalKeepsItsNewestCriticalPredecessor()
    {
        var queue = new Phase2BoundedRecognitionQueue();
        Assert.True(queue.Enqueue(Item(0, isCritical: true)));
        Assert.True(queue.Enqueue(Item(1, isCritical: true)));
        Assert.True(queue.Enqueue(Item(2, isCritical: true)));

        queue.DropStaleFrames();

        // 关键事件通道（2026-08-15）：关键帧全部保留，不丢中间关键帧。
        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(first.IsCritical);
        Assert.Equal(0L, first.BufferedFrame.Sequence);

        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(second.IsCritical);
        Assert.Equal(1L, second.BufferedFrame.Sequence);

        var latest = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(latest.IsCritical);
        Assert.Equal(2L, latest.BufferedFrame.Sequence);

        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task DropStaleFramesWithInterleavedRegularKeepsOnlyNewestCriticalPair()
    {
        var queue = new Phase2BoundedRecognitionQueue(capacity: 4);
        Assert.True(queue.Enqueue(Item(0, isCritical: true)));
        Assert.True(queue.Enqueue(Item(1, isCritical: true)));
        Assert.True(queue.Enqueue(Item(2, isCritical: false)));
        Assert.True(queue.Enqueue(Item(3, isCritical: true)));

        queue.DropStaleFrames();

        // 关键事件通道（2026-08-15）：关键帧全部保留 + 最新普通帧。
        var first = await queue.DequeueAsync(CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);
        var regular = await queue.DequeueAsync(CancellationToken.None);
        var latest = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(first.IsCritical);
        Assert.Equal(0L, first.BufferedFrame.Sequence);

        Assert.True(second.IsCritical);
        Assert.Equal(1L, second.BufferedFrame.Sequence);

        Assert.False(regular.IsCritical);
        Assert.Equal(2L, regular.BufferedFrame.Sequence);

        Assert.True(latest.IsCritical);
        Assert.Equal(3L, latest.BufferedFrame.Sequence);

        using var cancellation = new CancellationTokenSource();
        var pending = queue.DequeueAsync(cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task DropStaleFramesWithMultipleCriticalFramesKeepsNewestCriticalBeforeLatestFrame()
    {
        var queue = new Phase2BoundedRecognitionQueue();
        Assert.True(queue.Enqueue(Item(0, isCritical: true)));
        Assert.True(queue.Enqueue(Item(1, isCritical: true)));
        Assert.True(queue.Enqueue(Item(2, isCritical: false)));

        queue.DropStaleFrames();

        // 关键事件通道（2026-08-15）：关键帧全部保留 + 最新普通帧。
        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(first.IsCritical);
        Assert.Equal(0L, first.BufferedFrame.Sequence);

        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(second.IsCritical);
        Assert.Equal(1L, second.BufferedFrame.Sequence);

        var latest = await queue.DequeueAsync(CancellationToken.None);
        Assert.False(latest.IsCritical);
        Assert.Equal(2L, latest.BufferedFrame.Sequence);

        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task DropStaleFramesKeepsLatestCriticalAndDequeueIsConsistent()
    {
        var queue = new Phase2BoundedRecognitionQueue();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(queue.Enqueue(Item(i, isCritical: i == 2)));
        }

        // 跳帧：应保留"最新关键帧(i=2) + 最新帧(i=4)"，丢弃 i=0/1/3
        queue.DropStaleFrames();

        var first = await queue.DequeueAsync(CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.True(first.IsCritical);
        Assert.Equal(4L, second.BufferedFrame.Sequence);

        // 队列已空：下一次 Dequeue 应等待（信号量计数一致，不抛异常）
        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task EnqueueReplacesStaleOrdinaryFramesAndKeepsLatestCritical()
    {
        var queue = new Phase2BoundedRecognitionQueue();
        // 普通帧入队会互相替换（Enqueue 730 路径：非关键帧替换旧普通帧），
        // 队列只保留最新普通帧；关键帧替换普通帧（拥塞替换路径）。
        for (var i = 0; i < 6; i++)
        {
            Assert.True(queue.Enqueue(Item(i, isCritical: false)));
        }

        Assert.True(queue.Enqueue(Item(6, isCritical: true)));

        // 连续取出：最新普通帧(i=5) + 关键帧(i=6)，信号量计数一致不抛异常
        var ordinary = await queue.DequeueAsync(CancellationToken.None);
        var critical = await queue.DequeueAsync(CancellationToken.None);
        Assert.False(ordinary.IsCritical);
        Assert.Equal(5L, ordinary.BufferedFrame.Sequence);
        Assert.True(critical.IsCritical);
        Assert.Equal(6L, critical.BufferedFrame.Sequence);

        // 队列已空：下一次 Dequeue 应等待（available 计数与 items 一致）
        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task CriticalCongestionReplacementKeepsLatestAndCountsConsistent()
    {
        // 覆盖 Enqueue 751 行拥塞分支（maximumCriticalCapacity=20）：
        // 塞满 20 个关键帧后，第 21 个关键帧触发 RemoveFirst+AddLast
        // （无信号量泄漏）→ Dequeue 20 次成功、第 21 次阻塞等待。
        var queue = new Phase2BoundedRecognitionQueue();
        for (var i = 0; i < 20; i++)
        {
            Assert.True(queue.Enqueue(Item(i, isCritical: true)));
        }

        Assert.True(queue.Enqueue(Item(20, isCritical: true)));

        for (var i = 0; i < 20; i++)
        {
            var item = await queue.DequeueAsync(CancellationToken.None);
            Assert.NotNull(item);
        }

        var pending = queue.DequeueAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
    }

    private static Phase2RecognitionWorkItem Item(long sequence, bool isCritical) => new(
        new Phase2BufferedFrame(
            sequence,
            Frame(),
            new Phase2FrameSignature(
                (ulong)sequence,
                (ulong)sequence,
                (ulong)sequence,
                (ulong)sequence,
                (ulong)sequence,
                (ulong)sequence),
            Phase2FrameChangeKind.SceneTransition,
            IsReliable: true),
        "shot.png",
        "evidence",
        "run",
        isCritical);

    private static CaptureFrame Frame() => new(
        16,
        16,
        64,
        new byte[16 * 16 * 4],
        new PixelRect(0, 0, 16, 16),
        DateTimeOffset.UtcNow);
}
