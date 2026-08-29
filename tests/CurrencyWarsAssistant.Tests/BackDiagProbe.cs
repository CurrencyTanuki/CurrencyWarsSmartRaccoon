using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using System.Text;

namespace CurrencyWarsAssistant.Tests;

// 诊断：对用户截图复刻端到端后台路径（WarpBackRow + BackRow=true 逐槽识别），
// 打印每槽 best/runnerUp/conf/lead/State，定位"霍霍端到端 unknown"根因。
public sealed class BackDiagProbe
{
    static readonly string[] INPUT_PATHS =
    {
        @"C:\Users\zzz81\AppData\Roaming\reasonix\global-workspace\.reasonix\attachments\clipboard-20260820-011854.825808-000009.png",
    };
    // 当前帧后台槽总数（onboarding 由 population-storeLevel 派生，
    // 011854 = population 8, storeLevel 8 → 6）
    const int BackSlotCount = 6;

    [Fact]
    public void Run()
    {
        var repo = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repo, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var recognizer = new OpenCvCharacterCardRecognizer(
            candidateLimit: 32,
            lenientLeadOverCharacterIds: ["currency_wars_character_40", "currency_wars_character_56", "currency_wars_character_72", "currency_wars_character_trailblazer"],
            lenientConfidenceCharacterIds: ["currency_wars_character_05", "currency_wars_character_23"]);
        var templates = LoadTemplates(gameData);

        var sb = new StringBuilder();
        foreach (var path in INPUT_PATHS)
        {
            var frame = CaptureFrameLoader.LoadFile(path);
            sb.AppendLine($"==== {Path.GetFileName(path)} ({frame.Width}x{frame.Height})  BackSlotCount={BackSlotCount} ====");

            // 端到端路径：warp + BackRow=true
            var (warped, warpedSlots) =
                BackRowPerspectiveGeometry.WarpBackRow(frame, Math.Min(BackSlotCount, 9));

            // 全量回退：0..BackSlotCount-1（相对索引）
            var opts = CharacterCardRecognitionOptions.Standard with
            {
                BackRow = true,
                StarBand = StarBand.BackRight,
            };
            var all = recognizer.Recognize(warped, templates, warpedSlots, opts);
            sb.AppendLine("-- warp+BackRow=true 批量（相对索引0..N）--");
            for (var i = 0; i < all.Count; i++)
            {
                var s = all[i];
                var lead = s.Confidence - s.RunnerUpConfidence;
                sb.AppendLine($"  [{i}] State={s.State} id={s.CharacterId ?? "-"} conf={s.Confidence:F3} " +
                    $"runnerUp={s.RunnerUpConfidence:F3} lead={lead:F3} " +
                    $"runnerUpId={s.RunnerUpCharacterId ?? "-"} star={s.StarLevel?.ToString() ?? "-"}");
            }

            // 端到端"增量/应援"路径：单槽循环 Recognize([slots[i]])，同
            // RecognizeCharactersSafely（cheered || absoluteSlotIndices 非 null）分支。
            sb.AppendLine("-- warp+BackRow=true 单槽循环（同端到端增量分支）--");
            for (var i = 0; i < BackSlotCount; i++)
            {
                var recognized = recognizer.Recognize(
                    warped, templates, [warpedSlots[i]],
                    opts with { StarBand = StarBand.BackRight })[0];
                var lead = recognized.Confidence - recognized.RunnerUpConfidence;
                sb.AppendLine($"  [{i}] State={recognized.State} id={recognized.CharacterId ?? "-"} " +
                    $"conf={recognized.Confidence:F3} runnerUp={recognized.RunnerUpConfidence:F3} " +
                    $"lead={lead:F3} runnerUpId={recognized.RunnerUpCharacterId ?? "-"} " +
                    $"star={recognized.StarLevel?.ToString() ?? "-"} ref={recognized.ReferenceBounds}");
            }

            // 端到端"应援槽位"分支复刻（RecognizeCharactersSafely 里 cheered
            // 槽强制 BackRow=false + ExcludeBottomRatio=0.25 + ExcludeSides=true）。
            // 若霍霍位被误判为应援槽，conf 应掉到 ~0.51 且 unknown（前台 0.55 阈值卡掉）。
            sb.AppendLine("-- warp+BackRow=false 应援槽选项（同端到端 cheered 分支）--");
            for (var i = 0; i < BackSlotCount; i++)
            {
                var cheerOpts = opts with
                {
                    BackRow = false,
                    ExcludeBottomRatio = 0.25,
                    ExcludeSides = true,
                };
                var recognized = recognizer.Recognize(
                    warped, templates, [warpedSlots[i]], cheerOpts)[0];
                var lead = recognized.Confidence - recognized.RunnerUpConfidence;
                sb.AppendLine($"  [{i}] State={recognized.State} id={recognized.CharacterId ?? "-"} " +
                    $"conf={recognized.Confidence:F3} runnerUp={recognized.RunnerUpConfidence:F3} " +
                    $"lead={lead:F3} runnerUpId={recognized.RunnerUpCharacterId ?? "-"} " +
                    $"star={recognized.StarLevel?.ToString() ?? "-"}");
            }

            // 空槽分界：后台实际只有 BackSlotCount 人，其余是空/无语。
            for (var i = BackSlotCount; i < all.Count; i++)
            {
                var s = all[i];
                sb.AppendLine($"   [{i}](空槽区) State={s.State} id={s.CharacterId ?? "-"} conf={s.Confidence:F3}");
            }
        }

        var outTxt = sb.ToString();
        System.Console.WriteLine(outTxt);
        var outDir = Path.Combine(Path.GetTempPath(), "cwbackdiag");
        System.IO.Directory.CreateDirectory(outDir);
        System.IO.File.WriteAllText(Path.Combine(outDir, "BACKDIAG.txt"), outTxt);
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
