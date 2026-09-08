namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// A4 星徽装配幂等预查的判定结果（P1-A，2026-09-09 修复批）。
/// </summary>
internal enum GrailBadgeAssemblyPrecheckOutcome
{
    /// <summary>账本无该槽/该名带徽证据——正常走拖拽装配。</summary>
    ProceedToDrag,

    /// <summary>目标已带徽（按名佐证成立）——幂等成功：不拖拽、不再记账。</summary>
    AlreadyCarries,

    /// <summary>挂起槽位经名字佐证提升为按名记账（解析既有账，非新记账）——不拖拽。</summary>
    PromotePendingSlot,

    /// <summary>账本说挂起但无法按名佐证（画面读不到、台账无名字）——不拖拽不记账，
    /// 诚实失败交决策层 I10 复核（G15 教训：盲目重拖=游戏拒绝横幅）。</summary>
    UncertainNoDrag,
}

/// <summary>
/// A4 星徽装配幂等预查（P1-A，2026-09-09 修复批）+ 装配拒绝横幅文本判定。
/// G15 局实锤：I7 报未携带但目标实际已带徽→A4 重拖→游戏横幅「无法穿戴相同羁绊的
/// 星徽」×2。本守卫在拖拽前查星徽账本（按名携带者∪挂起槽位），幂等命中绝不重拖。
/// 纯函数、零 IO——控制器层行为单测直接覆盖（internal 可测）。
/// </summary>
internal static class GrailBadgeAssemblyGuard
{
    /// <summary>
    /// 拖拽前预查判定。名字佐证优先级：实时画面读数 &gt; 引擎台账期望名
    /// （画面是地面真值；台账仅在画面不可读时作为弱佐证）。
    /// </summary>
    /// <param name="carrierNames">星徽账本：已按名记账的携带者。</param>
    /// <param name="pendingSlotKeys">星徽账本：已记账但未解析角色的挂起槽位键。</param>
    /// <param name="slotKey">本次 A4 的目标槽位键（BadgeLedgerSlotKey 产物）。</param>
    /// <param name="liveOccupantName">目标槽位实时读到的占用人名（null=不可读）。</param>
    /// <param name="expectedOccupantName">引擎台账认为的该槽占用人名（可为 null）。</param>
    public static (GrailBadgeAssemblyPrecheckOutcome Outcome, string? CorroboratedName) Decide(
        IReadOnlySet<string> carrierNames,
        IReadOnlySet<string> pendingSlotKeys,
        string slotKey,
        string? liveOccupantName,
        string? expectedOccupantName)
    {
        var byName = NonEmpty(liveOccupantName) ?? NonEmpty(expectedOccupantName);
        if (byName is not null && carrierNames.Contains(byName))
        {
            return (GrailBadgeAssemblyPrecheckOutcome.AlreadyCarries, byName);
        }

        if (pendingSlotKeys.Contains(slotKey))
        {
            // 画面读数优先；画面不可读时台账期望名亦可佐证（挂起槽位本就源于
            // "装配成功但角色名未解析"，台账名是当时唯一在场证据链的延续）。
            var corroborated = NonEmpty(liveOccupantName) ?? NonEmpty(expectedOccupantName);
            return corroborated is null
                ? (GrailBadgeAssemblyPrecheckOutcome.UncertainNoDrag, null)
                : (GrailBadgeAssemblyPrecheckOutcome.PromotePendingSlot, corroborated);
        }

        return (GrailBadgeAssemblyPrecheckOutcome.ProceedToDrag, null);
    }

    /// <summary>
    /// 装配拒绝横幅判定：OCR 文本命中「无法穿戴相同羁绊的星徽」关键证据。
    /// 用词组合判（穿戴∧羁绊同时出现）抗单字误识——两个词在备战页常规文本中
    /// 均不出现，联合命中误报率极低。null/空文本=未命中。
    /// </summary>
    public static bool IsBadgeRejectionBannerText(string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return false;
        }

        var containsWearable = ocrText.Contains("穿戴", StringComparison.Ordinal)
            || ocrText.Contains("装备", StringComparison.Ordinal);
        var containsBond = ocrText.Contains("羁绊", StringComparison.Ordinal);
        var containsRejection = ocrText.Contains("无法", StringComparison.Ordinal)
            || ocrText.Contains("不能", StringComparison.Ordinal)
            || ocrText.Contains("相同", StringComparison.Ordinal);
        return containsBond && containsRejection && containsWearable;
    }

    private static string? NonEmpty(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name;
}
