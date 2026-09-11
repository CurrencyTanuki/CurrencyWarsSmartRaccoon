// GuidePlaybook.cs —— guide-playbook v1/v1.1/v1.2 攻略文件加载与面板投影（M1 静态渲染用）。
// 纪律：攻略文件仅是建议数据源；对局状态（金/级/人口/阵容）只作评估输入，永不在面板重复展示。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurrencyWarsAdvisor.GuidePlaybooks
{
    /// <summary>攻略文件（guide-playbook v1 系）。未知字段忽略不报错（兼容未来小版本）。</summary>
    public sealed class GuidePlaybook
    {
        [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "";
        [JsonPropertyName("guideId")] public string GuideId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "draft";
        [JsonPropertyName("applicableGameVersions")] public List<string> ApplicableGameVersions { get; set; } = new();
        [JsonPropertyName("archetypeName")] public string ArchetypeName { get; set; } = "";
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();

        [JsonPropertyName("signals")] public Signals? Signals { get; set; }
        [JsonPropertyName("applicability")] public Applicability? Applicability { get; set; }
        [JsonPropertyName("phases")] public List<Phase> Phases { get; set; } = new();
        [JsonPropertyName("actions")] public List<Action> Actions { get; set; } = new();
        [JsonPropertyName("branches")] public List<Branch> Branches { get; set; } = new();
        [JsonPropertyName("alternativeRoutes")] public List<AlternativeRoute> AlternativeRoutes { get; set; } = new();
        [JsonPropertyName("risks")] public List<Risk> Risks { get; set; } = new();
        [JsonPropertyName("craftingPaths")] public List<CraftingPath> CraftingPaths { get; set; } = new();
        [JsonPropertyName("guideCodes")] public List<GuideCode> GuideCodes { get; set; } = new();

        /// <summary>白班工单#2：加载时发现的未建模字段路径（如 versionApplicability、actions[x].triggerArgs 外新字段）。
        /// 不报错（兼容未来小版本），但不再静默丢弃——面板/调用方可展示告警。</summary>
        public List<string> LoadWarnings { get; } = new();

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        public static GuidePlaybook Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var pb = doc.Deserialize<GuidePlaybook>(Options)
                     ?? throw new InvalidDataException($"攻略文件反序列化为空: {path}");
            if (string.IsNullOrEmpty(pb.GuideId) || pb.Actions.Count == 0)
                throw new InvalidDataException($"攻略文件缺少 guideId 或 actions: {path}");
            CollectUnknownFields(doc.RootElement, pb);
            return pb;
        }

        // v1.2 schema 各实体合法属性集（手工镜像自 standard/guide-playbook.v1.1.schema.json，schema 升版须同步）
        private static readonly HashSet<string> RootProps = new(StringComparer.Ordinal)
            { "actions", "alternativeRoutes", "applicability", "applicableGameVersions", "archetypeId", "archetypeName",
              "branches", "craftingPaths", "evidenceRefs", "excludedContent", "goalIds", "guideCodes", "guideId",
              "missingInformationPolicy", "notes", "phases", "risks", "schemaVersion", "signals", "sourceRefs",
              "status", "title", "versionApplicability" };
        private static readonly HashSet<string> SignalsProps = new(StringComparer.Ordinal)
            { "bondIds", "coreCharacterIds", "equipmentIds", "investmentEnvironmentIds", "investmentStrategyIds", "optionalCharacterIds" };
        private static readonly HashSet<string> PhaseProps = new(StringComparer.Ordinal)
            { "actionIds", "evidenceRefs", "notes", "order", "phaseId", "recommendedState", "selector", "shoppingPlan", "title" };
        private static readonly HashSet<string> SelectorProps = new(StringComparer.Ordinal)
            { "nodeIds", "nodeRanges", "nodeTypes", "planeIds" };
        private static readonly HashSet<string> ActionProps = new(StringComparer.Ordinal)
            { "actionId", "benefits", "conditions", "costs", "evidenceRefs", "fallbackActionIds", "instruction",
              "invalidatesWhen", "operationType", "preconditions", "priority", "risks", "targetCharacterIds",
              "title", "trigger", "triggerArgs" };
        private static readonly HashSet<string> BranchProps = new(StringComparer.Ordinal)
            { "branchId", "evidenceRefs", "otherwiseActionIds", "priority", "thenActionIds", "transitionToPhaseId", "when" };
        private static readonly HashSet<string> ShoppingPlanProps = new(StringComparer.Ordinal)
            { "costFocus", "evidenceRefs", "keepGoldAtLeast", "notes", "priorityCharacterIds", "refreshBound", "refreshPolicy", "spendDownToGold", "stopWhen", "targetLevel" };
        private static readonly HashSet<string> ApplicabilityProps = new(StringComparer.Ordinal)
            { "defaultUnknownPolicy", "prohibited", "required" };
        // C# 根模型已建模字段（与上方 schema 集的差集=当前被丢弃的 v1.2 能力，白班逐步补齐后从此集移出告警）
        private static readonly HashSet<string> ModeledRootProps = new(StringComparer.Ordinal)
            { "schemaVersion", "guideId", "title", "status", "applicableGameVersions", "archetypeName", "notes",
              "signals", "applicability", "phases", "actions", "branches", "alternativeRoutes", "risks", "craftingPaths", "guideCodes" };

        private static void CollectUnknownFields(JsonElement root, GuidePlaybook pb)
        {
            // 根级双查：schema 外字段（拼写漂移）+ schema 内但 C# 模型未建模（如 applicability/versionApplicability，当前被静默丢弃）
            foreach (var prop in root.EnumerateObject())
            {
                if (!RootProps.Contains(prop.Name))
                    pb.LoadWarnings.Add(prop.Name + "（schema 外字段）");
                else if (!ModeledRootProps.Contains(prop.Name))
                    pb.LoadWarnings.Add(prop.Name + "（schema 内未建模）");
            }
            if (root.TryGetProperty("signals", out var sig)) Scan(sig, SignalsProps, "signals", pb);
            // R6 P3-3：已建模容器的二层扫描（recommendedState/shoppingPlan/applicability 内部不再静默丢弃）
            if (root.TryGetProperty("phases", out var phases) && phases.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < phases.GetArrayLength(); i++)
                {
                    var p = phases[i];
                    var pid = p.TryGetProperty("phaseId", out var id) && !string.IsNullOrEmpty(id.GetString()) ? id.GetString() : $"#{i}";
                    Scan(p, PhaseProps, $"phases[{pid}]", pb);
                    if (p.TryGetProperty("selector", out var sel)) Scan(sel, SelectorProps, $"phases[{pid}].selector", pb);
                    if (p.TryGetProperty("shoppingPlan", out var sp)) Scan(sp, ShoppingPlanProps, $"phases[{pid}].shoppingPlan", pb);
                }
            if (root.TryGetProperty("applicability", out var app)) Scan(app, ApplicabilityProps, "applicability", pb);
            if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < actions.GetArrayLength(); i++)
                {
                    var a = actions[i];
                    var aid = a.TryGetProperty("actionId", out var id) && !string.IsNullOrEmpty(id.GetString()) ? id.GetString() : $"#{i}";
                    Scan(a, ActionProps, $"actions[{aid}]", pb);
                }
            if (root.TryGetProperty("branches", out var branches) && branches.ValueKind == JsonValueKind.Array)
                for (int i = 0; i < branches.GetArrayLength(); i++)
                {
                    var b = branches[i];
                    var bid = b.TryGetProperty("branchId", out var id) && !string.IsNullOrEmpty(id.GetString()) ? id.GetString() : $"#{i}";
                    Scan(b, BranchProps, $"branches[{bid}]", pb);
                }
        }

        private static void Scan(JsonElement obj, HashSet<string> known, string path, GuidePlaybook pb)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in obj.EnumerateObject())
                if (!known.Contains(prop.Name))
                    pb.LoadWarnings.Add(string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}");
        }

        public Action? FindAction(string id) => Actions.FirstOrDefault(a => a.ActionId == id);
        public Phase? FindPhase(string id) => Phases.FirstOrDefault(p => p.PhaseId == id);

        /// <summary>面板投影：一个攻略阶段的静态建议卡集合（M1 无评估，按 priority 降序）。</summary>
        public IReadOnlyList<SuggestionCard> ProjectPhase(string phaseId)
        {
            var phase = FindPhase(phaseId) ?? throw new KeyNotFoundException($"phaseId 不存在: {phaseId}");
            var cards = new List<SuggestionCard>();
            foreach (var aid in phase.ActionIds)
            {
                var a = FindAction(aid);
                if (a is null) continue; // 引用悬空在离线校验器拦截，运行时跳过
                cards.Add(new SuggestionCard(
                    ActionId: a.ActionId,
                    Verb: a.OperationType ?? "other",
                    Title: a.Title,
                    Instruction: a.Instruction,
                    Priority: a.Priority,
                    Targets: a.TargetCharacterIds,
                    Fallbacks: a.FallbackActionIds,
                    Gated: a.IsHighRisk));
            }
            // 禁区卡：该阶段相关的 excluded/风险知识（risks 全局挂靠，M3 再做阶段归属）。
            // R5 P2-6/P3-3：按 severity 降序取前 3（原文件序会把高severity挤掉）；卡片 ID 用 risk-{riskId} 稳定化。
            foreach (var r in Risks.OrderByDescending(r => SeverityRank(r.Severity)).Take(3))
                cards.Add(SuggestionCard.Warning(r.RiskId, r.Description, r.Severity));
            return cards.OrderByDescending(c => c.Priority).ToList();
        }

        private static int SeverityRank(string severity) => severity switch
        {
            "high" => 0,
            "medium" => 1,
            "low" => 2,
            _ => 3,
        };
    }

    public sealed class Signals
    {
        [JsonPropertyName("coreCharacterIds")] public List<string> CoreCharacterIds { get; set; } = new();
        [JsonPropertyName("optionalCharacterIds")] public List<string> OptionalCharacterIds { get; set; } = new();
        [JsonPropertyName("equipmentIds")] public List<string> EquipmentIds { get; set; } = new();
        [JsonPropertyName("bondIds")] public List<string> BondIds { get; set; } = new();
        [JsonPropertyName("investmentStrategyIds")] public List<string> InvestmentStrategyIds { get; set; } = new();
    }

    public sealed class Phase
    {
        [JsonPropertyName("phaseId")] public string PhaseId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("order")] public int? Order { get; set; }
        [JsonPropertyName("selector")] public Selector? Selector { get; set; }
        [JsonPropertyName("actionIds")] public List<string> ActionIds { get; set; } = new();
        [JsonPropertyName("recommendedState")] public RecommendedState? RecommendedState { get; set; }
        [JsonPropertyName("shoppingPlan")] public ShoppingPlan? ShoppingPlan { get; set; }
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    }

    /// <summary>账号/版本层前置（applicability.required）——BestMatch 排除与面板提示用（第 2 期 B）。
    /// R5 P3 unknownPolicy 实装（2026-09-08）：defaultUnknownPolicy（prohibited/known-only，字段语义见 field-dictionary 二b）
    /// 与 prohibited（禁止与该攻略并用的账号层条件）建模；语义消费（prohibited 驱动排除）待第 2 期 D 扩展。</summary>
    public sealed class Applicability
    {
        [JsonPropertyName("required")] public List<Condition> Required { get; set; } = new();
        [JsonPropertyName("defaultUnknownPolicy")] public string? DefaultUnknownPolicy { get; set; }
        [JsonPropertyName("prohibited")] public List<string> Prohibited { get; set; } = new();
    }

    /// <summary>A1/A2 类信息的字段源（第 2 期 C：搜牌目标与商店口径上屏）。</summary>
    public sealed class RecommendedState
    {
        [JsonPropertyName("lineup")] public LineupTarget? Lineup { get; set; }
    }

    public sealed class LineupTarget
    {
        [JsonPropertyName("acquisitionPriorities")] public List<AcquisitionPriority> AcquisitionPriorities { get; set; } = new();
    }

    public sealed class AcquisitionPriority
    {
        [JsonPropertyName("characterId")] public string CharacterId { get; set; } = "";
        [JsonPropertyName("priority")] public int Priority { get; set; }
        [JsonPropertyName("targetStars")] public int? TargetStars { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    public sealed class ShoppingPlan
    {
        [JsonPropertyName("priorityCharacterIds")] public List<string> PriorityCharacterIds { get; set; } = new();
        [JsonPropertyName("refreshPolicy")] public string? RefreshPolicy { get; set; }
        [JsonPropertyName("stopWhen")] public List<string> StopWhen { get; set; } = new();
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
    }

    public sealed class Selector
    {
        [JsonPropertyName("planeIds")] public List<int> PlaneIds { get; set; } = new();
        [JsonPropertyName("nodeIds")] public List<string> NodeIds { get; set; } = new();
        [JsonPropertyName("nodeTypes")] public List<string> NodeTypes { get; set; } = new();
        [JsonPropertyName("nodeRanges")] public List<NodeRange> NodeRanges { get; set; } = new();
    }

    public sealed class NodeRange
    {
        [JsonPropertyName("planeId")] public int PlaneId { get; set; }
        [JsonPropertyName("fromNodeIndex")] public int FromNodeIndex { get; set; }
        [JsonPropertyName("toNodeIndex")] public int ToNodeIndex { get; set; }
    }

    public sealed class Action
    {
        [JsonPropertyName("actionId")] public string ActionId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("instruction")] public string Instruction { get; set; } = "";
        [JsonPropertyName("operationType")] public string? OperationType { get; set; }
        [JsonPropertyName("targetCharacterIds")] public List<string> TargetCharacterIds { get; set; } = new();
        [JsonPropertyName("priority")] public int Priority { get; set; } = 500;
        [JsonPropertyName("conditions")] public List<Condition> Conditions { get; set; } = new();
        [JsonPropertyName("trigger")] public string? Trigger { get; set; }
        [JsonPropertyName("triggerArgs")] public Dictionary<string, JsonElement>? TriggerArgs { get; set; }
        [JsonPropertyName("fallbackActionIds")] public List<string> FallbackActionIds { get; set; } = new();
        [JsonPropertyName("benefits")] public List<string> Benefits { get; set; } = new();
        [JsonPropertyName("evidenceRefs")] public List<EvidenceRef>? EvidenceRefs { get; set; }
        [JsonPropertyName("risks")] public List<string> Risks { get; set; } = new();

        /// <summary>高风险动作（弃局/卖出等）面板只警示，永不作为"现在做"。</summary>
        public bool IsHighRisk => OperationType is "abandon_run" or "sell_character";
    }

    public sealed class EvidenceRef
    {
        [JsonPropertyName("evidenceSetId")] public string EvidenceSetId { get; set; } = "";
        [JsonPropertyName("claimId")] public string ClaimId { get; set; } = "";
    }

    public sealed class Condition
    {
        [JsonPropertyName("field")] public string Field { get; set; } = "";
        [JsonPropertyName("operator")] public string Op { get; set; } = "";
        [JsonPropertyName("expected")] public JsonElement Expected { get; set; }
    }

    public sealed class Branch
    {
        [JsonPropertyName("branchId")] public string BranchId { get; set; } = "";
        [JsonPropertyName("priority")] public int Priority { get; set; } = 1;
        [JsonPropertyName("when")] public List<Condition> When { get; set; } = new();
        [JsonPropertyName("thenActionIds")] public List<string> ThenActionIds { get; set; } = new();
        [JsonPropertyName("otherwiseActionIds")] public List<string> OtherwiseActionIds { get; set; } = new();
    }

    public sealed class AlternativeRoute
    {
        [JsonPropertyName("routeId")] public string RouteId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
    }

    public sealed class Risk
    {
        [JsonPropertyName("riskId")] public string RiskId { get; set; } = "";
        [JsonPropertyName("severity")] public string Severity { get; set; } = "medium";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
    }

    public sealed class CraftingPath
    {
        [JsonPropertyName("resultId")] public string ResultId { get; set; } = "";
        [JsonPropertyName("inputIds")] public List<string> InputIds { get; set; } = new();
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    public sealed class GuideCode
    {
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        // 溯源参考（用户裁决 2026-09-05）：仅存档展示，禁作匹配键/决策输入。
    }

    /// <summary>面板建议卡（简洁原则：动词+目标+一句指令，不自述类型标签）。</summary>
    public sealed record SuggestionCard(
        string ActionId,
        string Verb,
        string Title,
        string Instruction,
        int Priority,
        IReadOnlyList<string> Targets,
        IReadOnlyList<string> Fallbacks,
        bool Gated)
    {
        public static SuggestionCard Warning(string riskId, string description, string severity) =>
            new("risk-" + riskId, "warn", "禁区/风险", description, severity == "high" ? 999 : 0, Array.Empty<string>(), Array.Empty<string>(), true);
    }
}
