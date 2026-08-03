using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

public sealed class InitialRewardFormationPlannerTests
{
    private readonly InitialRewardFormationPlanner _planner = new();

    [Fact]
    public void DefaultRosterRejectsUnlistedAndAcceptsListedCharacters()
    {
        var rejected = _planner.Plan(
        [
            Bench(0, "三月七", "前台"),
            Bench(1, "花火", "前后台")
        ]);

        Assert.Equal(
            PreparationFormationPlanStatus.NoEligibleCharacter,
            rejected.Status);
        Assert.Empty(rejected.Placements);

        var accepted = _planner.Plan(
        [
            Bench(0, "飞霄", "前台"),
            Bench(1, "灵砂", "前后台"),
            Bench(2, "银枝", "前台"),
            Bench(3, "阿格莱雅", "后台")
        ],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.True(accepted.IsReady);
        Assert.Equal(3, accepted.Placements.Count);
        string[] expected = ["飞霄", "银枝", "阿格莱雅"];
        Assert.Equal(
            expected,
            accepted.Placements
                .Select(item => item.Source.Character.Name)
                .ToArray());
    }

    [Fact]
    public void AppliesFrontBackAndFlexiblePositionRules()
    {
        var plan = _planner.Plan(
        [
            Bench(0, "乱破", "前台"),
            Bench(1, "千冶•刃", "后台"),
            Bench(2, "吉尔伽美什", "前后台")
        ]);

        Assert.True(plan.IsReady);
        Assert.Collection(
            plan.Placements,
            placement =>
            {
                Assert.Equal("乱破", placement.Source.Character.Name);
                Assert.Equal(PreparationLane.Front, placement.Lane);
                Assert.Equal(0, placement.TargetSlot);
            },
            placement =>
            {
                Assert.Equal("千冶•刃", placement.Source.Character.Name);
                Assert.Equal(PreparationLane.Back, placement.Lane);
                Assert.Equal(0, placement.TargetSlot);
            },
            placement =>
            {
                Assert.Equal("吉尔伽美什", placement.Source.Character.Name);
                Assert.Equal(PreparationLane.Front, placement.Lane);
                Assert.Equal(1, placement.TargetSlot);
            });
    }

    [Fact]
    public void UsesAtMostThreeCharactersAndKeepsAFrontlineCandidate()
    {
        var accepted = new HashSet<string>(
            ["后台甲", "后台乙", "后台丙", "前台甲"],
            StringComparer.OrdinalIgnoreCase);
        var plan = _planner.Plan(
        [
            Bench(0, "后台甲", "后台"),
            Bench(1, "后台乙", "后台"),
            Bench(2, "后台丙", "后台"),
            Bench(3, "前台甲", "前台")
        ],
        accepted);

        Assert.True(plan.IsReady);
        Assert.Equal(3, plan.Placements.Count);
        Assert.Contains(
            plan.Placements,
            placement => placement.Source.Character.Name == "前台甲" &&
                         placement.Lane == PreparationLane.Front);
    }

    [Fact]
    public void BackPreferredCharacterCanFillRequiredFrontSlot()
    {
        var accepted = new HashSet<string>(
            ["后台甲", "后台乙"],
            StringComparer.OrdinalIgnoreCase);
        var plan = _planner.Plan(
        [
            Bench(0, "后台甲", "后台"),
            Bench(1, "后台乙", "后台")
        ],
        accepted);

        Assert.True(plan.IsReady);
        Assert.Equal(PreparationLane.Front, plan.Placements[0].Lane);
        Assert.Equal(PreparationLane.Back, plan.Placements[1].Lane);
    }

    [Fact]
    public void LeavesMissingSlotsForShopInsteadOfUsingUnlistedFiller()
    {
        var plan = _planner.Plan(
        [
            Bench(0, "镜流", "后台"),
            Bench(1, "三月七", "前台"),
            Bench(2, "花火", "前后台")
        ]);

        Assert.True(plan.IsReady);
        var placement = Assert.Single(plan.Placements);
        Assert.Equal("镜流", placement.Source.Character.Name);
        Assert.Equal(PreparationLane.Front, placement.Lane);
        Assert.Contains("商店优先补充", plan.Message);
    }

    [Fact]
    public void ShopCompletionNeverDeploysCharactersOutsideRewardRoster()
    {
        var existing = new[]
        {
            Placement(0, "大丽花", "前台", PreparationLane.Front, 0),
            Placement(1, "远坂凛", "前台", PreparationLane.Front, 1)
        };
        var plan = _planner.Plan(
        [
            Bench(2, "花火", "前后台"),
            Bench(5, "藿藿", "后台", "仙舟")
        ],
        new HashSet<string>(
            ["大丽花", "远坂凛"],
            StringComparer.OrdinalIgnoreCase),
        existing);

        Assert.Empty(plan.Placements);
        Assert.Contains("仍缺 1 名", plan.Message);
    }

    [Fact]
    public void LimitsFateGrailToOneCharacterAcrossInitialAndSupplementalFormation()
    {
        var accepted = new HashSet<string>(
            ["远坂凛", "吉尔伽美什", "万敌", "风堇"],
            StringComparer.OrdinalIgnoreCase);
        var initial = _planner.Plan(
        [
            Bench(0, "远坂凛", "前后台", "命运圣杯"),
            Bench(1, "吉尔伽美什", "前后台", "命运圣杯"),
            Bench(2, "万敌", "前台"),
            Bench(3, "风堇", "后台")
        ],
        accepted);

        Assert.Equal(3, initial.Placements.Count);
        Assert.Single(initial.Placements.Where(item =>
            FateGrailFormationPolicy.IsCandidate(item.Source.Character)));
        Assert.Contains(initial.Placements,
            item => item.Source.Character.Name == "远坂凛");
        Assert.DoesNotContain(initial.Placements,
            item => item.Source.Character.Name == "吉尔伽美什");
        Assert.Contains("最多上场 1 名", initial.Message);

        var supplement = _planner.Plan(
        [
            Bench(4, "吉尔伽美什", "前后台", "命运圣杯"),
            Bench(5, "三月七", "前台")
        ],
        new HashSet<string>(
            ["远坂凛", "三月七"],
            StringComparer.OrdinalIgnoreCase),
        [Placement(
            0,
            "远坂凛",
            "前后台",
            PreparationLane.Front,
            0,
            "命运圣杯")]);

        Assert.DoesNotContain(supplement.Placements,
            item => item.Source.Character.Name == "吉尔伽美什");
        Assert.Contains(supplement.Placements,
            item => item.Source.Character.Name == "三月七");
    }

    [Fact]
    public void DeduplicatesSameCharacterAndSupplementsWithDifferentShopCharacter()
    {
        var accepted = new HashSet<string>(
            ["阿格莱雅", "乱破", "大丽花"],
            StringComparer.OrdinalIgnoreCase);
        var initial = _planner.Plan(
        [
            Bench(0, "乱破", "前台"),
            Bench(1, "阿格莱雅", "前台"),
            Bench(3, "阿格莱雅", "前台"),
            Bench(4, "藿藿", "后台")
        ],
        accepted);

        Assert.True(initial.IsReady);
        Assert.Equal(2, initial.Placements.Count);
        Assert.Equal(
            2,
            initial.Placements
                .Select(item => item.Source.Character.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());

        var supplement = _planner.Plan(
        [
            Bench(0, "大丽花", "前台"),
            Bench(3, "阿格莱雅", "前台")
        ],
        accepted,
        initial.Placements);

        var added = Assert.Single(supplement.Placements);
        Assert.Equal("大丽花", added.Source.Character.Name);
        Assert.DoesNotContain(
            initial.Placements,
            item => item.Lane == added.Lane &&
                    item.TargetSlot == added.TargetSlot);
    }

    [Fact]
    public void OneInitialScholarDoesNotDisplaceRewardCharactersBeforeShopPairExists()
    {
        var plan = _planner.Plan(
        [
            Bench(0, "艾丝妲", "前台", "银河学者"),
            Bench(1, "万敌", "前台"),
            Bench(2, "银狼LV.999", "前台"),
            Bench(3, "乱破", "前台")
        ],
        enableGalaxyScholarPair: true);

        Assert.Equal(3, plan.Placements.Count);
        Assert.DoesNotContain(plan.Placements,
            item => item.Source.Character.Name == "艾丝妲");
    }

    [Fact]
    public void TwoInitialScholarsAreBothDeployedWithoutShopDependency()
    {
        var plan = _planner.Plan(
        [
            Bench(0, "艾丝妲", "前台", "银河学者"),
            Bench(1, "黑塔", "前后台", "银河学者"),
            Bench(2, "万敌", "前台")
        ],
        enableGalaxyScholarPair: true);

        Assert.Equal(3, plan.Placements.Count);
        Assert.Equal(
            ["艾丝妲", "黑塔"],
            plan.Placements
                .Where(item => GalaxyScholarPairPolicy.IsCandidate(
                    item.Source.Character))
                .Select(item => item.Source.Character.Name));
    }

    [Fact]
    public void ShopScholarCanReplaceOrdinaryCharacterWhenTeamIsFull()
    {
        var existing = new[]
        {
            Placement(0, "万敌", "前台", PreparationLane.Front, 0),
            Placement(1, "银狼LV.999", "前台", PreparationLane.Front, 1),
            Placement(2, "乱破", "前台", PreparationLane.Front, 2)
        };
        var plan = _planner.Plan(
        [
            Bench(3, "艾丝妲", "前台", "银河学者"),
            Bench(4, "黑塔", "前后台", "银河学者")
        ],
        existingPlacements: existing,
        enableGalaxyScholarPair: true);

        Assert.Equal(2, plan.Placements.Count);
        Assert.Equal(
            ["艾丝妲", "黑塔"],
            plan.Placements.Select(item => item.Source.Character.Name));
        Assert.Equal(2, plan.ReplacedPlacements.Count);
        Assert.All(plan.Placements, replacement =>
            Assert.Contains(plan.ReplacedPlacements, replaced =>
                replaced.Lane == replacement.Lane &&
                replaced.TargetSlot == replacement.TargetSlot));
    }

    private static RecognizedBenchCharacter Bench(
        int slot,
        string name,
        string position,
        params string[] bonds) =>
        new(
            slot,
            new CurrencyWarsCharacterData(
                $"character_{slot}",
                name,
                position,
                [1],
                false,
                bonds),
            0.95);

    private static PreparationPlacement Placement(
        int slot,
        string name,
        string position,
        PreparationLane lane,
        int targetSlot,
        params string[] bonds) =>
        new(Bench(slot, name, position, bonds), lane, targetSlot);
}
