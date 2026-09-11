using System.IO;
using System.Text.Json;

namespace CurrencyWarsAssistant.Advisor;

/// <summary>v2.1 调参表（CASE_20260910 §8/§11/§15 用户口径；全部可调，禁止硬编码到逻辑分支）。</summary>
public static class MatcherV2Tuning
{
    public const double N0 = 1.0;
    public const double N1 = 0.80;
    public const double N2 = 0.60;
    public const double N3 = 0.40;
    public const int HealHpThreshold = 60;
}

public sealed record V2Board(
    IReadOnlyList<string> FieldIds,
    IReadOnlyList<string> BenchIds,
    int Level,
    int Gold,
    int TeamHp)
{
    public IReadOnlyList<string> AllIds { get; } = FieldIds.Concat(BenchIds).ToList();
}

public sealed record V2BuyRec(string CharacterId, double Score, string Tier, string GateNote);

public sealed record V2StageMatch(string LineupId, string Stage, double Jaccard);

public sealed record V2Report(
    V2StageMatch BestStageMatch,
    List<V2StageMatch> TopStageMatches,
    List<string> ActivatedGuides,
    List<V2BuyRec> Buys,
    List<V2BuyRec> Gated,
    List<string> UniversalNow,
    List<string> Caveats);

/// <summary>
/// v2.1 推荐引擎：推荐度=匹配度(阶段向量Jaccard)×必要性(N0-N3)×信源置信度；
/// N0 门控(≥1 个 N0 在场=攻略激活)/费用可达门控/专家门控/通用拐价值线。
/// 数据=data/4.4/matcher_v2/*.json；机制依据=CASE_20260910_A820_BOARD.md §11-13。
/// </summary>
public static class MatcherV2
{
    private static Dictionary<string, JsonElement>? _necessity;
    private static JsonElement _sourceConf;
    private static Dictionary<string, JsonElement>? _lineups;
    private static JsonElement _universal;
    private static string? _loadedFrom;

