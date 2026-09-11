// GuideMatcher.cs —— 攻略匹配器（2026-09-08 实现，按 research/matcher-top-design-20260907.md 设计稿）。
// 五阶段：A 候选过滤 → B 轨迹相似度（sim=max over phases）→ C 跨攻略聚合 urge=Σ sim×necessity×absent
//        → D 建议管线（通用表 evidence 填空未完成，留空占位不手写数值）→ E 报告组装。
// 纯函数无状态：同快照必同报告。全部系数为设计稿建议初值，集中在 MatcherTuning，待用户拍板后校准。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAdvisor.GuidePlaybooks
{
    /// <summary>可调初值（设计稿 §二/§三/§七；全部待用户拍板）。</summary>
    public static class MatcherTuning
    {
        public const double Wc = 0.50, Wb = 0.30, We = 0.20;   // 角色/羁绊/装备权重（Wb/We 通道保留待 schema 扩展）
        public const int TopK = 3;                              // 入围聚合的攻略数
        public const double MinSimForAggregation = 0.30;        // 弱匹配截断（C3）
        public const double N0 = 1.0, N1 = 0.7, N2 = 0.4, N3 = 0.15;
        public const double ExcludedCap = 0.5;                  // 独占条件识别不到 → 封顶降权（预留）
        public const int UrgeTopN = 10;
    }

    /// <summary>匹配报告（设计稿 §六 schema 的 C# 投影）。</summary>
    public sealed class MatchReport
    {
        public string SnapshotDigest { get; set; } = "";
        public List<MatchEntry> Matches { get; set; } = new();
        public List<CharacterUrge> CharacterUrges { get; set; } = new();
        public List<EconomyAdvice> EconomyAdvice { get; set; } = new();
        public List<string> Caveats { get; set; } = new();
    }

    public sealed class MatchEntry
    {
        public string GuideId { get; set; } = "";
        public string Title { get; set; } = "";
        public double Similarity { get; set; }
        public string MatchedPhase { get; set; } = "";
        public string ConditionStatus { get; set; } = "verified";
        public bool IsPrimary { get; set; }
    }

    public sealed class CharacterUrge
    {
        public string CharacterId { get; set; } = "";
        public double Urge { get; set; }
        public string NecessityTier { get; set; } = "";
        public List<UrgeSource> Sources { get; set; } = new();
    }

    public sealed class UrgeSource
    {
        public string GuideId { get; set; } = "";
        public double Similarity { get; set; }
        public string NecessityTier { get; set; } = "";
        public double Contribution { get; set; }
    }

    public sealed class EconomyAdvice
    {
        public string Field { get; set; } = "genericPlan";
        public string Advice { get; set; } = "";
        public string Source { get; set; } = "generic-plan";
    }

    public static class GuideMatcher
    {
        /// <summary>必要性分层（设计稿 §二：priority+note 自动初映射，人工复核批次确认）。</summary>
        public static (string Tier, double Weight) NecessityOf(AcquisitionPriority p)
        {
            var note = p.Note ?? string.Empty;
            bool conditional = note.Contains("条件位", StringComparison.Ordinal) || note.Contains("过渡", StringComparison.Ordinal);
            bool core = note.Contains("核心件", StringComparison.Ordinal) || note.Contains("唯一解", StringComparison.Ordinal) || note.Contains("刚需", StringComparison.Ordinal);
            if (core && p.Priority <= 1) return ("N0", MatcherTuning.N0);
            if (p.Priority <= 1) return ("N1", MatcherTuning.N1);
            if (p.Priority <= 4) return ("N2", MatcherTuning.N2);
            if (conditional || p.Priority >= 5) return ("N3", MatcherTuning.N3);
            return ("N2", MatcherTuning.N2);
        }

        public static MatchReport Match(RunSnapshot snapshot, IEnumerable<GuidePlaybook> library)
        {
            var report = new MatchReport { SnapshotDigest = Digest(snapshot) };
            var owned = OwnedSet(snapshot);

            // R5 P3-7（人口镜像·注释级）：本匹配器当前不消费人口；等级侧输入（MatcherUrgency.Build 的
            // currentLevel，由调用方取自快照 StoreLevel）与人口是两个不同量——真实人口 ≈ 基础人口（=商店等级）
            // + 宝钻/特殊单位扩容特例（至多 3 个、每个 +1，硬上限 13；口径详见
            // GuideEvaluator.RunContextFactory.FromSnapshot 与 Contracts.RunSnapshot 的注释）。
            // TODO(人口字段)：未来攻略条件/待办引入 population 语义时，必须等 RunSnapshot 落地独立人口字段后
            // 接真实观测，禁止在"等级=人口"镜像近似上直接放大用途（宝钻/特殊单位多的局镜像会低估人口）。

            // A 候选生成：本版快照无版本/环境字段（RunSnapshot 无 GameVersion/Node）——
            // 版本过滤与条件门（A1/A3）待 RunSnapshot 扩展后启用；当前全库入围，条件帽见 Caveats。
            var candidates = library.ToList();

            // B 轨迹相似度：sim(g) = max_p score(g,p)
            var scored = new List<(GuidePlaybook g, double sim, string phase)>();
            foreach (var g in candidates)
            {
                double best = 0; var bestPhase = string.Empty;
                foreach (var phase in g.Phases)
                {
                    var s = PhaseScore(owned, phase);
                    if (s > best) { best = s; bestPhase = phase.PhaseId; }
                }
                if (best > 0) scored.Add((g, best, bestPhase));
            }
            scored.Sort((a, b) => b.sim.CompareTo(a.sim));

            var top = scored.Where(x => x.sim >= MatcherTuning.MinSimForAggregation).Take(MatcherTuning.TopK).ToList();
            for (int i = 0; i < top.Count; i++)
            {
                report.Matches.Add(new MatchEntry
                {
                    GuideId = top[i].g.GuideId, Title = top[i].g.Title,
                    Similarity = Math.Round(top[i].sim, 4), MatchedPhase = top[i].phase,
                    ConditionStatus = "verified", IsPrimary = i == 0,
                });
            }

            // C2 跨攻略聚合 urge(c) = Σ sim × necessity × absent（C3：弱匹配已截断；在场角色不产生获取 urge）
            var urges = new Dictionary<string, CharacterUrge>(StringComparer.Ordinal);
            foreach (var (g, sim, _) in top)
            {
                var prios = g.Phases
                    .SelectMany(p => p.RecommendedState?.Lineup?.AcquisitionPriorities ?? new List<AcquisitionPriority>());
                foreach (var pr in prios)
                {
                    if (string.IsNullOrEmpty(pr.CharacterId)) continue;
                    if (owned.Contains(pr.CharacterId)) continue;
                    var (tier, w) = NecessityOf(pr);
                    if (!urges.TryGetValue(pr.CharacterId, out var u))
                    {
                        u = new CharacterUrge { CharacterId = pr.CharacterId, NecessityTier = tier };
                        urges[pr.CharacterId] = u;
                    }
                    if (w > TierWeight(u.NecessityTier)) u.NecessityTier = tier;
                    u.Urge += sim * w;
                    u.Sources.Add(new UrgeSource { GuideId = g.GuideId, Similarity = Math.Round(sim, 4), NecessityTier = tier, Contribution = Math.Round(sim * w, 4) });
                }
            }
            report.CharacterUrges = urges.Values
                .OrderByDescending(u => u.Urge).Take(MatcherTuning.UrgeTopN)
                .Select(u => { u.Urge = Math.Round(u.Urge, 4); u.Sources = u.Sources.OrderByDescending(s => s.Contribution).ToList(); return u; })
                .ToList();

            // D2 通用方案表：evidence 填空未完成（旧表"连败/云顶节奏"列已证伪撤回）——留空占位，绝不手写数值。
            report.EconomyAdvice.Add(new EconomyAdvice
            {
                Advice = "通用方案表待 evidence 填空（设计稿 §四纪律：无据不手写）；当前仅攻略特定字段（matchedPhase 的 shoppingPlan.stopWhen）可给对账建议。",
                Source = "generic-plan:pending-evidence",
            });

            report.Caveats.Add("系数全部为设计稿建议初值（MatcherTuning），待用户拍板后校准。");
            report.Caveats.Add("RunSnapshot 尚无版本/环境/节点字段：版本过滤与条件门（A1/A3）待快照扩展后启用；条件型攻略降级路径未激活。");
            report.Caveats.Add("Wb/We（羁绊/装备相似度）通道保留但暂计 0：playbook phase 快照未携带 bonds/equipment 目标结构，不引入臆造数据。");
            return report;
        }

        private static double TierWeight(string tier) => tier switch
        {
            "N0" => MatcherTuning.N0, "N1" => MatcherTuning.N1, "N3" => MatcherTuning.N3, _ => MatcherTuning.N2,
        };

        private static HashSet<string> OwnedSet(RunSnapshot s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            Collect(set, s.BoardCharacterIds);
            Collect(set, s.BenchCharacterIds);
            Collect(set, s.LineupIds);
            return set;
        }

        private static void Collect(HashSet<string> into, Observation<IReadOnlyList<string>>? o)
        {
            if (o == null || o.Status != ObservationStatus.Known || o.Value == null) return;
            foreach (var id in o.Value)
            {
                if (!string.IsNullOrEmpty(id)) into.Add(id);
            }
        }

        /// <summary>B1 单 phase 相似度。playbook phase 快照现仅携带 acquisitionPriorities（角色+必要性），
        /// 故 charScore=目标集与持有的 Jaccard、carry(priority=1)全命中加权；Wb/We 通道计 0 并在 Caveats 声明（no-fabrication：不臆测 bond/equip 目标）。</summary>
        private static double PhaseScore(HashSet<string> owned, Phase phase)
        {
            var prios = phase.RecommendedState?.Lineup?.AcquisitionPriorities ?? new List<AcquisitionPriority>();
            var targetIds = prios.Select(p => p.CharacterId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            if (targetIds.Count == 0) return 0;

            var hit = targetIds.Count(owned.Contains);
            double charScore = (double)hit / targetIds.Count;
            var carry = prios.Where(p => p.Priority == 1).Select(p => p.CharacterId).ToHashSet(StringComparer.Ordinal);
            if (carry.Count > 0 && carry.All(owned.Contains)) charScore = Math.Min(1.0, charScore + 0.15);
            return MatcherTuning.Wc * charScore;
        }

        private static string Digest(RunSnapshot s)
        {
            var parts = new List<string>();
            void add(string name, Observation<IReadOnlyList<string>>? o)
            {
                if (o != null && o.Status == ObservationStatus.Known && o.Value != null)
                {
                    parts.Add(name + "=" + string.Join("|", o.Value));
                }
            }
            add("board", s.BoardCharacterIds);
            add("bench", s.BenchCharacterIds);
            add("syn", s.SynergyIds);
            add("eq", s.EquipmentIds);
            if (s.StoreLevel.Status == ObservationStatus.Known) parts.Add("store=" + s.StoreLevel.Value);
            var raw = string.Join(";", parts);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
        }
    }
}
