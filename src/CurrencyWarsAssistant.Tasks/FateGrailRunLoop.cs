using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」整局刷取循环（隔离新增，纯编排层）。
/// <para>
/// 每局流程：
/// <list type="number">
///   <item>调用注入的 <c>openingLoop</c>（<see cref="OpeningRerollLoopCoordinator"/> 或同签名的 fake）
///     刷开局：只接受 067/018/019 三个可推进环境，完成后打 1-1/1-2 奖励关并在投资策略页选完偏好策略。</item>
///   <item>进入 1-3 后，用 <see cref="FateGrailRunCoordinator"/> 驱动三星五费决策（快照由调用方注入
///     readSnapshot 组装——识别投资环境/血量/金币/阵容/祈愿试炼，见 <see cref="FateGrailSnapshotAssembler"/>）。</item>
///   <item>决策 Outcome：Achieved=本局成功收工；RerollRequested / Stopped / Exhausted=重开一局。</item>
/// </list>
/// <paramref name="maxRounds"/> 是本循环的整局安全阀（防"永远刷不到"无限挂机）。
/// </para>
/// </summary>
public sealed class FateGrailRunLoop
{
    /// <summary>开局重刷循环器（生产=OpeningRerollLoopCoordinator；测试=fake）。</summary>
    public delegate Task<OpeningRerollLoopResult> OpeningLoopRunner(
        OpeningFilterSet filters,
        OpeningRerollLoopOptions options,
        CancellationToken cancellationToken);

    /// <summary>每局滚动录屏的生命周期（成功保留 / 失败删除；下一局重新 Start）。</summary>
    public interface IRoundRecorder
    {
        /// <summary>开始录制本局（临时文件）。</summary>
        Task StartAsync(string roundId, CancellationToken cancellationToken);

        /// <summary>结束本局录制：success=true 保留到 outputDirectory；false 丢弃临时文件。</summary>
        Task FinishAsync(bool success, string? outputDirectory, CancellationToken cancellationToken);
    }

    private readonly OpeningLoopRunner _openingLoop;
    private readonly FateGrailRunCoordinator _coordinator;

    public FateGrailRunLoop(
        OpeningLoopRunner openingLoop,
        FateGrailRunCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(openingLoop);
        ArgumentNullException.ThrowIfNull(coordinator);
        _openingLoop = openingLoop;
        _coordinator = coordinator;
    }

    /// <summary>每局滚动录屏（null=不录屏）。init 属性以便工厂在构造后以 with 装配。</summary>
    public IRoundRecorder? RoundRecorder { get; init; }

    /// <summary>滚动录屏保留目录（由调用方配置；null=不保留成功录像）。</summary>
    public string? RecordingOutputDirectory { get; init; }

    /// <summary>单局结果。</summary>
    public enum RoundOutcome
    {
        Achieved,
        Reopened,
    }

    /// <summary>整局刷取结果。</summary>
    public sealed record Result(
        RoundOutcome Outcome,
        int RoundsPlayed,
        string? AchievedNode,
        string? AchievedMessage)
    {
        public bool Succeeded => Outcome == RoundOutcome.Achieved;
    }

    /// <summary>
    /// 运行整局刷取，直到达成或达到 <paramref name="maxRounds"/> 局上限。
    /// </summary>
    /// <param name="filters">开局过滤器：只接受 067/018/019 可推进环境（三星五费 D1）。</param>
    /// <param name="openingOptions">开局循环选项（奖励关完成、偏好策略注入等）。</param>
    /// <param name="maxRounds">整局轮数上限（安全阀）。</param>
    public async Task<Result> RunAsync(
        OpeningFilterSet filters,
        OpeningRerollLoopOptions openingOptions,
        FateGrailRunEngine.UserGoal goal = FateGrailRunEngine.UserGoal.AnyOne,
        int maxRounds = 20,
        CancellationToken cancellationToken = default)
    {
        if (maxRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRounds), "整局轮数上限必须大于零。");
        }

        for (var round = 1; round <= maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ① 本局起录屏（滚动：每局一个临时文件，结束按结果保留/丢弃）。
            if (RoundRecorder is not null)
            {
                try { await RoundRecorder.StartAsync($"round{round}", cancellationToken); }
                catch (Exception) { /* 录屏失败不影响刷局主流程 */ }
            }

            var roundSucceeded = false;
            try
            {
                // 刷开局（环境过滤 + 1-1/1-2 奖励关 + 选策略）。
                var opening = await _openingLoop(
                    filters,
                    openingOptions,
                    cancellationToken);
                if (!opening.Succeeded)
                {
                    // 本局开局未满足（环境不合/奖励关失败/超时），循环器内部已负责重刷；
                    // 此处仅作进度记录，继续下一轮由 openingLoop 再次刷开局。
                    continue;
                }

                // 进入 1-3：决策驱动（识别→Step→执行→Outcome）。goal 仅作日志/回传；
                // 真正的目标以快照 Snapshot.Goal 为准（readSnapshot 组装时写目标）。
                var outcome = await _coordinator.RunAsync(
                    goal: goal,
                    maxDecisions: 200,
                    cancellationToken);

                switch (outcome)
                {
                    case FateGrailRunCoordinator.Outcome.Achieved achieved:
                        roundSucceeded = true;
                        return new Result(
                            RoundOutcome.Achieved,
                            round,
                            achieved.Node,
                            achieved.Message);

                    default:
                        // RerollRequested / Stopped / Exhausted：本局未达成，重开一局。
                        break;
                }
            }
            finally
            {
                // 本局结束：成功则保留录像，否则丢弃临时文件（滚动录屏核心语义）。
                // 注意：必须用 CancellationToken.None——即使主流程已取消，也要把 ffmpeg
                // 正常收尾并删/保临时文件，不能带着已取消 token 进 FinishAsync（会抛 OCE 跳过收尾）。
                if (RoundRecorder is not null)
                {
                    try
                    {
                        await RoundRecorder.FinishAsync(
                            roundSucceeded,
                            roundSucceeded ? RecordingOutputDirectory : null,
                            CancellationToken.None);
                    }
                    catch (Exception) { /* 录屏清理失败不影响主流程 */ }
                }
            }

            if (roundSucceeded)
            {
                break;
            }
        }

        return new Result(
            RoundOutcome.Reopened,
            maxRounds,
            null,
            $"已刷取 {maxRounds} 局仍未达成三星五费（安全阀）。");
    }
}