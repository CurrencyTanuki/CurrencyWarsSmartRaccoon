using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public static class RewardSettlementDetailEvidence
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;

    public static bool IsMatch(CaptureFrame frame) =>
        HasRatio(frame, new PixelRect(770, 860, 380, 70), IsLight, 0.55) &&
        HasRatio(frame, new PixelRect(760, 150, 900, 690), IsDark, 0.72);

    private static bool HasRatio(
        CaptureFrame frame,
        PixelRect reference,
        Func<byte, byte, byte, bool> predicate,
        double minimum)
    {
        if (frame.Width <= 0 || frame.Height <= 0 ||
            Math.Abs(frame.Width / (double)frame.Height - 16d / 9d) > 0.02)
        {
            return false;
        }

        var left = (int)Math.Round(reference.X * frame.Width / (double)ReferenceWidth);
        var top = (int)Math.Round(reference.Y * frame.Height / (double)ReferenceHeight);
        var right = (int)Math.Round(reference.Right * frame.Width / (double)ReferenceWidth);
        var bottom = (int)Math.Round(reference.Bottom * frame.Height / (double)ReferenceHeight);
        var matched = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                var offset = y * frame.Stride + x * 4;
                if (offset < 0 || offset + 2 >= frame.BgraPixels.Length)
                {
                    return false;
                }

                sampled++;
                if (predicate(
                        frame.BgraPixels[offset + 2],
                        frame.BgraPixels[offset + 1],
                        frame.BgraPixels[offset]))
                {
                    matched++;
                }
            }
        }

        return sampled > 0 && matched / (double)sampled >= minimum;
    }

    private static bool IsLight(byte red, byte green, byte blue) =>
        red >= 210 && green >= 210 && blue >= 210;

    private static bool IsDark(byte red, byte green, byte blue) =>
        red <= 55 && green <= 55 && blue <= 55;
}

public static class CurrencyWarsHomeEvidence
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;

    // Context-only fallback for the Currency Wars home page. The normal title
    // template can be obscured by the automation log overlay, while these two
    // independent layout regions remain visible.
    public static bool IsMatch(CaptureFrame frame) =>
        HasRatio(frame, new PixelRect(1320, 925, 570, 105), IsLightBlue, 0.55) &&
        HasRatio(frame, new PixelRect(40, 260, 250, 210), IsDarkBlue, 0.85);

    private static bool HasRatio(
        CaptureFrame frame,
        PixelRect reference,
        Func<byte, byte, byte, bool> predicate,
        double minimum)
    {
        if (frame.Width <= 0 || frame.Height <= 0 ||
            Math.Abs(frame.Width / (double)frame.Height - 16d / 9d) > 0.02)
        {
            return false;
        }

        var left = (int)Math.Round(reference.X * frame.Width / (double)ReferenceWidth);
        var top = (int)Math.Round(reference.Y * frame.Height / (double)ReferenceHeight);
        var right = (int)Math.Round(reference.Right * frame.Width / (double)ReferenceWidth);
        var bottom = (int)Math.Round(reference.Bottom * frame.Height / (double)ReferenceHeight);
        var matched = 0;
        var sampled = 0;
        for (var y = top; y < bottom; y += 4)
        {
            for (var x = left; x < right; x += 4)
            {
                var offset = y * frame.Stride + x * 4;
                if (offset < 0 || offset + 2 >= frame.BgraPixels.Length)
                {
                    return false;
                }

                sampled++;
                if (predicate(
                        frame.BgraPixels[offset + 2],
                        frame.BgraPixels[offset + 1],
                        frame.BgraPixels[offset]))
                {
                    matched++;
                }
            }
        }

        return sampled > 0 && matched / (double)sampled >= minimum;
    }

    private static bool IsLightBlue(byte red, byte green, byte blue) =>
        red >= 170 && green >= 185 && blue >= 210 && blue >= red + 15;

    private static bool IsDarkBlue(byte red, byte green, byte blue) =>
        red < 100 && green < 120 && blue < 170;
}

/// <summary>
/// Completes the currently displayed investment selection, enters preparation,
/// abandons the run, advances the settlement pages and verifies that the
/// Currency Wars home page has returned.
/// </summary>
/// <summary>
/// 1.2.119（审计簇 C）：祈愿试炼弹框的按需应答能力。可靠检测/应答核心在
/// WishTrialSelectionAutomation（1.2.106 已证不在屏零副作用），由持有该组件的
/// 类实现本接口并注入弃局恢复类——此前恢复类只认 gala 弹框，祈愿弹框在屏时
/// 弃局链只敢空转等待（审计局 4/6：弹框在屏 68/74 秒无人应答→弃局）。
/// </summary>
public interface IWishTrialPopupHandler
{
    /// <summary>祈愿试炼弹框在屏则应答（检测+选侧+确认），返回是否已处理。</summary>
    Task<bool> DismissWishTrialPopupIfUpAsync(
        nint windowHandle,
        CancellationToken cancellationToken);
}

/// <summary>
/// 1.2.119（审计簇 C/E）：统一模态弹框守卫——快照长尾恢复、M2 开矿前、M1 出战前
/// 等时点的"弹框在屏先应答"入口（实拍+分类器认页，禁用 I1）。
/// </summary>
public interface IModalGuard
{
    Task<bool> DismissBlockingModalIfUpAsync(
        nint windowHandle,
        CancellationToken cancellationToken);
}

/// <summary>
/// 弃局兜底：主动放弃当前对局并回到安全入口页。异常不外泄，失败也继续（四轮 R1-R5）。
/// 1.2.119（审计簇 A）：追加 reason 留痕参数——所有弃局必须带发起原因
/// （消灭审计定性的"无留痕弃局"：R3/失败 混用标签导致弃局决策不可审计）。
/// </summary>
public interface IRunAbandoner
{
    Task<RejectedOpeningRecoveryResult> AbandonCurrentRunAsync(
        nint windowHandle,
        CancellationToken cancellationToken,
        string? reason = null);
}

/// <summary>盛会弹框消除结果三态（坑50；A16 回执与泵退避共用）。</summary>
public enum GalaBondDismissOutcome
{
    /// <summary>弹框不在屏（零点击，识别未命中）。</summary>
    NotOnScreen,

    /// <summary>弹框已应答关闭（任选角色+确认选择）。</summary>
    Dismissed,

    /// <summary>应答后弹框仍在（候选点位耗尽，如实失败）。</summary>
    Failed,
}

/// <summary>
/// 盛会之星羁绊升档选择框的按需消除（坑50，1.2.114）：弹框在屏才点击
/// （任选一名角色+确认选择，用户 2026-09-06 23:0x 口径"随便点一个"），
/// 不在屏时零点击零副作用——供循环泵在运营 tick 前应答，与祈愿弹框同性质。
/// </summary>
public interface IGalaBondPopupHandler
{
    Task<GalaBondDismissOutcome> DismissGalaBondPopupIfUpAsync(
        nint windowHandle,
        CancellationToken cancellationToken);
}

