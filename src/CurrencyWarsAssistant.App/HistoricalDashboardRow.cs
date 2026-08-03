namespace CurrencyWarsAssistant.App;

public sealed record HistoricalDashboardRow(
    string NodeId,
    string FinalDamageDisplay,
    string RemainingActionDisplay,
    string GoldDeltaDisplay,
    string GoldRewardDisplay,
    string DataQualityDisplay,
    string PerfectClearDisplay,
    string PerfectClearState,
    string HealthDeltaDisplay,
    string HealthDeltaState,
    double DamageNormalized,
    bool IsLatest,
    long? FinalDamage,
    int? RemainingActionValue,
    int? AbsoluteGold,
    string AbsoluteGoldDisplay,
    long? TheoreticalDamage,
    string TheoreticalDamageDisplay,
    bool IsRewardNode);
