using System;
using System.Collections.Generic;
using System.Linq;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」决策树主链的运行引擎（纯逻辑，可独立单测；识别/点屏由上层注入）。
/// <para>
/// 对应决策树（docs/决策树_3star5cost.mmd + DECISION_TREE_1-3三星五费_自动刷_logic_review.html）主链节点：
/// <list type="bullet">
///   <item>D1 环境可推进（067/019 任一存在，否则重刷；018 契约已剔除）</item>
///   <item>D2 血量节点①（选策略前 hpInvest≥88）→ D21 终点线分叉（奇迹代偿 vs 无限之釜）</item>
///   <item>X1/X2 奇迹代偿线 hp&lt;88 时必须刷「二极管276」补到≥88，否则重刷</item>
///   <item>G BUY 买商店 1/2/3 费圣杯成员组件（同一角色只购一次）</item>
///   <item>D3 凑齐 4 圣杯（成员+星徽/圣杯转），否则重刷</item>
///   <item>D4/J1/J2 奇迹代偿线必须已有 5 费本体在池，否则重刷；无限之釜线跳过本体要求</item>
///   <item>K/D5 血量节点②：祈愿试炼浮现「奇迹代偿(扣88)」时，hp≥88 才可点，否则改选另一试炼</item>
///   <item>L/M1/M2 一般试炼：第 1/2 个诅咒都没想要的 → 默认选左（不重刷）</item>
///   <item>H2/N1/N2/C2 选奇迹代偿→扣88+全金币→8投影；选不了且另一也非目标→重刷</item>
///   <item>P 达成终点（无限之釜→已三星 Archer；奇迹代偿→8投影+本体在池）→ 交用户手动拖 8 投影</item>
///   <item>Q/D7 用户目标分叉（A 只要1个 / B 全员）→ 收工</item>
/// </list>
/// <b>重要分层</b>：识别（读环境名/id、读血量、读金币、读试炼文本、读是否已 8 投影/本体）属于上层；
/// 本引擎只接收这些<b>事实输入</b>并产出<b>决策与动作</b>，因此可脱离游戏做完整单测。
/// 所有"已持有/是否凑齐/是否拿到"均为输入事实，引擎不臆造识别结果。
/// </para>
/// </summary>
public static class FateGrailRunEngine
{
    /// <summary>用户目标（决策树 Q/D7）。</summary>
    public enum UserGoal
    {
        /// <summary>A：只要 1 个三星 5 费。</summary>
        AnyOne,
        /// <summary>B：全员三星 5 费。</summary>
        All,
    }

    /// <summary>本局达成的终点线（决策树 D21 / D4 / P）。</summary>
    public enum EndLine
    {
        /// <summary>奇迹代偿：8 完美投影仪 + 已有 5 费本体在池（需 hp≥88）。</summary>
        MiracleCompensation,
        /// <summary>无限之釜：直接给三星 Archer（不需 88 血、不需已有本体）。</summary>
        InfernalCauldron,
        /// <summary>尚不确定（D21 决策前）。</summary>
        Undetermined,
    }

    /// <summary>步骤要执行的动作（识别/判定结果之外的行动指令）。</summary>
    public enum Action
    {
        /// <summary>无动作（仅判定推进）。</summary>
        None,
        /// <summary>买「二极管276」策略补血。</summary>
        BuyDiode,
        /// <summary>买商店 1/2/3 费圣杯成员（远坂凛/吉尔伽美什/Saber，同角色只购一次）。</summary>
        BuyStoreMembers,
        /// <summary>选用「采购专员」(051/238) 抬缺费角色刷出概率。</summary>
        PickPurchaseSpecialist,
        /// <summary>选用「命运圣杯星徽」策略补第 5 圣杯（321 无副作用优先 > 333）。</summary>
        PickStarBadge,
        /// <summary>祈愿试炼择一侧（一般试炼，默认左）。</summary>
        ChoosePassiveTrial,
        /// <summary>选「奇迹代偿」试炼（扣88血+全金币→8完美投影仪）。</summary>
        ChooseMiracleCompensation,
        /// <summary>确保 ≥1 个 5 费本体在池（登场Archer/聘书/英雄登场）。</summary>
        Ensure5CostBody,
        /// <summary>购买经验升人口（点商店等级识别框内任意点两次，8 金币）。用户 2026-08-26：4命杯+1星徽齐且人口<5 时触发，激活5圣杯。</summary>
        BuyPopulation,
        /// <summary>本局判成/收工（交用户手动拖 8 投影）。</summary>
        Achieved,
        /// <summary>本局判不成，重刷。</summary>
        Reroll,
    }

