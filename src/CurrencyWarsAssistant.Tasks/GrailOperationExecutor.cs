using CurrencyWarsAssistant.Core;
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

    /// <summary>P-16（1.2.70）：给 GrailRunLoop 等无日志通道的协作对象开放的遥测转发。</summary>
    internal void PublishTelemetry(
        string code,
        string message,
        TaskEventLevel level = TaskEventLevel.Information) =>
        rewardStage.PublishGrailTelemetry(code, message, level);

    /// <summary>
    /// 1.2.102（用户第四次重申口径）：银河学者购买**仅 1-1 生效**——引擎按节点锚点
    /// 同步（1-2/1-3 一律 false）。此前裸 M5 白名单无节点门，1-3 买了真理医生并自动
    /// 上场（实弹 12:00:14，挤占真目标金币与槽位）。
    /// </summary>
    internal bool AllowGalaxyScholarPurchase { get; set; } = true;

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
        // P2-1（2026-09-09 审查修复）：金币本地账三字段一并复位——执行器跨局复用，
        // 上局 LastShopPassGold 若在新局持有器首次读数（I10 economy 帧可能 Unknown）
        // 前播种，会把上局余额串进本局预算（审查实锤的跨局泄漏向量）。
        LastShopPassGold = -1;
        LastShopPassGoldAt = DateTimeOffset.MinValue;
        goldAccountBrokenLastPass = false;
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

    /// <summary>A4 装配成功记账（星徽账本：装上即本局恒绑定该槽位角色，识别漏读由账本兜底
    /// ——2026-09-03 用户拍板，组装器把账本与识别取并集）。</summary>
    public void RecordBadgeEquippedAtSlot(string slotKey) =>
        stateHolder.RecordBadgeEquippedAtSlot(slotKey);

    /// <summary>当前星徽账本按名携带者（S1A 卖人过滤用：账本明知是携带者的角色绝不入卖人候选）。</summary>
    public IReadOnlySet<string> PeekBadgeCarrierNames() =>
        stateHolder.PeekBadgeLedger().CarrierNames;

    /// <summary>挂起槽位提升为按名记账（P1-A，2026-09-09：A4 幂等预查的名字佐证提升；
    /// 解析既有账而非新记账——同一枚徽不产生第二条账目）。</summary>
    public void PromoteBadgeCarrier(string slotKey, string characterName) =>
        stateHolder.PromoteBadgeCarrier(slotKey, characterName);

    /// <summary>星徽账本挂起槽位键（front:{n}，1.2.109 审查 P2-1：对账卖出须排除——
    /// 占用者识别不可见时挂起槽位无法提升名字，可能正是星徽载体）。</summary>
    public IReadOnlySet<string> PeekBadgePendingSlotKeys() =>
        stateHolder.PeekBadgeLedger().PendingSlots.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>读单个前台槽位实际占用人名（1.2.109 台账对账卖出的双源确认）。</summary>
    public async Task<string?> PeekFrontSlotCharacterAsync(
        nint windowHandle, int frontSlot, string expectedPreparationPageId, CancellationToken ct) =>
        await preparationBoard.ReadFrontSlotCharacterAsync(
            windowHandle, frontSlot, expectedPreparationPageId, ct);

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

    /// <summary>1.2.90：最近一次商店 Pass 中执行器自动上场的单位→前台槽位（0 基）。
    /// 决策层上场台账据此登记（M5 腿）——只记前台（后台兜底上场仅在 5 人口场景，台账
    /// 为前台模型不记）；上场失败/昔涟不在此列。</summary>
    public IReadOnlyDictionary<string, int> LastShopPassDeployedFrontSlots { get; private set; } =
        new Dictionary<string, int>();

    /// <summary>最近一次商店 pass 结束时的本地记账金币（1.2.24 修上报口径：持有器缓存滞后于刷新扣款）。</summary>
    public int LastShopPassGold { get; private set; } = -1;

    /// <summary>LastShopPassGold 的落账时刻（P2-E，2026-09-09：播种延续账的时效判据——
    /// 持有器金币读数比它新=识别已在备战页刷新余额，本地账让位）。</summary>
    private DateTimeOffset LastShopPassGoldAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>上一条 M5 金币本地账是否被判坏账（P2-E：CostUnknown 后本地账作废，
    /// 绝不让坏账播种下一条指令的余额）。</summary>
    private bool goldAccountBrokenLastPass { get; set; }

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
        // 1.2.105（15:13 局实锤，12:00 事故第二次重演的根因）：条件追加分支此前不受
        // AllowGalaxyScholarPurchase 节点门约束——1-3 场上已有黑塔+艾丝妲时，裸 M5
        // 白名单自动追加真理医生买入并上场（门只接了同帧双学者分支）。学者购买仅 1-1：
        // 门关闭时条件分支同样禁用。
        if (AllowGalaxyScholarPurchase
            && galaxyScholars.Count(name => owned.Contains(name)) >= 1)
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
        var galaxyScholars = AllowGalaxyScholarPurchase
            ? new HashSet<string>(
                GetBondMemberNames("银河学者"), StringComparer.OrdinalIgnoreCase)
            : []; // 1.2.102：仅 1-1 允许学者购买（引擎节点门）
        var extraTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boughtNames = new List<string>();
        var boughtAny = false;
        // 1.2.90 台账回带：本 Pass 执行器自动上场的单位→前台槽位（决策层台账登记用）。
        var passDeployedFrontSlots = new Dictionary<string, int>(StringComparer.Ordinal);
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
            // 坑38 批次补（实测 2026-09-03）：默认路径此前不写持久账本，识别漏名时
            // 圣杯循环的已购并集会缺失该角色 → 同名成员被重买。两本账与圣杯循环路径同口径。
            _grailPurchaseLedger.Add(pass.BoughtCharacterName);
            stateHolder.RecordPurchased(pass.BoughtCharacterName);

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
                        if (lane == PreparationLane.Front)
                        {
                            passDeployedFrontSlots[pass.BoughtCharacterName] = slot; // 1.2.90 台账回带
                        }
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
        LastShopPassDeployedFrontSlots = passDeployedFrontSlots;
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
        // 1.2.90 台账回带：本循环执行器自动上场的单位→前台槽位（决策层台账登记用）。
        var passDeployedFrontSlotsN14 = new Dictionary<string, int>(StringComparer.Ordinal);
        // 金币本地账（2026-09-03 用户拍板：刷到金币不足刷新为止，无保留线）：
        // 入口守卫已保证金币读数非空；买成/刷新/买经验各扣实价。
        // P2-E（2026-09-09 修复批）：播种改"本地账延续优先"——持有器金币只在备战页
        // 识别更新，商店页刷新/购买扣款不回写；连续 M5（1-3 逛店）若每次都从持有器
        // 播种，第二条指令会拿陈旧高余额继续挥霍（下午批实锤）。本地账仅在持有器读数
        // 不比它新时延续（回备战页后识别刷新金币=持有器读数更新，自动交还主导权）；
        // 账目曾被判定坏账（CostUnknown）时丢弃本地账。
        var holderGold = stateHolder.PeekGold();
        var gold =
            LastShopPassGold >= 0
            && !goldAccountBrokenLastPass
            && (holderGold.CapturedAt is not { } holderAt || LastShopPassGoldAt >= holderAt)
                ? LastShopPassGold
                : holderGold.Value ?? 0;
        var goldAccountBroken = false;
        // 成员账（含星徽携带者，与快照 BondMemberCount 同口径）+升 5 标记。
        var bondMembers = snapshot.BondMemberCount;
        var xpBought = false;
        var boughtNames = new List<string>();
        var boughtAny = false;
        var shopOpen = false;
        var refreshes = 0;
        var readFailures = 0;
        var lastShelfSignature = string.Empty; // P-07（1.2.69）刷新失效检测
        // 1.2.119（审计簇 F1/F3）：换点位重试与降级重扫的"有界一次"标志。
        var refreshOffsetRetried = false;
        var degradedRescanUsed = false;
        var staleShelfRounds = 0;
        // P-16（1.2.70）：购买决策留痕——聚合变量，循环结束发一条 GrailShopLoopSummary。
        string? endReason = null;
        var skippedOwnedTotal = new List<string>();
        var skippedNotTargetTotal = new List<string>();
        var skippedUnaffordableTotal = new List<string>();
        var deployFailures = 0;
        var iteration = 0;
        for (;
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

            skippedOwnedTotal.AddRange(pass.SkippedOwnedNames ?? []);
            skippedNotTargetTotal.AddRange(pass.SkippedNotTargetNames ?? []);
            skippedUnaffordableTotal.AddRange(pass.SkippedUnaffordableNames ?? []);

            if (!pass.ShopRead)
            {
                // 刷新动画未落定时读货架会失败：商店仍开着，重试而非提前收摊
                //（1.2.22 实测"刷两次就关店"的诱因之一）。
                readFailures++;
                if (readFailures >= 2 || !shopOpen)
                {
                    endReason = "ShopReadFailed";
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
                    endReason = "PurchaseUncertain";
                    rewardStage.PublishGrailTelemetry(
                        "GrailShopPurchaseUncertain",
                        $"{pass.BoughtCharacterName} 购买验证超时（可能已买）——本地防重买，收摊交决策层复核。",
                        TaskEventLevel.Warning);
                    break;
                }

                boughtAny = true;
                owned.Add(pass.BoughtCharacterName);
                _grailPurchaseLedger.Add(pass.BoughtCharacterName); // 记账：跨指令防重买
                stateHolder.RecordPurchased(pass.BoughtCharacterName); // 持久已购（跨识别帧去重，1.2.31）
                boughtNames.Add(pass.BoughtCharacterName);
                // P2-E（2026-09-09 修复批）：官方数据查无费用（识别误名/数据缺口）时
                // 金币账无法延续——按 0 扣账会虚高余额继续挥霍（下午批实锤 G06）。
                // 处置=立即收摊（关店停循环，角色留板凳交决策层部署/复核），本地账
                // 标记坏账不再延续到下一条 M5，余额交决策层 I10 对账。
                var boughtCost = TryGetCharacterCost(pass.BoughtCharacterName);
                if (boughtCost is not { } knownCost)
                {
                    goldAccountBroken = true;
                    endReason = "CostUnknown";
                    rewardStage.PublishGrailTelemetry(
                        "GrailShopCostUnknown",
                        $"{pass.BoughtCharacterName} 官方数据查无费用——金币账无法延续，立即收摊（角色留板凳交决策层复核）。",
                        TaskEventLevel.Warning);
                    break;
                }

                gold = Math.Max(0, gold - knownCost);
                rewardStage.PublishGrailTelemetry(
                    "GrailShopBought",
                    $"已购买 {pass.BoughtCharacterName}，扣费后金={gold}。");
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
                var deployOutcome = await DeployBoughtToRealEmptySlotAsync(
                    windowHandle,
                    pass.BoughtCharacterName,
                    expectedPreparationPageId,
                    occupiedFront,
                    occupiedBack,
                    cancellationToken);
                if (deployOutcome.Deployed)
                {
                    bondMembers++; // 白名单非昔涟买到并上场=命杯成员+1（星徽携带者同口径计入）
                    if (deployOutcome.FrontSlot is { } deployedFrontSlot)
                    {
                        passDeployedFrontSlotsN14[pass.BoughtCharacterName] = deployedFrontSlot; // 1.2.90 台账回带
                    }
                }
                else
                {
                    // P-16（1.2.70）：钱花了没上场的留痕（无空位/验证未见卡）。
                    deployFailures++;
                    rewardStage.PublishGrailTelemetry(
                        "GrailShopDeploySkipped",
                        $"{pass.BoughtCharacterName} 已购买但未能上场（无空位或验证未见卡）——待决策层 I10 对账。",
                        TaskEventLevel.Warning);
                }

                if (!HasMoreShopTargetsOnShelf(pass.ShopCharacterNames, purchaseNames, owned))
                {
                    endReason = "ShelfTargetsExhausted";
                    break;
                }

                continue; // 同帧还有目标：重开（不刷新）直接买下一个
            }

            if (pass.PurchaseCheck == RewardStageAutomationController.GrailShopPurchaseCheck.NotPurchased)
            {
                endReason = "PurchaseNotConfirmed";
                rewardStage.PublishGrailTelemetry(
                    "GrailShopPurchaseNotPurchased",
                    $"{pass.BoughtCharacterName} 点击后未确认购买成功（金币不足/点击无效）——停止本店购买。",
                    TaskEventLevel.Warning);
                break; // 金币不足/点击无效：停止本店购买（与 Shop.cs 批量路径语义一致）
            }

            // P-07（1.2.69）：刷新失效防护——点了刷新但货架全名单没变（刷新被游戏
            // 拒绝/异常吞掉），继续盲目刷新只会白烧金币。到达此处=本轮无购买：
            // 连续 2 轮货架签名（排序后全名单）与上轮相同 → 停止本店循环，
            // 剩余判定交决策层。刷新生效时 5 槽随机角色全同的概率≈0，无误伤。
            var shelfSignature = string.Concat(
                (pass.ShopCharacterNames ?? []).Order(StringComparer.Ordinal));
            staleShelfRounds = shelfSignature == lastShelfSignature
                ? staleShelfRounds + 1
                : 0;
            lastShelfSignature = shelfSignature;

            // 1.2.119（审计簇 F3）：货架识别降级（槽数<5=有未识别槽）——静置 1.2s
            // 重扫一次（1.2.108 弹店重扫先例推广），有界一次防循环；降级段不刷新
            //（省金币）。重扫仍降级→照常走刷新/判定流程。
            if ((pass.ShopCharacterNames?.Count ?? 0) < 5 && !degradedRescanUsed)
            {
                degradedRescanUsed = true;
                rewardStage.PublishGrailTelemetry(
                    "GrailShopDegradedRescan",
                    "货架识别降级（槽数<5）——静置 1.2s 后重扫一次（有界，不刷新）。",
                    TaskEventLevel.Warning);
                await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
                continue;
            }

            // 1.2.119（审计簇 F1，1-2/2-3）：首次货架签名不变=刷新点击未生效——
            // 换点位(−20,+12)重试一次刷新后重新识别（continue），不再连点同点位
            // 空烧金币；重试后仍不变→落下方 ShelfStale 判停（有界）。
            if (staleShelfRounds == 1 && !refreshOffsetRetried)
            {
                refreshOffsetRetried = true;
                if (await rewardStage.RetryShopRefreshWithOffsetAsync(
                        windowHandle,
                        cancellationToken))
                {
                    continue;
                }
            }

            if (staleShelfRounds >= 2)
            {
                endReason = "ShelfSignatureStale";
                rewardStage.PublishGrailTelemetry(
                    "GrailShopShelfStale",
                    "连续 2 轮无购买且货架全名单未变化——判定刷新失效，停止本店循环（P-07）。",
                    TaskEventLevel.Warning);
                break;
            }

            // 金币利用最大化（2026-09-03 用户最终拍板，商店控件内置逻辑，非决策层）：
            // ①命杯成员+星徽携带者=4（人口已到 1-3 上限）：金币>10 → 立即买经验升 5 人口
            //   继续刷第 5 人；金币≤10 → 金币已尽，直接停止刷新收摊（试炼判定/山穷水尽归决策层）；
            // ②成员<4（或已升 5 人口）：一直刷新到金币不足刷新价为止（无保留线）。
            // 1.2.119（审查 P2-3）：引擎部署段买经验（人口解锁）后闩锁置位——
            // 此处再买=同一局双扣 16 金，闩锁互斥。
            if (bondMembers >= FourMemberBondCap && !xpBought && !stateHolder.XpBoughtThisRun)
            {
                if (gold <= XpPushGoldThreshold
                    || !await ExecuteBuyXpAsync(windowHandle, cancellationToken))
                {
                    endReason = gold <= XpPushGoldThreshold ? "GoldBelowXpCost" : "XpPurchaseFailed";
                    break;
                }

                xpBought = true;
                stateHolder.MarkXpBoughtThisRun();
                gold = Math.Max(0, gold - (GrailRunSnapshot.XpPurchaseGoldCost + (stateHolder.PeekXpSurcharge() ? 1 : 0)));
                continue; // 人口已到 5：继续商店循环刷第 5 个成员/昔涟
            }

            var refreshCost = RewardStageAutomationController.ShopRefreshGoldCost
                + (stateHolder.PeekRefreshSurcharge() ? 1 : 0);
            if (refreshes >= MaxShopRefreshesPerCommand || gold < refreshCost)
            {
                endReason = refreshes >= MaxShopRefreshesPerCommand ? "RefreshCap" : "GoldBelowRefreshCost";
                break; // 金币不足刷新价（或本条指令刷新上限）：收摊，最终判定归决策层
            }

            if (!await rewardStage.RefreshShopOnceAsync(windowHandle, cancellationToken))
            {
                endReason = "RefreshInputFailed";
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

        // 09-08 通宵 P1 兜底扫描（23/33 局"携带者坐板凳"教训）：M5 收摊后复核星徽
        // 携带者账本——携带者若在板凳=立即重部署到真空位（8.2 清单第 9 条不变量）。
        // 昔涟除外（买而不上另有 L 系流程管）。失败不阻断（有保护兜底不卖）。
        try
        {
            var benchAfterClose = await preparationBoard.ReadStableBenchCharactersAsync(
                windowHandle, expectedPreparationPageId, cancellationToken);
            var carriers = stateHolder.PeekBadgeLedger().CarrierNames;
            var carrierBench = benchAfterClose?.FirstOrDefault(item =>
                carriers.Contains(item.Character.Name) &&
                !string.Equals(item.Character.Name, GrailRunSnapshot.XilianName, StringComparison.Ordinal));
            if (carrierBench is not null)
            {
                PublishTelemetry(
                    "GrailBadgeCarrierRedeploy",
                    $"星徽携带者「{carrierBench.Character.Name}」在板凳——重部署到真空位（8.2-9 不变量）。",
                    TaskEventLevel.Warning);
                await DeployBoughtToRealEmptySlotAsync(
                    windowHandle, carrierBench.Character.Name, expectedPreparationPageId,
                    occupiedFront, occupiedBack, cancellationToken,
                    isDisplacedRedeploy: true);
            }
        }
        catch (Exception carrierScanError)
        {
            PublishTelemetry(
                "GrailBadgeCarrierRedeployScanFailed",
                $"携带者重部署扫描失败（不阻断）: {carrierScanError.Message}",
                TaskEventLevel.Warning);
        }

        // P-16（1.2.70）：单条 M5 一条终态汇总——为什么停、买到谁、跳过谁，复盘不再拼凑。
        endReason ??= "IterationCap";
        var ownedSkipDistinct = skippedOwnedTotal.Distinct(StringComparer.Ordinal).ToArray();
        var notTargetDistinct = skippedNotTargetTotal.Distinct(StringComparer.Ordinal).ToArray();
        var unaffordableDistinct = skippedUnaffordableTotal.Distinct(StringComparer.Ordinal).ToArray();
        rewardStage.PublishGrailTelemetry(
            "GrailShopLoopSummary",
            $"买=[{string.Join(",", boughtNames)}] 刷={refreshes} 轮={iteration} 终态金={gold} " +
            $"结束原因={endReason}" +
            (ownedSkipDistinct.Length > 0 ? $"；跳过已拥有×{ownedSkipDistinct.Length}" : string.Empty) +
            (notTargetDistinct.Length > 0 ? $"；非目标×{notTargetDistinct.Length}" : string.Empty) +
            (unaffordableDistinct.Length > 0
                ? $"；金币不足跳过=[{string.Join(",", unaffordableDistinct)}]"
                : string.Empty) +
            (deployFailures > 0 ? $"；上场失败×{deployFailures}" : string.Empty) + "。");

        LastShopPassBoughtNames = boughtNames;
        LastShopPassDeployedFrontSlots = passDeployedFrontSlotsN14;
        LastShopPassGold = goldAccountBroken ? -1 : gold; // 实时本地账（1.2.24：持有器缓存滞后，回执须报刷新后的真实余额）；P2-E：坏账作废不播种下一条指令
        LastShopPassGoldAt = goldAccountBroken ? DateTimeOffset.MinValue : DateTimeOffset.Now;
        goldAccountBrokenLastPass = goldAccountBroken;
        return boughtAny;
    }

    /// <summary>
    /// 上场到真实空槽（修坑#4 同款"计数器与盘面脱钩"风险）：前台 4 槽优先、后台 6 槽兜底；
    /// 占用集合=指令前置 I10 快照（识别保守口径，Uncertain 占位槽也算占用）+本轮已部署。
    /// 无空位=诚实返回不拖拽（拖到有人=互换；第 5 人须先买经验升人口——N17 归决策层）。
    /// </summary>
    /// <summary>返回（是否上场成功, 前台槽位——仅前台上场时有值；后台兜底上场返回 null，
    /// 台账为前台模型不记后台）。</summary>
    private async Task<(bool Deployed, int? FrontSlot)> DeployBoughtToRealEmptySlotAsync(
        nint windowHandle,
        string boughtName,
        string expectedPreparationPageId,
        HashSet<int> occupiedFront,
        HashSet<int> occupiedBack,
        CancellationToken cancellationToken,
        bool isDisplacedRedeploy = false)
    {
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            windowHandle, expectedPreparationPageId, cancellationToken);
        var bought = bench?.FirstOrDefault(item =>
            string.Equals(item.Character.Name, boughtName, StringComparison.OrdinalIgnoreCase));
        if (bought is null)
        {
            // P1-4（1.2.75 实测根因修复）：买后卡飞向备战席的动画期首读常见"未见卡"——
            // 03:37 命中局远坂凛买而未上、前置门判 Dead 弃掉好局实锤。等待 2.5 秒
            // 重读一次（X13 精神），仍未见卡才如实 false。
            await Task.Delay(TimeSpan.FromMilliseconds(2500), cancellationToken);
            bench = await preparationBoard.ReadStableBenchCharactersAsync(
                windowHandle, expectedPreparationPageId, cancellationToken);
            bought = bench?.FirstOrDefault(item =>
                string.Equals(item.Character.Name, boughtName, StringComparison.OrdinalIgnoreCase));
            if (bought is null)
            {
                PublishTelemetry(
                    "GrailShopDeploySkipped",
                    $"{boughtName} 已购买但备战席两读均未见卡（动画期/识别滞后）——交决策层 I10 对账。",
                    TaskEventLevel.Warning);
                return (false, null); // 验证过仍未见卡：交决策层 I10 复核，绝不拖旧坐标
            }
        }

        // 1.2.109（19:44 局实弹：三条独立 M5 各自的占用快照在商店动画期取，场上
        // 已部署成员读不出→每次都判 4 号位空闲→凛/闪/Saber 连续互换挤压，羁绊恒 1）：
        // 空槽判定改为部署时实时读前后台槽区（与卖人验证同款槽区识别原语，全天可靠），
        // 陈旧占用表只作兜底对照；前台满则后台，全满诚实跳过（绝不互换挤人）。
        // 09-08 通宵 P1（用户截图+拖拽日志铁证）：识别流停滞期实时读会拿到陈旧帧
        //（旧槽位被读成空）→拖上被占槽=游戏判定交换→已上场成员(常为星徽携带者)
        // 被换下板凳永不归位。修法：①实时读失败→隔 800ms 重读一次,仍失败=诚实跳过
        // 本次部署（卡留板凳,交对账/下轮处理,绝不盲拖）；②部署后复核板凳,检出交换
        // 立即把被换下成员重部署到真空位（见下方 swap 检测块）。
        var lane = PreparationLane.Front;
        int? slot = null;
        var liveOccupied = await preparationBoard.ReadLiveSlotOccupancyAsync(
            windowHandle, expectedPreparationPageId, cancellationToken);
        if (liveOccupied is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
            liveOccupied = await preparationBoard.ReadLiveSlotOccupancyAsync(
                windowHandle, expectedPreparationPageId, cancellationToken);
        }

        if (liveOccupied is null)
        {
            PublishTelemetry(
                "GrailShopDeploySkipped",
                $"{boughtName} 购买后槽位占用两次实时读均失败（识别流停滞）——诚实跳过本次上场，" +
                "卡留板凳交对账，绝不盲拖 risking 交换。",
                TaskEventLevel.Warning);
            return (false, null);
        }

        var occupiedFrontLive = new HashSet<int>(occupiedFront.Concat(liveOccupied.Value.Front));
        var occupiedBackLive = new HashSet<int>(occupiedBack.Concat(liveOccupied.Value.Back));
        for (var i = 0; i < FrontSlotCapacity; i++)
        {
            if (!occupiedFrontLive.Contains(i))
            {
                slot = i;
                break;
            }
        }

        if (slot is null)
        {
            lane = PreparationLane.Back;
            for (var i = 0; i < BackSlotCapacity; i++)
            {
                if (!occupiedBackLive.Contains(i))
                {
                    slot = i;
                    break;
                }
            }
        }

        if (slot is null)
        {
            return (false, null); // 无空位：不拖不互换，决策层按 N17 处理人口
        }

        if (await preparationBoard.GrailDeployBenchCharacterAsync(
                windowHandle, bought, lane, slot.Value, expectedPreparationPageId, cancellationToken))
        {
            // 1.2.109 审查 P2-2：按 lane 回落登记到正确的陈旧集合。
            (lane == PreparationLane.Front ? occupiedFront : occupiedBack).Add(slot.Value);

            // 09-08 通宵 P1 交换检测：部署后复核板凳——若出现预拖清单之外的新卡
            //（=拖上了被占槽发生交换,被换下的在场成员——常为星徽携带者——落板凳），
            // 立即重部署到真空位（一层,防递归）。用户截图冻结局即此形态。
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
            var postBench = await preparationBoard.ReadStableBenchCharactersAsync(
                windowHandle, expectedPreparationPageId, cancellationToken);
            var preBenchNames = (bench ?? Array.Empty<RecognizedBenchCharacter>())
                .Select(item => item.Character.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var displaced = postBench?.FirstOrDefault(item =>
                !preBenchNames.Contains(item.Character.Name) &&
                !string.Equals(item.Character.Name, boughtName, StringComparison.OrdinalIgnoreCase));
            if (displaced is not null && !isDisplacedRedeploy)
            {
                PublishTelemetry(
                    "GrailShopDeploySwapDetected",
                    $"部署 {boughtName} 后检出交换：板凳出现新卡「{displaced.Character.Name}」" +
                    "（被换下的在场成员，常为星徽携带者）——立即重部署到真空位。",
                    TaskEventLevel.Warning);
                await DeployBoughtToRealEmptySlotAsync(
                    windowHandle, displaced.Character.Name, expectedPreparationPageId,
                    occupiedFront, occupiedBack, cancellationToken,
                    isDisplacedRedeploy: true);
            }

            return (true, lane == PreparationLane.Front ? slot.Value : null);
        }

        return (false, null);
    }

    /// <summary>官方数据角色费用（P2-E 改靶 2026-09-09：查无角色或费用集空=null——
    /// null 不再伪装成 0 费，调用方收摊交对账；0 是"免费"不是"未知"）。</summary>
    private int? TryGetCharacterCost(string name) =>
        gameData.CurrencyWarsCharacters
            .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Costs ?? Array.Empty<int>()).DefaultIfEmpty(0).Min())
            .Cast<int?>()
            .FirstOrDefault(cost => cost is > 0);

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
        // P1 审查修复：账本按名携带者绝不入卖人候选——备战席识别不含装备明细，
        // 只按非成员/非5费过滤会漏掉"换下场的星徽携带者"（坑34 同款风险）。
        var badgeCarriers = PeekBadgeCarrierNames();
        var sellable = bench
            .Where(item => !item.Character.BondNames.Any(
                bond => bond is not null && bond.Contains("命运圣杯", StringComparison.Ordinal))
                && !IsPureFiveCost(item.Character)
                && !badgeCarriers.Contains(item.Character.Name))
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

