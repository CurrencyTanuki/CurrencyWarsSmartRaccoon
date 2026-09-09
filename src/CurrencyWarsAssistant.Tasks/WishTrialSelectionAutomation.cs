using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>祈愿试炼弹框处理结果状态。</summary>
public enum WishTrialSelectionStatus
{
    /// <summary>未检测到祈愿试炼弹框（当前不在该页面，无需处理）。</summary>
    NotDetected,
    /// <summary>成功识别两个试炼（含名称）。</summary>
    SelectionDetected,
    /// <summary>已按策略选择并确认，弹框已退出（或进入二次确认后成功）。</summary>
    Confirmed,
    /// <summary>弹框出现但名称 OCR 未能读出。</summary>
    RecognitionFailed,
    /// <summary>点击输入失败。</summary>
    InputFailed
}

/// <summary>祈愿试炼弹框中识别到的信息。坐标均为 2K(2559×1439) 参考。</summary>
public sealed record WishTrialSelectionInfo(
    string? LeftName,
    string? RightName,
    PixelRect LeftOptionBounds,
    PixelRect RightOptionBounds,
    // 2026-08-26 新增：左右两个祈愿试炼的「获得奖励物品名」。
    // 上方识别框读到的奖励物品名（如 Archer/金币/五费聘用书）。null=未读出。
    string? LeftReward = null,
    string? RightReward = null);

/// <summary>点一次「确认选择」并复查弹框后的结果。</summary>
public enum WishTrialConfirmOutcome
{
    /// <summary>点确认后弹框已退出。</summary>
    Exited,
    /// <summary>点确认后弹框仍在（需重试：再点目标试炼 + 再点确认）。</summary>
    StillOpen,
    /// <summary>确认按钮点击输入失败。</summary>
    InputFailed
}

