using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 结果导向验证（用户 2026-08-07）：全部用户参考图跑完整识别链路，
/// 输出每张图的阵容识别数 + 装备识别数——直接回答"能不能识别并记录"。
/// </summary>
public sealed class UserReferenceFullRecognitionTests
{
    private readonly ITestOutputHelper _output;

    public UserReferenceFullRecognitionTests(ITestOutputHelper output)
        => _output = output;

    [Theory]
    [InlineData("user_ref_000032_prep22.png", "000032(2-2)")]
    [InlineData("user_ref_000033_prep24_with_mergemats.png", "000033(2-4合成前)")]
    [InlineData("user_ref_000034_prep24_after_merge.png", "000034(2-4合成后)")]
    [InlineData("user_ref_000035_prep27.png", "000035(2-7)")]
    [InlineData("user_ref_000036_prep31.png", "000036(3-1)")]
    [InlineData("user_ref_000037_prep32.png", "000037(3-2)")]
    [InlineData("user_ref_000036_prep31.png", "000038(3-4)")]
    [InlineData("user_ref_000039.png", "000039")]
    public async Task FullRecognitionOnUserRefs(string file, string label)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05",
                ]),
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"),
            gameData,
            new WindowsOfflineOcr("en-US"));

        var path = Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file);
        if (!File.Exists(path))
        {
            _output.WriteLine($"[{label}] 缺少文件 {file}");
            return;
        }

        var frame = CaptureFrameLoader.LoadFile(path);
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            $"fixture:{file}",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);

        var formation = state.Formation.Value ?? [];
        var known = formation.Count(s =>
            s.CharacterId is not null &&
            !s.CharacterId.StartsWith(
                "unknown-formation-unit",
                StringComparison.Ordinal));
        var withEquip = formation.Count(s => (s.EquipmentIds ?? []).Count > 0);
        var synergies = (state.ActiveSynergies.Value ?? []).Count;
        var inventory = (state.InventorySlots.Value ?? []).Count;
        _output.WriteLine(
            $"[{label}] 阵容 {known}/{formation.Count} 识别 | 装备 {withEquip}/{formation.Count} 有装备 | " +
            $"羁绊 {synergies} 个 | 库存 {inventory} 格");
        // 输出识别出的角色（用于对照参考图）
        foreach (var s in formation.Where(s =>
                     s.CharacterId is not null &&
                     !s.CharacterId.StartsWith(
                         "unknown-formation-unit",
                         StringComparison.Ordinal)))
        {
            _output.WriteLine(
                $"    {s.Zone}#{s.SlotIndex} {s.CharacterId} 装备[{string.Join(",", s.EquipmentIds)}]");
        }
    }

    [Theory]
    [InlineData("user_ref_000036_prep31.png", "000036")]
    [InlineData("user_ref_000037_prep32.png", "000037")]
    [InlineData("user_ref_000035_prep27.png", "000035")]
    [InlineData("user_ref_000036_prep31.png", "000038")]
    [InlineData("user_ref_000039.png", "000039")]
    public void DebugCheerDetection(string file, string label)
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file));
        // Front#0 固定坐标（1920参考系）→ 帧像素
        var slot = new Core.PixelRect(
            (int)(681 * frame.Width / 1920d),
            (int)(329 * frame.Height / 1080d),
            (int)(128 * frame.Width / 1920d),
            (int)(140 * frame.Height / 1080d));
        var method = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "DetectCallEffect",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static,
            null,
            [typeof(CaptureFrame), typeof(Core.PixelRect)],
            null);
        var result = (bool)method!.Invoke(null, [frame, slot])!;
        _output.WriteLine($"[{label}] Front#0 应援检测: {result}");
    }

    [Theory]
    [InlineData("user_ref_000036_prep31.png", "000036")]
    [InlineData("user_ref_000037_prep32.png", "000037")]
    public void DebugSilverWolfScoreWithBottomExclude(string file, string label)
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file));
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                Path.Combine(dataDirectory, "character-card-templates"),
                $"{character.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, f)))
            .ToList();
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds: ["currency_wars_character_05"]);
        var slot = new Core.PixelRect(
            (int)(681 * frame.Width / 1920d),
            (int)(329 * frame.Height / 1080d),
            (int)(128 * frame.Width / 1920d),
            (int)(140 * frame.Height / 1080d));
        var r1 = recognizer.Recognize(frame, templates, [slot],
            CharacterCardRecognitionOptions.Standard)[0];
        var r2 = recognizer.Recognize(frame, templates, [slot],
            CharacterCardRecognitionOptions.Standard with { ExcludeBottomRatio = 0.25 })[0];
        _output.WriteLine(
            $"[{label}] Front#0 银狼: 标准={r1.Confidence:F3} ({r1.CharacterId}) | " +
            $"避开底部25%={r2.Confidence:F3} ({r2.CharacterId})");
    }

    [Theory]
    [InlineData("user_ref_000036_prep31.png", "000036")]
    [InlineData("user_ref_000037_prep32.png", "000037")]
    public void DebugBatchVsSingleSlot(string file, string label)
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file));
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                Path.Combine(dataDirectory, "character-card-templates"),
                $"{character.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, f)))
            .ToList();
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds: ["currency_wars_character_05"]);
        // 多槽批量（前 4 个 Front 槽）
        var frontSlots = new List<Core.PixelRect>();
        foreach (var s in Phase2RecognitionRegions.PreparationCharacterSlots1920.Take(4))
        {
            frontSlots.Add(new Core.PixelRect(
                (int)(s.X * frame.Width / 1920d),
                (int)(s.Y * frame.Height / 1080d),
                (int)(s.Width * frame.Width / 1920d),
                (int)(s.Height * frame.Height / 1080d)));
        }
        var batch = recognizer.Recognize(frame, templates, frontSlots,
            CharacterCardRecognitionOptions.Standard with { ExcludeBottomRatio = 0.25 });
        _output.WriteLine($"[{label}] 批量 Front#0: {batch[0].CharacterId} conf={batch[0].Confidence:F3}");

        // 单槽（相同坐标）
        var single = recognizer.Recognize(frame, templates, [frontSlots[0]],
            CharacterCardRecognitionOptions.Standard with { ExcludeBottomRatio = 0.25 })[0];
        _output.WriteLine($"[{label}] 单槽 Front#0: {single.CharacterId} conf={single.Confidence:F3}");
    }

    [Theory]
    [InlineData("user_ref_000035_prep27.png", "000035")]
    [InlineData("user_ref_000036_prep31.png", "000036")]
    public void DebugBackSlotCounts(string file, string label)
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file));
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                Path.Combine(dataDirectory, "character-card-templates"),
                $"{character.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, f)))
            .ToList();
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds: ["currency_wars_character_05"]);

        // 6/7/8/9 格坐标分别识别后台，统计识别成功数
        for (var n = 6; n <= 9; n++)
        {
            var slots = Phase2RecognitionRegions.BackCharacterSlots1920(n)
                .Select(s => new Core.PixelRect(
                    (int)(s.X * frame.Width / 1920d),
                    (int)(s.Y * frame.Height / 1080d),
                    (int)(s.Width * frame.Width / 1920d),
                    (int)(s.Height * frame.Height / 1080d)))
                .ToList();
            var results = recognizer.Recognize(frame, templates, slots,
                CharacterCardRecognitionOptions.Standard);
            var ok = results.Count(r =>
                r.CharacterId is not null &&
                !r.CharacterId.StartsWith("unknown-formation-unit", StringComparison.Ordinal));
            _output.WriteLine($"[{label}] 后台{n}格: 识别 {ok}/{results.Count} 个角色");
        }
    }

    [Theory]
    [InlineData("user_ref_000032_prep22.png", "000032")]
    [InlineData("user_ref_000035_prep27.png", "000035")]
    public async Task DebugEconomyHealthFields(string file, string label)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds: ["currency_wars_character_05"]),
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"),
            gameData,
            new WindowsOfflineOcr("en-US"));
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            file));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            $"fixture:{file}",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);
        _output.WriteLine(
            $"[{label}] 等级={state.PlayerProgress.Value?.Level ?? -1} 经验={state.PlayerProgress.Value?.Experience ?? -1} 血量={state.Health.Value} " +
            $"利息={state.Interest.Value.ToString()} " +
            $"累计消费={state.CumulativeSpend.Value.ToString()} " +
            $"商店Lv={state.StoreLevel.Value.ToString()} " +
            $"扳手数={state.DismantleToolCount.Value.ToString()} " +
            $"节点={state.NodeId.Value ?? "null"} 难度={state.EnemyDifficulty.Value.ToString()}");
    }

    [Fact]
    public void DebugFrontFiveSlots000036()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                Path.Combine(dataDirectory, "character-card-templates"),
                $"{character.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, f)))
            .ToList();
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds: ["currency_wars_character_05"]);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000036_prep31.png"));
        // 前台 5 个候选位置（中心 479/738/887/1032/1185）
        var centers = new[] { 479, 738, 887, 1032, 1185 };
        foreach (var c in centers)
        {
            var slot = new Core.PixelRect(
                (int)((c - 64) * frame.Width / 1920d),
                (int)(329 * frame.Height / 1080d),
                (int)(128 * frame.Width / 1920d),
                (int)(140 * frame.Height / 1080d));
            var r = recognizer.Recognize(frame, templates, [slot],
                CharacterCardRecognitionOptions.Standard)[0];
            _output.WriteLine($"前台 x{c}: {r.CharacterId} conf={r.Confidence:F3} star={r.StarLevel}");
        }
    }

    [Fact]
    public void DebugSilverWolfWithSideExclude()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                Path.Combine(dataDirectory, "character-card-templates"),
                $"{character.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, f)))
            .ToList();
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds: ["currency_wars_character_05"]);
        foreach (var (file, label) in new[]
                 {
                     ("user_ref_000036_prep31.png", "000036"),
                     ("user_ref_000039.png", "000039"),
                     ("user_ref_000036_prep31.png", "000038"),
                 })
        {
            var frame = CaptureFrameLoader.LoadFile(Path.Combine(
                RepositoryRoot,
                "tests",
                "CurrencyWarsAssistant.Tests",
                "Fixtures",
                "PageReplay",
                file));
            var slot = new Core.PixelRect(
                (int)(681 * frame.Width / 1920d),
                (int)(329 * frame.Height / 1080d),
                (int)(128 * frame.Width / 1920d),
                (int)(140 * frame.Height / 1080d));
            var r1 = recognizer.Recognize(frame, templates, [slot],
                CharacterCardRecognitionOptions.Standard)[0];
            var r2 = recognizer.Recognize(frame, templates, [slot],
                CharacterCardRecognitionOptions.Standard with
                {
                    ExcludeBottomRatio = 0.25,
                    ExcludeSides = true
                })[0];
            var r3 = recognizer.Recognize(frame, templates, [slot],
                CharacterCardRecognitionOptions.Standard with
                {
                    ExcludeBottomRatio = 0.35,
                    ExcludeSides = true
                })[0];
            _output.WriteLine($"{label} Front0: 标准={r1.CharacterId} {r1.Confidence:F3} | " +
                              $"避25+侧={r2.CharacterId} {r2.Confidence:F3} | " +
                              $"避35+侧={r3.CharacterId} {r3.Confidence:F3}");
        }
    }

    [Fact]
    public async Task DebugAnalyzerPath000036()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds: ["currency_wars_character_05"]),
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"),
            gameData,
            new WindowsOfflineOcr("en-US"));
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000036_prep31.png"));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:000036-path",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);
        foreach (var s in state.Formation.Value ?? [])
        {
            _output.WriteLine($"{s.Zone}{s.SlotIndex}: {s.CharacterId} conf={s.Confidence:F3} 应援={s.IsCheered}");
        }
    }

    [Fact]
    public void DebugCheerScaledCoords()
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000038_prep34.png"));
        // analyzer 前置检测用 boardReferenceSlots（缩放后坐标）
        var slot = new Core.PixelRect(
            (int)(681 * frame.Width / 1920d),
            (int)(329 * frame.Height / 1080d),
            (int)(128 * frame.Width / 1920d),
            (int)(140 * frame.Height / 1080d));
        var method = typeof(Phase2OperationalScreenshotAnalyzer).GetMethod(
            "DetectCallEffect",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static,
            null,
            [typeof(CaptureFrame), typeof(Core.PixelRect)],
            null);
        var result = (bool)method!.Invoke(null, [frame, slot])!;
        _output.WriteLine($"000038 Front#0 缩放坐标应援检测: {result} (slot={slot.X},{slot.Y},{slot.Width}x{slot.Height})");
    }

    [Fact]
    public void DebugBatch000038()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                Path.Combine(dataDirectory, "character-card-templates"),
                $"{character.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, f)))
            .ToList();
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds: ["currency_wars_character_05"]);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000038_prep34.png"));
        // 批量（全部 11 槽：前台4 + 后台7——与 analyzer 一致）
        var slots = new List<Core.PixelRect>();
        foreach (var s in Phase2RecognitionRegions.PreparationCharacterSlots1920.Take(4))
        {
            slots.Add(new Core.PixelRect(
                (int)(s.X * frame.Width / 1920d),
                (int)(s.Y * frame.Height / 1080d),
                (int)(s.Width * frame.Width / 1920d),
                (int)(s.Height * frame.Height / 1080d)));
        }
        foreach (var s in Phase2RecognitionRegions.BackCharacterSlots1920(7))
        {
            slots.Add(new Core.PixelRect(
                (int)(s.X * frame.Width / 1920d),
                (int)(s.Y * frame.Height / 1080d),
                (int)(s.Width * frame.Width / 1920d),
                (int)(s.Height * frame.Height / 1080d)));
        }
        var batch = recognizer.Recognize(frame, templates, slots,
            CharacterCardRecognitionOptions.Standard);
        for (var i = 0; i < batch.Count; i++)
        {
            _output.WriteLine($"批量 Front{i}: {batch[i].CharacterId} conf={batch[i].Confidence:F3}");
        }
        // 单槽 Front0
        var single = recognizer.Recognize(frame, templates, [slots[0]],
            CharacterCardRecognitionOptions.Standard)[0];
        _output.WriteLine($"单槽 Front0: {single.CharacterId} conf={single.Confidence:F3}");
    }

    [Fact]
    public async Task DebugStoreLevelScan()
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000035_prep27.png"));
        var ocr = new CurrencyWarsAssistant.Tasks.WindowsOfflineOcr("zh-Hans");
        // 左下角候选区域（购买经验按钮 Lv.X）
        var candidates = new[]
        {
            new Core.NormalizedRect(0.05, 0.85, 0.20, 0.13),
            new Core.NormalizedRect(0.10, 0.90, 0.20, 0.08),
            new Core.NormalizedRect(0.12, 0.92, 0.15, 0.06),
            new Core.NormalizedRect(0.05, 0.95, 0.25, 0.04),
            new Core.NormalizedRect(0.15, 0.88, 0.10, 0.10),
            new Core.NormalizedRect(0.08, 0.93, 0.20, 0.05),
            new Core.NormalizedRect(0.16, 0.83, 0.06, 0.04),
            new Core.NormalizedRect(0.17, 0.84, 0.05, 0.035),
            new Core.NormalizedRect(0.05, 0.82, 0.12, 0.06),
            new Core.NormalizedRect(0.06, 0.83, 0.10, 0.05),
            new Core.NormalizedRect(0.08, 0.84, 0.08, 0.04),
        };
        foreach (var (r, i) in candidates.Select((r, i) => (r, i)))
        {
            var px = r.ToPixels(frame.Width, frame.Height);
            var text = await ocr.RecognizeAsync(
                frame,
                new Core.PixelRect(px.X, px.Y, px.Width, px.Height),
                CancellationToken.None);
            _output.WriteLine($"区域[{i}] ({px.X},{px.Y},{px.Width}x{px.Height}): OCR=[{text.Text}]");
        }
    }

    [Fact]
    public async Task DebugSynergy000035()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40",
                    "currency_wars_character_56",
                    "currency_wars_character_72",
                    "currency_wars_character_trailblazer",
                ],
                lenientConfidenceCharacterIds: ["currency_wars_character_05"]),
            LoadCharacterTemplates(gameData),
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new WindowsOfflineOcr("zh-Hans"),
            gameData,
            new WindowsOfflineOcr("en-US"));
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000035_prep27.png"));
        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:000035-syn",
            EmptySnapshot(frame.CapturedAt),
            CancellationToken.None);
        foreach (var s in state.ActiveSynergies.Value ?? [])
        {
            _output.WriteLine($"羁绊 {s.SynergyId}: ActiveCount={s.ActiveCount} 下一级={s.NextThreshold}");
        }
        // 场上角色（银狼应在）
        foreach (var f in state.Formation.Value ?? [])
        {
            if (f.CharacterId is not null && !f.CharacterId.StartsWith("unknown"))
            {
                _output.WriteLine($"  {f.Zone}{f.SlotIndex}: {f.CharacterId}");
            }
        }
    }

    [Fact]
    public void DebugAllSlots000036()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                Path.Combine(dataDirectory, "character-card-templates"),
                $"{character.Id}__*.png")
                .Select(f => new CharacterCardTemplateDefinition(
                    character.Id, character.Name, f)))
            .ToList();
        using var recognizer = new OpenCvCharacterCardRecognizer(
            lenientLeadOverCharacterIds:
            [
                "currency_wars_character_40",
                "currency_wars_character_56",
                "currency_wars_character_72",
                "currency_wars_character_trailblazer",
            ],
            lenientConfidenceCharacterIds: ["currency_wars_character_05"]);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000036_prep31.png"));
        // 全部槽位（前台4+后台6+备战席9）
        var slots = new List<Core.PixelRect>();
        foreach (var s in Phase2RecognitionRegions.PreparationCharacterSlots1920)
        {
            slots.Add(new Core.PixelRect(
                (int)(s.X * frame.Width / 1920d),
                (int)(s.Y * frame.Height / 1080d),
                (int)(s.Width * frame.Width / 1920d),
                (int)(s.Height * frame.Height / 1080d)));
        }
        foreach (var s in Phase2RecognitionRegions.BenchCharacterSlots1920)
        {
            slots.Add(new Core.PixelRect(
                (int)(s.X * frame.Width / 1920d),
                (int)(s.Y * frame.Height / 1080d),
                (int)(s.Width * frame.Width / 1920d),
                (int)(s.Height * frame.Height / 1080d)));
        }
        var results = recognizer.Recognize(frame, templates, slots,
            CharacterCardRecognitionOptions.Standard);
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var label = i < 10 ? (i < 4 ? $"Front{i}" : $"Back{i}") : $"Bench{i - 10}";
            _output.WriteLine($"{label}: {r.CharacterId} conf={r.Confidence:F3}");
        }
    }

    private static RunSnapshot EmptySnapshot(DateTimeOffset asOf) => new()
    {
        RunId = "user-ref-full",
        AsOf = asOf
    };

    private static IReadOnlyList<CharacterCardTemplateDefinition>
        LoadCharacterTemplates(GameDataCatalog gameData)
    {
        var directory = Path.Combine(RepositoryRoot, "data", "4.4", "character-card-templates");
        return gameData.CurrencyWarsCharacters
            .SelectMany(character => Directory.GetFiles(
                directory,
                $"{character.Id}__*.png")
                .Select(file => new CharacterCardTemplateDefinition(
                    character.Id,
                    character.Name,
                    file)))
            .ToList();
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
