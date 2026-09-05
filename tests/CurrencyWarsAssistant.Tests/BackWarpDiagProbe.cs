using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CurrencyWarsAssistant.Tests;

/// <summary>裁 warp 后每后台槽放大图 + 用生产 characterRecognizer 打印每槽
/// best 角 + conf + runnerUp + lead，定位 2 星霍霍为何 conf0.5<0.55。</summary>
public sealed class BackWarpDiagProbe
{
    // 2026-09-05：取证帧源已随磁盘卫生清理（runs/run-20260818-163242），探针无数据必挂；
    // 恢复数据或改指新取证目录时移除本 Skip。
    [Fact(Skip = "2026-09-05 取证帧已清理（run-20260818-163242）：诊断探针数据缺席时跳过")]
    public void DumpWarpedBackCrops()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var screens = Path.Combine(appData, "CurrencyWarsSmartRaccoon", "runs",
            "run-20260818-163242", "screenshots");
        var outDir = Path.Combine(Path.GetTempPath(), "cwbackdiag");
        Directory.CreateDirectory(outDir);

        var shot = "20260818-084639619.png";

        // production同参 识别器（同 analyzer：lenientLeadOver/lenientConfidence）
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var recognizer = new OpenCvCharacterCardRecognizer(
            candidateLimit: 32,
            lenientLeadOverCharacterIds: ["currency_wars_character_40", "currency_wars_character_56", "currency_wars_character_72", "currency_wars_character_trailblazer"],
            lenientConfidenceCharacterIds: ["currency_wars_character_05", "currency_wars_character_23"]);
        var templates = LoadTemplates(gameData);

        var frame = CaptureFrameLoader.LoadFile(Path.Combine(screens, shot));
        foreach (var count in new[] { 6 })
        {
            dynamic pair = InvokeWarp(frame, count);
            using var warpedMat = CopyToMat((object)pair.Item1);
            var rects = (System.Collections.IEnumerable)pair.Item2;
            var rectList = new List<PixelRect>();
            foreach (var item in rects)
            {
                dynamic r = item;
                rectList.Add(new PixelRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
            }
            // 裁每槽放大图（2x）
            foreach (var (i, r) in rectList.Select((r, i) => (i, r)))
            {
                if (r.Width <= 0 || r.Height <= 0 || r.X < 0 || r.Y < 0 || r.X + r.Width > 1920 || r.Y + r.Height > 1080)
                {
                    continue;
                }
                using var crop = new Mat(warpedMat, new OpenCvSharp.Rect(r.X, r.Y, r.Width, r.Height));
                var big = new Mat();
                Cv2.Resize(crop, big, new OpenCvSharp.Size(r.Width * 2, r.Height * 2), 0, 0, InterpolationFlags.Linear);
                Cv2.ImWrite(Path.Combine(outDir, $"SLOT_{i}.png"), big);
            }
            // 生产识别 warp 帧全后台槽 → 打印
            var results = recognizer.Recognize(
                (CaptureFrame)pair.Item1, templates, rectList,
                CharacterCardRecognitionOptions.Standard);
            var lines = new List<string>();
            foreach (var (i, s) in results.Select((s, i) => (i, s)))
            {
                var r = rectList[i];
                lines.Add($"slot{i}: rect={r.Width}x{r.Height}@({r.X},{r.Y}) best={s.CharacterId ?? "-"} conf={s.Confidence:F3} runnerUp={s.RunnerUpConfidence:F3}");
            }
            // 模板尺寸（每角色取第一张模板读取）
            using (var probeImg = new Mat())
            {
                foreach (var kv in templates.GroupBy(t => t.CharacterId))
                {
                    using (var t = Cv2.ImRead(kv.First().File, ImreadModes.Grayscale))
                    {
                        lines.Add($"tmpl {kv.Key}: {t.Width}x{t.Height}");
                    }
                }
            }
            System.IO.File.WriteAllLines(Path.Combine(outDir, "SLOT_CONF.txt"), lines);
            System.IO.File.WriteAllText(Path.Combine(outDir, "PROBE_DONE.txt"), "ok");
        }
    }

    static IReadOnlyList<CharacterCardTemplateDefinition> LoadTemplates(GameDataCatalog g)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dir = Path.Combine(repo, "data", "4.4", "character-card-templates");
        return g.CurrencyWarsCharacters.SelectMany(ch =>
            Directory.GetFiles(dir, $"{ch.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(ch.Id, ch.Name, f))).ToList();
    }

    static Mat CopyToMat(dynamic frame)
    {
        var w = (int)frame.Width;
        var h = (int)frame.Height;
        var pixels = (byte[])frame.BgraPixels;
        var m = new Mat(h, w, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, m.Data, pixels.Length);
        return m;
    }

    static object InvokeWarp(object frame, int slotCount)
    {
        var asm = Assembly.Load("CurrencyWarsAssistant.Tasks");
        var geom = asm.GetType("CurrencyWarsAssistant.Tasks.BackRowPerspectiveGeometry")!;
        var method = geom.GetMethod("WarpBackRow", BindingFlags.Public | BindingFlags.Static)!;
        return method.Invoke(null, new[] { frame, slotCount })!;
    }
}
