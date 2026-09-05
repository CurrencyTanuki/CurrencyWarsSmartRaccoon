namespace CurrencyWarsAssistant.Tasks;

/// <summary>截图循环诊断快照（只读）。</summary>
public sealed record CaptureLoopStatsSnapshot(
    long Successes,
    long Failures,
    long ConsecutiveFailures,
    DateTimeOffset? LastSuccessAt,
    double LastIntervalMs,
    double MinIntervalMs,
    double AverageIntervalMs,
    double MaxIntervalMs)
{
    public static CaptureLoopStatsSnapshot Empty { get; } =
        new(0, 0, 0, null, 0, 0, 0, 0);
}

/// <summary>
/// 1.2.96 识别流周期冻结根因诊断（纯观测，B.4 立项）：截图循环（约 100ms 一帧）的
/// 成功/失败计数与最近成功间隔聚合（最近 32 次 min/avg/max）。与捕获层
/// IGameCapture.StreamStats 对照可区分病理：渐慢（间隔逐次抬升）、断崖
/// （ConsecutiveFailures 突增）、重启后即死（新会话计数低且失败快速增长）、
/// 独立死亡（长稳后突停）。实例随识别会话内每次管线新建而重建=天然按会话归零。
/// </summary>
internal sealed class CaptureLoopStats
{
    private const int IntervalWindowSize = 32;
    private readonly object gate = new();
    private readonly Queue<double> intervalsMs = new(IntervalWindowSize);
    private long successes;
    private long failures;
    private long consecutiveFailures;
    private DateTimeOffset? lastSuccessAt;
    private double lastIntervalMs;

    public void RecordSuccess(DateTimeOffset at)
    {
        lock (gate)
        {
            successes++;
            if (lastSuccessAt is { } previous)
            {
                var interval = (at - previous).TotalMilliseconds;
                lastIntervalMs = interval;
                intervalsMs.Enqueue(interval);
                while (intervalsMs.Count > IntervalWindowSize)
                {
                    intervalsMs.Dequeue();
                }
            }

            lastSuccessAt = at;
            consecutiveFailures = 0;
        }
    }

    public void RecordFailure()
    {
        lock (gate)
        {
            failures++;
            consecutiveFailures++;
        }
    }

    public CaptureLoopStatsSnapshot Snapshot()
    {
        lock (gate)
        {
            double min = 0;
            double max = 0;
            double avg = 0;
            if (intervalsMs.Count > 0)
            {
                min = double.MaxValue;
                foreach (var interval in intervalsMs)
                {
                    if (interval < min)
                    {
                        min = interval;
                    }

                    if (interval > max)
                    {
                        max = interval;
                    }

                    avg += interval;
                }

                avg /= intervalsMs.Count;
            }

            return new CaptureLoopStatsSnapshot(
                successes,
                failures,
                consecutiveFailures,
                lastSuccessAt,
                lastIntervalMs,
                min,
                avg,
                max);
        }
    }
}