    /// <summary>单步运行输出：当前节点、动作、是否收工/重刷、下一节点。</summary>
    public sealed record FateGrailStep(
        string Node,           // 决策树节点标签（D1/D2/... / P / EAA / EAB），供审核逐点对照
        Action Action,
        string Message,
        bool Done,
        bool RequiresReroll,
        bool RequiresUserManual,
        string? NextNode = null,
        // 祈愿试炼选侧（缺口5修复）：ChoosePassiveTrial / ChooseMiracleCompensation
        // 要选哪一侧。Left=左、Right=右；null=无需点试炼（非试炼动作）。
        TrialSide? TrialToChoose = null,
        // 投资策略选取：PickPurchaseSpecialist / PickStarBadge 时带目标策略 id（238/051 或 321/333）。
        string? TargetInvestmentStrategyId = null)
    {
        public bool IsTerminal => Done || RequiresReroll;
    }

    /// <summary>祈愿试炼要选的侧。</summary>
    public enum TrialSide
    {
        Left,
        Right,
    }

    /// <summary>运行输入快照（一次 Step 所需的所有决策树事实）。</summary>
    public sealed record Snapshot(
        string? EnvironmentId,           // 投资环境 id（investment_environment_067/019/...，null=未识别）
        int Hp,                          // 当前血量
        int Gold,                        // 当前金币
        IReadOnlySet<string> OwnedMembers,   // 已持有命杯成员（远坂凛/吉尔伽美什/Saber/Archer 与其它）
        IReadOnlySet<string> AvailableStrategyIds, // 本局可选投资策略 id 全集
        UserGoal Goal,                   // 用户目标
        EndLine Line,                    // 本次已确定的终点线（D21 前可传 Undetermined）
        bool HasBody5Cost = false,       // 是否有 ≥1 个 5 费本体在池（前台/后台/备战席任一识别到 5 费角色即真，1 星即可）
        bool HasEightProjectors = false, // (已废弃) 选「奇迹代偿」即保证 8 完美投影仪，不再需要识别确认；保留字段兼容上层
        bool DiodeTaken = false,         // 是否已吃过二极管 +10
        string? LeftTrial = null,        // 当前祈愿档左侧试炼文本
        string? RightTrial = null,       // 当前祈愿档右侧试炼文本
        bool TrialOneTaken = false,      // 试炼计数①：第 1 个祈愿试炼（诅咒档①）已选过（内部状态，选过记 1，非现场识别）
        bool TrialTwoTaken = false,      // 试炼计数②：第 2 个祈愿试炼（诅咒档②）已选过（内部状态，选过记 1，非现场识别）
        bool HasStarBadge = false,      // 是否已持有「命运圣杯星徽」（第4/5圣杯档触发件）
        bool BodyAcquisitionFailed = false,  // J2：确认本体后仍失败/金币花完仍无本体 -> 重刷
        int BondTier = 0,               // 当前祈愿档位（2/3/4/5按上羁绊逐档推进；0=未知）
        IReadOnlySet<string>? FiveCostBodyIds = null,  // 识别到的 5 费本体角色名集合（前台/后台/备战席；含昔涟则全员达成）
        int Population = 0,              // 当前人口上限（备战页识别；0=未知/未识别；激活5圣杯需≥5）
        string? EnvGifted5Cost = null,   // SA1：067 英雄登场环境随机赠送的 2 星 5 费角色名（识别层填；null=未识别/非 067）
                                         // 语义：赠送的 5 费默认放备战席最左、19 节点后才能上场——只能作本体/采购刷子，
                                         // 不激活羁绊。若送的是 Archer，需再补一张「能上场」的 Archer 才能凑 4 圣杯。
        IReadOnlySet<string>? OwnedInvestmentStrategyIds = null); // 已持有（已选上）的投资策略 id 集合；null=尚未选任何策略

    /// <summary>
    /// 整局内部状态（非现场识别，由协调器/整局循环在执行动作后自增并随次传给
    /// <see cref="FateGrailSnapshotAssembler.Assemble"/> 写进每次快照）。
    /// 用户 2026-08-25：试炼计数/二极管/本体失败为软件内部自增，不现场识别。
    /// </summary>
    public sealed record InternalState(
        bool DiodeTaken = false,              // 是否已吃过「二极管276」+10
        bool TrialOneTaken = false,           // 试炼计数①：第 1 个祈愿试炼已选过（选过记 1）
        bool TrialTwoTaken = false,           // 试炼计数②：第 2 个祈愿试炼已选过（选过记 1）
        bool HasStarBadge = false,            // 是否已持有「命运圣杯星徽」
        bool BodyAcquisitionFailed = false);

    // 环境 id（与 data/4.4/investment-environments.json 对齐）
    public const string EnvironmentHeroArrival = "investment_environment_067";
    public const string EnvironmentContract = "investment_environment_018"; // (已剔除 2026-08-26)：018 契约最多 3 人口，摸不到最终圣杯试炼门槛，不可能 1-3 三星五费。
    public const string EnvironmentInvitation = "investment_environment_019";

