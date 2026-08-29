using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using System.Diagnostics;
using System.Text.Json;

namespace CurrencyWarsAssistant.Tests;

// 临时工具：跑用户截图，输出角色名/星级/装备名/羁绊（原样+翻译）
public sealed class ClipboardFrameProbe
{
    static readonly string[] INPUT_PATHS =
    {
        @"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\125924.png",
        @"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\132307.png",
        @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260821-080042.753049-000001.png",
    };

    [Fact]
    public async Task Run()
    {
        var repo = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repo, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        using var ppOcr = new PpOcrOfflineOcr(Path.Combine(
            dataDirectory, "..", "ocr", "rapidocr", "PP-OCRv6_rec_small.onnx"), maximumConcurrency: 1);

        // 角色 id -> 中文名
        var charName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in gameData.CurrencyWarsCharacters)
        {
            charName[c.Id] = c.Name;
            charName[c.Id.Replace("currency_wars_character_", "character_", StringComparison.Ordinal)] = c.Name;
        }
        // 装备 id -> 中文名
        var equipName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var eqJson = Path.Combine(repo, "data", "runtime", "1.0.0", "4.4", "equipment", "equipment.json");
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(eqJson));
            if (doc.RootElement.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in recs.EnumerateArray())
                {
                    if (r.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.String &&
                        r.TryGetProperty("name", out var nmE) && nmE.ValueKind == JsonValueKind.String)
                    {
                        equipName[idE.GetString()!] = nmE.GetString()!;
                    }
                }
            }
        }
        catch { }

        var allOut = new List<string>();
        var templates = LoadTemplates(gameData);
        var iconCatalog = Phase2IconTemplateCatalog.Load(dataDirectory);
        foreach (var path in INPUT_PATHS)
        {
            var frame = CaptureFrameLoader.LoadFile(path);
            // 每次分析重建 analyzer：跨图复用会因 OpenCv 内部状态污染导致整页 Unknown
            //（8/17 极小图后 8/19~8/21 全部 Unknown 的根因）。改为逐图独立分析。
            var analyzer = new Phase2OperationalScreenshotAnalyzer(
                new OpenCvCharacterCardRecognizer(
                    candidateLimit: 32,
                    lenientLeadOverCharacterIds: ["currency_wars_character_40", "currency_wars_character_56", "currency_wars_character_72", "currency_wars_character_trailblazer"],
                    lenientConfidenceCharacterIds: ["currency_wars_character_05", "currency_wars_character_23"]),
                templates, iconRecognizer,
                iconCatalog,
                new WindowsOfflineOcr("zh-Hans"), gameData,
                new WindowsOfflineOcr("en-US"), storeLevelOcr: ppOcr);
            var sw = Stopwatch.StartNew();
            var state = await analyzer.AnalyzeAsync(frame, "unknown", "clip:" + Path.GetFileName(path),
                new RunSnapshot { RunId = "clip", AsOf = frame.CapturedAt }, CancellationToken.None);
            sw.Stop();

            var o = new List<string> {
                $"==== {Path.GetFileName(path)} ({frame.Width}x{frame.Height}) ====",
                $"全量识别耗时(ms)={sw.ElapsedMilliseconds}",
                $"PageFamily={state.PageFamily} Node={state.NodeId?.Status}:{state.NodeId?.Value}",
                $"Health={state.Health?.Status}:{state.Health?.Value} Difficulty={state.EnemyDifficulty?.Status}:{state.EnemyDifficulty?.Value}",
                $"StoreLevel={state.StoreLevel?.Status}:{state.StoreLevel?.Value} Population={state.Population?.Status}:{state.Population?.Value}",
                $"Interest={state.Interest?.Status}:{state.Interest?.Value} 连胜={state.CumulativeSpend?.Status}:{state.CumulativeSpend?.Value}",
                $"PlayerProgress={state.PlayerProgress?.Status}:{state.PlayerProgress?.Value}",
                $"扳手={state.DismantleToolCount?.Status}:{state.DismantleToolCount?.Value}",
            };
            string nm(string? id) => id is null ? "-" : (charName.TryGetValue(id, out var n) ? n : id);
            string em(string? id) => id is null ? "-" : (equipName.TryGetValue(id, out var n) ? n : id);
            var form = state.Formation?.Value ?? [];
            foreach (var zone in new[] { FormationZone.Front, FormationZone.Back, FormationZone.Bench })
            {
                var inZone = form.Where(x => x.Zone == zone).OrderBy(x => x.SlotIndex).ToList();
                o.Add($"  {zone}({inZone.Count}):");
                foreach (var x in inZone)
                {
                    var extra = new List<string>();
                    if (x.StarLevel is not null) extra.Add($"星{x.StarLevel}");
                    extra.Add($"conf{x.Confidence:F2}");
                    if (x.CandidateCharacterIds is { Count: > 0 })
                        extra.Add($"cand=[{string.Join(",", x.CandidateCharacterIds.Select(nm))}]");
                    if (x.FailureReason is not null)
                        extra.Add($"fail={x.FailureReason}");
                    if (x.EquipmentIds is { Count: > 0 }) extra.Add("装备:" + string.Join("、", x.EquipmentIds.Select(em)));
                    if (x.SpecialEquipmentIds is { Count: > 0 }) extra.Add("特殊:" + string.Join(",", x.SpecialEquipmentIds.Select(em)));
                    if (x.IsCheered) extra.Add("应援");
                    if (x.IsHunterStar) extra.Add("猎星人");
                    if (x.EquipmentIds is not { Count: > 0 } && x.SpecialEquipmentIds is not { Count: > 0 }) extra.Add("无装备");
                    if (x.EquipmentSlots is { Count: > 0 })
                        extra.Add($"EquipSlots={string.Join(";", x.EquipmentSlots.Select(e => $"{e.SlotIndex}:{(e.EquipmentId ?? "?")}:{e.Confidence:F2}"))}");
                    o.Add($"    [{x.SlotIndex}] {nm(x.CharacterId)} {string.Join(" ", extra)}" +
                        (x.CardRegion is { } cr
                            ? $" |card({cr.X * 2559:F0},{cr.Y * 1439:F0},{cr.Width * 2559:F0}x{cr.Height * 1439:F0})"
                            : ""));
                }
                if (inZone.Count == 0) o.Add($"    (空)");
            }
            var syn = state.ActiveSynergies?.Value ?? [];
            o.Add($"  羁绊({syn.Count}): " + (syn.Count == 0 ? "(空)" :
                string.Join(", ", syn.Select(s => $"{s.SynergyId}:{s.ActiveCount}/next{s.NextThreshold?.ToString() ?? "满"}"))));
            var simp = state.SimpleEquipmentIds?.Value ?? [];
            o.Add($"  场上普通装备({simp.Count}): " + (simp.Count == 0 ? "(空)" : string.Join(",", simp.Select(em))));
            var spec = state.SpecialItemIds?.Value ?? [];
            o.Add($"  特殊物品(去重{spec.Count}): " + (spec.Count == 0 ? "(空)" : string.Join(",", spec.Select(em))));
            // Inventory 每槽明细（定位特殊物品，2026-08-19）
            var invState = state.InventorySlots?.Value ?? [];
            o.Add($"  Inventory每槽: " + string.Join(" | ",
                invState.Select(sb => $"{sb.SlotIndex}:{(sb.ItemId ?? "-")}:{sb.Occupancy}:{sb.Confidence:F2}:q{(sb.Quantity?.ToString() ?? "-")}")));
            // 统计各类物品数量（优先角标 Quantity，缺省按占用格计1）
            var itemCount = invState
                .Where(s => s.Occupancy == EquipmentSlotOccupancy.Equipped)
                .GroupBy(s => s.ItemId ?? "").ToDictionary(
                    g => g.Key,
                    g => g.Sum(s => s.Quantity ?? 1));
            o.Add($"  物品数量(角标): " + (itemCount.Count == 0 ? "(空)" :
                string.Join(", ", itemCount.Select(kv => $"{em(string.IsNullOrEmpty(kv.Key) ? null : kv.Key)}x{kv.Value}"))));
            o.Add($"  投资环境={state.InvestmentEnvironmentId?.Status}:{state.InvestmentEnvironmentId?.Value}");
            var stratId = state.InvestmentStrategyIds?.Value ?? [];
            o.Add($"  投资策略({stratId.Count}): {string.Join(",", stratId)}");
            var neg = state.NegativeAffixIds?.Value ?? [];
            o.Add($"  负面词条({neg.Count}): {string.Join(",", neg)}");
            allOut.AddRange(o);
        }

        var outTxt = string.Join("\n", allOut);
        System.Console.WriteLine(outTxt);
        var outDir = Path.Combine(Path.GetTempPath(), "cwbackdiag");
        System.IO.Directory.CreateDirectory(outDir);
        System.IO.File.WriteAllText(
            Path.Combine(outDir, "CLIP_RESULT.txt"), outTxt);
    }

    static IReadOnlyList<CharacterCardTemplateDefinition> LoadTemplates(GameDataCatalog g)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dir = Path.Combine(repo, "data", "4.4", "character-card-templates");
        return g.CurrencyWarsCharacters.SelectMany(ch =>
            Directory.GetFiles(dir, $"{ch.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(ch.Id, ch.Name, f))).ToList();
    }
}
