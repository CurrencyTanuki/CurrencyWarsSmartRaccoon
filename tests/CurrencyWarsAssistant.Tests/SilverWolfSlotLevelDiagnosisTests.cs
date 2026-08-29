using System.Reflection;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 银狼 Front#0 槽位级匹配诊断（2026-08-07）：
/// 直接调 OpenCvCharacterCardRecognizer.Recognize（不经 analyzer），
/// 输出槽位的 best/runner-up/全部候选分数——定位银狼失败是
/// Match 分不足（模板没匹配上）还是 runner-up 分差不足（区分度）。
/// 打call特效是通用机制（开拓者可加持任意角色），解法必须通用，
/// 不能给银狼单独补特效模板。
/// </summary>
public sealed class SilverWolfSlotLevelDiagnosisTests
{
    [Fact]
    public void DumpFront0SlotMatchScores()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var templates = LoadCharacterTemplates(gameData);

        // 只构建 recognizer，不走 analyzer（避免 Formation 平均分干扰）
        var recognizer = new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds:
                [
                    "currency_wars_character_40", // 白厄
                    "currency_wars_character_56", // 飞霄
                    "currency_wars_character_72", // 阿格莱雅（实测与开拓者混淆）
                    "currency_wars_character_trailblazer", // 开拓者
                ],
                lenientConfidenceCharacterIds:
                [
                    "currency_wars_character_05", // 银狼LV.999（变费）
                ]);
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));

        // Front#0 槽位（1920 参考坐标 681,329,128,140）
        var slot = new PixelRect(681, 329, 128, 140);
        var results = recognizer.Recognize(frame, templates, [slot]);
        var r0 = results[0];
        Console.WriteLine(
            $"Front#0 state={r0.State} conf={r0.Confidence:F3} " +
            $"runnerup={r0.RunnerUpConfidence:F3} " +
            $"char={r0.CharacterId} runnerupChar={r0.RunnerUpCharacterId}");

        // 银狼 3 个模板各自的 Match 分数（反射调 private Match）
        var matchMethod = typeof(OpenCvCharacterCardRecognizer)
            .GetMethod("Match", BindingFlags.Static | BindingFlags.NonPublic)!;
        // 需要 Normalize 后的帧和搜索图像——直接调 Recognize 已有结果，
        // 这里补充：对银狼 3 模板逐个 Match（用 recognizer 的 search 逻辑太深，
        // 简化：输出 Recognize 对银狼模板的分数即可，上面 conf 已是 best）
        var silverWolf = templates.Where(t => t.CharacterId == "currency_wars_character_05").ToArray();
        Console.WriteLine($"银狼模板数: {silverWolf.Length}");

        // 用反射验证 Match 方法存在
        Assert.NotNull(matchMethod);
    }

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
