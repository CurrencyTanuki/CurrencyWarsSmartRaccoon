using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>商店刷新购买循环的结束状态。</summary>
public enum ShopRefreshPurchaseLoopStatus
{
    /// <summary>需要购买名单中的角色已全部获得（或名单为空时至少完成一轮）。</summary>
    Complete,
    /// <summary>商店识别不可用/商店未打开，循环未执行购买。</summary>
    RecognitionUnavailable,
    /// <summary>剩余金币不足以支付下一次刷新费用，提前停止。已买的角色仍被记录。</summary>
    InsufficientGold,
    /// <summary>刷新按钮点击失败/商店状态异常，停止以避免盲点。</summary>
    RefreshFailed,
    /// <summary>令牌取消。</summary>
    Cancelled
}

public sealed record ShopRefreshPurchaseLoopResult(
    ShopRefreshPurchaseLoopStatus Status,
    IReadOnlyCollection<string> PurchasedNames,
    string Message)
{
    public bool Completed => Status == ShopRefreshPurchaseLoopStatus.Complete;
}

public sealed partial class RewardStageAutomationController
{
    /// <summary>
    /// 商店"刷新"按钮圆心，2K(2559×1439) 参考坐标，运行时按实际窗口等比缩放
    /// （用户 2026-08-23 在 2559×1439 商店页实机指定，缩放基准=2K 而非 1920）。
    /// </summary>
    private static readonly PixelPoint ShopRefreshCenterPoint2K =
        new(2155, 677);

    /// <summary>每次刷新商店的固定金币费用（用户 2026-08-23 确认；1-3 循环 M5 金币门控同用此值）。</summary>
    private const int ShopRefreshCost = 2;

    /// <summary>刷新单价对外只读口径（1-3 N14 循环的金币门控使用）。</summary>
    internal const int ShopRefreshGoldCost = ShopRefreshCost;

    /// <summary>
    /// 1-3 N14 刷新一次（2026-09-03 用户修正：1-3 必须刷新）。金币门控归执行器，
    /// 本方法只负责点击并等货架动画落定。返回点击是否成功。
    /// </summary>
    internal async Task<bool> RefreshShopOnceAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var clicked = await ClickShopRefreshAsync(windowHandle, cancellationToken);
        if (clicked)
        {
            // 1.2.68 提速：900→400（1.2.29 曾 1100→900）。刷新后总前置=本处 400ms+
            // 圣杯循环热路径 ReadStableShopAsync 250ms=650ms（1.2.29 时代为 900+500=1400ms）；
            // 货架误读由循环内 readFailures<2 重试、双帧稳定判据与外层 M5 重进兜底
            //（1.2.22 教训仍保留，F5 注释口径修正）。
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        return clicked;
    }

    /// <summary>
    /// 刷新循环的最大轮次上限（安全阀，防止目标一直不出现时无限刷新烧金币/盲点）。
    /// 正常情况由"名单买齐"或"金币不足"提前退出，此值只在异常时兜底。
    /// </summary>
    private const int MaximumShopRefreshRounds = 200;

    /// <summary>连续若干轮都没有新增购买时停止（防盲点死循环，例如页面已离开商店或按钮失效）。</summary>
    private const int ConsecutiveNoPurchaseStopThreshold = 6;

