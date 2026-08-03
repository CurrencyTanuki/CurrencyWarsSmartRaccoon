using System.Globalization;
using System.Text.Json;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

internal sealed record ChallengeReportExtensionField(
    string Path,
    string Json);

internal sealed record ChallengeReportEvaluation(
    string Title,
    string Summary,
    string Tone,
    IReadOnlyList<string> Evidence,
    bool HasEnoughData);

internal sealed record ChallengeReportFixedFacts(
    string? InvestmentEnvironment,
    IReadOnlyList<string> InvestmentStrategies,
    IReadOnlyList<string> NegativeAffixes,
    IReadOnlyList<string> SpecialItems,
    IReadOnlyList<string> ExpertAdvisors);

internal sealed record ChallengeReportNode(
    CompletedRunNodeRecord Source,
    int Plane,
    int Sequence,
    int? Gold,
    int? GoldDelta,
    int? CumulativeSpend,
    int? SpendDelta,
    long? FinalDamage,
    bool HasTrustedDamage,
    IReadOnlyList<string> Uncertainty)
{
    public string NodeId => Source.NodeId;
    public FinalNodeBattleState? Battle => Source.FinalBattle;
    public RunSnapshot? Preparation => Source.FinalPreparationSnapshot;
    public Phase2OperationalState? PreparationState =>
        Source.FinalPreparationState;
}

internal sealed record ChallengeReportPlane(
    int Plane,
    IReadOnlyList<ChallengeReportNode> Nodes,
    ChallengeReportEvaluation Evaluation);

internal sealed record ChallengeReportDocument(
    CompletedRunRecord Run,
    IReadOnlyList<ChallengeReportNode> Nodes,
    IReadOnlyList<ChallengeReportPlane> Planes,
    ChallengeReportFixedFacts FixedFacts,
    ChallengeReportEvaluation OverallEvaluation,
    IReadOnlyList<string> Uncertainty,
    IReadOnlyList<ChallengeReportExtensionField> ExtensionFields,
    string CoverageText,
    int TrustedBattleCount,
    int BattleCount);

