using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」操作执行器：把纯逻辑决策（<see cref="GrailLoopOperation"/> 的动作与
/// <see cref="GrailTrialResponseDecider"/> 的选侧）接到既有执行组件上，并回填状态。
/// 替代旧 FateGrailProductionExecutor（审计确认其动作分支缺失导致整局必停——本类逐动作补齐）。
/// </summary>
public sealed partial class GrailOperationExecutor(
    RewardStageAutomationController rewardStage,
    PreparationBoardController preparationBoard,
    WishTrialSelectionAutomation trialSelection,
    TrialRecruitSelectionAutomation trialRecruit,
    GrailRunStateHolder stateHolder,
    GameDataCatalog gameData)
{
    private int _frontDeployCount;

    /// <summary>
    /// 新对局必须调用：上场槽计数器是进程级字段，不归零会把上一局用过的
    /// 槽位串进本局（2026-09-02 实测事故：上局黑塔占前台1号位，本局远坂凛
    /// 被放到前台2号位，1号位空着）。
    /// </summary>
    internal void ResetDeploymentProgressForNewMatch()
    {
        _frontDeployCount = 0;
        // 跨局归零同时清空购买账本（账本=本局 M5 实际买到过的角色，防识别漏名导致的重复购买）
        _grailPurchaseLedger.Clear();
    }

    /// <summary>
    /// 本局 M5 圣杯实际购买过的角色账本（进程级持久，跨指令/跨识别帧不失）。
    /// 坑：识别漏名时快照已购集合不完整 → 同名命杯成员被重复购买（2026-09-03 远坂凛×2 实测）。
    /// 规格条款：白名单必须取自状态机持久已购集合（GRAIL_COMMAND_SET M5 注）。
    /// </summary>
    private readonly HashSet<string> _grailPurchaseLedger = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从官方数据动态解析羁绊成员角色名（含费用），替代一切手写名单——
    /// 2026-09-02 隐雷复盘：手写银河学者名单漏了艾丝妲/阮•梅，从此名单一律运行时解析。
    /// </summary>
    public IReadOnlyList<string> GetBondMemberNames(string bondName) =>
        gameData.CurrencyWarsCharacters
            .Where(character => character.BondNames.Any(
                bond => bond is not null && bond.Contains(bondName, StringComparison.Ordinal)))
            .Select(character => character.Name)
            .ToList();

    /// <summary>命运圣杯羁绊成员（动态解析；Archer 商店 0% 刷不出，但保留在白名单无害）。</summary>
    public IReadOnlySet<string> GrailBondMemberNames =>
        new HashSet<string>(GetBondMemberNames("命运圣杯"), StringComparer.OrdinalIgnoreCase);

    /// <summary>单次 ExecuteShopPassAsync 的购买目标上限（命杯 1 + 双银河学者场景兜底）。</summary>
    private const int MaxShopBuyTargetsPerPass = 3;

    /// <summary>1-3 循环模式单条 M5 的刷新上限（仅异常兜底；真实边界=金币耗尽——2026-09-03 用户拍板"刷到金币没了"，绝不允许上限强制关店造成关店空转）。</summary>
    private const int MaxShopRefreshesPerCommand = 40;

    /// <summary>命杯成员+星徽携带者的 1-3 基础人口上限（第 5 人须买经验——2026-09-03 用户最终拍板）。</summary>
    private const int FourMemberBondCap = 4;

    /// <summary>成员达上限时的升 5 金币门槛：金币>10 买经验升 5，≤10 直接停刷收摊（2026-09-03 用户拍板）。</summary>
    private const int XpPushGoldThreshold = 10;

    /// <summary>前台/后台槽位容量（与 A2 位置语义 1-4/1-6 一致）。</summary>
    private const int FrontSlotCapacity = 4;
    private const int BackSlotCapacity = 6;

    /// <summary>最近一次商店 pass 读到的货架全名单（诊断/决策核对用；null=未跑过）。</summary>
    public IReadOnlyList<string>? LastShopPassShelfNames { get; private set; }

    /// <summary>最近一次商店 pass 实际买到并上场的角色名单（M5 结果行口径修复，2026-09-02）。</summary>
    public IReadOnlyList<string> LastShopPassBoughtNames { get; private set; } = Array.Empty<string>();

    /// <summary>最近一次商店 pass 结束时的本地记账金币（1.2.24 修上报口径：持有器缓存滞后于刷新扣款）。</summary>
    public int LastShopPassGold { get; private set; } = -1;

    /// <summary>当前局面快照（由编排层在每次识别更新后刷新，供 select 委托内决策使用）。</summary>
    public GrailRunSnapshot? LatestSnapshot { get; set; }

    /// <summary>用户目标（opening 期无识别快照时构建最小快照用）。</summary>
    public GrailUserGoal Goal { get; set; } = GrailUserGoal.Single;

    /// <summary>5 费判定（唯一口径）：纯 [5] 才算——银狼LV.999（costs=[3,4,5]）按用户拍板视作 3 费（审计#13）。</summary>
    private static bool IsPureFiveCost(CurrencyWarsCharacterData character) =>
        (character.Costs ?? []).Count == 1 && (character.Costs ?? [])[0] == 5;

    /// <summary>A5 无参（N12）判定用：该角色是否纯 5 费（唯一费用=5）。</summary>
    public static bool IsPureFiveCostCharacter(CurrencyWarsCharacterData character) =>
        IsPureFiveCost(character);

    /// <summary>opening 期无识别管线时，用持有器事件态构建最小快照（首祈愿盲选左不依赖识别；奇迹代偿因血量未知自动保守拒绝）。</summary>
    internal GrailRunSnapshot BuildMinimalSnapshot()
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
    /// <summary>
    /// 动态购买白名单（2026-09-02 用户拍板）：基础=命杯三成员+昔涟；当已拥有至少 1 名
    /// 银河学者时，追加「另一个不同的、出战名单内的银河学者」——绝不看到银河学者就买，
    /// 凑不满 2 人买了也白买（单人且不在名单不上场）。
    /// </summary>
    public IReadOnlySet<string> BuildShopPurchaseNames(
        IReadOnlyCollection<string> ownedNames)
    {
        // 购买白名单全部动态解析（2026-09-02 用户拍板"名单一律从数据来"）：
        // ① 命运圣杯成员（N14：见到就买，同名只买一次）；
        // ② 昔涟（非命杯 5 费本体，买了不上场）；
        // ③ 银河学者成员——条件购买：已拥有其中之一时，追加另一个不同的
        //    （凑 2 激活猫猫糕；绝不看到银河学者就买）。
        var names = new HashSet<string>(GrailBondMemberNames, StringComparer.OrdinalIgnoreCase);
        names.Add(GrailRunSnapshot.XilianName);
        var owned = ownedNames as IReadOnlySet<string> ??
            new HashSet<string>(ownedNames, StringComparer.OrdinalIgnoreCase);
        var galaxyScholars = GetBondMemberNames("银河学者");
        if (galaxyScholars.Count(name => owned.Contains(name)) >= 1)
        {
            foreach (var name in galaxyScholars)
            {
                if (!owned.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// 判定本次购买后是否需要按 N14 仪式再来一遍（关店→重开→不刷新→买下一个）：
    /// 仅当货架同帧还剩「白名单内且未拥有」的目标时成立（如双银河学者同刷）。
    /// </summary>
    public static bool HasMoreShopTargetsOnShelf(
        IReadOnlyList<string>? shelfCharacterNames,
        IReadOnlySet<string> purchaseNames,
        IReadOnlyCollection<string> ownedNames) =>
        shelfCharacterNames is not null &&
        shelfCharacterNames.Any(name =>
            purchaseNames.Contains(name) &&
            !ownedNames.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// N14 同帧双银河学者分支（2026-09-02 用户裁定补齐）：货架同帧出现 ≥2 个
    /// 不同的、未拥有的银河学者时，返回其中全部未拥有者作为购买目标——
    /// 不再要求先持有 1 名；不足两个（含重名去重后只剩一个）返回空。
    /// </summary>
    public static IReadOnlyList<string> SameFrameScholarTargets(
        IReadOnlyList<string>? shelfCharacterNames,
        IReadOnlyCollection<string> ownedNames,
        IReadOnlySet<string> galaxyScholarNames)
    {
        if (shelfCharacterNames is null || shelfCharacterNames.Count == 0)
        {
            return [];
        }

        var unowned = new List<string>();
        foreach (var name in shelfCharacterNames)
        {
            if (galaxyScholarNames.Contains(name) &&
                !unowned.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                !ownedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                unowned.Add(name);
            }
        }

        return unowned.Count >= 2 ? unowned : [];
    }

    public async Task<bool> ExecuteShopPassAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken,
        bool shopAlreadyOpen = false,
        bool grailLoopMode = false)
    {
        if (grailLoopMode)
        {
            // 1-3 N14 循环语义（2026-09-03 用户修正）：与下方 1-1/1-2 单轮默认语义分道，
            // 默认路径零改动（既有实机验证行为与测试不动）。
            return await ExecuteGrailShopLoopAsync(
                windowHandle, snapshot, expectedPreparationPageId, cancellationToken);
        }

        LastShopPassShelfNames = null; // 全空读数时不回填上一条指令的旧货架
        // N14 银河学者双买（2026-09-02 用户拍板，1-1/1-2 专属）：一个 pass 只买一个目标；买完后若
        // 货架同帧仍有白名单目标（典型=两个不同银河学者同刷），关店→重开→不刷新→再买。
        // 本默认语义无论金币多少都绝不刷新货架（1-3 循环语义=必须刷新，见循环方法）。
        // 同帧双银河学者分支（2026-09-02 用户裁定补齐）：货架同帧出现 ≥2 个不同的
        // 未拥有银河学者时，即使当前一个都没持有也全部买下（此前只实现"已拥有≥1
        // 才买另一个"，同帧双学者零持有被漏买——实测事故）。
        var owned = new HashSet<string>(
            snapshot.OwnedCharacterNames, StringComparer.OrdinalIgnoreCase);
        owned.UnionWith(_grailPurchaseLedger);
        var galaxyScholars = new HashSet<string>(
            GetBondMemberNames("银河学者"), StringComparer.OrdinalIgnoreCase);
        var extraTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boughtNames = new List<string>();
        var boughtAny = false;
        var shopOpen = shopAlreadyOpen;
        for (var iteration = 0; iteration < MaxShopBuyTargetsPerPass; iteration++)
        {
            var purchaseNames = new HashSet<string>(
                BuildShopPurchaseNames(owned), StringComparer.OrdinalIgnoreCase);
            foreach (var extra in extraTargets)
            {
                purchaseNames.Add(extra);
            }

            var pass = await rewardStage.GrailShopPassAsync(
                windowHandle,
                purchaseNames,
                owned,
                expectedPreparationPageId,
                cancellationToken,
                shopOpen);
            // 无论是否买到都关商店：页面必须回到 preparation_* 才能进入决策流
            //（不关店会让 N14/ShopPass 路由永久停留在商店页——复审阻断 2）
            await rewardStage.CloseShopAsync(windowHandle, expectedPreparationPageId, cancellationToken);
            shopOpen = false;
            if (pass.ShopCharacterNames is { Count: > 0 })
            {
                LastShopPassShelfNames = pass.ShopCharacterNames;
            }

            if (pass.BoughtCharacterName is not null)
            {
                boughtNames.Add(pass.BoughtCharacterName);
            }

            // 本轮货架若命中同帧双银河学者，把其中未拥有的全部并入目标
            //（先于"没买到就退出"判定，否则首轮无命杯目标时双学者会被跳过）。
            foreach (var target in SameFrameScholarTargets(
                         pass.ShopCharacterNames, owned, galaxyScholars))
            {
                extraTargets.Add(target);
                purchaseNames.Add(target);
            }

            if (pass.BoughtCharacterName is null)
            {
                if (extraTargets.Count == 0)
                {
                    break;
                }

                // 没买到但本轮刚锁定同帧学者目标：下一轮继续买（受迭代上限约束）。
                continue;
            }

            boughtAny = true;
            owned.Add(pass.BoughtCharacterName);

            // 上场：读到该角色后拖到前台（部署计数由本执行器维护，避免拖上已占槽）
            var bench = await preparationBoard.ReadStableBenchCharactersAsync(
                windowHandle, expectedPreparationPageId, cancellationToken);
            var bought = bench?.FirstOrDefault(item =>
                string.Equals(item.Character.Name, pass.BoughtCharacterName, StringComparison.OrdinalIgnoreCase));
            if (bought is not null)
            {
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
            }

            if (!HasMoreShopTargetsOnShelf(
                    pass.ShopCharacterNames, purchaseNames, owned))
            {
                break;
            }
        }

        LastShopPassBoughtNames = boughtNames;
        return boughtAny;
    }

    /// <summary>
    /// 1-3 N14 商店循环（2026-09-03 用户修正语义，rule.md 四.9 阶段范围）：
    /// 开店→读货架；买到命杯成员=购买后验证→关店→上场（快照真实空槽，非计数器）→重开；
    /// 同帧多目标=重开不刷新直接买下一个（N14 原文）；买到昔涟=不关店不上场继续看；
    /// 无目标=金币利用最大化（2026-09-03 用户最终拍板，商店控件内置逻辑）：
    ///   成员=4 且未升5：金币>10 买经验升5继续，≤10 直接停刷收摊；
    ///   成员<4 或已升5：刷新到金币不足刷新价为止（无保留线）；
    /// 买前金<费用绝不点（点击必失败）；刷新不可用=收摊关店回备战页。
    /// 终止性：迭代上限 6（买/刷/买经验合计），每步均有界 await。
    /// 银河学者规则仅 1-1 生效：本循环白名单=命杯成员（data/4.4 动态解析）+昔涟。
    /// </summary>
    private async Task<bool> ExecuteGrailShopLoopAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        LastShopPassShelfNames = null; // 全空读数时不回填上一条指令的旧货架
        var owned = new HashSet<string>(
            snapshot.OwnedCharacterNames, StringComparer.OrdinalIgnoreCase);
        owned.UnionWith(_grailPurchaseLedger); // 识别漏名兜底：账本内角色绝不重复购买
        var purchaseNames = new HashSet<string>(GrailBondMemberNames, StringComparer.OrdinalIgnoreCase);
        purchaseNames.Add(GrailRunSnapshot.XilianName);
        var occupiedFront = new HashSet<int>(snapshot.OccupiedFrontSlots);
        var occupiedBack = new HashSet<int>(snapshot.OccupiedBackSlots);
        // 金币本地账（2026-09-03 用户拍板：刷到金币不足刷新为止，无保留线）：
        // 入口守卫已保证金币读数非空；买成/刷新/买经验各扣实价。
        var gold = stateHolder.PeekGold().Value ?? 0;
        // 成员账（含星徽携带者，与快照 BondMemberCount 同口径）+升 5 标记。
        var bondMembers = snapshot.BondMemberCount;
        var xpBought = false;
        var boughtNames = new List<string>();
        var boughtAny = false;
        var shopOpen = false;
        var refreshes = 0;
        var readFailures = 0;
        for (var iteration = 0;
             iteration < MaxShopBuyTargetsPerPass + MaxShopRefreshesPerCommand;
             iteration++)
        {
            var pass = await rewardStage.GrailShopPassAsync(
                windowHandle,
                purchaseNames,
                owned,
                expectedPreparationPageId,
                cancellationToken,
                shopAlreadyOpen: shopOpen,
                verifyPurchase: true,
                canAffordPurchase: cost => gold >= cost);
            if (pass.ShopCharacterNames is { Count: > 0 })
            {
                LastShopPassShelfNames = pass.ShopCharacterNames;
            }

            if (!pass.ShopRead)
            {
                // 刷新动画未落定时读货架会失败：商店仍开着，重试而非提前收摊
                //（1.2.22 实测"刷两次就关店"的诱因之一）。
                readFailures++;
                if (readFailures >= 2 || !shopOpen)
                {
                    break;
                }

                continue;
            }

            readFailures = 0;
            // Pass 成功返回时商店处于打开状态（关店只在买到成员后发生）——
            // 必须置位，否则收摊分支永不执行、页面会停在商店页（1.2.22 实测 bug）。
            shopOpen = true;

            if (pass.BoughtCharacterName is not null)
            {
                if (pass.PurchaseCheck == RewardStageAutomationController.GrailShopPurchaseCheck.Uncertain)
                {
                    // 验证超时：实际可能已买——本地记 owned 防刷新后重买，
                    // 但不计为买到不上报，收摊交决策层 I10 复核（防同一角色重复花费）。
                    owned.Add(pass.BoughtCharacterName);
                    break;
                }

                boughtAny = true;
                owned.Add(pass.BoughtCharacterName);
                _grailPurchaseLedger.Add(pass.BoughtCharacterName); // 记账：跨指令防重买
                stateHolder.RecordPurchased(pass.BoughtCharacterName); // 持久已购（跨识别帧去重，1.2.31）
                boughtNames.Add(pass.BoughtCharacterName);
                gold = Math.Max(0, gold - GetCharacterCost(pass.BoughtCharacterName));
                var isXilian = string.Equals(
                    pass.BoughtCharacterName,
                    GrailRunSnapshot.XilianName,
                    StringComparison.Ordinal);
                if (isXilian)
                {
                    stateHolder.MarkNewBondMemberAvailable(); // 昔涟只买不上场，不关店继续看
                    continue;
                }

                // 命杯成员：买→关店→上场→重开（N14 修正语义：关店仅在买到后发生）
                await rewardStage.CloseShopAsync(windowHandle, expectedPreparationPageId, cancellationToken);
                shopOpen = false;
                if (await DeployBoughtToRealEmptySlotAsync(
                    windowHandle,
                    pass.BoughtCharacterName,
                    expectedPreparationPageId,
                    occupiedFront,
                    occupiedBack,
                    cancellationToken))
                {
                    bondMembers++; // 白名单非昔涟买到并上场=命杯成员+1（星徽携带者同口径计入）
                }
                if (!HasMoreShopTargetsOnShelf(pass.ShopCharacterNames, purchaseNames, owned))
                {
                    break;
                }

                continue; // 同帧还有目标：重开（不刷新）直接买下一个
            }

            if (pass.PurchaseCheck == RewardStageAutomationController.GrailShopPurchaseCheck.NotPurchased)
            {
                break; // 金币不足/点击无效：停止本店购买（与 Shop.cs 批量路径语义一致）
            }

            // 金币利用最大化（2026-09-03 用户最终拍板，商店控件内置逻辑，非决策层）：
            // ①命杯成员+星徽携带者=4（人口已到 1-3 上限）：金币>10 → 立即买经验升 5 人口
            //   继续刷第 5 人；金币≤10 → 金币已尽，直接停止刷新收摊（试炼判定/山穷水尽归决策层）；
            // ②成员<4（或已升 5 人口）：一直刷新到金币不足刷新价为止（无保留线）。
            if (bondMembers >= FourMemberBondCap && !xpBought)
            {
                if (gold <= XpPushGoldThreshold
                    || !await ExecuteBuyXpAsync(windowHandle, cancellationToken))
                {
                    break;
                }

                xpBought = true;
                gold = Math.Max(0, gold - (GrailRunSnapshot.XpPurchaseGoldCost + (stateHolder.PeekXpSurcharge() ? 1 : 0)));
                continue; // 人口已到 5：继续商店循环刷第 5 个成员/昔涟
            }

            var refreshCost = RewardStageAutomationController.ShopRefreshGoldCost
                + (stateHolder.PeekRefreshSurcharge() ? 1 : 0);
            if (refreshes >= MaxShopRefreshesPerCommand || gold < refreshCost)
            {
                break; // 金币不足刷新价（或本条指令刷新上限）：收摊，最终判定归决策层
            }

            if (!await rewardStage.RefreshShopOnceAsync(windowHandle, cancellationToken))
            {
                break;
            }

            gold -= refreshCost;
            refreshes++;
        }

        if (shopOpen)
        {
            // 收摊回备战页：本条指令结束页面必须回 preparation_*（识别门禁要求）。
            // 这与"关店仅在买到后"的仪式语义不冲突——禁的是无买到时反复关店重开的空转。
            await rewardStage.CloseShopAsync(windowHandle, expectedPreparationPageId, cancellationToken);
        }

        LastShopPassBoughtNames = boughtNames;
        LastShopPassGold = gold; // 实时本地账（1.2.24：持有器缓存滞后，回执须报刷新后的真实余额）
        return boughtAny;
    }

    /// <summary>
    /// 上场到真实空槽（修坑#4 同款"计数器与盘面脱钩"风险）：前台 4 槽优先、后台 6 槽兜底；
    /// 占用集合=指令前置 I10 快照（识别保守口径，Uncertain 占位槽也算占用）+本轮已部署。
    /// 无空位=诚实返回不拖拽（拖到有人=互换；第 5 人须先买经验升人口——N17 归决策层）。
    /// </summary>
    private async Task<bool> DeployBoughtToRealEmptySlotAsync(
        nint windowHandle,
        string boughtName,
        string expectedPreparationPageId,
        HashSet<int> occupiedFront,
        HashSet<int> occupiedBack,
        CancellationToken cancellationToken)
    {
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            windowHandle, expectedPreparationPageId, cancellationToken);
        var bought = bench?.FirstOrDefault(item =>
            string.Equals(item.Character.Name, boughtName, StringComparison.OrdinalIgnoreCase));
        if (bought is null)
        {
            return false; // 验证过仍未见卡：交决策层 I10 复核，绝不拖旧坐标
        }

        var lane = PreparationLane.Front;
        var occupied = occupiedFront;
        var capacity = FrontSlotCapacity;
        int? slot = null;
        for (var i = 0; i < capacity; i++)
        {
            if (!occupied.Contains(i))
            {
                slot = i;
                break;
            }
        }

        if (slot is null)
        {
            lane = PreparationLane.Back;
            occupied = occupiedBack;
            capacity = BackSlotCapacity;
            for (var i = 0; i < capacity; i++)
            {
                if (!occupied.Contains(i))
                {
                    slot = i;
                    break;
                }
            }
        }

        if (slot is null)
        {
            return false; // 无空位：不拖不互换，决策层按 N17 处理人口
        }

        if (await preparationBoard.GrailDeployBenchCharacterAsync(
                windowHandle, bought, lane, slot.Value, expectedPreparationPageId, cancellationToken))
        {
            occupied.Add(slot.Value);
            return true;
        }

        return false;
    }

    /// <summary>官方数据角色费用（费用集最小值；银狼等多费用角色按最小计，与卖价口径一致）。</summary>
    private int GetCharacterCost(string name) =>
        gameData.CurrencyWarsCharacters
            .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Costs ?? Array.Empty<int>()).DefaultIfEmpty(0).Min())
            .FirstOrDefault();

    /// <summary>
    /// A4 星徽装配（1.2.25 位置语义定稿）：把物品栏星徽拖到指定前台/后台槽位角色。
    /// 源点=物品栏全面板金色质心探测（探测不到=诚实失败，定标盲拖已废除）；拖后重扫面板自证。
    /// 不依赖部署明细/角色名（识别本局两次把已上场角色读丢，名称解析口径废除——用户 2026-09-03 明令）。
    /// </summary>
    public Task<bool> ExecuteBadgeAssemblyToSlotAsync(
        nint windowHandle,
        PreparationLane lane,
        int slotIndex,
        string expectedPreparationPageId,
        CancellationToken cancellationToken) =>
        lane == PreparationLane.Front
            ? preparationBoard.GrailDragBadgeToFrontCharacterAsync(
                windowHandle,
                new CurrencyWarsAssistant.Advisor.RelativeRegion(0, 0, 0, 0),
                slotIndex,
                expectedPreparationPageId,
                cancellationToken)
            : preparationBoard.GrailDragBadgeToBenchSlotAsync(
                windowHandle,
                slotIndex,
                expectedPreparationPageId,
                cancellationToken);

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
        // 交接包 S1A/S1B 语义（GrailSellAllAsync）：备战席候选+场上可卖候选都卖，
        // 不因凑够 targetGold 提前停（用户拍板"全部卖光"）。
        // 组装器回填时已排除命杯成员/5 费/星徽携带者
        var deployedSellable = snapshot.DeployedNonGrailCharacters.ToArray();
        return await preparationBoard.GrailSellAllAsync(
            windowHandle,
            sellable,
            sellable.Select(item => (item.Character.Costs ?? []).Min()).ToArray(),
            deployedSellable,
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

    internal async Task OpenLettersAsync(nint windowHandle, GrailUserGoal goal, CancellationToken cancellationToken)
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

