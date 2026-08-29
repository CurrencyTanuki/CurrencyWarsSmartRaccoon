namespace CurrencyWarsAssistant.Tasks;

/// <summary>用户目标模式（定稿决策树 L3/L9）。</summary>
public enum GrailUserGoal
{
    /// <summary>单人：任意一个 5 费角色三星即成功（A 目标）。</summary>
    Single,

    /// <summary>全员：等价于把昔涟做到三星——奇迹代偿正确选择 + 本体昔涟在场（B 目标，用户 2026-08-29 拍板）。</summary>
    All,
}

/// <summary>祈愿弹框的某一侧（F2 识别的左右两个试炼）。</summary>
public enum GrailTrialSide
{
    Left,
    Right,
}

/// <summary>
/// 「1-3 三星五费」纯逻辑运行状态（依据 docs/DECISION_TREE_1-3三星五费_定稿版_20260829.mmd）。
/// 只承载决策所需事实；识别（Phase2）与点屏（执行层）由上层组装/执行，本类型可脱离游戏单测。
/// 字段语义与决策树节点一一对应，见各属性注释。
/// </summary>
public sealed record GrailRunSnapshot
{
    /// <summary>冲 5 档买经验固定开销：点商店购买经验区 2 次 = 8 金币 = 8 经验（4→5 级，用户 2026-08-29 拍板）。</summary>
    public const int XpPurchaseGoldCost = 8;

    /// <summary>1-3 不买经验的基础人口（覆盖 2/3/4 档；第 5 档需买经验到 5）。</summary>
    public const int BasePopulation = 4;

    /// <summary>商店刷新最低金币门槛（低于此值视为无法继续刷新；实机可校准）。</summary>
    public const int MinRefreshGold = 2;

    /// <summary>
    /// 当前商店刷新价格：基础 2 金；选中令咒决议·回路过载/行为限制后其诅咒代价 = 刷新价格 +1 金
    /// （用户 2026-08-29 确认；解除条件——回路过载=累计刷新16次、行为限制=商店等级8——
    /// 决策侧保守取“选中后全程 +1”，宁多备一枚金币也不空点）。
    /// </summary>
    public int RefreshGoldCost { get; init; } = MinRefreshGold;

    /// <summary>当前买经验两次的总价：基础 8 金；回路过载诅咒（购买经验价格+1）期间为 10 金。</summary>
    public int XpPurchaseTotalCost { get; init; } = XpPurchaseGoldCost;

    /// <summary>命运圣杯羁绊中可从商店购买的三名成员（Archer 商店 0%，不在此列）。</summary>
    public static readonly string[] ShopBondMemberNames = ["远坂凛", "吉尔伽美什", "Saber"];

    /// <summary>Archer：唯一 5 费命杯成员，仅前台；来源=试炼/聘用书（商店 0%）。</summary>
    public const string ArcherName = "Archer";

    /// <summary>昔涟：5 费，非命杯成员；全员模式的最终目标本体（三星后交用户）。</summary>
    public const string XilianName = "昔涟";

    public GrailUserGoal Goal { get; init; } = GrailUserGoal.Single;

    /// <summary>当前小队生命值（null=识别失败；决策按不满足血量门槛处理——F13 防御口径，宁可选另一侧也不冒被游戏拒绝卡死的风险）。</summary>
    public int? TeamHealth { get; init; }

    /// <summary>当前人口。</summary>
    public int Population { get; init; } = BasePopulation;

    /// <summary>当前金币。</summary>
    public int Gold { get; init; }

    /// <summary>
    /// 已上场（前台/后台）的命运圣杯羁绊成员名（按不同角色去重——自走棋通则：同一角色不能重复上场）。
    /// 羁绊档位只计上场成员，备战席不计（决策树 N4“上场了 N 位”口径）；L4 的 5 费在场判定才含备战席。
    /// </summary>
    public IReadOnlySet<string> DeployedBondMembers { get; init; } = new HashSet<string>();

