using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 真实帧回放回归：快速刷开局路径关键页面用真实游戏截图识别。
/// 防回归：敌人概览页必须能被分类器识别（上次卡死根因——锚点阈值 90%
/// 过高 + 搜索区域错位，实机帧 88% 匹配被拒，导航等 enemy_overview 永远等不到）。
/// </summary>
public sealed class FastRerollRealFrameReplayTests
{
    [Fact]
    public void EnemyOverviewRealFrames_AreClassifiedAsEnemyOverview()
    {
        var classifier = CreatePageClassifier();
        var files = new[]
        {
            "enemy_overview_short_affixes_2048x1152.png",
            "enemy_overview_stress_reaction_2048x1152.png"
        };
        foreach (var file in files)
        {
            var frame = LoadPageReplay(file);
            var result = classifier.Classify(frame);
            Assert.NotNull(result);
            Assert.Equal(
                "enemy_overview",
                result.PageId);
            Assert.True(
                result.Confidence >= 0.80,
                $"{file}: enemy_overview 匹配分 {result.Confidence:P1} 低于阈值 80%");
        }
    }

    [Fact]
    public void PreparationRealFrames_AreClassifiedAsPreparation()
    {
        var classifier = CreatePageClassifier();
        var files = new[]
        {
            "preparation_1_1_after_shop_batch_2048x1152.png",
            "preparation_five_cards_2048x1152.png",
            "preparation_six_cards_2048x1152.png"
        };
        foreach (var file in files)
        {
            var frame = LoadPageReplay(file);
            var result = classifier.Classify(frame);
            Assert.NotNull(result);
            Assert.Equal(
                "preparation_1_1",
                result.PageId);
        }
    }

    [Fact]
    public void InvestmentStrategyRealFrame_IsClassified()
    {
        var classifier = CreatePageClassifier();
        var frame = LoadPageReplay("investment_strategy_long_gold_2048x1152.png");
        var result = classifier.Classify(frame);
        Assert.NotNull(result);
        Assert.Equal(
            "investment_strategy",
            result.PageId);
    }

    [Fact]
    public void BattleFrame_IsClassifiedAsBattle_AndPauseFrameStaysPause()
    {
        // 防回归：战斗页（实机截图）必须识别为战斗页并映射为 Battle 状态，
        // 战斗暂停页仍识别为 reward_battle_pause。
        // 背景变化（mask 匹配，BetterGI 方案）不应导致战斗页识别失败。
        var classifier = CreatePageClassifier();

        var battleFrame = LoadPageReplay("user_battle_page_213505.png");
        var battleResult = classifier.Classify(battleFrame);
        Assert.NotNull(battleResult);
        Assert.True(
            battleResult.PageId is "battle_generic" or "reward_battle",
            $"战斗页识别成了 {battleResult.PageId}");
        Assert.Equal(
            CurrencyWarsAssistant.Tasks.RewardBattlePageState.Battle,
            CurrencyWarsAssistant.Tasks.RewardBattlePageStateClassifier.Classify(
                battleResult.PageId,
                "preparation_1_1",
                "investment_strategy"));

        var pauseFrame = LoadPageReplay("user_battle_pause_213522.png");
        var pauseResult = classifier.Classify(pauseFrame);
        Assert.NotNull(pauseResult);
        Assert.Equal(
            "reward_battle_pause",
            pauseResult.PageId);
    }

    private static IGamePageClassifier CreatePageClassifier()
    {
        var config = GamePageRecognitionConfig.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "page-recognition.1920x1080.json"));
        return new TemplateGamePageClassifier(
            new OpenCvTemplateMatcher(),
            config.Pages);
    }

    private static CaptureFrame LoadPageReplay(string fileName) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            fileName));

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
}
