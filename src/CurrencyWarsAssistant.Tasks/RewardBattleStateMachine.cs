using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

public enum AutoBattleVisualState
{
    Unknown,
    Disabled,
    Enabled
}

public readonly record struct RewardAutoBattleDecision(
    AutoBattleVisualState Consensus,
    bool ConfirmedEnabled,
    bool ShouldPressToggle,
    int EnabledVotes,
    int DisabledVotes);

public static class RewardAutoBattlePolicy
{
    public static RewardAutoBattleDecision Observe(
        bool previouslyConfirmedEnabled,
        IEnumerable<AutoBattleVisualState> observations)
    {
        var recent = observations.TakeLast(3).ToArray();
        var enabledVotes = recent.Count(item =>
            item == AutoBattleVisualState.Enabled);
        var disabledVotes = recent.Count(item =>
            item == AutoBattleVisualState.Disabled);
        var consensus = enabledVotes >= 2
            ? AutoBattleVisualState.Enabled
            : disabledVotes >= 2
                ? AutoBattleVisualState.Disabled
                : AutoBattleVisualState.Unknown;
        var confirmedEnabled = previouslyConfirmedEnabled ||
            consensus == AutoBattleVisualState.Enabled;
        return new RewardAutoBattleDecision(
            consensus,
            confirmedEnabled,
            !confirmedEnabled &&
                recent.Length >= 3 &&
                consensus == AutoBattleVisualState.Disabled,
            enabledVotes,
            disabledVotes);
    }
}

public static class RewardBattleTimingPolicy
{
    public static readonly TimeSpan DefaultBattleBudget =
        TimeSpan.FromMinutes(3);
    public static readonly TimeSpan OverheatedBattleBudget =
        TimeSpan.FromMinutes(5);

    public static TimeSpan SelectBattleBudget(
        string? selectedInvestmentEnvironmentId) =>
        selectedInvestmentEnvironmentId is
            "investment_environment_060" or
            "investment_environment_061"
                ? OverheatedBattleBudget
                : DefaultBattleBudget;
}
public enum RewardBattlePageState
{
    Preparation,
    Battle,
    Success,
    ExpectedPostBattle,
    Unknown
}

public enum RewardBattleFlowState
{
    AwaitingPage,
    Preparation,
    StartingBattle,
    Battle,
    Success,
    ContinuingAfterSuccess,
    ExpectedPostBattle
}

internal enum RewardBattleWaitResult
{
    Completed,
    Failed,
    TimedOut
}

internal enum RewardBattleTimeoutHandlingResult
{
    Completed,
    RecoveredToHome,
    Blocked,
    Failed
}

public sealed class RewardBattleStartOwnershipTracker
{
    private bool _successfulInputAwaitingBattle;

    public bool IsAuthorized { get; private set; }

    public void MarkSuccessfulStartInput() =>
        _successfulInputAwaitingBattle = true;

    public bool Observe(RewardBattleFlowState state)
    {
        if (state == RewardBattleFlowState.Preparation)
        {
            _successfulInputAwaitingBattle = false;
            return false;
        }

        if (state != RewardBattleFlowState.Battle ||
            !_successfulInputAwaitingBattle ||
            IsAuthorized)
        {
            return false;
        }

        _successfulInputAwaitingBattle = false;
        IsAuthorized = true;
        return true;
    }
}

public sealed record RewardBattleObservation(
    bool Allowed,
    RewardBattlePageState Observation,
    RewardBattleFlowState State,
    string? PageId,
    bool UsedContextualBattleEvidence,
    double BattleConfidence,
    double EvidenceLead,
    string Message);

