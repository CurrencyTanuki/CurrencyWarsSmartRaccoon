using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.App;

public sealed class IncompleteRunViewModel(
    RunCheckpointSummary summary)
{
    public RunCheckpointSummary Summary { get; } = summary;
    public string RunId => Summary.Checkpoint.RunId;
    public string StartedAtDisplay =>
        Summary.Checkpoint.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public string LastSavedDisplay =>
        Summary.Checkpoint.LastSavedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string LastNodeDisplay =>
        string.IsNullOrWhiteSpace(Summary.Checkpoint.LastConfirmedNodeId)
            ? "节点未确认"
            : Summary.Checkpoint.LastConfirmedNodeId;
    public string CompletenessDisplay =>
        $"{Summary.Checkpoint.DataCompleteness.Ratio:P0} · " +
        $"{Summary.Checkpoint.DataCompleteness.RecordedNodeCount} 个最终节点";
    public string RecoveryDisplay => Summary.Health switch
    {
        RunCheckpointHealth.Healthy => "断点正常",
        RunCheckpointHealth.RecoveredFromBackup => "已从备份恢复",
        RunCheckpointHealth.PartiallyRecovered => "可部分恢复",
        RunCheckpointHealth.SynthesizedFromArtifacts => "旧记录，可续录",
        _ => "状态未知"
    };
}

public sealed class RunResumeRequestedEventArgs(
    RunCheckpointSummary checkpoint) : EventArgs
{
    public RunCheckpointSummary Checkpoint { get; } = checkpoint;
}