/// <summary>
/// 命运圣杯「祈愿试炼」弹框的自动识别与选择。
/// <para>
/// 背景：每次激活命运圣杯羁绊新等级时，约 3 秒后自动弹出 2 选 1 的祈愿试炼弹框，
/// 期间无法其它操作。本类负责：①检测弹框出现（页面分类器识别 wish_trial_selection）；
/// ②识别左右两个试炼的名称（PpOcr 读名称框）；③按策略点击其中一个选项、
/// 点「确认选择」按钮，直到弹框退出。
/// </para>
/// <para>
/// 坐标均为 2K(2559×1439) 参考（用户 2026-08-23 在 2559×1439 实机红框标定），
/// 运行时按实际窗口等比缩放。选择"哪个更合适"的具体规则由调用方传入
/// <see cref="WishTrialSelectionStrategy.Select"/>（本轮默认选中左侧，规则
/// 若何后续细化）。
/// </para>
/// </summary>
public sealed class WishTrialSelectionAutomation(
    IGameCapture capture,
    IAutomationPageClassifier pageClassifier,
    PpOcrOfflineOcr ocr,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    ITaskEventSink eventSink) : IWishTrialPopupHandler
{
    /// <summary>祈愿试炼弹框页面 id。</summary>
    public const string SelectionPageId = "wish_trial_selection";

    // ---- 2K(2559×1439) 参考坐标（用户 2026-08-23 红框图实（录）标定） ----

    /// <summary>左侧试炼选项可点击矩形。</summary>
    private static readonly PixelRect LeftOption2K = new(617, 245, 589, 447);
    /// <summary>右侧试炼选项可点击矩形。</summary>
    private static readonly PixelRect RightOption2K = new(1583, 244, 592, 447);
    /// <summary>「确认选择」按钮矩形（点击后弹框退出，可能需再点一次二次确认）。</summary>
    private static readonly PixelRect ConfirmButton2K = new(1749, 818, 499, 73);

    // ---- 名称识别框（2K 参考，红框图内的小扁框，软件只需识别这里面的名称） ----

    private static readonly PixelRect LeftNameBox2K = new(637, 596, 553, 80);
    private static readonly PixelRect RightNameBox2K = new(1600, 596, 560, 75);

    // ---- 奖励物品名识别框（2K 参考，2026-08-26 用户实机红框标定） ----
    // 每个祈愿试炼卡片「中上部」的扁平红框：框住的是「获得奖励物品名字」。
    // 左卡片奖励框（框住 Archer）、右卡片奖励框（框住 金币）。真实文字会替换为 五费聘用书 等。
    private static readonly PixelRect LeftRewardBox2K = new(798, 414, 228, 46);
    private static readonly PixelRect RightRewardBox2K = new(1775, 410, 212, 51);

    /// <summary>最近一次检测到的祈愿试炼弹框信息（读到的左右名称 + 选项矩形）。
    /// 供「1-3 三星五费」readSnapshot 组装决策快照时读取（纯新增，不改识别逻辑）。
    /// 未检测到弹框时为 null。</summary>
    private WishTrialSelectionInfo? _latestTrialInfo;

    /// <summary>最近一次检测到的祈愿试炼左右名称（null=未检测到/已退出弹框）。
    /// 供决策层读取 LeftTrial/RightTrial 事实。</summary>
    public WishTrialSelectionInfo? LatestTrialInfo => _latestTrialInfo;

    /// <summary>清除最近试炼信息。弹框未出现（NotDetected）或确认退出（Confirmed）时由本组件内部调用，避免残留。</summary>
    public void ClearLatestTrialInfo() => _latestTrialInfo = null;

    /// <summary>处理祈愿试炼弹框：若弹框出现则读名称并按策略选择。</summary>
    /// <param name="select">
    /// 选择策略：给定左右名称，返回要选哪一个（&lt;=0 点左，&gt;0 点右，<c>null</c> = 不点击）。
    /// null 返回值用于"识别不全/条件不满足、绝不误点"的防御路径（2026-08-29 定稿决策树 F2/F10 防御）；
    /// 返回 null 时本方法以 <see cref="WishTrialSelectionStatus.RecognitionFailed"/> 结束且不点击。
    /// 不传 select 时用内置 PickWinningSide（两侧都不可达成 → 不点击）。
    /// </param>
    /// <summary>
    /// 1.2.119（审计簇 C）：IWishTrialPopupHandler 实现——祈愿试炼弹框在屏则应答
    /// （检测/选侧/确认全部由本类 TryHandleSelectionAsync 完成，不在屏=零点击零
    /// 副作用，1.2.106 已证可靠）。返回 true=弹框已应答退出（Confirmed）。
    /// </summary>
    public async Task<bool> DismissWishTrialPopupIfUpAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var status = await TryHandleSelectionAsync(
            windowHandle,
            cancellationToken);
        return status == WishTrialSelectionStatus.Confirmed;
    }

    // P3-1（对抗审查 1.2.119 复核）：祈愿应答并发双击防护——引擎弹框守卫（IModalGuard，
    // P1-1 接线后激活）与操作层 M3/两处泵可能并发进入应答，双重点击会落到底层页面。
    // 本类为 DI 单例，实例信号量即全局限（gala 侧 recovery 为 Transient 故用 static 门）。
    private readonly SemaphoreSlim _wishSelectionGate = new(1, 1);

    public async Task<WishTrialSelectionStatus> TryHandleSelectionAsync(
        nint windowHandle,
        CancellationToken cancellationToken,
        Func<string?, string?, int?>? select = null)
    {
        await _wishSelectionGate.WaitAsync(cancellationToken);
        try
        {
            return await TryHandleSelectionCoreAsync(windowHandle, cancellationToken, select);
        }
        finally
        {
            _wishSelectionGate.Release();
        }
    }

    private async Task<WishTrialSelectionStatus> TryHandleSelectionCoreAsync(
        nint windowHandle,
        CancellationToken cancellationToken,
        Func<string?, string?, int?>? select)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var frame = await capture.CaptureAsync(window, cancellationToken);
        var page = pageClassifier.Classify(frame);
        if (!string.Equals(
                page?.PageId,
                SelectionPageId,
                StringComparison.OrdinalIgnoreCase))
        {
            eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "WishTrialNotDetected",
                $"页面={page?.PageId ?? "Unknown"}；未检测到祈愿试炼弹框，无需处理。"));
            // 2026-08-26：弹框未出现/已退出，清空残留信息，避免 LatestTrialInfo 携带陈旧试炼/奖励名。
            ClearLatestTrialInfo();
            return WishTrialSelectionStatus.NotDetected;
        }

        // 识别左右试炼名称。
        var leftName = await ReadNameAsync(frame, LeftNameBox2K, cancellationToken);
        var rightName = await ReadNameAsync(frame, RightNameBox2K, cancellationToken);
        // 2026-08-26 新增：识别左右「获得奖励物品名」（中上部奖励框）。
        var leftReward = await ReadNameAsync(frame, LeftRewardBox2K, cancellationToken);
        var rightReward = await ReadNameAsync(frame, RightRewardBox2K, cancellationToken);
        var info = new WishTrialSelectionInfo(
            leftName,
            rightName,
            LeftOption2K,
            RightOption2K,
            leftReward,
            rightReward);
        _latestTrialInfo = info;
        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            "WishTrialSelectionDetected",
            $"祈愿试炼弹框已识别；左侧=[{leftName ?? "未读出"}]/奖励=[{leftReward ?? "未读出"}]，" +
            $"右侧=[{rightName ?? "未读出"}]/奖励=[{rightReward ?? "未读出"}]。"));

        if (leftName is null && rightName is null)
        {
            // 09-10 夜审 P1-A 同族：双名 OCR 全空时此前"仅记录不点击"同样会让强制
            // 二选一弹框永久卡死。F9 盲选左本就是用户拍板的合法动作（不识别直接选左），
            // 此处按盲选左继续走选卡+确认，事件降级记录。
            eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "WishTrialNameRecognitionFailed",
                "两个试炼名称均未能通过 OCR 读出；按 F9 盲选左继续（弹框强制二选一，不点=卡死；本事件保持 Warning 级留痕，行为降级发生在 NoWinningSide 侧）。"));
        }

        // 选择策略：
        //   - 调用方传了 select（现有路径，如决策层 TrialToChoose）→ 直接按其决定选哪侧。
        //   - 调用方没传 select（2026-08-26 新增组件内置判定）→ 用 PickWinningSide 自动选出
        //     "可达成目标"的试炼（奇迹代偿/无限之釜只看名；令咒决议类须名+五费聘用书同对，OCR 差一字容错）。
        //     两侧都匹配取左；都没匹配返回 null -> 不点击（避免误选），交上层。
        int? side;
        if (select is not null)
        {
            side = select(leftName, rightName);
            if (side is null)
            {
                eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Warning,
                    "WishTrialSelectAbstained",
                    $"选择策略弃权（左=[{leftName ?? "未读出"}]/奖励=[{leftReward ?? "未读出"}]，" +
                    $"右=[{rightName ?? "未读出"}]/奖励=[{rightReward ?? "未读出"}]）；不点击，等待识别重试。"));
                // 保留 LatestTrialInfo 供调用方重试判定（弹框仍在屏上），不清空。
                return WishTrialSelectionStatus.RecognitionFailed;
            }
        }
        else
        {
            side = PickWinningSide(leftName, leftReward, rightName, rightReward);
            if (side is null)
            {
                // 09-10 夜审 P1-A（C4 实锤）：无关键试炼时此前"不点击"会让强制二选一
                // 弹框永久卡死（带活局等死 4 分 34 秒实拍）。rule 四.7 用户拍板 M3 优先级
                // 链末端="一般选左"——任何弹框都必须给出一个侧，此处回落选左+确认。
                side = 0;
                eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Information,
                    "WishTrialNoWinningSide",
                    $"未命中可达成目标的祈愿试炼（左=[{leftName ?? "未读出"}]/奖励=[{leftReward ?? "未读出"}]，" +
                    $"右=[{rightName ?? "未读出"}]/奖励=[{rightReward ?? "未读出"}]）；按 rule 四.7 兜底选左+确认（不再空等）。"));
            }
        }

        var target = (side <= 0) ? LeftOption2K : RightOption2K;
        var targetDisplay = (side <= 0)
            ? (leftName ?? "左侧试炼")
            : (rightName ?? "右侧试炼");

        // 重试循环：(先点选目标试炼 → 再点一次「确认选择」 → 复查弹框是否退出)。
        // 若复查发现弹框仍在（例如点击时正处动画/输入未完全生效），再点一次目标试炼
        // + 一次确认，直到弹框退出或达到上限。确认本身只需点一次，不要在同轮重复点确认。
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var currentWindow = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var currentFrame = await capture.CaptureAsync(currentWindow, cancellationToken);
            var currentPage = pageClassifier.Classify(currentFrame);
            if (!string.Equals(
                    currentPage?.PageId,
                    SelectionPageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Information,
                    "WishTrialPopupExited",
                    $"祈愿试炼弹框已退出（页面={currentPage?.PageId ?? "Unknown"}）。"));
                ClearLatestTrialInfo();  // 弹框已退出，清空残留，避免 LatestTrialInfo 携带已确认试炼/奖励名。
                return WishTrialSelectionStatus.Confirmed;
            }

            var selected = await ClickRectCenterAsync(
                currentWindow,
                target,
                $"选择{targetDisplay}",
                cancellationToken);
            if (!selected)
            {
                return WishTrialSelectionStatus.InputFailed;
            }

            var confirmOutcome = await ClickOnceConfirmAndProbeExitAsync(
                currentWindow,
                cancellationToken);
            if (confirmOutcome == WishTrialConfirmOutcome.Exited)
            {
                ClearLatestTrialInfo();  // 弹框已退出，清空残留。
                return WishTrialSelectionStatus.Confirmed;
            }

            if (confirmOutcome == WishTrialConfirmOutcome.InputFailed)
            {
                return WishTrialSelectionStatus.InputFailed;
            }

            // StillOpen：弹框仍在，进入下一轮（再点目标试炼 + 再点一次确认）。
            eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "WishTrialSelectionRetry",
                $"第 {attempt} 次(选试炼+确认)后弹框仍在；重试一次。"));
        }

        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Warning,
            "WishTrialSelectionMaxAttempts",
            $"已重试 {maximumAttempts} 次(选目标试炼+确认)弹框仍未退出，停止以避免反复点击。"));
        return WishTrialSelectionStatus.InputFailed;
    }

    /// <summary>点一次「确认选择」，并复查弹框是否已退出。</summary>
    /// <remarks>
    /// 用户 2026-08-23 澄清：只需点一次确认，弹框即消失，不需要第二次。
    /// <summary>点一次「确认选择」，并复查弹框是否退出。</summary>
    /// <remarks>
    /// 用户 2026-08-23 澄清：确认只需点一次，弹框即消失；不必在同一轮重复点确认。
    /// 若复查发现弹框仍在，返回 <see cref="WishTrialConfirmOutcome.StillOpen"/>，
    /// 由外层重试循环"再点一次目标试炼 + 再点一次确认"。
    /// </remarks>
    private async Task<WishTrialConfirmOutcome> ClickOnceConfirmAndProbeExitAsync(
        GameWindowInfo window,
        CancellationToken cancellationToken)
    {
        var frame = await capture.CaptureAsync(window, cancellationToken);
        var page = pageClassifier.Classify(frame);
        if (!string.Equals(
                page?.PageId,
                SelectionPageId,
                StringComparison.OrdinalIgnoreCase))
        {
            eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Information,
                "WishTrialAlreadyExited",
                $"点击确认前祈愿试炼弹框已退出（页面={page?.PageId ?? "Unknown"}）。"));
            return WishTrialConfirmOutcome.Exited;
        }

        var clicked = await ClickRectCenterAsync(
            window,
            ConfirmButton2K,
            "确认选择",
            cancellationToken);
        if (!clicked)
        {
            eventSink.Publish(new TaskEvent(
                DateTimeOffset.Now,
                TaskEventLevel.Warning,
                "WishTrialConfirmInputFailed",
                "「确认选择」按钮点击输入未成功发送。"));
            return WishTrialConfirmOutcome.InputFailed;
        }

        // 等弹框更新，复查是否已退出（只需一次确认即退出）。
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        frame = await capture.CaptureAsync(window, cancellationToken);
        page = pageClassifier.Classify(frame);
        if (string.Equals(
                page?.PageId,
                SelectionPageId,
                StringComparison.OrdinalIgnoreCase))
        {
            // 仍在弹框：可能是点击时动画/输入未生效，交由外层重试（再选再确认）。
            return WishTrialConfirmOutcome.StillOpen;
        }

        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            "WishTrialPopupExited",
            $"点一次「确认选择」后祈愿试炼弹框已退出（页面={page?.PageId ?? "Unknown"}）。"));
        return WishTrialConfirmOutcome.Exited;
    }

    private async Task<string?> ReadNameAsync(
        CaptureFrame frame,
        PixelRect box2K,
        CancellationToken cancellationToken)
    {
        // IOfflineOcr.RecognizeAsync 期望 frame 绝对像素坐标；把 2K 参考缩放到当前帧。
        var region = Map2KToWindow(box2K, frame);
        var result = await ocr.RecognizeAsync(frame, region, cancellationToken);
        var text = result?.Text?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task<bool> ClickRectCenterAsync(
        GameWindowInfo window,
        PixelRect rect2K,
        string displayName,
        CancellationToken cancellationToken)
    {
        var center = new PixelPoint(
            rect2K.X + rect2K.Width / 2,
            rect2K.Y + rect2K.Height / 2);
        var point = Map2KToWindow(center, window.ClientArea.Width, window.ClientArea.Height);
        var target = new ClickTarget(
            "wish_trial_click",
            displayName,
            window,
            BoundsAround(window, point));
        var action = await input.ClickAsync(
            target,
            new ActionPolicy
            {
                AfterActionDelay = TimeSpan.FromMilliseconds(300),
                VerifyForegroundBeforeClick = true
            },
            cancellationToken);
        return action.Succeeded;
    }

    /// <summary>把 2K(2559×1439) 参考矩形缩放到实际帧像素。</summary>
    private static PixelRect Map2KToWindow(
        PixelRect rect2K,
        CaptureFrame frame) =>
        Map2KToWindow(rect2K, frame.Width, frame.Height);

    /// <summary>把 2K(2559×1439) 参考矩形缩放到窗口客户区尺寸。</summary>
    private static PixelRect Map2KToWindow(
        PixelRect rect2K,
        int width,
        int height) =>
        new(
            (int)Math.Round(rect2K.X * width / 2559d),
            (int)Math.Round(rect2K.Y * height / 1439d),
            (int)Math.Round(rect2K.Width * width / 2559d),
            (int)Math.Round(rect2K.Height * height / 1439d));

    /// <summary>把 2K 参考点缩放到窗口客户区。</summary>
    private static PixelPoint Map2KToWindow(
        PixelPoint point,
        int width,
        int height) =>
        new(
            (int)Math.Round(point.X * width / 2559d),
            (int)Math.Round(point.Y * height / 1439d));

    private static PixelRect BoundsAround(
        GameWindowInfo window,
        PixelPoint point)
    {
        var radius = Math.Max(
            4,
            (int)Math.Round(6 * window.ClientArea.Width / 2559d));
        return new PixelRect(
            Math.Clamp(point.X - radius, 0, Math.Max(0, window.ClientArea.Width - radius * 2)),
            Math.Clamp(point.Y - radius, 0, Math.Max(0, window.ClientArea.Height - radius * 2)),
            radius * 2,
            radius * 2);
    }

    // =====================================================================================
    // 2026-08-26 新增：祈愿试炼"是否可达成目标"的判定逻辑（含奖励物品名识别与 OCR 容错）
    // =====================================================================================

    /// <summary>关键胜利试炼名（只看试炼名即一定成功；奖励固定，不看奖励名）。</summary>
    private static readonly string[] VictoryTrialNames =
    {
        "奇迹代偿",
        "无限之釜",
    };

    /// <summary>可达成目标但奖励不固定的试炼名（须试炼名 + 奖励"五费聘用书"同时识别对才选）。</summary>
    private static readonly string[] ConditionalTrialNames =
    {
        "令咒决议·行为限制",
        "令咒决议·回路过载",
    };

    /// <summary>可达成目标所需的奖励物品名（五费聘用书）。</summary>
    private const string TargetRewardName = "五费聘用书";

    /// <summary>
    /// 判定某个祈愿试炼（试炼名 + 奖励物品名）是否为"可达成目标"的试炼。
    /// <para>
    /// 规则（用户 2026-08-26 拍板）：
    /// <list type="bullet">
    ///   <item><b>奇迹代偿 / 无限之釜</b>：奖励固定，只要识别到试炼名（OCR 差一字容错）即一定成功。</item>
    ///   <item><b>令咒决议·行为限制 / 令咒决议·回路过载</b>：奖励不固定，必须<b>试炼名</b> 与
    ///        <b>奖励"五费聘用书"</b> 同时识别对（各 OCR 差一字容错），才判定可达成。</item>
    ///   <item>其它试炼：不可达成目标。</item>
    /// </list>
    /// 容错：所有名称比较允许编辑距离 ≤ 1（差一个字以内）。
    /// </para>
    /// </summary>
    public static bool IsWinningTrial(string? trialName, string? rewardName)
    {
        if (string.IsNullOrWhiteSpace(trialName))
            return false;

        // 用「子串/滑动窗口模糊匹配」兼容两种 OCR 文本：
        //   - 纯短名（如 "奇迹代偿"）
        //   - 长描述串（如 "令人决议·奇迹代偿：扣88血"、"诅咒·无限之釜：直接获得1个三星5费Archer"）
        //     只要文本里出现目标试炼名（或与它编辑距离≤1），即命中。奖励同理。
        foreach (var victory in VictoryTrialNames)
            if (ContainsFuzzy(trialName, victory, allowLongWindow: true))
                return true;

        foreach (var conditional in ConditionalTrialNames)
            if (ContainsFuzzy(trialName, conditional, allowLongWindow: true))
                return ContainsFuzzy(rewardName, TargetRewardName, allowLongWindow: true);

        return false;
    }

    /// <summary>
    /// 判断 <paramref name="text"/> 是否命中 <paramref name="keyword"/>（OCR 容错）：
    /// <list type="bullet">
    ///   <item>text 直接包含 keyword 子串 → 命中（兼容长描述串）。</item>
    ///   <item>否则在 text 中滑窗，找与 keyword 编辑距离 ≤ 1 的子串（兼容"差一字"）。
    ///        允许 keyword 长度在窗口中增删一字（<paramref name="allowLongWindow"/>）。</item>
    /// </list>
    /// </summary>
    public static bool ContainsFuzzy(string? text, string keyword, bool allowLongWindow = false)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
            return false;
        if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // 滑动窗口：窗口长度取 keyword 长度（或 keyword±1，用于 OCR 多/少一字）。
        foreach (var winLen in CandidateWindowLengths(keyword.Length, allowLongWindow))
        {
            if (winLen <= 0 || winLen > text.Length) continue;
            for (var i = 0; i + winLen <= text.Length; i++)
            {
                var window = text.Substring(i, winLen);
                if (WindowMatches(window, keyword))
                    return true;
            }
        }
        return false;
    }

    private static bool WindowMatches(string window, string keyword)
    {
        if (string.Equals(window, keyword, StringComparison.OrdinalIgnoreCase))
            return true;
        // window 与 keyword 长度差 ≤ 1 时允许一次编辑（替换/插入/删除）。
        return Math.Abs(window.Length - keyword.Length) <= 1
            && EditDistance(window, keyword) <= 1;
    }

    private static IEnumerable<int> CandidateWindowLengths(int keywordLength, bool allowLongWindow)
    {
        // 优先等长窗口，再 keyword±1（允许 OCR 多/少一字）。
        yield return keywordLength;
        if (allowLongWindow)
        {
            if (keywordLength - 1 > 0) yield return keywordLength - 1;
            yield return keywordLength + 1;
        }
    }

    /// <summary>
    /// 在左右两个祈愿试炼中，挑出"可达成目标"的那一侧。
    /// <para>两侧都匹配时取左；都不匹配返回 null（交调用方默认/不选，避免误选）。</para>
    /// </summary>
    public static int? PickWinningSide(string? leftName, string? leftReward, string? rightName, string? rightReward)
    {
        var leftWins = IsWinningTrial(leftName, leftReward);
        var rightWins = IsWinningTrial(rightName, rightReward);

        if (leftWins && rightWins) return 0;        // 都匹配，取左
        if (leftWins) return 0;                     // 只有左匹配
        if (rightWins) return 1;                    // 只有右匹配
        return null;                                // 都不匹配
    }

    /// <summary>Levenshtein 编辑距离（大小写不敏感）。用于窗口与关键字的长描述串/差一字容错；长度差 > 1 直接返回 2（超出容错上限加速）。</summary>
    private static int EditDistance(string a, string b)
    {
        // 长度差 > 1 直接判距 >1（WindowMatches 已挡，这里兜底）。
        if (Math.Abs(a.Length - b.Length) > 1) return 2;
        // 用 DP，但一旦最低可能距离 >1 即可早退（仅需判断是否 <=1）。
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
