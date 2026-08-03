using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

public sealed class GamePageClassifierTests
{
    [Fact]
    public void SettlementDetailStructureIsContextEvidenceButPreparationIsNot()
    {
        Assert.True(RewardSettlementDetailEvidence.IsMatch(
            LoadFrame(Path.Combine(FixtureDirectory, "challenge_failed.jpg"))));
        Assert.False(RewardSettlementDetailEvidence.IsMatch(
            LoadFrame(Path.Combine(FixtureDirectory, "preparation_1_1.jpg"))));
    }

    [Fact]
    public void CurrencyWarsHomeContextEvidenceAcceptsOverlayScreenshotButRejectsPreparation()
    {
        Assert.True(CurrencyWarsHomeEvidence.IsMatch(
            LoadFrame(Path.Combine(FixtureDirectory, "currency_wars_home_overlay_2048x1152.png"))));
        Assert.True(CurrencyWarsHomeEvidence.IsMatch(
            LoadFrame(Path.Combine(
                FixtureDirectory,
                "currency_wars_home_recovery_2048x1152.jpg"))));
        Assert.False(CurrencyWarsHomeEvidence.IsMatch(
            LoadFrame(Path.Combine(FixtureDirectory, "preparation_1_1.jpg"))));
    }

    private static readonly (string FixtureFile, string ExpectedPageId)[]
        ReplayPages =
        [
            ("normal_hud.jpg", "normal_hud"),
            ("normal_hud_notification.jpg", "normal_hud"),
            ("normal_hud_rivet_town_2560x1440.png", "normal_hud"),
            ("normal_hud_rivet_town_bright_2560x1440.png", "normal_hud"),
            ("guide_shell.jpg", "guide_shell"),
            ("guide_currency_wars.jpg", "guide_currency_wars"),
            ("update_popup.jpg", "update_popup"),
            ("score_popup.jpg", "score_popup"),
            ("abandon_settlement_prompt.jpg", "abandon_settlement_prompt"),
            ("challenge_failed.jpg", "challenge_failed"),
            ("challenge_failed_2_5_health_depleted_user.png", "challenge_health_depleted"),
            ("currency_wars_home.jpg", "currency_wars_home"),
            ("mode_selection.jpg", "mode_selection"),
            ("rank_difficulty.jpg", "rank_difficulty"),
            ("rank_difficulty_a6.jpg", "rank_difficulty"),
            ("rank_difficulty_a4.jpg", "rank_difficulty"),
            ("rank_difficulty_in_progress.jpg", "rank_difficulty_in_progress"),
            ("enemy_overview.jpg", "enemy_overview"),
            ("enemy_overview_animation.jpg", "enemy_overview"),
            ("plane_progress.jpg", "plane_progress"),
            ("investment_environment.jpg", "investment_environment"),
            ("preparation_1_1.jpg", "preparation_1_1"),
            ("preparation_1_1_after_shop_batch_2048x1152.png", "preparation_1_1"),
            ("shop_open_1_1.jpg", "reward_shop"),
            ("shop_open_1_2.jpg", "reward_shop"),
            ("reward_shop_after_two_purchases.jpg", "reward_shop"),
            ("reward_shop_after_two_purchases_2048x1152.png", "reward_shop"),
            ("battle_1_1.jpg", "reward_battle"),
            ("reward_battle_pause_2048x1152.png", "reward_battle_pause"),
            ("incomplete_lineup_prompt.jpg", "incomplete_lineup_prompt"),
            ("challenge_success_1_1.jpg", "challenge_success"),
            ("preparation_1_2.jpg", "preparation_1_2"),
            ("investment_strategy.jpg", "investment_strategy"),
            ("companion_selection_single_unselected_2048x1152.png", "companion_selection"),
            ("companion_selection_dual_selected_2048x1152.png", "companion_selection"),
            ("companion_selection_post_preparation_2048x1152.png", "preparation_1_1")
        ];

