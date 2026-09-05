using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 1.2.96 识别流冻结诊断聚合器单测：成功/失败计数、连续失败、间隔聚合窗口。
/// </summary>
public sealed class CaptureLoopStatsTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptySnapshotHasZeroes()
    {
        var snapshot = new CaptureLoopStats().Snapshot();

        Assert.Equal(0, snapshot.Successes);
        Assert.Equal(0, snapshot.Failures);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.LastSuccessAt);
        Assert.Equal(0, snapshot.MaxIntervalMs);
    }

    [Fact]
    public void FirstSuccessRecordsNoInterval()
    {
        var stats = new CaptureLoopStats();

        stats.RecordSuccess(Base);
        var snapshot = stats.Snapshot();

        Assert.Equal(1, snapshot.Successes);
        Assert.Equal(Base, snapshot.LastSuccessAt);
        Assert.Equal(0, snapshot.LastIntervalMs);
        Assert.Equal(0, snapshot.MaxIntervalMs);
    }

    [Fact]
    public void IntervalAggregationTracksMinMaxAverage()
    {
        var stats = new CaptureLoopStats();
        stats.RecordSuccess(Base);
        stats.RecordSuccess(Base.AddMilliseconds(100));
        stats.RecordSuccess(Base.AddMilliseconds(400)); // 间隔 300ms

        var snapshot = stats.Snapshot();

        Assert.Equal(3, snapshot.Successes);
        Assert.Equal(300, snapshot.LastIntervalMs);
        Assert.Equal(100, snapshot.MinIntervalMs);
        Assert.Equal(300, snapshot.MaxIntervalMs);
        Assert.Equal(200, snapshot.AverageIntervalMs);
    }

    [Fact]
    public void ConsecutiveFailuresGrowAndResetOnSuccess()
    {
        var stats = new CaptureLoopStats();
        stats.RecordSuccess(Base);

        stats.RecordFailure();
        stats.RecordFailure();
        Assert.Equal(2, stats.Snapshot().ConsecutiveFailures);

        stats.RecordSuccess(Base.AddSeconds(1));
        var snapshot = stats.Snapshot();
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(2, snapshot.Failures);
    }

    [Fact]
    public void IntervalWindowKeepsOnlyMostRecent32()
    {
        var stats = new CaptureLoopStats();
        stats.RecordSuccess(Base);
        // 40 次成功=39 个间隔，前 7 个（100ms 档）应被 32 窗口挤出。
        foreach (var i in Enumerable.Range(1, 40))
        {
            stats.RecordSuccess(Base.AddMilliseconds(i * (i <= 7 ? 100 : 500)));
        }

        var snapshot = stats.Snapshot();

        Assert.Equal(41, snapshot.Successes);
        Assert.Equal(500, snapshot.MinIntervalMs);
        Assert.Equal(500, snapshot.LastIntervalMs);
    }
}
