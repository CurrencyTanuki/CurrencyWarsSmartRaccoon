using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>聘用书选角色处理结果的结束状态。</summary>
public enum TrialRecruitSelectionStatus
{
    /// <summary>未检测到选角色悬浮窗（当前不在该界面，无需处理）。</summary>
    NotDetected,
    /// <summary>成功识别悬浮窗中的候选角色卡牌。</summary>
    PanelDetected,
    /// <summary>已按目标角色选择并选中，悬浮窗已退出（角色已上备战席）。</summary>
    Selected,
    /// <summary>悬浮窗出现但 4 个候选名识别不足/无法用 OCR 读出。</summary>
    RecognitionFailed,
    /// <summary>悬浮窗出现但候选角色中没有任何一个匹配目标角色名。</summary>
    NoTargetMatch,
    /// <summary>点击输入失败。</summary>
    InputFailed
}

/// <summary>
/// 「聘用书」选角色悬浮窗的独立识别与选择小组件（四费/五费聘用书通用）。
/// <para>
/// 背景：备战席某格为「聘用书」道具（四费紫底白字/五费黄底白字），点击该格
/// 会在页面顶部弹出一个悬浮窗，列出 4 张同费用的候选角色卡（卡面布局类似商店）。
/// 点某张卡牌的正中心即选中该角色，角色直接上备战席，<b>无需任何二次确认/确定</b>。
/// </para>
/// <para>
/// 本组件<b>不接入主识别流程</b>：它只在调用方主动请求时执行（三星五费独立
/// 功能使用）。坐标均为 2K(2559×1439) 参考（用户 2026-08-23 红框图 + 像素定位实标），
/// 运行时按实际窗口等比缩放。
/// </para>
/// </summary>
public sealed class TrialRecruitSelectionAutomation(
    IGameCapture capture,
    PpOcrOfflineOcr ocr,
    IInputController input,
    IGameForegroundGuard foregroundGuard,
    ITaskEventSink eventSink)
{
    // ---- 2K(2559×1439) 参考坐标（用户 2026-08-23 红框图实录标定） ----

    /// <summary>备战席「聘用书」格子（第二个格子）的默认点击中心（触发弹悬浮窗）。</summary>
    public static readonly PixelPoint RecruitSlotCenter2K = new(375, 1267);

    /// <summary>悬浮窗外框（页面顶部整窗，参考用，识别不依赖它）。</summary>
    public static readonly PixelRect PanelBound2K = new(458, 23, 1830, 571);

    /// <summary>4 张候选卡牌点击框（点各自正中心即选中，无需确认）。</summary>
    private static readonly PixelRect[] CardBounds2K =
    [
        new(660, 178, 340, 369),
        new(1020, 177, 331, 367),
        new(1383, 176, 333, 373),
        new(1736, 179, 337, 370)
    ];

    /// <summary>4 个候选角色名称识别框（卡牌下部扁框，OCR 识别这里面的角色名）。</summary>
    private static readonly PixelRect[] NameBoxes2K =
    [
        new(673, 483, 315, 56),
        new(1035, 488, 302, 45),
        new(1391, 483, 312, 56),
        new(1748, 483, 312, 55)
    ];

    /// <summary>判定悬浮窗出现所需的最少识别到候选名数量。</summary>
    private const int MinimumDetectedNames = 2;

    /// <summary>一次点击选中后的复查重试上限。</summary>
    private const int MaximumAttempts = 3;

    /// <summary>参与匹配的识别文本最短长度（避免单个残字误匹配）。</summary>
    private const int MinimumMatchableNameLength = 2;

    /// <summary>
    /// 点击备战席「聘用书」格子触发悬浮窗，然后识别 4 个候选角色名并选中目标角色。
    /// </summary>
    /// <param name="targetNames">本次想拿到的目标角色名集合（如 ["知更鸟","大黑塔"]）。
    /// 命中规则：双向包含匹配（识别"知更"也能命中目标"知更鸟"），且识别文本长度≥2。
    /// </param>
    /// <returns><see cref="TrialRecruitSelectionStatus.Selected"/> 表示成功选中；其余为失败/未触发。
    /// 若识别到候选但无目标匹配，返回 <see cref="TrialRecruitSelectionStatus.NoTargetMatch"/>，
    /// 单人目标（anyFiveCost=true）例外：任意 5 费都可能是目标——歧义/无匹配时选第一张，
    /// 绝不让聘用书废在手里（用户公理：机会优先，条件后补），

    /// 不会盲目点击（避免白耗聘用书）。</returns>
    public async Task<TrialRecruitSelectionStatus> TryOpenAndSelectAsync(
        nint windowHandle,
        IReadOnlySet<string> targetNames,
        CancellationToken cancellationToken,
        PixelPoint? recruitSlotCenter2K = null,
        // 用户公理"机会优先，条件后补"：无匹配/歧义时选第一张可读候选（0 张可读才放弃）。
        // 单人目标=任意 5 费都可能是本体；歧义保守不点会让聘用书废在手里。
        bool fallbackPickFirst = false)
    {
        var triggerPoint = recruitSlotCenter2K ?? RecruitSlotCenter2K;

        // ① 点击备战席聘用书格子，触发悬浮窗。
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var clickedTrigger = await ClickPointAsync(
            window,
            triggerPoint,
            "备战席聘用书格子",
            cancellationToken);
        if (!clickedTrigger)
        {
            return TrialRecruitSelectionStatus.InputFailed;
        }

        // 等悬浮窗出现。
        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);

        // ② 重试循环：读 4 名 → 匹配 → 点目标卡 → 复查悬浮窗是否退出。
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var currentWindow = await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken);
            var frame = await capture.CaptureAsync(currentWindow, cancellationToken);

            var names = await ReadNamesAsync(frame, cancellationToken);
            var detectedCount = names.Count(name =>
                !string.IsNullOrWhiteSpace(name) && name.Length >= MinimumMatchableNameLength);
            if (detectedCount < MinimumDetectedNames)
            {
                // 上一轮已成功选中并退出（悬浮窗消失 → 读不出候选名）。
                if (attempt > 1)
                {
                    eventSink.Publish(new TaskEvent(
                        DateTimeOffset.Now,
                        TaskEventLevel.Information,
                        "TrialRecruitSelected",
                        "赋值聘用书候选角色后悬浮窗已退出（角色已上备战席）。"));
                    return TrialRecruitSelectionStatus.Selected;
                }

                eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Information,
                    "TrialRecruitNotDetected",
                    $"点击聘用书格子后未读出候选名（识别到 {detectedCount} 个）；" +
                    "视为未触发选角悬浮窗。"));
                return TrialRecruitSelectionStatus.NotDetected;
            }

            // ③ 找出匹配目标角色的卡牌。
            var matchedIndex = FindTargetCard(names, targetNames);
            if (matchedIndex < 0 && fallbackPickFirst)
            {
                // 兜底（用户公理）：无匹配/歧义时选第一张可读候选——聘用书开了就必须拿人
                matchedIndex = Array.FindIndex(names, name => !string.IsNullOrWhiteSpace(name));
                if (matchedIndex >= 0)
                {
                    eventSink.Publish(new TaskEvent(
                        DateTimeOffset.Now,
                        TaskEventLevel.Warning,
                        "TrialRecruitFallbackPick",
                        $"候选=[{string.Join(" / ", names.Select(n => n ?? "?"))}]，" +
                        $"未命中目标；按兜底策略选择第 {matchedIndex + 1} 张（机会优先，条件后补）。"));
                }
            }

            if (matchedIndex < 0)
            {
                eventSink.Publish(new TaskEvent(
                    DateTimeOffset.Now,
                    TaskEventLevel.Information,
                    "TrialRecruitNoTargetMatch",
                    $"候选角色名=[{string.Join(" / ", names.Select(n => n ?? "?"))}]，" +
                    "均不匹配目标角色，放弃本次点击。"));
                return TrialRecruitSelectionStatus.NoTargetMatch;
            }

            // ④ 点目标卡正中心（无需确认，点即选中并上备战席）。
            var clicked = await ClickRectCenterAsync(
                currentWindow,
                CardBounds2K[matchedIndex],
                $"选中候选角色 {names[matchedIndex]}",
                cancellationToken);
            if (!clicked)
            {
                return TrialRecruitSelectionStatus.InputFailed;
            }

            // 复查：等悬浮窗消退（读不到候选名 → 已选中）。
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Warning,
            "TrialRecruitMaxAttempts",
            $"已重试 {MaximumAttempts} 次仍无法确认选中，停止以避免反复点击。"));
        return TrialRecruitSelectionStatus.InputFailed;
    }

    /// <summary>只检测当前是否在选角色悬浮窗（不点击、不改状态）；供编排判定用。</summary>
    public async Task<TrialRecruitSelectionStatus> TryDetectIfOpenAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var frame = await capture.CaptureAsync(
            await foregroundGuard.WaitUntilForegroundAsync(
                windowHandle,
                cancellationToken),
            cancellationToken);
        var names = await ReadNamesAsync(frame, cancellationToken);
        var detectedCount = names.Count(name =>
            !string.IsNullOrWhiteSpace(name) && name.Length >= MinimumMatchableNameLength);
        if (detectedCount < MinimumDetectedNames)
        {
            return TrialRecruitSelectionStatus.NotDetected;
        }

        eventSink.Publish(new TaskEvent(
            DateTimeOffset.Now,
            TaskEventLevel.Information,
            "TrialRecruitPanelDetected",
            $"选角色悬浮窗已识别，候选名=[{string.Join(" / ", names.Select(n => n ?? "?"))}]。"));
        return TrialRecruitSelectionStatus.PanelDetected;
    }

    /// <summary>读 4 个候选角色名称框（PpOcr）。</summary>
    private async Task<string?[]> ReadNamesAsync(
        CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        var names = new string?[NameBoxes2K.Length];
        for (var i = 0; i < NameBoxes2K.Length; i++)
        {
            var region = Map2KToWindow(NameBoxes2K[i], frame.Width, frame.Height);
            var result = await ocr.RecognizeAsync(frame, region, cancellationToken);
            var text = result?.Text?.Trim() ?? string.Empty;
            names[i] = string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return names;
    }

    /// <summary>用双向包含匹配目标角色名；返回命中的卡牌索引。</summary>
    /// <remarks>
    /// 只允许<b>恰好一个</b>候选命中：多个候选命中(OCR 噪声/多个目标同现)时返回 -1
    /// 而不是选第一个，避免误选（用户红线：坐标/选择不得自作主张；宁可跳过不点错）。
    /// </remarks>
    private static int FindTargetCard(
        IReadOnlyList<string?> names,
        IReadOnlySet<string> targetNames)
    {
        var matchedIndex = -1;
        for (var i = 0; i < names.Count; i++)
        {
            var candidate = names[i];
            if (string.IsNullOrWhiteSpace(candidate) ||
                candidate.Length < MinimumMatchableNameLength)
            {
                continue;
            }

            var matches = targetNames.Any(target =>
                !string.IsNullOrWhiteSpace(target) &&
                (candidate.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                 target.Contains(candidate, StringComparison.OrdinalIgnoreCase)));
            if (matches)
            {
                if (matchedIndex >= 0)
                {
                    // 两个不同候选卡牌都匹配目标：命中歧义，保守不点。
                    return -1;
                }

                matchedIndex = i;
            }
        }

        return matchedIndex;
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
            "trial_recruit_click",
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

    private async Task<bool> ClickPointAsync(
        GameWindowInfo window,
        PixelPoint point2K,
        string displayName,
        CancellationToken cancellationToken)
    {
        var point = Map2KToWindow(point2K, window.ClientArea.Width, window.ClientArea.Height);
        var target = new ClickTarget(
            "trial_recruit_trigger",
            displayName,
            window,
            BoundsAround(window, point));
        var action = await input.ClickAsync(
            target,
            new ActionPolicy
            {
                AfterActionDelay = TimeSpan.FromMilliseconds(150),
                VerifyForegroundBeforeClick = true
            },
            cancellationToken);
        return action.Succeeded;
    }

    /// <summary>把 2K(2559×1439) 参考矩形缩放到窗口客户区尺寸。</summary>
    private static PixelRect Map2KToWindow(PixelRect rect2K, int width, int height) =>
        new(
            (int)Math.Round(rect2K.X * width / 2559d),
            (int)Math.Round(rect2K.Y * height / 1439d),
            (int)Math.Round(rect2K.Width * width / 2559d),
            (int)Math.Round(rect2K.Height * height / 1439d));

    private static PixelPoint Map2KToWindow(PixelPoint point, int width, int height) =>
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
}
