// AdvisorPipeline.cs —— v1→v2 接线组合层（清单 F5"一行调用接上"的实装，1.2.131）。
// 流程：攻略册容错加载 → GuideMatcher.Match（v1，产出每攻略 Similarity）→
//       相似度按文件名映射喂 MatcherV2.Recommend（v2.1）→ 双报告返回。
// 信源置信度：playbook.sourceRefs.title 命中 source_confidence 精确源名（子串）即取该系数；
// 未命中回落 MatcherV2Tuning.DefaultSourceConfidence。自动映射为启发式，v2.2 可替换为显式映射表。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CurrencyWarsAssistant.Advisor;
using CurrencyWarsAdvisor.GuidePlaybooks;
// 别名：Contracts.cs 在本命名空间另有 record GuidePlaybook（主契约）——文件加载器类必须显式绑定。
using GuidePlaybookFile = CurrencyWarsAdvisor.GuidePlaybooks.GuidePlaybook;

public static class AdvisorPipeline
{
    public sealed record LibraryEntry(string FileName, GuidePlaybookFile Playbook, string? LoadError);

    public sealed record PipelineResult(
        IReadOnlyList<LibraryEntry> Library,
        MatchReport Match,
        V2Report V2);

    /// <summary>容错加载攻略册目录：单文件失败记入 LoadError 不中断（v0 形制旧文件可共存）。</summary>
    public static IReadOnlyList<LibraryEntry> LoadPlaybooks(string playbookDir)
    {
        var list = new List<LibraryEntry>();
        foreach (var f in Directory.GetFiles(playbookDir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                list.Add(new LibraryEntry(Path.GetFileName(f), GuidePlaybookFile.Load(f), null));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                list.Add(new LibraryEntry(Path.GetFileName(f), new GuidePlaybookFile(), e.Message));
            }
        }
        return list;
    }

    /// <summary>v1→v2 组合：Match 的每攻略 Similarity 作为 MatcherV2 的匹配度因子；
    /// 信源置信度按 sourceRefs.title 启发式映射（命中 source_confidence 精确源名）。同快照必同报告。</summary>
    public static PipelineResult Run(RunSnapshot snapshot, string playbookDir, string dataRoot)
    {
        var library = LoadPlaybooks(playbookDir);
        var playable = library.Where(x => x.LoadError is null).Select(x => x.Playbook).ToList();
        var match = GuideMatcher.Match(snapshot, playable);

        // guideId → 文件名（MatcherV2 的 necessity 键空间=文件名）
        var idToFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var x in library.Where(x => x.LoadError is null))
            idToFile.TryAdd(x.Playbook.GuideId, x.FileName); // 重复 guideId 保首个（容错加载哲学）
        var sims = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var m in match.Matches)
        {
            var key = idToFile.TryGetValue(m.GuideId, out var f) ? f : m.GuideId;
            sims[key] = m.Similarity;
        }

        // 信源置信度启发映射：sourceRefs.title 含 source_confidence 精确源名（子串）→ 该系数。
        // P2（终审）修复：KnownSourceNames 读静态缓存，必须先 EnsureLoaded，否则首次运行全空。
        MatcherV2.EnsureLoaded(dataRoot);
        var sourceConf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in library.Where(x => x.LoadError is null))
        {
            var coeff = SourceConfidenceForPlaybook((GuidePlaybookFile)entry.Playbook);
            if (coeff is { } c) sourceConf[entry.FileName] = c;
        }

        var board = new V2Board(
            FieldIds: snapshot.BoardCharacterIds is { Status: ObservationStatus.Known } b && b.Value is not null
                ? b.Value : Array.Empty<string>(),
            BenchIds: snapshot.BenchCharacterIds is { Status: ObservationStatus.Known } be && be.Value is not null
                ? be.Value : Array.Empty<string>(),
            Level: snapshot.StoreLevel is { Status: ObservationStatus.Known } l && l.Value is int lv ? lv : 1,
            Gold: snapshot.Economy is { Status: ObservationStatus.Known } g && g.Value is int gd ? gd : 0,
            TeamHp: snapshot.Health is { Status: ObservationStatus.Known } h && h.Value is int hp ? hp : 100);

        var v2 = MatcherV2.Recommend(board, dataRoot, sims, guideSourceConf: sourceConf);
        return new PipelineResult(library, match, v2);
    }

    /// <summary>启发式：sourceRefs.title 含 source_confidence 精确源名（子串）→ 该源系数；未命中 → null（回落默认 0.7）。</summary>
    public static double? SourceConfidenceForPlaybook(GuidePlaybookFile pb)
    {
        if (pb.SourceRefs is not { Count: > 0 }) return null;
        foreach (var r in pb.SourceRefs)
        {
            var title = r.Title;
            if (string.IsNullOrEmpty(title)) continue;
            foreach (var name in MatcherV2.KnownSourceNames())
            {
                if (title.Contains(name, StringComparison.Ordinal))
                    return MatcherV2.SourceConfidenceFor(name);
            }
        }
        return null;
    }
}
