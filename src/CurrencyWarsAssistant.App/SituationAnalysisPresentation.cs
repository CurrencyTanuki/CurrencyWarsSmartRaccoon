using System.Globalization;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.App;

internal sealed record SituationAnalysisPresentation(
    string PageSummary,
    string StateSummary,
    string WarningsSummary,
    int KnownFieldCount,
    int UnknownFieldCount)
{
    public static SituationAnalysisPresentation Build(
        ScreenshotAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var snapshot = result.Snapshot;
        var operational = result.OperationalState;
        var pageFamily = operational?.PageFamily ?? Phase2PageFamily.Unknown;
        var pageText = snapshot.PageId.Status == ObservationStatus.Known &&
                       !string.IsNullOrWhiteSpace(snapshot.PageId.Value)
            ? snapshot.PageId.Value!
            : PageFamilyName(pageFamily);
        var stageText = snapshot.Stage.Status == ObservationStatus.Known &&
                        !string.IsNullOrWhiteSpace(snapshot.Stage.Value)
            ? snapshot.Stage.Value!
            : "未知";

        var pageLines = new List<string>
        {
            $"页面：{pageText}",
            $"页面类型：{PageFamilyName(pageFamily)}",
            $"阶段：{stageText}"
        };
        if (operational is not null &&
            operational.NodeId.Status == ObservationStatus.Known)
        {
            pageLines.Add($"节点：{operational.NodeId.Value}");
        }

        var stateLines = new List<string>();
        var warnings = result.Warnings
            .Where(IsUserFacingWarning)
            .ToList();
        var known = 0;
        var unknown = 0;

        if (snapshot.Economy.Status == ObservationStatus.Known ||
            pageFamily == Phase2PageFamily.Preparation)
        {
            AddObservation(stateLines, warnings, "经济", snapshot.Economy,
                value => value.ToString(CultureInfo.InvariantCulture),
                ref known, ref unknown);
        }

        if (snapshot.Health.Status == ObservationStatus.Known ||
            pageFamily is Phase2PageFamily.Preparation or
                Phase2PageFamily.BattleSettlement)
        {
            AddObservation(stateLines, warnings, "生命", snapshot.Health,
                value => value.ToString(CultureInfo.InvariantCulture),
                ref known, ref unknown);
        }

        if (operational is not null)
        {
            AddOperationalState(
                operational,
                stateLines,
                warnings,
                ref known,
                ref unknown);
        }

        if (stateLines.Count == 0)
        {
            stateLines.Add(pageFamily == Phase2PageFamily.Main
                ? "当前主页面没有需要写入对局记录的动态字段。"
                : "当前帧没有可可靠确认的业务字段；未将未知值伪装为 0。" );
        }

        if (operational is not null)
        {
            // Diagnostics stay available in structured JSON and logs. They are
            // not user warnings, and showing classifier scores here made a
            // correctly recovered page look like a complete recognition failure.
            warnings.AddRange(operational.PartialFields
                .Where(item => IsPartialFieldRelevant(pageFamily, item.Field))
                .Select(item =>
                    $"{item.Field}/{item.TemporaryId}：{item.FailureReason}"));
            warnings.AddRange(operational.PendingIcons.Select(item =>
                $"{item.Category}/{item.TemporaryId ?? item.SlotKey}：{item.Status}"));
        }

        var warningLines = warnings
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new SituationAnalysisPresentation(
            string.Join(Environment.NewLine, pageLines),
            string.Join(Environment.NewLine, stateLines),
            warningLines.Length == 0
                ? "无额外警告。"
                : string.Join(Environment.NewLine, warningLines.Select(value => "• " + value)),
            known,
            unknown);
    }

    private static bool IsUserFacingWarning(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith(
            "以下字段在这张截图中不可可靠确定：",
            StringComparison.Ordinal) &&
        !value.StartsWith("recognition:", StringComparison.Ordinal) &&
        !value.StartsWith("perf:", StringComparison.Ordinal);

    private static bool IsPartialFieldRelevant(
        Phase2PageFamily pageFamily,
        string field) => pageFamily switch
        {
            // Main has no dynamic per-run fields. Page-anchor/OCR fallback
            // evidence remains in JSON and logs but is not a user warning.
            Phase2PageFamily.Main => false,
            Phase2PageFamily.Preparation =>
                field.StartsWith("preparation-", StringComparison.Ordinal) ||
                field.StartsWith("main-", StringComparison.Ordinal),
            Phase2PageFamily.Battle =>
                field.StartsWith("battle-", StringComparison.Ordinal) ||
                string.Equals(field, "remaining-action", StringComparison.Ordinal),
            Phase2PageFamily.BattleSettlement =>
                field.StartsWith("settlement-", StringComparison.Ordinal),
            // On a truly unknown/transition frame, partial regions are the only
            // retained evidence and must remain visible rather than discarded.
            _ => true
        };

    private static void AddOperationalState(
        Phase2OperationalState state,
        List<string> lines,
        List<string> warnings,
        ref int known,
        ref int unknown)
    {
        if (state.PageFamily is Phase2PageFamily.Preparation or Phase2PageFamily.Battle)
        {
            AddObservation(lines, warnings, "节点", state.NodeId, value => value,
                ref known, ref unknown);
        }

        if (state.PageFamily == Phase2PageFamily.Preparation)
        {
            AddObservation(lines, warnings, "敌人难度", state.EnemyDifficulty,
                value => value.ToString(CultureInfo.InvariantCulture), ref known, ref unknown);
            AddObservation(lines, warnings, "利息", state.Interest,
                value => value.ToString(CultureInfo.InvariantCulture), ref known, ref unknown);
            AddObservation(lines, warnings, "累计消费", state.CumulativeSpend,
                value => value.ToString(CultureInfo.InvariantCulture), ref known, ref unknown);
            AddObservation(lines, warnings, "等级/经验", state.PlayerProgress,
                value => $"Lv.{value.Level} {value.Experience}/{value.ExperienceToNextLevel}",
                ref known, ref unknown);
            AddObservation(lines, warnings, "阵容", state.Formation,
                FormatFormation, ref known, ref unknown);
            AddObservation(lines, warnings, "已激活羁绊", state.ActiveSynergies,
                FormatSynergies, ref known, ref unknown);
            AddObservation(lines, warnings, "拆解工具", state.DismantleToolCount,
                value => value.ToString(CultureInfo.InvariantCulture), ref known, ref unknown);
            AddObservation(lines, warnings, "简易装备", state.SimpleEquipmentIds,
                FormatIds, ref known, ref unknown);
            AddObservation(lines, warnings, "敌人负面词条", state.NegativeAffixIds,
                FormatIds, ref known, ref unknown);
            AddObservation(lines, warnings, "投资环境", state.InvestmentEnvironmentId,
                value => value, ref known, ref unknown);
            AddObservation(lines, warnings, "投资策略", state.InvestmentStrategyIds,
                FormatIds, ref known, ref unknown);
        }

        if (state.PageFamily == Phase2PageFamily.Battle)
        {
            unknown += state.BattleDamage.Value?.Count(value =>
                IsUnknownId(value.CharacterId)) ?? 0;
            unknown += state.BattleSynergyDamage.Value?.Count(value =>
                IsUnknownId(value.SynergyId)) ?? 0;
            AddObservation(lines, warnings, "剩余行动值", state.RemainingActionValue,
                value => $"{value.RemainingRounds}轮 + {value.CurrentRoundActionValue} = {value.TotalActionValue}",
                ref known, ref unknown);
            AddObservation(lines, warnings, "角色最终伤害候选", state.BattleDamage,
                values => FormatDamageRows(values, warnings),
                ref known, ref unknown);
            AddObservation(lines, warnings, "羁绊最终伤害候选", state.BattleSynergyDamage,
                values => FormatSynergyDamageRows(values, warnings),
                ref known, ref unknown);
            AddObservation(lines, warnings, "战斗画面总伤害候选",
                state.BattleScreenDamageCandidate, FormatDamage,
                ref known, ref unknown);
        }

        if (state.PageFamily == Phase2PageFamily.BattleSettlement)
        {
            unknown += state.SettlementDamage.Value?.Count(value =>
                IsUnknownId(value.CharacterId)) ?? 0;
            AddObservation(lines, warnings, "奖励金币", state.SettlementGoldReward,
                value => value.ToString(CultureInfo.InvariantCulture), ref known, ref unknown);
            AddObservation(lines, warnings, "结算前三伤害", state.SettlementDamage,
                values => FormatDamageRows(values, warnings),
                ref known, ref unknown);
            AddObservation(lines, warnings, "结算画面总伤害候选",
                state.SettlementScreenDamageCandidate, FormatDamage,
                ref known, ref unknown);
        }
    }

    private static void AddObservation<T>(
        List<string> lines,
        List<string> warnings,
        string label,
        Observation<T> observation,
        Func<T, string> formatter,
        ref int known,
        ref int unknown)
    {
        if (observation.Value is not null)
        {
            if (observation.Status == ObservationStatus.Known)
            {
                known++;
                lines.Add($"{label}：{formatter(observation.Value)}（{observation.Confidence:P0}）");
            }
            else
            {
                unknown++;
                var partialReason = observation.Uncertainty.Count == 0
                    ? "只有部分字段具备可验证证据"
                    : string.Join("；", observation.Uncertainty);
                lines.Add(
                    $"{label}：{formatter(observation.Value)}" +
                    $"（残缺；{partialReason}）");
                warnings.Add($"{label}：{partialReason}");
            }

            return;
        }

        unknown++;
        var reason = observation.Uncertainty.Count == 0
            ? "没有可验证证据"
            : string.Join("；", observation.Uncertainty);
        lines.Add($"{label}：{StatusName(observation.Status)}（{reason}）");
        warnings.Add($"{label}：{reason}");
    }

    private static string FormatFormation(
        IReadOnlyList<FormationCharacterState> values) => values.Count == 0
        ? "已确认无角色"
        : string.Join("；", values.Select(value =>
        {
            var character = string.IsNullOrWhiteSpace(value.CharacterId)
                ? value.TemporaryId ?? $"未知角色{value.SlotIndex + 1}"
                : value.CharacterId;
            var equipment = value.EquipmentIds.Count == 0
                ? "无装备"
                : "装备=" + string.Join(",", value.EquipmentIds);
            return $"{value.Zone}[{value.SlotIndex + 1}]={character} {equipment}";
        }));

    private static string FormatSynergies(
        IReadOnlyList<ActiveSynergyState> values) => values.Count == 0
        ? "已确认无激活羁绊"
        : string.Join("；", values.Select(value =>
            $"{value.SynergyId ?? "未知羁绊"}" +
            (value.ActiveCount is null ? "" : $"={value.ActiveCount}")));

    private static string FormatIds(IReadOnlyList<string> values) =>
        values.Count == 0 ? "已确认无" : string.Join("、", values);

    private static string FormatDamageRows(
        IReadOnlyList<CharacterDamageState> values,
        List<string> warnings)
    {
        if (values.Count == 0)
        {
            return "已确认无伤害行";
        }

        return string.Join("；", values.Select(value =>
        {
            var source = value.CharacterId;
            if (IsUnknownId(source))
            {
                source = UnknownCharacterName(value);
                if (!string.IsNullOrWhiteSpace(value.FailureReason))
                {
                    warnings.Add($"{source}：{value.FailureReason}");
                }
            }

            var damage = string.IsNullOrWhiteSpace(value.RawText)
                ? FormatDamage(value.Damage)
                : $"{value.RawText}（{value.Damage:N0}）";
            return $"#{value.Rank} {source} {damage}";
        }));
    }

    private static string FormatSynergyDamageRows(
        IReadOnlyList<SynergyDamageState> values,
        List<string> warnings)
    {
        if (values.Count == 0)
        {
            return "已确认无羁绊伤害行";
        }

        return string.Join("；", values.Select(value =>
        {
            var source = value.SynergyId;
            if (IsUnknownId(source))
            {
                source = value.TemporaryId ?? $"未知羁绊{value.Rank}";
                if (!string.IsNullOrWhiteSpace(value.FailureReason))
                {
                    warnings.Add($"{source}：{value.FailureReason}");
                }
            }

            var damage = string.IsNullOrWhiteSpace(value.RawText)
                ? FormatDamage(value.Damage)
                : $"{value.RawText}（{value.Damage:N0}）";
            return $"#{value.Rank} {source} {damage}";
        }));
    }

    private static string UnknownCharacterName(CharacterDamageState value)
    {
        var suffix = (value.TemporaryId ?? value.CharacterId)?
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(part => int.TryParse(part, out _));
        return int.TryParse(suffix, out var index)
            ? $"未知角色{index}"
            : $"未知角色{value.Rank}";
    }

    private static bool IsUnknownId(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.StartsWith("unknown-", StringComparison.OrdinalIgnoreCase);

    private static string FormatDamage(long value) =>
        value >= 10_000
            ? $"{value / 10_000d:0.####}万（{value:N0}）"
            : value.ToString("N0", CultureInfo.InvariantCulture);

    private static string StatusName(ObservationStatus status) => status switch
    {
        ObservationStatus.Conflict => "存在冲突",
        ObservationStatus.Stale => "沿用旧值，可能已过期",
        _ => "未知或暂不可见"
    };

    private static string PageFamilyName(Phase2PageFamily pageFamily) =>
        pageFamily switch
        {
            Phase2PageFamily.Main => "货币战争主界面",
            Phase2PageFamily.Preparation => "备战页面",
            Phase2PageFamily.Battle => "战斗页面",
            Phase2PageFamily.BattleSettlement => "战斗结算",
            _ => "未知页面"
        };
}
