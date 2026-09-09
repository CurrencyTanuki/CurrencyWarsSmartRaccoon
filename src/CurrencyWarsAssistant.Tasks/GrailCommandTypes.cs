namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 指令集 v4.3.1 指令类别（编号对应 docs/GRAIL_COMMAND_SET_v43_final.md；A8 已按用户裁定删除，
/// 概览确认点击下沉 M1 内部）。取值分三段便于分发器路由：I=1xx 识别、A=2xx 操作、M=3xx 宏。
/// </summary>
public enum GrailCommandKind
{
    I1 = 101, I2, I3, I4, I5, I6, I7, I8, I9, I10,
    A1 = 201, A2, A3, A4, A5, A6, A7,
    // A16（1.2.115，用户令单功能隔离测试）：盛会之星升档选择框消除——弹框在屏才
    // 应答（任选角色+确认选择），不在屏零点击；绝不弃局、不碰状态机。
    A9 = 209, A10, A11, A12, A13, A14, A15, A16,
    M1 = 301, M2, M3, M4, M5, M6, M7, M8,
}

/// <summary>指令（决策层下行）。负载只允许语义参数（角色名/名单/页面约定），绝不携带坐标。</summary>
public sealed record GrailCommand(GrailCommandKind Kind, object? Payload = null);

/// <summary>指令上下文：窗口句柄、备战页约定、目标模式（组合根构造一次，各层共用）。</summary>
public sealed record GrailCommandContext(nint WindowHandle, string PreparationPageId, GrailUserGoal Goal);

/// <summary>
/// 指令结果：只携带事实负载或失败原因。操作层不做「操作后确认」——没有操作后再读一次的代码，
/// 成败由决策层用已有状态统一对账判定（用户 2026-09-01 拍板）。
/// </summary>
public sealed record GrailCommandResult(GrailCommandKind Kind, object? Payload, string? Error)
{
    public static GrailCommandResult Ok(GrailCommandKind kind, object? payload = null) => new(kind, payload, null);

    public static GrailCommandResult Fail(GrailCommandKind kind, string error) => new(kind, null, error);
}

/// <summary>指令处理器（识别/操作/宏三类适配器各实现一份；分发器按指令段路由）。</summary>
public interface IGrailCommandHandler
{
    Task<GrailCommandResult> HandleAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken);
}

// ---- 语义负载（只有需要的指令才定义） ----

/// <summary>A1 部署上场：角色名 + 前台/后台。TargetSlot=null 时按占用序内部分配；
/// 决策层可显式传槽位（0 基：前台 0..3/后台 0..5）做精确落位/有意互换
/// （2026-09-02 用户令：槽位由决策层传入，三次计数器事故后的结构性修正）。</summary>
public sealed record GrailDeployArgs(string CharacterName, PreparationLane Lane, int? TargetSlot = null);

/// <summary>A3 出售备战席角色。</summary>
public sealed record GrailCharacterArgs(string CharacterName);

/// <summary>A2 出售场上角色（位置语义，2026-09-02 用户设计定案）：前台/后台 + 槽位号（1 基）。
/// 操作层按标准槽位几何直接拖出售区——不依赖识别卡位、不做名称反查。</summary>
/// <summary>ExpectedCharacterName=引擎台账认为该槽位的角色名（2026-09-08 P1-C：
/// 操作层拖前做卡面身份比对防模型漂移误卖；空=跳过比对维持位置语义）。</summary>
public sealed record GrailPositionArgs(PreparationLane Lane, int SlotIndex, string? ExpectedCharacterName = null);

/// <summary>A3 出售备战席角色（位置语义）：备战席槽位号（1 基，1-9）。</summary>
public sealed record GrailBenchSlotArgs(int SlotIndex);

/// <summary>A10/M7 投资策略：策略 ID 集（语义名，槽位号由操作层解析）。</summary>
public sealed record GrailStrategyArgs(IReadOnlySet<string> PreferredStrategyIds);

