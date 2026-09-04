using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」在 <see cref="RewardStageAutomationController"/> 上的新增单轮商店动作
/// （定稿决策树 N14：买→关商店→上场→选祈愿→重开；本类只做"开→读→买一个→返回"，由编排层串联）。
/// 与既有 <see cref="RewardStageAutomation.ShopRefresh.RunShopRefreshPurchaseLoopAsync"/> 的区别：
/// 不循环刷新、单轮只买一个（一轮刷出多个目标时由编排层在两次 Pass 之间插入上场/祈愿）。
/// </summary>
public sealed partial class RewardStageAutomationController
{
    /// <summary>单轮商店 Pass 的结果。</summary>
    public sealed record GrailShopPassResult(
        bool ShopOpened,
        bool ShopRead,
        string? BoughtCharacterName,
        int BoughtSlot,
        IReadOnlyList<string> ShopCharacterNames,
        string Message,
        GrailShopPurchaseCheck PurchaseCheck = GrailShopPurchaseCheck.Confirmed,
        IReadOnlyList<string>? SkippedOwnedNames = null,
        IReadOnlyList<string>? SkippedUnaffordableNames = null);

    /// <summary>购买后验证结论（1.2.21）：Confirmed=槽位清空确认买到；NotPurchased=金币不足/点击无效；
    /// Uncertain=验证超时（实际可能已买，调用方须防重买并交决策层复核）。</summary>
    public enum GrailShopPurchaseCheck
    {
        Confirmed,
        NotPurchased,
        Uncertain,
    }

