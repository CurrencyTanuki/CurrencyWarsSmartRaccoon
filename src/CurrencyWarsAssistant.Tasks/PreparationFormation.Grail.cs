namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」在 <see cref="PreparationBoardController"/> 上的新增适配动作
/// （定稿决策树 N12/N13/N14/N17a 与 S1A/S1B 卖人）。
/// 机制依据（用户 2026-08-29 确认）：拖到空位=移动，拖到有人位置=互换；
/// 上场=拖到前台/后台空位；N12 最左置位=拖到备战席第 0 格（有人自动互换）。
/// </summary>
public sealed partial class PreparationBoardController
{
    /// <summary>把一名备战席角色拖上场（前台/后台指定槽位，既有 DeployWithVerificationAsync 带验证）。</summary>
    internal async Task<bool> GrailDeployBenchCharacterAsync(
        nint windowHandle,
        RecognizedBenchCharacter candidate,
        PreparationLane lane,
        int targetSlot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        return await DeployWithVerificationAsync(
            windowHandle,
            new PreparationPlacement(candidate, lane, targetSlot),
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>出售一名指定备战席角色（既有 SellCharacterWithVerificationAsync：拖到出售区+双帧空槽验证）。</summary>
    internal async Task<bool> GrailSellBenchCharacterAsync(
        nint windowHandle,
        RecognizedBenchCharacter candidate,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        return await SellCharacterWithVerificationAsync(
            windowHandle,
            candidate,
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>
    /// TargetGold 卖人（S1A/S1B）：按候选顺序逐个出售，直到金币达到目标或无可卖角色。
    /// 保护规则由调用方传入候选（组装器口径：非命杯成员、非 5 费；保留线已扣除）。
    /// </summary>
    /// <param name="sellableCandidates">可卖候选（按出售优先序）。</param>
    /// <param name="currentGold">当前金币（持有器缓存）。</param>
    /// <param name="targetGold">目标金币（N17a 前置=8；常规刷新=当前刷新价）。</param>
    /// <param name="goldPerSale">每名角色的出售所得（按候选顺序对应；SaleValue=角色最低费用）。</param>
    internal async Task<GrailSellResult> GrailSellUntilGoldAsync(
        nint windowHandle,
        IReadOnlyList<RecognizedBenchCharacter> sellableCandidates,
        IReadOnlyList<int> goldPerSale,
        int currentGold,
        int targetGold,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var sold = 0;
        var gold = currentGold;
        for (var index = 0; index < sellableCandidates.Count && gold < targetGold; index++)
        {
            var ok = await SellCharacterWithVerificationAsync(
                windowHandle,
                sellableCandidates[index],
                expectedPreparationPageId,
                cancellationToken);
            if (!ok)
            {
                break;
            }

            gold += index < goldPerSale.Count ? goldPerSale[index] : 0;
            sold++;
        }

        return new GrailSellResult(sold, gold, gold >= targetGold);
    }
}

/// <summary>TargetGold 卖人结果。</summary>
public sealed record GrailSellResult(int SoldCount, int EstimatedGold, bool TargetReached);
