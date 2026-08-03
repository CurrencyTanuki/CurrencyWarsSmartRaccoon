using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed record Phase2OcrNameEvidence(
    string ObjectId,
    string StandardName,
    IReadOnlyList<string> RawTexts,
    double Confidence);

public static class Phase2NamedContentEvidenceResolver
{
    public static Phase2NamedContentRecognition Resolve(
        Phase2NamedContentKind kind,
        string slotKey,
        RelativeRegion region,
        Phase2OcrNameEvidence? ocrEvidence,
        Phase2IconRecognition iconEvidence,
        EvidenceReference evidence,
        bool iconOnlyWithoutText = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotKey);
        ArgumentNullException.ThrowIfNull(iconEvidence);

        var candidates = (iconEvidence.CandidateTemplateIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ocrEvidence is not null)
        {
            if (iconEvidence.IsKnown &&
                !string.Equals(
                    ocrEvidence.ObjectId,
                    iconEvidence.TemplateId,
                    StringComparison.Ordinal))
            {
                return Conflict(
                    kind,
                    slotKey,
                    region,
                    ocrEvidence,
                    iconEvidence,
                    candidates,
                    evidence,
                    $"OCR={ocrEvidence.ObjectId} 与图标={iconEvidence.TemplateId} 冲突");
            }

            var iconSupportsOcr = iconEvidence.IsKnown ||
                                  candidates.Contains(
                                      ocrEvidence.ObjectId,
                                      StringComparer.Ordinal);
            var credibleContradiction =
                !iconEvidence.IsKnown &&
                iconEvidence.Confidence >= 0.62 &&
                candidates.Length > 0 &&
                !iconSupportsOcr;
            if (credibleContradiction)
            {
                return Conflict(
                    kind,
                    slotKey,
                    region,
                    ocrEvidence,
                    iconEvidence,
                    candidates,
                    evidence,
                    $"OCR={ocrEvidence.ObjectId} 不在图标候选集合中");
            }

            var evidenceKind = iconSupportsOcr
                ? Phase2RecognitionEvidenceKind.OcrAndIcon
                : Phase2RecognitionEvidenceKind.Ocr;
            var confidence = iconSupportsOcr
                ? Math.Clamp(
                    ocrEvidence.Confidence * 0.80 +
                    Math.Max(0, iconEvidence.Confidence) * 0.20,
                    0,
                    1)
                : ocrEvidence.Confidence;
            return new Phase2NamedContentRecognition(
                kind,
                slotKey,
                ObservationStatus.Known,
                ocrEvidence.ObjectId,
                ocrEvidence.StandardName,
                ocrEvidence.RawTexts,
                confidence,
                region,
                evidenceKind,
                candidates,
                [],
                evidence with
                {
                    Locator = $"named:{kind}:{slotKey}",
                    Summary = string.Join(" | ", ocrEvidence.RawTexts),
                    Confidence = confidence
                });
        }

        if (iconEvidence.IsKnown && iconEvidence.TemplateId is not null)
        {
            return new Phase2NamedContentRecognition(
                kind,
                slotKey,
                ObservationStatus.Known,
                iconEvidence.TemplateId,
                null,
                [],
                iconEvidence.Confidence,
                region,
                iconOnlyWithoutText
                    ? Phase2RecognitionEvidenceKind.IconOnlyWithoutText
                    : Phase2RecognitionEvidenceKind.Icon,
                candidates,
                [],
                evidence with
                {
                    Locator = $"vision:{kind}:{slotKey}",
                    Confidence = iconEvidence.Confidence
                });
        }

        return new Phase2NamedContentRecognition(
            kind,
            slotKey,
            ObservationStatus.Unknown,
            null,
            null,
            [],
            0,
            region,
            iconOnlyWithoutText
                ? Phase2RecognitionEvidenceKind.IconOnlyWithoutText
                : Phase2RecognitionEvidenceKind.Icon,
            candidates,
            [],
            evidence with
            {
                Locator = $"vision:{kind}:{slotKey}",
                Confidence = iconEvidence.Confidence
            });
    }

    private static Phase2NamedContentRecognition Conflict(
        Phase2NamedContentKind kind,
        string slotKey,
        RelativeRegion region,
        Phase2OcrNameEvidence ocrEvidence,
        Phase2IconRecognition iconEvidence,
        IReadOnlyList<string> candidates,
        EvidenceReference evidence,
        string reason) => new(
            kind,
            slotKey,
            ObservationStatus.Conflict,
            null,
            null,
            ocrEvidence.RawTexts,
            0,
            region,
            Phase2RecognitionEvidenceKind.OcrAndIcon,
            candidates,
            [reason],
            evidence with
            {
                Locator = $"conflict:{kind}:{slotKey}",
                Summary = reason,
                Confidence = Math.Min(
                    ocrEvidence.Confidence,
                    Math.Max(0, iconEvidence.Confidence))
            });
}
