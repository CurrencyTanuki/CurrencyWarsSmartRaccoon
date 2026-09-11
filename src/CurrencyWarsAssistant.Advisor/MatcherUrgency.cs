// MatcherUrgency.cs —— §三.5 待办紧迫度算法（2026-09-08 实现）。
// urgency(item) = 100 × G(缺口 0~1] × T(时间窗 [0.3,1]) × S(后果权 [0.5,1.5])；档位 ≥70 红/40 黄/15 绿/<15 隐藏。
// 纪律（no-fabrication）：设计稿 §三.5A 的 pace 基准曲线（匀速/高难/连败）出自 generic-plan-economy 3.1，
// 该表三列已证伪撤回（连败列=云顶污染、匀速列=社区口述）——**本实现不内置任何具体曲线数值**，
// 曲线由调用方注入（PaceBaseline，数据待 evidence 填空后从官方/实测源录入）；无曲线时不生成"升等级"类待办并记 caveat。
// 血量/金币阈值同理可注入（默认关闭强预警，宁缺勿错——§三.5F 局况第一输入原则）。
using System;
using System.Collections.Generic;
using System.Linq;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAdvisor.GuidePlaybooks
{
    /// <summary>可插拔基准数据（调用方注入；注入空表=相关待办不生成）。</summary>
    public sealed class PaceBaseline
    {
        /// <summary>方案名 → (节点 "plane-node" → 该节点中等等级)。如 "evidence-sourced": {"2-1":4,...}。</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Schemes { get; init; }
            = new Dictionary<string, IReadOnlyDictionary<string, int>>();
        /// <summary>默认方案名（按难度档/局况选择；空=无基准）。</summary>
        public string? DefaultScheme { get; init; }
        /// <summary>血量预警阈值（升序 [30,60]=<30 危/<60 警）。null=不生成血量待办（待 evidence）。</summary>
        public int[]? HpThresholds { get; init; }
    }

    /// <summary>待办条目（七类：levelup/coin/character/gear/roll/hp/position-warning）。</summary>
    public sealed class UrgencyItem
    {
        public string Type { get; set; } = "";
        public string Action { get; set; } = "";
        public string? Current { get; set; }
        public string? Target { get; set; }
        public double Urgency { get; set; }
        public string Band { get; set; } = "";   // red/yellow/green/hidden
        public string Source { get; set; } = ""; // 攻略名 / 方案名
        public string Reason { get; set; } = "";
    }


    public static class PaceBaselineFactory
    {
        /// <summary>从 evidence JSON（tools/extract_pace_baseline.py 产物，practice-report 帧证中位）加载。
        /// 证据级=practice-report frame-verified；每格来源报告清单见 research/guides/pace_baseline_evidence.json。</summary>
        public static PaceBaseline FromEvidenceJson(string path, string schemeName = "practice-median-A8")
        {
            var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var nodes = doc.RootElement.GetProperty("nodes");
            var curve = new Dictionary<string, int>();
            foreach (var np in nodes.EnumerateObject())
            {
                var med = np.Value.GetProperty("median_level").GetDouble();
                curve[np.Name] = (int)Math.Round(med);
            }
            return new PaceBaseline
            {
                Schemes = new Dictionary<string, IReadOnlyDictionary<string, int>> { [schemeName] = curve },
                DefaultScheme = curve.Count > 0 ? schemeName : null,
            };
        }
    }

    public static class MatcherUrgency
    {
        public const double Red = 70, Yellow = 40, Green = 15;

        /// <summary>dev 分档（设计稿 §三.5A：≤-1 跟不上 / 0 中等 / ≥+1 大大领先）。</summary>
        public static string DevBand(int dev) => dev <= -1 ? "behind" : dev == 0 ? "on-pace" : "ahead";

        /// <summary>等级待办：dev<0 生成；G=落后一档 0.7~1.0（差 1 档 0.7、≥2 档 1.0）、超前达标=0（待办消失）。</summary>
        public static UrgencyItem? LevelTodo(string node, int currentLevel, int baselineLevel, string scheme,
            double timeWindow = 0.6, double severity = 1.0)
        {
            int dev = currentLevel - baselineLevel;
            if (dev >= 0) return null;                       // 达成/领先 → 待办消失（设计稿 §三.5D）
            double g = dev == -1 ? 0.7 : 1.0;                // 落后一整档=0.7，更多=1.0
            double u = 100 * g * timeWindow * severity;
            return new UrgencyItem
            {
                Type = "levelup", Action = $"升等级（基准 {baselineLevel}）", Current = currentLevel.ToString(),
                Target = baselineLevel.ToString(), Urgency = Math.Round(u, 1),
                Band = Band(u), Source = "pace:" + scheme,
                Reason = $"落后 {Math.Abs(dev)} 档（{DevBand(dev)}）",
            };
        }

        /// <summary>血量预警：仅当调用方注入阈值且局况可靠（§三.5F：单维越线只出中性提示）。</summary>
        public static UrgencyItem? HpTodo(int hp, int[] thresholds, double severity = 1.0)
        {
            if (thresholds == null || thresholds.Length == 0) return null;
            if (hp >= thresholds[^1]) return null;
            // 越线深度：低于最深阈值=1.0，介于两档间按比例
            double g = hp <= thresholds[0] ? 1.0
                : 1.0 - (double)(hp - thresholds[0]) / (thresholds[^1] - thresholds[0]);
            double u = 100 * Math.Max(0.3, g) * severity;
            return new UrgencyItem
            {
                Type = "hp", Action = "血量预警（中性提示，局况未验证）", Current = hp.ToString(),
                Target = thresholds[^1].ToString(), Urgency = Math.Round(u, 1),
                Band = Band(u), Source = "thresholds:injected",
                Reason = $"低于预警线 {thresholds[^1]}（越线深度 {g:0.00}）",
            };
        }

        /// <summary>角色获取待办：由 urge 表直接映射（urge 已含 sim×necessity×absent，归一为缺口度）。</summary>
        public static UrgencyItem? CharacterTodo(CharacterUrge urge, double threshold = 0.15)
        {
            if (urge.Urge < threshold) return null;
            double u = 100 * Math.Min(1.0, urge.Urge) * 1.0 * 1.0;
            return new UrgencyItem
            {
                Type = "character", Action = $"获取 {urge.CharacterId}", Current = "未持有",
                Target = urge.NecessityTier, Urgency = Math.Round(u, 1),
                Band = Band(u), Source = "matcher:urge",
                Reason = "urge 聚合（匹配×必要性×缺席）",
            };
        }

        public static string Band(double u) =>
            u >= Red ? "red" : u >= Yellow ? "yellow" : u >= Green ? "green" : "hidden";

        /// <summary>从匹配报告生成待办清单（等级需注入基准曲线；无曲线时该类不生成并记 caveat）。</summary>
        public static List<UrgencyItem> Build(MatchReport report, RunSnapshot snapshot, PaceBaseline? baseline,
            int? currentLevel = null, string? node = null, int? hp = null)
        {
            var items = new List<UrgencyItem>();
            // 等级待办：需要基准曲线+当前等级+节点三者齐备
            if (baseline != null && currentLevel.HasValue && !string.IsNullOrEmpty(node)
                && !string.IsNullOrEmpty(baseline.DefaultScheme)
                && baseline.Schemes.TryGetValue(baseline.DefaultScheme!, out var curve)
                && curve.TryGetValue(node!, out var baseLv))
            {
                var item = LevelTodo(node!, currentLevel.Value, baseLv, baseline.DefaultScheme!);
                if (item != null) items.Add(item);
            }
            // 血量待办：阈值注入才生成（§三.5F 宁缺勿错）
            if (hp.HasValue && baseline?.HpThresholds != null)
            {
                var item = HpTodo(hp.Value, baseline.HpThresholds);
                if (item != null) items.Add(item);
            }
            // 角色获取待办：来自匹配报告 urge 表
            foreach (var u in report.CharacterUrges)
            {
                var item = CharacterTodo(u);
                if (item != null) items.Add(item);
            }
            // 排序：urgency 降序；hidden 档默认不输出（设计稿 §三.5C）
            return items.Where(i => i.Band != "hidden").OrderByDescending(i => i.Urgency).ToList();
        }
    }
}
