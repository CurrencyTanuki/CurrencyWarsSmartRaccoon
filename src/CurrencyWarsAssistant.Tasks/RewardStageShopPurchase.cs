using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

public sealed record RewardShopPurchaseDecision(
    RewardShopSlot Slot,
    CurrencyWarsCharacterData Character,
    bool IsPresetCandidate,
    bool IsFormationCandidate,
    bool IsGalaxyScholarPairCandidate,
    string Reason);

public sealed class RewardShopPurchasePlanner
{
    public IReadOnlyList<RewardShopPurchaseDecision> Plan(
        IReadOnlyList<RewardShopSlot> slots,
        RewardStageAutomationOptions options,
        IEnumerable<string> presetPurchaseSuppressedNames,
        IEnumerable<string> formationReservedNames,
        IEnumerable<CurrencyWarsCharacterData>? ownedCharacters = null,
        bool allowGalaxyScholarPairPurchase = true)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(options);
        var presetNames = presetPurchaseSuppressedNames.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        // formationNames 统计"场上已部署的不同角色"（而非全部拥有角色）：
        // 只有名单内的已部署角色计入；满 InitialTeamCapacity 人后，
        // 不再为凑阵容购买名单内角色（只保留三仙舟+2DOT 预设与银河学者补位）。
        var deployedFormationNames = options.InitialFormationPlacements
            .Select(item => item.Source.Character.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var formationNames = deployedFormationNames
            .Where(options.FormationCharacterNames.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var galaxyScholarNames = (ownedCharacters ?? [])
            .Where(GalaxyScholarPairPolicy.IsCandidate)
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var galaxyScholarPairIsAvailable = galaxyScholarNames
            .Concat(slots
                .Where(item => item.Character is not null)
                .Select(item => item.Character!)
                .Where(GalaxyScholarPairPolicy.IsCandidate)
                .Select(item => item.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(GalaxyScholarPairPolicy.ActivationCharacterCount)
            .Count() >= GalaxyScholarPairPolicy.ActivationCharacterCount;
        var decisions = new List<RewardShopPurchaseDecision>();
        foreach (var slot in slots.Where(item => item.Character is not null))
        {
            var character = slot.Character!;
            var retainedPurchase =
                options.RetainedCharacterNames.Contains(character.Name);
            var customPurchase = retainedPurchase ||
                options.AutoPurchaseCharacterNames.Contains(character.Name);
            var presetCandidate =
                options.EnableEarlyStrongFormationPurchase &&
                EarlyStrongFormationCharacterPolicy.IsCandidate(character) &&
                presetNames.Add(character.Name);
            var formationCandidate =
                // 场上已部署总数未满 3 人（避免部署了非名单角色也算满员）
                // 且名单内已部署角色数未满 3，才允许补名单内角色；
                // 满了就不再为凑阵容购买（只保留三仙舟+2DOT 预设与银河学者补位）。
                deployedFormationNames.Count <
                    InitialRewardFormationPlanner.InitialTeamCapacity &&
                formationNames.Count <
                    InitialRewardFormationPlanner.InitialTeamCapacity &&
                options.FormationCharacterNames.Contains(character.Name) &&
                formationNames.Add(character.Name);
            var galaxyScholarPairCandidate =
                allowGalaxyScholarPairPurchase &&
                options.EnableGalaxyScholarRewardStrategy &&
                galaxyScholarPairIsAvailable &&
                galaxyScholarNames.Count <
                    GalaxyScholarPairPolicy.ActivationCharacterCount &&
                GalaxyScholarPairPolicy.IsCandidate(character) &&
                galaxyScholarNames.Add(character.Name);
            if (!customPurchase &&
                !presetCandidate &&
                !formationCandidate &&
                !galaxyScholarPairCandidate)
            {
                continue;
            }

            var reasons = new List<string>();
            if (formationCandidate)
            {
                reasons.Add("补足奖励关不同角色阵容");
            }

            if (customPurchase)
            {
                reasons.Add(retainedPurchase
                    ? "用户保留角色清单（商店所有副本全买）"
                    : "用户自动购买清单");
            }

            if (presetCandidate)
            {
                reasons.Add("三仙舟+2持续伤害过渡预设");
            }

            if (galaxyScholarPairCandidate)
            {
                reasons.Add("补足2名不同银河学者并确保羁绊上场");
            }

            decisions.Add(new RewardShopPurchaseDecision(
                slot,
                character,
                presetCandidate,
                formationCandidate,
                galaxyScholarPairCandidate,
                string.Join("、", reasons)));
        }

        return decisions;
    }
}

public static class RewardMineOwnershipPolicy
{
    public static IReadOnlyList<string> Synchronize(
        IEnumerable<RecognizedBenchCharacter> stableBench,
        bool enableEarlyStrongPreset,
        ISet<string> presetPurchaseSuppressedNames,
        IDictionary<string, CurrencyWarsCharacterData> ownedCharacters)
    {
        ArgumentNullException.ThrowIfNull(stableBench);
        ArgumentNullException.ThrowIfNull(presetPurchaseSuppressedNames);
        ArgumentNullException.ThrowIfNull(ownedCharacters);
        var newlySuppressed = new List<string>();
        foreach (var item in stableBench)
        {
            ownedCharacters[item.Character.Name] = item.Character;
            if (enableEarlyStrongPreset &&
                EarlyStrongFormationCharacterPolicy.IsCandidate(item.Character) &&
                presetPurchaseSuppressedNames.Add(item.Character.Name))
            {
                newlySuppressed.Add(item.Character.Name);
            }
        }

        return newlySuppressed;
    }
}
public enum RewardShopPurchasePostcondition
{
    Uncertain,
    ConfirmedInShop,
    ConfirmedAfterAutomaticClose,
    NotPurchased
}

public readonly record struct RewardShopPurchaseVerification(
    RewardShopPurchasePostcondition Postcondition,
    string? PageId)
{
    public bool Confirmed =>
        Postcondition is RewardShopPurchasePostcondition.ConfirmedInShop or
            RewardShopPurchasePostcondition.ConfirmedAfterAutomaticClose;

    public bool ShopAutomaticallyClosed =>
        Postcondition ==
        RewardShopPurchasePostcondition.ConfirmedAfterAutomaticClose;

    public bool RequiresFreshShopSnapshot => ShopAutomaticallyClosed;
}

public static class RewardShopPurchaseSafetyPolicy
{
    public static bool RequiresProtectedRecoveryAfterTimeout(
        RewardShopPurchaseVerification verification) =>
        verification.Postcondition == RewardShopPurchasePostcondition.Uncertain;

    public static bool ContinueRewardStageAfterProtectedUncertainPurchase(
        RewardShopPurchaseVerification verification) =>
        verification.Postcondition == RewardShopPurchasePostcondition.Uncertain;
}

public static class RewardShopBatchTransitionPolicy
{
    public static bool IsCompletedBatchPage(
        string? pageId,
        string expectedPreparationPage) =>
        string.Equals(
            pageId,
            "reward_shop",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            pageId,
            expectedPreparationPage,
            StringComparison.OrdinalIgnoreCase);
}

public static class RewardShopBatchSnapshotPolicy
{
    public const int MaximumObservations = 2;
}

public static class RewardShopPurchaseContextPolicy
{
    public static bool CanUseObservation(
        string? classifiedPageId,
        IReadOnlyList<RewardShopSlot> beforePurchase,
        IReadOnlyList<RewardShopSlot> observed,
        int targetSlot)
    {
        if (string.Equals(
                classifiedPageId,
                "reward_shop",
                StringComparison.OrdinalIgnoreCase))
        {
            return IsContextualShopFrame(
                beforePurchase,
                observed,
                targetSlot,
                requiredSupportingSlots: 1);
        }

        return classifiedPageId is null &&
            IsContextualShopFrame(
                beforePurchase,
                observed,
                targetSlot,
                requiredSupportingSlots: 2);
    }

    public static bool IsContextualShopFrame(
        IReadOnlyList<RewardShopSlot> beforePurchase,
        IReadOnlyList<RewardShopSlot> observed,
        int targetSlot,
        IReadOnlySet<int>? consumedSlots = null,
        int requiredSupportingSlots = 2)
    {
        ArgumentNullException.ThrowIfNull(beforePurchase);
        ArgumentNullException.ThrowIfNull(observed);
        var observedBySlot = observed.ToDictionary(item => item.Slot);
        var supportingSlots = beforePurchase.Count(before =>
            before.Slot != targetSlot &&
            consumedSlots?.Contains(before.Slot) != true &&
            before.Character is { } expected &&
            observedBySlot.TryGetValue(before.Slot, out var current) &&
            current.Character is { } actual &&
            string.Equals(
                expected.Id,
                actual.Id,
                StringComparison.OrdinalIgnoreCase));
        return supportingSlots >= requiredSupportingSlots;
    }
}

public sealed class RewardShopPurchasePageTracker(
    int slotIndex,
    string expectedCharacterId,
    string expectedPreparationPage)
{
    private readonly RewardShopPurchaseVerificationAccumulator _shop =
        new(slotIndex, expectedCharacterId);
    private int _consecutivePreparationFrames;

    public RewardShopPurchaseVerification Observe(
        string? pageId,
        IReadOnlyList<RewardShopSlot>? slots = null)
    {
        if (string.Equals(
                pageId,
                expectedPreparationPage,
                StringComparison.OrdinalIgnoreCase))
        {
            _consecutivePreparationFrames++;
            return new RewardShopPurchaseVerification(
                _consecutivePreparationFrames >= 2
                    ? RewardShopPurchasePostcondition
                        .ConfirmedAfterAutomaticClose
                    : RewardShopPurchasePostcondition.Uncertain,
                pageId);
        }

        _consecutivePreparationFrames = 0;
        if (!string.Equals(
                pageId,
                "reward_shop",
                StringComparison.OrdinalIgnoreCase) ||
            slots is null)
        {
            return new RewardShopPurchaseVerification(
                RewardShopPurchasePostcondition.Uncertain,
                pageId);
        }

        return new RewardShopPurchaseVerification(
            _shop.Observe(slots) switch
            {
                RewardShopPurchaseVerificationStatus.Confirmed =>
                    RewardShopPurchasePostcondition.ConfirmedInShop,
                RewardShopPurchaseVerificationStatus.NotPurchased =>
                    RewardShopPurchasePostcondition.NotPurchased,
                _ => RewardShopPurchasePostcondition.Uncertain
            },
            pageId);
    }
}

public static class RewardShopPurchaseTiming
{
    public static readonly TimeSpan MaximumBatchDuration =
        TimeSpan.FromSeconds(15);
    public static readonly TimeSpan VerificationTimeout =
        TimeSpan.FromSeconds(10);
    public static readonly TimeSpan VerificationPollInterval =
        TimeSpan.FromMilliseconds(250);
}