internal sealed class ChallengeReportModelBuilder(GameDataCatalog? catalog = null)
{
    private readonly IReadOnlyDictionary<string, string> _characterNames =
        (catalog?.CurrencyWarsCharacters ?? [])
        .ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _environmentNames =
        (catalog?.InvestmentEnvironments ?? [])
        .ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _strategyNames =
        (catalog?.InvestmentStrategies ?? [])
        .ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _affixNames =
        (catalog?.EnemyAffixes ?? [])
        .ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);

    public ChallengeReportDocument Build(
        CompletedRunRecord run,
        IReadOnlyList<ChallengeReportExtensionField>? extensionFields = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        var ordered = run.Nodes
            .Select(item => (Source: item, Parsed: ParseNode(item.NodeId)))
            .OrderBy(item => item.Parsed.Plane)
            .ThenBy(item => item.Parsed.Sequence)
            .ThenBy(item => item.Source.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var nodes = new List<ChallengeReportNode>(ordered.Length);
        int? previousGold = null;
        int? previousSpend = null;
        foreach (var item in ordered)
        {
            var gold = KnownInt(item.Source.FinalPreparationSnapshot?.Economy);
            var spend = KnownInt(item.Source.FinalPreparationState?.CumulativeSpend) ??
                        KnownInt(item.Source.FinalPreparationSnapshot?.CumulativeSpend);
            var goldDelta = gold.HasValue && previousGold.HasValue
                ? gold - previousGold
                : (int?)null;
            var spendDelta = spend.HasValue && previousSpend.HasValue
                ? spend - previousSpend
                : spend;
            var battle = item.Source.FinalBattle;
            var finalDamage = battle?.SelectedDamage ?? battle?.TotalDamage;
            var nodeUncertainty = (battle?.FinalUncertainty ?? [])
                .Concat(item.Source.FinalPreparationState?.Diagnostics ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            nodes.Add(new ChallengeReportNode(
                item.Source,
                item.Parsed.Plane,
                item.Parsed.Sequence,
                gold,
                goldDelta,
                spend,
                spendDelta,
                finalDamage,
                battle is
                {
                    IsComplete: true,
                    CanDriveDecisions: true
                } && finalDamage.HasValue,
                nodeUncertainty));
            previousGold = gold ?? previousGold;
            previousSpend = spend ?? previousSpend;
        }

        var planes = Enumerable.Range(1, 3)
            .Select(plane =>
            {
                var planeNodes = nodes.Where(item => item.Plane == plane).ToArray();
                return new ChallengeReportPlane(
                    plane,
                    planeNodes,
                    EvaluatePlane(run.RunId, plane, planeNodes));
            })
            .ToArray();
        var fixedFacts = BuildFixedFacts(nodes);
        var uncertainty = run.Uncertainty
            .Concat(nodes.SelectMany(item => item.Uncertainty))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var battleCount = nodes.Count(item => item.Battle is not null);
        var trustedBattleCount = nodes.Count(item => item.HasTrustedDamage);
        return new ChallengeReportDocument(
            run,
            nodes,
            planes,
            fixedFacts,
            EvaluateOverall(run.RunId, nodes, planes),
            uncertainty,
            extensionFields ?? [],
            BuildCoverageText(nodes),
            trustedBattleCount,
            battleCount);
    }

    public string CharacterName(string? id) => ResolveName(
        id,
        _characterNames,
        "未知角色");

    public string EnvironmentName(string? id) => ResolveName(
        id,
        _environmentNames,
        "未知投资环境");

    public string StrategyName(string? id) => ResolveName(
        id,
        _strategyNames,
        "未知投资策略");

    public string AffixName(string? id) => ResolveName(
        id,
        _affixNames,
        "未知负面词条");

    private ChallengeReportFixedFacts BuildFixedFacts(
        IReadOnlyList<ChallengeReportNode> nodes)
    {
        var states = nodes
            .Select(item => item.PreparationState)
            .Where(item => item is not null)
            .Cast<Phase2OperationalState>()
            .ToArray();
        var snapshots = nodes
            .Select(item => item.Preparation)
            .Where(item => item is not null)
            .Cast<RunSnapshot>()
            .ToArray();
        var environmentId = states
            .Select(item => KnownReference(item.InvestmentEnvironmentId))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ??
            snapshots
                .Select(item => KnownReference(item.InvestmentEnvironmentId))
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var strategyIds = states
            .SelectMany(item => KnownReference(item.InvestmentStrategyIds) ?? [])
            .Concat(snapshots.SelectMany(item =>
                KnownReference(item.InvestmentStrategyIds) ?? []))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var affixIds = states
            .SelectMany(item => KnownReference(item.NegativeAffixIds) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var specialItems = snapshots
            .SelectMany(item => KnownReference(item.SpecialItemIds) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var advisors = snapshots
            .SelectMany(item => KnownReference(item.ExpertAdvisorIds) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ChallengeReportFixedFacts(
            string.IsNullOrWhiteSpace(environmentId)
                ? null
                : EnvironmentName(environmentId),
            strategyIds.Select(StrategyName).ToArray(),
            affixIds.Select(AffixName).ToArray(),
            specialItems,
            advisors);
    }

    private static ChallengeReportEvaluation EvaluateOverall(
        string runId,
        IReadOnlyList<ChallengeReportNode> nodes,
        IReadOnlyList<ChallengeReportPlane> planes)
    {
        var trusted = nodes
            .Where(item => item.HasTrustedDamage && item.Battle?.IsRewardNode != true)
            .ToArray();
        var coveredPlanes = trusted.Select(item => item.Plane).Distinct().Count();
        if (trusted.Length < 3 || coveredPlanes < 2)
        {
            return new ChallengeReportEvaluation(
                "数据不足，暂不进行整局好坏评价",
                "当前报告会完整展示已确认数据，但不会依据未记录节点或低置信结果评价玩家表现。",
                "insufficient",
                [
                    $"可信战斗节点 {trusted.Length} 个",
                    $"可信数据覆盖 {coveredPlanes} 个位面"
                ],
                false);
        }

        var knownClear = trusted
            .Where(item => item.Battle?.ClearStatus != NodeClearStatus.Unknown)
            .ToArray();
        var perfectCount = knownClear.Count(item =>
            item.Battle?.ClearStatus == NodeClearStatus.Perfect);
        var healthLoss = trusted
            .Where(item => item.Battle?.HealthDelta is < 0)
            .Sum(item => -item.Battle!.HealthDelta!.Value);
        var actionRatios = trusted
            .Where(item => item.Battle?.RemainingActionValue is not null)
            .Select(item =>
            {
                var battle = item.Battle!;
                var maximum = battle.EffectiveMaximumActionValue ??
                              battle.BaseMaximumActionValue;
                return maximum is > 0
                    ? battle.RemainingActionValue!.TotalActionValue /
                      (double)maximum.Value
                    : (double?)null;
            })
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        var averageActionRatio = actionRatios.Length == 0
            ? (double?)null
            : actionRatios.Average();
        var strong = knownClear.Length > 0 && perfectCount == knownClear.Length &&
                     healthLoss == 0 && averageActionRatio is >= 0.35;
        var weak = knownClear.Length > 0 && perfectCount * 2 < knownClear.Length ||
                   healthLoss >= 20;
        var title = strong
            ? Pick(runId, "收官稳健，运营与战斗余量都很充足", "节奏清晰，整局保持了较高容错")
            : weak
                ? Pick(runId, "成功完成挑战，但中后段仍有提升空间", "挑战已完成，部分节点消耗偏高")
                : Pick(runId, "整体推进稳定，关键节点表现可靠", "完成度良好，局势控制较为平稳");
        var summary = strong
            ? "完美节点、血量和剩余行动值共同表明本局拥有较高稳定性。"
            : weak
                ? "报告只根据已确认的通关、血量和行动值指出高消耗节点，未对缺失数据作负面推断。"
                : "已确认节点没有出现持续恶化信号，构筑和伤害随位面推进保持可用。";
        var evidence = new List<string>
        {
            knownClear.Length == 0
                ? "完美通关状态未形成足够证据"
                : $"已知完美通关 {perfectCount}/{knownClear.Length}",
            $"确认血量损失 {healthLoss}"
        };
        if (averageActionRatio.HasValue)
        {
            evidence.Add($"平均剩余行动比例 {averageActionRatio.Value:P0}");
        }

        return new ChallengeReportEvaluation(
            title,
            summary,
            strong ? "excellent" : weak ? "warning" : "balanced",
            evidence,
            true);
    }

    private static ChallengeReportEvaluation EvaluatePlane(
        string runId,
        int plane,
        IReadOnlyList<ChallengeReportNode> nodes)
    {
        var trusted = nodes
            .Where(item => item.HasTrustedDamage && item.Battle?.IsRewardNode != true)
            .ToArray();
        if (trusted.Length == 0)
        {
            return new ChallengeReportEvaluation(
                "数据不足",
                nodes.Count == 0
                    ? "本位面没有进入记录范围。"
                    : "本位面有残缺记录，但没有足够可信的战斗结果。",
                "insufficient",
                [$"记录节点 {nodes.Count} 个；可信战斗 0 个"],
                false);
        }

        if (trusted.Length == 1)
        {
            return new ChallengeReportEvaluation(
                "单点记录",
                "仅展示该节点结果，不据此推断整个位面的上升或下降趋势。",
                "balanced",
                [$"可信节点 {trusted[0].NodeId}：{FormatDamage(trusted[0].FinalDamage)}"],
                false);
        }

        var first = trusted[0];
        var last = trusted[^1];
        var growth = first.FinalDamage is > 0 && last.FinalDamage.HasValue
            ? last.FinalDamage.Value / (double)first.FinalDamage.Value
            : (double?)null;
        var healthLoss = trusted
            .Where(item => item.Battle?.HealthDelta is < 0)
            .Sum(item => -item.Battle!.HealthDelta!.Value);
        var title = growth switch
        {
            >= 1.5 => Pick(runId + plane, "伤害成长明显", "构筑在本位面完成提速"),
            <= 0.7 => Pick(runId + plane, "后段输出承压", "本位面伤害出现回落"),
            _ => "表现相对平稳"
        };
        var summary = growth.HasValue
            ? $"从 {first.NodeId} 到 {last.NodeId} 的可信最终伤害变化为 {growth.Value:P0}；确认血量损失 {healthLoss}。"
            : $"本位面记录到 {trusted.Length} 个可信节点；伤害趋势数据不足。";
        return new ChallengeReportEvaluation(
            title,
            summary,
            growth <= 0.7 || healthLoss >= 10 ? "warning" : "balanced",
            [
                $"起点 {first.NodeId}：{FormatDamage(first.FinalDamage)}",
                $"终点 {last.NodeId}：{FormatDamage(last.FinalDamage)}"
            ],
            true);
    }

    private static string BuildCoverageText(
        IReadOnlyList<ChallengeReportNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return "没有记录到节点";
        }

        return $"{nodes[0].NodeId}—{nodes[^1].NodeId}";
    }

    private static (int Plane, int Sequence) ParseNode(string? value)
    {
        var parts = value?.Split('-', 2) ?? [];
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var plane) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
            ? (plane, sequence)
            : (int.MaxValue, int.MaxValue);
    }

    private static int? KnownInt(Observation<int>? observation) =>
        observation?.Status == ObservationStatus.Known
            ? observation.Value
            : null;

    private static T? KnownReference<T>(Observation<T>? observation)
        where T : class =>
        observation?.Status == ObservationStatus.Known
            ? observation.Value
            : null;

    private static string ResolveName(
        string? id,
        IReadOnlyDictionary<string, string> names,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return fallback;
        }

        if (names.TryGetValue(id, out var name))
        {
            return name;
        }

        return id.StartsWith("unknown", StringComparison.OrdinalIgnoreCase)
            ? $"{fallback}（{id}）"
            : id;
    }

    private static string Pick(string seed, params string[] options)
    {
        uint hash = 2166136261;
        foreach (var character in seed)
        {
            hash ^= character;
            hash *= 16777619;
        }

        return options[(int)(hash % (uint)options.Length)];
    }

    private static string FormatDamage(long? value) => value switch
    {
        null => "未记录",
        >= 100_000_000 => $"{value.Value / 100_000_000d:0.##}亿",
        >= 10_000 => $"{value.Value / 10_000d:0.##}万",
        _ => value.Value.ToString("N0", CultureInfo.InvariantCulture)
    };

    public static IReadOnlyList<ChallengeReportExtensionField> ReadExtensions(
        JsonDocument document)
    {
        var result = new List<ChallengeReportExtensionField>();
        var knownRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "schemaVersion", "archiveVersion", "runId", "completedAt",
            "isFinal", "completionPageId", "completionNodeId",
            "completionScreenshotFile", "ratingText", "lastSnapshot",
            "lastOperationalState", "nodes", "sourceAnalysisFiles",
            "uncertainty"
        };
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!knownRoot.Contains(property.Name))
            {
                result.Add(new ChallengeReportExtensionField(
                    property.Name,
                    Compact(property.Value)));
            }
        }

        if (document.RootElement.TryGetProperty("nodes", out var nodes) &&
            nodes.ValueKind == JsonValueKind.Array)
        {
            var knownNode = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "nodeId", "finalPreparationSnapshot", "finalPreparationState",
                "finalBattle", "preparationAnalysisFile", "finalBattleFile"
            };
            var index = 0;
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                var nodeId = node.TryGetProperty("nodeId", out var id)
                    ? id.GetString() ?? index.ToString(CultureInfo.InvariantCulture)
                    : index.ToString(CultureInfo.InvariantCulture);
                foreach (var property in node.EnumerateObject())
                {
                    if (!knownNode.Contains(property.Name))
                    {
                        result.Add(new ChallengeReportExtensionField(
                            $"nodes[{nodeId}].{property.Name}",
                            Compact(property.Value)));
                    }
                }

                AddUnknownObjectProperties(
                    result,
                    node,
                    "finalBattle",
                    $"nodes[{nodeId}].finalBattle",
                    FinalBattleProperties);
                AddUnknownObjectProperties(
                    result,
                    node,
                    "finalPreparationSnapshot",
                    $"nodes[{nodeId}].finalPreparationSnapshot",
                    SnapshotProperties);
                AddUnknownObjectProperties(
                    result,
                    node,
                    "finalPreparationState",
                    $"nodes[{nodeId}].finalPreparationState",
                    OperationalStateProperties);

                index++;
            }
        }

        return result;
    }

    private static readonly IReadOnlySet<string> FinalBattleProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nodeId", "characterDamage", "totalDamage", "remainingActionValue",
            "capturedAt", "evidence", "synergyDamage", "isComplete",
            "canDriveDecisions", "uncertainty", "degradedObservations",
            "partialFields", "unresolvedDamage", "battleScreenDamageCandidate",
            "settlementScreenDamageCandidate", "selectedDamage",
            "selectedDamageSource", "settlementTopThree", "goldReward",
            "preBattleHealth", "postBattleHealth", "healthDelta", "clearStatus",
            "theoreticalDamageLimit", "baseMaximumActionValue",
            "confirmedActionIncrease", "effectiveMaximumActionValue",
            "theoreticalDamageQuality", "theoreticalDamageRule", "isRewardNode"
        };

    private static readonly IReadOnlySet<string> SnapshotProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "schemaVersion", "runId", "asOf", "pageId", "stage", "economy",
            "cumulativeSpend", "health", "actionPoints", "currentNodeDamage",
            "boardCharacterIds", "benchCharacterIds", "shopCharacterIds",
            "lineupIds", "synergyIds", "investmentEnvironmentId",
            "investmentStrategyIds", "equipmentIds", "specialItemIds",
            "expertAdvisorIds", "enemyIds", "nodes", "appliedEventIds",
            "diagnostics"
        };

    private static readonly IReadOnlySet<string> OperationalStateProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pageFamily", "pageId", "nodeId", "enemyDifficulty", "interest",
            "cumulativeSpend", "playerProgress", "formation", "activeSynergies",
            "dismantleToolCount", "simpleEquipmentIds", "negativeAffixIds",
            "investmentEnvironmentId", "investmentStrategyIds", "battleDamage",
            "battleSynergyDamage", "battleUnresolvedDamage",
            "battleScreenDamageCandidate", "settlementDamage",
            "settlementScreenDamageCandidate", "settlementGoldReward",
            "remainingActionValue", "finalBattle", "namedContent", "pendingIcons",
            "partialFields", "diagnostics"
        };

    private static void AddUnknownObjectProperties(
        ICollection<ChallengeReportExtensionField> result,
        JsonElement parent,
        string propertyName,
        string path,
        IReadOnlySet<string> known)
    {
        if (!parent.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                result.Add(new ChallengeReportExtensionField(
                    $"{path}.{property.Name}",
                    Compact(property.Value)));
            }
        }
    }

    private static string Compact(JsonElement value)
    {
        var json = value.GetRawText();
        return json.Length <= 800 ? json : json[..800] + "…";
    }
}
