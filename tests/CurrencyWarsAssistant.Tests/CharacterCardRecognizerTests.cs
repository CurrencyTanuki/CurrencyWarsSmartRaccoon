using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

public sealed class CharacterCardRecognizerTests
{
    private static readonly IReadOnlyList<PixelRect> BenchSlots =
    [
        new(383, 844, 114, 137),
        new(506, 844, 119, 137),
        new(633, 844, 117, 137),
        new(759, 844, 114, 137),
        new(883, 844, 116, 137),
        new(1005, 844, 116, 137),
        new(1128, 844, 116, 137),
        new(1250, 844, 116, 137),
        new(1374, 844, 116, 137)
    ];

    private static readonly IReadOnlyList<PixelRect> BoardSlots =
    [
        new(681, 329, 128, 140),
        new(827, 329, 122, 140),
        new(972, 329, 120, 140),
        new(1114, 329, 120, 140),
        new(535, 600, 140, 145),
        new(687, 600, 130, 145),
        new(829, 600, 130, 145),
        new(966, 600, 130, 145),
        new(1108, 600, 130, 145),
        new(1258, 600, 130, 145)
    ];

    [Fact]
    public void RecognizesInitialBenchCardsAndLeavesUnusedSlotsEmpty()
    {
        var templates = LoadTemplates();
        var frame = LoadFrame(
            Path.Combine(FixtureDirectory, "preparation_1_1.jpg"));
        using var recognizer = new OpenCvCharacterCardRecognizer();

        var results = recognizer.Recognize(frame, templates, BenchSlots);

        Assert.Equal(
            new[] { "三月七", "飞霄", "银枝", "不死途" },
            results.Take(4).Select(item => item.DisplayName).ToArray());
        Assert.All(
            results.Take(4),
            item => Assert.Equal(
                CharacterCardSlotState.Recognized,
                item.State));
        Assert.All(
            results.Skip(4),
            item => Assert.Equal(CharacterCardSlotState.Empty, item.State));
    }

    [Fact]
    public void LivePreparationDoesNotTurnPatternedEmptyBoardSlotsIntoUnknownUnits()
    {
        var templates = LoadTemplates();
        var frame = LoadFrame(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "preparation-1-7-user.png"));
        using var recognizer = new OpenCvCharacterCardRecognizer();

        var board = recognizer.Recognize(frame, templates, BoardSlots);
        var bench = recognizer.Recognize(frame, templates, BenchSlots);