public sealed class RewardBattleStateMachine(
    string preparationPageId,
    string expectedPostBattlePageId)
{
    public const double MinimumBattleConfidence = 0.74;
    public const double MinimumEvidenceLead = 0.15;

    public RewardBattleFlowState State { get; private set; } =
        RewardBattleFlowState.AwaitingPage;

    public RewardBattleObservation Observe(
        string? classifiedPageId,
        IReadOnlyList<PageAnchorDiagnostic> diagnostics)
    {
        var pageId = classifiedPageId;
        var usedContext = false;
        var battleConfidence = 0d;
        var evidenceLead = 0d;
        if (pageId is null &&
            State is RewardBattleFlowState.StartingBattle or
                RewardBattleFlowState.Battle)
        {
            battleConfidence = diagnostics
                .Where(item => string.Equals(
                    item.PageId,
                    "reward_battle",
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Confidence)
                .DefaultIfEmpty(0)
                .Max();
            var otherConfidence = diagnostics
                .Where(item => !string.Equals(
                    item.PageId,
                    "reward_battle",
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Confidence)
                .DefaultIfEmpty(0)
                .Max();
            evidenceLead = battleConfidence - otherConfidence;
            usedContext =
                battleConfidence >= MinimumBattleConfidence &&
                evidenceLead >= MinimumEvidenceLead;
            if (usedContext)
            {
                pageId = "reward_battle";
            }
        }

        var observation = RewardBattlePageStateClassifier.Classify(
            pageId,
            preparationPageId,
            expectedPostBattlePageId);
        if (State == RewardBattleFlowState.Battle &&
            observation == RewardBattlePageState.Preparation)
        {
            observation = RewardBattlePageState.Unknown;
        }

        if (observation == RewardBattlePageState.Unknown)
        {
            var allowed = pageId is null ||
                State == RewardBattleFlowState.Battle;
            return new RewardBattleObservation(
                allowed,
                observation,
                State,
                pageId,
                usedContext,
                battleConfidence,
                evidenceLead,
                allowed
                    ? State == RewardBattleFlowState.Battle
                        ? "战斗上下文中的终结技/过场帧不参与未知页恢复；保持 Battle。"
                        : $"动画帧未形成页面结论；保持迁移状态 {State}。"
                    : $"当前迁移状态 {State} 不允许跳到已识别页面 {pageId}。");
        }

        var next = observation switch
        {
            RewardBattlePageState.Preparation => RewardBattleFlowState.Preparation,
            RewardBattlePageState.Battle => RewardBattleFlowState.Battle,
            RewardBattlePageState.Success => RewardBattleFlowState.Success,
            RewardBattlePageState.ExpectedPostBattle =>
                RewardBattleFlowState.ExpectedPostBattle,
            _ => State
        };
        var previous = State;
        var allowedTransition = State switch
        {
            RewardBattleFlowState.AwaitingPage => true,
            RewardBattleFlowState.Preparation =>
                next is RewardBattleFlowState.Preparation or
                    RewardBattleFlowState.Battle or
                    RewardBattleFlowState.Success or
                    RewardBattleFlowState.ExpectedPostBattle,
            RewardBattleFlowState.StartingBattle =>
                next is RewardBattleFlowState.Preparation or
                    RewardBattleFlowState.Battle or
                    RewardBattleFlowState.Success or
                    RewardBattleFlowState.ExpectedPostBattle,
            RewardBattleFlowState.Battle =>
                next is RewardBattleFlowState.Battle or
                    RewardBattleFlowState.Success or
                    RewardBattleFlowState.ExpectedPostBattle,
            RewardBattleFlowState.Success =>
                next is RewardBattleFlowState.Success or
                    RewardBattleFlowState.ExpectedPostBattle,
            RewardBattleFlowState.ContinuingAfterSuccess =>
                next is RewardBattleFlowState.Success or
                    RewardBattleFlowState.ExpectedPostBattle,
            RewardBattleFlowState.ExpectedPostBattle =>
                next == RewardBattleFlowState.ExpectedPostBattle,
            _ => false
        };
        return new RewardBattleObservation(
            allowedTransition,
            observation,
            allowedTransition ? next : State,
            pageId,
            usedContext,
            battleConfidence,
            evidenceLead,
            allowedTransition
                ? $"{previous} -> {next}"
                : $"不允许从 {previous} 跳到 {next}（页面 {pageId}）。");
    }

    public bool Apply(RewardBattleObservation observation)
    {
        if (!observation.Allowed)
        {
            return false;
        }

        State = observation.State;
        return true;
    }

    public bool TryStartBattle()
    {
        if (State != RewardBattleFlowState.Preparation)
        {
            return false;
        }

        State = RewardBattleFlowState.StartingBattle;
        return true;
    }

    public bool TryContinueChallenge()
    {
        if (State != RewardBattleFlowState.Success)
        {
            return false;
        }

        State = RewardBattleFlowState.ContinuingAfterSuccess;
        return true;
    }
}

public static class RewardBattlePageStateClassifier
{
    public static RewardBattlePageState Classify(
        string? pageId,
        string preparationPageId,
        string expectedPostBattlePageId)
    {
        if (string.Equals(
                pageId,
                preparationPageId,
                StringComparison.OrdinalIgnoreCase))
        {
            return RewardBattlePageState.Preparation;
        }

        if (string.Equals(
                pageId,
                "reward_battle",
                StringComparison.OrdinalIgnoreCase) ||
            // battle_generic 是战斗兜底页（伤害页签/暂停键任一命中即识别），
            // 也属于战斗状态——实机战斗页常识别为 battle_generic。
            string.Equals(
                pageId,
                "battle_generic",
                StringComparison.OrdinalIgnoreCase))
        {
            return RewardBattlePageState.Battle;
        }

        if (string.Equals(
                pageId,
                "challenge_success",
                StringComparison.OrdinalIgnoreCase))
        {
            return RewardBattlePageState.Success;
        }

        return string.Equals(
            pageId,
            expectedPostBattlePageId,
            StringComparison.OrdinalIgnoreCase)
                ? RewardBattlePageState.ExpectedPostBattle
                : RewardBattlePageState.Unknown;
    }
}
