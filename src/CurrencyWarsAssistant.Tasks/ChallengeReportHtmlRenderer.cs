using System.Globalization;
using System.Net;
using System.Text;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

internal sealed class ChallengeReportHtmlRenderer(
    ChallengeReportModelBuilder modelBuilder,
    ChallengeReportAssetCatalog assets)
{
    private const int ChartWidth = 540;
    private const int ChartHeight = 250;

    public string Render(ChallengeReportDocument document)
    {
        var builder = new StringBuilder(96_000);
        builder.Append("""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>货币战争挑战总结</title>
<style>
:root{color-scheme:dark;--bg:#080d19;--panel:#131c34;--panel2:#18223d;--line:#34476f;--gold:#f3c65f;--cyan:#6ed8ff;--green:#8fdcb2;--pink:#ff7bb6;--purple:#bc8cff;--warn:#f2bd6c;--bad:#ff8c9e;--text:#f5f7ff;--muted:#9aa8c7}
*{box-sizing:border-box}body{margin:0;color:var(--text);background:radial-gradient(circle at 75% -10%,#2c2446 0,transparent 32%),linear-gradient(155deg,#070b14,#10172a 48%,#080d19);font:14px/1.6 "Microsoft YaHei UI","Microsoft YaHei",sans-serif}main{width:min(1220px,calc(100% - 28px));margin:22px auto 60px}.panel{background:rgba(19,28,52,.94);border:1px solid var(--line);border-radius:14px}.hero{position:relative;overflow:hidden;padding:30px 32px;border-color:#826527;background:linear-gradient(135deg,#17182d 0,#17182d 64%,#2b243e 64%,#201c35 100%)}.hero:after{content:"";position:absolute;right:14%;top:-90px;width:210px;height:560px;transform:rotate(31deg);background:rgba(136,94,164,.12)}.eyebrow{color:var(--gold);font-weight:800;letter-spacing:.14em}.hero h1{font-size:42px;line-height:1.15;margin:12px 0 5px}.muted{color:var(--muted)}.metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;margin-top:22px;position:relative;z-index:1}.metric{padding:14px 16px;background:rgba(18,27,51,.92);border:1px solid #334a79;border-radius:10px}.metric b{display:block;margin:4px 0;font-size:25px}.gold{color:var(--gold)}.cyan{color:var(--cyan)}.green{color:var(--green)}.pink{color:var(--pink)}.warn{color:var(--warn)}h2{font-size:24px;margin:34px 0 13px}h3{font-size:18px;margin:0 0 10px}.evaluation{display:grid;grid-template-columns:190px 1fr;gap:22px;padding:22px}.grade{display:flex;min-height:126px;flex-direction:column;justify-content:center;padding:16px;border:1px solid #806127;border-radius:12px;background:#20263d}.grade strong{font-size:27px;color:var(--warn)}.evidence-chips,.chips{display:flex;flex-wrap:wrap;gap:7px;margin-top:12px}.chip{padding:4px 9px;border:1px solid #3a507e;border-radius:999px;background:#1e2a49;color:#cdd8ee;font-size:12px}.charts{display:grid;grid-template-columns:1fr 1fr;gap:12px}.chart{padding:16px 16px 12px}.chart-head{display:flex;justify-content:space-between;align-items:center}.scale{font-size:12px;color:var(--muted)}svg{width:100%;height:auto;display:block}.gridline{stroke:#293857;stroke-width:1}.axis-label{fill:#9aa8c7;font-size:11px}.point{stroke:#f8fbff;stroke-width:1}.missing{fill:none;stroke:#73819d;stroke-width:1.5}.chart-note{font-size:12px;color:#8593ae;margin:4px 2px}.plane-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}.plane{padding:18px;border-left:6px solid var(--gold)}.plane[data-plane="1"]{border-left-color:#7f8da9}.plane[data-plane="2"]{border-left-color:var(--cyan)}.plane[data-plane="3"]{border-left-color:var(--gold)}.plane-title{display:flex;justify-content:space-between;gap:12px}.fixed-grid{display:grid;grid-template-columns:repeat(2,1fr);gap:12px}.fixed{padding:18px}.fixed dt{color:var(--muted);font-size:12px}.fixed dd{margin:2px 0 12px}.formation-list{display:grid;gap:12px}.formation{padding:17px}.formation-head{display:flex;justify-content:space-between;gap:12px;align-items:center}.avatars{display:flex;gap:10px;overflow-x:auto;padding:12px 0 4px}.avatar-card{width:118px;min-width:118px}.avatar{width:92px;height:92px;border-radius:12px;border:1px solid #7082a9;background:linear-gradient(145deg,#3a4764,#222c45);object-fit:cover}.avatar-fallback{display:flex;align-items:center;justify-content:center;font-size:28px;font-weight:800}.avatar-card b,.avatar-card small{display:block}.avatar-card small{color:var(--muted)}.change{font-size:12px;color:var(--muted)}.damage-panels{display:grid;gap:12px}.damage-panel{padding:18px}.damage-row{display:grid;grid-template-columns:180px 1fr 120px 110px;align-items:center;gap:12px;margin:9px 0}.bar-track{height:11px;background:#27334e;border-radius:6px;overflow:hidden}.bar{height:100%;min-width:6px;border-radius:6px;background:var(--gold)}.source-status{font-size:12px}.trusted{color:var(--green)}.degraded{color:var(--warn)}table{width:100%;border-collapse:collapse}th,td{padding:9px 8px;border-bottom:1px solid #2b3a5d;text-align:right;vertical-align:top}th{color:#b5c2db;font-size:12px}th:first-child,td:first-child{text-align:left}.table-wrap{overflow-x:auto;padding:8px 16px 16px}.missing-value{color:var(--warn)}.status-perfect{color:var(--green)}.status-not-perfect{color:var(--bad)}details{border:1px solid #2e4068;border-radius:9px;background:#111a30;margin-top:10px}summary{padding:9px 12px;cursor:pointer;color:#cdd8ee}details>div{padding:0 12px 12px}.node-extra{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px;margin-top:10px}.node-extra div{padding:8px;background:#192440;border-radius:7px}.node-extra small{display:block;color:var(--muted)}.uncertainty{padding:18px}.uncertainty li{margin:5px 0}.footer{margin-top:24px;color:#7f8ba5;font-size:12px}.exact-table{font-size:12px}.print-only{display:none}@media(max-width:850px){.metrics,.charts,.plane-grid,.fixed-grid{grid-template-columns:1fr 1fr}.evaluation{grid-template-columns:1fr}.node-extra{grid-template-columns:1fr 1fr}.damage-row{grid-template-columns:130px 1fr 90px}.damage-row .source-status{display:none}}@media(max-width:560px){main{width:min(100% - 16px,1220px)}.metrics,.charts,.plane-grid,.fixed-grid{grid-template-columns:1fr}.hero h1{font-size:32px}.node-extra{grid-template-columns:1fr}}@media print{body{background:#fff;color:#111}.panel,.metric,.grade,.fixed,.formation,.damage-panel,details{background:#fff;border-color:#c7cbd2;color:#111}.muted,.chart-note,.change,.avatar-card small,.footer{color:#555}.print-only{display:block}.screen-only{display:none}main{width:100%;margin:0}.hero{background:#fff}.hero:after{display:none}h2{break-after:avoid}.damage-panel,.formation,.plane{break-inside:avoid}}
</style>
</head>
<body><main>
""");
        AppendHero(builder, document);
        AppendEvaluation(builder, document.OverallEvaluation);
        AppendCharts(builder, document);
        AppendPlanes(builder, document);
        AppendFixedFacts(builder, document.FixedFacts);
        AppendFormations(builder, document.Nodes);
        AppendDamageComposition(builder, document.Nodes);
        AppendNodeTable(builder, document.Nodes);
        AppendEvidenceAndExtensions(builder, document);
        builder.Append("<div class=\"footer\">报告由货币战争小助手离线生成。原始截图是否保留取决于用户设置；结构化分析与最终封存文件保留在对局目录。本报告不会把缺失值视为 0。<br>交流QQ群：726898246 · 官网：https://taskflowai.cn</div></main></body></html>");
        return builder.ToString();
    }

    private static void AppendHero(
        StringBuilder builder,
        ChallengeReportDocument document)
    {
        var bestDamage = document.Nodes
            .Where(item => item.FinalDamage.HasValue && item.Battle?.IsRewardNode != true)
            .OrderByDescending(item => item.FinalDamage)
            .FirstOrDefault();
        var finalHealth = KnownInt(document.Run.LastSnapshot?.Health);
        builder.Append("<header class=\"hero panel\"><div class=\"eyebrow\">货币战争 · 挑战总结</div><h1>")
            .Append(H(string.IsNullOrWhiteSpace(document.Run.RatingText)
                ? "挑战完成"
                : $"挑战完成 · {document.Run.RatingText}"))
            .Append("</h1><div class=\"muted\">")
            .Append(H(document.Run.RunId)).Append(" · ")
            .Append(H(document.CoverageText)).Append(" · ")
            .Append(H(document.Run.CompletedAt == DateTimeOffset.MinValue
                ? "完成时间未记录"
                : document.Run.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)))
            .Append("</div><div class=\"metrics\">");
        AppendMetric(builder, "有效最高伤害", bestDamage is null ? "未记录" : FormatDamageCompact(bestDamage.FinalDamage), bestDamage?.NodeId ?? "无可信节点", "gold");
        AppendMetric(builder, "最终生命", FormatNullable(finalHealth), "挑战结束时", "pink");
        AppendMetric(builder, "战斗结果覆盖", $"{document.TrustedBattleCount} / {document.BattleCount}", "可信 / 已记录", "cyan");
        AppendMetric(
            builder,
            "数据状态",
            document.Run.IsFinal ? "已封存" : "未封存",
            document.Run.IsFinal
                ? "已封存，不再修改"
                : document.ExtensionFields.Count == 0
                    ? "当前格式"
                    : $"含 {document.ExtensionFields.Count} 个扩展字段",
            document.Run.IsFinal ? "green" : "warn");
        builder.Append("</div></header>");
    }

    private static void AppendMetric(
        StringBuilder builder,
        string label,
        string value,
        string note,
        string color) => builder
        .Append("<div class=\"metric\"><span class=\"muted\">")
        .Append(H(label)).Append("</span><b class=\"").Append(color).Append("\">")
        .Append(H(value)).Append("</b><small>").Append(H(note)).Append("</small></div>");

    private static void AppendEvaluation(
        StringBuilder builder,
        ChallengeReportEvaluation evaluation)
    {
        builder.Append("<h2>挑战评价</h2><section class=\"panel evaluation\"><div class=\"grade\"><strong>")
            .Append(H(evaluation.HasEnoughData ? "综合评价" : "评价暂缓"))
            .Append("</strong><span class=\"muted\">")
            .Append(H(evaluation.HasEnoughData ? evaluation.Tone : "数据不足"))
            .Append("</span></div><div><h3>").Append(H(evaluation.Title))
            .Append("</h3><p class=\"muted\">").Append(H(evaluation.Summary))
            .Append("</p><div class=\"evidence-chips\">");
        foreach (var item in evaluation.Evidence)
        {
            builder.Append("<span class=\"chip\">").Append(H(item)).Append("</span>");
        }

        builder.Append("</div></div></section>");
    }

    private static void AppendCharts(
        StringBuilder builder,
        ChallengeReportDocument document)
    {
        builder.Append("<h2>核心趋势</h2><section class=\"charts\">");
        AppendChart(builder, "最终伤害", document.Nodes
            .Select(item => new ChartPoint(item.NodeId, item.Battle?.IsRewardNode == true ? null : item.FinalDamage))
            .ToArray(), "#f3c65f", allowLogarithmic: true, "奖励关不进入伤害趋势");
        AppendChart(builder, "理论出伤极限", document.Nodes
            .Select(item => new ChartPoint(item.NodeId, item.Battle?.IsRewardNode == true ? null : item.Battle?.TheoreticalDamageLimit))
            .ToArray(), "#bc8cff", allowLogarithmic: true, "估算值在节点明细中标注");
        AppendChart(builder, "战斗结束剩余行动值", document.Nodes
            .Select(item => new ChartPoint(item.NodeId, item.Battle?.RemainingActionValue?.TotalActionValue))
            .ToArray(), "#6ed8ff", allowLogarithmic: false, "使用校正后的最终值");
        AppendChart(builder, "出战前金币", document.Nodes
            .Select(item => new ChartPoint(item.NodeId, item.Gold))
            .ToArray(), "#8fdcb2", allowLogarithmic: false, "节点最终备战快照");
        builder.Append("</section><p class=\"chart-note\">空心圆表示未记录；缺失点不会按 0 绘制，也不会跨点连线。</p>");
    }

    private static void AppendChart(
        StringBuilder builder,
        string title,
        IReadOnlyList<ChartPoint> points,
        string color,
        bool allowLogarithmic,
        string note)
    {
        var positive = points.Where(item => item.Value is > 0).Select(item => (double)item.Value!.Value).ToArray();
        var logarithmic = allowLogarithmic && positive.Length >= 2 && positive.Max() / positive.Min() >= 100;
        var available = points.Where(item => item.Value.HasValue && (!logarithmic || item.Value > 0)).ToArray();
        builder.Append("<article class=\"panel chart\"><div class=\"chart-head\"><h3>")
            .Append(H(title)).Append("</h3><span class=\"scale\">")
            .Append(logarithmic ? "对数尺度" : "线性尺度").Append("</span></div>");
        if (available.Length == 0)
        {
            builder.Append("<div class=\"muted\" style=\"height:210px;display:flex;align-items:center;justify-content:center\">数据不足，未绘制趋势</div>");
        }
        else
        {
            builder.Append(RenderSvg(points, color, logarithmic));
        }

        builder.Append("<div class=\"chart-note\">").Append(H(note)).Append("</div><details><summary>查看精确数据</summary><div><table class=\"exact-table\"><tbody>");
        foreach (var point in points)
        {
            builder.Append("<tr><td>").Append(H(point.Label)).Append("</td><td>")
                .Append(H(FormatDamage(point.Value))).Append("</td></tr>");
        }

        builder.Append("</tbody></table></div></details></article>");
    }

    private static string RenderSvg(
        IReadOnlyList<ChartPoint> points,
        string color,
        bool logarithmic)
    {
        const double left = 74;
        const double top = 26;
        const double right = 522;
        const double bottom = 208;
        var transformed = points
            .Where(item => item.Value.HasValue && (!logarithmic || item.Value > 0))
            .Select(item => Transform(item.Value!.Value, logarithmic))
            .ToArray();
        var minimum = transformed.Min();
        var maximum = transformed.Max();
        ExpandRange(ref minimum, ref maximum);
        var builder = new StringBuilder();
        builder.Append("<svg viewBox=\"0 0 ").Append(ChartWidth).Append(' ').Append(ChartHeight)
            .Append("\" role=\"img\" aria-label=\"趋势图\">");
        for (var index = 0; index < 4; index++)
        {
            var ratio = index / 3d;
            var y = top + (bottom - top) * ratio;
            var transformedValue = maximum - (maximum - minimum) * ratio;
            var displayValue = logarithmic ? Math.Pow(10, transformedValue) : transformedValue;
            builder.Append("<line class=\"gridline\" x1=\"").Append(F(left)).Append("\" y1=\"").Append(F(y)).Append("\" x2=\"").Append(F(right)).Append("\" y2=\"").Append(F(y)).Append("\"/><text class=\"axis-label\" x=\"66\" y=\"").Append(F(y + 4)).Append("\" text-anchor=\"end\">").Append(H(FormatAxis(displayValue))).Append("</text>");
        }

        (double X, double Y)? previous = null;
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var x = points.Count <= 1 ? (left + right) / 2 : left + (right - left) * index / (points.Count - 1d);
            builder.Append("<text class=\"axis-label\" x=\"").Append(F(x)).Append("\" y=\"232\" text-anchor=\"middle\">").Append(H(point.Label)).Append("</text>");
            if (!point.Value.HasValue || logarithmic && point.Value <= 0)
            {
                builder.Append("<circle class=\"missing\" cx=\"").Append(F(x)).Append("\" cy=\"").Append(F(bottom)).Append("\" r=\"4\"/>");
                previous = null;
                continue;
            }

            var transformedValue = Transform(point.Value.Value, logarithmic);
            var yRatio = (transformedValue - minimum) / (maximum - minimum);
            var y = bottom - yRatio * (bottom - top);
            if (previous.HasValue)
            {
                builder.Append("<line x1=\"").Append(F(previous.Value.X)).Append("\" y1=\"").Append(F(previous.Value.Y)).Append("\" x2=\"").Append(F(x)).Append("\" y2=\"").Append(F(y)).Append("\" stroke=\"").Append(color).Append("\" stroke-width=\"3\"/>");
            }

            builder.Append("<circle class=\"point\" cx=\"").Append(F(x)).Append("\" cy=\"").Append(F(y)).Append("\" r=\"4.5\" fill=\"").Append(color).Append("\"/>");
            previous = (x, y);
        }

        return builder.Append("</svg>").ToString();
    }

    private static void AppendPlanes(
        StringBuilder builder,
        ChallengeReportDocument document)
    {
        builder.Append("<h2>位面总结</h2><section class=\"plane-grid\">");
        foreach (var plane in document.Planes)
        {
            builder.Append("<article class=\"panel plane\" data-plane=\"").Append(plane.Plane)
                .Append("\"><div class=\"plane-title\"><h3>第").Append(ChineseNumber(plane.Plane))
                .Append("位面</h3><span class=\"muted\">")
                .Append(plane.Nodes.Count == 0 ? "未覆盖" : H($"{plane.Nodes[0].NodeId}—{plane.Nodes[^1].NodeId}"))
                .Append("</span></div><b>").Append(H(plane.Evaluation.Title))
                .Append("</b><p class=\"muted\">").Append(H(plane.Evaluation.Summary))
                .Append("</p><div class=\"chips\">");
            foreach (var evidence in plane.Evaluation.Evidence)
            {
                builder.Append("<span class=\"chip\">").Append(H(evidence)).Append("</span>");
            }

            builder.Append("</div></article>");
        }

        builder.Append("</section>");
    }

    private static void AppendFixedFacts(
        StringBuilder builder,
        ChallengeReportFixedFacts facts)
    {
        builder.Append("<h2>对局固定构筑</h2><section class=\"fixed-grid\"><article class=\"panel fixed\"><dl><dt>投资环境</dt><dd>")
            .Append(H(facts.InvestmentEnvironment ?? "未记录"))
            .Append("</dd><dt>投资策略</dt><dd>").Append(H(ListOrUnknown(facts.InvestmentStrategies)))
            .Append("</dd><dt>敌人负面词条</dt><dd>").Append(H(ListOrUnknown(facts.NegativeAffixes)))
            .Append("</dd></dl></article><article class=\"panel fixed\"><dl><dt>特殊物品</dt><dd>")
            .Append(H(ListOrUnknown(facts.SpecialItems))).Append("</dd><dt>专家顾问</dt><dd>")
            .Append(H(ListOrUnknown(facts.ExpertAdvisors))).Append("</dd><dt>说明</dt><dd class=\"muted\">这些对局级字段只取最终封存中的有效观测；未知不会被解释为未获得。</dd></dl></article></section>");
    }

    private void AppendFormations(
        StringBuilder builder,
        IReadOnlyList<ChallengeReportNode> nodes)
    {
        builder.Append("<h2>阵容、装备与构筑变化</h2><section class=\"formation-list\">");
        HashSet<string>? previous = null;
        var rendered = 0;
        foreach (var node in nodes)
        {
            var formation = node.PreparationState?.Formation.Value;
            if (formation is not { Count: > 0 })
            {
                continue;
            }

            var current = formation.Select(item => item.CharacterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = previous is null ? [] : current.Except(previous, StringComparer.OrdinalIgnoreCase).Select(modelBuilder.CharacterName).ToArray();
            var removed = previous is null ? [] : previous.Except(current, StringComparer.OrdinalIgnoreCase).Select(modelBuilder.CharacterName).ToArray();
            builder.Append("<article class=\"panel formation\"><div class=\"formation-head\"><h3>")
                .Append(H(node.NodeId)).Append(" 出战前最终阵容</h3><span class=\"change\">")
                .Append(previous is null
                    ? "首个可用阵容快照"
                    : H($"新增：{ListOrNone(added)}；移出：{ListOrNone(removed)}"))
                .Append("</span></div><div class=\"avatars\">");
            foreach (var character in formation.OrderBy(item => item.Zone).ThenBy(item => item.SlotIndex))
            {
                var displayName = modelBuilder.CharacterName(character.CharacterId);
                var avatar = assets.GetCharacterAvatarDataUri(character.CharacterId);
                builder.Append("<div class=\"avatar-card\">");
                if (avatar is null)
                {
                    builder.Append("<div class=\"avatar avatar-fallback\">").Append(H(Initial(displayName))).Append("</div>");
                }
                else
                {
                    builder.Append("<img class=\"avatar\" alt=\"").Append(H(displayName)).Append("\" src=\"").Append(avatar).Append("\">");
                }

                builder.Append("<b>").Append(H(displayName)).Append("</b><small>")
                    .Append(H(ZoneName(character.Zone))).Append(character.SlotIndex + 1)
                    .Append(character.StarLevel.HasValue ? $" · {character.StarLevel}星" : " · 星级未记录")
                    .Append("</small><small>装备：")
                    .Append(H(character.EquipmentIds.Count == 0 ? "未记录" : string.Join("、", character.EquipmentIds)))
                    .Append("</small></div>");
            }

            builder.Append("</div>");
            AppendNodeOperationalFields(builder, node);
            builder.Append("</article>");
            previous = current;
            rendered++;
        }

        if (rendered == 0)
        {
            builder.Append("<article class=\"panel formation muted\">没有可用的出战前最终阵容快照。</article>");
        }

        builder.Append("</section>");
    }

    private static void AppendNodeOperationalFields(
        StringBuilder builder,
        ChallengeReportNode node)
    {
        var state = node.PreparationState;
        var snapshot = node.Preparation;
        builder.Append("<div class=\"node-extra\">");
        AppendSmallField(builder, "金币", FormatNullable(node.Gold));
        AppendSmallField(builder, "金币变化", FormatSigned(node.GoldDelta));
        AppendSmallField(builder, "累计花费", FormatNullable(node.CumulativeSpend));
        AppendSmallField(builder, "花费变化", FormatSigned(node.SpendDelta));
        AppendSmallField(builder, "血量", FormatObservation(snapshot?.Health));
        AppendSmallField(builder, "敌人难度", FormatObservation(state?.EnemyDifficulty));
        AppendSmallField(builder, "利息", FormatObservation(state?.Interest));
        AppendSmallField(builder, "拆解工具", FormatObservation(state?.DismantleToolCount));
        AppendSmallField(builder, "等级 / 经验", FormatProgress(state?.PlayerProgress));
        AppendSmallField(builder, "简易装备", FormatObservationList(state?.SimpleEquipmentIds));
        AppendSmallField(builder, "当前羁绊", FormatSynergies(state?.ActiveSynergies));
        AppendSmallField(builder, "节点质量", node.Source.FinalPreparationState is null ? "仅有快照" : "含运营状态");
        builder.Append("</div>");
    }

    private void AppendDamageComposition(
        StringBuilder builder,
        IReadOnlyList<ChallengeReportNode> nodes)
    {
        builder.Append("<h2>最终伤害构成</h2><section class=\"damage-panels\">");
        var rendered = 0;
        foreach (var node in nodes.Where(item => item.Battle is not null))
        {
            var battle = node.Battle!;
            var sources = new List<DamageSource>();
            sources.AddRange(battle.CharacterDamage.Select(item => new DamageSource(
                modelBuilder.CharacterName(item.CharacterId ?? item.TemporaryId),
                item.Damage,
                item.CanDriveDecisions,
                item.FailureReason ?? "角色伤害")));
            sources.AddRange(battle.FinalSynergyDamage.Select(item => new DamageSource(
                item.SynergyId ?? item.TemporaryId ?? "未知羁绊",
                item.Damage,
                item.CanDriveDecisions,
                item.FailureReason ?? "羁绊伤害")));
            sources.AddRange(battle.FinalUnresolvedDamage.Select(item => new DamageSource(
                item.SourceId ?? item.TemporaryId,
                item.Damage,
                false,
                item.FailureReason)));
            builder.Append("<article class=\"panel damage-panel\"><div class=\"formation-head\"><h3>")
                .Append(H(node.NodeId)).Append(" · ").Append(H(FormatDamageCompact(node.FinalDamage)))
                .Append("</h3><span class=\"").Append(node.HasTrustedDamage ? "trusted" : "degraded")
                .Append("\">").Append(node.HasTrustedDamage ? "可信最终值" : "残缺/待复查")
                .Append("</span></div>");
            if (sources.Count == 0)
            {
                builder.Append("<p class=\"muted\">没有可用的角色、羁绊或未知来源伤害明细。</p>");
            }
            else
            {
                var maximum = Math.Max(1, sources.Max(item => item.Damage));
                foreach (var source in sources.OrderByDescending(item => item.Damage))
                {
                    builder.Append("<div class=\"damage-row\"><b>").Append(H(source.Name))
                        .Append("</b><div class=\"bar-track\"><div class=\"bar\" style=\"width:")
                        .Append((100d * source.Damage / maximum).ToString("0.##", CultureInfo.InvariantCulture))
                        .Append("%\"></div></div><span class=\"gold\">").Append(H(FormatDamageCompact(source.Damage)))
                        .Append("</span><span class=\"source-status ")
                        .Append(source.Trusted ? "trusted" : "degraded").Append("\">")
                        .Append(H(source.Trusted ? "已确认" : "保留候选")).Append("</span></div>");
                }
            }

            if (battle.FinalSettlementTopThree.Count > 0)
            {
                builder.Append("<details><summary>结算页前三名及双来源候选</summary><div><p>战斗画面候选：")
                    .Append(H(FormatDamage(battle.BattleScreenDamageCandidate)))
                    .Append("；结算前三合计：").Append(H(FormatDamage(battle.SettlementScreenDamageCandidate)))
                    .Append("；最终采用：").Append(H(FormatDamage(node.FinalDamage))).Append("。</p><ol>");
                foreach (var item in battle.FinalSettlementTopThree.OrderBy(item => item.Rank))
                {
                    builder.Append("<li>").Append(H(modelBuilder.CharacterName(item.CharacterId ?? item.TemporaryId)))
                        .Append("：").Append(H(FormatDamage(item.Damage))).Append("</li>");
                }

                builder.Append("</ol></div></details>");
            }

            builder.Append("<div class=\"node-extra\">");
            AppendSmallField(builder, "战前 → 战后血量", battle.HealthDepleted
                ? $"{FormatNullable(battle.PreBattleHealth)} → 已耗尽（具体变化未知）"
                : $"{FormatNullable(battle.PreBattleHealth)} → {FormatNullable(battle.PostBattleHealth)}（{FormatSigned(battle.HealthDelta)}）");
            AppendSmallField(builder, "完美通关", ClearName(battle.ClearStatus));
            AppendSmallField(builder, "基础 / 有效最大行动", $"{FormatNullable(battle.BaseMaximumActionValue)} / {FormatNullable(battle.EffectiveMaximumActionValue)}");
            AppendSmallField(builder, "确认增加行动", FormatNullable(battle.ConfirmedActionIncrease));
            AppendSmallField(builder, "理论值规则", string.IsNullOrWhiteSpace(battle.TheoreticalDamageRule) ? "未记录" : battle.TheoreticalDamageRule);
            AppendSmallField(builder, "理论值质量", battle.TheoreticalDamageQuality.ToString());
            AppendSmallField(builder, "奖励金币", FormatNullable(battle.GoldReward));
            AppendSmallField(builder, "结果状态", battle.IsComplete && battle.CanDriveDecisions ? "完整可信" : "残缺/待复查");
            builder.Append("</div>");

            builder.Append("</article>");
            rendered++;
        }

        if (rendered == 0)
        {
            builder.Append("<article class=\"panel damage-panel muted\">没有记录到最终战斗伤害。</article>");
        }

        builder.Append("</section>");
    }

    private static void AppendNodeTable(
        StringBuilder builder,
        IReadOnlyList<ChallengeReportNode> nodes)
    {
        builder.Append("<h2>节点精确数据</h2><section class=\"panel table-wrap\"><table><thead><tr><th>节点</th><th>最终伤害</th><th>来源</th><th>剩余行动</th><th>金币</th><th>金币Δ</th><th>累计花费</th><th>花费Δ</th><th>奖励</th><th>血量Δ</th><th>完美</th><th>理论极限</th><th>质量</th></tr></thead><tbody>");
        foreach (var node in nodes)
        {
            var battle = node.Battle;
            builder.Append("<tr><td>").Append(H(node.NodeId)).Append(battle?.IsRewardNode == true ? " <span class=\"chip\">奖励</span>" : string.Empty)
                .Append("</td><td>").Append(Value(FormatDamage(node.FinalDamage), node.FinalDamage.HasValue))
                .Append("</td><td>").Append(H(DamageSourceName(battle?.SelectedDamageSource)))
                .Append("</td><td>").Append(Value(FormatNullable(battle?.RemainingActionValue?.TotalActionValue), battle?.RemainingActionValue is not null))
                .Append("</td><td>").Append(Value(FormatNullable(node.Gold), node.Gold.HasValue))
                .Append("</td><td>").Append(Value(FormatSigned(node.GoldDelta), node.GoldDelta.HasValue))
                .Append("</td><td>").Append(Value(FormatNullable(node.CumulativeSpend), node.CumulativeSpend.HasValue))
                .Append("</td><td>").Append(Value(FormatSigned(node.SpendDelta), node.SpendDelta.HasValue))
                .Append("</td><td>").Append(Value(FormatNullable(battle?.GoldReward), battle?.GoldReward is not null))
                .Append("</td><td>").Append(Value(
                    battle?.HealthDepleted == true ? "下降（未知）" : FormatSigned(battle?.HealthDelta),
                    battle?.HealthDepleted == true || battle?.HealthDelta is not null))
                .Append("</td><td class=\"").Append(ClearClass(battle?.ClearStatus)).Append("\">")
                .Append(H(ClearName(battle?.ClearStatus))).Append("</td><td>")
                .Append(Value(FormatDamage(battle?.TheoreticalDamageLimit), battle?.TheoreticalDamageLimit is not null))
                .Append("</td><td>").Append(H(QualityName(battle))).Append("</td></tr>");
        }

        builder.Append("</tbody></table></section>");
    }

    private static void AppendEvidenceAndExtensions(
        StringBuilder builder,
        ChallengeReportDocument document)
    {
        builder.Append("<h2>数据质量、证据与附录</h2><section class=\"panel uncertainty\"><h3>数据缺口与冲突</h3>");
        if (document.Uncertainty.Count == 0)
        {
            builder.Append("<p class=\"muted\">本次封存没有报告额外冲突。</p>");
        }
        else
        {
            builder.Append("<ul>");
            foreach (var item in document.Uncertainty)
            {
                builder.Append("<li>").Append(H(item)).Append("</li>");
            }

            builder.Append("</ul>");
        }

        builder.Append("<details><summary>来源文件与终局证据</summary><div><ul><li>全局封存：completed-run.v1.json</li>");
        if (!string.IsNullOrWhiteSpace(document.Run.CompletionScreenshotFile))
        {
            builder.Append("<li>终局截图：").Append(H(document.Run.CompletionScreenshotFile)).Append("</li>");
        }

        foreach (var node in document.Nodes)
        {
            builder.Append("<li>").Append(H(node.NodeId)).Append("：备战=")
                .Append(H(node.Source.PreparationAnalysisFile ?? "未记录"))
                .Append("；战斗=").Append(H(node.Source.FinalBattleFile ?? "未记录"))
                .Append("；置信度=").Append(H(FormatConfidence(node.Battle?.Evidence.Confidence))).Append("</li>");
        }

        builder.Append("</ul></div></details>");
        if (document.ExtensionFields.Count > 0)
        {
            builder.Append("<details><summary>其他已记录字段（未来版本兼容）</summary><div><table class=\"exact-table\"><tbody>");
            foreach (var field in document.ExtensionFields)
            {
                builder.Append("<tr><td>").Append(H(field.Path)).Append("</td><td><code>")
                    .Append(H(field.Json)).Append("</code></td></tr>");
            }

            builder.Append("</tbody></table></div></details>");
        }

        builder.Append("</section>");
    }

    private static void AppendSmallField(
        StringBuilder builder,
        string label,
        string value) => builder.Append("<div><small>").Append(H(label))
        .Append("</small><span>").Append(H(value)).Append("</span></div>");

    private static int? KnownInt(Observation<int>? observation) =>
        observation?.Status == ObservationStatus.Known
            ? observation.Value
            : null;

    private static string FormatObservation(Observation<int>? observation) =>
        observation?.Status switch
        {
            ObservationStatus.Known => FormatNullable(observation.Value),
            ObservationStatus.Stale => "已过期",
            ObservationStatus.Conflict => "冲突",
            _ => "未记录"
        };

    private static string FormatObservationList(Observation<IReadOnlyList<string>>? observation) =>
        observation?.Status switch
        {
            ObservationStatus.Known => ListOrUnknown(observation.Value ?? []),
            ObservationStatus.Stale => $"{ListOrUnknown(observation.Value ?? [])}（已过期）",
            ObservationStatus.Conflict => "冲突",
            _ => "未记录"
        };

    private static string FormatProgress(Observation<PlayerProgressState>? observation) =>
        observation?.Status == ObservationStatus.Known && observation.Value is not null
            ? $"Lv.{observation.Value.Level} · {observation.Value.Experience}/{observation.Value.ExperienceToNextLevel}"
            : "未记录";

    private static string FormatSynergies(Observation<IReadOnlyList<ActiveSynergyState>>? observation) =>
        observation?.Status == ObservationStatus.Known && observation.Value is { Count: > 0 }
            ? string.Join("、", observation.Value.Select(item =>
                $"{item.SynergyId ?? "未知羁绊"} {item.ActiveCount?.ToString(CultureInfo.InvariantCulture) ?? "?"}"))
            : "未记录";

    private static string FormatNullable(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "未记录";

    private static string FormatSigned(int? value) => value switch
    {
        null => "未记录",
        > 0 => $"+{value.Value.ToString(CultureInfo.InvariantCulture)}",
        _ => value.Value.ToString(CultureInfo.InvariantCulture)
    };

    private static string FormatDamage(long? value) => value switch
    {
        null => "未记录",
        >= 100_000_000 => $"{value.Value / 100_000_000d:0.##}亿 ({value.Value.ToString("N0", CultureInfo.InvariantCulture)})",
        >= 10_000 => $"{value.Value / 10_000d:0.##}万 ({value.Value.ToString("N0", CultureInfo.InvariantCulture)})",
        _ => value.Value.ToString("N0", CultureInfo.InvariantCulture)
    };

    private static string FormatDamageCompact(long? value) => value switch
    {
        null => "未记录",
        >= 100_000_000 => $"{value.Value / 100_000_000d:0.##}亿",
        >= 10_000 => $"{value.Value / 10_000d:0.##}万",
        _ => value.Value.ToString("N0", CultureInfo.InvariantCulture)
    };

    private static string FormatAxis(double value)
    {
        var absolute = Math.Abs(value);
        return absolute switch
        {
            >= 100_000_000 => $"{value / 100_000_000d:0.##}亿",
            >= 10_000 => $"{value / 10_000d:0.##}万",
            >= 1_000 => $"{value / 1_000d:0.#}千",
            _ => value.ToString("0.#", CultureInfo.InvariantCulture)
        };
    }

    private static string FormatConfidence(double? confidence) =>
        confidence.HasValue ? confidence.Value.ToString("P0", CultureInfo.CurrentCulture) : "未记录";

    private static string FormatObservation<T>(Observation<T>? observation) =>
        observation?.Status.ToString() ?? "未记录";

    private static string ListOrUnknown(IReadOnlyList<string> values) =>
        values.Count == 0 ? "未记录" : string.Join("、", values);

    private static string ListOrNone(IReadOnlyList<string> values) =>
        values.Count == 0 ? "无" : string.Join("、", values);

    private static string ZoneName(FormationZone zone) => zone switch
    {
        FormationZone.Front => "前台",
        FormationZone.Back => "后台",
        FormationZone.Bench => "候补",
        _ => "未知站位"
    };

    private static string DamageSourceName(FinalDamageSelectionSource? source) => source switch
    {
        FinalDamageSelectionSource.BattleLastFrame => "战斗末帧",
        FinalDamageSelectionSource.SettlementTopThree => "结算前三",
        _ => "未记录"
    };

    private static string ClearName(NodeClearStatus? status) => status switch
    {
        NodeClearStatus.Perfect => "✓ 完美",
        NodeClearStatus.NotPerfect => "✕ 未完美",
        _ => "? 未知"
    };

    private static string ClearClass(NodeClearStatus? status) => status switch
    {
        NodeClearStatus.Perfect => "status-perfect",
        NodeClearStatus.NotPerfect => "status-not-perfect",
        _ => "missing-value"
    };

    private static string QualityName(FinalNodeBattleState? battle)
    {
        if (battle is null)
        {
            return "未记录";
        }

        var result = battle.IsComplete && battle.CanDriveDecisions ? "完整可信" : "残缺/待复查";
        result = battle.TheoreticalDamageQuality == TheoreticalDamageQuality.WalterEstimated
            ? result + "；理论值估算"
            : result;
        return string.IsNullOrWhiteSpace(battle.TheoreticalDamageRule)
            ? result
            : result + "；" + battle.TheoreticalDamageRule;
    }

    private static string Initial(string value) =>
        string.IsNullOrWhiteSpace(value) ? "?" : value[..1];

    private static string ChineseNumber(int value) => value switch
    {
        1 => "一",
        2 => "二",
        3 => "三",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static string Value(string text, bool known) => known
        ? H(text)
        : $"<span class=\"missing-value\">{H(text)}</span>";

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static double Transform(double value, bool logarithmic) =>
        logarithmic ? Math.Log10(value) : value;

    private static void ExpandRange(ref double minimum, ref double maximum)
    {
        if (Math.Abs(maximum - minimum) < 0.000_001)
        {
            var padding = Math.Max(Math.Abs(maximum) * 0.1, 1);
            minimum -= padding;
            maximum += padding;
            return;
        }

        var paddingValue = (maximum - minimum) * 0.08;
        minimum -= paddingValue;
        maximum += paddingValue;
    }

    private sealed record ChartPoint(string Label, long? Value)
    {
        public ChartPoint(string label, int? value)
            : this(label, value.HasValue ? (long?)value.Value : null)
        {
        }
    }

    private sealed record DamageSource(
        string Name,
        long Damage,
        bool Trusted,
        string Reason);
}
