using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;
using System.Text;

namespace CurrencyWarsAssistant.Tests;

// 临时诊断：132307 阿格莱雅 slot1(轮滑鞋046) 位，用当前四角 warp 的 crop
// 交给识别器打分，打印 best/candidate(是否 045/046 并列) + 存 crop 图。
public sealed class Equip046DiagProbe
{
    [Fact]
    public void Run()
    {
        var repo = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dataDirectory = Path.Combine(repo, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        const string framePath = @"D:\CWAFix-20260814\tests\CurrencyWarsAssistant.Tests\Fixtures\phase2-2026-07-28\132307.png";
        var frame = CaptureFrameLoader.LoadFile(framePath);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var iconCatalog = Phase2IconTemplateCatalog.Load(dataDirectory);
        // 该角色 Front 装备候选（advanced-equipment 含 simple-equipment）
        var templates = iconCatalog.Where(t =>
            string.Equals(t.Category, "advanced-equipment", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.Category, "simple-equipment", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // 阿格莱雅 card0 slot1 的四角（FrontEquip[3][0][1]），1920 参考系 → warp 52×52
        var quad = CalibrationSlots.FrontEquip[3][0][1];
        using var warp = CalibratedPerspective.WarpOneQuad(
            frame.BgraPixels, frame.Width, frame.Height, quad, 52, 52, sourceReuse: null);
        var px = new byte[checked(52 * 4 * 52)];
        for (var r = 0; r < 52; r++)
        {
            System.Runtime.InteropServices.Marshal.Copy(warp.Ptr(r), px, r * 52 * 4, 52 * 4);
        }
        var slotFrame = new CaptureFrame(52, 52, 52 * 4, px,
            new PixelRect(0, 0, 52, 52), frame.CapturedAt);

        var res = iconRecognizer.RecognizeEquipmentSlotByDHash(
            slotFrame, "advanced-equipment", new NormalizedRect(0, 0, 1, 1), templates);

        var sb = new StringBuilder();
        sb.AppendLine($"quad=FrontEquip[3][0][1] ({string.Join(",", quad.Select(p => $"[{p[0]},{p[1]}]"))})");
        sb.AppendLine($"frame={framePath} ({frame.Width}x{frame.Height})");
        sb.AppendLine($"result: IsKnown={res.IsKnown} TemplateId={res.TemplateId ?? "-"} Conf={res.Confidence:F3} Region={res.Region}");
        sb.AppendLine($"CandidateTemplateIds=[{(res.CandidateTemplateIds is null ? "null" : string.Join(",", res.CandidateTemplateIds))}]");
        // 存 crop
        Directory.CreateDirectory(@"D:\CWAFix-20260814\tools\diag");
        string cropPath = @"D:\CWAFix-20260814\tools\diag\046_slot_warp.png";
        cv2_imwrite(warp, cropPath);
        sb.AppendLine($"crop saved: {cropPath}");
        System.IO.File.WriteAllText(
            @"D:\CWAFix-20260814\tools\diag\EQ046_DIAG.txt", sb.ToString());
        System.Console.WriteLine(sb);
    }

    private static void cv2_imwrite(Mat m, string path)
    {
        using var r = m.Clone();
        Cv2.ImWrite(path, r);
    }
}
