using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

internal sealed record Phase2SettlementSemanticResult(
    string PageId,
    double Confidence,
    IReadOnlyList<string> Evidence);

internal static class Phase2SettlementSemanticClassifier
{
    public static async Task<Phase2SettlementSemanticResult?> TryClassifyAsync(
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

        var regions = new[]
        {
            Phase2RecognitionRegions.SettlementTitle,
            Phase2RecognitionRegions.SettlementSemanticBody,
            Phase2RecognitionRegions.SettlementAction
        };
        var recognized = await Task.WhenAll(regions.Select(region =>
            ReadAsync(frame, region, ocr, cancellationToken))).ConfigureAwait(false);
        var title = Compact(recognized[0]);
        var body = Compact(recognized[1]);
        var action = Compact(recognized[2]);
        var all = title + body + action;
        var evidence = recognized
            .SelectMany(item => item)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var normalizedTitle = title
            .Replace("挑找", "挑战", StringComparison.Ordinal)
            .Replace("挑站", "挑战", StringComparison.Ordinal);
        var hasSuccessTitle =
            normalizedTitle.Contains("挑战成功", StringComparison.Ordinal) ||
            normalizedTitle.Contains("挑战成", StringComparison.Ordinal) &&
            !normalizedTitle.Contains("失败", StringComparison.Ordinal);
        var hasHealth =
            all.Contains("小队生命值", StringComparison.Ordinal) ||
            all.Contains("小队命值", StringComparison.Ordinal);
        var hasEvaluation = body.Contains("对局评价", StringComparison.Ordinal);
        var hasProgress = body.Contains("挑战进度", StringComparison.Ordinal);
        var hasSettlementDetails =
            body.Contains("获得金币总览", StringComparison.Ordinal) ||
            body.Contains("数据统计", StringComparison.Ordinal) ||
            body.Contains("基础奖励", StringComparison.Ordinal);

        if (title.Contains("挑战失败", StringComparison.Ordinal) &&
            action.Contains("下一步", StringComparison.Ordinal) &&
            (hasEvaluation || hasHealth))
        {
            return new Phase2SettlementSemanticResult(
                "challenge_failed",
                hasEvaluation && hasHealth ? 0.96 : 0.90,
                evidence);
        }

        if (title.Contains("挑战结束", StringComparison.Ordinal) &&
            action.Contains("前往结算", StringComparison.Ordinal) &&
            (hasProgress || hasHealth || ContainsNode(body)))
        {
            return new Phase2SettlementSemanticResult(
                "challenge_health_depleted",
                hasProgress ? 0.95 : 0.89,
                evidence);
        }

        if (hasSuccessTitle &&
            action.Contains("继续挑战", StringComparison.Ordinal) &&
            (hasSettlementDetails || hasHealth))
        {
            return new Phase2SettlementSemanticResult(
                "challenge_success",
                hasSettlementDetails && hasHealth ? 0.96 : 0.90,
                evidence);
        }

        if (hasSuccessTitle &&
            action.Contains("下一步", StringComparison.Ordinal) &&
            (hasEvaluation || hasHealth))
        {
            return new Phase2SettlementSemanticResult(
                "challenge_success",
                hasEvaluation && hasHealth ? 0.96 : 0.90,
                evidence);
        }

        if (hasSuccessTitle &&
            action.Contains("点击空白加速", StringComparison.Ordinal) &&
            (hasProgress || hasHealth || ContainsNode(body)))
        {
            return new Phase2SettlementSemanticResult(
                "challenge_success",
                hasProgress || hasHealth ? 0.94 : 0.88,
                evidence);
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadAsync(
        CaptureFrame frame,
        CurrencyWarsAssistant.Core.NormalizedRect normalized,
        IOfflineOcr ocr,
        CancellationToken cancellationToken)
    {
        var region = normalized.ToPixels(frame.Width, frame.Height);
        var result = ocr is IAdaptiveOfflineOcr adaptive
            ? await adaptive.RecognizeRobustAsync(frame, region, cancellationToken)
                .ConfigureAwait(false)
            : await ocr.RecognizeAsync(frame, region, cancellationToken)
                .ConfigureAwait(false);
        return result.Lines
            .Prepend(result.Text)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string Compact(IEnumerable<string> values) =>
        string.Concat(values).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool ContainsNode(string text)
    {
        for (var index = 0; index + 2 < text.Length; index++)
        {
            if (text[index] is >= '1' and <= '3' &&
                text[index + 1] == '-' &&
                text[index + 2] is >= '1' and <= '9')
            {
                return true;
            }
        }

        return false;
    }
}