    /// <summary>
    /// 独立可调用的商店"识别→购买→刷新→重新识别"循环。
    /// <para>
    /// 流程（遵循用户 2026-08-23 简化要求，去掉一切单次购买确认/额外判定）：
    /// <list type="number">
    /// <item>识别商店当前 5 个槽位（含已购买产生的空槽，空槽 Character=null）。</item>
    /// <item>对名单内且尚未获得的角色，每个槽位只点击一次即视为购买，不做任何确认。</item>
    /// <item>若名单已全部获得则成功退出；否则检查剩余金币（注入 readRemainingGold）。</item>
    /// <item>金币足以刷新则点击刷新按钮（2K 圆心等比缩放，点完几乎无延迟进入下一轮）。</item>
    /// <item>金币不足则按 InsufficientGold 停止（"刷掉重开"不属于本模块职责）。</item>
    /// </list>
    /// 本模块不接管 1-1/1-2/1-3 的流程推进，由调用方决定何时调用。可用
    /// <paramref name="purchaseNames"/> 覆盖名单（例如把"命运圣杯"成员加入必买名单）。
    /// </para>
    /// </summary>
    public async Task<ShopRefreshPurchaseLoopResult> RunShopRefreshPurchaseLoopAsync(
        nint windowHandle,
        RewardStageAutomationOptions options,
        Func<CaptureFrame, CancellationToken, Task<int?>>? readRemainingGold,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? purchaseNames = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 需要购买名单 = 调用方显式名单，否则回落配置的自动购买 + 保留角色名单。
        var neededNames = (purchaseNames ??
            (options.AutoPurchaseCharacterNames
                .Concat(options.RetainedCharacterNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 初始已拥有 = 配置中声明的角色，避免把它们当成待购买目标。
        var ownedNames = options.InitialOwnedCharacters
            .Select(item => item.Character.Name)
            .Concat(options.InitialFormationPlacements.Select(item =>
                item.Source.Character.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 已购买（本轮在商店里点过的）名字，同样视为已获得。
        var purchasedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Publish(
            "ShopRefreshLoopStarted",
            $"商店刷新购买循环启动；需要购买名单=[{string.Join("、", neededNames)}]；" +
            $"初始已拥有=[{string.Join("、", ownedNames)}]。");

        var round = 0;
        var consecutiveNoPurchaseRounds = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            round++;
            if (round > MaximumShopRefreshRounds)
            {
                return new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.RefreshFailed,
                    purchasedNames,
                    $"已连续执行 {MaximumShopRefreshRounds} 轮仍未购齐目标，触发安全阀停止以保护资源。");
            }

            var snapshot = await ReadStableShopAsync(
                windowHandle,
                consumedSlots: null,
                cancellationToken);
            if (snapshot is null)
            {
                // 可能是商店未能稳定打开，也可能是本轮购买恰好触发商店自动关闭
                // 回到了备战页（此时目标应视为已按简化规则处理过）。不做强断言，
                // 以免把"自动关闭"误报成"识别失败"；停止循环交由调用方决定下一步。
                return new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.RecognitionUnavailable,
                    purchasedNames,
                    "商店稳定快照不可用（可能未打开或已自动关闭）；本轮停止并返回当前已购名单。");
            }

            // 点选名单内、初始未拥有且尚未购得的角色，每个槽位点击一次，不做二次确认。
            var boughtAnyThisRound = false;
            foreach (var slot in snapshot)
            {
                var name = slot.Character?.Name;
                if (name is null ||
                    !neededNames.Contains(name) ||
                    ownedNames.Contains(name) ||
                    purchasedNames.Contains(name))
                {
                    continue;
                }

                var click = await ClickShopCardAsync(
                    windowHandle,
                    slot.Slot,
                    name,
                    cancellationToken);
                if (!click.Succeeded)
                {
                    Publish(
                        "ShopRefreshPurchaseClickSkipped",
                        $"{name} 的购买输入未发送（{click.Message}）；本槽视为未购买，继续下一目标。",
                        TaskEventLevel.Warning);
                    continue;
                }

                // 简化：点一次即认为触发购买，不等待/不确认（用户 2026-08-23 要求）。
                purchasedNames.Add(name);
                boughtAnyThisRound = true;
                Publish(
                    "ShopRefreshPurchaseClickedOnce",
                    $"已对槽位 {slot.Slot + 1} 的 {name} 发送一次购买点击（不等待确认）。",
                    TaskEventLevel.Information);
            }

            // 清单已全部获得 → 成功退出（即使名单内还有未点击的也已在此轮覆盖）。
            var namesToObtain = neededNames
                .Where(name => !ownedNames.Contains(name) &&
                               !purchasedNames.Contains(name))
                .ToArray();
            if (namesToObtain.Length == 0)
            {
                return new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.Complete,
                    purchasedNames,
                    $"需要购买的 {neededNames.Count} 个角色已全部获得。");
            }

            // 连续多轮无新增购买，说明很可能已离开商店/刷新按钮失效/金币已耗（防盲点死循环）。
            consecutiveNoPurchaseRounds = boughtAnyThisRound
                ? 0
                : consecutiveNoPurchaseRounds + 1;
            if (consecutiveNoPurchaseRounds >= ConsecutiveNoPurchaseStopThreshold)
            {
                return new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.RefreshFailed,
                    purchasedNames,
                    $"连续 {consecutiveNoPurchaseRounds} 轮刷新后没有任何新增购买，" +
                    $"很可能是页面已离开商店或刷新按钮失效，停止以避免盲点。");
            }

            // 读取剩余金币（注入者自行按现有 Economy 区域实现；null=不判断金币）。
            int? remainingGold = null;
            if (readRemainingGold is not null)
            {
                var (_, frame) = await CaptureForegroundAsync(
                    windowHandle,
                    cancellationToken);
                remainingGold = await readRemainingGold(frame, cancellationToken);
                Publish(
                    "ShopRefreshGoldRead",
                    $"本轮剩余金币={remainingGold?.ToString() ?? "未识别"}；" +
                    $"仍缺 =[{string.Join("、", namesToObtain)}]。",
                    TaskEventLevel.Information);
            }

            if (remainingGold is { } gold &&
                gold < ShopRefreshCost)
            {
                return new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.InsufficientGold,
                    purchasedNames,
                    $"剩余金币 {gold} < 刷新费用 {ShopRefreshCost}，" +
                    $"尚未购得 =[{string.Join("、", namesToObtain)}]，停止本模块。");
            }

            var refreshClick = await ClickShopRefreshAsync(
                windowHandle,
                cancellationToken);
            if (!refreshClick)
            {
                return new ShopRefreshPurchaseLoopResult(
                    ShopRefreshPurchaseLoopStatus.RefreshFailed,
                    purchasedNames,
                    "刷新按钮点击失败；停止以避免盲点。");
            }

            // 点完刷新后进入下一轮识别，几乎不额外等待（刷新动画由下轮快照自身稳定）。
        }