public sealed class CurrencyWarsRejectedOpeningRecovery(
    ICurrencyWarsOpeningNavigator navigator,
    IGameCapture capture,
    IGamePageClassifier classifier,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    ITaskEventSink eventSink,
    IWishTrialPopupHandler? wishTrialHandler = null,
    Func<nint, CancellationToken, Task<bool>>? closeShopIfOpen = null,
    Func<nint, CancellationToken, Task<bool>>? selectLeftmostStrategyIfUp = null) :
    IRejectedOpeningRecovery,
    IAbandonSettlementRecovery,
    IRunAbandoner,
    IGalaBondPopupHandler,
    IModalGuard
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;
    private static readonly StandardPoint AbandonAndSettlePoint = new(750, 744);
    private static readonly StandardPoint NextPoint = new(960, 899);
    // "前台区域无角色，无法出战"提示弹窗的确认按钮（12:08 实拍帧裁测，1920 参考系）。
    private static readonly StandardPoint UncompletedPromptConfirmPoint = new(960, 699);
    // 1.2.119（审计簇 A）：弃局链硬上限——此前该链在 Esc 无效页面上每 40 秒空转，
    // 通宵实测 15 段游程合计 ≈117 分钟（占窗口 47%，最长 23 分钟）。
    private static readonly TimeSpan AbandonChainHardDeadline =
        TimeSpan.FromSeconds(90);
    // 1.2.119（审计簇 C）：gala 应答与三处既有泵的并发双击防护（P2-7）——
    // DI 为 Transient 多实例，实例字段退避不共享，故用类级信号量串行化点击。
    private static readonly SemaphoreSlim GalaDismissGate = new(1, 1);

    // ---- 盛会之星羁绊升档选择框（坑50，1.2.114）----
    // 页面 ID 与识别表/AutomationPageIds/FastPageIds 三处同步（坑48 纪律）。
    public const string GalaBondPopupPageId = "gala_star_bond_selection";
    // P1-B（2026-09-09 修复批）：角色详情面板（右侧残留框）。G15 局实锤：面板残留
    // 90% 局时长的槽位识别污染（右侧后台槽/备战席右段被遮）。同坑48 三处同步。
    public const string CharacterDetailPopupPageId = "character_detail_popup";
    // 残留框关闭点击点（1920 参考系）：前台卡行与后台槽行之间的空白带——(700,525)
    // 不落任何卡槽/按钮（FrontSlots y≤469 / BackSlots y≥600 之间），且在面板矩形
    // x≥1392 之外=框外点击。未用 Esc——备战页 Esc 语义=打开弃局结算菜单（四.22
    // 链路），详情框若不吞 Esc 会误开菜单。
    private static readonly StandardPoint CharacterDetailDismissPoint = new(700, 525);
    // 卡片行候选点位（1920 参考系，stall_end.png 实拍标定 2026-09-06 深夜）：
    // 卡片间距 248、行中心 x≈1075，候选覆盖 1~4 卡布局；点间隙无害（无选中，
    // 确认钮置灰），复查不过换下一候选。全部点位都在弹框矩形内（模态吞输入，
    // 误点不落底层页面——坑39 盲点击纪律按页 ID 门禁）。
    private static readonly StandardPoint[] GalaPortraitCandidates =
    [
        new(970, 270), new(1218, 270), new(1094, 270), new(846, 270), new(1342, 270)
    ];
    // 「确认选择」按钮（未选人时置灰，点选后激活；点击置灰态无害）。
    private static readonly StandardPoint GalaConfirmPoint = new(1540, 590);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private TimeSpan _pauseBaseline;

    private DateTimeOffset ActiveUtcNow =>
        DateTimeOffset.UtcNow -
        (foregroundGuard.TotalPausedDuration - _pauseBaseline);

    /// <summary>
    /// 弃局兜底入口（不依赖开局快照，卡在任意页面均可）：Esc 进入放弃确认弹窗 →
    /// 完成结算推进回主页；Esc 无效（非对局页）时按页面身份分流——结算详情页点
    /// "下一页/保存并退出"位推进，备战页绝不点击（防误触出战），主界面即成功
    /// （2026-09-04 用户令）。最多 3 轮。
    /// </summary>
    public async Task<RejectedOpeningRecoveryResult> AbandonCurrentRunAsync(
        nint windowHandle,
        CancellationToken cancellationToken,
        string? reason = null)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        // 1.2.119（审计簇 A3）：弃局发起原因必须留痕——此前 R3/失败/策略弃局共用
        // 一个"本局判定结束"标签，弃局决策完全不可审计（无留痕弃局 3 例）。
        Publish(
            "RecoveryAbandonStarted",
            string.IsNullOrWhiteSpace(reason)
                ? "弃局链启动（未提供原因——调用方应传 reason 以供审计）。"
                : $"弃局链启动，原因：{reason}",
            TaskEventLevel.Warning);
        await SaveAbandonEvidenceAsync(windowHandle, "start", cancellationToken);
        // 1.2.119（审计簇 A）：硬上限 90 秒——此前 Esc 空转最长 23 分钟。
        var deadline = ActiveUtcNow + AbandonChainHardDeadline;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (ActiveUtcNow >= deadline)
            {
                Publish(
                    "RecoveryAbandonDeadlineExceeded",
                    $"弃局链达到 {AbandonChainHardDeadline.TotalSeconds:F0} 秒硬上限" +
                    "（页面始终不满足任何可推进分支）——如实失败并交外层。",
                    TaskEventLevel.Warning);
                return RejectedOpeningRecoveryResult.Failed(
                    "弃局链 90 秒硬上限耗尽（页面身份始终无法推进）。");
            }

            // 1.2.58（独立分析 P-12）：进 1-1 后 1ms 即发 Esc 的失败率 22%——
            // 先给入场动画 2.5 秒；Esc 重试 1→2 次（多数失败几秒后重按即成功）。
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            var exitPrompt = await PressKeyUntilPageAsync(
                windowHandle,
                InputKey.Escape,
                "使用 Esc 退出当前对局",
                "abandon_settlement_prompt",
                TimeSpan.FromSeconds(4),
                2,
                cancellationToken);

            if (exitPrompt is null)
            {
                // 2026-09-04 用户令：兜底点击必须按页面身份分流——
                // ①备战页（preparation_）绝不点击：(960,899) 在备战页上是出战按钮，
                //   误触会真实开战（备战期灾难）。
                //   1.2.119（审计簇 A）：改为 StillInGame 如实返回——此前该分支
                //   continue 空转，通宵实测单段最长空转 23 分钟（审计 6-7-2/8-1）。
                // ②主界面（normal_hud/currency_wars_home）：弃局目标已达成，直接成功。
                // ③无法出战提示弹窗：点"确认"关闭。
                // ④盛会/祈愿弹框：先应答再弃（1.2.114+1.2.119 簇 C）。
                // ⑤商店页：先关店再弃（1.2.119 审计 2-2）。
                // ⑥投资策略页：过路选择再弃（坑 19：Esc 无效模态）。
                // ⑦Unknown：禁止一切兜底点击。
                var fallbackPage = await ReadStablePageAsync(
                    windowHandle,
                    cancellationToken);
                var fallbackPageId = fallbackPage?.PageId ?? string.Empty;

                // 坑50（1.2.114）：盛会之星羁绊升档选择框浮在备战页上——先应答关闭
                //（任选角色+确认选择），下一轮 Esc 即可正常弃局；消除失败也绝不
                // fallthrough 到 (960,899)（该点在此弹框上的语义未经验证）。
                if (string.Equals(
                        fallbackPageId,
                        GalaBondPopupPageId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Publish(
                        "RecoveryGalaPopupBlocked",
                        "Esc 无效根因=盛会之星羁绊升档选择框在屏——先应答关闭（任选一名角色+确认选择）再弃局。",
                        TaskEventLevel.Warning);
                    if (await DismissGalaBondPopupCoreAsync(
                            windowHandle,
                            cancellationToken))
                    {
                        continue;
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken);
                    continue;
                }

                // 1.2.119（审计簇 A/簇 C）：祈愿试炼弹框在屏=应答而非空转——
                // 审计局 4/6：弹框在屏 68/74 秒无人应答→弃局链全败。
                if (string.Equals(
                        fallbackPageId,
                        "wish_trial_selection",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (wishTrialHandler is not null
                        && await wishTrialHandler.DismissWishTrialPopupIfUpAsync(
                            windowHandle,
                            cancellationToken))
                    {
                        continue;
                    }

                    Publish(
                        "RecoveryWishPopupDismissFailed",
                        "祈愿试炼弹框应答失败（无处理器或应答未通过）——如实失败，不盲点。",
                        TaskEventLevel.Warning);
                    return RejectedOpeningRecoveryResult.Failed(
                        "弃局链遇祈愿试炼弹框且应答失败。");
                }

                // 1.2.119（审查 P3-1）：列车同行伙伴选择框（模态）——任选点位未标定，
                // 绝不 fallthrough 到 (960,899)（该模态页语义未验证），如实失败交外层。
                if (string.Equals(
                        fallbackPageId,
                        "companion_selection",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Publish(
                        "RecoveryCompanionSelectionUntested",
                        "弃局链遇列车同行伙伴选择框（任选点位未标定，本版不点击）——如实失败。",
                        TaskEventLevel.Warning);
                    return RejectedOpeningRecoveryResult.Failed(
                        "弃局链遇列车同行伙伴选择框且无标定点位。");
                }

                // 1.2.119（审计 2-2/簇 A 分流表）：商店页在屏=先关店再弃——
                // 此前落"其余→点 (960,899)"桶，该点在商店页=货架卡片区（语义未验证）。
                if (string.Equals(
                        fallbackPageId,
                        "reward_shop",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (closeShopIfOpen is not null
                        && await closeShopIfOpen(windowHandle, cancellationToken))
                    {
                        continue;
                    }

                    return RejectedOpeningRecoveryResult.StillInGame(
                        "弃局链遇商店页且无关店能力（closeShopIfOpen 未注入或失败）——对局仍在。");
                }

                // 1.2.119（审计簇 A 分流表）：投资策略页=强制模态（坑 19：Esc 无效），
                // 过路选择（最左+确认）进局后下一轮 Esc 走局内弃局。
                if (string.Equals(
                        fallbackPageId,
                        "investment_strategy",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (selectLeftmostStrategyIfUp is not null
                        && await selectLeftmostStrategyIfUp(
                            windowHandle,
                            cancellationToken))
                    {
                        continue;
                    }

                    Publish(
                        "RecoveryStrategyPageStuck",
                        "投资策略页过路选择失败（无委托或选择未生效）——如实失败。",
                        TaskEventLevel.Warning);
                    return RejectedOpeningRecoveryResult.Failed(
                        "弃局链遇投资策略页且过路选择失败。");
                }

                // 1.2.119（审计簇 A 分流表）：出战人数不足提示=点确认关闭（既有点位）。
                if (string.Equals(
                        fallbackPageId,
                        "incomplete_lineup_prompt",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await ClickStandardPointAsync(
                        windowHandle,
                        "recovery_incomplete_prompt_confirm",
                        "弃局链：确认关闭无法出战提示",
                        UncompletedPromptConfirmPoint,
                        new ActionPolicy(),
                        cancellationToken);
                    continue;
                }

                if (fallbackPageId.StartsWith(
                        "preparation_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 1.2.119（审计簇 A）：对局仍在=弃局前提不成立，StillInGame 如实
                    // 返回交上层按语义分流（入口重刷期=维持弃局重开；局内弃局=终止
                    // 弃局回入口判页）。**绝不重入运营循环 S5**（复核 P1-6④）。
                    Publish(
                        "RecoveryStillInGame",
                        $"当前为备战页（{fallbackPageId}）——对局仍在，弃局链如实终止（StillInGame）。",
                        TaskEventLevel.Warning);
                    await SaveAbandonEvidenceAsync(windowHandle, "still-in-game", cancellationToken);
                    return RejectedOpeningRecoveryResult.StillInGame(
                        $"弃局链检测到对局仍在（{fallbackPageId}）。");
                }

                if (fallbackPageId is "normal_hud" or "currency_wars_home")
                {
                    Publish(
                        "RecoveryAlreadyAtHome",
                        $"已识别 {fallbackPageId}——弃局目标（回到主界面）已达成。");
                    return RejectedOpeningRecoveryResult.Recovered(
                        "已回到货币战争主界面。");
                }

                // 1.2.58（架构审查 3-4 遗漏①）：页面完全无法识别（Esc 可能刚打开
                // 系统菜单/半透明动画态）时禁止一切兜底点击——(960,899) 在未知页面
                // 上的语义未经验证，点了就是盲点。只报警并交给外层重试。
                if (fallbackPage is null)
                {
                    Publish(
                        "RecoverySkipUnknownClick",
                        "当前页面无法识别（Unknown）——禁止兜底点击，等待下一轮 Esc 后重判。",
                        TaskEventLevel.Warning);
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken);
                    continue;
                }

                var isPrompt = fallbackPage is
                    { PageId: "uncompleted_battle_prompt" };
                var clickResult = await ClickStandardPointAsync(
                    windowHandle,
                    $"grail_abandon_exit_{attempt}",
                    isPrompt ? "确认关闭无法出战提示" : "保存并退出/结算推进位",
                    isPrompt ? UncompletedPromptConfirmPoint : NextPoint,
                    new ActionPolicy(),
                    cancellationToken);
                if (clickResult.Succeeded)
                {
                    exitPrompt = await WaitForPageAsync(
                        windowHandle,
                        "abandon_settlement_prompt",
                        TimeSpan.FromSeconds(4),
                        cancellationToken);
                }
            }

            if (exitPrompt is not null)
            {
                return await CompleteFromAbandonSettlementPromptCoreAsync(
                    windowHandle,
                    cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        // 1.2.97（rule 四.22 全覆盖）：Esc/退出按钮链耗尽仍未进入放弃结算确认页——
        // 实弹 05:0x：1-2 战败后游戏弹"对局未完成"统计页（识别表外=Unknown、Esc 无效），
        // 循环内保守语义（Unknown 禁点击）两轮耗尽后若直接 Failed，引擎将对统计页
        // 空转无限循环。按用户弃局原则：结算转场页族不识别、盲点推进位点到回主界面
        // （统计页的"返回货币战争"按钮恰在推进位附近）。盲点直通自带主页/备战页急停。
        Publish(
            "RecoveryFallbackBlindAdvance",
            "Esc 与退出按钮链耗尽仍未进入放弃结算确认页——按弃局原则盲点直通主界面（不识别中间页）。",
            TaskEventLevel.Warning);
        await SaveAbandonEvidenceAsync(windowHandle, "blind-advance", cancellationToken);
        if (await BlindAdvanceToHomeAsync(windowHandle, cancellationToken))
        {
            return RejectedOpeningRecoveryResult.Recovered(
                "Esc 链走不通后盲点推进已回到货币战争主界面。");
        }

        await SaveAbandonEvidenceAsync(windowHandle, "failed", cancellationToken);
        return Failed("弃局兜底：Esc 与退出按钮均未能进入放弃结算确认页；盲点直通也未确认回主界面。");
    }

    /// <summary>IGalaBondPopupHandler：弹框在屏才应答（任选角色+确认选择）；不在屏=NotOnScreen 零点击。</summary>
    public async Task<GalaBondDismissOutcome> DismissGalaBondPopupIfUpAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        if (!await IsGalaBondPopupOnScreenAsync(windowHandle, cancellationToken))
        {
            return GalaBondDismissOutcome.NotOnScreen;
        }

        return await DismissGalaBondPopupCoreAsync(windowHandle, cancellationToken)
            ? GalaBondDismissOutcome.Dismissed
            : GalaBondDismissOutcome.Failed;
    }

    /// <summary>
    /// 角色详情残留框关闭核心（P1-B，2026-09-09 修复批）：框外空白点击 → 复查认页。
    /// 每次点击前先认页（坑39：详情框未退出时 (700,525) 是备战页空白带，误点虽无害
    /// 但无意义；框已退出立即收手）。两击耗尽仍不退出=如实失败，绝不升级为盲点连点。
    /// 面板非模态（不吞输入），关闭失败只污染识别不妨碍输入——失败不阻断调用方流程。
    /// </summary>
    private async Task<bool> DismissCharacterDetailPopupCoreAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsCharacterDetailPopupOnScreenAsync(windowHandle, cancellationToken))
            {
                Publish(
                    "RecoveryDetailPopupDismissed",
                    "角色详情面板已不在屏（点击前认页）——视为已关闭。");
                return true;
            }

            var click = await ClickStandardPointAsync(
                windowHandle,
                $"detail_popup_dismiss_{attempt}",
                "角色详情面板：框外空白点击关闭",
                CharacterDetailDismissPoint,
                new ActionPolicy { AfterActionDelay = TimeSpan.FromMilliseconds(600) },
                cancellationToken);
            if (!click.Succeeded)
            {
                continue;
            }
        }

        var stillUp = await IsCharacterDetailPopupOnScreenAsync(windowHandle, cancellationToken);
        if (stillUp)
        {
            Publish(
                "RecoveryDetailPopupDismissFailed",
                "角色详情面板两次框外点击后仍在屏——如实失败（不盲点；识别污染交由快照复核吸收）。",
                TaskEventLevel.Warning);
        }
        return !stillUp;
    }

    private async Task<bool> IsCharacterDetailPopupOnScreenAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var frame = await capture.CaptureAsync(window, cancellationToken);
        return string.Equals(
            classifier.Classify(frame)?.PageId,
            CharacterDetailPopupPageId,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 1.2.119（审计簇 C）：统一模态弹框守卫——按需单帧实拍+分类器认页，在屏即应答。
    /// **禁用 I1 作为验页源**（I1 读识别流 LatestAnalysis：盛会弹框期按设计抑制 1.2.114、
    /// 流异常期陈旧——审计 4-1/6-7-3 的弹框场景下 I1 恒失效）。覆盖五大模态：盛会
    /// 升档框（自答）/祈愿试炼框（注入的 IWishTrialPopupHandler）/出战人数不足提示
    /// （点确认）/角色详情残留框（P1-B，点空白关闭）/列车同行伙伴选择框（识别表内
    /// companion_selection，任选点击点位未标定——本版仅报告不点击，待标定后接入）。
    /// 返回 true=检测到模态并已处理。
    /// </summary>
    public async Task<bool> DismissBlockingModalIfUpAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        var page = await ReadStablePageAsync(windowHandle, cancellationToken);
        var pageId = page?.PageId ?? string.Empty;

        if (string.Equals(pageId, GalaBondPopupPageId, StringComparison.OrdinalIgnoreCase))
        {
            await SaveGuardEvidenceAsync(windowHandle, "gala-before", cancellationToken);
            // 互斥由 DismissGalaBondPopupCoreAsync 内部的类级门统一保证（P2-1 修正：
            // 门下沉到 Core，泵/弃局链/守卫三条路径全部覆盖）。
            var galaResult = await DismissGalaBondPopupCoreAsync(
                windowHandle,
                cancellationToken);
            await SaveGuardEvidenceAsync(windowHandle, $"gala-after-{galaResult}", cancellationToken);
            return galaResult;
        }

        if (string.Equals(pageId, CharacterDetailPopupPageId, StringComparison.OrdinalIgnoreCase))
        {
            await SaveGuardEvidenceAsync(windowHandle, "detail-before", cancellationToken);
            var detailResult = await DismissCharacterDetailPopupCoreAsync(
                windowHandle,
                cancellationToken);
            await SaveGuardEvidenceAsync(windowHandle, $"detail-after-{detailResult}", cancellationToken);
            return detailResult;
        }

        if (string.Equals(pageId, "wish_trial_selection", StringComparison.OrdinalIgnoreCase))
        {
            var wishResult = wishTrialHandler is not null
                && await wishTrialHandler.DismissWishTrialPopupIfUpAsync(
                    windowHandle,
                    cancellationToken);
            await SaveGuardEvidenceAsync(windowHandle, $"wish-after-{wishResult}", cancellationToken);
            return wishResult;
        }

        if (string.Equals(
                pageId,
                "incomplete_lineup_prompt",
                StringComparison.OrdinalIgnoreCase))
        {
            await ClickStandardPointAsync(
                windowHandle,
                "modal_guard_incomplete_confirm",
                "弹框守卫：确认关闭无法出战提示",
                UncompletedPromptConfirmPoint,
                new ActionPolicy(),
                cancellationToken);
            return true;
        }

        if (string.Equals(
                pageId,
                "companion_selection",
                StringComparison.OrdinalIgnoreCase))
        {
            Publish(
                "RecoveryCompanionSelectionUntested",
                "弹框守卫：列车同行伙伴选择框在屏（任选点位未标定，本版不点击）——请上报此日志以补充标定。",
                TaskEventLevel.Warning);
            return false;
        }

        return false;
    }

    /// <summary>
    /// 盛会之星升档选择框消除核心：随便点一张角色卡 → 点「确认选择」→ 复查页。
    /// 点到卡片间隙=无选中（确认钮置灰、弹框不动），复查不过换下一候选；
    /// 全部候选耗尽仍不退出=如实失败（不升级为盲点）。复查等待含弹框退出动画。
    /// 审查 P2-1：每次点击前单帧认页——弹框已退出（含 >3s 退出动画窗）立即收手，
    /// 后续点击绝不落到已恢复的底层备战页（(970,270) 等点位在备战页语义未验证，坑39）。
    /// </summary>
    private async Task<bool> DismissGalaBondPopupCoreAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        // 1.2.119（审查 P2-1 修正）：类级门下沉到 Core 本体——弃局链/泵（经
        // DismissGalaBondPopupIfUpAsync，与本类同实例）/统一守卫三条调用路径
        // 全部串行化，杜绝并发双击。Core 自身绝不重入（无内部再等待该门路径）。
        await GalaDismissGate.WaitAsync(cancellationToken);
        try
        {
            return await DismissGalaBondPopupCoreLockedAsync(
                windowHandle,
                cancellationToken);
        }
        finally
        {
            GalaDismissGate.Release();
        }
    }

    private async Task<bool> DismissGalaBondPopupCoreLockedAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        foreach (var portrait in GalaPortraitCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsGalaBondPopupOnScreenAsync(windowHandle, cancellationToken))
            {
                Publish(
                    "RecoveryGalaPopupDismissed",
                    "盛会之星升档选择框已不在屏（点击前认页）——视为已消除。");
                return true;
            }

            var selectResult = await ClickStandardPointAsync(
                windowHandle,
                $"gala_portrait_{portrait.X}_{portrait.Y}",
                "盛会之星升档框：任选一名角色（用户口径：随便点）",
                portrait,
                new ActionPolicy { AfterActionDelay = TimeSpan.FromMilliseconds(350) },
                cancellationToken);
            if (!selectResult.Succeeded)
            {
                continue;
            }

            if (!await IsGalaBondPopupOnScreenAsync(windowHandle, cancellationToken))
            {
                Publish(
                    "RecoveryGalaPopupDismissed",
                    "盛会之星升档选择框已不在屏（选人后认页）——确认钮不再点击。");
                return true;
            }

            await ClickStandardPointAsync(
                windowHandle,
                "gala_confirm",
                "盛会之星升档框：确认选择",
                GalaConfirmPoint,
                new ActionPolicy { AfterActionDelay = TimeSpan.Zero },
                cancellationToken);

            var deadline = ActiveUtcNow + TimeSpan.FromSeconds(3);
            while (ActiveUtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await IsGalaBondPopupOnScreenAsync(windowHandle, cancellationToken))
                {
                    Publish(
                        "RecoveryGalaPopupDismissed",
                        "盛会之星升档选择框已应答关闭（任选角色+确认选择）——Esc 链可正常继续。");
                    return true;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(300),
                    cancellationToken);
            }
        }

        Publish(
            "RecoveryGalaPopupDismissFailed",
            "盛会之星升档选择框应答后仍未退出——候选点位耗尽，如实失败（不盲点）。",
            TaskEventLevel.Warning);
        return false;
    }

    /// <summary>
    /// 1.2.119 证据留存（用户令"留存所有必要证据，事后一起分析"）：弃局链关键节点
    /// 实拍落盘 abandon-evidence\——事后审计对照画面真值，验证弃局定性（快照不可得/
    /// 弹框在屏/真山穷水尽）是否属实。保存异常绝不影响弃局主流程。
    /// </summary>
    private async Task SaveAbandonEvidenceAsync(
        nint windowHandle,
        string tag,
        CancellationToken cancellationToken)
    {
        try
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(window, cancellationToken);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CurrencyWarsSmartRaccoon",
                "abandon-evidence");
            Directory.CreateDirectory(directory);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmssfff}-abandon-{tag}.png";
            frame.SavePng(Path.Combine(directory, name));
        }
        catch
        {
            // 取证保存失败不影响弃局。
        }
    }

    /// <summary>1.2.119 证据留存：弹框守卫应答前后实拍 guard-evidence\（事后验证
    /// 应答正确性——审计簇 B"应答后遗留详情框"的教训）。异常不影响守卫。</summary>
    private async Task SaveGuardEvidenceAsync(
        nint windowHandle,
        string tag,
        CancellationToken cancellationToken)
    {
        try
        {
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(window, cancellationToken);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CurrencyWarsSmartRaccoon",
                "guard-evidence");
            Directory.CreateDirectory(directory);
            frame.SavePng(Path.Combine(directory,
                $"{DateTime.Now:yyyyMMdd-HHmmssfff}-guard-{tag}.png"));
        }
        catch
        {
            // 取证保存失败不影响守卫。
        }
    }

    /// <summary>单帧认页：盛会弹框是否仍在屏（坑50；所有应答点击的击前门禁）。</summary>
    private async Task<bool> IsGalaBondPopupOnScreenAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var frame = await capture.CaptureAsync(window, cancellationToken);
        return string.Equals(
            classifier.Classify(frame)?.PageId,
            GalaBondPopupPageId,
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task<RejectedOpeningRecoveryResult> RecoverAsync(
        nint windowHandle,
        OpeningSnapshot rejectedOpening,
        OpeningFilterEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        Publish(
            "RecoveryStarted",
            $"当前开局不满足条件：{string.Join("；", evaluation.Reasons)}");

        CurrencyWarsNavigationResult? preparation = null;
        const int maximumPreparationAttempts = 3;
        for (var attempt = 1;
             attempt <= maximumPreparationAttempts;
             attempt++)
        {
            preparation = await navigator.RunAsync(
                windowHandle,
                new CurrencyWarsNavigationOptions
                {
                    StopAfterOpeningRecognition = false,
                    StopAtPreparation = true
                },
                cancellationToken);
            if (preparation.FinalState ==
                CurrencyWarsNavigationState.ReachedPreparation)
            {
                break;
            }

            Publish(
                "RecoveryActionRetry",
                $"进入 1-1 备战页第 {attempt} 次未成功：{preparation.Message}；" +
                "准备从当前已知页面继续重试。",
                TaskEventLevel.Warning);
            if (attempt < maximumPreparationAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        if (preparation is null ||
            preparation.FinalState != CurrencyWarsNavigationState.ReachedPreparation)
        {
            return Failed(
                $"无法进入 1-1 备战页：{preparation?.Message ?? "未产生导航结果"}");
        }

        // 1.2.58（独立分析 P-12）：同上——先等入场动画，Esc 重试 1→2 次。
        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
        var exitPrompt = await PressKeyUntilPageAsync(
            windowHandle,
            InputKey.Escape,
            "使用 Esc 退出当前对局",
            "abandon_settlement_prompt",
            TimeSpan.FromSeconds(4),
            2,
            cancellationToken);
        if (exitPrompt is null)
        {
            // 2026-09-04 用户令（1.2.57 补分流）：Esc 无效=非对局页，兜底点击必须
            // 按页面身份分流——与 AbandonCurrentRunAsync 同款规则。1.2.56 漏改本处，
            // 导致备战页被连点 (960,899)=出战按钮三次（15:17 专家研讨会局实况）。
            var fallbackPage = await ReadStablePageAsync(
                windowHandle,
                cancellationToken);
            var fallbackPageId = fallbackPage?.PageId ?? string.Empty;

            // 09-10 深夜班（弃局活锁实锤 03:15-03:19）：Esc 双败后确认框可能
            // 已在屏但 4s 验证窗内识别滞后未确认——稳定读即 abandon_settlement_prompt
            // 或再等 3s 重探命中，都直接走统一结算返回流程。此前两种情况都会落到
            // Failed 交外层，而外层重开时人还在局内，形成"重开→判未命中→再弃局"
            // 活锁（每轮 ~2.5 分钟，靠 NavigationFailed→决策层 A9 完整链概率逃生）。
            if (string.Equals(
                    fallbackPageId,
                    "abandon_settlement_prompt",
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    "RecoveryPromptConfirmed",
                    "Esc 双败后稳定读已确认放弃结算提示页——直接走统一结算返回流程。");
                return await CompleteFromAbandonSettlementPromptCoreAsync(
                    windowHandle,
                    cancellationToken);
            }

            var reprompt = await WaitForPageAsync(
                windowHandle,
                "abandon_settlement_prompt",
                TimeSpan.FromSeconds(3),
                cancellationToken);
            if (reprompt is not null)
            {
                Publish(
                    "RecoveryPromptConfirmed",
                    "Esc 双败后重探命中放弃结算提示页（识别滞后）——走统一结算返回流程。");
                return await CompleteFromAbandonSettlementPromptCoreAsync(
                    windowHandle,
                    cancellationToken);
            }

            // 坑50（1.2.114，审查 P2-3）：与 AbandonCurrentRunAsync 对称——本路径
            //（开局不合格弃局）遇盛会升档弹框同样先应答，否则 (960,899)×3 落在
            // 弹框上仅靠模态吞输入免祸，动画窗内则真实落在出战键附近。
            if (string.Equals(
                    fallbackPageId,
                    GalaBondPopupPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    "RecoveryGalaPopupBlocked",
                    "Esc 无效根因=盛会之星羁绊升档选择框在屏——先应答关闭再弃局。",
                    TaskEventLevel.Warning);
                await DismissGalaBondPopupCoreAsync(windowHandle, cancellationToken);
                return Failed("盛会之星升档选择框阻断弃局——已尝试应答，交由外层重试。");
            }

            if (fallbackPageId.StartsWith(
                    "preparation_",
                    StringComparison.OrdinalIgnoreCase))
            {
                // 09-10 深夜班：P-12 口径补按（完整链同款："多数失败几秒后重按即
                // 成功"）——Esc 双败后页面仍为备战页=Esc 未生效（或确认框被关闭），
                // 补发一次 Esc 再等确认框；仍无效才如实失败交外层。
                // 红线不动：此处依旧绝不点击出战区 (960,899)。
                Publish(
                    "RecoverySkipPreparationClick",
                    $"当前为备战页（{fallbackPageId}），禁止点击出战区；" +
                    "补按一次 Esc 重试后再判定。",
                    TaskEventLevel.Warning);
                await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
                var retryPrompt = await PressKeyUntilPageAsync(
                    windowHandle,
                    InputKey.Escape,
                    "使用 Esc 退出当前对局（备战页补按）",
                    "abandon_settlement_prompt",
                    TimeSpan.FromSeconds(4),
                    1,
                    cancellationToken);
                if (retryPrompt is not null)
                {
                    return await CompleteFromAbandonSettlementPromptCoreAsync(
                        windowHandle,
                        cancellationToken);
                }

                return Failed("备战页 Esc 无法弃局且禁止点击出战区；补按一次仍无效，交外层重试。");
            }

            if (fallbackPageId is "normal_hud" or "currency_wars_home")
            {
                Publish(
                    "RecoveryAlreadyAtHome",
                    $"已识别 {fallbackPageId}——弃局目标（回到主界面）已达成。");
                return RejectedOpeningRecoveryResult.Recovered(
                    "已回到货币战争主界面。");
            }

            // 1.2.58（架构审查 3-4 遗漏①）：Unknown 页禁止兜底点击（同上）。
            if (fallbackPage is null)
            {
                Publish(
                    "RecoverySkipUnknownClick",
                    "当前页面无法识别（Unknown）——禁止兜底点击，交由外层重试。",
                    TaskEventLevel.Warning);
                return Failed("页面无法识别（Unknown）；已停止兜底点击。");
            }

            Publish(
                "RecoveryFallbackStarted",
                "Esc 未进入放弃确认页，改点结算链\"下一页/保存并退出\"位并进行有限重试。",
                TaskEventLevel.Information);
            exitPrompt = await ClickUntilPageAsync(
                windowHandle,
                "exit_rejected_run",
                "保存并退出/结算推进位",
                NextPoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.FromMilliseconds(200)
                },
                "abandon_settlement_prompt",
                TimeSpan.FromSeconds(4),
                3,
                cancellationToken);
        }

        if (exitPrompt is null)
        {
            // 1.2.93（用户令，弃局原则第 4 次重申）：识别分流的链路走不通时，
            // **盲点中间直通主界面**——不管当前页面是什么（对局未完成总结页/任意
            // 结算中间页），连点推进位直到回到货币战争主界面。识别不是这里的前置
            // 条件（此前"识别不到→诚实失败→卡 Unknown 死循环"10 分钟即此病）。
            // 内联红线：回主界面即停（分类+强证据双形态）。
            Publish(
                "RecoveryBlindAdvanceStarted",
                "弃局识别链未走通——切换盲点中间直通模式：连点推进位直到回主界面。",
                TaskEventLevel.Warning);
            var blind = await BlindAdvanceToHomeAsync(windowHandle, cancellationToken);
            return blind
                ? RejectedOpeningRecoveryResult.Recovered("盲点推进已回到货币战争主界面。")
                : Failed("盲点推进 15 秒未确认回主界面；已停止输入交外层重试。");
        }

        return await CompleteFromAbandonSettlementPromptCoreAsync(
            windowHandle,
            cancellationToken);
    }

    /// <summary>
    /// 1.2.93 盲点直通主界面（用户令"弃局结算页直接盲点中间，点到回主界面为止，
    /// 不要识别这些中间页面"）：连点推进位（结算链按钮/下一页同位置），击前单帧探测
    /// 只判主界面（分类器 home/normal_hud 与强证据兜底，1.2.95 审查 P3-3 对齐推进段
    /// 早停强度），主界面即停=成功。上限 15 秒。
    /// 刻意不识别其他中间页——弃局场景的中间页族无需辨认（用户弃局原则）。
    /// </summary>
    private async Task<bool> BlindAdvanceToHomeAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + TimeSpan.FromSeconds(15);
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 1.2.95 审查 P3-3 加固（与结算推进段同款）：击前主页检查——已回主界面
            // 绝不再点（1.2.63 红线）；早停含 normal_hud 与强证据兜底（与基线强度对齐）。
            // 1.2.97 补备战页急停：本函数使用面扩大到 Esc 链耗尽后的统计页场景，
            // 盲点推进位在备战页上是出战按钮——探测到立即停手交外层。
            var probeWindow = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var probeFrame = await capture.CaptureAsync(probeWindow, cancellationToken);
            var probePage = classifier.Classify(probeFrame);
            if (probePage?.PageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase) == true)
            {
                Publish(
                    "RecoveryBlindAdvanceAbortPreparation",
                    $"盲点推进探测到备战页（{probePage.PageId}）——立即停止（备战页绝不点击推进位）。",
                    TaskEventLevel.Warning);
                return false;
            }

            // 坑50（1.2.114）：盲点推进位 (960,899) 在盛会之星升档选择框上的语义
            // 未经验证（模态吞输入）——先尝试应答关闭；关不掉就停手交外层，不盲点。
            if (string.Equals(
                    probePage?.PageId,
                    GalaBondPopupPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Publish(
                    "RecoveryBlindAdvanceGalaPopup",
                    "盲点推进探测到盛会之星升档选择框——先应答关闭（任选一名角色+确认选择）。",
                    TaskEventLevel.Warning);
                if (!await DismissGalaBondPopupCoreAsync(
                        windowHandle,
                        cancellationToken))
                {
                    return false;
                }

                continue;
            }

            if (probePage?.PageId is "currency_wars_home" or "normal_hud"
                || (probePage is null && CurrencyWarsHomeEvidence.IsMatch(probeFrame)))
            {
                // F5（指南风暴 09-10，缺陷 C 落点观测）：落 normal_hud=盲点直通把
                // 货币战争模式整个退掉了（落在游戏大厅而非货币战争主界面）——
                // 1.2.126 后 M8 指南链可自动导回，此处响亮留痕供审计区分两种落点。
                var landedOnGameHome = string.Equals(
                    probePage?.PageId,
                    "normal_hud",
                    StringComparison.OrdinalIgnoreCase);
                Publish(
                    "RecoveryBlindAdvanceHomeConfirmed",
                    probePage is not null
                        ? $"盲点推进确认回到{(landedOnGameHome ? "游戏主界面（normal_hud——货币战争模式已被退出，M8 指南链将自动导回）" : "货币战争主界面")}（{probePage.PageId}，{probePage.Confidence:P1}）。"
                        : "盲点推进确认回到货币战争主界面（强证据兜底）。",
                    landedOnGameHome ? TaskEventLevel.Warning : TaskEventLevel.Information);
                return true;
            }

            await ClickStandardPointAsync(
                windowHandle,
                "blind_advance_next",
                "盲点推进（中下部）",
                NextPoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.Zero
                },
                cancellationToken);
            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }

        return false;
    }

    public async Task<RejectedOpeningRecoveryResult>
        CompleteFromAbandonSettlementPromptAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        _pauseBaseline = foregroundGuard.TotalPausedDuration;
        var prompt = await WaitForPageAsync(
            windowHandle,
            "abandon_settlement_prompt",
            TimeSpan.FromSeconds(4),
            cancellationToken);
        if (prompt is null)
        {
            return Failed(
                "复用放弃结算恢复前未稳定确认 abandon_settlement_prompt；未发送危险输入。");
        }

        Publish(
            "RecoveryPromptConfirmed",
            "已稳定确认放弃结算提示页，复用统一结算返回主页流程。");
        return await CompleteFromAbandonSettlementPromptCoreAsync(
            windowHandle,
            cancellationToken);
    }

    private async Task<RejectedOpeningRecoveryResult>
        CompleteFromAbandonSettlementPromptCoreAsync(
            nint windowHandle,
            CancellationToken cancellationToken)
    {

        PageClassificationResult? challengeFailed = null;
        var settleClickSucceeded = false;
        // 1.2.91（用户令点法 2026-09-05 晚）："放弃并结算"（750,744 中偏下）连点
        // **6 秒窗口、0.5 秒探测间隔、单轮**（strike≥1 只探不击）。连点内单帧快探：
        // challenge_failed（连续 2 帧）/主页（分类或强证据）→提前收；
        // 备战页→立即停手（弃局未生效）。
        {
            var settleClick = await ClickStandardPointAsync(
                windowHandle,
                "abandon_and_settle",
                "放弃并结算",
                AbandonAndSettlePoint,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.Zero
                },
                cancellationToken);
            if (!settleClick.Succeeded)
            {
                Publish(
                    "RecoveryStrategyCycle",
                    "放弃结算点击输入失败。",
                    TaskEventLevel.Warning);
            }
            else
            {
                settleClickSucceeded = true;
                // 1.2.101（用户令点法重申）：放弃并结算**只点一次**——首击后不再点击，
                // 只探测等挑战失败页出现（此前交替连点会打扰双帧确认且无必要）。
                var rapidDeadline = ActiveUtcNow + TimeSpan.FromSeconds(6);
                var challengeStrikes = 0;
                while (ActiveUtcNow < rapidDeadline && challengeFailed is null)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(500),
                        cancellationToken);

                    var probeWindow = await foregroundGuard.WaitUntilForegroundAsync(
                        windowHandle,
                        cancellationToken);
                    var probeFrame = await capture.CaptureAsync(probeWindow, cancellationToken);
                    var page = classifier.Classify(probeFrame);

                    if (page?.PageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Publish(
                            "RecoveryRapidClickAbortPreparation",
                            "探测到备战页——弃局未生效，停止推进（备战页绝不点击）。",
                            TaskEventLevel.Warning);
                        return Failed("弃局期间回到备战页；已停止推进交外层处理。");
                    }

                    if (page is not null &&
                        string.Equals(page.PageId, "challenge_failed", StringComparison.OrdinalIgnoreCase))
                    {
                        challengeStrikes++;
                        if (challengeStrikes >= 2)
                        {
                            challengeFailed = page;
                            Publish(
                                "RecoveryPromptConfirmed",
                                $"已进入挑战失败页（{page.Confidence:P1}，连续 {challengeStrikes} 帧）。");
                        }
                    }
                    else
                    {
                        challengeStrikes = 0;
                    }

                    if (page?.PageId is "currency_wars_home" or "normal_hud"
                        || (page is null && CurrencyWarsHomeEvidence.IsMatch(probeFrame)))
                    {
                        challengeFailed = new PageClassificationResult(
                            "challenge_failed",
                            "challenge_failed_via_home",
                            0.95,
                            []);
                        Publish(
                            "RecoveryCompletedEarlyHome",
                            "探测期间已回到货币战争主界面（结算直接完成）——停止推进。");
                    }
                }

                if (challengeFailed is null)
                {
                    Publish(
                        "RecoveryStrategyCycle",
                        "放弃结算后 6 秒未探测到挑战失败页。",
                        TaskEventLevel.Warning);
                }
            }
        }

        if (challengeFailed is null)
        {
            // 1.2.95 审查 P3-4：初始点击输入失败与连点未探测是两种事实，文案区分。
            if (!settleClickSucceeded)
            {
                return Failed("放弃并结算初始点击输入失败，连点推进未启动。");
            }

            // 1.2.100（实弹 09:57/09:54 两轮复现）：6 秒窗内没探到挑战失败页=游戏停在
            // 结算链任意中间页（放弃弹框确认链/总结页族）——此前直接 Failed 交外层，
            // 引擎 M8 导航对 Unknown 空转 1-2 分钟才被恢复层救回。按 rule 四.22（中间页
            // 不识别、盲点到回主界面）超时即直通，压缩恢复延迟到秒级。
            Publish(
                "RecoverySettleTimeoutBlindAdvance",
                "放弃结算连点 6 秒未探测到挑战失败页——按弃局原则盲点直通主界面（不识别中间页）。",
                TaskEventLevel.Warning);
            if (await BlindAdvanceToHomeAsync(windowHandle, cancellationToken))
            {
                return RejectedOpeningRecoveryResult.Recovered(
                    "放弃结算超时后盲点推进已回到货币战争主界面。");
            }

            return Failed("放弃并结算连点 6 秒未探测到挑战失败页；盲点直通也未确认回主界面。");
        }

        // 1.2.91 复审 P1：经主页收敛（challenge_failed_via_home 合成态）=已回主界面，
        // **绝不再点任何位置**（16:32 红线：主界面点击=弹系统菜单/退出模式）——
        // 直接带 returnedHome 走统一验证。
        var convergedViaHome = challengeFailed is not null
            && challengeFailed.DisplayName == "challenge_failed_via_home";
        var returnedHome = convergedViaHome;
        if (challengeFailed is not null && !convergedViaHome)
        {
            // 1.2.102（实弹修正）：用户口径"页面中间偏下的下一页"=(960,899)（正中偏底，
            // 1.2.100 盲点直通同点位 43/43 实弹全成功；1.2.101 误映射 750,744 导致 8/8
            // 弃局烧满 15 秒）。看到挑战失败页后一直连点 (960,899) 直到主页——不加任何
            // 其他操作。击前主页/备战页急停保留（1.2.63 红线）；15 秒上限兜底。
            var advanceDeadline = ActiveUtcNow + TimeSpan.FromSeconds(15);
            while (ActiveUtcNow < advanceDeadline && !cancellationToken.IsCancellationRequested)
            {
                var probeWindow = await foregroundGuard.WaitUntilForegroundAsync(
                    windowHandle,
                    cancellationToken);
                var probeFrame = await capture.CaptureAsync(probeWindow, cancellationToken);
                var page = classifier.Classify(probeFrame);

                if (page?.PageId is "currency_wars_home" or "normal_hud"
                    || (page is null && CurrencyWarsHomeEvidence.IsMatch(probeFrame)))
                {
                    Publish(
                        "RecoveryCompletedEarlyHome",
                        "结算推进连点确认回到货币战争主界面——停止连点。");
                    returnedHome = true;
                    break;
                }

                if (page?.PageId.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Publish(
                        "RecoveryAdvanceAbortPreparation",
                        $"结算推进探测到备战页（{page.PageId}）——立即停止（备战页绝不点击推进位）。",
                        TaskEventLevel.Warning);
                    return Failed("结算推进期间回到备战页；已停止推进交外层处理。");
                }

                await ClickStandardPointAsync(
                    windowHandle,
                    "settlement_next_advance_rapid",
                    "结算下一页连点（中偏下）",
                    NextPoint,
                    new ActionPolicy
                    {
                        AfterActionDelay = TimeSpan.Zero
                    },
                    cancellationToken);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
        }
        else if (convergedViaHome)
        {
            returnedHome = true;
        }

        const string message = "已放弃不合格开局并返回货币战争主界面。";
        if (!returnedHome)
        {
            return Failed(
                "结算下一页连点 15 秒后仍未确认回到主界面；输入已停止，交外层恢复。");
        }

        Publish("RecoveryCompleted", message);
        return RejectedOpeningRecoveryResult.Recovered(message);
    }

    private async Task<ActionResult> ClickStandardPointAsync(
        nint windowHandle,
        string id,
        string displayName,
        StandardPoint point,
        ActionPolicy policy,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);

        var mapped = new PixelPoint(
            (int)Math.Round(point.X * window.ClientArea.Width / (double)ReferenceWidth),
            (int)Math.Round(point.Y * window.ClientArea.Height / (double)ReferenceHeight));
        var bounds = new PixelRect(
            Math.Clamp(mapped.X - 3, 0, Math.Max(0, window.ClientArea.Width - 6)),
            Math.Clamp(mapped.Y - 3, 0, Math.Max(0, window.ClientArea.Height - 6)),
            6,
            6);
        Publish("RecoveryAction", $"准备执行：{displayName}");
        return await input.ClickAsync(
            new ClickTarget(id, displayName, window, bounds),
            policy,
            cancellationToken);
    }

    private async Task<PageClassificationResult?> ClickUntilPageAsync(
        nint windowHandle,
        string actionId,
        string displayName,
        StandardPoint point,
        ActionPolicy policy,
        string expectedPageId,
        TimeSpan verificationTimeout,
        int maximumAttempts,
        CancellationToken cancellationToken,
        string? requiredPageId = null)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requiredPageId is not null &&
                await WaitForPageAsync(
                    windowHandle,
                    requiredPageId,
                    TimeSpan.FromSeconds(2),
                    cancellationToken) is null)
            {
                Publish(
                    "RecoveryActionPageMismatch",
                    $"执行“{displayName}”前未稳定确认 {requiredPageId}；" +
                    "本次不发送危险输入。",
                    TaskEventLevel.Warning);
                return null;
            }

            Publish(
                "RecoveryActionAttempt",
                $"执行“{displayName}”：尝试 {attempt}/{maximumAttempts}。");
            var action = await ClickStandardPointAsync(
                windowHandle,
                $"{actionId}_{attempt}",
                displayName,
                point,
                policy,
                cancellationToken);
            if (action.Succeeded)
            {
                var detected = await WaitForPageAsync(
                    windowHandle,
                    expectedPageId,
                    verificationTimeout,
                    cancellationToken);
                if (detected is not null)
                {
                    return detected;
                }
            }

            var failure = action.Succeeded
                ? $"游戏没有进入预期页面“{expectedPageId}”"
                : action.Message;
            Publish(
                "RecoveryActionRetry",
                $"“{displayName}”第 {attempt}/{maximumAttempts} 次未成功：{failure}；" +
                (attempt < maximumAttempts ? "准备重试。" : "已达到重试上限。"),
                TaskEventLevel.Warning);
            if (attempt < maximumAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
        }

        return null;
    }

    // 1.2.101 审查 P3-3：WaitForKnownSettlementPageAsync 已删（统一验证窗移除后零调用者）；
    // RewardSettlementDetailEvidence 保留（测试公用+盲点段潜在取证价值）。

    private async Task<PageClassificationResult?> PressKeyUntilPageAsync(
        nint windowHandle,
        InputKey key,
        string displayName,
        string expectedPageId,
        TimeSpan verificationTimeout,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            Publish(
                "RecoveryKeyAttempt",
                $"执行按键方案“{displayName}”：尝试 {attempt}/{maximumAttempts}。");
            var action = await input.PressKeyAsync(
                window,
                key,
                new ActionPolicy
                {
                    AfterActionDelay = TimeSpan.Zero // 1.2.94：后接 4s 页面轮询才是真闸门
                },
                cancellationToken);
            if (action.Succeeded)
            {
                var detected = await WaitForPageAsync(
                    windowHandle,
                    expectedPageId,
                    verificationTimeout,
                    cancellationToken);
                if (detected is not null)
                {
                    Publish(
                        "RecoveryKeySucceeded",
                        $"按键方案“{displayName}”成功，已进入预期页面。");
                    return detected;
                }
            }

            Publish(
                "RecoveryKeyRetry",
                $"按键方案“{displayName}”第 {attempt}/{maximumAttempts} 次未成功；" +
                (attempt < maximumAttempts ? "准备重试。" : "已达到重试上限。"),
                TaskEventLevel.Warning);
            if (attempt < maximumAttempts)
            {
                // 1.2.89 Esc 纪律（用户令 2026-09-05 第 5 问题）：补按前必须先认页——
                // ①预期弹框其实已开（识别慢/漏帧）时再按=把弹框关掉，改为继续等待；
                // ②已在主界面时再按=退出货币战争模式（16:32 实锤弹到游戏本体），立即停手；
                // ③Unknown（弹框疑似开着）不补按，继续等待；
                // ④祈愿试炼弹框不按 Esc（模态，需 M3 应答），继续等待；
                // ⑤其余已分类页（reward_shop/enemy_overview/challenge_failed 等）允许补按
                //   （Esc 关店等是合法用途）——注释口径以此为准（审查 P3-3 修正）。
                var currentPageBeforeRetry = await ReadStablePageAsync(
                    windowHandle,
                    cancellationToken);
                var currentPageId = currentPageBeforeRetry?.PageId ?? string.Empty;
                if (string.Equals(currentPageId, expectedPageId, StringComparison.OrdinalIgnoreCase))
                {
                    Publish(
                        "RecoveryKeyPageConfirmedLate",
                        $"页面其实已是预期页（识别滞后）——不补按，继续等待稳定确认。");
                    var lateDetected = await WaitForPageAsync(
                        windowHandle,
                        expectedPageId,
                        verificationTimeout,
                        cancellationToken);
                    if (lateDetected is not null)
                    {
                        Publish(
                            "RecoveryKeySucceeded",
                            $"按键方案“{displayName}”成功（延迟确认）。");
                        return lateDetected;
                    }

                    continue;
                }

                if (currentPageId is "normal_hud" or "currency_wars_home")
                {
                    Publish(
                        "RecoveryKeyAbortAtHome",
                        "已识别货币战争主界面——停止按键方案（防止 Esc 退出模式）。",
                        TaskEventLevel.Warning);
                    return null;
                }

                if (currentPageBeforeRetry is null
                    || currentPageId.StartsWith("wish_trial", StringComparison.OrdinalIgnoreCase))
                {
                    Publish(
                        "RecoveryKeySkipRetryUnknown",
                        currentPageBeforeRetry is null
                            ? "当前页面无法识别（疑似弹框开着）——不补按 Esc，继续等待识别。"
                            : "检测到祈愿试炼弹框——Esc 无效且有害，不补按，继续等待识别。",
                        TaskEventLevel.Warning);
                    var graceDetected = await WaitForPageAsync(
                        windowHandle,
                        expectedPageId,
                        verificationTimeout,
                        cancellationToken);
                    if (graceDetected is not null)
                    {
                        return graceDetected;
                    }

                    continue;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
        }

        return null;
    }

    private async Task<PageClassificationResult?> WaitForPageAsync(
        nint windowHandle,
        string expectedPageId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = ActiveUtcNow + timeout;
        var stability = new ConsecutiveObservationTracker<string>(
            2,
            StringComparer.OrdinalIgnoreCase);
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);

            var frame = await capture.CaptureAsync(window, cancellationToken);
            var detected = classifier.Classify(frame);
            if (detected is not null &&
                string.Equals(
                    detected.PageId,
                    expectedPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (stability.Observe(detected.PageId))
                {
                    Publish(
                        "RecoveryPageRecognized",
                        $"已识别：{detected.DisplayName}（{detected.Confidence:P1}）");
                    return detected;
                }
            }
            else
            {
                stability.Reset();
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private RejectedOpeningRecoveryResult Failed(string message)
    {
        Publish("RecoveryFailed", message, TaskEventLevel.Error);
        return RejectedOpeningRecoveryResult.Failed(message);
    }

    /// <summary>通用稳定页读取：连续 2 帧同一已知页面才算数（兜底分流依据）。</summary>
    private async Task<PageClassificationResult?> ReadStablePageAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        string? stablePageId = null;
        var stableCount = 0;
        var deadline = ActiveUtcNow + TimeSpan.FromSeconds(3);
        while (ActiveUtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(window, cancellationToken);
            var detected = classifier.Classify(frame);
            if (detected is not null &&
                string.Equals(
                    stablePageId,
                    detected.PageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                stableCount++;
            }
            else
            {
                stablePageId = detected?.PageId;
                stableCount = detected is null ? 0 : 1;
            }

            if (detected is not null && stableCount >= 2)
            {
                return detected;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private void Publish(
        string code,
        string message,
        TaskEventLevel level = TaskEventLevel.Information) =>
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            level,
            code,
            message));
}


