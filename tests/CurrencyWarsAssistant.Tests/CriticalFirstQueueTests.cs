using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 红测（修复 3-7 结算帧保护先行）：关键事件通道——识别队列出队时
/// 关键帧（页面边界/结算页）优先于普通帧，即使普通帧先入队。
/// 背景：单帧分析约 10s 时，3-7 评级页关键帧排在普通帧 FIFO 后面
/// 数分钟，页面早已过去 → 整局结束识别无输入（用户实机反馈）。
/// </summary>
public sealed class CriticalFirstQueueTests
{
    [Fact]
    public async Task CriticalFrameDequeuesBeforeEarlierRegularFrames()
    {
        var queue = new Phase2BoundedRecognitionQueue(
            capacity: 6,
            maximumCriticalCapacity: 20);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(queue.Enqueue(WorkItem($"regular-{i}", isCritical: false)));
        }

        var critical = WorkItem("critical-settlement", isCritical: true);
        Assert.True(queue.Enqueue(critical));

        var first = await queue.DequeuePreferCriticalAsync(
            CancellationToken.None);
        Assert.True(first.IsCritical, "关键帧必须优先于先入队的普通帧出队");
        Assert.Equal("critical-settlement", first.ScreenshotName);

        // 普通帧入队去重（队列设计只保留最新普通帧）：剩 regular-2
        var second = await queue.DequeuePreferCriticalAsync(
            CancellationToken.None);
        Assert.Equal("regular-2", second.ScreenshotName);
    }

    [Fact]
    public async Task NoCriticalFrameDequeuesSoleRegularFrame()
    {
        var queue = new Phase2BoundedRecognitionQueue(
            capacity: 6,
            maximumCriticalCapacity: 20);
        Assert.True(queue.Enqueue(WorkItem("a", isCritical: false)));
        Assert.True(queue.Enqueue(WorkItem("b", isCritical: false)));
        // 普通帧入队去重：队列只保留最新普通帧 b
        var first = await queue.DequeuePreferCriticalAsync(
            CancellationToken.None);
        Assert.False(first.IsCritical);
        Assert.Equal("b", first.ScreenshotName);
    }

    private static long _sequence;

    private static Phase2RecognitionWorkItem WorkItem(
        string name,
        bool isCritical) => new(
        new Phase2BufferedFrame(
            ++_sequence,
            new CaptureFrame(
                16,
                16,
                64,
                new byte[16 * 16 * 4],
                new PixelRect(0, 0, 16, 16),
                DateTimeOffset.UtcNow),
            new Phase2FrameSignature(
                (ulong)_sequence,
                (ulong)_sequence,
                (ulong)_sequence,
                (ulong)_sequence,
                (ulong)_sequence,
                (ulong)_sequence),
            Phase2FrameChangeKind.SceneTransition,
            IsReliable: true),
        name,
        $"run:test/screenshots/{name}.png",
        "test-run",
        isCritical);
}
