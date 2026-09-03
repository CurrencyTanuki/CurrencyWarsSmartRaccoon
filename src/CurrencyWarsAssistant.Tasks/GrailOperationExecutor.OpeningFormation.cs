using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

public sealed partial class GrailOperationExecutor
{
    /// <summary>
    /// 1-3 开局一次性动作（定稿决策树 N13/N2，闩锁每局一次，由
    /// <see cref="GrailRunLoop"/> 在进入 1-3 循环后调用）：
    /// <list type="number">
    /// <item>N13：先上场所有备战席上的命运圣杯羁绊角色（备战席不识别装备，
    ///   星徽携带者已在场上——这里主要是命杯成员）。</item>
    /// <item>N2：物品栏有未携带星徽时，装配到备战席一个非命杯、非 5 费角色并上场。</item>
    /// </list>
    /// 注：N12（采购专员下 5 费放备战席最左）是**每 tick 自限**动作，不在本闩锁内，
    /// 见 <see cref="ExecuteLeftmostMaintenanceAsync"/>。
    /// 槽位分配用本地一次性分配器（从快照实际空槽派生并随部署推进），避免
    /// 两个动作各自按陈旧快照算空槽导致互相把对方成员换下备战席。
    /// </summary>
    public async Task<bool> ExecuteOpeningFormationAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (stateHolder.PeekOpeningFormationApplied())
        {
            return false; // 本局已执行过，不再重复
        }

        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            windowHandle, expectedPreparationPageId, cancellationToken);
        if (bench is null)
        {
            // 备战页门禁/识别失败：不置闩锁，下一 tick 重试
            return false;
        }

        // 跨阶段去重播种（审计 A）：1-1/1-2 买到的角色在备战席/场上会被识别进
        // snapshot.OwnedCharacterNames——把它们播种进持久 purchased 集合，使 1-3 的空快照
        // 首次商店 Pass 也"同名只买一次"（用户反复强调的规则，跨阶段生效）。
        foreach (var ownedName in snapshot.OwnedCharacterNames)
        {
            stateHolder.RecordPurchased(ownedName);
        }

        // N13 开局第一步：先开启晶矿（决策树 N13 明确"进入1-3备战节点，先开启晶矿"；
        // 用户 2026-08-31 指出此前 1-3 不再先开金矿——复用既有开晶矿基建）。
        await rewardStage.OpenMineBallsStandaloneAsync(windowHandle, cancellationToken);

        var occupiedFront = new HashSet<int>(snapshot.OccupiedFrontSlots);
        var occupiedBack = new HashSet<int>(snapshot.OccupiedBackSlots);
        var performed = false;

        // N13：统一上场所有命杯成员（优先 Front 0..3 空槽，满则 Back 0..5——参考 N14 空槽派生）
        foreach (var item in bench.OrderBy(item => item.BenchSlot))
        {
            if (!FateGrailFormationPolicy.IsCandidate(item.Character))
            {
                continue;
            }

            // 部署期同名守卫（审计 B）：该命杯成员已上场（DeployedBondMembers 含它）则跳过——
            // 只上阵一个（用户反复强调的规则）。快照 DeployedBondMembers 按角色去重，
            // 防止识别误差/历史残留导致同名角色重复上阵。
            if (snapshot.DeployedBondMembers.Contains(item.Character.Name))
            {
                continue;
            }

            var (lane, slot) = NextEmptySlot(occupiedFront, occupiedBack);
            if (slot < 0)
            {
                break; // 前台/后台都满（人口上限），剩下的留给 N16 买经验升人口
            }

            var deployed = await preparationBoard.GrailDeployBenchCharacterAsync(
                windowHandle, item, lane, slot, expectedPreparationPageId, cancellationToken);
            if (deployed)
            {
                if (lane == PreparationLane.Front)
                {
                    occupiedFront.Add(slot);
                }
                else
                {
                    occupiedBack.Add(slot);
                }

                performed = true;
            }
        }

        // N2：星徽装配（019/全是这家伙的错 开局星徽在 1-3 已入物品栏；非命杯非 5 费
        // 可上场角色 = 星徽携带者候选，5 费留备战席最左给采购专员）——与 N13 共享槽位分配器，
        // 不会把已部署成员换下备战席
        performed |= await AssembleOpeningBadgesAsync(
            windowHandle, snapshot, bench,
            occupiedFront, occupiedBack,
            expectedPreparationPageId, cancellationToken);

        stateHolder.MarkOpeningFormationApplied();
        return performed;
    }

    /// <summary>
    /// N12 持续性维护（每 tick 自限，不依赖开局闩锁）：采购专员策略下，5 费角色必须在
    /// 备战席最左侧一格（决策树 N12：拿到 5 费后第一件事移动到最左；F11a 开出的昔涟等
    /// 非命杯 5 费也按此处理）。用**当前快照**判定采购专员——避免开局闩锁首 tick 时
    /// 策略图标识别未跟上导致整局被跳过（复审应修#2 识别竞态）。
    /// </summary>
    public async Task<bool> ExecuteLeftmostMaintenanceAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.ActiveInvestmentStrategyIds.Any(InvestmentStrategyPicker.IsPurchaseSpecialist))
        {
            return false; // 未选采购专员（或识别未跟上），保守不动作
        }

        await MoveFiveCostsToLeftmostBenchAsync(
            windowHandle, expectedPreparationPageId, cancellationToken);
        return true;
    }

    /// <summary>
    /// N2 独立入口：把一枚未携带星徽装配到备战席非命杯非 5 费角色并上场（自限：
    /// 物品栏无星徽或备战席无可装配角色即返回）。用于开局后试炼奖励等新到的星徽。
    /// 与开局闩锁同 tick 冲突防护：开局动作尚未执行时跳过——否则本方法按陈旧快照
    /// 空槽部署会和 N13 的部署槽位冲突（互换把命杯成员换回备战席）。
    /// </summary>
    /// <summary>
    /// 右栏星徽槽定标回退（RelativeRegion=客户区归一化）：识别层 InventorySlots 对
    /// 019 开局星徽失明（08-20/21 装备标定回归，handoff 〇-C/〇-N）。定标：2026-09-02
    /// 实拍截图右栏第 3 格星徽图标，中心≈(2470,452)@2560×1440（放大复测修订值；
    /// 旧值 (2490,466) 偏右下 20/14px，仍在图标内）。仅在 A4 指令路径
    /// （allowCalibratedFallback=true）启用，事件循环路径绝不盲拖。
    /// </summary>
    private static readonly RelativeRegion FallbackInventoryStarBadgeRegion =
        new(0.9498, 0.2939, 0.0300, 0.0400);

    /// <summary>快照陈旧度门：超过该时长未刷新的快照不作为拖拽依据（与 I1 陈旧帧同口径）。</summary>
    private static readonly TimeSpan BadgeAssemblySnapshotFreshness =
        TimeSpan.FromSeconds(15);

    /// <summary>
    /// A4 星徽装配（2026-09-02 新口径）：装配给【已上场】的前台角色（装给备战席=白装）；
    /// 识别失明时（A4 指令路径）用实测定标回退；场上无人或快照超龄=失败回事实。
    /// </summary>
    public async Task<bool> ExecuteBadgeAssemblyAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        string expectedPreparationPageId,
        CancellationToken cancellationToken,
        bool allowCalibratedFallback = false,
        bool requireOpeningLatch = true,
        int? preferredFrontSlot = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (requireOpeningLatch && !stateHolder.PeekOpeningFormationApplied())
        {
            return false; // 开局动作（N13 等）尚未完成，避免同 tick 槽位冲突
        }

        if (snapshot.CapturedAt is { } capturedAt &&
            DateTimeOffset.UtcNow - capturedAt > BadgeAssemblySnapshotFreshness)
        {
            return false; // 快照超龄：槽位占用不可信，拒绝盲拖（决策层应先刷新识别）
        }

        // 目标槽位：决策层可指名（规格原文"目标角色名"），否则自动取最靠前的已占用前台槽。
        var frontSlot = preferredFrontSlot
            ?? snapshot.OccupiedFrontSlots.OrderBy(x => x).Cast<int?>().FirstOrDefault();
        if (frontSlot is null)
        {
            return false; // 场上无角色：绝不装配给备战席（旧口径废除）
        }

        var badgeRegion = snapshot.InventoryStarBadgeRegions.FirstOrDefault()
            ?? (allowCalibratedFallback ? FallbackInventoryStarBadgeRegion : null);
        if (badgeRegion is null)
        {
            return false; // 识别无星徽且未允许定标回退：维持事件循环保守口径
        }

        return await preparationBoard.GrailDragBadgeToFrontCharacterAsync(
            windowHandle,
            badgeRegion,
            frontSlot.Value,
            expectedPreparationPageId,
            cancellationToken);
    }

    /// <summary>开局块内一次性装配全部未携带星徽（每枚装配一个非命杯非 5 费角色并上场）。</summary>
    private async Task<bool> AssembleOpeningBadgesAsync(
        nint windowHandle,
        GrailRunSnapshot snapshot,
        IReadOnlyList<RecognizedBenchCharacter> bench,
        ISet<int> occupiedFront,
        ISet<int> occupiedBack,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        if (snapshot.UncarriedStarBadges <= 0 || snapshot.InventoryStarBadgeRegions.Count == 0)
        {
            return false;
        }

        var remaining = bench.ToList();
        var badgeCount = Math.Min(snapshot.UncarriedStarBadges, snapshot.InventoryStarBadgeRegions.Count);
        var assembled = false;
        for (var index = 0; index < badgeCount; index++)
        {
            var target = remaining
                .OrderBy(item => item.BenchSlot)
                .FirstOrDefault(item =>
                    !FateGrailFormationPolicy.IsCandidate(item.Character) &&
                    !IsPureFiveCost(item.Character));
            if (target is null)
            {
                break;
            }

            var dragged = await preparationBoard.GrailDragBadgeToBenchCharacterAsync(
                windowHandle,
                snapshot.InventoryStarBadgeRegions[index],
                target,
                expectedPreparationPageId,
                cancellationToken);
            if (!dragged)
            {
                break;
            }

            var (lane, slot) = NextEmptySlot(occupiedFront, occupiedBack);
            if (slot < 0)
            {
                break;
            }

            var deployed = await preparationBoard.GrailDeployBenchCharacterAsync(
                windowHandle, target, lane, slot, expectedPreparationPageId, cancellationToken);
            if (!deployed)
            {
                // 装配已拖但上场失败：星徽已在备战席角色上（装备识别不可见），停止后续装配
                break;
            }

            if (lane == PreparationLane.Front)
            {
                occupiedFront.Add(slot);
            }
            else
            {
                occupiedBack.Add(slot);
            }

            assembled = true;
            remaining.Remove(target);
        }

        return assembled;
    }

    /// <summary>N12：把备战席每个 5 费角色（纯 [5] 口径）拖到最左侧一格。</summary>
    private async Task MoveFiveCostsToLeftmostBenchAsync(
        nint windowHandle,
        string expectedPreparationPageId,
        CancellationToken cancellationToken)
    {
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            windowHandle, expectedPreparationPageId, cancellationToken);
        if (bench is null)
        {
            return;
        }

        foreach (var item in bench.OrderBy(item => item.BenchSlot))
        {
            if (!IsPureFiveCost(item.Character))
            {
                continue;
            }

            await preparationBoard.GrailMoveToLeftmostBenchAsync(
                windowHandle, item, expectedPreparationPageId, cancellationToken);
        }
    }

    /// <summary>从快照实际空槽派生的下一个可部署槽位（前台 0..3 优先，满则后台 0..5；找不到返回 slot=-1）。</summary>
    private static (PreparationLane Lane, int Slot) NextEmptySlot(
        ISet<int> occupiedFront,
        ISet<int> occupiedBack)
    {
        for (var slot = 0; slot < 4; slot++)
        {
            if (!occupiedFront.Contains(slot))
            {
                return (PreparationLane.Front, slot);
            }
        }

        for (var slot = 0; slot < 6; slot++)
        {
            if (!occupiedBack.Contains(slot))
            {
                return (PreparationLane.Back, slot);
            }
        }

        return (PreparationLane.Front, -1);
    }
}