    [Fact]
    public void ClassifierRecognizesAllPrivacySafeReplayFrames()
    {
        var config = GamePageRecognitionConfig.Load(
            Path.Combine(RepositoryRoot, "config", "page-recognition.1920x1080.json"));
        var classifier = new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
        var companionSelection = Assert.Single(
            config.Pages,
            page => page.Id == "companion_selection");
        Assert.Equal(2, companionSelection.Anchors.Count);
        var normalHud = Assert.Single(
            config.Pages,
            page => page.Id == "normal_hud");
        Assert.True(Assert.Single(normalHud.Anchors).EdgeDetection);
        var rewardBattlePause = Assert.Single(
            config.Pages,
            page => page.Id == "reward_battle_pause");
        Assert.Equal(2, rewardBattlePause.Anchors.Count);
        var rewardShop = Assert.Single(
            config.Pages,
            page => page.Id == "reward_shop");
        Assert.Equal(2, rewardShop.Anchors.Count);
        Assert.Equal(1, rewardShop.MinimumAnchorMatches);
        var incompleteLineupPrompt = Assert.Single(
            config.Pages,
            page => page.Id == "incomplete_lineup_prompt");
        Assert.Equal(2, incompleteLineupPrompt.Anchors.Count);
        foreach (var (fixtureFile, expectedPageId) in ReplayPages)
        {
            var frame = LoadFrame(Path.Combine(FixtureDirectory, fixtureFile));
            var result = classifier.Classify(frame);

            var diagnostics = string.Join(
                "; ",
                classifier.LastDiagnostics
                    .OrderByDescending(item => item.Confidence)
                    .Take(8)
                    .Select(item =>
                        $"{item.PageId}/{item.AnchorId}=" +
                        $"{item.Confidence:P1} (threshold {item.Threshold:P0})"));
            Assert.True(
                result is not null,
                $"{fixtureFile}: no page matched. {diagnostics}");
            Assert.Equal(expectedPageId, result.PageId);
            var minimumConfidence = expectedPageId switch
            {
                "rank_difficulty" => 0.76,
                "normal_hud" => 0.52,
                _ => 0.90
            };
            Assert.True(
                result.Confidence >= minimumConfidence,
                $"{fixtureFile}: {result.Confidence:F4}");
        }
    }

    [Theory]
    [InlineData("reward_shop_refresh_panel")]
    [InlineData("reward_shop_refresh_disabled_panel")]
    public void AlternativeShopAnchorsAcceptEitherVisualState(
        string acceptedAnchorId)
    {
        var page = new GamePageDefinition
        {
            Id = "reward_shop",
            DisplayName = "奖励关商店",
            Priority = 70,
            MinimumAnchorMatches = 1,
            Anchors =
            [
                Anchor("reward_shop_refresh_panel"),
                Anchor("reward_shop_refresh_disabled_panel")
            ]
        };
        var classifier = new TemplateGamePageClassifier(
            new SelectiveTemplateMatcher(acceptedAnchorId),
            [page]);

        var result = classifier.Classify(EmptyFrame());

        Assert.NotNull(result);
        Assert.Equal("reward_shop", result.PageId);
        Assert.Equal(acceptedAnchorId, Assert.Single(result.AnchorMatches).Id);
    }

    [Fact]
    public void AlternativeShopAnchorsStillRejectWhenNeitherStateMatches()
    {
        var page = new GamePageDefinition
        {
            Id = "reward_shop",
            DisplayName = "奖励关商店",
            Priority = 70,
            MinimumAnchorMatches = 1,
            Anchors =
            [
                Anchor("reward_shop_refresh_panel"),
                Anchor("reward_shop_refresh_disabled_panel")
            ]
        };
        var classifier = new TemplateGamePageClassifier(
            new SelectiveTemplateMatcher("different_anchor"),
            [page]);

        Assert.Null(classifier.Classify(EmptyFrame()));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2048, 1152)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void DisabledRefreshShopReplayScalesAcrossSixteenByNine(
        int width,
        int height)
    {
        var config = GamePageRecognitionConfig.Load(
            Path.Combine(RepositoryRoot, "config", "page-recognition.1920x1080.json"));
        var classifier = new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
        using var source = Cv2.ImRead(
            Path.Combine(
                FixtureDirectory,
                "reward_shop_after_two_purchases_2048x1152.png"),
            ImreadModes.Color);
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size(width, height));

        var result = classifier.Classify(ToCaptureFrame(scaled));

