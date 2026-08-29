using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 红测 A（阶段 1 修封存）：奖励关/无战斗节点必须封存，且不得伪造战斗伤害。
///
/// 回放数据：run-20260814-143614 的 21 个真实 analysis 快照（实机 0.2.855
/// 落盘的 analyzer 输出，按 snapshot.asOf 捕获时间排序），逐帧喂给
/// Phase2OperationalStateTracker.Observe，收集 FinalizedBattle 节点序列。
///
/// 背景：实机 0.2.855 中 1-1（奖励关，秒杀无战斗页/无结算页）从未被封存，
/// 从节点历史消失；checkpoint finalizedNodeIds 只有 1-2/1-3/1-4。
/// 产品合同：奖励节点即使没有普通战斗页，也不能因此永久丢失。
/// </summary>
public sealed class Phase2RewardNodeFinalizationRedTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    private static readonly string FixtureDir = Path.Combine(
        RepositoryRoot,
        "tests",
        "CurrencyWarsAssistant.Tests",
        "Fixtures",
        "PageReplay",
        "run-20260814-143614");

    private sealed record ReplayFrame(
        DateTimeOffset AsOf,
        string AnalysisId,
        Phase2OperationalState State);

    private static List<ReplayFrame> LoadRunSequence()
    {
        var frames = new List<ReplayFrame>();
        foreach (var file in Directory.GetFiles(FixtureDir, "analysis-*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var asOf = root.GetProperty("snapshot")
                .GetProperty("asOf")
                .GetDateTimeOffset();
            var stateJson = root.GetProperty("operationalState").GetRawText();
            var state = JsonSerializer.Deserialize<Phase2OperationalState>(
                stateJson,
                AdvisorJson.Options)
                ?? throw new InvalidDataException(
                    $"unable to deserialize operationalState from {file}");
            frames.Add(new ReplayFrame(asOf, file, state));
        }

        Assert.True(
            frames.Count >= 20,
            $"expected >= 20 analysis frames in fixture, got {frames.Count}");
        return frames.OrderBy(frame => frame.AsOf).ToList();
    }

    private static List<string> ReplayAndCollectFinalized(
        IReadOnlyList<ReplayFrame> frames,
        out Dictionary<string, FinalNodeBattleState> finalBattles)
    {
        var tracker = new Phase2OperationalStateTracker();
        var finalizedNodes = new List<string>();
        finalBattles = new Dictionary<string, FinalNodeBattleState>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var frame in frames)
        {
            var update = tracker.Observe(frame.State);
            if (update.FinalizedBattle is { } finalized &&
                !string.IsNullOrWhiteSpace(finalized.NodeId) &&
                !finalizedNodes.Contains(finalized.NodeId))
            {
                finalizedNodes.Add(finalized.NodeId);
                finalBattles[finalized.NodeId] = finalized;
            }
        }

        return finalizedNodes;
    }

    /// <summary>
    /// 红测核心：1-1（奖励关）必须出现在封存节点序列中，且不伪造战斗伤害。
    /// 在 0.2.842 基线上此断言预期失败（红）——1-1 从未封存，从历史消失。
    /// </summary>
    [Fact]
    public void RewardNodeOneOneIsFinalizedWithoutFabricatedDamage()
    {
        var frames = LoadRunSequence();
        var finalizedNodes = ReplayAndCollectFinalized(
            frames,
            out var finalBattles);

        // 核心断言：1-1 不得从历史消失
        Assert.Contains("1-1", finalizedNodes);

        // 无伪造伤害：奖励关回放中没有可靠战斗伤害证据时，不得编造数值。
        var oneOne = finalBattles["1-1"];
        Assert.True(
            oneOne.SelectedDamage is null || !oneOne.CanDriveDecisions,
            "1-1 奖励关不得伪造战斗伤害（SelectedDamage 应为 null 或 CanDriveDecisions=false）");
        Assert.True(
            oneOne.IsComplete,
            "1-1 封存应标记 IsComplete（走奖励关必然过关规则）");
    }

    /// <summary>
    /// 回归对照：1-2（有真实结算证据的节点）的封存行为不得回退，
    /// 且封存顺序正确（1-1 先于 1-2）。
    /// 说明：该回放数据中 1-3/1-4/1-6 的落盘 analysis 全部是备战帧
    /// （实机积压导致其战斗/结算帧未落盘），没有可封存证据，不属于
    /// 本红测范围；其封存由实机运行与阶段 2 完整回放验证。
    /// </summary>
    [Fact]
    public void OrdinaryNodesStillFinalizeInOrder()
    {
        var frames = LoadRunSequence();
        var finalizedNodes = ReplayAndCollectFinalized(
            frames,
            out _);

        Assert.Contains("1-2", finalizedNodes);

        var i1 = finalizedNodes.IndexOf("1-1");
        var i2 = finalizedNodes.IndexOf("1-2");
        Assert.True(i1 >= 0 && i1 < i2,
            $"unexpected finalization order: [{string.Join(", ", finalizedNodes)}]");
    }
}
