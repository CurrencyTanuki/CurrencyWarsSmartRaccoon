using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using System.Text.RegularExpressions;

namespace CurrencyWarsAssistant.Tasks;

public enum Phase2RunCompletionPageKind
{
    Unknown,
    FailureSettlementTransition,
    FinalFailure,
    NodeSuccessAnimation,
    NodeSuccessDetails,
    FinalSuccess
}

public static class Phase2RunCompletionDetector
{
    private const double CompletionPageMinimumConfidence = 0.5;
    private static readonly NormalizedRect RatingRegion =
        new(0.37, 0.40, 0.26, 0.28);

    private static string? ResolvePageId(
        ScreenshotAnalysisResult analysis,
        out double confidence)
    {
        if (analysis.Snapshot.PageId.Status == ObservationStatus.Known)
        {
            confidence = analysis.Snapshot.PageId.Confidence;
            return analysis.Snapshot.PageId.Value;
        }

        confidence = 0;
        return analysis.OperationalState?.PageId;
    }

    public static bool IsCompletedRunPage(
        ScreenshotAnalysisResult analysis,
        string? trackedNodeId) =>
        Classify(analysis, trackedNodeId) is
            Phase2RunCompletionPageKind.FinalFailure or
            Phase2RunCompletionPageKind.FinalSuccess;

    public static Phase2RunCompletionPageKind Classify(
        ScreenshotAnalysisResult analysis,
        string? trackedNodeId)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var pageId = ResolvePageId(analysis, out var confidence);
        if (confidence < CompletionPageMinimumConfidence)
        {
            return Phase2RunCompletionPageKind.Unknown;
        }

        if (string.Equals(pageId, "challenge_failed", StringComparison.Ordinal))
        {
            // “挑战失败 / 下一步 / 对局评价”是整局失败的最终页。
            // 玩家可在仍有正数生命值时主动结算（保存并退出），此时页面
            // 往往没有完整的结算语义摘要——只要页面本身可靠识别为
            // challenge_failed（已过置信度门槛）就判定整局失败结束。
            // challenge_failed 是专属终局页，不易与节点结算帧混淆。
            return Phase2RunCompletionPageKind.FinalFailure;
        }

        if (string.Equals(
                pageId,
                "challenge_health_depleted",
                StringComparison.Ordinal))
        {
            if (HasNodeSettlementContent(analysis))
            {
                // The title anchor is shared with some normal settlement
                // frames. A populated reward/damage summary is stronger
                // evidence that this is a continuing node result.
                return Phase2RunCompletionPageKind.NodeSuccessDetails;
            }

            // “挑战结束 / 前往结算”只是失败侧结算流程页。最终失败页还会
            // 随后出现；在此归档会造成重复归档并丢失最终评级。
            return Phase2RunCompletionPageKind.FailureSettlementTransition;
        }

        if (!string.Equals(pageId, "challenge_success", StringComparison.Ordinal))
        {
            return Phase2RunCompletionPageKind.Unknown;
        }

        var currentPageNode = ResolveCurrentPageNode(analysis);
        if (HasNodeSettlementContent(analysis))
        {
            return Phase2RunCompletionPageKind.NodeSuccessDetails;
        }

        if (currentPageNode is not null)
        {
            // 节点结算动画会显示当前节点。即使当前节点是 3-7，也不能仅凭
            // trackedNodeId 把“点击空白加速”动画误当成整局评级页。
            return Phase2RunCompletionPageKind.NodeSuccessAnimation;
        }

        return HasSemanticSettlementEvidence(analysis) &&
               string.Equals(
                   trackedNodeId,
                   "3-7",
                   StringComparison.OrdinalIgnoreCase)
            ? Phase2RunCompletionPageKind.FinalSuccess
            : Phase2RunCompletionPageKind.NodeSuccessAnimation;
    }

    public static bool IsFailedRunPage(ScreenshotAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return Classify(analysis, trackedNodeId: null) ==
               Phase2RunCompletionPageKind.FinalFailure;
    }

    public static bool IsFailureSettlementTransitionPage(
        ScreenshotAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return Classify(analysis, trackedNodeId: null) ==
               Phase2RunCompletionPageKind.FailureSettlementTransition;
    }

    public static bool IsHealthDepletedRunPage(ScreenshotAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        // Compatibility alias retained for callers that still use the former
        // page name. It identifies the red failure transition, not a completed
        // run and not proof that the final failure page has zero health.
        return IsFailureSettlementTransitionPage(analysis);
    }

    private static string? ResolveCurrentPageNode(
        ScreenshotAnalysisResult analysis)
    {
        var operationalNode = analysis.OperationalState?.NodeId;
        if (operationalNode?.Status == ObservationStatus.Known &&
            IsNodeId(operationalNode.Value))
        {
            return operationalNode.Value;
        }

        return analysis.Snapshot.Stage.Status == ObservationStatus.Known &&
               IsNodeId(analysis.Snapshot.Stage.Value)
            ? analysis.Snapshot.Stage.Value
            : null;
    }

    private static bool HasNodeSettlementContent(
        ScreenshotAnalysisResult analysis)
    {
        var state = analysis.OperationalState;
        return state is not null &&
               (state.SettlementGoldReward.Status == ObservationStatus.Known ||
                state.SettlementScreenDamageCandidate.Status == ObservationStatus.Known ||
                state.SettlementDamage.Status == ObservationStatus.Known ||
                (state.SettlementDamage.Value?.Count ?? 0) > 0);
    }

    private static bool HasSemanticSettlementEvidence(
        ScreenshotAnalysisResult analysis) =>
        analysis.Snapshot.PageId.Evidence.Any(item => string.Equals(
            item.Locator,
            "ocr:settlement-semantic-layout",
            StringComparison.Ordinal));

    private static bool IsNodeId(string? value) =>
        value is { Length: 3 } &&
        value[0] is >= '1' and <= '3' &&
        value[1] == '-' &&
        value[2] is >= '1' and <= '9';

    public static async Task<string?> ReadRatingAsync(
        CaptureFrame frame,
        IOfflineOcr ocr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(ocr);
        if (!ocr.IsAvailable)
        {
            return null;
        }

        var region = RatingRegion.ToPixels(frame.Width, frame.Height);
        var result = ocr is IAdaptiveOfflineOcr adaptive
            ? await adaptive.RecognizeRobustAsync(frame, region, cancellationToken)
                .ConfigureAwait(false)
            : await ocr.RecognizeAsync(frame, region, cancellationToken)
                .ConfigureAwait(false);
        return ParseRating(result.Lines.Prepend(result.Text));
    }

    internal static string? ParseRating(IEnumerable<string> texts)
    {
        foreach (var text in texts.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var compact = Regex.Replace(text.ToUpperInvariant(), "[^A-Z]", string.Empty);
            var match = Regex.Match(compact, "(?:SSS|SS|S|A|B|C)", RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }
}