        Assert.NotNull(result);
        Assert.Equal("reward_shop", result.PageId);
        Assert.True(result.Confidence >= 0.86, result.Confidence.ToString("F4"));
        Assert.Equal(
            "reward_shop_refresh_disabled_panel",
            Assert.Single(result.AnchorMatches).Id);
    }

    [Theory]
    [InlineData("preparation_1_1.jpg")]
    [InlineData("preparation_1_1_after_shop_batch_2048x1152.png")]
    [InlineData("preparation_1_2.jpg")]
    [InlineData("battle_1_1.jpg")]
    [InlineData("currency_wars_home.jpg")]
    public void ShopAlternativesRejectClosedAndSimilarNonShopPages(string fixtureFile)
    {
        var config = GamePageRecognitionConfig.Load(
            Path.Combine(RepositoryRoot, "config", "page-recognition.1920x1080.json"));
        var shop = Assert.Single(
            config.Pages,
            page => page.Id == "reward_shop");
        var classifier = new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            [shop]);

        Assert.Null(classifier.Classify(
            LoadFrame(Path.Combine(FixtureDirectory, fixtureFile))));
    }

    [Fact]
    public void BattlePauseRequiresBothIndependentAnchors()
    {
        var config = GamePageRecognitionConfig.Load(
            Path.Combine(RepositoryRoot, "config", "page-recognition.1920x1080.json"));
        var pause = Assert.Single(
            config.Pages,
            page => page.Id == "reward_battle_pause");
        var frame = LoadFrame(Path.Combine(
            FixtureDirectory,
            "reward_battle_pause_2048x1152.png"));
        var singleAnchorOnly = new TemplateGamePageClassifier(
            new SelectiveTemplateMatcher(pause.Anchors[0].Id),
            [pause]);

        Assert.Null(singleAnchorOnly.Classify(frame));
    }

    [Fact]
    public void ActiveBattleIsNotAcceptedAsBattlePause()
    {
        var config = GamePageRecognitionConfig.Load(
            Path.Combine(RepositoryRoot, "config", "page-recognition.1920x1080.json"));
        var pause = Assert.Single(
            config.Pages,
            page => page.Id == "reward_battle_pause");
        var classifier = new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            [pause]);

        Assert.Null(classifier.Classify(
            LoadFrame(Path.Combine(FixtureDirectory, "battle_1_1.jpg"))));
    }

    [Fact]
    public void ClassifierLeavesOneTimeUnlockOverlayUnknownForEscapeRecovery()
    {
        var config = GamePageRecognitionConfig.Load(
            Path.Combine(RepositoryRoot, "config", "page-recognition.1920x1080.json"));
        var classifier = new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
        var frame = LoadFrame(
            Path.Combine(FixtureDirectory, "unknown_new_content_unlock.jpg"));

        Assert.Null(classifier.Classify(frame));
    }

    [Fact]
    public void MatcherNormalizesLargerSixteenByNineFrameAndMapsBoundsBack()
    {
        var fixture = Path.Combine(FixtureDirectory, "currency_wars_home.jpg");
        using var source = Cv2.ImRead(fixture, ImreadModes.Color);
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size(2560, 1440));
        var frame = ToCaptureFrame(scaled);
        var templatePath = Path.Combine(
            RepositoryRoot,
            "config",
            "templates",
            "1920x1080",
            "pages",
            "currency-wars-home-title.png");
        var definition = new TemplateDefinition
        {
            Id = "currency_wars_home_title",
            DisplayName = "货币战争零和博弈标题",
            File = templatePath,
            SearchRegion = new NormalizedRect(0, 0.02, 0.24, 0.20),
            Threshold = 0.85
        };

        var result = new OpenCvTemplateMatcher().Find(frame, definition);

        Assert.NotNull(result);
        Assert.InRange(result.ClientBounds.X, 44, 50);
        Assert.InRange(result.ClientBounds.Y, 90, 96);
        Assert.InRange(result.ClientBounds.Width, 398, 402);
        Assert.InRange(result.ClientBounds.Height, 178, 182);
    }

    [Fact]
    public void MatcherRejectsUnsupportedAspectRatio()
    {
        var definition = new TemplateDefinition
        {
            Id = "test",
            DisplayName = "test",
            File = Path.Combine(
                RepositoryRoot,
                "config",
                "templates",
                "1920x1080",
                "pages",
                "currency-wars-home-title.png")
        };
        var frame = new CaptureFrame(
            100,
            100,
            400,
            new byte[40_000],
            new PixelRect(0, 0, 100, 100),
            DateTimeOffset.UtcNow);

        Assert.Null(new OpenCvTemplateMatcher().Find(frame, definition));
    }

    [Fact]
    public void OnePixelManualScreenshotShortfallIsAccepted()
    {
        Assert.True(OpenCvTemplateMatcher.HasSupportedAspectRatio(2559, 1439));
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string FixtureDirectory =>
        Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay");

    private static CaptureFrame LoadFrame(string path)
    {
        using var image = Cv2.ImRead(path, ImreadModes.Color);
        return ToCaptureFrame(image);
    }

    private static CaptureFrame ToCaptureFrame(Mat bgr)
    {
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

    private static TemplateDefinition Anchor(string id) => new()
    {
        Id = id,
        DisplayName = id,
        File = Path.Combine(RepositoryRoot, "config", "page-recognition.1920x1080.json"),
        Threshold = 0.90
    };

    private static CaptureFrame EmptyFrame() => new(
        1920,
        1080,
        1920 * 4,
        new byte[1920 * 1080 * 4],
        new PixelRect(0, 0, 1920, 1080),
        DateTimeOffset.UtcNow);

    private sealed class SelectiveTemplateMatcher(string acceptedAnchorId)
        : ITemplateMatcher
    {
        public TemplateMatchResult? Find(
            CaptureFrame frame,
            TemplateDefinition definition) =>
            Probe(frame, definition);

        public TemplateMatchResult? Probe(
            CaptureFrame frame,
            TemplateDefinition definition) =>
            string.Equals(
                definition.Id,
                acceptedAnchorId,
                StringComparison.OrdinalIgnoreCase)
                ? new TemplateMatchResult(
                    definition.Id,
                    definition.DisplayName,
                    0.99,
                    new PixelRect(0, 0, 1, 1))
                : null;
    }
}
