using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 增量识别（帧变化对比）核心行为测试：
/// 1) 静止画面在 2s 全量兜底之间应产出 IsIncremental 增量帧（每 1.5s 一次，
///    只补失败字段），且不得饿死 2s 全量。
/// 2) 静止帧不满足 1.5s 间隔时不产出增量帧（去重节流仍生效）。
/// </summary>
public sealed class Phase2IncrementalSelectorTests
{
    [Fact]
    public void StaticStreamYieldsIncrementalFramesBetweenFullRefreshCadence()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-08-18T00:00:00.000+08:00");
        var selected = new List<Phase2SelectedFrame>();

        // 静止流：同像素帧 → ChangeKind==Unchanged。5fps 共 6.1 秒。
        for (var index = 0; index < 31; index++)
        {
            selected.AddRange(selector.Observe(
                CreateFrame(24) with { CapturedAt = start.AddMilliseconds(index * 200) },
                wasReliable: true,
                Phase2PageFamily.Preparation).FramesToRecognize);
        }

        var incrementals = selected
            .Where(item => item.IsIncremental)
            .OrderBy(item => item.BufferedFrame.Frame.CapturedAt)
            .ToArray();
        var fulls = selected
            .Where(item => !item.IsIncremental)
            .OrderBy(item => item.BufferedFrame.Frame.CapturedAt)
            .ToArray();

        // 2s 全量兜底不能丢：首帧 + 0/2/4/6s 全量帧。
        Assert.NotEmpty(fulls);
        Assert.Equal(start, fulls[0].BufferedFrame.Frame.CapturedAt);
        Assert.Contains(fulls, item =>
            item.BufferedFrame.Frame.CapturedAt == start.AddSeconds(2));

        // 静止画面应在全量间隔之间产出增量帧（每 1.5s）。
        // 期望序列：0 全量 → 1.6s 增量 → 2s 全量 → 3.6s 增量 → 4s 全量 →
        //           5.6s 增量 → 6s 全量。
        Assert.True(
            incrementals.Length >= 2,
            $"期望至少 2 个增量帧，实际 {incrementals.Length}（全量 {fulls.Length} 帧：" +
            $" {string.Join(",", fulls.Select(f => (f.BufferedFrame.Frame.CapturedAt - start).TotalMilliseconds))}）");
        // 第一个增量帧必须落在首全量（0s）与第二个全量（2s）之间。
        Assert.True(fulls.Length >= 2);
        Assert.True(
            incrementals[0].BufferedFrame.Frame.CapturedAt > fulls[0].BufferedFrame.Frame.CapturedAt &&
            incrementals[0].BufferedFrame.Frame.CapturedAt < fulls[1].BufferedFrame.Frame.CapturedAt,
            $"期望增量帧落在两次全量之间，增量ts={incrementals[0].BufferedFrame.Frame.CapturedAt - start}，" +
            $"全量ts=[{string.Join(",", fulls.Select(f => (f.BufferedFrame.Frame.CapturedAt - start).TotalMilliseconds))}]");
    }

    [Fact]
    public void StaticFrameBeforeIncrementalIntervalShouldNotQueue()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-08-18T00:00:00.000+08:00");

        selector.Observe(
            CreateFrame(24) with { CapturedAt = start },
            wasReliable: true,
            Phase2PageFamily.Preparation);
        // 不到 1.5s 的静止帧：既不是全量（<2s）也不是增量（<1.5s），应去重跳过。
        selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(700) },
            wasReliable: true,
            Phase2PageFamily.Preparation);
        var tooSoon = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(1100) },
            wasReliable: true,
            Phase2PageFamily.Preparation);

        Assert.Empty(tooSoon.FramesToRecognize);

        // 满 1.5s 的静止帧 → 增量帧。
        var due = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(1600) },
            wasReliable: true,
            Phase2PageFamily.Preparation);
        Assert.Contains(due.FramesToRecognize, item => item.IsIncremental);
    }

    [Fact]
    public void PageChangeStillForcesFullNotIncremental()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-08-18T00:00:00.000+08:00");

        selector.Observe(
            CreateFrame(24) with { CapturedAt = start },
            wasReliable: true,
            Phase2PageFamily.Preparation);
        // 页面变化（战斗页 fast 匹配）：应走关键/全量，绝不产出增量帧。
        var change = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(200) },
            wasReliable: true,
            Phase2PageFamily.Battle,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Battle,
                "battle_generic"));

        Assert.NotEmpty(change.FramesToRecognize);
        Assert.All(change.FramesToRecognize, item => Assert.False(item.IsIncremental));
    }

    private static CaptureFrame CreateFrame(byte value)
    {
        const int width = 160;
        const int height = 90;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }
}