    // 投资策略 id（与 data/4.4/investment-strategies.json 对齐；用户 2026-08-26 拍板全元素进决策）
    /// <summary>采购专员·彩（每 5 刷，更频繁，优先）。</summary>
    public const string PurchaseSpecialistColor = InvestmentStrategyPicker.PurchaseSpecialistColor;
    /// <summary>采购专员·金（每 7 刷，兜底）。</summary>
    public const string PurchaseSpecialistGold = InvestmentStrategyPicker.PurchaseSpecialistGold;
    /// <summary>二极管（+10 血/上限、+6 金）。奇迹代偿需血≥88 时买。</summary>
    public const string DiodeInvestmentStrategyId = FateGrailHealthGate.DiodeInvestmentStrategyId;
    /// <summary>命运圣杯星徽（给 1 星徽 + 远坂凛，无副作用）。</summary>
    public const string StarBadgeStrategyId = "investment_strategy_321";
    /// <summary>都是它的错！（给 1 星徽 + 10 金；副作用：未完美通关且带星徽角色参战时可能卖带星徽上场角色×3）。</summary>
    public const string StarBadgeSideEffectStrategyId = "investment_strategy_333";

    // 祈愿试炼：两五费聘书（可自选 Archer/昔涟，本体的一个来源；3/4 圣杯档才开）。
    // 试炼文本可能是「五费聘用书」/「聘书」；"聘用书"不包含连续子串"聘书"，故统一匹配"聘用"。
    public const string TrialOfferKeyword = "聘用";

    /// <summary>1-3 三星五费目标 5 费核心角色（昔涟最核心：三星给金币续凑另一 5 费；见 Handoff 8-26 A/B 目标）。</summary>
    public const string Core5CostXilian = "昔涟";