/// <summary>M5 商店 Pass 参数：带圣杯标记=1-3 N14 循环语义（2026-09-03 用户修正：
/// 白名单仅命杯+昔涟、无目标刷新 2 金、买到才关店→上场→重开、真实空槽上场、购买后验证）；
/// 不带=1-1/1-2 单轮语义原样。</summary>
public sealed record GrailShopPassArgs(bool GrailLoopMode);

/// <summary>M1 开战：从备战页打完一场战斗并推进到预期落地页（页面 ID 沿用既有代码的语义约定）。</summary>
public sealed record GrailBattleArgs(string PreparationPageId, string ExpectedPostBattlePageId);

// ---- 事实负载 ----

/// <summary>I1 页面事实。</summary>
/// <summary>
/// I1 页面事实。IsStale=最新帧已超出 15 秒陈旧度窗口（识别流冻结/停止时
/// LatestAnalysis 会一直停在最后一帧——2026-09-02 实测冻结 6 分钟后 I1 仍报旧页，
/// 下游必须把 Stale 读数当"无现状"处理，绝不当作当前页面）。
/// </summary>
public sealed record GrailPageFact(
    string? PageId,
    bool WishDialogOpen,
    DateTimeOffset? CapturedAt = null,
    bool IsStale = false);

/// <summary>角色事实（Slot=1 基槽位号标签，含区划前缀：前台N/后台N/备战席N；规格 I3/I4 要求槽位号）。</summary>
public sealed record GrailCharacterFact(string? Name, int? Cost, string Slot);

/// <summary>I5/I6 血量/金币读数（Value=null 表示未知；CapturedAt 供决策层做陈旧度防御）。</summary>
public sealed record GrailMeterFact(int? Value, DateTimeOffset? CapturedAt);

/// <summary>I7 星徽事实。</summary>
public sealed record GrailBadgeFact(int TotalObtained, int Uncarried);

/// <summary>I9 档位事实（羁绊计数=上场命杯成员+星徽携带者，口径同快照）。</summary>
public sealed record GrailTierFact(int BondMemberCount, IReadOnlySet<string> DeployedBondMembers);

/// <summary>M4 开聘用书事实。</summary>
public sealed record GrailLettersFact(int Obtained, int Opened);

/// <summary>M3 祈愿应答事实（宏只回事实；收工/弃局判定归决策层 GrailFinalJudge）。</summary>
public sealed record GrailWishOutcomeFact(
    bool Responded,
    int WishesResponded,
    int LettersObtained,
    int LettersOpened,
    bool MiracleSelected,
    bool CauldronSelected);

/// <summary>M5 商店 Pass 事实（含货架全名单，供决策层核对购买判定）。
/// 1.2.90：DeployedFrontSlots=执行器自动上场的单位→前台槽位（0 基），供上场台账登记。
/// 09-10 深夜班：LiveLedgerGold=执行器实时本地账（1.2.24 口径：刷新扣款实时反映；
/// 坏账/未跑=null 不兜底）——区别于 GoldAfter 的"本地账无效时兜底持有器值"混合语义，
/// 供 R3 判死作行为级金尽证据（OCR 幻值时唯一可信源）。</summary>
public sealed record GrailShopPassFact(
    bool BoughtCharacter,
    int? GoldAfter,
    IReadOnlyList<string>? ShelfCharacterNames = null,
    IReadOnlyList<string>? BoughtCharacterNames = null,
    IReadOnlyDictionary<string, int>? DeployedFrontSlots = null,
    int? LiveLedgerGold = null);

/// <summary>M8 开局重刷事实（停靠点=1-1 备战席入口，2026-09-02 用户拍板）。</summary>
public sealed record GrailOpeningFact(
    bool Succeeded,
    string Message,
    string? MatchedEnvironmentId = null,
    string? MatchedEnvironmentName = null);