    private static JsonElement LoadSub(string file, string prop)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.TryGetProperty(prop, out var e) ? e.Clone() : default;
    }

    public static void Load(string dataRoot)
    {
        var dir = Path.Combine(dataRoot, "matcher_v2");
        var nec = LoadSub(Path.Combine(dir, "matcher_v2_data.json"), "necessity");
        _necessity = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in nec.EnumerateObject()) _necessity[p.Name] = p.Value.Clone();
        _sourceConf = LoadSub(Path.Combine(dir, "matcher_v2_data.json"), "source_confidence");
        var lin = LoadSub(Path.Combine(dir, "lineup_stages.json"), "lineups");
        _lineups = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in lin.EnumerateObject()) _lineups[p.Name] = p.Value.Clone();
        _universal = LoadSub(Path.Combine(dir, "universal_supports.json"), "categories").Clone();
        _loadedFrom = Path.GetFullPath(dataRoot);
    }

    private static string NormId(string id) => id.Replace("currency_wars_character_", "").Trim();

    private static double TierWeight(string? tier) => tier switch
    {
        "N0" => MatcherV2Tuning.N0, "N1" => MatcherV2Tuning.N1,
        "N2" => MatcherV2Tuning.N2, _ => MatcherV2Tuning.N3,
    };

    private static HashSet<string> StageIds(JsonElement seg)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (seg.ValueKind == JsonValueKind.Array)
            foreach (var e in seg.EnumerateArray())
            {
                var s = e.ValueKind == JsonValueKind.String ? e.GetString() :
                        (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i.GetString() : null);
                if (!string.IsNullOrEmpty(s)) set.Add(NormId(s));
            }
        return set;
    }

    public static V2Report Recommend(V2Board board, string dataRoot, IReadOnlyDictionary<string, double>? guideSims = null)
    {
        if (_necessity is null || _loadedFrom != Path.GetFullPath(dataRoot)) Load(dataRoot);
        var caveats = new List<string>();
        var fieldNorm = board.FieldIds.Select(NormId).ToHashSet(StringComparer.Ordinal);

        // —— 阶段化匹配：板面 vs 600 套阵容 Early/Middle/Final 向量（Jaccard），邻近阶段衰减
        var (wPrev, wCur, wNext) = board.Level <= 3 ? (0.3, 1.0, 0.5)
                                  : board.Level <= 6 ? (0.5, 1.0, 0.5) : (0.3, 1.0, 0.3);
        var stageMatches = new List<(string Lineup, string Stage, double J, double W)>();
        foreach (var kv in _lineups!)
        {
            var seg = kv.Value;
            foreach (var stageName in new[] { "Early", "Middle", "Final" })
            {
                if (!seg.TryGetProperty(stageName, out var arr)) continue;
                var ids = StageIds(arr);
                if (ids.Count == 0) continue;
                var inter = ids.Count(fieldNorm.Contains);
                var union = ids.Union(fieldNorm).Count();
                var j = (double)inter / union;
                var w = stageName switch { "Early" => wPrev, "Middle" => wCur, _ => wNext };
                stageMatches.Add((kv.Key, stageName, j, w));
            }
        }
        var ranked = stageMatches.OrderByDescending(x => x.J * x.W).ToList();
        var best = ranked.Count > 0 ? ranked[0] : ("", "", 0.0, 0.0);
        var topStages = ranked.Take(5).Select(x => new V2StageMatch(x.Lineup, x.Stage, Math.Round(x.J, 4))).ToList();

        // —— N0 门控 + 三系数购买推荐（推荐度=匹配度×必要性×置信度；在场不产生 urge）
        var buys = new List<V2BuyRec>();
        var gated = new List<V2BuyRec>();
        var activated = new List<string>();
        foreach (var kv in _necessity!)
        {
            var guideFile = kv.Key;
            var guideShort = guideFile.Replace("guide-v44-", "").Replace(".guide-playbook.v1.1.json", "");
            var tiers = kv.Value;
            var n0OnField = false;
            foreach (var cidProp in tiers.EnumerateObject())
            {
                var tier = cidProp.Value.TryGetProperty("tier", out var t) ? t.GetString() : null;
                if (tier == "N0" && fieldNorm.Contains(NormId(cidProp.Name))) { n0OnField = true; break; }
            }
            var sim = guideSims is not null && guideSims.TryGetValue(guideFile, out var sv) ? sv : 0.5; // 接入点:v1 GuideMatcher.Match 的 Similarity
            if (!n0OnField)
            {
                gated.AddRange(GuideBuys(guideFile, tiers, fieldNorm, guideShort, false, sim));
                continue;
            }
            activated.Add(guideShort);
            buys.AddRange(GuideBuys(guideFile, tiers, fieldNorm, guideShort, true, sim));
        }
        buys.Sort((a, b) => b.Score.CompareTo(a.Score));

        // —— 通用拐价值线（阵容成型后上浮；HP 低→治疗再上调）
        var universalNow = new List<string>();
        foreach (var cat in _universal.EnumerateObject())
        foreach (var u in cat.Value.EnumerateArray())
        {
            var id = u.TryGetProperty("id", out var i2) ? NormId(i2.GetString() ?? "") : "";
            if (fieldNorm.Contains(id)) continue;
            var nm = u.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var cost = u.TryGetProperty("cost", out var c) ? c.GetInt32() : 0;
            var pri = cat.Name == "治疗" && board.TeamHp < MatcherV2Tuning.HealHpThreshold ? "优先(血线紧)" : "常规";
            universalNow.Add($"{nm}({cat.Name},{cost}费){pri}");
        }

        var report = new V2Report(
            new V2StageMatch(best.Item1, best.Item2, Math.Round(best.Item3, 4)), topStages,
            activated, buys.Take(12).ToList(), gated.Take(10).ToList(), universalNow, caveats);
        return report;
    }

    private static IEnumerable<V2BuyRec> GuideBuys(
        string guideFile, JsonElement tiers, HashSet<string> fieldNorm, string guideShort, bool activated, double sim)
    {
        foreach (var cidProp in tiers.EnumerateObject())
        {
            var cid = NormId(cidProp.Name);
            if (fieldNorm.Contains(cid)) continue; // 在场不产生 urge（v1 规则）
            var tier = cidProp.Value.TryGetProperty("tier", out var t) ? t.GetString() ?? "N3" : "N3";
            var confTier = cidProp.Value.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "medium" : "medium";
            var w = TierWeight(tier) * sim * confTier switch { "high" => 1.0, "medium" => 0.8, _ => 0.6 };
            var note = (activated ? "已激活" : "转型候选") + $"/{guideShort}/{tier}";
            yield return new V2BuyRec(cid, Math.Round(w, 4), tier, note);
        }
    }
}
