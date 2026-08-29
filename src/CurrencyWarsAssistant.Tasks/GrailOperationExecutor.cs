using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」操作执行器：把纯逻辑决策（<see cref="GrailLoopOperation"/> 的动作与
/// <see cref="GrailTrialResponseDecider"/> 的选侧）接到既有执行组件上，并回填状态。
/// 替代旧 FateGrailProductionExecutor（审计确认其动作分支缺失导致整局必停——本类逐动作补齐）。
/// </summary>
public sealed class GrailOperationExecutor(
    RewardStageAutomationController rewardStage,
    PreparationBoardController preparationBoard,
    WishTrialSelectionAutomation trialSelection,
    TrialRecruitSelectionAutomation trialRecruit,
    GrailRunStateHolder stateHolder)
{
    private int _frontDeployCount;
    /// <summary>N14 购买名单：命杯三成员 + 昔涟（Archer 商店 0% 不入名单）。</summary>
    public static readonly IReadOnlySet<string> ShopPurchaseNames = new HashSet<string>(
        ["远坂凛", "吉尔伽美什", "Saber", GrailRunSnapshot.XilianName]);

    /// <summary>当前局面快照（由编排层在每次识别更新后刷新，供 select 委托内决策使用）。</summary>
    public GrailRunSnapshot? LatestSnapshot { get; set; }

    /// <summary>N14 一轮：买一个命杯角色 → 关商店 → 上场。返回是否买到了。</summary>
    public async Task<bool> ExecuteShopPassAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var pass = await rewardStage.GrailShopPassAsync(
            windowHandle,
            ShopPurchaseNames,
            snapshot.OwnedCharacterNames,
            expectedPreparationPageId,
            cancellationToken);
        if (pass.BoughtCharacterName is null)
        {
            return false;
        }

        // 买后即退（N14：关商店→上场→选祈愿→重开商店）
        await rewardStage.CloseShopAsync(windowHandle, expectedPreparationPageId, cancellationToken);

        // 上场：读到该角色后拖到前台（部署计数由本执行器维护，避免拖上已占槽）
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            windowHandle, expectedPreparationPageId, cancellationToken);
        var bought = bench?.FirstOrDefault(item =>
            string.Equals(item.Character.Name, pass.BoughtCharacterName, StringComparison.OrdinalIgnoreCase));
        if (bought is null)
        {
            return true;
        }

        // N14 语义：昔涟只买不上场（不占上场人口、不触发关店-上场仪式）
        var boughtIsXilian = string.Equals(pass.BoughtCharacterName, GrailRunSnapshot.XilianName, StringComparison.Ordinal);
        if (!boughtIsXilian)
        {
            var slot = Math.Min(_frontDeployCount, 3);
            var lane = _frontDeployCount < 4 ? PreparationLane.Front : PreparationLane.Back;
            var backSlot = Math.Min(Math.Max(_frontDeployCount - 4, 0), 5);
            var deployed = await preparationBoard.GrailDeployBenchCharacterAsync(
                windowHandle, bought,
                lane, lane == PreparationLane.Front ? slot : backSlot,
                expectedPreparationPageId, cancellationToken);
            if (deployed)
            {
                _frontDeployCount++;
            }
        }
        else
        {
            stateHolder.MarkNewBondMemberAvailable();
        }

        return true;
    }

    /// <summary>N17a 前半：买经验（点购买经验区 2 次 = 8 金币升 1 人口；金币校验由决策层 N17 把关）。</summary>
    public Task<bool> ExecuteBuyXpAsync(nint windowHandle, CancellationToken cancellationToken) =>
        rewardStage.BuyPopulationAsync(windowHandle, cancellationToken);

    /// <summary>S1A/S1B：按保留线卖人凑金币。</summary>
    public async Task<GrailSellResult> ExecuteSellForGoldAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        int targetGold,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            windowHandle, expectedPreparationPageId, cancellationToken) ?? [];
        var sellable = bench
            .Where(item => !item.Character.BondNames.Any(
                bond => bond is not null && bond.Contains("命运圣杯", StringComparison.Ordinal))
                && !(item.Character.Costs ?? []).Contains(5))
            .Take(snapshot.SellableBeyondKeepLineCount)
            .ToArray();
        return await preparationBoard.GrailSellUntilGoldAsync(
            windowHandle,
            sellable,
            sellable.Select(item => (item.Character.Costs ?? []).Min()).ToArray(),
            snapshot.Gold,
            targetGold,
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>
    /// 祈愿弹框响应（F1~F17）：决策在 select 委托内、点击前完成；
    /// 血量取证取点击前持有器缓存（确认后血已扣 88，事后取会永久失真）。
    /// 仅 Confirmed 后落地状态；弃权（RecognitionUncertain）不点击不计数。
    /// </summary>
    public async Task<bool> ExecuteWishDialogAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var snapshot = LatestSnapshot;
        if (snapshot is null)
        {
            return false;
        }

        var healthAtSelection = snapshot.TeamHealth;
        GrailTrialResponse? response = null;
        var status = await trialSelection.TryHandleSelectionAsync(
            windowHandle,
            cancellationToken,
            select: (leftName, rightName) =>
            {
                var info = trialSelection.LatestTrialInfo;
                var context = new GrailTrialPairContext(
                    leftName,
                    info?.LeftReward,
                    rightName,
                    info?.RightReward);
                response = GrailTrialResponseDecider.Decide(snapshot, context);
                return response.Side switch
                {
                    GrailTrialSide.Left => 0,
                    GrailTrialSide.Right => 1,
                    _ => null, // RecognitionUncertain：弃权不点击
                };
            });

        if (status != WishTrialSelectionStatus.Confirmed || response is null)
        {
            return false;
        }

        stateHolder.ApplyTrialResponse(response, healthAtSelection);

        // F11a：立刻打开两个五费聘用书（全员优先昔涟；命中歧义时保守不点，等下轮重试）
        if (response.OpenLettersAfter)
        {
            await OpenLettersAsync(windowHandle, snapshot.Goal, cancellationToken);
        }

        return true;
    }

    private async Task OpenLettersAsync(nint windowHandle, GrailUserGoal goal, CancellationToken cancellationToken)
    {
        // 目标集：全员优先昔涟（判定必需）；单人=昔涟/Archer（组件唯一命中才点，歧义保守不点）
        var targets = goal == GrailUserGoal.All
            ? (IReadOnlySet<string>)new HashSet<string> { GrailRunSnapshot.XilianName }
            : new HashSet<string> { GrailRunSnapshot.XilianName, GrailRunSnapshot.ArcherName };

        // 带重试：第二本书可能不在固定格/候选暂不匹配——重试直到两本全开或尝试耗尽
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (_, obtained, opened, _, _, _, _, _) = stateHolder.PeekEventState();
            if (opened >= obtained)
            {
                return;
            }

            var status = await trialRecruit.TryOpenAndSelectAsync(
                windowHandle, targets, cancellationToken);
            if (status == TrialRecruitSelectionStatus.Selected)
            {
                stateHolder.MarkLetterOpened();
            }

            await Task.Delay(1200, cancellationToken);
        }
    }
}