        return new ShopRefreshPurchaseLoopResult(
            ShopRefreshPurchaseLoopStatus.Cancelled,
            purchasedNames,
            "令牌已取消。");
    }

    private async Task<bool> ClickShopRefreshAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        var window = await foregroundGuard.WaitUntilForegroundAsync(
            windowHandle,
            cancellationToken);
        var point = MapRefreshPixelPoint(window, ShopRefreshCenterPoint2K);
        var target = new ClickTarget(
            "shop_refresh",
            "刷新商店",
            window,
            BoundsAround(window, point));
        var action = await input.ClickAsync(
            target,
            new ActionPolicy
            {
                // 用户 2026-08-23 明确：点完立即识别，几乎无延迟，不要乱加延时。
                AfterActionDelay = TimeSpan.Zero
            },
            cancellationToken);
        if (!action.Succeeded)
        {
            Publish(
                "ShopRefreshClickFailed",
                action.Message,
                TaskEventLevel.Warning);
            return false;
        }

        Publish(
            "ShopRefreshClicked",
            $"刷新按钮在 2K 参考 ({ShopRefreshCenterPoint2K.X},{ShopRefreshCenterPoint2K.Y})，" +
            $"映射到客户区落点=({point.X},{point.Y})；已发送一次点击，立即进入下一轮识别。");
        return true;
    }

    /// <summary>
    /// 把 2K(2559×1439) 参考像素点等比缩放到实际窗口客户区。
    /// <para>
    /// 说明：现有 <c>MapStandardPoint</c> 以 1920×1080 为基准，而本模块的刷新按钮是
    /// 用户以 2K 商店页实测指定，故以 2559×1439 为基准。两者对 16:9 比例窗口等价
    ///（2559/1439≈1.778≈1920/1080，差异约 0.03%），仅为避免维护者误以为两套坐标语义不同。
    /// </para>
    /// </summary>
    private static PixelPoint MapRefreshPixelPoint(
        GameWindowInfo window,
        PixelPoint point) =>
        new(
            (int)Math.Round(point.X * window.ClientArea.Width / 2559d),
            (int)Math.Round(point.Y * window.ClientArea.Height / 1439d));
}
