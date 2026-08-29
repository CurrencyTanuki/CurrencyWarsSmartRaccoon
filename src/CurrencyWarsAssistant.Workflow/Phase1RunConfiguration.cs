using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Workflow;

/// <summary>
/// An immutable copy of every value that can affect one automation run.
/// UI changes made after <see cref="Create"/> are intentionally invisible to
/// the running workflow.
/// </summary>
public sealed record Phase1RunConfiguration(
    nint WindowHandle,
    OpeningFilterSet Filters,
    OpeningRerollLoopOptions Options)
{
    public static Phase1RunConfiguration Create(
        nint windowHandle,
        OpeningFilterSet filters,
        OpeningRerollLoopOptions options)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(options);

        return new Phase1RunConfiguration(
            windowHandle,
            CopyFilters(filters),
            CopyOptions(options));
    }

    private static OpeningFilterSet CopyFilters(OpeningFilterSet source) => new()
    {
        InvestmentEnvironments = source.InvestmentEnvironments
            .Select(item => item with { })
            .ToArray(),
        Competitors = source.Competitors.Select(item => item with { }).ToArray(),
        EnemyModifiers = source.EnemyModifiers.Select(item => item with { }).ToArray(),
        Combinations = source.Combinations.Select(item => new OpeningCombinationFilter
        {
            Id = item.Id,
            DisplayName = item.DisplayName,
            State = item.State,
            InvestmentEnvironmentIds = item.InvestmentEnvironmentIds.ToArray(),
            CompetitorIds = item.CompetitorIds.ToArray(),
            EnemyModifierIds = item.EnemyModifierIds.ToArray()
        }).ToArray(),
        Profiles = source.Profiles.Select(item => new OpeningFilterProfile
        {
            Id = item.Id,
            DisplayName = item.DisplayName,
            IsEnabled = item.IsEnabled,
            AcceptedInvestmentEnvironmentIds =
                item.AcceptedInvestmentEnvironmentIds.ToArray(),
            RequiredCompetitorIds = item.RequiredCompetitorIds.ToArray(),
            RejectedCompetitorIds = item.RejectedCompetitorIds.ToArray(),
            RequiredEnemyModifierIds = item.RequiredEnemyModifierIds.ToArray(),
            RejectedEnemyModifierIds = item.RejectedEnemyModifierIds.ToArray(),
            PreferredInvestmentStrategyIds =
                item.PreferredInvestmentStrategyIds.ToArray()
        }).ToArray()
    };

    private static OpeningRerollLoopOptions CopyOptions(
        OpeningRerollLoopOptions source) => new()
    {
        MaximumRounds = source.MaximumRounds,
        MaximumRuntime = source.MaximumRuntime,
        DeployMatchedOpening = source.DeployMatchedOpening,
        CompleteRewardStages = source.CompleteRewardStages,
        InitialRewardCharacterNames = source.InitialRewardCharacterNames?
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        BenchSaleMode = source.BenchSaleMode,
        BenchSaleInterestThreshold = source.BenchSaleInterestThreshold,
        EnableUnknownPageEscapeRecovery =
            source.EnableUnknownPageEscapeRecovery,
        GameMode = source.GameMode,
        RewardStage = new RewardStageAutomationOptions
        {
            EnableEarlyStrongFormationPurchase =
                source.RewardStage.EnableEarlyStrongFormationPurchase,
            EnableGalaxyScholarRewardStrategy =
                source.RewardStage.EnableGalaxyScholarRewardStrategy,
            AutoPurchaseCharacterNames = source.RewardStage
                .AutoPurchaseCharacterNames
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            RetainedCharacterNames = source.RewardStage
                .RetainedCharacterNames
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            PreferredInvestmentStrategyIds = source.RewardStage
                .PreferredInvestmentStrategyIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            SelectedInvestmentEnvironmentId =
                source.RewardStage.SelectedInvestmentEnvironmentId
        }
    };
}
