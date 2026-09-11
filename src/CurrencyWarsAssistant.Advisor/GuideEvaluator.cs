// GuideEvaluator.cs —— M2：按对局快照评估攻略动作，产出 现在/下步/警示 分流（含差距量化）。
// 纪律：RunContext 仅作评估输入；输出不含任何现状罗列（只允许"差X/LvN后"式差距）。
// R5 修复轮（2026-09-06）：未知运算符/未知触发器 fail-closed；between_inclusive/contains_* 全语义；
// owned 未知不伪造缺口（P2-3）；去静态穿参（P2-4）；分支按 priority 首匹配（P2-5）；现在做置顶（P2-6）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CurrencyWarsAdvisor.GuidePlaybooks
{
    using CurrencyWarsAssistant.Advisor;
    /// <summary>对局上下文（推理输入，永不直接展示）。</summary>
    public sealed class RunContext
    {
        public int Level { get; init; } = -1;          // -1=未知
        public int Population { get; init; } = -1;
        public int Gold { get; init; } = -1;
        public HashSet<string> OwnedCharacterIds { get; init; } = new();
        /// <summary>持有集是否完整观测（上场+备战席都 Known）；false 时 owned 类条件只降级"阵容未接入"，不伪造缺口。</summary>
        public bool OwnedKnown { get; init; }
        /// <summary>当局已激活羁绊（null=未观测）。第 2 期 D：bonds 条件实接。</summary>
        public HashSet<string>? SynergyIds { get; init; }
        /// <summary>当局已选投资策略（null=未观测）。第 2 期 D：investment_strategies 条件实接。</summary>
        public HashSet<string>? InvestmentStrategyIds { get; init; }
        public string? NodeId { get; init; }
    }

    public enum CardState { ReadyNow, NeedsMore, WarningOnly }

    public static class RunContextFactory
    {
        // R6 P1-1：快照羁绊 ID 空间（"bond_中文名"/"bond:中文名"/裸中文名）→ 攻略侧 currency_wars_bond_XX。
        // 源=standard-ids/bonds.json（4.4 目录），新增羁绊须同步本表。
        private static readonly Dictionary<string, string> BondNameToId = new(StringComparer.Ordinal)
        {
            ["星核猎手"] = "currency_wars_bond_01", ["贝洛伯格"] = "currency_wars_bond_02", ["公司"] = "currency_wars_bond_03",
            ["星间旅人"] = "currency_wars_bond_04", ["巡海游侠"] = "currency_wars_bond_05", ["昼之半神"] = "currency_wars_bond_06",
            ["夜之半神"] = "currency_wars_bond_07", ["银河学者"] = "currency_wars_bond_08", ["盛会之星"] = "currency_wars_bond_09",
            ["狼狩"] = "currency_wars_bond_10", ["列车同行"] = "currency_wars_bond_11", ["仙舟"] = "currency_wars_bond_12",
            ["治疗"] = "currency_wars_bond_13", ["持续伤害"] = "currency_wars_bond_14", ["护盾"] = "currency_wars_bond_15",
            ["击破"] = "currency_wars_bond_16", ["减益"] = "currency_wars_bond_17", ["能量"] = "currency_wars_bond_18",
            ["群攻"] = "currency_wars_bond_19", ["燃血"] = "currency_wars_bond_20", ["追击"] = "currency_wars_bond_21",
            ["量子同频"] = "currency_wars_bond_22", ["战技点"] = "currency_wars_bond_23", ["救世主"] = "currency_wars_bond_24",
            ["大守护者"] = "currency_wars_bond_25", ["命运卜者"] = "currency_wars_bond_26", ["挚爱之人"] = "currency_wars_bond_27",
            ["魔术师"] = "currency_wars_bond_28", ["欢愉"] = "currency_wars_bond_29", ["头号玩家"] = "currency_wars_bond_30",
            ["命运圣杯"] = "currency_wars_bond_31", ["领航员"] = "currency_wars_bond_32", ["师徒"] = "currency_wars_bond_33",
        };

        /// <summary>快照羁绊 ID 归一到攻略侧标准空间；无法识别的原样保留（宁滥勿失，求交集时无害）。</summary>
        public static HashSet<string> NormalizeSynergyIds(IEnumerable<string> raw)
        {
            var outIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in raw)
            {
                if (s.StartsWith("currency_wars_bond_")) { outIds.Add(s); continue; }
                var name = s.StartsWith("bond_") ? s[5..] : s.StartsWith("bond:") ? s[5..] : s;
                outIds.Add(BondNameToId.TryGetValue(name, out var id) ? id : s);
            }
            return outIds;
        }

        /// <summary>从对局快照构建评估输入（Observation 未知项保持 -1/空，由评估器降级提示）。</summary>
        public static RunContext FromSnapshot(RunSnapshot s)
        {
            var owned = new HashSet<string>();
            bool boardKnown = s.BoardCharacterIds is { Status: ObservationStatus.Known } b && b.Value is not null;
            bool benchKnown = s.BenchCharacterIds is { Status: ObservationStatus.Known } be && be.Value is not null;
            if (boardKnown)
                foreach (var id in s.BoardCharacterIds.Value!) owned.Add(id);
            if (benchKnown)
                foreach (var id in s.BenchCharacterIds.Value!) owned.Add(id);
            return new RunContext
            {
                Level = s.StoreLevel is { Status: ObservationStatus.Known } l && l.Value is int lv ? lv : -1,
                // R5 P3-7（人口镜像近似·注释级口径备案）：本行用商店等级镜像人口，是近似而非观测。
                // 口径：真实人口（团队规模）≈ 基础人口（=商店等级）+ 备战席扩容特例——
                //   ① 财富宝钻：每个 +1 团队规模上限（无需穿戴）；
                //   ② 特殊单位（佩佩/叽米/姵姵等吉祥物位）：至多 3 个，每个 +1 格；
                //   ③ 硬上限 13（实证锚：10 级 + 星徽大使叽米自带 3 财富宝钻 = 13）。
                // 出处：research/basic-mechanics/xp-bench-tables.md §四/§五、mechanics-verification-report.md F3
                //（用户实机独有口径，网络未证实，禁当官方数值引用）。
                // 误判方向：宝钻/特殊单位多的局真实人口 > 等级，本镜像低估人口 →
                //   on_population_threshold / population greater_or_equal 类条件会误报"差 X 人口"或滞后解锁
                //   （M4 已备案"特殊单位+1格/个罕见"，本班 F3 实证并非罕见，R5 P3-7 正式登记）。
                // TODO(人口字段)：RunSnapshot 无独立人口字段；待快照/OCR 链路新增 Observation<int> Population
                //   （接入点见 Contracts.RunSnapshot.StoreLevel 处注释）后，本行改读真实字段，删除镜像。
                Population = s.StoreLevel is { Status: ObservationStatus.Known } l2 && l2.Value is int lv2 ? lv2 : -1,
                Gold = s.Economy is { Status: ObservationStatus.Known } g && g.Value is int gv ? gv : -1,
                NodeId = s.Stage is { Status: ObservationStatus.Known } st ? st.Value : null,
                OwnedCharacterIds = owned,
                OwnedKnown = boardKnown && benchKnown, // 只有一侧 Known 视为不完整，保守按未接入处理
                SynergyIds = s.SynergyIds is { Status: ObservationStatus.Known } sy && sy.Value is not null
                    ? NormalizeSynergyIds(sy.Value) : null, // R6 P1-1：快照名空间归一为 currency_wars_bond_XX
                InvestmentStrategyIds = s.InvestmentStrategyIds is { Status: ObservationStatus.Known } iv && iv.Value is not null
                    ? new HashSet<string>(iv.Value) : null,
            };
        }
    }

    public sealed record EvaluatedCard(SuggestionCard Card, CardState State, string? Gap)
    {
        public string? Evidence { get; init; }
        public string Headline => State == CardState.ReadyNow ? Card.Title
                                 : State == CardState.NeedsMore ? $"{Card.Title}（{Gap}）"
                                 : "⛔ " + Card.Title;
    }

    public static class GuideEvaluator
    {
        /// <summary>M4：按持有角色与攻略 signals 的重合度自动推荐攻略（核心×3/可选×1 加权）。
        /// 第 2 期 B：账号前置（applicability 的 owned 类条件）不满足的攻略不参与推荐。
        /// P2（审计 09-11）：持有集未完整观测（OwnedKnown=false）时不做推荐——旧实现全库同分
        /// 静默返回输入序第一册。返回 null=无可推荐（调用方须声明"阵容未接入"类原因）。</summary>
        public static (GuidePlaybook? Pb, int Score) BestMatch(
            IEnumerable<GuidePlaybook> guides, RunContext ctx)
        {
            GuidePlaybook? best = null; int bestScore = 0;
            if (!ctx.OwnedKnown) return (null, 0); // 阵容未观测：推荐无意义，绝不静默返首册
            foreach (var pb in guides)
            {
                if (ApplicabilityGaps(pb, ctx).Gaps.Count > 0) continue; // 账号前置未满足：排除出推荐
                int score = 0;
                if (pb.Signals is { } s)
                {
                    score += 3 * s.CoreCharacterIds.Count(ctx.OwnedCharacterIds.Contains)
                           + 1 * s.OptionalCharacterIds.Count(ctx.OwnedCharacterIds.Contains);
                }
                if (best is null || score > bestScore) { bestScore = score; best = pb; }
            }
            return (best, bestScore);
        }

        /// <summary>第 2 期 A：按当前节点定位阶段（NodeId "P-N" 解析；nodeIds 精确>nodeRanges>仅 planeIds；nodeTypes 无数据源不参与）。</summary>
        public static string? MatchPhaseId(GuidePlaybook pb, string? nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            var ordered = pb.Phases.OrderBy(p => p.Order ?? 999).ToList();
            foreach (var ph in ordered) // 1) nodeIds 精确命中
                if (ph.Selector?.NodeIds.Contains(nodeId) == true) return ph.PhaseId;
            var parts = nodeId.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var plane) && int.TryParse(parts[1], out var idx))
            {
                foreach (var ph in ordered) // 2) nodeRanges 区间命中
                    if (ph.Selector is { } sel && sel.PlaneIds.Contains(plane)
                        && sel.NodeRanges.Any(r => r.PlaneId == plane && idx >= r.FromNodeIndex && idx <= r.ToNodeIndex))
                        return ph.PhaseId;
                foreach (var ph in ordered) // 3) 仅 planeIds 限定（无更细锚点）
                    if (ph.Selector is { } sel2 && sel2.PlaneIds.Contains(plane)
                        && sel2.NodeRanges.Count == 0 && sel2.NodeIds.Count == 0)
                        return ph.PhaseId;
            }
            return null;
        }

        /// <summary>第 2 期 B：账号前置评估。Gaps=不满足项（阻断推荐）；Unverified=无数据源无法验证的字段（只提示不阻断）。</summary>
        public static (IReadOnlyList<string> Gaps, IReadOnlyList<string> Unverified) ApplicabilityGaps(GuidePlaybook pb, RunContext? ctx)
        {
            var gaps = new List<string>();
            var unverified = new List<string>();
            if (pb.Applicability?.Required is not { Count: > 0 } req) return (gaps, unverified);
            if (ctx is null) { unverified.Add("未接对局数据"); return (gaps, unverified); }
            foreach (var c in req.Select(ToStub))
            {
                if (c is null) continue;
                switch (c.Field)
                {
                    case "investment_strategies":
                    case "bonds": // 当局已激活/已选，快照可验证 → 阻断
                        // P1（审计 09-11）修复：字段未观测（null）≠ 条件不满足——未观测归 Unverified
                        // 不阻断（与 owned_characters 同口径）；仅在已观测且条件确实不满足时才阻断。
                        var set = c.Field == "bonds" ? ctx.SynergyIds : ctx.InvestmentStrategyIds;
                        if (set is null)
                        {
                            unverified.Add(c.Field == "bonds" ? "已激活羁绊（未观测，不阻断）" : "已选投资策略（未观测，不阻断）");
                            break;
                        }
                        var g = EvalCondition(c, ctx);
                        if (g is not null) gaps.Add(g);
                        break;
                    case "owned_characters":
                        // R6 P2-1：applicability 的 owned 指"账号持有"，当局持有是错误近似（前中期会错杀推荐）——
                        // 账号持有无快照数据源，归 Unverified 不阻断，文案明示。
                        unverified.Add("账号持有（当局阵容无法验证）");
                        break;
                    default:
                        unverified.Add(c.Field); // gameVersion 等无快照数据源：不阻断，只标注
                        break;
                }
            }
            return (gaps, unverified);
        }

        /// <summary>评估一个阶段的动作卡。ctx=null 时全部按 NeedsMore("未接对局数据")。</summary>
        public static IReadOnlyList<EvaluatedCard> EvaluatePhase(GuidePlaybook pb, string phaseId, RunContext? ctx)
        {
            var outCards = new List<EvaluatedCard>();
            foreach (var raw in pb.ProjectPhase(phaseId))
            {
                if (raw.Verb == "warn" || raw.Gated)
                {
                    outCards.Add(new EvaluatedCard(raw, CardState.WarningOnly, null));
                    continue;
                }
                var a = pb.FindAction(raw.ActionId);
                if (a is null) { outCards.Add(new EvaluatedCard(raw, CardState.WarningOnly, null)); continue; }
                string? gap = Evaluate(a, ctx, pb.Branches);
                var ev = (a.EvidenceRefs is { Count: > 0 } ers)
                    ? "证据: " + string.Join("; ", ers.Take(2).Select(r => $"{r.EvidenceSetId}/{r.ClaimId}"))
                    : null;
                outCards.Add(gap is null
                    ? new EvaluatedCard(raw, CardState.ReadyNow, null) { Evidence = ev }
                    : new EvaluatedCard(raw, CardState.NeedsMore, gap) { Evidence = ev });
            }
            // P2-6：设计文档档位序=①现在做(绿,优先级降序)→②下步准备→③/④警示。ReadyNow=0 在最前。
            return outCards.OrderBy(c => (int)c.State).ThenByDescending(c => c.Card.Priority).ToList();
        }

        /// <summary>null=可现在做；否则返回差距描述（永不罗列现状）。</summary>
        private static string? Evaluate(Action a, RunContext? ctx, IReadOnlyList<Branch> branches)
        {
            if (ctx is null) return "未接对局数据";
            var bg = EvaluateBranchGate(a, ctx, branches);
            if (bg is not null) return bg;
            // 1) trigger（P1-2 收尾：参数缺失/未知触发器一律门控，绝不放行）
            switch (a.Trigger)
            {
                case null:
                case "manual":
                    break;
                case "on_level_threshold":
                    if (ArgInt(a, "level") is not int l1) return "触发参数未接入";
                    if (ctx.Level < 0) return "等级未接入";
                    if (ctx.Level < l1) return $"Lv{l1} 后";
                    break;
                case "on_population_threshold":
                    if (ArgInt(a, "population") is not int p1) return "触发参数未接入";
                    if (ctx.Population < 0) return "人口未接入";
                    if (ctx.Population < p1) return $"{p1} 人口后";
                    break;
                case "on_gold_threshold":
                    if (ArgInt(a, "gold") is not int g1) return "触发参数未接入";
                    if (ctx.Gold < 0) return "金币未接入";
                    if (ctx.Gold < g1) return $"差 {g1 - ctx.Gold} 金";
                    break;
                default:
                    return "触发未接入"; // on_node_enter/on_affix_seen/on_equipment_acquired 等：接线前 fail-closed
            }
            // 2) conditions
            foreach (var c in a.Conditions.Select(ToStub))
            {
                if (c is null) continue;
                var g2 = EvalCondition(c, ctx);
                if (g2 is not null) return g2;
            }
            return null;
        }

        /// <summary>测试入口：供回归测试直调分支门控（prod 走 EvaluatePhase）。</summary>
        public static string? EvaluateBranchGateForTest(GuidePlaybook pb, string actionId, RunContext ctx)
        {
            var action = pb.FindAction(actionId) ?? new Action { ActionId = actionId };
            return EvaluateBranchGate(action, ctx, pb.Branches);
        }

        /// <summary>P2-5：分支按 priority 升序、首个 when 全满足者胜（schema 语义）；全不满足才输出最高优先级分支的差距。
        /// P1（审计 09-11）修复：①referenced 同时认 then 与 otherwise 引用（原来 otherwise 引用的动作完全绕过门控）；
        /// ②方向修正——选中分支的 otherwise 动作应被压制（then/else 语义：条件成立走 then），旧实现反而放行；
        /// 全部 when 不满足时 otherwise 动作按 else 语义可做，then 动作被最高优先级差距门控。</summary>
        private static string? EvaluateBranchGate(Action a, RunContext ctx, IReadOnlyList<Branch> branches)
        {
            if (branches.Count == 0) return null;
            bool referenced = branches.Any(b => b.ThenActionIds.Contains(a.ActionId)
                                                || b.OtherwiseActionIds.Contains(a.ActionId));
            if (!referenced) return null; // 动作不在任何分支里：不受分支门控
            Branch? selected = null;
            string? topGap = null; // 全不满足时最高优先级分支的差距
            var unknownHit = false; // P2（终审）：条件未知≠条件为假——未知存在时禁止 otherwise 放行（fail-closed）
            foreach (var br in branches.OrderBy(b => b.Priority))
            {
                bool ok = true;
                string? gap = null;
                foreach (var w in br.When)
                {
                    var stub = ToStub(w);
                    if (stub is null) { ok = false; gap ??= "分支条件未接入"; unknownHit = true; continue; }
                    var g = EvalCondition(stub, ctx);
                    if (g is not null)
                    {
                        ok = false; gap ??= g;
                        if (g.Contains("未接入") || g.Contains("未观测")) unknownHit = true;
                    }
                }
                if (ok) { selected = br; break; } // 首个匹配分支胜出，低优先级分支不再评估
                topGap ??= gap;
            }
            if (selected is null)
            {
                // 全部 when 不满足：then 动作被门控。otherwise 动作按 else 语义可做——
                // 但分支条件存在"未知/未观测"时无法确证 when 为假，otherwise 一律门控（fail-closed）。
                if (branches.Any(b => b.ThenActionIds.Contains(a.ActionId)))
                    return (topGap ?? "等待分支条件") + "（分支）";
                return unknownHit ? (topGap ?? "分支条件未接入") + "（分支·条件未知）" : null;
            }
            if (selected.ThenActionIds.Contains(a.ActionId)) return null; // 选中分支的 then：可做
            return $"分支 {selected.BranchId} 优先命中（otherwise 抑制）（分支）"; // 选中分支压制 otherwise 与未选中分支动作
        }

        private static string? EvalCondition(ConditionStub c, RunContext ctx)
        {
            switch (c.Field)
            {
                case "level" when ctx.Level >= 0:
                    return EvalNum(ctx.Level, c, "级");
                case "population" when ctx.Population >= 0:
                    return EvalNum(ctx.Population, c, "人口");
                case "gold" when ctx.Gold >= 0:
                    return EvalNum(ctx.Gold, c, "金");
                case "owned_characters":
                    if (!ctx.OwnedKnown) return "阵容未接入"; // P2-3：持有集未完整观测时不伪造"缺 X"
                    if (c.Values.Count == 0) return "条件参数未接入";
                    switch (c.Op)
                    {
                        case "contains_all":
                            if (!c.Values.IsSubsetOf(ctx.OwnedCharacterIds))
                                return "缺 " + string.Join("、", c.Values.Except(ctx.OwnedCharacterIds));
                            break;
                        case "contains_any":
                            if (!c.Values.Overlaps(ctx.OwnedCharacterIds)) return "未持有目标角色";
                            break;
                        case "contains_none":
                            if (c.Values.Overlaps(ctx.OwnedCharacterIds))
                                return "已持有 " + string.Join("、", c.Values.Intersect(ctx.OwnedCharacterIds)) + "（需未持有）";
                            break;
                        default:
                            return "运算符未接入";
                    }
                    break;
                case "bonds":
                case "investment_strategies":
                    {
                        var set = c.Field == "bonds" ? ctx.SynergyIds : ctx.InvestmentStrategyIds; // 第 2 期 D：两字段实接（null=未观测走保守降级）
                        if (set is null) return "字段未接入";
                        if (c.Values.Count == 0) return "条件参数未接入";
                        switch (c.Op)
                        {
                            case "contains_all":
                                if (!c.Values.IsSubsetOf(set)) return "未开 " + string.Join("、", c.Values.Except(set));
                                break;
                            case "contains_any":
                                if (!c.Values.Overlaps(set)) return "未开目标羁绊/策略";
                                break;
                            case "contains_none":
                                if (c.Values.Overlaps(set)) return "已开 " + string.Join("、", c.Values.Intersect(set)) + "（需未开）";
                                break;
                            default:
                                return "运算符未接入";
                        }
                        break;
                    }
                default:
                    return "字段未接入"; // kafka_stars/acquired_items 等：保守降级（require_review 口径）
            }
            return null;
        }

        private sealed record ConditionStub(string Field, string Op, double Num, double Min, double Max, bool HasRange, HashSet<string> Values);

        private static ConditionStub? ToStub(Condition c)
        {
            double num = double.NaN;
            var vals = new HashSet<string>();
            bool hasRange = false; double min = 0, max = 0;
            if (c.Expected.ValueKind == JsonValueKind.Number) num = c.Expected.GetDouble();
            else if (c.Expected.ValueKind == JsonValueKind.Array)
            {
                var arr = c.Expected.EnumerateArray().ToList();
                if (c.Op == "between_inclusive" && arr.Count == 2
                    && arr[0].ValueKind == JsonValueKind.Number && arr[1].ValueKind == JsonValueKind.Number)
                {
                    min = arr[0].GetDouble(); max = arr[1].GetDouble();
                    if (min > max) (min, max) = (max, min);
                    hasRange = true;
                }
                else foreach (var v in arr) vals.Add(v.ToString());
            }
            else vals.Add(c.Expected.ToString());
            return new ConditionStub(c.Field, c.Op, num, min, max, hasRange, vals);
        }

        private static int? ArgInt(Action a, string key)
            => a.TriggerArgs is { } args && args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
               ? v.GetInt32() : (int?)null;

        private static bool NumOk(double cur, string op, double target) => op switch
        {
            "greater_or_equal" => cur >= target,
            "greater_than" => cur > target,
            "less_or_equal" => cur <= target,
            "less_than" => cur < target,
            "equals" => Math.Abs(cur - target) < 0.001,
            _ => false, // R5 P1-2：未收录运算符一律不满足（fail-closed，违反则显示条件未达而非放行）
        };

        /// <summary>数值条件统一入口：between_inclusive 走区间；其余走 NumOk（未知运算符 fail-closed）。</summary>
        private static string? EvalNum(double cur, ConditionStub c, string unit)
        {
            if (c.HasRange)
            {
                if (c.Op != "between_inclusive") return "运算符未接入";
                if (cur < c.Min) return $"差 {c.Min - cur:0.#} {unit}";
                if (cur > c.Max) return $"超出 {cur - c.Max:0.#} {unit}";
                return null;
            }
            return NumOk(cur, c.Op, c.Num) ? null : Gap(unit, c.Num, cur, c.Op);
        }

        // P3-5：equals 在 cur<target 时说"差X"（原实现误走"超出0"）。
        private static string Gap(string unit, double target, double cur, string op) =>
            op.Contains("greater") || op == "equals"
                ? $"差 {Math.Max(0, target - cur):0.#} {unit}"
                : $"超出 {Math.Max(0, cur - target):0.#} {unit}";
    }
}
