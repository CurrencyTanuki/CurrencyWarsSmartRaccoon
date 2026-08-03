using System.Globalization;
using System.Text;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

internal sealed class ChallengeReportMarkdownRenderer(
    ChallengeReportModelBuilder modelBuilder)
{
    public string Render(ChallengeReportDocument document)
    {
        var builder = new StringBuilder(32_000);
        builder.AppendLine("# 货币战争挑战总结")
            .AppendLine()
            .AppendLine($"- 对局：`{Escape(document.Run.RunId)}`")
            .AppendLine($"- 完成时间：{(document.Run.CompletedAt == DateTimeOffset.MinValue ? "未记录" : document.Run.CompletedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture))}")
            .AppendLine($"- 覆盖范围：{Escape(document.CoverageText)}")
            .AppendLine($"- 终局节点：{Escape(document.Run.CompletionNodeId)}")
            .AppendLine($"- 对局评价：{TextOrUnknown(document.Run.RatingText)}")
            .AppendLine($"- 最终生命：{FormatObservation(document.Run.LastSnapshot?.Health)}")
            .AppendLine($"- 数据状态：{(document.Run.IsFinal ? "已封存，不再修改" : "未封存")}")
            .AppendLine();

        builder.AppendLine("## 挑战评价")
            .AppendLine()
            .AppendLine($"**{Escape(document.OverallEvaluation.Title)}**")
            .AppendLine()
            .AppendLine(Escape(document.OverallEvaluation.Summary))
            .AppendLine();
        foreach (var evidence in document.OverallEvaluation.Evidence)
        {
            builder.AppendLine($"- 依据：{Escape(evidence)}");
        }

        builder.AppendLine()
            .AppendLine("## 对局固定构筑")
            .AppendLine()
            .AppendLine($"- 投资环境：{TextOrUnknown(document.FixedFacts.InvestmentEnvironment)}")
            .AppendLine($"- 投资策略：{ListOrUnknown(document.FixedFacts.InvestmentStrategies)}")
            .AppendLine($"- 敌人负面词条：{ListOrUnknown(document.FixedFacts.NegativeAffixes)}")
            .AppendLine($"- 特殊物品：{ListOrUnknown(document.FixedFacts.SpecialItems)}")
            .AppendLine($"- 专家顾问：{ListOrUnknown(document.FixedFacts.ExpertAdvisors)}")
            .AppendLine();

        builder.AppendLine("## 位面评价").AppendLine();
        foreach (var plane in document.Planes)
        {
            builder.AppendLine($"### 第{ChineseNumber(plane.Plane)}位面")
                .AppendLine()
                .AppendLine($"- 结论：{Escape(plane.Evaluation.Title)}")
                .AppendLine($"- 说明：{Escape(plane.Evaluation.Summary)}")
                .AppendLine();
        }

        builder.AppendLine("## 节点运营与结果")
            .AppendLine()
            .AppendLine("| 节点 | 最终伤害 | 伤害来源 | 剩余行动值 | 金币 | 金币变化 | 累计花费 | 花费变化 | 奖励 | 血量变化 | 通关 | 理论出伤极限 | 质量 |")
            .AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---|");
        foreach (var node in document.Nodes)
        {
            var battle = node.Battle;
            builder.Append("| ").Append(Escape(node.NodeId))
                .Append(" | ").Append(FormatDamage(node.FinalDamage))
                .Append(" | ").Append(DamageSourceName(battle?.SelectedDamageSource))
                .Append(" | ").Append(FormatInteger(battle?.RemainingActionValue?.TotalActionValue))
                .Append(" | ").Append(FormatInteger(node.Gold))
                .Append(" | ").Append(FormatSigned(node.GoldDelta))
                .Append(" | ").Append(FormatInteger(node.CumulativeSpend))
                .Append(" | ").Append(FormatSigned(node.SpendDelta))
                .Append(" | ").Append(FormatInteger(battle?.GoldReward))
                .Append(" | ").Append(battle?.HealthDepleted == true
                    ? "下降（未知）"
                    : FormatSigned(battle?.HealthDelta))
                .Append(" | ").Append(ClearName(battle?.ClearStatus))
                .Append(" | ").Append(FormatDamage(battle?.TheoreticalDamageLimit))
                .Append(" | ").Append(QualityName(battle))
                .AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## 节点阵容、装备与构筑").AppendLine();
        foreach (var node in document.Nodes)
        {
            builder.AppendLine($"### {Escape(node.NodeId)}").AppendLine();
            var formation = node.PreparationState?.Formation.Value;
            if (formation is not { Count: > 0 })
            {
                builder.AppendLine("- 阵容：未记录").AppendLine();
            }
            else
            {
                foreach (var character in formation.OrderBy(item => item.Zone).ThenBy(item => item.SlotIndex))
                {
                    builder.Append("- ").Append(ZoneName(character.Zone)).Append(character.SlotIndex + 1)
                        .Append("：").Append(Escape(modelBuilder.CharacterName(character.CharacterId)))
                        .Append(character.StarLevel.HasValue ? $"（{character.StarLevel}星）" : "（星级未记录）")
                        .Append("；装备：")
                        .AppendLine(character.EquipmentIds.Count == 0
                            ? "未记录"
                            : string.Join("、", character.EquipmentIds.Select(Escape)));
                }

                builder.AppendLine();
            }

            AppendOperational(builder, node);
            AppendDamage(builder, node);
        }

        builder.AppendLine("## 数据缺口与复查依据").AppendLine();
        if (document.Uncertainty.Count == 0)
        {
            builder.AppendLine("- 本次封存记录未报告额外数据冲突。");
        }
        else
        {
            foreach (var item in document.Uncertainty)
            {
                builder.AppendLine($"- {Escape(item)}");
            }
        }

        builder.AppendLine()
            .AppendLine("## 来源文件")
            .AppendLine()
            .AppendLine("- 全局记录：`completed-run.v1.json`");
        if (!string.IsNullOrWhiteSpace(document.Run.CompletionScreenshotFile))
        {
            builder.AppendLine($"- 终局证据截图：`{Escape(document.Run.CompletionScreenshotFile)}`");
        }

        foreach (var node in document.Nodes)
        {
            builder.AppendLine($"- {Escape(node.NodeId)}：备战 `{Escape(node.Source.PreparationAnalysisFile ?? "未记录")}`；战斗 `{Escape(node.Source.FinalBattleFile ?? "未记录")}`");
        }

        if (document.ExtensionFields.Count > 0)
        {
            builder.AppendLine().AppendLine("## 其他已记录字段").AppendLine();
            foreach (var field in document.ExtensionFields)
            {
                builder.AppendLine($"- `{Escape(field.Path)}`：`{Escape(field.Json)}`");
            }
        }

        return builder.ToString();
    }

    private void AppendOperational(StringBuilder builder, ChallengeReportNode node)
    {
        var state = node.PreparationState;
        var snapshot = node.Preparation;
        builder.AppendLine($"- 金币：{FormatInteger(node.Gold)}；金币变化：{FormatSigned(node.GoldDelta)}")
            .AppendLine($"- 累计花费：{FormatInteger(node.CumulativeSpend)}；花费变化：{FormatSigned(node.SpendDelta)}")
            .AppendLine($"- 血量：{FormatObservation(snapshot?.Health)}；敌人难度：{FormatObservation(state?.EnemyDifficulty)}；利息：{FormatObservation(state?.Interest)}")
            .AppendLine($"- 等级/经验：{FormatProgress(state?.PlayerProgress)}；拆解工具：{FormatObservation(state?.DismantleToolCount)}")
            .AppendLine($"- 简易装备：{FormatListObservation(state?.SimpleEquipmentIds)}")
            .AppendLine($"- 当前羁绊：{FormatSynergies(state?.ActiveSynergies)}")
            .AppendLine();
    }

    private void AppendDamage(StringBuilder builder, ChallengeReportNode node)
    {
        var battle = node.Battle;
        if (battle is null)
        {
            builder.AppendLine("- 最终战斗：未记录").AppendLine();
            return;
        }

        builder.AppendLine($"- 最终伤害：{FormatDamage(node.FinalDamage)}；战斗候选：{FormatDamage(battle.BattleScreenDamageCandidate)}；结算候选：{FormatDamage(battle.SettlementScreenDamageCandidate)}")
            .AppendLine($"- 剩余行动：{FormatInteger(battle.RemainingActionValue?.TotalActionValue)}；理论出伤极限：{FormatDamage(battle.TheoreticalDamageLimit)}；理论规则：{TextOrUnknown(battle.TheoreticalDamageRule)}")
            .AppendLine($"- 最大行动：基础 {FormatInteger(battle.BaseMaximumActionValue)}；确认增加 {FormatInteger(battle.ConfirmedActionIncrease)}；有效 {FormatInteger(battle.EffectiveMaximumActionValue)}；理论值质量 {battle.TheoreticalDamageQuality}")
            .AppendLine(battle.HealthDepleted
                ? $"- 血量：{FormatInteger(battle.PreBattleHealth)} → 已耗尽（具体变化未知）"
                : $"- 血量：{FormatInteger(battle.PreBattleHealth)} → {FormatInteger(battle.PostBattleHealth)}（{FormatSigned(battle.HealthDelta)}）")
            .AppendLine($"- 完美通关：{ClearName(battle.ClearStatus)}；奖励金币：{FormatInteger(battle.GoldReward)}")
            .AppendLine("- 角色伤害：");
        foreach (var item in battle.CharacterDamage.OrderBy(item => item.Rank))
        {
            builder.AppendLine($"  - {Escape(modelBuilder.CharacterName(item.CharacterId ?? item.TemporaryId))}：{FormatDamage(item.Damage)}（{(item.CanDriveDecisions ? "已确认" : "残缺候选")}）");
        }

        foreach (var item in battle.FinalSynergyDamage.OrderBy(item => item.Rank))
        {
            builder.AppendLine($"  - 羁绊 {Escape(item.SynergyId ?? item.TemporaryId ?? "未知")}：{FormatDamage(item.Damage)}（{(item.CanDriveDecisions ? "已确认" : "残缺候选")}）");
        }

        foreach (var item in battle.FinalUnresolvedDamage.OrderBy(item => item.Rank))
        {
            builder.AppendLine($"  - 未知来源 {Escape(item.TemporaryId)}：{FormatDamage(item.Damage)}；{Escape(item.FailureReason)}");
        }

        builder.AppendLine();
    }

    private static string FormatObservation(Observation<int>? observation) =>
        observation?.Status switch
        {
            ObservationStatus.Known => FormatInteger(observation.Value),
            ObservationStatus.Stale => "已过期",
            ObservationStatus.Conflict => "冲突",
            _ => "未记录"
        };

    private static string FormatProgress(Observation<PlayerProgressState>? observation) =>
        observation?.Status == ObservationStatus.Known && observation.Value is not null
            ? $"Lv.{observation.Value.Level} {observation.Value.Experience}/{observation.Value.ExperienceToNextLevel}"
            : "未记录";

    private static string FormatListObservation(Observation<IReadOnlyList<string>>? observation) =>
        observation?.Status == ObservationStatus.Known
            ? ListOrUnknown(observation.Value ?? [])
            : "未记录";

    private static string FormatSynergies(Observation<IReadOnlyList<ActiveSynergyState>>? observation) =>
        observation?.Status == ObservationStatus.Known && observation.Value is { Count: > 0 }
            ? string.Join("、", observation.Value.Select(item => $"{item.SynergyId ?? "未知羁绊"} {item.ActiveCount?.ToString(CultureInfo.InvariantCulture) ?? "?"}"))
            : "未记录";

    private static string FormatDamage(long? value) => value switch
    {
        null => "未记录",
        >= 100_000_000 => $"{value.Value / 100_000_000d:0.##}亿 ({value.Value:N0})",
        >= 10_000 => $"{value.Value / 10_000d:0.##}万 ({value.Value:N0})",
        _ => value.Value.ToString("N0", CultureInfo.InvariantCulture)
    };

    private static string FormatInteger(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "未记录";

    private static string FormatSigned(int? value) => value switch
    {
        null => "未记录",
        > 0 => $"+{value.Value.ToString(CultureInfo.InvariantCulture)}",
        _ => value.Value.ToString(CultureInfo.InvariantCulture)
    };

    private static string TextOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未记录" : Escape(value);

    private static string ListOrUnknown(IReadOnlyList<string> values) =>
        values.Count == 0 ? "未记录" : string.Join("、", values.Select(Escape));

    private static string DamageSourceName(FinalDamageSelectionSource? source) => source switch
    {
        FinalDamageSelectionSource.BattleLastFrame => "战斗末帧",
        FinalDamageSelectionSource.SettlementTopThree => "结算前三",
        _ => "未记录"
    };

    private static string ClearName(NodeClearStatus? status) => status switch
    {
        NodeClearStatus.Perfect => "完美",
        NodeClearStatus.NotPerfect => "✕ 未完美",
        _ => "未知"
    };

    private static string QualityName(FinalNodeBattleState? battle) => battle switch
    {
        null => "未记录",
        { IsComplete: true, CanDriveDecisions: true } =>
            battle.TheoreticalDamageQuality == TheoreticalDamageQuality.WalterEstimated
                ? "完整可信；理论值估算"
                : "完整可信",
        _ => "残缺/待复查"
    };

    private static string ZoneName(FormationZone zone) => zone switch
    {
        FormationZone.Front => "前台",
        FormationZone.Back => "后台",
        FormationZone.Bench => "候补",
        _ => "未知站位"
    };

    private static string ChineseNumber(int value) => value switch
    {
        1 => "一",
        2 => "二",
        3 => "三",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static string Escape(string value) => value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("`", "'", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
