using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」决策主链的上层驱动循环（协调器，隔离新增，不入主识别管线）。
/// <para>
/// <see cref="FateGrailRunEngine.Step"/> 是纯决策单步；本协调器把它接到外界：
/// 循环「读快照 → <see cref="FateGrailRunEngine.Step"/> → 按 <see cref="FateGrailRunEngine.Action"/>
/// 分发到 <see cref="IActionExecutor"/> 执行 → 处理 IsTerminal（Reroll 重刷 / Achieved 交人工）」。
/// 快照读取与动作执行通过 <see cref="IActionExecutor"/> 注入：
/// 测试用 fake 记录动作序列；生产用真实 <see cref="RewardStageAutomationController"/> 点屏
/// + 现有识别（环境/HP/金币/试炼文本）组装。从而<b>协调器本身可脱离真实游戏全量单测</b>。
/// </para>
/// 注意分层：识别正确性（尤其 <see cref="FateGrailRunEngine.Snapshot.HasBody5Cost"/> 5费本体在池）
/// 属执行器/识别层，需实机标定；<see cref="FateGrailRunEngine.Snapshot.HasEightProjectors"/> 已废弃
/// （用户 2026-08-25：选「奇迹代偿」即保证 8 完美投影仪，无需识别确认）。
/// 本协调器只负责驱动与状态推进，不臆造识别结果。
/// </summary>
public sealed class FateGrailRunCoordinator
{
    private static readonly IReadOnlySet<FateGrailRunEngine.Action> TerminalOnDone =
        new HashSet<FateGrailRunEngine.Action> { FateGrailRunEngine.Action.Achieved };

    private readonly Func<CancellationToken, Task<FateGrailRunEngine.Snapshot?>> _readSnapshot;
    private readonly IActionExecutor _executor;

    /// <summary>
    /// 构造协调器。
    /// <paramref name="readSnapshot"/> 每次循环读取当前局快照（识别结果）；返回 null 表示"无快照（暂不可识别）"，驱动将短退重试。
    /// <paramref name="executor"/> 负责按 <see cref="FateGrailRunEngine.Action"/> 执行真实点屏动作（并更新外部状态）。
    /// 若 <paramref name="executor"/> 为 null，则任何非 <see cref="FateGrailRunEngine.Action.None"/> 动作都会抛
    /// <see cref="InvalidOperationException"/>（防止"无执行器却声称跑了"），如仅想跑决策可传 null 但动作应全为 None。
    /// </summary>
    public FateGrailRunCoordinator(
        Func<CancellationToken, Task<FateGrailRunEngine.Snapshot?>> readSnapshot,
        IActionExecutor? executor = null)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        _readSnapshot = readSnapshot;
        _executor = executor ?? NoOpExecutor.Instance;
    }

    /// <summary>动作执行器（隔离边界）：把决策动作映射到真实点屏；测试用 fake。</summary>
    public interface IActionExecutor
    {
        /// <summary>执行单个决策动作；<paramref name="stepNode"/> 为当前决策树节点标签（用于日志/统计）。</summary>
        /// <param name="trialToChoose">祈愿试炼要选的侧（ChoosePassiveTrial/ChooseMiracleCompensation 时非空；其余为 null）。</param>
        /// <returns>true 表示动作已被处理（可继续下一步）；false 表示调用方需停止/重试。</returns>
        Task<bool> ExecuteAsync(
            FateGrailRunEngine.Action action,
            string stepNode,
            string message,
            FateGrailRunEngine.TrialSide? trialToChoose,
            CancellationToken cancellationToken);
    }

    /// <summary>无操作的执行器（仅当全部动作均为 None 时才安全，避免误跑）。</summary>
    internal sealed class NoOpExecutor : IActionExecutor
    {
        public static readonly NoOpExecutor Instance = new();
        public Task<bool> ExecuteAsync(
            FateGrailRunEngine.Action action,
            string stepNode,
            string message,
            FateGrailRunEngine.TrialSide? trialToChoose,
            CancellationToken cancellationToken)
        {
            if (action != FateGrailRunEngine.Action.None)
                throw new InvalidOperationException(
                    $"无执行器不可执行动作 {action}（决策节点 {stepNode}）。");
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// 运行三星五费决策驱动，直到达成（<see cref="FateGrailRunEngine.Action.Achieved"/>,交人工）或被取消。
    /// <para>单局决策驱动；<paramref name="goal"/> 仅作日志/回传，不作为决策源——引擎以快照自带的
    /// <see cref="FateGrailRunEngine.Snapshot.Goal"/> 为准（<paramref name="readSnapshot"/> 应把目标写进快照）。</para>
    /// <param name="maxDecisions">本局决策步数上限（安全阀，防"永远不收敛"）；达到则停止并返回未达成。
    /// 由调用方决定是否重开一局（<see cref="Outcome.RerollRequested"/> 供外层循环继续）。</param>
    /// </summary>
    public async Task<Outcome> RunAsync(
        FateGrailRunEngine.UserGoal goal,
        int maxDecisions = 200,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < maxDecisions; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _readSnapshot(cancellationToken);
            if (snapshot is null)
            {
                // 暂无可识别快照：短退重试（模拟"等待画面稳定"）。
                await Task.Delay(300, cancellationToken);
                continue;
            }

            var step = FateGrailRunEngine.Step(snapshot);
            if (step.RequiresReroll)
                return new Outcome.RerollRequested();

            // 评审修复（阻断1）：Done 步（如 N1 奇迹代偿）若还带实际动作（点试炼/扣血），
            // 必须先执行动作再收工；否则"点选奇迹代偿→8投影"永远不会发生，整局被虚报成功。
            // Achieved 动作（无限之釜/已达成终点）本身就是终态，无需执行。
            if (step.Action != FateGrailRunEngine.Action.None &&
                step.Action != FateGrailRunEngine.Action.Achieved)
            {
                var handled = await _executor.ExecuteAsync(
                    step.Action, step.Node, step.Message, step.TrialToChoose, cancellationToken);
                if (!handled)
                    return new Outcome.Stopped(step.Node, step.Message);
            }

            if (step.Done || TerminalOnDone.Contains(step.Action))
                return new Outcome.Achieved(step.Node, step.Message);

            // 前进：跳过无动作步。若执行器已推进外部状态，下一轮读到的快照会变化；
            // 本协调器不做内部状态机，完全依赖外部快照驱动（保持可单测、无隐藏状态）。
        }
        return new Outcome.Exhausted(maxDecisions);
    }

    /// <summary>驱动结果。</summary>
    public abstract record Outcome
    {
        /// <summary>已达成目标（交人工 / 收工）。</summary>
        public sealed record Achieved(string Node, string Message) : Outcome;
        /// <summary>引擎判定本局不可行，需要重刷开局。</summary>
        public sealed record RerollRequested() : Outcome;
        /// <summary>执行器返回未处理/中断（如需用户介入）。</summary>
        public sealed record Stopped(string Node, string Message) : Outcome;
        /// <summary>达成分步数上限仍未收敛（安全阀）。</summary>
        public sealed record Exhausted(int MaxDecisions) : Outcome;

        public static Achieved MakeAchieved(string node, string msg) => new(node, msg);
    }
}
