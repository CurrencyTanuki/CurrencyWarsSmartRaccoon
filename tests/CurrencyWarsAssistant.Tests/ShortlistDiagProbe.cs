using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CurrencyWarsAssistant.Tests;

/// <summary>横评 warp 槽0卡面：①Shortlist 里藿藿(c69)在不在前32；
/// ②Rank(全模板) 各角色真实分数——验证"完全不像的角色为何也高到0.499"。</summary>
public sealed class ShortlistDiagProbe
{
    // 2026-09-05：取证帧源已随磁盘卫生清理（runs/run-20260818-163242），探针无数据必挂；
    // 恢复数据或改指新取证目录时移除本 Skip。
    [Fact(Skip = "2026-09-05 取证帧已清理（run-20260818-163242）：诊断探针数据缺席时跳过")]
    public void DumpShortlistAndRank()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var screens = Path.Combine(appData, "CurrencyWarsSmartRaccoon", "runs",
            "run-20260818-163242", "screenshots");
        var outDir = Path.Combine(Path.GetTempPath(), "cwbackdiag");

        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var recognizer = new OpenCvCharacterCardRecognizer(candidateLimit: 32);
        var templates = LoadTemplates(gameData);

        var frame = CaptureFrameLoader.LoadFile(Path.Combine(screens, "20260818-084639619.png"));
        dynamic pair = Wrap((object)frame, 6);
        using var warpedBgr = ToBgr(pair.Item1);
        var rects = (System.Collections.IEnumerable)pair.Item2;
        var items = rects.Cast<object>().ToList();
        dynamic r0 = items[0];
        var rc = new OpenCvSharp.Rect((int)r0.X, (int)r0.Y, (int)r0.Width, (int)r0.Height);

        var lines = new List<string> { $"warp size={warpedBgr.Width}x{warpedBgr.Height} slot0={rc}" };

        // Shortlist：藿藿在不在候选
        using (var slotImage = new Mat(warpedBgr, rc))
        {
            var shortlistMeth = recognizer.GetType().GetMethod(
                "Shortlist", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var shortlist = (System.Collections.IEnumerable)shortlistMeth.Invoke(
                recognizer, new object[] { slotImage, templates })!;
            var list = shortlist.Cast<object>().ToList();
            var has = list.Any(t => ((dynamic)t).CharacterId == "currency_wars_character_69");
            lines.Add($"shortlist.Count={list.Count}  huohuo(c69)In={has}");
        }

        // Rank：全模板真实分数（BGR searchImage）
        using (var slotImage = new Mat(warpedBgr, rc))
        {
            var options = CharacterCardRecognitionOptions.Standard;
            var rankMeth = recognizer.GetType().GetMethod(
                "Rank", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ranked = (System.Collections.IEnumerable)rankMeth.Invoke(
                recognizer, new object[] { slotImage, templates, options })!;
            var results = new List<(string id, double conf)>();
            foreach (var r in ranked)
            {
                dynamic pairResult = r;
                string id = ((dynamic)pairResult.Item1).CharacterId;
                double conf = (double)pairResult.Item2;
                results.Add((id, conf));
            }
            lines.Add("=== RANK(全模板) top ===");
            foreach (var (id, conf) in results.OrderByDescending(x => x.conf).Take(12))
                lines.Add($"  {id}: {conf:F3}");
            lines.Add($"... 藿藿(c69) => " +
                string.Join(", ", results.Where(x => x.id == "currency_wars_character_69").Select(x => $"{x.conf:F3}")));
            lines.Add($"=== 明显像/不像对照 ===");
        }

        System.IO.File.WriteAllLines(Path.Combine(outDir, "SHORTLIST_RANK.txt"), lines);
        System.IO.File.WriteAllText(Path.Combine(outDir, "SHORTLIST_DONE.txt"), "ok");
    }

    static Mat ToBgr(dynamic frame)
    {
        var w = (int)frame.Width;
        var h = (int)frame.Height;
        var pixels = (byte[])frame.BgraPixels;
        using var bgra = new Mat(h, w, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, bgra.Data, pixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    static object Wrap(object frame, int slotCount)
    {
        var asm = Assembly.Load("CurrencyWarsAssistant.Tasks");
        var geom = asm.GetType("CurrencyWarsAssistant.Tasks.BackRowPerspectiveGeometry")!;
        var method = geom.GetMethod("WarpBackRow", BindingFlags.Public | BindingFlags.Static)!;
        return method.Invoke(null, new[] { frame, slotCount })!;
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
