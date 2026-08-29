namespace CurrencyWarsAssistant.Game;

/// <summary>
/// Resolves the player-facing character cost without changing the recognition
/// meaning of <c>CurrentCost</c>.
/// </summary>
public static class CharacterCostDisplay
{
    public static int? Resolve(
        int? currentCost,
        IReadOnlyList<int>? catalogCosts)
    {
        if (currentCost.HasValue)
        {
            return currentCost;
        }

        if (catalogCosts is not { Count: > 0 })
        {
            return null;
        }

        var onlyCost = catalogCosts[0];
        return catalogCosts.All(cost => cost == onlyCost)
            ? onlyCost
            : null;
    }
}