    /// <summary>空投资策略集（Q0 读取 OwnedInvestmentStrategyIds 为 null 时的回退，避免空引用）。</summary>
    private static readonly IReadOnlySet<string> EmptyStrategy =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 判断开局投资环境是否可推进（D1）。可推进 = 命中 067/019 任一关键环境。
    /// <para>2026-08-26 用户裁决：<b>018 命运圣杯契约已剔除</b>——它白送 凛+闪（2 圣杯）
    /// 完成两次试炼后送 Archer，但 1-3 最多 3 人口，凑不到第 5 圣杯（星徽）的 5 人口门槛，
    /// 摸不到「直接给三星五费」的最终祈愿试炼，<b>不可能</b>在 1-3 达成三星五费。</para>
    /// <para>保留 067 英雄登场（送 2 星 5 费本体在池，不上场）与 019 命运圣杯邀请（送星徽=第5圣杯）。</para>
    /// </summary>
    public static bool IsEnvironmentViable(string? environmentId) =>
        string.Equals(environmentId, EnvironmentHeroArrival, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(environmentId, EnvironmentInvitation, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 依据一局输入快照推进<b>一步</b>决策树，返回该步所需动作与下一节点。
    /// <para>约定：调用方在进入某节点时给足该节点所需事实；本引擎按决策树判据给出动作并推进。</para>
    /// <para>2026-08-26 用户拍板：<b>全元素进决策</b>——投资环境(067/019)、投资策略(采购专员238/051、
    /// 二极管276、星徽321>333)、起源试炼(奇迹代偿/无限之釜/两五费聘书)都作为决策节点接进本树，
    /// 由决策层按本局情况按需选择（需要才选、不硬门槛），而非只被动用一两个元素。</para>
    /// </summary>
    public static FateGrailStep Step(Snapshot s)
    {
        // D1 环境可推进？
        if (!IsEnvironmentViable(s.EnvironmentId))
            return Reroll("D1", "开局环境不可推进（无 067 英雄登场 / 019 命运圣杯邀请 任一关键环境），重刷。");

        // SA1 英雄登场环境识别「随机送的 5 费是谁」：
        //   - 送 Archer：上不了台、不能激活 4 圣杯（只作本体复制目标/采购刷子）-> 需补一张「能上场」的 Archer。
        //   - 送昔涟：5 费本体走圣杯线（全员三星五费最核心）。
        //   - 送其它 5 费：只作本体复制目标。
        // 此判定收敛为 EnvGifted5Cost 单一字段；送 Archer 时 HasFourBond 需区分可上场 Archer（见下）。
        if (IsHeroArrival(s.EnvironmentId) && !string.IsNullOrEmpty(s.EnvGifted5Cost))
        {
            var gifted = s.EnvGifted5Cost;
            if (string.Equals(gifted, "Archer", StringComparison.OrdinalIgnoreCase))
            {
                // 067 送 Archer 上不了台：不能算 4 圣杯（见 HasFourBond），
                // 只在还需一张「能上场」Archer 时才需要采购专员刷出（Q0 投资策略节点处理）。
            }
            // 昔涟 / 其它 5 费：本体已在池（由五费本体识别收敛到 HasBody5Cost / FiveCostBodyIds），此处仅作记录。
            // 是否仍缺「能上场」的 Archer 用于凑 4 圣杯，交由 D3 HasFourBond 判定。
        }

        // 2026-08-26 用户裁决重构：去掉「奇迹代偿线 / 无限之釜线」的顶部硬分叉（EndLine）。
        // 根因：祈愿试炼每档随机 2 选 1，满 4 人口抽试炼时并不知道会出奇迹代偿还是无限之釜，
        // 顶部按 Goal 猜终点线（Goal=All 猜奇迹代偿 / AnyOne 猜无限之釜）是错误模型。
        // 正确模型：前置准备统一（不分线），抽到试炼后由 WickerLineTrialStep 统一响应
        // （是奇迹代偿(有效血≥88+本体)→N1 点选成功；是无限之釜→P 点选成功；否则择优/重刷）。
        // 前置准备对任何胜利试炼都通用——
        // D2 血量节点①：有效血<88 且走「奇迹代偿」时才需买二极管278（见 WickerLineTrialStep 内 N2/J1 处理）；
        //              走「无限之釜」不需 88 血。此处不预分线、不因血量重刷，由试炼响应决定。

        // G 买商店命杯成员（缺则买；命杯成员要越多越好，祈愿弹框已适配）。
        var missingStore = FateGrailShoppingPolicy.StorePurchaseNames
            .FirstOrDefault(n => !s.OwnedMembers.Contains(n));
        if (missingStore is not null)
        {
            return new FateGrailStep("G", Action.BuyStoreMembers,
                $"缺商店命杯成员 [{missingStore}]，先购买（远坂凛/吉尔伽美什/Saber）。",
                Done: false, RequiresReroll: false, RequiresUserManual: false,
                NextNode: "G");
        }

        // Q0 投资策略决策节点（2026-08-26 用户拍板：全元素进决策，按需选择、不硬门槛）。
        // 对应新决策树 `INV -> N1 (Q0 本局需要哪些投资策略)`：
        //   - 都不需要(目标本体已到手且血够且不需星徽) -> NONE 任意选一个走节点，不为挑策略重刷。
        //   - 需要采购专员238/051(目标5费未到手+有5费刷子 / 全员缺第二5费 / 067送Archer上不了台需补能上场Archer)
        //   - 需要星徽321/333(缺星徽且要上5圣杯档·全员)
        //   - 需要二极管276(走奇迹代偿且血<88)
        // Q1 刷到 -> 选之并推进；Q2 没刷到 -> 不立刻重刷，先推进看聘书/切换路径，走不通才重刷。
        // Q3 刷到但不兼容 -> 兼容/保留/切换，全不通才停。
        // 一次 Step 只推进一路：返回「选哪个策略」动作后由上层选定并写回 OwnedInvestmentStrategyIds，
        // 下一次 Step 再按 Q1 判定是否已持有。
        var ownedStrat = s.OwnedInvestmentStrategyIds ?? EmptyStrategy;
        var starAlreadyTaken =
            ownedStrat.Contains(StarBadgeStrategyId, StringComparer.OrdinalIgnoreCase) ||
            ownedStrat.Contains(StarBadgeSideEffectStrategyId, StringComparer.OrdinalIgnoreCase);
        var specAlreadyTaken =
            ownedStrat.Contains(PurchaseSpecialistColor, StringComparer.OrdinalIgnoreCase) ||
            ownedStrat.Contains(PurchaseSpecialistGold, StringComparer.OrdinalIgnoreCase);

        // ---- Q0 投资策略决策节点的入口守卫 ----
        // 投资策略选在「1-3 进入祈愿试炼抽取之前」。若当前已浮现胜利试炼（奇迹代偿/无限之釜），
        // 说明已进入 CORE 试炼统一响应阶段——此时 Q0 不再介入，直接交试炼响应（N1/P/聘书/J1）。
        // 否则才按 Q0 判断是否需提前选采购专员/星徽（二极管由试炼响应 X2 遇到奇迹代偿残血时买）。
        if (!HasVictoryTrial(s))
        {
            // ---- Q1P 采购专员线 ----
            if (NeedsPurchaseSpecialist(s))
            {
                // 已持有采购专员 -> 进 G_NEED 商店（本体靠它刷）。
                // 未持有 -> 可选项里有采购专员(Pick)才选；没有则 Q2 等聘书 / 切换 / 重刷。
                if (!specAlreadyTaken)
                {
                    var spec = InvestmentStrategyPicker.PickPurchaseSpecialist(s.AvailableStrategyIds);
                    if (spec is not null)
                    {
                        return new FateGrailStep("Q1P", Action.PickPurchaseSpecialist,
                            $"Q0 需要采购专员({spec}) 提升目标 5 费刷出率（目标本体未到手/缺能上场Archer/全员缺第二5费），选之。",
                            Done: false, RequiresReroll: false, RequiresUserManual: false,
                            NextNode: "G_NEED", TargetInvestmentStrategyId: spec);
                    }
                    // Q2P：没刷到采购专员 -> 不立刻重刷。靠三/四圣杯开出的「两五费聘书」补本体。
                    // （真实「是否开得出聘书」由后续祈愿试炼浮现判，本步只表达采购专员未得，放行进入 G_NEED，
                    //   不在此刻为采购专员未出而重刷。）
                }
                // specAlreadyTaken 或采购专员不可选但放行：落入 G_NEED。
            }

            // ---- Q1S 星徽线 ----
            if (NeedsStarBadge(s))
            {
                if (!starAlreadyTaken)
                {
                    var star = PickStarBadge(s.AvailableStrategyIds);
                    if (star is not null)
                    {
                        return new FateGrailStep("Q1S", Action.PickStarBadge,
                            $"Q0 需要星徽上 5 圣杯（全员目标），可选 [{star}]（321 无副作用优先 > 333），选之。",
                            Done: false, RequiresReroll: false, RequiresUserManual: false,
                            NextNode: "G_NEED", TargetInvestmentStrategyId: star);
                    }
                    // Q2S：没刷到星徽。单人 4 圣杯已够出胜利试炼 -> 放行；全员必须 5 圣杯，靠 019/试炼兜底。
                }
                // starTaken 或单人/已放过 -> 落入 G_NEED。
            }

            // ---- Q1D 二极管线（不提前买，仅在祈愿浮现奇迹代偿且血<88 的 X2 买）----
            // 有效血够 88 则无需二极管；血<88 但可补(38≤hp<88)且可选 276 时，由试炼响应 X2 抽到奇迹代偿时买。
            // 本 Q0 阶段不提前买（避免为探索性 8 抽白费金币）——2026-08-26 用户「不硬门槛」的体现。
        }
        // NONE / 已选齐：落入 G_NEED（商店盘点买圣杯成员）。

        // D3 凑 4 圣杯：任何胜利试炼（奇迹代偿/无限之釜）都需够档位；缺第 4 档触发件则胜利试炼永不浮现。
        // (2026-08-26 用户拍板) 不再「直接重刷」：先尝试 G_REF2 商店刷到底——若金币不足(<2)/连刷无新增
        // 才判缺触发件重刷；在此之前先继续刷商店看能否买到（第4档靠 Archer 能上场 / 星徽）。
        if (!HasFourBond(s))
        {
            if (CanStillAffordShop(s) || HasUnfinishedShopStock(s))
            {
                return new FateGrailStep("G_REF2", Action.BuyStoreMembers,
                    $"D3 缺第 4 档（需「能上场」Archer 或星徽），但商店仍可刷（金币={s.Gold}）：继续刷商店看能否买到 -> 不直接重刷。",
                    Done: false, RequiresReroll: false, RequiresUserManual: false,
                    NextNode: "G");
            }
            return Reroll("D3",
                "商店已刷到底仍缺第 4 档触发件（既无「能上场」Archer 也无星徽），胜利试炼不会出现 -> 重刷。");
        }

        // 统一响应试炼（奇迹代偿/无限之釜/择优/机会耗尽/买人口）。
        return WickerLineTrialStep(s);
    }
    private static FateGrailStep WickerLineTrialStep(Snapshot s)
    {
        // 用户 2026-08-25 澄清：祈愿试炼每档从当前级别任务池【随机】抽 2 个，
        // 不存在"第 5 档必定黑杯"。"漆黑之杯/诅咒·圣杯任务"只是最终试炼池里
        // 的一个普通条目（副作用型），抽到它不选即可；并非固定第 5 档产出。
        // 故此处不再有 BLACKCUP 死档；胜利判定只看"当前 2 选 1 是否出现
        // 无限之釜 / 奇迹代偿(血≥88+本体)"，任何档位出现即达成。

        // K/D5 血量节点②（通用）：祈愿试炼浮现「奇迹代偿(扣88)」时独立判定血量。
        if (TrialRequestsMiracleCompensation(s.LeftTrial, s.RightTrial))
        {
            var effectHp = s.Hp + (s.DiodeTaken ? FateGrailHealthGate.DiodeHealthBonus : 0);
            if (effectHp >= FateGrailHealthGate.HealthGateThreshold)
            {
                // 用户 2026-08-25 判定简化：血≥88 + 已识别奇迹代偿 + 有 5 费本体在池 -> 点选即成功。
                // 选中奇迹代偿=扣88+全金币→8完美投影仪，必定能拖成三星，无需再识别/确认"是否已有8投影"。
                if (!s.HasBody5Cost)
                {
                    // J2：本体获取已失败（登场Archer/聘书均不可得、金币花完）-> 无复制目标 -> 重刷。
                    if (s.BodyAcquisitionFailed)
                    {
                        return Reroll("J2",
                            "祈愿浮现「奇迹代偿」但 5 费本体获取失败（登场Archer/聘书均不可得），无复制目标 -> 重刷。");
                    }
                    return new FateGrailStep("J1", Action.Ensure5CostBody,
                        "祈愿浮现「奇迹代偿」但 5 费本体尚未在池，先确保 ≥1 个 5 费角色（前台/后台/备战席任一）再点。",
                        Done: false, RequiresReroll: false, RequiresUserManual: false,
                        NextNode: "J2");
                }

                var hasXilian = s.FiveCostBodyIds?.Contains(
                    "昔涟", StringComparer.OrdinalIgnoreCase) == true;
                var message = hasXilian
                    ? "奇迹代偿：5 费本体含昔涟，选中后全员三星五费达成；交用户手动拖 8 投影成型。"
                    : "奇迹代偿：5 费本体在池（非昔涟），选中后达成 1 个三星五费；交用户手动拖 8 投影成型。";
                return new FateGrailStep("N1", Action.ChooseMiracleCompensation,
                    message,
                    Done: true, RequiresReroll: false, RequiresUserManual: true,
                    NextNode: hasXilian ? "EAB" : "EAA",
                    TrialToChoose: MiracleSide(s));
            }
            // 有效血 <88：奇迹代偿图标不可点。先尝试买「二极管276」补到 ≥88（若可买），
            // 补血后再回来看能否点奇迹代偿；补不了才改选/重刷。
            // 只有「买二极管后有效血能到 ≥88」才值得买（即初始有效血 ≥78）；否则买了仍 <88 白费金币。
            if (!s.DiodeTaken &&
                s.AvailableStrategyIds.Contains(
                    FateGrailHealthGate.DiodeInvestmentStrategyId,
                    StringComparer.OrdinalIgnoreCase) &&
                FateGrailHealthGate.IsMiracleCompensationClickable(
                    effectHp, diodeAlreadyApplied: true))
            {
                return new FateGrailStep("X2", Action.BuyDiode,
                    $"祈愿浮现「奇迹代偿」但有效 hp={effectHp}&lt;88，买二极管276(+10)→ {effectHp + FateGrailHealthGate.DiodeHealthBonus}≥88 再回来点。",
                    Done: false, RequiresReroll: false, RequiresUserManual: false,
                    NextNode: "C2");
            }
            // 吃过二极管仍 <88，或策略里没有 276 -> 血补不起来，奇迹代偿不可点。
            // 若另一侧是「出售圣杯/拆散羁绊」类禁选试炼，点它等于自毁本局 -> 重刷；
            // 若圣杯成员已收满（BondTier>=5，机会耗尽）仍无胜利件且血补不起来 -> 本局无法达成 -> 重刷；
            // 否则改选另一试炼（不重刷）。
            var otherTrial = OtherTrialText(s);
            if (PrayTrialPreference.IsForbidden(otherTrial) || s.BondTier >= 5)
            {
                return Reroll("C2",
                    "祈愿浮现「奇迹代偿」但 hp 不足不可点，且另一侧试炼不可选（禁选类或机会用尽仍无胜利试炼），本局无法达成目标 -> 重刷。");
            }
            return new FateGrailStep("N2", Action.ChoosePassiveTrial,
                $"祈愿浮现「奇迹代偿」但 hp={effectHp}&lt;88 不可点，改选另一侧试炼。",
                Done: false, RequiresReroll: false, RequiresUserManual: false,
                NextNode: "C2",
                TrialToChoose: OtherTrialSide(s));
        }

        // L/D6 一般试炼：优先高价值试炼（祈愿择优）；第1、2个诅咒都没想要的 -> 默认选左（不重刷）。
        var pref = PrayTrialPreference.Choose(s.LeftTrial, s.RightTrial);

        // 用户 2026-08-25 澄清：试炼按档位随机抽取，胜利试炼（无限之釜/奇迹代偿）
        // 在较高羁绊档位才可能进入抽取池；但只要当前 2 选 1 出现即可达成，
        // 与具体档位号无关。故胜利判定不限档位（去掉旧"仅第4档"限制）。

        // 达成终点：无限之釜线 = 直接给三星 Archer（无需 88/本体）。
        // 注意：祈愿试炼是「2 选 1」弹框，必须先点选所在侧才算领取——
        // 因此这里发 ChoosePassiveTrial（带 TrialToChoose）且 Done，
        // 由协调器先执行点选动作、再收工（与 N1 奇迹代偿同构）；不能只报 Achieved 不点选。
        if (HasInfernalCauldron(s.LeftTrial, s.RightTrial))
        {
            return new FateGrailStep("P", Action.ChoosePassiveTrial,
                s.Goal == UserGoal.All
                    ? "无限之釜浮现：先点选该侧领取三星 Archer；目标 B 全员：续追其它 5 费（交用户/后续流程）。"
                    : "无限之釜浮现：先点选该侧领取三星 Archer；目标 A 只要 1 个 -> 收工，交用户接管。",
                Done: true, RequiresReroll: false,
                RequiresUserManual: s.Goal == UserGoal.AnyOne,
                NextNode: s.Goal == UserGoal.AnyOne ? "EAA" : "EAB",
                TrialToChoose: CauldronSide(s));
        }

        // 两五费聘书试炼（2026-08-26 用户拍板：聘书是本体/Archer 来源，专门响应）。
        // 3/4 圣杯档才开，给 2 张自选 5 费（可自选 Archer / 昔涟）。选了：
        //   - 目标本体/能上场 Archer 到手（补 4 圣杯或本体复制目标）。
        //   - 但聘书非「胜利试炼」（不直接给三星），选它不等于达成，推进（Done=false）等后续试炼。
        if (HasTrialOffer(s.LeftTrial, s.RightTrial))
        {
            var target = s.Goal == UserGoal.All ? Core5CostXilian : "Archer";
            return new FateGrailStep("BOOK", Action.ChoosePassiveTrial,
                $"祈愿浮现「两五费聘书」：{(s.Goal == UserGoal.All ? "目标 B 全员选昔涟补本体/续凑" : "目标 A 选 Archer 补本体/能上场 Archer")}，先点选该侧领取聘书。",
                Done: false, RequiresReroll: false, RequiresUserManual: false,
                NextNode: "BOOK",
                TrialToChoose: OfferSide(s));
        }

        // 抽取机会耗尽（用户 2026-08-25/26）：5 个圣杯羁绊物品（凛/闪/Saber/Archer/星徽）已齐（BondTier=5）。
        // 要激活 5 圣杯最终试炼需 5 人口：若当前人口<5，先买经验升人口（用户 2026-08-26：4命杯+1星徽齐才触发，
        // 点商店等级识别框内任意点两次=8金币升1人口），而不是直接重刷。
        // 人口已>=5 且仍无胜利试炼才判"机会耗尽重刷"。
        if (s.BondTier >= 5)
        {
            if (s.Population < 5)
            {
                return new FateGrailStep("POP", Action.BuyPopulation,
                    $"5 圣杯羁绊已齐（BondTier={s.BondTier}）但当前人口 {s.Population}<5，需购买经验升人口到 5 才能激活最终圣杯试炼。",
                    Done: false, RequiresReroll: false, RequiresUserManual: false,
                    NextNode: "C2");
            }
            return Reroll("C2",
                "5 个圣杯羁绊角色已收满且人口≥5，仍无胜利试炼（无限之釜/奇迹代偿），本局无法达成目标 -> 重刷。");
        }

        // 一般试炼：选高价值侧；都没有想要的 -> 默认左（不重刷）。
        if (pref == PrayTrialPreference.TrialChoice.None)
        {
            // 已历经多档仍无高价值侧，且目标 B 全员本体未到手 -> 续追无望 -> 重刷。
            if ((s.TrialOneTaken && s.TrialTwoTaken) &&
                s.Goal == UserGoal.All && !s.HasBody5Cost)
                return Reroll("C2",
                    "前两档试炼均已选仍无高价值侧，且目标 B 全员本体未到手，续追无望 -> 重刷。");
            return new FateGrailStep("M1", Action.ChoosePassiveTrial,
                "第 1/2 档试炼都没刷到想要的：默认选左侧试炼推进（试炼计数由上层在选后自增为 TrialOneTaken/TrialTwoTaken，不重刷）。",
                Done: false, RequiresReroll: false, RequiresUserManual: false,
                NextNode: "L",
                TrialToChoose: TrialSide.Left);
        }

        return new FateGrailStep("M2", Action.ChoosePassiveTrial,
            $"祈愿试炼择优：选择高价值侧（{pref}）。",
            Done: false, RequiresReroll: false, RequiresUserManual: false,
            NextNode: "M2",
            TrialToChoose: pref == PrayTrialPreference.TrialChoice.Right
                ? TrialSide.Right
                : TrialSide.Left);
    }

    /// <summary>「奇迹代偿」试炼所在侧（N1 点它；若两侧都有取左）。</summary>
    private static TrialSide MiracleSide(Snapshot s) =>
        ContainsAny(s.LeftTrial, "奇迹代偿")
            ? TrialSide.Left
            : TrialSide.Right;

    /// <summary>「奇迹代偿」对面的另一侧（N2 血不够时改选它）。</summary>
    private static TrialSide OtherTrialSide(Snapshot s) =>
        ContainsAny(s.LeftTrial, "奇迹代偿")
            ? TrialSide.Right
            : TrialSide.Left;

    /// <summary>「奇迹代偿」对面一侧的试炼文本（N2 判其是否禁选）。</summary>
    private static string? OtherTrialText(Snapshot s) =>
        ContainsAny(s.LeftTrial, "奇迹代偿")
            ? s.RightTrial
            : s.LeftTrial;

    /// <summary>「无限之釜」所在侧（P 步点它领取；若两侧都有取左）。</summary>
    private static TrialSide CauldronSide(Snapshot s) =>
        ContainsAny(s.LeftTrial, "无限之釜")
            ? TrialSide.Left
            : TrialSide.Right;

    private static bool HasFourBond(Snapshot s)
    {
        // 需要 3 名商店成员全持 + 第 4 档触发件（已持有「能上场」的 Archer，或已持有「圣杯星徽 / 圣杯转」）。
        // 2026-08-26 用户拍板修正：067 英雄登场送的 Archer 上不了台、不激活羁绊，**不算可上场的 Archer**。
        //   -> 只有 067 送 Archer 上不了台时，须再补一张「能上场」的 Archer（登场Archer/聘书/采购刷出）才算 4 圣杯。
        var storeOwned = FateGrailShoppingPolicy.StorePurchaseNames
            .All(n => s.OwnedMembers.Contains(n, StringComparer.OrdinalIgnoreCase));
        var hasPlayableArcher = s.OwnedMembers.Contains("Archer", StringComparer.OrdinalIgnoreCase)
            // 若 067 送的就是 Archer（上不了台），需排除：OwnedMembers 里那份不算「能上场」。
            && !(IsHeroArrival(s.EnvironmentId) && IsArcher(s.EnvGifted5Cost));
        var hasFourthBondTrigger = hasPlayableArcher || s.HasStarBadge;
        return storeOwned && hasFourthBondTrigger;
    }

    private static bool IsHeroArrival(string? env) =>
        string.Equals(env, EnvironmentHeroArrival, StringComparison.OrdinalIgnoreCase);

    private static bool IsArcher(string? name) =>
        string.Equals(name, "Archer", StringComparison.OrdinalIgnoreCase);

    /// <summary>Q0：本局是否需上 5 圣杯（全员目标）且缺星徽。单人目标 A 只要 4 圣杯，通常不需星徽。</summary>
    private static bool NeedsStarBadge(Snapshot s)
    {
        // 4 圣杯已够出奇迹代偿/无限之釜（单人 A / 无需5档）。需星徽 = 目标 B 全员要 5 圣杯档 且 尚未持有星徽。
        if (s.Goal != UserGoal.All)
            return false;
        if (s.HasStarBadge)
            return false;
        // 星徽来源（019 环境 / 321/333 策略 / 试炼）——本节点负责在可选策略里挑 321>333。
        return true;
    }

    /// <summary>Q0：在可选策略里挑星徽策略。优先 321（无副作用+送远坂凛）> 333（星徽+10金但有副作用）。</summary>
    private static string? PickStarBadge(IReadOnlySet<string> selectedStrategyIds)
    {
        if (selectedStrategyIds.Contains(StarBadgeStrategyId, StringComparer.OrdinalIgnoreCase))
            return StarBadgeStrategyId;              // 321：无副作用，优先
        if (selectedStrategyIds.Contains(StarBadgeSideEffectStrategyId, StringComparer.OrdinalIgnoreCase))
            return StarBadgeSideEffectStrategyId;    // 333：也给星徽，兜底
        return null;
    }

    /// <summary>Q0：本局是否需要「采购专员」提升目标 5 费角色刷出率。
    /// <para>对应新决策树 Q1P 触发条件：目标 5 费未到手 + 有 5 费刷子（067 送的最左即可当刷子）/
    /// 全员目标缺第二 5 费 / 067 送 Archer 上不了台需补能上场 Archer。
    /// 在 <see cref="FateGrailFlowDecider.Compute"/> 的 ChosenStrategyIds 也纳入（结合二极管），供上层商店循环选用。</para></summary>
    private static bool NeedsPurchaseSpecialist(Snapshot s)
    {
        // 采购专员靠「备战席最左那个 5 费」当刷子刷同费 5 费：用于「出目标 5 费本体」或「补能上场 Archer」。
        if (s.Goal == UserGoal.All)
            return true;  // 全员（B）需续凑第二 5 费
        if (!s.HasBody5Cost)
            return true;  // 本体未到手，需刷本体
        if (IsHeroArrival(s.EnvironmentId) && IsArcher(s.EnvGifted5Cost))
            return true;  // 067 送 Archer 上不了台，需补能上场 Archer
        return false;
    }

    // ---------- G_REF2 商店刷到底判定（D3 缺第 4 档触发件时的先决条件） ----------
    private static bool CanStillAffordShop(Snapshot s) => s.Gold >= 2;
    private static bool HasUnfinishedShopStock(Snapshot s)
        // 商店可继续刷的乐观启发：金币仍够买。真实「连 6 轮无新增」由上层商店插件返回/Snapshot 状态驱动。
        => s.Gold >= 2;

    private static bool TrialRequestsMiracleCompensation(string? left, string? right) =>
        ContainsAny(left, "奇迹代偿") || ContainsAny(right, "奇迹代偿");

    /// <summary>当前是否已浮现胜利试炼（奇迹代偿 / 无限之釜）。浮现则 Q0 不再介入，交 CORE 统一响应。</summary>
    private static bool HasVictoryTrial(Snapshot s) =>
        HasInfernalCauldron(s.LeftTrial, s.RightTrial) ||
        TrialRequestsMiracleCompensation(s.LeftTrial, s.RightTrial);

    private static bool HasInfernalCauldron(string? left, string? right) =>
        ContainsAny(left, "无限之釜") || ContainsAny(right, "无限之釜");

    /// <summary>判断祈愿试炼是否出现「两五费聘书」（可自选 Archer/昔涟，本体的一个来源）。</summary>
    private static bool HasTrialOffer(string? left, string? right) =>
        ContainsAny(left, TrialOfferKeyword) || ContainsAny(right, TrialOfferKeyword);

    /// <summary>「两五费聘书」所在侧（选它领取；两侧都有取左）。</summary>
    private static TrialSide OfferSide(Snapshot s) =>
        ContainsAny(s.LeftTrial, TrialOfferKeyword)
            ? TrialSide.Left
            : TrialSide.Right;

    private static FateGrailStep Reroll(string node, string message) =>
        new(node, Action.Reroll, message, Done: false, RequiresReroll: true,
            RequiresUserManual: false, NextNode: "R1");

    private static bool ContainsAny(string? text, string keyword)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
