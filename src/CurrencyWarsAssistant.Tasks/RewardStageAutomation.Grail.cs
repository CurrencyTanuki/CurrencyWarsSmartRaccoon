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
        string Message);

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(purchaseNames);
        ArgumentNullException.ThrowIfNull(ownedNames);

        if (!await OpenShopAsync(windowHandle, expectedPreparationPage, cancellationToken))
        {
            return new GrailShopPassResult(
                ShopOpened: false,
                ShopRead: false,
                BoughtCharacterName: null,
                BoughtSlot: -1,
                ShopCharacterNames: [],
                Message: "商店未能打开。");
        }

        var slots = await ReadStableShopAsync(windowHandle, consumedSlots: null, cancellationToken);
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
                continue;
            }

            var buyResult = await ClickShopCardAsync(
                windowHandle,
                slot.Slot,
                character.Name,
                cancellationToken);
            var bought = buyResult.Succeeded;
            return new GrailShopPassResult(
                ShopOpened: true,
                ShopRead: true,
                BoughtCharacterName: bought ? character.Name : null,
                BoughtSlot: slot.Slot,
                ShopCharacterNames: shopNames,
                Message: bought
                    ? $"已购买 {character.Name}（槽位 {slot.Slot}）。"
                    : $"购买 {character.Name} 点击未确认成功。");
        }

        return new GrailShopPassResult(
            ShopOpened: true,
            ShopRead: true,
            BoughtCharacterName: null,
            BoughtSlot: -1,
            ShopCharacterNames: shopNames,
            Message: "本轮商店无目标角色（或目标均已拥有）。");
    }
}
