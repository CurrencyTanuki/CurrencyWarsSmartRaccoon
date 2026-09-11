using System.Text.Json;
using System.IO;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

// VisionCli: 截图 → 板面角色识别 JSON（复用 Vision 库，零 UI）
// 用法: dotnet run --project src/CurrencyWarsAssistant.VisionCli -- <截图路径> [后台槽位数]
return Run(args);

static int Run(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("用法: VisionCli <截图路径> [后台槽位数=3]");
        return 2;
    }
    var imagePath = args[0];
    int backSlots = args.Length > 1 ? int.Parse(args[1]) : 3;

    // 1. 读图并归一到 1920 参考宽
    using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
    if (src.Empty())
    {
        Console.Error.WriteLine("FAIL: 无法读取图片 " + imagePath);
        return 2;
    }
    if (src.Width != 1920)
    {
        var scale = 1920.0 / src.Width;
        Cv2.Resize(src, src, new OpenCvSharp.Size(1920, (int)Math.Round(src.Height * scale)));
    }
    Cv2.CvtColor(src, src, ColorConversionCodes.BGR2BGRA);
    var bgra = new byte[src.Rows * src.Cols * 4];
    System.Runtime.InteropServices.Marshal.Copy(src.Data, bgra, 0, bgra.Length);
    var frame = new CaptureFrame(
        src.Width, src.Height, src.Width * 4, bgra,
        new PixelRect(0, 0, src.Width, src.Height), DateTimeOffset.Now);

    // 2. 槽位（与 Phase2OperationalScreenshotAnalyzer 同款：前 4 槽 + 后台 N 槽）
    var slots = new List<PixelRect>();
    slots.AddRange(Phase2RecognitionRegions.PreparationCharacterSlots1920.Take(4));
    slots.AddRange(Phase2RecognitionRegions.BackCharacterSlots1920(backSlots));

    // 3. 模板（同 App.LoadCharacterCardTemplates：{id}__*.png 全变体）
    var dataDir = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "4.4"));
    var templateDir = Path.Combine(dataDir, "character-card-templates");
    if (!Directory.Exists(templateDir))
    {
        Console.Error.WriteLine("FAIL: 模板目录不存在 " + templateDir);
        return 2;
    }
    var templates = new List<CharacterCardTemplateDefinition>();
    foreach (var file in Directory.GetFiles(templateDir, "*__*.png"))
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith("bench_empty") || name.StartsWith("bench_special"))
            continue;
        var id = name.Split("__")[0];
        templates.Add(new CharacterCardTemplateDefinition(id, id, file));
    }
    Console.Error.WriteLine($"模板 {templates.Count} 张 | 帧 {frame.Width}x{frame.Height} | 槽位 {slots.Count}");

    // 4. 识别
    using var recognizer = new OpenCvCharacterCardRecognizer();
    var results = recognizer.Recognize(
        frame, templates, slots, CharacterCardRecognitionOptions.Standard);

    // 5. 输出 JSON
    var payload = new
    {
        source = imagePath,
        frameSize = new[] { frame.Width, frame.Height },
        slots = results.Select(r => new
        {
            slot = r.SlotIndex,
            state = r.State.ToString(),
            characterId = r.CharacterId,
            displayName = r.DisplayName,
            confidence = Math.Round(r.Confidence, 4),
            starLevel = r.StarLevel,
            currentCost = r.CurrentCost,
            runnerUp = r.RunnerUpCharacterId,
            runnerUpConfidence = Math.Round(r.RunnerUpConfidence, 4),
            bounds = new[] { r.ReferenceBounds.X, r.ReferenceBounds.Y, r.ReferenceBounds.Width, r.ReferenceBounds.Height }
        })
    };
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
