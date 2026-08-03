using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using System.Text.RegularExpressions;

namespace CurrencyWarsAssistant.Tasks;

public enum PreparationLane
{
    Front,
    Back
}

public sealed record RecognizedBenchCharacter(
    int BenchSlot,
    CurrencyWarsCharacterData Character,
    double Confidence);

public sealed record PreparationPlacement(
    RecognizedBenchCharacter Source,
    PreparationLane Lane,
    int TargetSlot);

public enum PreparationFormationPlanStatus
{
    Ready,
    NoEligibleCharacter
}

public sealed record PreparationFormationPlan(
    PreparationFormationPlanStatus Status,
    IReadOnlyList<PreparationPlacement> Placements,
    string Message)
{
    public bool IsReady => Status == PreparationFormationPlanStatus.Ready;

    public IReadOnlyList<PreparationPlacement> ReplacedPlacements { get; init; } =
        [];
}

public static class GalaxyScholarPairPolicy
{
    public const string BondName = "银河学者";
    public const int ActivationCharacterCount = 2;

    public static bool IsCandidate(CurrencyWarsCharacterData character) =>
        character.BondNames.Contains(BondName, StringComparer.OrdinalIgnoreCase);
}

public static class FateGrailFormationPolicy
{
    public const string BondName = "命运圣杯";
    public const int MaximumDeployedCharacters = 1;

    public static bool IsCandidate(CurrencyWarsCharacterData character) =>
        character.BondNames.Contains(
            BondName,
            StringComparer.OrdinalIgnoreCase);
}

public sealed class InitialRewardFormationPlanner
{
    public const int InitialTeamCapacity = 3;

    public static IReadOnlySet<string> DefaultEligibleCharacterNames { get; } =
        new HashSet<string>(
            [
                "阿格莱雅",
                "大丽花",
                "飞霄",
                "黑塔",
                "乱破",
                "万敌",
                "爻光",
                "远坂凛",
                "丹恒•饮月",
                "绯英",
                "风堇",
                "吉尔伽美什",
                "卡芙卡",
                "千冶•刃",
                "缇宝",
                "银枝",
                "Saber",
                "白厄",
                "黄泉",
                "姬子",
                "姬子•启行",
                "镜流",
                "那刻夏",
                "希儿",
                "银狼LV.999",
                "真理医生"
            ],
            StringComparer.OrdinalIgnoreCase);

