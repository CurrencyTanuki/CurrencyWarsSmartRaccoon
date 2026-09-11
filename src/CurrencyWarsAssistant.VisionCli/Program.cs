using System.Text.Json;
using System.IO;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

// VisionCli: 截图 → 板面角色识别 JSON（复用 Vision 库，零 UI）
// 用法: dotnet run --project src/CurrencyWarsAssistant.VisionCli -- <截图路径> [后台槽位数=6] [数据目录]
return Run(args);

static int Run(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("用法: VisionCli <截图路径> [后台槽位数=6] [数据目录]");
        return 2;
    }
    var imagePath = args[0];
    // 审计 P2（默认后台槽数 3 无效）：默认值 3→6——识别库 BackCharacterSlots1920
    // 仅支持 6-9，旧默认 3 会静默回退到 6 格坐标，识别位置全错。
    var backSlots = 6;
    if (args.Length > 1)
    {
        // 审计 P2（int.Parse 崩溃）：args[1] 改 TryParse——非数字或 <1 时
        // 打印用法提示并以退出码 2 退出（与"参数缺失→用法+exit 2"风格一致）。
        if (!int.TryParse(args[1], out var parsedBackSlots) || parsedBackSlots < 1)
        {
            Console.Error.WriteLine("用法: VisionCli <截图路径> [后台槽位数=6] [数据目录]");
            return 2;
        }
        // 审计 P2（默认后台槽数 3 无效）：显式传 1-5 或 10+ 时不静默回退，
        // 明确报"后台槽数须为 6-9"并以退出码 2 退出。
        if (parsedBackSlots is < 6 or > 9)
        {
            Console.Error.WriteLine(
                "FAIL: 后台槽数须为 6-9（收到 " + parsedBackSlots + "）");
            return 2;
        }
        backSlots = parsedBackSlots;
    }

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
    // 审计 P1（后台槽识别参数）：前台/后台槽位列表分开保存，便于分两次 Recognize。
    var frontSlots = Phase2RecognitionRegions
        .PreparationCharacterSlots1920.Take(4).ToList();
    var backSlotRects = Phase2RecognitionRegions.BackCharacterSlots1920(backSlots);
    var slots = frontSlots.Concat(backSlotRects).ToList();

    // 3. 模板（同 App.LoadCharacterCardTemplates：{id}__*.png 全变体）
    // 审计 P3（dataDir 上跳 5 级脆弱）：保留上跳 5 级的默认值，但支持第 3 参
    // 显式传数据目录；显式目录不存在时报错并以退出码 2 退出。
    string dataDir;
    if (args.Length > 2)
    {
        dataDir = Path.GetFullPath(args[2]);
        if (!Directory.Exists(dataDir))
        {
            Console.Error.WriteLine("FAIL: 数据目录不存在 " + dataDir);
            return 2;
        }
    }
    else
    {
        dataDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "4.4"));
    }
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
        // 审计 P2（识别器构造对齐 App）：special_unit_* 模板 Kind=SpecialOccupied
        //（同 App.LoadCharacterCardTemplates：特殊单位无星级、lead-over 门归零、
        // 内置宽松置信度），误配成 Character 会被 0.04 区分度门判 Uncertain。
        var kind = id.StartsWith("special_unit_", StringComparison.Ordinal)
            ? CharacterCardTemplateKind.SpecialOccupied
            : CharacterCardTemplateKind.Character;
        templates.Add(new CharacterCardTemplateDefinition(id, id, file, kind));
    }
    Console.Error.WriteLine($"模板 {templates.Count} 张 | 帧 {frame.Width}x{frame.Height} | 槽位 {slots.Count}");

    // 4. 识别
    // 审计 P2（识别器构造对齐 App）：原样复制 App.xaml.cs 识别器注册处（约
    // 250-274 行）传入的放宽集合——无参构造会让白厄/飞霄/阿格莱雅/开拓者
    // 被 0.04 全局区分度门判 Uncertain、银狼/符玄被 0.55 置信度门判 Uncertain。
    using var recognizer = new OpenCvCharacterCardRecognizer(
        candidateLimit: 32,
        // 区分度门放宽角色（用户 2026-08-07）：白厄/飞霄/开拓者等
        // 银灰发系相似角色，与用户实机模板 runner-up 分差小
        //（0.013-0.047），全局 0.04 会判 Uncertain——仅这些角色
        // 放宽到 0.02，其他角色保持 0.04 保守。
        lenientLeadOverCharacterIds:
        [
            "currency_wars_character_40", // 白厄
            "currency_wars_character_56", // 飞霄
            "currency_wars_character_72", // 阿格莱雅（实测与开拓者混淆）
            "currency_wars_character_trailblazer", // 开拓者
        ],
        // 变费角色置信度门槛放宽（用户 2026-08-07）：银狼 3/4/5 费
        // 背景色不同，匹配分天然偏低（detail 5/5/15 下 0.465）——
        // 仅银狼放宽到 0.46，其他角色保持 0.55。
        lenientConfidenceCharacterIds:
        [
            "currency_wars_character_05", // 银狼LV.999（变费）
            // 2026-08-15 实机：符玄后台 0.535 卡 0.55 阈值 0.015
            //（卡面多变体，与银狼同类问题）。
            "currency_wars_character_23", // 符玄
        ]);
    // 审计 P1（后台槽识别参数）：前台/后台分两次 Recognize。前台 Standard；
    // 后台用 Standard with { BackRow = true }（触发 6px 内缩裁框 + 后台宽松
    // 颜色惩罚线 0.80 / 置信度 0.50 / 区分度 0.015）且星档 StarBand.BackRight
    //（后台星在卡面右下）——同 PreparationFormation.Grail.cs（约 504 行）与
    // Phase2OperationalScreenshotAnalyzer（约 7304-7315 行）的参数形态。
    // 旧写法一次 Standard 识别全部槽位，后台在能量特效污染下大概率判 Uncertain。
    var frontResults = recognizer.Recognize(
        frame, templates, frontSlots, CharacterCardRecognitionOptions.Standard);
    var backResults = recognizer.Recognize(
        frame, templates, backSlotRects,
        CharacterCardRecognitionOptions.Standard with
        {
            BackRow = true,
            StarBand = StarBand.BackRight
        });
    // 后台这次调用只传后台槽矩形，识别器返回相对槽位（0..N-1），须重映射回
    // 绝对槽位（4..）再合并（同 Phase2 的 remapBackSlotIndex）；后台结果
    // 覆盖/填充后台槽位，前台结果不动。
    var results = frontResults
        .Concat(backResults.Select((r, i) =>
            r with { SlotIndex = frontSlots.Count + i }))
        .ToList();

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