        Assert.Equal(5, board.Count(item =>
            item.State == CharacterCardSlotState.Recognized));
        Assert.Equal(5, board.Count(item =>
            item.State == CharacterCardSlotState.Empty));
        Assert.DoesNotContain(board, item =>
            item.State == CharacterCardSlotState.Uncertain);
        Assert.Equal(2, bench.Count(item =>
            item.State == CharacterCardSlotState.Recognized));
        Assert.Equal(7, bench.Count(item =>
            item.State == CharacterCardSlotState.Empty));
        Assert.DoesNotContain(bench, item =>
            item.State == CharacterCardSlotState.Uncertain);
    }

    [Fact]
    public void LivePreparationRecognizesCharacterIdentityAndStarLevelPerSlot()
    {
        var templates = LoadTemplates();
        var frame = LoadFrame(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "preparation-1-7-user.png"));
        using var recognizer = new OpenCvCharacterCardRecognizer();

        var board = recognizer.Recognize(frame, templates, BoardSlots);
        var bench = recognizer.Recognize(frame, templates, BenchSlots);
        var actual = board
            .Where(item => item.State == CharacterCardSlotState.Recognized)
            .Select(item => $"board:{item.SlotIndex}:{item.CharacterId}:{item.StarLevel}")
            .Concat(bench
                .Where(item => item.State == CharacterCardSlotState.Recognized)
                .Select(item => $"bench:{item.SlotIndex}:{item.CharacterId}:{item.StarLevel}"))
            .ToArray();

        Assert.Equal(
            new[]
            {
                "board:0:currency_wars_character_24:1",
                "board:1:currency_wars_character_21:1",
                "board:2:currency_wars_character_59:3",
                "board:3:currency_wars_character_47:1",
                "board:6:currency_wars_character_02:1",
                "bench:1:currency_wars_character_39:1",
                "bench:2:currency_wars_character_23:1"
            },
            actual);

        var stableFrame = LoadFrame(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            "run-171955-preparation-1-3-stable.png"));
        var stableActual = recognizer
            .Recognize(stableFrame, templates, BoardSlots)
            .Where(item => item.State == CharacterCardSlotState.Recognized)
            .Select(item => $"board:{item.SlotIndex}:{item.CharacterId}:{item.StarLevel}")
            .Concat(recognizer
                .Recognize(stableFrame, templates, BenchSlots)
                .Where(item => item.State == CharacterCardSlotState.Recognized)
                .Select(item => $"bench:{item.SlotIndex}:{item.CharacterId}:{item.StarLevel}"))
            .ToArray();

        Assert.Equal(
            new[]
            {
                "board:0:currency_wars_character_04:1",
                "board:1:currency_wars_character_03:1",
                "board:2:currency_wars_character_53:2",
                "board:3:currency_wars_character_43:1",
                "bench:0:currency_wars_character_55:1"
            },
            stableActual);
    }

    [Fact]
    public void DecorativeSlotBorderDoesNotMakeAnEmptyInteriorLookOccupied()
    {
        var slot = new PixelRect(681, 329, 128, 140);
        var frame = CreateDecoratedEmptySlotFrame(slot);
        using var recognizer = new OpenCvCharacterCardRecognizer();

        var result = Assert.Single(recognizer.Recognize(
            frame,
            LoadTemplates(),
            [slot]));

        Assert.Equal(CharacterCardSlotState.Empty, result.State);
    }

    [Fact]
    public void EveryCharacterTemplateIsRecognizableAcrossBenchSlots()
    {
        var templates = LoadTemplates();
        using var recognizer = new OpenCvCharacterCardRecognizer();

        foreach (var template in templates)
        {
            using (var sourceTemplate = Cv2.ImRead(
                       template.File,
                       ImreadModes.Color))
            {
                Assert.Equal(111, sourceTemplate.Width);
                Assert.Equal(127, sourceTemplate.Height);
            }

            var batch = Enumerable
                .Repeat(template, BenchSlots.Count)
                .ToArray();
            var frame = CreateSyntheticBenchFrame(batch);

            var results = recognizer.Recognize(frame, templates, BenchSlots);

            Assert.Equal(
                batch.Select(item => item.DisplayName),
                results.Select(item => item.DisplayName));
            Assert.All(
                results,
                item => Assert.Equal(
                    template.Kind == CharacterCardTemplateKind.SpecialOccupied
                        ? CharacterCardSlotState.SpecialOccupied
                        : CharacterCardSlotState.Recognized,
                    item.State));
        }
    }

    [Fact]
    public void RecognizesUserFailureReplayAndSeparatesBaiEFromFeixiao()
    {
        var templates = LoadTemplates();
        var baiE = templates.Single(item => item.DisplayName == "白厄");
        using (var sourceTemplate = Cv2.ImRead(
                   baiE.File,
                   ImreadModes.Color))
        {
            Assert.Equal(111, sourceTemplate.Width);
            Assert.Equal(127, sourceTemplate.Height);
        }

        var frame = LoadFrame(
            Path.Combine(
                FixtureDirectory,
                "preparation_white_failure_2048x1152.png"));
        using var recognizer = new OpenCvCharacterCardRecognizer();

        var results = recognizer.Recognize(frame, templates, BenchSlots);

        Assert.Equal(
            new[] { "乱破", "阿格莱雅", "阿格莱雅", "白厄" },
            results.Take(4).Select(item => item.DisplayName).ToArray());
        Assert.All(
            results.Take(4),
            item => Assert.Equal(
                CharacterCardSlotState.Recognized,
                item.State));
        Assert.All(
            results.Skip(4),
            item => Assert.Equal(CharacterCardSlotState.Empty, item.State));
        Assert.Equal("飞霄", results[3].RunnerUpDisplayName);
        Assert.True(results[3].Confidence >= 0.58);
        Assert.True(
            results[3].Confidence - results[3].RunnerUpConfidence >= 0.07);
    }

    [Fact]
    public void RecognizesLiveBenchReplays()
    {
        var templates = LoadTemplates();
        using var recognizer = new OpenCvCharacterCardRecognizer();
        (string File, string[] Names)[] cases =
        [
            (
                "preparation_five_cards_2048x1152.png",
                ["桑博", "桑博", "飞霄", "万敌", "姬子•启行"]),
            (
                "preparation_six_cards_2048x1152.png",
                ["远坂凛", "吉尔伽美什", "翡翠", "赛飞儿", "远坂凛", "银狼LV.999"]),
            (
                "preparation_feixiao_baie_confusion_2559x1439.png",
                ["阿格莱雅", "藿藿", "飞霄", "砂金", "千冶•刃"]),
            (
                "preparation_nine_cards_low_confidence_2048x1152.png",
                ["藿藿", "吉尔伽美什", "三月七", "星期日", "远坂凛", "椒丘", "黑塔", "万敌", "爻光"])
        ];
        foreach (var test in cases)
        {
            var frame = LoadFrame(Path.Combine(FixtureDirectory, test.File));
            var results = recognizer.Recognize(frame, templates, BenchSlots);

            Assert.Equal(
                test.Names,
                results.Take(test.Names.Length)
                    .Select(item => item.DisplayName)
                    .ToArray());
            Assert.All(
                results.Take(test.Names.Length),
                item => Assert.Equal(
                    CharacterCardSlotState.Recognized,
                    item.State));
            Assert.All(
                results.Skip(test.Names.Length),
                item => Assert.Equal(CharacterCardSlotState.Empty, item.State));
            if (test.File == "preparation_feixiao_baie_confusion_2559x1439.png")
            {
                Assert.Equal("白厄", results[2].RunnerUpDisplayName);
                Assert.True(
                    results[2].Confidence -
                    results[2].RunnerUpConfidence >= 0.07);
            }
            if (test.File == "preparation_nine_cards_low_confidence_2048x1152.png")
            {
                Assert.All(
                    results.Skip(7),
                    item =>
                    {
                        Assert.True(item.Confidence >= 0.58);
                        Assert.True(
                            item.Confidence - item.RunnerUpConfidence >= 0.055);
                    });
            }
        }

        var privilegeBoxFrame = LoadFrame(Path.Combine(
            FixtureDirectory,
            "preparation_privilege_boxes_gold3_2559x1439.png"));
        var privilegeBoxResults = recognizer.Recognize(
            privilegeBoxFrame,
            templates,
            BenchSlots);
        Assert.All(
            privilegeBoxResults.Take(2),
            item =>
            {
                Assert.Equal(
                    CharacterCardSlotState.SpecialOccupied,
                    item.State);
                Assert.Null(item.CharacterId);
                Assert.Equal("特权武装箱", item.DisplayName);
            });
        Assert.Equal(
            new[] { "黑塔", "椒丘", "万敌", "刻律德菈" },
            privilegeBoxResults.Skip(2).Take(4)
                .Select(item => item.DisplayName));
        Assert.All(
            privilegeBoxResults.Skip(2).Take(4),
            item => Assert.Equal(
                CharacterCardSlotState.Recognized,
                item.State));
        Assert.All(
            privilegeBoxResults.Skip(6),
            item => Assert.Equal(CharacterCardSlotState.Empty, item.State));

        using var goldRecognizer = new OpenCvGoldDigitRecognizer();
        var goldTemplates = LoadGoldDigitTemplates();
        (string File, int Gold)[] goldCases =
        [
            ("preparation_1_1.jpg", 3),
            ("preparation_1_2.jpg", 7),
            ("preparation_privilege_boxes_gold3_2559x1439.png", 3)
        ];
        foreach (var goldCase in goldCases)
        {
            var frame = LoadFrame(Path.Combine(
                FixtureDirectory,
                goldCase.File));
            var result = goldRecognizer.Recognize(
                frame,
                goldTemplates,
                new PixelRect(1620, 895, 60, 55));
            Assert.True(
                result.Confidence >= 0.78,
                $"{goldCase.File}: best={result.Confidence:F6}, " +
                $"runner={result.RunnerUpConfidence:F6}");
            Assert.Equal(goldCase.Gold, result.Value);
        }
    }

    [Fact]
    public void UnsupportedAspectRatioReturnsUncertainWithoutGuessing()
    {
        var frame = new CaptureFrame(
            100,
            100,
            400,
            new byte[40_000],
            new PixelRect(0, 0, 100, 100),
            DateTimeOffset.UtcNow);
        using var recognizer = new OpenCvCharacterCardRecognizer();

        var result = Assert.Single(
            recognizer.Recognize(
                frame,
                LoadTemplates(),
                [new PixelRect(0, 0, 100, 100)]));

        Assert.Equal(CharacterCardSlotState.Uncertain, result.State);
        Assert.Null(result.CharacterId);
        Assert.Null(result.DisplayName);
        Assert.Equal(0, result.Confidence);
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));

    private static string FixtureDirectory =>
        Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay");

    private static CharacterCardTemplateDefinition[] LoadTemplates()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var catalog = GameDataCatalogLoader.Load(dataDirectory);
        var templateDirectory = Path.Combine(
            dataDirectory,
            "character-card-templates");
        var templates = catalog.CurrencyWarsCharacters
            .Select(character => new CharacterCardTemplateDefinition(
                character.Id,
                character.Name,
                Directory.GetFiles(
                    templateDirectory,
                    $"{character.Id}__*.png").Single()))
            .ToList();
        templates.Add(new CharacterCardTemplateDefinition(
            "bench_special_privilege_armament_box",
            "特权武装箱",
            Path.Combine(
                templateDirectory,
                "bench_special_privilege_armament_box.png"),
            CharacterCardTemplateKind.SpecialOccupied));
        return templates.ToArray();
    }

    private static GoldDigitTemplateDefinition[] LoadGoldDigitTemplates()
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "data",
            "4.4",
            "gold-digit-templates");
        return new[] { 3, 7 }
            .Select(digit => new GoldDigitTemplateDefinition(
                digit,
                Path.Combine(directory, $"digit_{digit}.png")))
            .ToArray();
    }

    private static CaptureFrame LoadFrame(string path)
    {
        using var bgr = Cv2.ImRead(path, ImreadModes.Color);
        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Cols,
            bgra.Rows,
            checked(bgra.Cols * 4),
            pixels,
            new PixelRect(0, 0, bgra.Cols, bgra.Rows),
            DateTimeOffset.UtcNow);
    }

    private static CaptureFrame CreateSyntheticBenchFrame(
        IReadOnlyList<CharacterCardTemplateDefinition> templates)
    {
        using var bgr = new Mat(
            OpenCvTemplateMatcher.ReferenceHeight,
            OpenCvTemplateMatcher.ReferenceWidth,
            MatType.CV_8UC3,
            Scalar.Black);
        for (var index = 0; index < templates.Count; index++)
        {
            using var source = Cv2.ImRead(
                templates[index].File,
                ImreadModes.Color);
            using var card = new Mat();
            Cv2.Resize(
                source,
                card,
                new Size(111, 127),
                interpolation: InterpolationFlags.Area);
            var slot = BenchSlots[index];
            var target = new Rect(
                slot.X + (slot.Width - card.Width) / 2,
                slot.Y + (slot.Height - card.Height) / 2,
                card.Width,
                card.Height);
            using var targetImage = new Mat(bgr, target);
            card.CopyTo(targetImage);
        }

        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked(bgra.Rows * bgra.Cols * 4)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Cols,
            bgra.Rows,
            checked(bgra.Cols * 4),
            pixels,
            new PixelRect(0, 0, bgra.Cols, bgra.Rows),
            DateTimeOffset.UtcNow);
    }

    private static CaptureFrame CreateDecoratedEmptySlotFrame(PixelRect slot)
    {
        const int width = OpenCvTemplateMatcher.ReferenceWidth;
        const int height = OpenCvTemplateMatcher.ReferenceHeight;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset + 3] = byte.MaxValue;
        }

        for (var y = slot.Y; y < slot.Bottom; y++)
        {
            for (var x = slot.X; x < slot.Right; x++)
            {
                var border = x < slot.X + 10 || x >= slot.Right - 10 ||
                             y < slot.Y + 10 || y >= slot.Bottom - 10;
                var value = border
                    ? (byte)(((x + y) & 1) == 0 ? 240 : 20)
                    : (byte)80;
                var offset = (y * width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
            }
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }
}