    /// <summary>
    /// 执行一轮商店 Pass：确保商店打开 → 读稳定快照 → 从目标名单中买第一个未拥有的角色 → 返回（不关商店）。
    /// 关商店/上场/祈愿由编排层按 N14 顺序调用（<see cref="CloseShopAsync"/> 为既有 internal 方法）。
    /// </summary>
    /// <param name="windowHandle">游戏窗口句柄。</param>
    /// <param name="purchaseNames">购买目标名单（凛/闪/Saber/昔涟；Archer 商店 0% 不入名单）。</param>
    /// <param name="ownedNames">已拥有角色名（同名去重：已拥有不再买）。</param>
    /// <param name="expectedPreparationPage">商店所在备战页 ID（1-1/1-2/1-3 布局全局一致）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<GrailShopPassResult> GrailShopPassAsync(
        nint windowHandle,
        IReadOnlySet<string> purchaseNames,
        IReadOnlySet<string> ownedNames,
        string expectedPreparationPage,
        CancellationToken cancellationToken,
        bool shopAlreadyOpen = false,
        bool verifyPurchase = false,
        Func<int, bool>? canAffordPurchase = null)
    {
        ArgumentNullException.ThrowIfNull(purchaseNames);
        ArgumentNullException.ThrowIfNull(ownedNames);

        // N14 衔接：策略确认后游戏可能停在商店页（reward_shop 已开）——此时跳过开店直接读
        if (!shopAlreadyOpen && !await OpenShopAsync(windowHandle, expectedPreparationPage, cancellationToken))
        {
            return new GrailShopPassResult(
                ShopOpened: false,
                ShopRead: false,
                BoughtCharacterName: null,
                BoughtSlot: -1,
                ShopCharacterNames: [],
                Message: "商店未能打开。");
        }

        // 1.2.68：已开店（刷新循环热路径）入口前置减半——页面门禁与双帧稳定判据仍在。
        var slots = await ReadStableShopAsync(
            windowHandle,
            consumedSlots: null,
            cancellationToken,
            initialSettleDelay: shopAlreadyOpen
                ? TimeSpan.FromMilliseconds(250)
                : null);
        if (slots is null)
        {
            return new GrailShopPassResult(
                ShopOpened: true,
                ShopRead: false,
                BoughtCharacterName: null,
                BoughtSlot: -1,
                ShopCharacterNames: [],
                Message: "商店快照读取失败。");
        }

        var shopNames = slots
            .Where(slot => slot.Character is not null)
            .Select(slot => slot.Character!.Name)
            .ToArray();

        // P-16（1.2.70）：跳过原因收集（不打日志，由执行器汇入 GrailShopLoopSummary）。
        var skippedOwned = new List<string>();
        var skippedUnaffordable = new List<string>();

        foreach (var slot in slots)
        {
            var character = slot.Character;
            if (character is null)
            {
                continue;
            }

            if (!purchaseNames.Contains(character.Name)
                || ownedNames.Contains(character.Name))
            {
                skippedOwned.Add(character.Name);
                continue;
            }

            if (canAffordPurchase is not null)
            {
                // 2026-09-03 用户判定：剩余金币<费用时点击必失败，绝不点（费用=官方数据费用集最小值）
                var cost = (character.Costs ?? Array.Empty<int>()).DefaultIfEmpty(0).Min();
                if (!canAffordPurchase(cost))
                {
                    skippedUnaffordable.Add($"{character.Name}(费{cost})");
                    continue;
                }
            }

            var buyResult = await ClickShopCardAsync(
                windowHandle,
                slot.Slot,
                character.Name,
                cancellationToken);
            var bought = buyResult.Succeeded;
            var check = GrailShopPurchaseCheck.Confirmed;
            if (bought && verifyPurchase)
            {
                var postcondition = await VerifyGrailPurchaseAsync(
                    windowHandle, slots, slot, character,
                    expectedPreparationPage, cancellationToken);
                check = postcondition switch
                {
                    RewardShopPurchasePostcondition.NotPurchased
                        => GrailShopPurchaseCheck.NotPurchased,
                    RewardShopPurchasePostcondition.Uncertain
                        => GrailShopPurchaseCheck.Uncertain,
                    _ => GrailShopPurchaseCheck.Confirmed,
                };
                bought = check == GrailShopPurchaseCheck.Confirmed;
            }

            if (!bought && check != GrailShopPurchaseCheck.Confirmed)
            {
                // 购买后验证不过：不计买到。NotPurchased=金币不足/点击无效（停止本店购买）；
                // Uncertain=可能实际已买（保留名字，调用方本地防重买并交决策层复核）。
                return new GrailShopPassResult(
                    ShopOpened: true,
                    ShopRead: true,
                    BoughtCharacterName: character.Name,
                    BoughtSlot: slot.Slot,
                    ShopCharacterNames: shopNames,
                    Message: check == GrailShopPurchaseCheck.NotPurchased
                        ? $"{character.Name} 点击后未确认购买成功（可能金币不足），本 Pass 停止购买。"
                        : $"{character.Name} 购买结果不确定（验证超时），收摊交决策层 I10 复核。",
                    PurchaseCheck: check,
                    SkippedOwnedNames: skippedOwned,
                    SkippedUnaffordableNames: skippedUnaffordable);
            }

            return new GrailShopPassResult(
                ShopOpened: true,
                ShopRead: true,
                BoughtCharacterName: bought ? character.Name : null,
                BoughtSlot: slot.Slot,
                ShopCharacterNames: shopNames,
                Message: bought
                    ? $"已购买 {character.Name}（槽位 {slot.Slot}）。"
                    : $"购买 {character.Name} 点击未确认成功。",
                SkippedOwnedNames: skippedOwned,
                SkippedUnaffordableNames: skippedUnaffordable);
        }

        return new GrailShopPassResult(
            ShopOpened: true,
            ShopRead: true,
            BoughtCharacterName: null,
            BoughtSlot: -1,
            ShopCharacterNames: shopNames,
            Message: "本轮商店无目标角色（或目标均已拥有）。",
            SkippedOwnedNames: skippedOwned,
            SkippedUnaffordableNames: skippedUnaffordable);
    }

    /// <summary>
    /// 购买后验证（1.2.21 补，修"M5 买到=点击成功"误报）：复用 1-1/1-2 批量路径的
    /// 后置条件基建——槽位清空/页面自动回备战=买到；原槽连续两帧仍在=未买到（金币不足）；
    /// 超时=Uncertain（交调用方防重买+决策层复核）。
    /// </summary>
    private async Task<RewardShopPurchasePostcondition> VerifyGrailPurchaseAsync(
        nint windowHandle,
        IReadOnlyList<RewardShopSlot> beforePurchase,
        RewardShopSlot slot,
        CurrencyWarsCharacterData character,
        string expectedPreparationPage,
        CancellationToken cancellationToken)
    {
        var decision = new RewardShopPurchaseDecision(
            slot,
            character,
            IsPresetCandidate: false,
            IsFormationCandidate: false,
            IsGalaxyScholarPairCandidate: false,
            Reason: "grail_loop");
        var verification = await VerifyShopPurchaseAsync(
            windowHandle,
            beforePurchase,
            decision,
            expectedPreparationPage,
            ActiveUtcNow + RewardShopPurchaseTiming.VerificationTimeout,
            cancellationToken);
        return verification.Postcondition;
    }
}