    public PreparationFormationPlan Plan(
        IReadOnlyList<RecognizedBenchCharacter> bench,
        IReadOnlySet<string>? eligibleCharacterNames = null,
        IReadOnlyList<PreparationPlacement>? existingPlacements = null,
        bool enableGalaxyScholarPair = false)
    {
        ArgumentNullException.ThrowIfNull(bench);
        existingPlacements ??= [];
        var eligibleNames = eligibleCharacterNames is { Count: > 0 }
            ? eligibleCharacterNames
            : DefaultEligibleCharacterNames;
        var effectiveExistingPlacements = existingPlacements.ToList();
        var replacedPlacements = new List<PreparationPlacement>();
        var priorityPlacements = new List<PreparationPlacement>();
        var priorityCharacters = new List<RecognizedBenchCharacter>();
        var availableGalaxyScholarNames = effectiveExistingPlacements
            .Select(item => item.Source.Character)
            .Concat(bench.Select(item => item.Character))
            .Where(GalaxyScholarPairPolicy.IsCandidate)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (enableGalaxyScholarPair &&
            availableGalaxyScholarNames.Length >=
                GalaxyScholarPairPolicy.ActivationCharacterCount)
        {
            var deployedScholarNames = effectiveExistingPlacements
                .Where(item => GalaxyScholarPairPolicy.IsCandidate(
                    item.Source.Character))
                .Select(item => item.Source.Character.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availableScholars = bench
                .Where(item => GalaxyScholarPairPolicy.IsCandidate(
                    item.Character))
                .Where(item => !deployedScholarNames.Contains(
                    item.Character.Name))
                .OrderBy(item => item.BenchSlot)
                .DistinctBy(
                    item => item.Character.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(
                    0,
                    GalaxyScholarPairPolicy.ActivationCharacterCount -
                    deployedScholarNames.Count))
                .ToArray();

            foreach (var scholar in availableScholars)
            {
                if (effectiveExistingPlacements.Count +
                    priorityPlacements.Count +
                    priorityCharacters.Count >= InitialTeamCapacity)
                {
                    var replaced = effectiveExistingPlacements
                        .LastOrDefault(item =>
                            !GalaxyScholarPairPolicy.IsCandidate(
                                item.Source.Character));
                    if (replaced is null)
                    {
                        break;
                    }

                    effectiveExistingPlacements.Remove(replaced);
                    replacedPlacements.Add(replaced);
                    priorityPlacements.Add(new PreparationPlacement(
                        scholar,
                        replaced.Lane,
                        replaced.TargetSlot));
                }
                else
                {
                    priorityCharacters.Add(scholar);
                }
            }
        }

        var fixedPlacements = effectiveExistingPlacements
            .Concat(priorityPlacements)
            .ToArray();
        var deployedNames = effectiveExistingPlacements
            .Select(item => item.Source.Character.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priorityNames = priorityCharacters
            .Select(item => item.Character.Name)
            .Concat(priorityPlacements.Select(item =>
                item.Source.Character.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingCapacity = Math.Max(
            0,
            InitialTeamCapacity - deployedNames.Count);
        var regularEligible = bench
            .Where(item => eligibleNames.Contains(item.Character.Name))
            .Where(item => !deployedNames.Contains(item.Character.Name))
            .Where(item => !priorityNames.Contains(item.Character.Name))
            .OrderBy(item => item.BenchSlot)
            .DistinctBy(
                item => item.Character.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deployedScholarCount = fixedPlacements
            .Select(item => item.Source.Character)
            .Where(GalaxyScholarPairPolicy.IsCandidate)
            .Select(item => item.Name)
            .Concat(priorityCharacters
                .Where(item => GalaxyScholarPairPolicy.IsCandidate(
                    item.Character))
                .Select(item => item.Character.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var selectionCapacity = Math.Max(
            0,
            remainingCapacity - priorityPlacements.Count);
        var selected = priorityCharacters.ToList();
        AddFormationCandidates(
            selected,
            regularEligible,
            selectionCapacity,
            fixedPlacements.Count(item =>
                FateGrailFormationPolicy.IsCandidate(item.Source.Character)));
        if (selected.Count == 0 &&
            priorityPlacements.Count == 0 &&
            effectiveExistingPlacements.Count == 0)
        {
            return new PreparationFormationPlan(
                PreparationFormationPlanStatus.NoEligibleCharacter,
                [],
                "备战席没有识别到任何适合首轮奖励关的角色。");
        }

        var hasExistingFront = fixedPlacements
            .Any(item =>
            item.Lane == PreparationLane.Front);
        if (!hasExistingFront && !selected.Any(PrefersFront))
        {
            var preferredFront = priorityCharacters
                .Concat(regularEligible)
                .Where(item =>
                    !FateGrailFormationPolicy.IsCandidate(item.Character) ||
                    selected.All(selectedItem =>
                        !FateGrailFormationPolicy.IsCandidate(
                            selectedItem.Character)) ||
                    selected.Any(selectedItem =>
                        string.Equals(
                            selectedItem.Character.Name,
                            item.Character.Name,
                            StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.BenchSlot)
                .FirstOrDefault(PrefersFront);
            if (preferredFront is not null &&
                selected.All(item =>
                    item.BenchSlot != preferredFront.BenchSlot))
            {
                if (selected.Count < selectionCapacity)
                {
                    selected.Add(preferredFront);
                }
                else
                {
                    selected[^1] = preferredFront;
                }
            }
        }

        selected = selected
            .DistinctBy(item => item.BenchSlot)
            .OrderBy(item => item.BenchSlot)
            .ToList();
        var frontline = selected.FirstOrDefault(PrefersFront) ??
                        selected.FirstOrDefault();
        var usedFrontSlots = fixedPlacements
            .Where(item => item.Lane == PreparationLane.Front)
            .Select(item => item.TargetSlot)
            .ToHashSet();
        var usedBackSlots = fixedPlacements
            .Where(item => item.Lane == PreparationLane.Back)
            .Select(item => item.TargetSlot)
            .ToHashSet();
        var placements = priorityPlacements
            .Concat(selected
            .Select(item =>
            {
                var lane =
                    frontline is not null &&
                    item.BenchSlot != frontline.BenchSlot &&
                    PrefersBack(item)
                    ? PreparationLane.Back
                    : PreparationLane.Front;
                var targetSlot = FirstUnusedSlot(
                    lane == PreparationLane.Front
                        ? usedFrontSlots
                        : usedBackSlots);
                return new PreparationPlacement(
                    item,
                    lane,
                    targetSlot);
            }))
            .ToArray();
        return new PreparationFormationPlan(
            PreparationFormationPlanStatus.Ready,
            placements,
            $"识别到 {bench.Count} 名角色，其中 {regularEligible.Count} 名符合常规奖励关名单；" +
            (enableGalaxyScholarPair
                ? $"银河学者优先约束已启用，当前规划可同时上场 " +
                  $"{Math.Min(GalaxyScholarPairPolicy.ActivationCharacterCount, deployedScholarCount)} 名；"
                : string.Empty) +
            $"计划新增部署 {placements.Length} 名不同角色；" +
            (bench.Count(item => FateGrailFormationPolicy.IsCandidate(
                 item.Character)) > FateGrailFormationPolicy.MaximumDeployedCharacters
                ? "命运圣杯成员已限制为最多上场 1 名，以避免触发祈愿试炼选择页；"
                : string.Empty) +
            (effectiveExistingPlacements.Count + placements.Length < InitialTeamCapacity
                ? $"仍缺 {InitialTeamCapacity - effectiveExistingPlacements.Count - placements.Length} 名，" +
                  "完整奖励流程将在商店优先补充名单内的不同角色。"
                : "阵容人数已满足首轮上限。"))
        {
            ReplacedPlacements = replacedPlacements
        };
    }

    private static int FirstUnusedSlot(ISet<int> used)
    {
        var slot = 0;
        while (!used.Add(slot))
        {
            slot++;
        }

        return slot;
    }

    private static void AddFormationCandidates(
        ICollection<RecognizedBenchCharacter> selected,
        IEnumerable<RecognizedBenchCharacter> candidates,
        int capacity,
        int deployedFateGrailCount)
    {
        var fateGrailCount = deployedFateGrailCount + selected.Count(item =>
            FateGrailFormationPolicy.IsCandidate(item.Character));
        foreach (var candidate in candidates)
        {
            if (selected.Count >= capacity)
            {
                return;
            }

            if (FateGrailFormationPolicy.IsCandidate(candidate.Character) &&
                fateGrailCount >=
                    FateGrailFormationPolicy.MaximumDeployedCharacters)
            {
                continue;
            }

            selected.Add(candidate);
            if (FateGrailFormationPolicy.IsCandidate(candidate.Character))
            {
                fateGrailCount++;
            }
        }
    }

    private static bool PrefersFront(RecognizedBenchCharacter item) =>
        !string.Equals(
            item.Character.Position,
            "后台",
            StringComparison.OrdinalIgnoreCase);

    private static bool PrefersBack(RecognizedBenchCharacter item) =>
        string.Equals(
            item.Character.Position,
            "后台",
            StringComparison.OrdinalIgnoreCase);
}

public enum PreparationBoardStatus
{
    Deployed,
    NoEligibleCharacter,
    RecognitionFailed,
    InputFailed
}

public sealed record PreparationBoardResult(
    PreparationBoardStatus Status,
    IReadOnlyList<RecognizedBenchCharacter> Bench,
    IReadOnlyList<PreparationPlacement> Placements,
    string Message)
{
    public bool Succeeded => Status == PreparationBoardStatus.Deployed;
    public bool ShouldReroll => !Succeeded;
}

public enum PreparationBenchSaleMode
{
    None,
    SellAll,
    InterestThreshold
}

public sealed class PreparationBoardOptions
{
    /// <summary>
    /// Optional reward-stage deployment allow-list. This is deliberately
    /// independent from retained and auto-purchased characters; null or an
    /// empty set uses the built-in reward formation roster.
    /// </summary>
    public IReadOnlySet<string>? EligibleCharacterNames { get; init; }
    public bool EnableGalaxyScholarPairFormation { get; init; }
    public PreparationBenchSaleMode BenchSaleMode { get; init; }
    public int InterestThreshold { get; init; } = 10;
    public IReadOnlySet<string> RetainedCharacterNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 快速刷开局模式：Stable（完整验证）/ Fast（去验证，OCR 两次）/
    /// Extreme（前三个角色无脑部署）。
    /// </summary>
    public FastRerollMode FastReroll { get; init; } = FastRerollMode.Stable;
    public IReadOnlySet<string> RequiredRetainedCharacterNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool EnableEarlyStrongFormationRetention { get; init; }
    public bool DeferBenchSaleUntilShopCompletion { get; init; }
}

public sealed record PreparationBenchSalePlan(
    IReadOnlyList<RecognizedBenchCharacter> Candidates,
    int TotalSaleValue,
    bool ShouldSell,
    string Message);

public static class EarlyStrongFormationCharacterPolicy
{
    public static bool IsCandidate(CurrencyWarsCharacterData character) =>
        character.Costs.DefaultIfEmpty(int.MaxValue).Min() <= 2 &&
        character.BondNames.Any(bond =>
            string.Equals(
                bond,
                "仙舟",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                bond,
                "持续伤害",
                StringComparison.OrdinalIgnoreCase));
}

public sealed class PreparationBenchSalePlanner
{
    public PreparationBenchSalePlan Plan(
        IReadOnlyList<RecognizedBenchCharacter> bench,
        IReadOnlyList<PreparationPlacement> placements,
        PreparationBoardOptions options,
        int? currentGold,
        IEnumerable<string>? deployedCharacterNames = null)
    {
        ArgumentNullException.ThrowIfNull(bench);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(options);

        if (options.BenchSaleMode == PreparationBenchSaleMode.None)
        {
            return new PreparationBenchSalePlan(
                [],
                0,
                false,
                "备战席出售已关闭。");
        }

        var deployedSlots = placements
            .Select(item => item.Source.BenchSlot)
            .ToHashSet();
        var deployedNames = (deployedCharacterNames ?? placements.Select(item =>
                item.Source.Character.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableBench = bench
            .Where(item => !deployedSlots.Contains(item.BenchSlot))
            .OrderBy(item => item.BenchSlot)
            .ToArray();
        var confirmedCharacterNames = availableBench
            .Select(item => item.Character.Name)
            .Concat(deployedNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingRequiredRetention = options.RequiredRetainedCharacterNames
            .Where(name => !confirmedCharacterNames.Contains(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingRequiredRetention.Length > 0)
        {
            return new PreparationBenchSalePlan(
                [],
                0,
                false,
                $"本轮已购买保留角色 {string.Join("、", missingRequiredRetention)} " +
                "未能在当前备战席或场上确认；为避免识别混淆导致刚买即卖，" +
                "本批整体跳过出售并继续奖励关。");
        }

        var customRetainedBenchSlots = availableBench
            .Where(item => options.RetainedCharacterNames.Contains(
                item.Character.Name))
            .Select(item => item.BenchSlot);
        var automaticallyRetainedBenchSlots = availableBench
            .GroupBy(
                item => item.Character.Name,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => !deployedNames.Contains(group.Key))
            .Where(group =>
                options.EnableEarlyStrongFormationRetention &&
                EarlyStrongFormationCharacterPolicy.IsCandidate(
                    group.First().Character))
            .Select(group => group.First().BenchSlot);
        var reservedBenchSlots = customRetainedBenchSlots
            .Concat(automaticallyRetainedBenchSlots)
            .ToHashSet();
        var candidates = availableBench
            .Where(item => !reservedBenchSlots.Contains(item.BenchSlot))
            .ToArray();
        var totalValue = candidates.Sum(SaleValue);
        var candidateSummary = string.Join(
            "、",
            candidates.Select(item =>
                $"{item.Character.Name}({SaleValue(item)})"));

        if (candidates.Length == 0)
        {
            return new PreparationBenchSalePlan(
                [],
                0,
                false,
                "没有可出售角色：已上场、用户保留和自动保留角色均已排除。");
        }

        if (options.BenchSaleMode == PreparationBenchSaleMode.SellAll)
        {
            return new PreparationBenchSalePlan(
                candidates,
                totalValue,
                true,
                $"将出售 {candidates.Length} 名可出售角色：{candidateSummary}；" +
                $"预计获得 {totalValue} 金币。");
        }

        if (currentGold is null)
        {
            return new PreparationBenchSalePlan(
                candidates,
                totalValue,
                false,
                "当前金币未能形成稳定识别结果；利息模式不执行出售。");
        }

        var threshold = options.InterestThreshold is 10 or 20
            ? options.InterestThreshold
            : 10;
        var projectedGold = currentGold.Value + totalValue;
        return new PreparationBenchSalePlan(
            candidates,
            totalValue,
            currentGold.Value < threshold && projectedGold >= threshold,
            currentGold.Value >= threshold
                ? $"当前已有 {currentGold.Value} 金币，已达到 {threshold} 金币档，不出售。"
                : projectedGold >= threshold
                    ? $"当前 {currentGold.Value} 金币；可出售角色 {candidateSummary}，" +
                      $"出售后预计 {projectedGold}，可达到 {threshold} 金币档。"
                    : $"当前 {currentGold.Value} 金币；可出售角色 {candidateSummary}，" +
                      $"合计 {totalValue}，无法达到 {threshold} 金币档，不出售。");
    }

    private static int SaleValue(RecognizedBenchCharacter item) =>
        item.Character.Costs.DefaultIfEmpty(0).Min();
}

public static partial class PreparationGoldParser
{
    [GeneratedRegex(@"(?<!\d)\d{1,2}(?!\d)")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"商\s*店")]
    private static partial Regex ShopLabelPattern();

    public static int? Parse(OcrTextResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var texts = result.Lines
            .Prepend(result.Text)
            .Append(string.Join(' ', result.Lines))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hasShopLabel = false;
        var anchoredValues = new List<int>();
        foreach (var text in texts)
        {
            var shopLabel = ShopLabelPattern().Match(text);
            if (!shopLabel.Success)
            {
                continue;
            }

            hasShopLabel = true;
            var tokens = Regex.Matches(text[..shopLabel.Index], @"\d+")
                .Cast<Match>()
                .Select(match => match.Value)
                .ToArray();
            if (tokens.Length == 1 &&
                int.TryParse(tokens[0], out var single) &&
                single <= 99)
            {
                anchoredValues.Add(single);
            }
            else if (tokens.Length >= 3 &&
                     int.TryParse(tokens[0], out var first) &&
                     int.TryParse(tokens[1], out var second) &&
                     first == 0 &&
                     second == 0 &&
                     int.TryParse(tokens[^1], out var trailing))
            {
                anchoredValues.Add(trailing % 100);
            }
        }

        if (hasShopLabel)
        {
            var distinctAnchoredValues = anchoredValues.Distinct().ToArray();
            return distinctAnchoredValues.Length == 1
                ? distinctAnchoredValues[0]
                : null;
        }

        var values = texts
            .SelectMany(text =>
                NumberPattern().Matches(text ?? string.Empty).Cast<Match>())
            .Select(match => int.Parse(match.Value))
            .Where(value => value <= 99)
            .Distinct()
            .ToArray();
        return values.Length == 1 ? values[0] : null;
    }
}

public sealed record PreparationMoveVisualChange(
    double SourceDifference,
    double TargetDifference)
{
    private const double ChangedThreshold = 12;
    private const double UnchangedThreshold = 7;

    public bool SourceChanged => SourceDifference >= ChangedThreshold;
    public bool TargetChanged => TargetDifference >= ChangedThreshold;
    public bool MoveObserved => SourceChanged && TargetChanged;
    public bool DefinitelyUnchanged =>
        SourceDifference <= UnchangedThreshold &&
        TargetDifference <= UnchangedThreshold;
}

public static class PreparationCompanionSelectionPolicy
{
    public const string PageId = "companion_selection";
    public const string HimekoQixingName = "姬子•启行";
    public const string TrainCompanionBondName = "列车同行";

    // This point is inside the only card in the one-candidate layout and the
    // first card in the two-candidate layout shown by the supplied captures.
    public static readonly PixelPoint FirstCandidatePoint = new(970, 250);
    public static readonly PixelPoint ConfirmPoint = new(1495, 600);

    public static bool CanTrigger(
        IEnumerable<CurrencyWarsCharacterData> deployedCharacters)
    {
        var deployed = deployedCharacters
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return deployed.Any(item => string.Equals(
                   item.Name,
                   HimekoQixingName,
                   StringComparison.OrdinalIgnoreCase)) &&
               deployed.Any(item =>
                   !string.Equals(
                       item.Name,
                       HimekoQixingName,
                       StringComparison.OrdinalIgnoreCase) &&
                   item.BondNames.Contains(
                       TrainCompanionBondName,
                       StringComparer.OrdinalIgnoreCase));
    }
}

public static class PreparationMoveVerifier
{
    public static PreparationMoveVisualChange Compare(
        CaptureFrame before,
        CaptureFrame after,
        PixelRect sourceReference,
        PixelRect targetReference) =>
        new(
            MeanAbsoluteDifference(before, after, sourceReference),
            MeanAbsoluteDifference(before, after, targetReference));

    private static double MeanAbsoluteDifference(
        CaptureFrame before,
        CaptureFrame after,
        PixelRect referenceBounds)
    {
        if (before.Width != after.Width ||
            before.Height != after.Height ||
            before.Stride != after.Stride)
        {
            return double.PositiveInfinity;
        }

        var bounds = MapReferenceRect(before, referenceBounds);
        if (bounds.IsEmpty)
        {
            return 0;
        }

        // Ignore card borders and sample every other pixel.  The interior is
        // what changes when a card leaves/enters a slot, while hover outlines
        // and cursor animations mostly live near the border.
        var insetX = Math.Max(2, bounds.Width / 8);
        var insetY = Math.Max(2, bounds.Height / 8);
        var left = bounds.X + insetX;
        var top = bounds.Y + insetY;
        var right = bounds.Right - insetX;
        var bottom = bounds.Bottom - insetY;
        long totalDifference = 0;
        long channelCount = 0;
        for (var y = top; y < bottom; y += 2)
        {
            var row = checked(y * before.Stride);
            for (var x = left; x < right; x += 2)
            {
                var offset = checked(row + x * 4);
                for (var channel = 0; channel < 3; channel++)
                {
                    totalDifference += Math.Abs(
                        before.BgraPixels[offset + channel] -
                        after.BgraPixels[offset + channel]);
                    channelCount++;
                }
            }
        }

        return channelCount == 0
            ? 0
            : totalDifference / (double)channelCount;
    }

    private static PixelRect MapReferenceRect(
        CaptureFrame frame,
        PixelRect referenceBounds)
    {
        var left = (int)Math.Round(
            referenceBounds.X * frame.Width /
            (double)OpenCvTemplateMatcher.ReferenceWidth);
        var top = (int)Math.Round(
            referenceBounds.Y * frame.Height /
            (double)OpenCvTemplateMatcher.ReferenceHeight);
        var right = (int)Math.Round(
            referenceBounds.Right * frame.Width /
            (double)OpenCvTemplateMatcher.ReferenceWidth);
        var bottom = (int)Math.Round(
            referenceBounds.Bottom * frame.Height /
            (double)OpenCvTemplateMatcher.ReferenceHeight);
        left = Math.Clamp(left, 0, frame.Width);
        top = Math.Clamp(top, 0, frame.Height);
        right = Math.Clamp(right, left, frame.Width);
        bottom = Math.Clamp(bottom, top, frame.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}

public interface IPreparationBoardController
{
    Task<PreparationBoardResult> PrepareAsync(
        nint windowHandle,
        PreparationBoardOptions options,
        CancellationToken cancellationToken);
}

public interface IPreparationBoardCompletionController
{
    Task<IReadOnlyList<RecognizedBenchCharacter>?>
        ReadStableBenchCharactersAsync(
            nint windowHandle,
            string expectedPreparationPageId,
            CancellationToken cancellationToken);

    Task<PreparationMineCapacityResult> EnsureMineCapacityAsync(
        nint windowHandle,
        IReadOnlyList<PreparationPlacement> existingPlacements,
        PreparationBoardOptions options,
        string expectedPreparationPageId,
        CancellationToken cancellationToken);

    Task<PreparationBoardResult> CompleteAfterShopAsync(
        nint windowHandle,
        IReadOnlyList<PreparationPlacement> existingPlacements,
        PreparationBoardOptions options,
        string expectedPreparationPageId,
        CancellationToken cancellationToken);
}

public sealed record PreparationMineCapacityResult(
    bool CanOpenMine,
    bool ReleasedSlot,
    int OccupiedSlots,
    string Message);

public static class PreparationBenchOccupancyPolicy
{
    public static int CountOccupied(
        IEnumerable<CharacterCardSlotRecognition> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots.Count(item =>
            item.State != CharacterCardSlotState.Empty);
    }
}

public sealed class PreparationBoardController(
    IGameCapture capture,
    ICharacterCardRecognizer recognizer,
    IReadOnlyList<CharacterCardTemplateDefinition> templates,
    IGoldDigitRecognizer goldDigitRecognizer,
    IReadOnlyList<GoldDigitTemplateDefinition> goldDigitTemplates,
    GameDataCatalog gameData,
    InitialRewardFormationPlanner planner,
    PreparationBenchSalePlanner salePlanner,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    IGamePageClassifier pageClassifier,
    IOfflineOcr ocr,
    ITaskEventSink eventSink) :
    IPreparationBoardController,
    IPreparationBoardCompletionController
{
    private static readonly TimeSpan InitialPreparationSettleDelay =
        TimeSpan.Zero;
    private static readonly TimeSpan PreparationRecognitionRetryDelay =
        TimeSpan.FromMilliseconds(200);
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

    private static readonly IReadOnlyList<PixelRect> FrontSlots =
    [
        new(681, 329, 128, 140),
        new(827, 329, 122, 140),
        new(972, 329, 120, 140),
        new(1114, 329, 120, 140)
    ];

    private static readonly IReadOnlyList<PixelRect> BackSlots =
    [
        new(535, 600, 140, 145),
        new(687, 600, 130, 145),
        new(829, 600, 130, 145),
        new(966, 600, 130, 145),
        new(1108, 600, 130, 145),
        new(1258, 600, 130, 145)
    ];

    private static readonly PixelRect GoldFocusedRegion =
        new(1580, 890, 140, 150);

    private static readonly PixelRect GoldContextRegion =
        new(1540, 800, 220, 270);

    private static readonly PixelRect GoldDigitRegion =
        new(1620, 895, 60, 55);

    private static readonly IReadOnlyList<PixelPoint> SellTargetPoints =
    [
        new(85, 930),
        new(1835, 930)
    ];

    private readonly IReadOnlyDictionary<string, CurrencyWarsCharacterData>
        _characters = gameData.CurrencyWarsCharacters.ToDictionary(
            item => item.Id,
            StringComparer.OrdinalIgnoreCase);

    public async Task<PreparationBoardResult> PrepareAsync(
        nint windowHandle,
        PreparationBoardOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Publish(
            TaskEventLevel.Information,
            "PreparationRecognitionStarted",
            "已到达 1-1，正在识别备战席角色。");

        // 极速版：备战席前三个角色无脑拖到前台，不做任何识别/规划/验证。
        if (options.FastReroll == FastRerollMode.Extreme)
        {
            return await PrepareExtremeAsync(
                windowHandle,
                options,
                cancellationToken);
        }

        // 快速版：只识别一次（单帧），裸拖拽部署，不做验证。
        if (options.FastReroll == FastRerollMode.Fast)
        {
            return await PrepareFastAsync(
                windowHandle,
                options,
                cancellationToken);
        }

        await Task.Delay(InitialPreparationSettleDelay, cancellationToken);
        var bench = await ReadStableBenchAsync(
            windowHandle,
            minimumOccupied: 3,
            maximumOccupied: BenchSlots.Count,
            requireLeadingContiguousSlots: true,
            expectedPreparationPageId: "preparation_1_1",
            cancellationToken);
        if (bench is null)
        {
            return Result(
                PreparationBoardStatus.RecognitionFailed,
                [],
                [],
                "备战页无法通过页面门禁或捕获已失效；三次 Esc 恢复后仍不能安全操作。");
        }

        Publish(
            TaskEventLevel.Information,
            "PreparationBenchRecognized",
            $"备战席识别：{string.Join(
                "、",
                bench.Select(item =>
                    $"{item.Character.Name}(模板相似度 {item.Confidence:P1})"))}");
        var plan = planner.Plan(
            bench,
            options.EligibleCharacterNames,
            enableGalaxyScholarPair:
                options.EnableGalaxyScholarPairFormation);
        if (!plan.IsReady)
        {
            return Result(
                PreparationBoardStatus.NoEligibleCharacter,
                bench,
                [],
                plan.Message);
        }

        Publish(
            TaskEventLevel.Information,
            "PreparationPlanReady",
            plan.Message + " " +
            string.Join(
                "；",
                plan.Placements.Select(item =>
                    $"{item.Source.Character.Name}→" +
                    $"{(item.Lane == PreparationLane.Front ? "前台" : "后台")}" +
                    $"{item.TargetSlot + 1}号位")));
        PublishCompanionSelectionExpectation(plan.Placements);

        foreach (var placement in plan.Placements)
        {
            var deployed = await DeployWithVerificationAsync(
                windowHandle,
                placement,
                "preparation_1_1",
                cancellationToken);
            if (!deployed)
            {
                return Result(
                    PreparationBoardStatus.InputFailed,
                    bench,
                    plan.Placements,
                    $"未能确认“{placement.Source.Character.Name}”已进入" +
                    $"{(placement.Lane == PreparationLane.Front ? "前台" : "后台")}" +
                    $"{placement.TargetSlot + 1}号位；已停止后续拖拽。");
            }
        }

        var sale = options.DeferBenchSaleUntilShopCompletion
            ? "备战席出售已延后到 1-1 商店补员与布阵验证之后。"
            : await SellBenchCharactersAsync(
                windowHandle,
                bench,
                plan.Placements,
                options,
                "preparation_1_1",
                cancellationToken);
        return Result(
            PreparationBoardStatus.Deployed,
            bench,
            plan.Placements,
            $"首轮奖励关布阵完成：{string.Join(
                "、",
                plan.Placements.Select(item =>
                    $"{item.Source.Character.Name}在" +
                    $"{(item.Lane == PreparationLane.Front ? "前台" : "后台")}"))}。" +
            $"{sale} 当前停在 1-1 备战页面，尚未点击出战。");
    }


    /// <summary>
    /// 极速版备战：备战席前三个槽位的角色无脑拖到前台 1/2/3 号位。
    /// 不识别角色、不做规划、不做拖动验证——直接按固定坐标拖拽。
    /// </summary>
    private async Task<PreparationBoardResult> PrepareExtremeAsync(
        nint windowHandle,
        PreparationBoardOptions options,
        CancellationToken cancellationToken)
    {
        var placements = new List<PreparationPlacement>();
        for (var slot = 0; slot < Math.Min(3, BenchSlots.Count); slot++)
        {
            var source = BenchSlots[slot];
            var target = FrontSlots[slot];
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return Result(
                    PreparationBoardStatus.RecognitionFailed,
                    [],
                    [],
                    "极速版备战：游戏窗口失效；已安全停止。");
            }

            var sourcePoint = new PixelPoint(
                source.X + source.Width / 2,
                source.Y + source.Height / 2);
            var targetPoint = new PixelPoint(
                target.X + target.Width / 2,
                target.Y + target.Height / 2);
            var drag = await input.DragAsync(
                new ClickTarget(
                    "extreme_deploy_" + (slot + 1),
                    "极速部署备战席" + (slot + 1) + "号位",
                    window,
                    BoundsAround(window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    VerifyPointerArrivalBeforeClick = false,
                    PointerSettleDelay = TimeSpan.Zero,
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                return Result(
                    PreparationBoardStatus.InputFailed,
                    [],
                    placements,
                    "极速版备战：备战席" + (slot + 1) + "号位拖拽失败；已安全停止。");
            }

            placements.Add(new PreparationPlacement(
                new RecognizedBenchCharacter(
                    slot,
                    _characters.Values.First(),
                    0),
                PreparationLane.Front,
                slot));
        }

        Publish(
            TaskEventLevel.Information,
            "PreparationExtremeDeployed",
            "极速版备战：备战席前三个角色已无脑拖到前台 1/2/3 号位（未识别角色）。");
        return Result(
            PreparationBoardStatus.Deployed,
            [],
            placements,
            "极速版备战完成：前三个备战席角色已拖到前台（无识别、无验证）。");
    }

    /// <summary>
    /// 快速版备战：单帧识别一次备战席 → 规划 → 裸拖拽部署（不做验证）。
    /// 商店补员后的第二次识别由 CompleteAfterShopAsync 的快速分支处理。
    /// </summary>
    private async Task<PreparationBoardResult> PrepareFastAsync(
        nint windowHandle,
        PreparationBoardOptions options,
        CancellationToken cancellationToken)
    {
        var captured = await CaptureVerifiedPreparationAsync(
            windowHandle,
            "preparation_1_1",
            allowEscapeRecovery: true,
            cancellationToken);
        if (captured is null)
        {
            return Result(
                PreparationBoardStatus.RecognitionFailed,
                [],
                [],
                "快速版备战：备战页门禁未通过；已安全停止。");
        }

        var slots = recognizer.Recognize(
            captured.Value.Frame,
            templates,
            BenchSlots);
        var bench = slots
            .Where(item =>
                item.State == CharacterCardSlotState.Recognized &&
                item.CharacterId is not null)
            .Select(item => new RecognizedBenchCharacter(
                item.SlotIndex,
                _characters[item.CharacterId!],
                item.Confidence))
            .ToArray();
        if (bench.Length == 0)
        {
            return Result(
                PreparationBoardStatus.RecognitionFailed,
                [],
                [],
                "快速版备战：单帧识别未发现任何角色；已安全停止。");
        }

        var plan = planner.Plan(
            bench,
            options.EligibleCharacterNames,
            enableGalaxyScholarPair:
                options.EnableGalaxyScholarPairFormation);
        var placements = plan.IsReady
            ? plan.Placements
            : bench
                .Take(Math.Min(3, bench.Length))
                .Select((item, index) => new PreparationPlacement(
                    item,
                    PreparationLane.Front,
                    index))
                .ToList();

        Publish(
            TaskEventLevel.Information,
            "PreparationFastPlanReady",
            "快速版备战：识别到 " + bench.Length + " 名角色，计划部署 " +
            placements.Count + " 名（无验证裸拖拽）。");

        // 裸拖拽部署：不做拖动验证、不做页面门禁复核。
        foreach (var placement in placements)
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return Result(
                    PreparationBoardStatus.RecognitionFailed,
                    bench,
                    placements,
                    "快速版备战：游戏窗口失效；已安全停止。");
            }

            var source = BenchSlots[placement.Source.BenchSlot];
            var target = placement.Lane == PreparationLane.Front
                ? FrontSlots[Math.Min(placement.TargetSlot, FrontSlots.Count - 1)]
                : BenchSlots[BenchSlots.Count - 1];
            var sourcePoint = new PixelPoint(
                source.X + source.Width / 2,
                source.Y + source.Height / 2);
            var targetPoint = new PixelPoint(
                target.X + target.Width / 2,
                target.Y + target.Height / 2);
            var drag = await input.DragAsync(
                new ClickTarget(
                    "fast_deploy_" + placement.Source.BenchSlot,
                    "快速部署" + placement.Source.Character.Name,
                    window,
                    BoundsAround(window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    VerifyPointerArrivalBeforeClick = false,
                    PointerSettleDelay = TimeSpan.Zero,
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                return Result(
                    PreparationBoardStatus.InputFailed,
                    bench,
                    placements,
                    "快速版备战：部署" + placement.Source.Character.Name + "拖拽失败。");
            }
        }

        return Result(
            PreparationBoardStatus.Deployed,
            bench,
            placements,
            "快速版备战完成：已识别一次并裸拖拽部署（无验证）。");
    }

    public async Task<PreparationMineCapacityResult> EnsureMineCapacityAsync(
        nint windowHandle,
        IReadOnlyList<PreparationPlacement> existingPlacements,
        PreparationBoardOptions options,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existingPlacements);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreparationPageId);

        // 快速刷开局：跳过备战席占用复核（用户方案），直接允许开晶矿。
        await Task.CompletedTask;
        return new PreparationMineCapacityResult(
            true,
            false,
            0,
            "快速刷开局：跳过备战席占用复核，直接允许开启晶矿球。");
    }

    public async Task<PreparationBoardResult> CompleteAfterShopAsync(
        nint windowHandle,
        IReadOnlyList<PreparationPlacement> existingPlacements,
        PreparationBoardOptions options,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existingPlacements);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreparationPageId);
        Publish(
            TaskEventLevel.Information,
            "PreparationShopCompletionStarted",
            $"已回到备战页面，等待动画稳定后补齐阵容并重新规划出售；当前已有 " +
            $"{existingPlacements.Select(item => item.Source.Character.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()}/" +
            $"{InitialRewardFormationPlanner.InitialTeamCapacity} 名不同角色。");

        // 快速版：商店补员后只识别一次（单帧），裸拖拽补位，不做验证。
        if (options.FastReroll == FastRerollMode.Fast)
        {
            return await CompleteAfterShopFastAsync(
                windowHandle,
                existingPlacements,
                options,
                expectedPreparationPageId,
                cancellationToken);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);

        var bench = await ReadStableBenchAsync(
            windowHandle,
            minimumOccupied: 0,
            maximumOccupied: BenchSlots.Count,
            requireLeadingContiguousSlots: false,
            expectedPreparationPageId,
            cancellationToken);
        if (bench is null)
        {
            return Result(
                PreparationBoardStatus.RecognitionFailed,
                [],
                existingPlacements,
                "商店补员后的备战页无法通过页面门禁或捕获已失效；" +
                "三次 Esc 恢复后仍不能安全拖动或出售。");
        }

        var supplement = planner.Plan(
            bench,
            options.EligibleCharacterNames,
            existingPlacements,
            enableGalaxyScholarPair:
                options.EnableGalaxyScholarPairFormation);
        var supplementalPlacements = new List<PreparationPlacement>();
        foreach (var placement in supplement.Placements)
        {
            if (!await DeployWithVerificationAsync(
                    windowHandle,
                    placement,
                    expectedPreparationPageId,
                    cancellationToken))
            {
                return Result(
                    PreparationBoardStatus.InputFailed,
                    bench,
                    [.. existingPlacements, .. supplementalPlacements],
                    $"商店补员后未能确认“{placement.Source.Character.Name}”部署成功；" +
                    "已停止后续拖动和出售。");
            }

            supplementalPlacements.Add(placement);
        }

        var combinedPlacements = existingPlacements
            .Except(supplement.ReplacedPlacements)
            .Concat(supplementalPlacements)
            .ToArray();
        var deployedCharacterCount = combinedPlacements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (deployedCharacterCount <
            InitialRewardFormationPlanner.InitialTeamCapacity)
        {
            return Result(
                PreparationBoardStatus.NoEligibleCharacter,
                bench,
                combinedPlacements,
                $"商店结束后仅验证到 {deployedCharacterCount}/" +
                $"{InitialRewardFormationPlanner.InitialTeamCapacity} 名不同角色上场；" +
                "未点击出战，避免触发人数未达上限确认框；本局转入安全重刷。");
        }

        PublishCompanionSelectionExpectation(combinedPlacements);
        var saleBench = bench;
        var salePlacements = supplementalPlacements;
        if (supplement.ReplacedPlacements.Count > 0)
        {
            Publish(
                TaskEventLevel.Information,
                "GalaxyScholarFormationReplacementVerified",
                $"银河学者羁绊补位已替换 {supplement.ReplacedPlacements.Count} 名普通奖励关角色；" +
                "重新识别备战席后再规划出售。" );
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            var refreshedBench = await ReadStableBenchAsync(
                windowHandle,
                minimumOccupied: 0,
                maximumOccupied: BenchSlots.Count,
                requireLeadingContiguousSlots: false,
                expectedPreparationPageId,
                cancellationToken);
            if (refreshedBench is null)
            {
                return Result(
                    PreparationBoardStatus.RecognitionFailed,
                    bench,
                    combinedPlacements,
                    "银河学者替换上场已经验证，但重新识别备战席时页面门禁或捕获失效；" +
                    "未继续发送出售输入。");
            }

            saleBench = refreshedBench;
            salePlacements = [];
        }

        var sale = await SellBenchCharactersAsync(
            windowHandle,
            saleBench,
            salePlacements,
            options,
            expectedPreparationPageId,
            cancellationToken,
            combinedPlacements.Select(item => item.Source.Character.Name));
        return Result(
            PreparationBoardStatus.Deployed,
            bench,
            combinedPlacements,
            $"商店补员完成：新增部署 {supplementalPlacements.Count} 名不同角色，" +
            $"当前阵容 {deployedCharacterCount}/" +
            $"{InitialRewardFormationPlanner.InitialTeamCapacity}；{sale}");
    }


    /// <summary>
    /// 快速版商店补员：单帧识别备战席 → 裸拖拽补位（不做验证）。
    /// 1-2 备战页同样走此路径（expectedPreparationPageId 区分）。
    /// </summary>
    private async Task<PreparationBoardResult> CompleteAfterShopFastAsync(
        nint windowHandle,
        IReadOnlyList<PreparationPlacement> existingPlacements,
        PreparationBoardOptions options,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var captured = await CaptureVerifiedPreparationAsync(
            windowHandle,
            expectedPreparationPageId,
            allowEscapeRecovery: true,
            cancellationToken);
        if (captured is null)
        {
            return Result(
                PreparationBoardStatus.RecognitionFailed,
                [],
                existingPlacements,
                "快速版商店补员：备战页门禁未通过；已安全停止。");
        }

        var slots = recognizer.Recognize(
            captured.Value.Frame,
            templates,
            BenchSlots);
        var bench = slots
            .Where(item =>
                item.State == CharacterCardSlotState.Recognized &&
                item.CharacterId is not null)
            .Select(item => new RecognizedBenchCharacter(
                item.SlotIndex,
                _characters[item.CharacterId!],
                item.Confidence))
            .ToArray();

        var supplement = planner.Plan(
            bench,
            options.EligibleCharacterNames,
            existingPlacements,
            enableGalaxyScholarPair:
                options.EnableGalaxyScholarPairFormation);
        var supplementalPlacements = new List<PreparationPlacement>();
        foreach (var placement in supplement.Placements)
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            if (window is null)
            {
                return Result(
                    PreparationBoardStatus.RecognitionFailed,
                    bench,
                    existingPlacements,
                    "快速版商店补员：游戏窗口失效；已安全停止。");
            }

            var source = BenchSlots[placement.Source.BenchSlot];
            var target = placement.Lane == PreparationLane.Front
                ? FrontSlots[Math.Min(placement.TargetSlot, FrontSlots.Count - 1)]
                : BenchSlots[BenchSlots.Count - 1];
            var sourcePoint = new PixelPoint(
                source.X + source.Width / 2,
                source.Y + source.Height / 2);
            var targetPoint = new PixelPoint(
                target.X + target.Width / 2,
                target.Y + target.Height / 2);
            var drag = await input.DragAsync(
                new ClickTarget(
                    "fast_shop_deploy_" + placement.Source.BenchSlot,
                    "快速补位" + placement.Source.Character.Name,
                    window,
                    BoundsAround(window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    VerifyPointerArrivalBeforeClick = false,
                    PointerSettleDelay = TimeSpan.Zero,
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                return Result(
                    PreparationBoardStatus.InputFailed,
                    bench,
                    existingPlacements,
                    "快速版商店补员：部署" + placement.Source.Character.Name + "拖拽失败。");
            }

            supplementalPlacements.Add(placement);
        }

        var combinedPlacements = existingPlacements
            .Except(supplement.ReplacedPlacements)
            .Concat(supplementalPlacements)
            .ToArray();
        Publish(
            TaskEventLevel.Information,
            "PreparationFastShopCompleted",
            "快速版商店补员完成：新增部署 " + supplementalPlacements.Count +
            " 名，当前共 " + combinedPlacements.Length + " 名（无验证）。");
        return Result(
            PreparationBoardStatus.Deployed,
            bench,
            combinedPlacements,
            "快速版商店补员完成（单帧识别 + 无验证拖拽）。");
    }

    private async Task<string> SellBenchCharactersAsync(
            nint windowHandle,
            IReadOnlyList<RecognizedBenchCharacter> bench,
            IReadOnlyList<PreparationPlacement> placements,
            PreparationBoardOptions options,
            string expectedPreparationPageId,
            CancellationToken cancellationToken,
            IEnumerable<string>? deployedCharacterNames = null)
    {
        int? currentGold = null;
        if (options.BenchSaleMode == PreparationBenchSaleMode.InterestThreshold)
        {
            currentGold = await ReadStableGoldAsync(
                windowHandle,
                expectedPreparationPageId,
                cancellationToken);
        }

        var plan = salePlanner.Plan(
            bench,
            placements,
            options,
            currentGold,
            deployedCharacterNames);
        Publish(
            TaskEventLevel.Information,
            "PreparationBenchSalePlanned",
            plan.Message);
        if (!plan.ShouldSell)
        {
            return plan.Message;
        }

        var soldCount = 0;
        var confirmedSaleValue = 0;
        var skippedNames = new List<string>();
        foreach (var candidate in plan.Candidates)
        {
            if (!await SellCharacterWithVerificationAsync(
                    windowHandle,
                    candidate,
                    expectedPreparationPageId,
                    cancellationToken))
            {
                skippedNames.Add(candidate.Character.Name);
                Publish(
                    TaskEventLevel.Warning,
                    "PreparationBenchSaleSkipped",
                    $"出售“{candidate.Character.Name}”经过 3 次有限尝试后仍未能确认源槽为空；" +
                    "已跳过该角色。出售属于可选经济优化，不会因此停止奖励关自动化。");
                continue;
            }

            soldCount++;
            confirmedSaleValue += candidate.Character.Costs
                .DefaultIfEmpty(0)
                .Min();
            if (options.BenchSaleMode ==
                    PreparationBenchSaleMode.InterestThreshold &&
                currentGold is not null &&
                currentGold.Value + confirmedSaleValue >=
                    options.InterestThreshold)
            {
                Publish(
                    TaskEventLevel.Information,
                    "PreparationBenchSaleThresholdReached",
                    $"已确认出售 {soldCount} 名角色，按可确定价值计算已从 " +
                    $"{currentGold.Value} 金币达到至少 {options.InterestThreshold} 金币；" +
                    "立即停止本批后续出售，避免超额卖角色。");
                break;
            }
        }

        if (options.BenchSaleMode == PreparationBenchSaleMode.InterestThreshold)
        {
            var verifiedGold = await ReadStableGoldAsync(
                windowHandle,
                expectedPreparationPageId,
                cancellationToken);
            if (verifiedGold is not null &&
                verifiedGold.Value < options.InterestThreshold)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "PreparationBenchSaleInterestNotReached",
                    $"已出售 {soldCount} 名角色，但复核金币仅为 {verifiedGold.Value}，" +
                    $"未达到 {options.InterestThreshold} 金币档；停止本批出售并继续奖励关。");
            }
        }

        return skippedNames.Count == 0
            ? $"已出售 {soldCount} 名可出售角色；用户保留、自动保留和已上场角色均未操作。"
            : $"已出售 {soldCount} 名可出售角色，跳过 {skippedNames.Count} 名未通过后置验证的角色" +
              $"（{string.Join("、", skippedNames)}）；继续奖励关自动化。";
    }

    private async Task<int?> ReadStableGoldAsync(
        nint windowHandle,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        if (!ocr.IsAvailable)
        {
            Publish(
                TaskEventLevel.Warning,
                "PreparationGoldUnavailable",
                "Windows 中文 OCR 不可用；利息模式不出售角色。");
            return null;
        }

        int? previous = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: attempt == 1,
                cancellationToken);
            if (captured is null)
            {
                return null;
            }

            var region = MapReferenceRect(
                captured.Value.Frame,
                GoldFocusedRegion);
            var recognized = await ocr.RecognizeAsync(
                captured.Value.Frame,
                region,
                cancellationToken);
            var ocrValue = PreparationGoldParser.Parse(recognized);
            var recognitionSource = "聚焦 OCR";
            if (ocrValue is null)
            {
                var contextRegion = MapReferenceRect(
                    captured.Value.Frame,
                    GoldContextRegion);
                var context = await ocr.RecognizeAsync(
                    captured.Value.Frame,
                    contextRegion,
                    cancellationToken);
                ocrValue = PreparationGoldParser.Parse(context);
                recognitionSource = "上下文 OCR";
            }

            var visual = goldDigitRecognizer.Recognize(
                captured.Value.Frame,
                goldDigitTemplates,
                GoldDigitRegion);
            int? current;
            if (ocrValue is not null &&
                visual.Value is not null &&
                ocrValue != visual.Value)
            {
                current = null;
                recognitionSource =
                    $"OCR={ocrValue} 与视觉={visual.Value} 冲突";
            }
            else if (ocrValue is not null)
            {
                current = ocrValue;
                recognitionSource = visual.Value == ocrValue
                    ? $"{recognitionSource}+视觉交叉确认"
                    : recognitionSource;
            }
            else
            {
                current = visual.Value;
                recognitionSource = visual.Value is null
                    ? "OCR 与视觉均不确定"
                    : $"视觉数字模板 {visual.Confidence:P1}";
            }
            Publish(
                TaskEventLevel.Information,
                "PreparationGoldRecognition",
                $"当前金币第 {attempt}/4 次识别：" +
                $"{(current is null ? "不确定" : current.Value)}；" +
                $"来源={recognitionSource}。");
            if (current is not null && current == previous)
            {
                return current;
            }

            previous = current;
            await Task.Delay(
                TimeSpan.FromMilliseconds(200),
                cancellationToken);
        }

        return null;
    }

    private async Task<bool> SellCharacterWithVerificationAsync(
        nint windowHandle,
        RecognizedBenchCharacter candidate,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: attempt == 1,
                cancellationToken);
            if (captured is null)
            {
                return false;
            }

            var sourceSlot = recognizer.Recognize(
                    captured.Value.Frame,
                    templates,
                    BenchSlots)
                .Single(item => item.SlotIndex == candidate.BenchSlot);
            if (sourceSlot.State == CharacterCardSlotState.Empty)
            {
                Publish(
                    TaskEventLevel.Information,
                    "PreparationBenchSaleAlreadyCompleted",
                    $"出售“{candidate.Character.Name}”前复核发现原备战槽已经为空，" +
                    "按已完成处理，不再重复拖动。");
                return true;
            }

            if (sourceSlot.State != CharacterCardSlotState.Recognized ||
                !string.Equals(
                    sourceSlot.CharacterId,
                    candidate.Character.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    TaskEventLevel.Warning,
                    "PreparationBenchSaleSourceChanged",
                    $"出售“{candidate.Character.Name}”前复核发现备战席{candidate.BenchSlot + 1}号位" +
                    $"已不是同一张可确认角色卡（当前状态 {sourceSlot.State}）；" +
                    "不对该槽发送输入。");
                return false;
            }

            var sourcePoint = MapReferencePoint(
                captured.Value.Window,
                BenchSlots[candidate.BenchSlot].Center);
            var targetIndex = (attempt - 1) % SellTargetPoints.Count;
            var targetPoint = MapReferencePoint(
                captured.Value.Window,
                SellTargetPoints[targetIndex]);
            Publish(
                TaskEventLevel.Information,
                "PreparationBenchSaleAttempt",
                $"出售“{candidate.Character.Name}”：第 {attempt}/3 次从备战席" +
                $"{candidate.BenchSlot + 1}号位拖到" +
                $"{(targetIndex == 0 ? "左侧" : "右侧")}出售区。");
            var drag = await input.DragAsync(
                new ClickTarget(
                    $"sell_{candidate.Character.Id}",
                    $"出售{candidate.Character.Name}",
                    captured.Value.Window,
                    BoundsAround(captured.Value.Window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "PreparationBenchSaleInputRejected",
                    $"出售“{candidate.Character.Name}”第 {attempt}/3 次输入未发送成功：" +
                    drag.Message);
                continue;
            }

            if (await VerifyBenchSlotEmptyAsync(
                    windowHandle,
                    candidate.BenchSlot,
                    expectedPreparationPageId,
                    cancellationToken))
            {
                Publish(
                    TaskEventLevel.Information,
                    "PreparationBenchSaleVerified",
                    $"已连续两帧确认“{candidate.Character.Name}”原备战槽为空。");
                return true;
            }

            Publish(
                TaskEventLevel.Warning,
                "PreparationBenchSalePostconditionUnmet",
                $"出售“{candidate.Character.Name}”第 {attempt}/3 次拖动已发送，" +
                "但未连续两帧确认原槽为空；将重新识别原槽后再决定是否重试。");
        }

        return false;
    }

    private async Task<bool> VerifyBenchSlotEmptyAsync(
        nint windowHandle,
        int benchSlot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var consecutiveEmptyFrames = 0;
        for (var observation = 1; observation <= 6; observation++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: false,
                cancellationToken);
            if (captured is null)
            {
                return false;
            }

            var slot = recognizer.Recognize(
                    captured.Value.Frame,
                    templates,
                    BenchSlots)
                .Single(item => item.SlotIndex == benchSlot);
            consecutiveEmptyFrames = slot.State == CharacterCardSlotState.Empty
                ? consecutiveEmptyFrames + 1
                : 0;
            if (consecutiveEmptyFrames >= 2)
            {
                return true;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }

        return false;
    }

    private async Task<(GameWindowInfo Window, CaptureFrame Frame)?>
        CaptureVerifiedPreparationAsync(
            nint windowHandle,
            string expectedPreparationPageId,
            bool allowEscapeRecovery,
            CancellationToken cancellationToken)
    {
        const int maximumEscapeAttempts = 3;
        var escapeAttempts = 0;
        (GameWindowInfo Window, CaptureFrame Frame)? latest = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(
                window,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            if (string.Equals(
                    page?.PageId,
                    PreparationCompanionSelectionPolicy.PageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!await ResolveCompanionSelectionAsync(
                        windowHandle,
                        expectedPreparationPageId,
                        cancellationToken))
                {
                    return null;
                }

                continue;
            }

            if (string.Equals(
                    page?.PageId,
                    expectedPreparationPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                // 快速刷开局：单帧确认备战页即返回（并行化，不再等第 2 帧）。
                latest = (window, frame);
                return latest;
            }


            if (page is null &&
                allowEscapeRecovery &&
                escapeAttempts < maximumEscapeAttempts)
            {
                escapeAttempts++;
                Publish(
                    TaskEventLevel.Warning,
                    "PreparationBenchSaleEscapeRecovery",
                    $"备战操作前识别到未知页面，执行第 {escapeAttempts}/" +
                    $"{maximumEscapeAttempts} 次 Esc；每次按键后重新识别页面。");
                var recovery = await input.PressKeyAsync(
                    window,
                    InputKey.Escape,
                    new ActionPolicy
                    {
                        AfterActionDelay = TimeSpan.FromMilliseconds(350)
                    },
                    cancellationToken);
                if (!recovery.Succeeded)
                {
                    return null;
                }

                continue;
            }

            Publish(
                TaskEventLevel.Warning,
                "PreparationBenchSalePageMismatch",
                $"备战操作前页面不符合预期：期望 {expectedPreparationPageId}，实际为 " +
                $"{page?.PageId ?? "未知页"}；已安全停止。");
            return null;
        }

        return null;
    }

    private async Task<(
        int OccupiedSlots,
        IReadOnlyList<RecognizedBenchCharacter> RecognizedCharacters)?>
        ReadStableBenchOccupancyAsync(
            nint windowHandle,
            string expectedPreparationPageId,
            CancellationToken cancellationToken)
    {
        string? previousSignature = null;
        for (var observation = 1; observation <= 4; observation++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: observation == 1,
                cancellationToken);
            if (captured is null)
            {
                return null;
            }

            var slots = recognizer.Recognize(
                captured.Value.Frame,
                templates,
                BenchSlots);
            var occupied = slots
                .Where(item => item.State != CharacterCardSlotState.Empty)
                .OrderBy(item => item.SlotIndex)
                .ToArray();
            var occupiedCount =
                PreparationBenchOccupancyPolicy.CountOccupied(slots);
            var recognized = occupied
                .Where(item =>
                    item.State == CharacterCardSlotState.Recognized &&
                    item.CharacterId is not null)
                .Select(item => new RecognizedBenchCharacter(
                    item.SlotIndex,
                    _characters[item.CharacterId!],
                    item.Confidence))
                .ToArray();
            var signature = string.Join(
                "|",
                occupied.Select(item =>
                    $"{item.SlotIndex}:{item.State}:{item.CharacterId ?? "?"}"));
            Publish(
                TaskEventLevel.Information,
                "PreparationMineCapacityObserved",
                $"开晶矿前第 {observation}/4 次备战席占用复核：" +
                $"物理占用 {occupiedCount}/{BenchSlots.Count}，" +
                $"其中可确认角色 {recognized.Length} 张；" +
                "疑似占用与特殊占用均不当作空槽。" );
            if (string.Equals(
                    previousSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                return (occupiedCount, recognized);
            }

            previousSignature = signature;
            await Task.Delay(
                TimeSpan.FromMilliseconds(200),
                cancellationToken);
        }

        return null;
    }

    private async Task<IReadOnlyList<RecognizedBenchCharacter>?>
        ReadStableBenchAsync(
            nint windowHandle,
            int minimumOccupied,
            int maximumOccupied,
            bool requireLeadingContiguousSlots,
            string expectedPreparationPageId,
            CancellationToken cancellationToken)
    {
        string? previousSignature = null;
        var consecutiveRecognized = new Dictionary<
            int,
            (string CharacterId, int Count, RecognizedBenchCharacter Item)>();
        int[] lastUncertainSlots = [];
        int[] lastSpecialOccupiedSlots = [];
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: attempt == 1,
                cancellationToken);
            if (captured is null)
            {
                return null;
            }

            var frame = captured.Value.Frame;
            var slots = recognizer.Recognize(
                frame,
                templates,
                BenchSlots);
            var uncertain = slots
                .Where(item =>
                    item.State == CharacterCardSlotState.Uncertain)
                .ToArray();
            lastUncertainSlots = uncertain
                .Select(item => item.SlotIndex)
                .ToArray();
            var occupied = slots
                .Where(item =>
                    item.State == CharacterCardSlotState.Recognized &&
                    item.CharacterId is not null)
                .ToArray();
            var specialOccupied = slots
                .Where(item =>
                    item.State == CharacterCardSlotState.SpecialOccupied)
                .ToArray();
            lastSpecialOccupiedSlots = specialOccupied
                .Select(item => item.SlotIndex)
                .ToArray();
            var physicallyOccupied = occupied
                .Concat(specialOccupied)
                .OrderBy(item => item.SlotIndex)
                .ToArray();
            foreach (var slot in slots)
            {
                if (slot.State != CharacterCardSlotState.Recognized ||
                    slot.CharacterId is null)
                {
                    consecutiveRecognized.Remove(slot.SlotIndex);
                    continue;
                }

                var item = new RecognizedBenchCharacter(
                    slot.SlotIndex,
                    _characters[slot.CharacterId],
                    slot.Confidence);
                consecutiveRecognized[slot.SlotIndex] =
                    consecutiveRecognized.TryGetValue(
                        slot.SlotIndex,
                        out var previous) &&
                    string.Equals(
                        previous.CharacterId,
                        slot.CharacterId,
                        StringComparison.OrdinalIgnoreCase)
                        ? (slot.CharacterId, previous.Count + 1, item)
                        : (slot.CharacterId, 1, item);
            }

            if (uncertain.Length > 0 ||
                occupied.Length < minimumOccupied ||
                occupied.Length > maximumOccupied ||
                (requireLeadingContiguousSlots &&
                 physicallyOccupied.Select(item => item.SlotIndex)
                     .SequenceEqual(
                         Enumerable.Range(0, physicallyOccupied.Length)) is false))
            {
                Publish(
                    TaskEventLevel.Information,
                    "PreparationRecognitionRetry",
                    $"备战席第 {attempt}/10 次识别未完整：" +
                    $"已识别 {occupied.Length} 张，疑似占用但不确定 {uncertain.Length} 张；" +
                    (specialOccupied.Length > 0
                        ? $"特殊占用 {specialOccupied.Length} 个（槽位" +
                          string.Join(
                              "、",
                              specialOccupied.Select(item => item.SlotIndex + 1)) +
                          "）；"
                        : string.Empty) +
                    (uncertain.Length > 0
                        ? "可疑槽位：" + string.Join(
                            "；",
                            uncertain.Select(item =>
                                $"{item.SlotIndex + 1}号槽首选" +
                                $"{item.DisplayName ?? "未知"} {item.Confidence:P1}" +
                                $"、次选{item.RunnerUpDisplayName ?? "未知"} " +
                                $"{item.RunnerUpConfidence:P1}")) + "；"
                        : string.Empty) +
                    "等待画面稳定后重试。");
                previousSignature = null;
                await Task.Delay(
                    PreparationRecognitionRetryDelay,
                    cancellationToken);
                continue;
            }

            var bench = occupied
                .Select(item => new RecognizedBenchCharacter(
                    item.SlotIndex,
                    _characters[item.CharacterId!],
                    item.Confidence))
                .ToArray();
            var signature = string.Join(
                "|",
                physicallyOccupied.Select(item =>
                    item.State == CharacterCardSlotState.SpecialOccupied
                        ? $"{item.SlotIndex}:special"
                        : $"{item.SlotIndex}:{item.CharacterId}"));
            if (string.Equals(
                    previousSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                return bench;
            }

            previousSignature = signature;
            await Task.Delay(
                PreparationRecognitionRetryDelay,
                cancellationToken);
        }

        var fallback = consecutiveRecognized.Values
            .Where(item => item.Count >= 2)
            .Select(item => item.Item)
            .OrderBy(item => item.BenchSlot)
            .ToArray();
        if (requireLeadingContiguousSlots)
        {
            var stablePhysicalSlots = fallback
                .Select(item => item.BenchSlot)
                .Concat(lastSpecialOccupiedSlots)
                .Distinct()
                .Order()
                .ToArray();
            if (!stablePhysicalSlots.SequenceEqual(
                    Enumerable.Range(0, stablePhysicalSlots.Length)))
            {
                fallback = [];
            }
        }

        Publish(
            TaskEventLevel.Warning,
            "PreparationRecognitionDegraded",
            $"备战席完整识别达到 10 次上限；将槽位 " +
            $"{(lastUncertainSlots.Length == 0 ? "无" : string.Join("、", lastUncertainSlots.Select(slot => slot + 1)))} " +
            $"视为空槽且不执行任何操作，仅使用 {fallback.Length} 张连续两帧确认的角色继续。" +
            (lastSpecialOccupiedSlots.Length > 0
                ? $"特殊占用槽位 {string.Join("、", lastSpecialOccupiedSlots.Select(slot => slot + 1))} " +
                  "仅作为不可放置位置，不作为角色返回。"
                : string.Empty) +
            (fallback.Length == 0
                ? "没有可靠角色时由上层按本轮不可用执行安全重开。"
                : string.Empty));
        return fallback;
    }

    private async Task<bool> DeployWithVerificationAsync(
        nint windowHandle,
        PreparationPlacement placement,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var targetReference = placement.Lane == PreparationLane.Front
            ? FrontSlots[placement.TargetSlot]
            : BackSlots[placement.TargetSlot];
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var captured = await CaptureVerifiedPreparationAsync(
                windowHandle,
                expectedPreparationPageId,
                allowEscapeRecovery: attempt == 1,
                cancellationToken);
            if (captured is null)
            {
                return false;
            }

            var window = captured.Value.Window;
            var before = captured.Value.Frame;
            var sourcePoint = MapReferencePoint(
                window,
                BenchSlots[placement.Source.BenchSlot].Center);
            var targetPoint = MapReferencePoint(
                window,
                targetReference.Center);
            Publish(
                TaskEventLevel.Information,
                "PreparationDeployAttempt",
                $"部署“{placement.Source.Character.Name}”：" +
                $"第 {attempt}/5 次从备战席{placement.Source.BenchSlot + 1}号位拖到" +
                $"{(placement.Lane == PreparationLane.Front ? "前台" : "后台")}" +
                $"{placement.TargetSlot + 1}号位。");
            var drag = await input.DragAsync(
                new ClickTarget(
                    $"deploy_{placement.Source.Character.Id}",
                    $"部署{placement.Source.Character.Name}",
                    window,
                    BoundsAround(window, sourcePoint)),
                targetPoint,
                TimeSpan.FromMilliseconds(650),
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(50)
                },
                cancellationToken);
            if (!drag.Succeeded)
            {
                continue;
            }

            var verification = await VerifyMoveAsync(
                windowHandle,
                before,
                BenchSlots[placement.Source.BenchSlot],
                targetReference,
                expectedPreparationPageId,
                cancellationToken);
            if (verification.MoveObserved)
            {
                Publish(
                    TaskEventLevel.Information,
                    "PreparationDeployVerified",
                    $"已确认“{placement.Source.Character.Name}”部署成功：" +
                    $"备战席源槽变化 {verification.SourceDifference:F1}，" +
                    $"场上目标槽变化 {verification.TargetDifference:F1}。");
                return true;
            }

            if (!verification.DefinitelyUnchanged)
            {
                Publish(
                    TaskEventLevel.Warning,
                    "PreparationDeployAmbiguous",
                    $"“{placement.Source.Character.Name}”拖动后的源槽/目标槽变化不完整" +
                    $"（{verification.SourceDifference:F1}/{verification.TargetDifference:F1}）；" +
                    "为避免重复拖动已停止并等待重新识别。");
                return false;
            }

            Publish(
                TaskEventLevel.Information,
                "PreparationDeployRetry",
                $"第 {attempt} 次拖动未产生画面变化，准备重试。");
        }

        return false;
    }

    private async Task<PreparationMoveVisualChange> VerifyMoveAsync(
        nint windowHandle,
        CaptureFrame before,
        PixelRect sourceReference,
        PixelRect targetReference,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        PreparationMoveVisualChange? previous = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(
                window,
                cancellationToken);
            var page = pageClassifier.Classify(frame);
            if (string.Equals(
                    page?.PageId,
                    PreparationCompanionSelectionPolicy.PageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!await ResolveCompanionSelectionAsync(
                        windowHandle,
                        expectedPreparationPageId,
                        cancellationToken))
                {
                    return previous ?? new PreparationMoveVisualChange(0, 0);
                }

                previous = null;
                continue;
            }

            var current = PreparationMoveVerifier.Compare(
                before,
                frame,
                sourceReference,
                targetReference);
            if (current.MoveObserved &&
                previous?.MoveObserved == true)
            {
                return current;
            }

            previous = current;
            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }

        return previous ?? new PreparationMoveVisualChange(0, 0);
    }

    private async Task<bool> ResolveCompanionSelectionAsync(
        nint windowHandle,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        const int maximumSelectionAttempts = 3;
        for (var attempt = 1; attempt <= maximumSelectionAttempts; attempt++)
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(window, cancellationToken);
            var page = pageClassifier.Classify(frame);
            if (string.Equals(
                    page?.PageId,
                    expectedPreparationPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(
                    page?.PageId,
                    PreparationCompanionSelectionPolicy.PageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    TaskEventLevel.Warning,
                    "CompanionSelectionPageLost",
                    $"伙伴选择第 {attempt}/{maximumSelectionAttempts} 次动作前页面不再可确认；" +
                    "未发送盲点输入。");
                return false;
            }

            Publish(
                TaskEventLevel.Information,
                "CompanionSelectionAttempt",
                $"检测到姬子•启行的伙伴选择阻塞页；第 {attempt}/" +
                $"{maximumSelectionAttempts} 次选择第一个候选并确认。");
            if (!await ClickPreparationPointAsync(
                    window,
                    PreparationCompanionSelectionPolicy.FirstCandidatePoint,
                    "选择首个列车同行伙伴",
                    TimeSpan.FromMilliseconds(400),
                    cancellationToken))
            {
                continue;
            }

            var candidateWindow =
                await foregroundGuard.WaitUntilForegroundAsync(
                    windowHandle,
                    cancellationToken);
            var candidateFrame = await capture.CaptureAsync(
                candidateWindow,
                cancellationToken);
            var candidatePage = pageClassifier.Classify(candidateFrame);
            if (!string.Equals(
                    candidatePage?.PageId,
                    PreparationCompanionSelectionPolicy.PageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    TaskEventLevel.Warning,
                    "CompanionSelectionCandidateUnverified",
                    "点击候选后未能确认仍在伙伴选择页；不点击确认按钮。");
                continue;
            }

            if (!await ClickPreparationPointAsync(
                    candidateWindow,
                    PreparationCompanionSelectionPolicy.ConfirmPoint,
                    "确认伙伴选择",
                    TimeSpan.FromMilliseconds(700),
                    cancellationToken))
            {
                continue;
            }

            var consecutivePreparationFrames = 0;
            for (var observation = 1; observation <= 8; observation++)
            {
                var verifyWindow =
                    await foregroundGuard.WaitUntilForegroundAsync(
                        windowHandle,
                        cancellationToken);
                var verifyFrame = await capture.CaptureAsync(
                    verifyWindow,
                    cancellationToken);
                var verifyPage = pageClassifier.Classify(verifyFrame);
                consecutivePreparationFrames = string.Equals(
                    verifyPage?.PageId,
                    expectedPreparationPageId,
                    StringComparison.OrdinalIgnoreCase)
                    ? consecutivePreparationFrames + 1
                    : 0;
                if (consecutivePreparationFrames >= 2)
                {
                    Publish(
                        TaskEventLevel.Information,
                        "CompanionSelectionCompleted",
                        $"伙伴选择已完成并连续两帧回到 {expectedPreparationPageId}。");
                    return true;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(250),
                    cancellationToken);
            }
        }

        Publish(
            TaskEventLevel.Warning,
            "CompanionSelectionFailed",
            $"伙伴选择达到 {maximumSelectionAttempts} 次上限，仍未回到" +
            $" {expectedPreparationPageId}；停止本次布阵输入。");
        return false;
    }

    private void PublishCompanionSelectionExpectation(
        IEnumerable<PreparationPlacement> placements)
    {
        var deployed = placements
            .Select(item => item.Source.Character)
            .ToArray();
        if (!PreparationCompanionSelectionPolicy.CanTrigger(deployed))
        {
            return;
        }

        Publish(
            TaskEventLevel.Information,
            "CompanionSelectionMayAppear",
            "当前计划同时包含姬子•启行和另一名列车同行角色；" +
            "后续只在 Vision 明确识别 companion_selection 时处理伙伴选择，不盲点。");
    }

    private async Task<bool> ClickPreparationPointAsync(
        GameWindowInfo window,
        PixelPoint referencePoint,
        string displayName,
        TimeSpan afterActionDelay,
        CancellationToken cancellationToken)
    {
        var point = MapReferencePoint(window, referencePoint);
        var action = await input.ClickAsync(
            new ClickTarget(
                displayName,
                displayName,
                window,
                BoundsAround(window, point)),
            new ActionPolicy
            {
                AfterActionDelay = afterActionDelay
            },
            cancellationToken);
        if (!action.Succeeded)
        {
            Publish(
                TaskEventLevel.Warning,
                "CompanionSelectionInputFailed",
                action.Message);
        }

        return action.Succeeded;
    }

    private static PixelPoint MapReferencePoint(
        GameWindowInfo window,
        PixelPoint point) =>
        new(
            (int)Math.Round(
                point.X * window.ClientArea.Width /
                (double)OpenCvTemplateMatcher.ReferenceWidth),
            (int)Math.Round(
                point.Y * window.ClientArea.Height /
                (double)OpenCvTemplateMatcher.ReferenceHeight));

    private static PixelRect MapReferenceRect(
        CaptureFrame frame,
        PixelRect source)
    {
        var left = (int)Math.Round(
            source.X * frame.Width /
            (double)OpenCvTemplateMatcher.ReferenceWidth);
        var top = (int)Math.Round(
            source.Y * frame.Height /
            (double)OpenCvTemplateMatcher.ReferenceHeight);
        var right = (int)Math.Round(
            source.Right * frame.Width /
            (double)OpenCvTemplateMatcher.ReferenceWidth);
        var bottom = (int)Math.Round(
            source.Bottom * frame.Height /
            (double)OpenCvTemplateMatcher.ReferenceHeight);
        return new PixelRect(
            left,
            top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top));
    }