    /// <summary>
    /// 第 5 个羁绊成员是否已经出现（商店刷出未拥有的命杯成员 / 第二枚命运圣杯星徽到手）——执行层回填。
    /// N16 的买经验门槛以此为前置，避免“理论上可获取但并未出现”时白烧 8 金币买经验。
    /// </summary>
    public bool NewBondMemberAvailable { get; init; }

    /// <summary>已上场且携带命运圣杯星徽、但本身不是命杯成员的角色数（每名 +1 羁绊计数；由装备槽识别回填）。</summary>
    public int BadgeCarrierNonMembers { get; init; }

    /// <summary>已获得、尚未被任何上场角色携带的星徽数（再上场一名携带者即可 +1 羁绊成员）。</summary>
    public int UncarriedStarBadges { get; init; }

    /// <summary>本局已获得的星徽总数（含已携带 + 未携带；S1A/S1B 保留线“留 N 个非命杯角色”的 N 即此值，组装层据此过滤可卖名单）。</summary>
    public int TotalStarBadgesObtained { get; init; }

    /// <summary>本快照的识别时间（null=未知）。血量等字段仅在备战页刷新，弹框期间为上一帧值——
    /// 决策器对超龄数据按“未识别”防御口径处理（与 F13 的未知血口径一致），陈旧度阈值由编排层定。</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>已拥有的角色名全集（同名去重购买依据：开局附赠/开金矿开到/已购买都算已拥有）。</summary>
    public IReadOnlySet<string> OwnedCharacterNames { get; init; } = new HashSet<string>();

    /// <summary>场上（前台/后台/备战席）是否存在 5 费角色（L4/F14a 口径，067 放备战席的也算）。</summary>
    public bool HasFiveCostBody { get; init; }

    /// <summary>场上（含备战席）是否存在名为昔涟的 5 费角色（L10）。</summary>
    public bool XilianOnField { get; init; }

    /// <summary>已获得的五费聘用书总数。</summary>
    public int LettersObtained { get; init; }

    /// <summary>已打开的五费聘用书数（L4 要求全部打开）。</summary>
    public int LettersOpened { get; init; }

    /// <summary>是否已正确选择令咒决议·奇迹代偿（F14/F12 改选落入等情形）。</summary>
    public bool MiracleCompensationSelected { get; init; }

    /// <summary>选择奇迹代偿那一刻的血量（L2 前置：须 ≥89）。</summary>
    public int? MiracleCompensationSelectedAtHealth { get; init; }

    /// <summary>是否已正确选择无限之釜（F16/F17，含经 F12 改选落到无限之釜的情形）。</summary>
    public bool InfiniteCauldronSelected { get; init; }

    /// <summary>本局已响应完毕的祈愿次数（每档一次，共 4 次：2/3/4/5 档各一次）。</summary>
    public int WishesResponded { get; init; }

    /// <summary>是否已触发 N17b（5 档激活机会永久放弃）——闩锁，防止后续重复进入买经验分支。</summary>
    public bool FiveBondGivenUp { get; init; }

    /// <summary>可出售角色数（执行层按“非命杯成员、非星徽携带者、非任何已拥有 5 费”过滤，并已扣除保留线=星徽数）。</summary>
    public int SellableBeyondKeepLineCount { get; init; }

    /// <summary>命运圣杯羁绊在场计数 = 上场命杯成员（按角色去重）+ 上场星徽携带者中的非成员角色数（N4/羁绊档位口径）。</summary>
    public int BondMemberCount => DeployedBondMembers.Count + BadgeCarrierNonMembers;

    /// <summary>是否本局首次祈愿（2 档首次激活 → F9 盲选左，不识别）。</summary>
    public bool IsFirstWishPending => WishesResponded == 0;

    /// <summary>五费聘用书是否全部打开（L4 前置）。</summary>
    public bool AllLettersOpened => LettersOpened >= LettersObtained;

    /// <summary>下一档羁绊所需的上场人数（当前羁绊计数 + 1，5 档封顶——满 5 后不再有更高档）。</summary>
    public int NextTierRequiredMembers => Math.Min(BondMemberCount + 1, 5);
}
