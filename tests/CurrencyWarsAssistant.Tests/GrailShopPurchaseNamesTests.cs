using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// M5 动态商店白名单（2026-09-02 用户拍板）：
/// 名单一律运行时从官方数据按羁绊解析（命杯=命运圣杯羁绊成员；银河学者=银河学者羁绊成员），
/// 不再手写——手写名单曾漏掉艾丝妲/阮•梅（隐雷复盘）。
/// 银河学者条件购买：已拥有其中之一→追加另一个不同的；同帧双银河学者→N14 仪式都买。
/// </summary>
public class GrailShopPurchaseNamesTests
{
    private static string RepoRoot { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly GameDataCatalog GameData = GameDataCatalogLoader.Load(
        Path.Combine(RepoRoot, "data", "4.4"));

    private sealed class ExecutorHolder(GameDataCatalog gameData)
    {
        public GrailOperationExecutor Executor { get; } = new(
            null!, null!, null!, null!, new GrailRunStateHolder(), gameData);
    }

    private static IReadOnlySet<string> Build(params string[] owned) =>
        new ExecutorHolder(GameData).Executor.BuildShopPurchaseNames(
            new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void GrailBondMembers_All_In_Purchase_Names()
    {
        var executor = new ExecutorHolder(GameData).Executor;
        var grailMembers = executor.GrailBondMemberNames;
        var names = Build();

        Assert.Contains("远坂凛", names);
        Assert.Contains("吉尔伽美什", names);
        Assert.Contains("Saber", names);
        Assert.Contains("Archer", grailMembers);
        foreach (var member in grailMembers)
        {
            Assert.Contains(member, names);
        }
    }

    [Fact]
    public void NoGalaxyScholarOwned_DoesNotAddAny()
    {
        var names = Build();

        Assert.DoesNotContain("真理医生", names);
        Assert.DoesNotContain("黑塔", names);
        Assert.DoesNotContain("艾丝妲", names);
        Assert.DoesNotContain("阮•梅", names);
        Assert.DoesNotContain("大黑塔", names);
    }

    [Fact]
    public void OneGalaxyScholarOwned_AddsAllOtherDifferentOnes()
    {
        // 场上已有真理医生（银河学者①）→ 其余四个不同银河学者全部入购买名单
        //（含 1 费艾丝妲——本次事故主角，货架刷出即应购买）。
        var names = Build("真理医生");

        Assert.Contains("黑塔", names);
        Assert.Contains("艾丝妲", names);
        Assert.Contains("阮•梅", names);
        Assert.Contains("大黑塔", names);
        Assert.DoesNotContain("真理医生", names);
    }

    [Fact]
    public void ShelfWithAnotherTarget_TriggersFollowUpPass()
    {
        var executor = new ExecutorHolder(GameData).Executor;
        var purchaseNames = executor.BuildShopPurchaseNames(
            new HashSet<string>(["真理医生"], StringComparer.OrdinalIgnoreCase));
        var ownedAfterFirstBuy = new HashSet<string>(
            ["真理医生", "艾丝妲"], StringComparer.OrdinalIgnoreCase);

        // 货架同帧还有艾丝妲（第一遍已买别的银河学者）→ 再来一遍
        Assert.True(GrailOperationExecutor.HasMoreShopTargetsOnShelf(
            ["艾丝妲", "藿藿"], purchaseNames,
            new HashSet<string>(["真理医生"], StringComparer.OrdinalIgnoreCase)));
        // 买完艾丝妲后货架无剩余目标 → 不再来
        Assert.False(GrailOperationExecutor.HasMoreShopTargetsOnShelf(
            ["艾丝妲", "藿藿"], purchaseNames, ownedAfterFirstBuy));
    }
}

public class GrailSameFrameScholarTargetsTests
{
    private static string RepoRoot { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly GameDataCatalog GameData = GameDataCatalogLoader.Load(
        Path.Combine(RepoRoot, "data", "4.4"));

    private static readonly GrailOperationExecutor Executor =
        new(null!, null!, null!, null!, new GrailRunStateHolder(), GameData);

    private static IReadOnlyList<string> Detect(
        string[] shelf, string[] owned) =>
        GrailOperationExecutor.SameFrameScholarTargets(
            shelf,
            owned,
            (IReadOnlySet<string>)new HashSet<string>(
                Executor.GetBondMemberNames("银河学者"),
                StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void SameFrameTwoUnownedScholars_ZeroOwned_ReturnsBoth()
    {
        // 2026-09-02 实测事故：货架同帧 [黑塔、远坂凛、万敌、阿格莱雅、艾丝妲]，
        // 零持有时两个学者都应成为购买目标（此前分支缺失→一个都没买）。
        var targets = Detect(
            ["黑塔", "远坂凛", "万敌", "阿格莱雅", "艾丝妲"], []);

        Assert.Equal(2, targets.Count);
        Assert.Contains("黑塔", targets);
        Assert.Contains("艾丝妲", targets);
    }

    [Fact]
    public void SingleScholarOnShelf_ZeroOwned_ReturnsEmpty()
    {
        var targets = Detect(["黑塔", "翡翠"], []);

        Assert.Empty(targets);
    }

    [Fact]
    public void SameFrameScholars_OneAlreadyOwned_ReturnsEmpty()
    {
        // 已持有其一 → 由 BuildShopPurchaseNames 的条件分支接管，此处不再返回。
        var targets = Detect(["黑塔", "艾丝妲"], ["黑塔"]);

        Assert.Empty(targets);
    }

    [Fact]
    public void DuplicateScholarNames_DedupedToOne_ReturnsEmpty()
    {
        var targets = Detect(["黑塔", "黑塔", "翡翠"], []);

        Assert.Empty(targets);
    }
}