    private static PixelRect BoundsAround(
        GameWindowInfo window,
        PixelPoint point)
    {
        var radius = Math.Max(
            8,
            (int)Math.Round(
                10 * window.ClientArea.Width /
                (double)OpenCvTemplateMatcher.ReferenceWidth));
        return new PixelRect(
            point.X - radius,
            point.Y - radius,
            radius * 2,
            radius * 2);
    }

    private PreparationBoardResult Result(
        PreparationBoardStatus status,
        IReadOnlyList<RecognizedBenchCharacter> bench,
        IReadOnlyList<PreparationPlacement> placements,
        string message)
    {
        Publish(
            status is PreparationBoardStatus.RecognitionFailed
                or PreparationBoardStatus.InputFailed
                ? TaskEventLevel.Warning
                : TaskEventLevel.Information,
            status.ToString(),
            message);
        return new PreparationBoardResult(
            status,
            bench,
            placements,
            message);
    }

    private void Publish(
        TaskEventLevel level,
        string code,
        string message) =>
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            level,
            code,
            message));

    public async Task<IReadOnlyList<RecognizedBenchCharacter>?>
        ReadStableBenchCharactersAsync(
            nint windowHandle,
            string expectedPreparationPageId,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreparationPageId);
        return await ReadStableBenchAsync(
            windowHandle,
            minimumOccupied: 0,
            maximumOccupied: BenchSlots.Count,
            requireLeadingContiguousSlots: false,
            expectedPreparationPageId,
            cancellationToken);
    }
}
