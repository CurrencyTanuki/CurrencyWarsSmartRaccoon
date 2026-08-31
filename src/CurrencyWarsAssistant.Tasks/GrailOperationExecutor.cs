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

    /// <summary>用户目标（opening 期无识别快照时构建最小快照用）。</summary>
    public GrailUserGoal Goal { get; set; } = GrailUserGoal.Single;

    /// <summary>5 费判定（唯一口径）：纯 [5] 才算——银狼LV.999（costs=[3,4,5]）按用户拍板视作 3 费（审计#13）。</summary>
    private static bool IsPureFiveCost(CurrencyWarsCharacterData character) =>
        (character.Costs ?? []).Count == 1 && (character.Costs ?? [])[0] == 5;

    /// <summary>opening 期无识别管线时，用持有器事件态构建最小快照（首祈愿盲选左不依赖识别；奇迹代偿因血量未知自动保守拒绝）。</summary>
    private GrailRunSnapshot BuildMinimalSnapshot()
    {
        var (wishes, obtained, opened, miracle, mHealth, cauldron, givenUp, newMember) = stateHolder.PeekEventState();
        var (health, _) = stateHolder.PeekHealth();
        return new GrailRunSnapshot
        {
            Goal = Goal,
            TeamHealth = health,
            Population = GrailRunSnapshot.BasePopulation,
            WishesResponded = wishes,
            LettersObtained = obtained,
            LettersOpened = opened,
            MiracleCompensationSelected = miracle,
            MiracleCompensationSelectedAtHealth = mHealth,
            InfiniteCauldronSelected = cauldron,
            FiveBondGivenUp = givenUp,
            NewBondMemberAvailable = newMember,
            HasFiveCostBody = cauldron,
        };
    }

    /// <summary>N14 一轮：买一个命杯角色 → 关商店 → 上场。返回是否买到了。</summary>
    public async Task<bool> ExecuteShopPassAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken,
        bool shopAlreadyOpen = false)
    {
        var pass = await rewardStage.GrailShopPassAsync(
            windowHandle,
            ShopPurchaseNames,
            snapshot.OwnedCharacterNames,
            expectedPreparationPageId,
            cancellationToken,
            shopAlreadyOpen);
        // 无论是否买到都关商店：页面必须回到 preparation_* 才能进入决策流
        //（不关店会让 N14/ShopPass 路由永久停留在商店页——复审阻断 2）
        await rewardStage.CloseShopAsync(windowHandle, expectedPreparationPageId, cancellationToken);

        if (pass.BoughtCharacterName is null)
        {
            return false;
        }

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
                && !IsPureFiveCost(item.Character))
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
        var snapshot = LatestSnapshot ?? BuildMinimalSnapshot();
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
        // 兜底开关（用户公理）：单人目标任选 5 费（聘用书开了必须拿人）；全员仍优先昔涟，
        // 但第二轮起也放开兜底——先拿住一个 5 费总比废书强
        var allowFallback = goal == GrailUserGoal.Single;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (_, obtained, opened, _, _, _, _, _) = stateHolder.PeekEventState();
            if (opened >= obtained)
            {
                return;
            }

            var status = await trialRecruit.TryOpenAndSelectAsync(
                windowHandle, targets, cancellationToken,
                fallbackPickFirst: allowFallback || attempt >= 1);
            if (status == TrialRecruitSelectionStatus.Selected)
            {
                stateHolder.MarkLetterOpened();
            }
            else if (status == TrialRecruitSelectionStatus.NoTargetMatch
                || status == TrialRecruitSelectionStatus.NotDetected)
            {
                // 用户原则：绝不放过机会——候选里没有优先目标也不能让书白开着。
                // 兜底：不传目标集（组件会选第一个候选），先把书打开拿到 5 费再说
                //（昔涟没开到还有采购专员/067 等其他来源，书不开=这个来源也废了）
                var fallback = await trialRecruit.TryOpenAndSelectAsync(
                    windowHandle, new HashSet<string>(), cancellationToken);
                if (fallback == TrialRecruitSelectionStatus.Selected)
                {
                    stateHolder.MarkLetterOpened();
                }
            }

            await Task.Delay(300, cancellationToken); // 提速（用户拍板）：开书后 1200→300
        }
    }
}

