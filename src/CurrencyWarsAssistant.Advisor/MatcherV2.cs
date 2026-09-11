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
    public const double DefaultSourceConfidence = 0.7; // 信源未知时的中间档（清单 F5 口径）
}

public sealed record V2Board(
    IReadOnlyList<string> FieldIds,
    IReadOnlyList<string> BenchIds,
    int Level,
    int Gold,
    int TeamHp)
{
    public IReadOnlyList<string> AllIds { get; } = FieldIds.Concat(BenchIds).ToList();
    /// <summary>当局难度标签（如 "A820"）；null=未观测。难度阶梯/血量估算 advisory 的输入。</summary>
    public string? DifficultyLabel { get; init; }
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
    List<string> Caveats)
{
    /// <summary>D 实装（1.2.131）：难度阶梯+血量公式 advisory（数据 status=待A820实测终验，仅参考）。</summary>
    public string? DifficultyAdvisory { get; init; }
}

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
    private static JsonElement? _expertGate;          // D 实装：专家门控（is_expert_roster 9 名）
    private static JsonElement? _difficultyLadder;    // D 实装：难度阶梯（status=待A820实测终验）
    private static JsonElement? _hpFormula;           // D 实装：血量公式（base×1.052^N）
    private static Dictionary<string, int[]>? _shopOdds; // D 实装：Lv → [1费..5费]%（费用可达门控）
    private static Dictionary<string, string> _charNameToId = new(StringComparer.Ordinal); // 中文名→id（专家门控映射）
    private static Dictionary<string, int> _charCost = new(StringComparer.Ordinal);        // id→模式内费用
    private static HashSet<string>? _expertIds;
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
        // D 实装（1.2.131）：加载此前零消费的数据段+门控依赖的外部表。
        if (LoadSub(Path.Combine(dir, "matcher_v2_data.json"), "expert_gate") is { } eg) _expertGate = eg.Clone();
        if (LoadSub(Path.Combine(dir, "matcher_v2_data.json"), "difficulty_ladder") is { } dl) _difficultyLadder = dl.Clone();
        if (LoadSub(Path.Combine(dir, "matcher_v2_data.json"), "hp_formula") is { } hf) _hpFormula = hf.Clone();
        if (File.Exists(Path.Combine(dir, "shop_odds.json"))
            && LoadSub(Path.Combine(dir, "shop_odds.json"), "odds") is { } odds)
        {
            _shopOdds = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var lv in odds.EnumerateObject())
            {
                var arr = new List<int>();
                foreach (var e in lv.Value.EnumerateArray()) arr.Add(e.GetInt32());
                _shopOdds[lv.Name] = arr.ToArray();
            }
        }
        // 专家门控需要"中文名→id"、费用门控需要"id→cost"：两表都出自
        // dataRoot 内的 currency-wars-characters.json（P1 修复：原路径错找上级目录、
        // 根形制错认 Array——实际为 Object{metadata, characters[...], bond_catalog}，审查员实测）。
        _charNameToId = new(StringComparer.Ordinal);
        _charCost = new(StringComparer.Ordinal);
        var charsFile = Path.Combine(Path.GetFullPath(dataRoot), "currency-wars-characters.json");
        if (File.Exists(charsFile))
        {
            using var cdoc = JsonDocument.Parse(File.ReadAllText(charsFile));
            JsonElement? chars = null;
            if (cdoc.RootElement.ValueKind == JsonValueKind.Array) chars = cdoc.RootElement.Clone();
            else if (cdoc.RootElement.ValueKind == JsonValueKind.Object
                && cdoc.RootElement.TryGetProperty("characters", out var carr)
                && carr.ValueKind == JsonValueKind.Array) chars = carr.Clone();
            if (chars is { } carr2)
                foreach (var c in carr2.EnumerateArray())
                {
                    var id = c.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var name = c.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!string.IsNullOrEmpty(name)) _charNameToId[name!] = id!;
                    if (c.TryGetProperty("costs", out var costs) && costs.ValueKind == JsonValueKind.Array
                        && costs.GetArrayLength() > 0 && costs[0].ValueKind == JsonValueKind.Number)
                        _charCost[id!] = costs[0].GetInt32();
                }
            _expertIds = new HashSet<string>(StringComparer.Ordinal);
            if (_expertGate is { } egEl && egEl.TryGetProperty("is_expert_roster", out var roster)
                && roster.ValueKind == JsonValueKind.Array)
                foreach (var n in roster.EnumerateArray())
                    if (n.GetString() is { } nn && _charNameToId.TryGetValue(nn, out var eid))
                        _expertIds.Add(eid);
        }
        _loadedFrom = Path.GetFullPath(dataRoot);
    }

    /// <summary>1.2.131：显式确保数据已加载（组合层在读取 KnownSourceNames 等查询前调用）。</summary>
    public static void EnsureLoaded(string dataRoot)
    {
        if (_necessity is null || _loadedFrom != Path.GetFullPath(dataRoot)) Load(dataRoot);
    }

    /// <summary>C 实装（v2.2 前置）：已加载的 source_confidence 精确源名清单（启发映射用）。</summary>
    public static IReadOnlyCollection<string> KnownSourceNames()
        => _sourceConf.ValueKind == JsonValueKind.Object
            ? _sourceConf.EnumerateObject().Select(p => p.Name).ToArray()
            : Array.Empty<string>();

    /// <summary>D 实装：信源置信度查询（source_confidence 28 源，精确名匹配）；未知源默认 0.7 中间档。</summary>
    public static double SourceConfidenceFor(string sourceName)
        => _sourceConf.ValueKind == JsonValueKind.Object
           && _sourceConf.TryGetProperty(sourceName, out var e)
           && e.TryGetProperty("coefficient", out var c)
           && c.ValueKind == JsonValueKind.Number ? c.GetDouble()
           : MatcherV2Tuning.DefaultSourceConfidence;

    /// <summary>D 实装：难度标签→阶梯分（如 "A820"→75）；未知标签返回 null。</summary>
    public static int? DifficultyScoreOf(string? difficultyLabel)
        => _difficultyLadder is { } dl && difficultyLabel is not null
           && dl.TryGetProperty(difficultyLabel, out var v) && v.ValueKind == JsonValueKind.Number
           ? v.GetInt32() : null;

    /// <summary>D 实装：boss 血量估算 = base × 1.052^(分-基线分)。数据 status=待A820实测终验，仅 advisory。</summary>
    public static double? BossHpEstimate(int difficultyScore)
    {
        if (_hpFormula is not { } hf || _difficultyLadder is not { } dl) return null;
        double @base = hf.TryGetProperty("base_boss", out var bb) && bb.ValueKind == JsonValueKind.Number ? bb.GetDouble() : 0;
        double factor = hf.TryGetProperty("factor", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 1.0;
        int minScore = int.MaxValue;
        foreach (var p in dl.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.GetInt32() < minScore)
                minScore = p.Value.GetInt32();
        if (minScore == int.MaxValue || @base <= 0 || factor <= 1) return null;
        return @base * Math.Pow(factor, difficultyScore - minScore);
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

    public static V2Report Recommend(V2Board board, string dataRoot,
        IReadOnlyDictionary<string, double>? guideSims = null,
        IReadOnlyDictionary<string, string>? guideSourceNames = null,
        IReadOnlyDictionary<string, double>? guideSourceConf = null)
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
        // D 实装：当前等级的费用概率档（Lv 夹取 1..10）；null=无表不做费用门控。
        int[]? odds = _shopOdds is not null && _shopOdds.TryGetValue("Lv" + Math.Clamp(board.Level, 1, 10), out var o)
            ? o : null;
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
            // C 实装（v2.2）：信源置信度=显式系数表 > 指南名→信源名（source_confidence 精确键） > 默认 0.7。
            double sourceConf = guideSourceConf is not null && guideSourceConf.TryGetValue(guideFile, out var sc)
                ? sc
                : guideSourceNames is not null && guideSourceNames.TryGetValue(guideFile, out var sn)
                    ? SourceConfidenceFor(sn)
                    : MatcherV2Tuning.DefaultSourceConfidence;
            var (roundBuys, roundGated) = GuideBuys(guideFile, tiers, fieldNorm, guideShort, !n0OnField, sim, sourceConf, odds, board.Level);
            gated.AddRange(roundGated);
            if (!n0OnField)
            {
                gated.AddRange(roundBuys); // 未激活攻略的候选整体转转型候选，不进购买栏
                continue;
            }
            activated.Add(guideShort);
            buys.AddRange(roundBuys);
        }
        buys.Sort((a, b) => b.Score.CompareTo(a.Score));

        // D 实装：难度阶梯+血量 advisory（数据 status=待A820实测终验——仅参考，不进任何判定）。
        string? difficultyAdvisory = null;
        if (board.DifficultyLabel is { } dl && DifficultyScoreOf(dl) is { } score)
        {
            var hp = BossHpEstimate(score);
            difficultyAdvisory = $"难度 {dl} → 阶梯分 {score}（数据待A820实测终验）"
                + (hp is { } h ? $"；boss 血量估算 {h / 10000:0.#} 万" : "");
        }

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
            activated, buys.Take(12).ToList(), gated.Take(10).ToList(), universalNow, caveats)
        { DifficultyAdvisory = difficultyAdvisory };
        return report;
    }

    private static (List<V2BuyRec> Buys, List<V2BuyRec> Gated) GuideBuys(
        string guideFile, JsonElement tiers, HashSet<string> fieldNorm, string guideShort, bool activated, double sim,
        double sourceConf, int[]? oddsAtLevel, int level)
    {
        // P1（终审）修复：门控命中（费用不可达/专家）必须真的进 gated 列表，不允许
        // 只改注释仍留在购买栏（旧实现 gateNote 只拼字符串、去向由 n0OnField 决定=失效）。
        var buys = new List<V2BuyRec>();
        var gated = new List<V2BuyRec>();
        foreach (var cidProp in tiers.EnumerateObject())
        {
            var cid = NormId(cidProp.Name);
            if (fieldNorm.Contains(cid)) continue; // 在场不产生 urge（v1 规则）
            var tier = cidProp.Value.TryGetProperty("tier", out var t) ? t.GetString() ?? "N3" : "N3";
            var confTier = cidProp.Value.TryGetProperty("confidence", out var cf) ? cf.GetString() ?? "medium" : "medium";
            // D 实装（1.2.131）：费用可达门控——当前等级概率档为 0 的费用进 gated（不进购买栏）。
            // 表长短于费用档位=低等级高费用 0%（如 Lv1 只有 [100]）→ 同样判不可达。
            var cost = _charCost.TryGetValue(cid, out var cc) ? cc : -1;
            string? costNote = null;
            if (cost > 0 && oddsAtLevel is { } odds)
                costNote = cost <= odds.Length
                    ? odds[cost - 1] <= 0 ? $"费用不可达（Lv{level} 无 {cost} 费）" : null
                    : $"费用不可达（Lv{level} 概率表无 {cost} 费）";
            // D 实装：专家门控——is_expert 名单不通过常规刷新出现（T1 机制库 L145）。
            var expertNote = _expertIds is { Count: > 0 } && _expertIds.Contains(cid) ? "专家（不常规刷新）" : null;
            var gateNote = costNote ?? expertNote;
            var w = TierWeight(tier) * sim
                    * confTier switch { "high" => 1.0, "medium" => 0.8, _ => 0.6 }
                    * sourceConf; // C 实装（v2.2）：推荐度=匹配度×必要性×置信度×信源置信度
            var note = (activated ? "已激活" : "转型候选") + $"/{guideShort}/{tier}";
            var rec = new V2BuyRec(cid, Math.Round(w, 4), tier,
                gateNote is not null ? note + "/" + gateNote : note);
            if (gateNote is not null) gated.Add(rec); else buys.Add(rec);
        }
        return (buys, gated);
    }
}
