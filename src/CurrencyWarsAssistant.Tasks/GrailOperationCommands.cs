using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 操作层指令适配器（A 类）：动游戏。所有坐标/槽位解析都沉在既有组件内部（本类零坐标）。
/// 动作只执行、不复核——本类没有「操作后再读一次」的确认代码，成败由决策层用已有状态
/// 统一对账判定（用户 2026-09-01 拍板）；既有组件内部的输入重试保持原样，返回值按事实回传。
/// 尚无底层实现的动作（场上出售/星徽装配/置最左/独立刷新/Esc 恢复/预设与策略开关）
/// 显式 Fail 并注明承载位置，绝不凭空编造坐标。
/// </summary>
public sealed class GrailOperationCommands(
    GrailOperationExecutor executor,
    RewardStageAutomationController rewardStage,
    PreparationBoardController preparationBoard,
    IRunAbandoner? runAbandoner,
    IGalaBondPopupHandler? galaPopupHandler = null) : IGrailCommandHandler
{
    // 上场槽位占用序（前台 4 槽、后台 6 槽，与 PreparationBoardController 的槽位表一致）。
    // 本计数器只服务 A1：M5 内部上场走执行器自己的计数，两本账不得混用。
    // 无锁前提=决策循环单线程顺序下发（当前唯一设计用法）。
    private int _frontDeployCount;
    private int _backDeployCount;

    /// <summary>新对局边界调用：A1 的槽位账与执行器一样是进程级字段，跨局必须作废
    /// （2026-09-02 跨局污染复盘；由分发器在 M8 时统一触发）。</summary>
    internal void ResetDeploymentCounters()
    {
        _frontDeployCount = 0;
        _backDeployCount = 0;
    }

    public async Task<GrailCommandResult> HandleAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        return command.Kind switch
        {
            GrailCommandKind.A1 => await DeployAsync(command, context, cancellationToken),
            GrailCommandKind.A3 => await SellBenchAsync(command, context, cancellationToken),
            GrailCommandKind.A6 => await BuyXpAsync(context, cancellationToken),
            GrailCommandKind.A9 => await AbandonAsync(command, context, cancellationToken),
            GrailCommandKind.A10 => await SelectStrategyAsync(command, context, cancellationToken),
            GrailCommandKind.A2 => await SellDeployedAsync(command, context, cancellationToken),
            GrailCommandKind.A4 => await AssembleBadgeAsync(command, context, cancellationToken),
            GrailCommandKind.A5 => await MoveToLeftSlotAsync(command, context, cancellationToken),
            GrailCommandKind.A15 => await ChooseSimpleEquipmentAsync(command, context, cancellationToken),
            GrailCommandKind.A16 => await DismissGalaPopupAsync(context, cancellationToken),
            GrailCommandKind.A7 => GrailCommandResult.Fail(command.Kind,
                "环境页免费刷新无独立实现（环境选择在 opening 刷开局流程内承载）。"),
            GrailCommandKind.A11 => GrailCommandResult.Fail(command.Kind,
                "三连刷由 A10/M7 的 SelectInvestmentStrategyAsync 内部承载，无独立入口。"),
            GrailCommandKind.A12 => GrailCommandResult.Fail(command.Kind,
                "Esc 未知页恢复由 A9 的有界弃局序列承载，无独立组件。"),
            GrailCommandKind.A13 => GrailCommandResult.Fail(command.Kind,
                "购买预设开关由选项承载（RewardStageAutomationOptions.EnableEarlyStrongFormationPurchase=false），M8 已带默认。"),
            GrailCommandKind.A14 => GrailCommandResult.Fail(command.Kind,
                "银河学者开关由选项承载（RewardStageAutomationOptions.EnableGalaxyScholarRewardStrategy=true），M8 已带默认。"),
            _ => GrailCommandResult.Fail(command.Kind, $"操作层不受理指令 {command.Kind}。"),
        };
    }

    /// <summary>A2 出售场上角色（2026-09-02 用户设计定案）：参数=位置语义（前台/后台+槽位号1基），
    /// 操作层按标准槽位几何直接拖出售区——不依赖识别卡位、不做名称反查（识别卡位带旧布局残留）。
    /// 命杯成员/星徽携带者/5费的保护由决策层保证：本指令只认位置。</summary>
    private async Task<GrailCommandResult> SellDeployedAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        if (command.Payload is not GrailPositionArgs args)
        {
            return GrailCommandResult.Fail(command.Kind, "缺少 GrailPositionArgs(前台/后台 + 槽位号1基)。");
        }

        var zone = args.Lane == PreparationLane.Front ? FormationZone.Front : FormationZone.Back;
        var region = GrailSnapshotAssembler.ResolveCanonicalSlotRegion(zone, args.SlotIndex);
        if (region is null)
        {
            return GrailCommandResult.Fail(command.Kind,
                $"槽位号 {args.SlotIndex + 1} 超出{(args.Lane == PreparationLane.Front ? "前台 1-4" : "后台 1-6")}范围。");
        }

        var label = $"{(args.Lane == PreparationLane.Front ? "前台" : "后台")}{args.SlotIndex + 1}号位角色";
        // P1-C（2026-09-08 下午批）：期望名进操作层做拖前身份比对（防模型漂移误卖）；
        // 无期望名（旧调用方）时 GrailDeployedCharacter.Name 保持槽位标签=跳过比对。
        var expectedName = string.IsNullOrWhiteSpace(args.ExpectedCharacterName)
            ? label
            : args.ExpectedCharacterName;
        var outcome = await preparationBoard.GrailSellDeployedCharacterAsync(
            context.WindowHandle,
            new GrailDeployedCharacter(expectedName, IsBondMember: false, IsFiveCost: false, region, SaleValue: 0),
            context.PreparationPageId,
            cancellationToken,
            backRow: args.Lane == PreparationLane.Back);
        if (outcome.Sold)
        {
            return GrailCommandResult.Ok(command.Kind, $"已出售{label}。");
        }

        if (outcome.IdentityMismatch)
        {
            // P1-C：引擎台账与画面身份不符（1-3 试用卡轮换/系统重排致占用模型漂移）——
            // 拒绝拖拽防误卖；决策层收到失败后应 I10 重对账而非重发同槽。
            return GrailCommandResult.Fail(command.Kind,
                $"{label}身份不符：画面该槽位角色与台账「{expectedName}」不一致——" +
                "疑似占用模型漂移，已拒绝拖拽防误卖；请 I10 重对账后重试。");
        }

        if (outcome.AlreadyGone)
        {
            // 坑38 批次：槽位已空=此前可能已卖出成功，也可能台账漂移指向了空槽——
            // 不算失败、绝不再拖，但文案明示"未确认卖出"，交决策层 I10 对账定夺。
            return GrailCommandResult.Ok(command.Kind,
                $"{label}槽位已空——未能确认「{expectedName}」此前是否卖出生效，未执行拖拽；请 I10 对账。");
        }

        return GrailCommandResult.Fail(command.Kind, $"{label}出售拖放未完成或结果不确定（详见事件日志 GrailDeployedSale*）；请用 I10/截图复核后再决定是否重发。");
    }

    /// <summary>A4 星徽装配（N2，2026-09-03 位置语义定稿）：把物品栏星徽拖到指定前台/后台槽位角色；
    /// 角色名形式废除（名称解析依赖部署明细识别，本局识别两次把已上场角色读丢导致误拒——教训同 1.2.18 拖角色改造）。
    /// P1-A（2026-09-09 修复批）：拖拽前幂等预查（星徽账本按名携带者∪挂起槽位双查）——
    /// 目标已带徽=幂等成功不拖不记；挂起槽位按名佐证=提升记账不拖；无佐证=不拖不记
    /// 诚实失败（G15 教训：带徽重拖触发游戏拒绝横幅「无法穿戴相同羁绊的星徽」）。
    /// ExpectedCharacterName=引擎台账期望名（可选弱佐证，实时画面读数优先）。</summary>
    private async Task<GrailCommandResult> AssembleBadgeAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 位置语义（用户 2026-09-03 明令）：决策层只传"拖到哪个槽位"，源点由操作层全面板探测。
        if (command.Payload is not GrailPositionArgs target)
        {
            return GrailCommandResult.Fail(command.Kind,
                "用法：A4 前台|后台 槽位号(1基)——把物品栏命运圣杯星徽拖到该槽位角色（位置语义定稿，角色名形式已废除）。");
        }

        var maxSlot = target.Lane == PreparationLane.Front ? 4 : 6;
        if (target.SlotIndex < 0 || target.SlotIndex >= maxSlot)
        {
            // 审查 P3-1：与 A2 同口径显式 Fail，不做静默 Clamp。
            return GrailCommandResult.Fail(command.Kind,
                $"槽位号越界：{(target.Lane == PreparationLane.Front ? "前台" : "后台")} 1-{maxSlot}。");
        }

        var slotLabel = (target.Lane == PreparationLane.Front ? "前台" : "后台") + (target.SlotIndex + 1) + "号位";
        var zone = target.Lane == PreparationLane.Front ? FormationZone.Front : FormationZone.Back;
        var slotKey = GrailSnapshotAssembler.BadgeLedgerSlotKey(zone, target.SlotIndex);

        // ---- P1-A 幂等预查（拖拽前；名字佐证优先用实时画面读数）----
        string? liveOccupant = null;
        if (target.Lane == PreparationLane.Front)
        {
            // 后台槽无单槽实时读原语（A4 后台路径仅 5 人口兜底场景），挂起键双查仍生效。
            liveOccupant = await executor.PeekFrontSlotCharacterAsync(
                context.WindowHandle, target.SlotIndex, context.PreparationPageId, cancellationToken);
        }

        var (precheck, corroborated) = GrailBadgeAssemblyGuard.Decide(
            executor.PeekBadgeCarrierNames(),
            executor.PeekBadgePendingSlotKeys(),
            slotKey,
            liveOccupant,
            target.ExpectedCharacterName);
        switch (precheck)
        {
            case GrailBadgeAssemblyPrecheckOutcome.AlreadyCarries:
                // 幂等路径禁再记账：账本已有该名，重复记录无意义且污染审计。
                return GrailCommandResult.Ok(GrailCommandKind.A4,
                    $"{slotLabel}已携带星徽（按名佐证：「{corroborated}」）——幂等成功，未拖拽未重复记账。");
            case GrailBadgeAssemblyPrecheckOutcome.PromotePendingSlot:
                executor.PromoteBadgeCarrier(slotKey, corroborated!);
                return GrailCommandResult.Ok(GrailCommandKind.A4,
                    $"{slotLabel}挂起徽账经名字佐证提升为「{corroborated}」携带——幂等成功，未拖拽。");
            case GrailBadgeAssemblyPrecheckOutcome.UncertainNoDrag:
                return GrailCommandResult.Fail(GrailCommandKind.A4,
                    $"{slotLabel}账本有挂起徽但画面/台账均无法按名佐证——不盲拖（防拒绝横幅），请 I10 复核后再发。");
        }

        var assembled = await executor.ExecuteBadgeAssemblyToSlotAsync(
            context.WindowHandle,
            target.Lane,
            target.SlotIndex,
            context.PreparationPageId,
            cancellationToken);
        if (assembled)
        {
            // 星徽账本记账（2026-09-03 用户拍板）：装上即本局恒携带，角色装备识别漏读
            // 由账本兜底（组装器并集合并），决策层无需重复确认识别。
            executor.RecordBadgeEquippedAtSlot(slotKey);
            return GrailCommandResult.Ok(GrailCommandKind.A4, $"星徽已装配到{slotLabel}角色（拖后物品栏探测自证通过）。");
        }

        return GrailCommandResult.Fail(GrailCommandKind.A4, $"未装配：物品栏未探测到星徽，或 3 次拖后自证星徽仍在（详见事件日志 GrailBadgeAssembly*）。");
    }

    /// <summary>
    /// A15 简易装备选择（晶矿掉落模态，2026-09-03 实测新增）：参数=幸运星(默认)/小刀/轮滑鞋/手枪。
    /// </summary>
    private async Task<GrailCommandResult> ChooseSimpleEquipmentAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 审查 P2：选择类指令不得静默换选项——别名精确优先、包含次之，未知名直接 Fail。
        var requested = "幸运星";
        if (command.Payload is GrailCharacterArgs args && !string.IsNullOrWhiteSpace(args.CharacterName))
        {
            requested = args.CharacterName;
        }

        (string Name, int Idx)[] aliases =
        [
            ("幸运星", 0), ("折叠小刀", 1), ("小刀", 1),
            ("轮滑鞋", 2), ("和平手枪", 3), ("手枪", 3),
        ];
        var match = aliases.FirstOrDefault(a => string.Equals(a.Name, requested, StringComparison.Ordinal));
        if (match.Name is null)
        {
            match = aliases.FirstOrDefault(a => a.Name.Contains(requested, StringComparison.Ordinal));
        }

        if (match.Name is null)
        {
            return GrailCommandResult.Fail(command.Kind,
                $"未知简易装备名「{requested}」。可选：幸运星/折叠小刀/轮滑鞋/和平手枪。");
        }

        var index = match.Idx;

        var done = await preparationBoard.GrailChooseSimpleEquipmentAsync(
            context.WindowHandle, index, cancellationToken);
        return done
            ? GrailCommandResult.Ok(command.Kind, $"已点击简易装备「{requested}」（第 {index + 1} 项）；弹框是否消失请 I1 复核。")
            : GrailCommandResult.Fail(command.Kind, "简易装备选择点击未完成（组件返回失败）。");
    }

    /// <summary>
    /// A5 置最左（N12）：无参=把备战席里最靠左的纯 5 费角色移到第 0 格（5 费本体
    /// 必须在最左侧一格）；带 GrailCharacterArgs=把指定角色移到第 0 格。
    /// </summary>
    private async Task<GrailCommandResult> MoveToLeftSlotAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            context.WindowHandle,
            context.PreparationPageId,
            cancellationToken);
        if (bench is null || bench.Count == 0)
        {
            return GrailCommandResult.Fail(command.Kind, "备战席未识别到角色，无法置最左。");
        }

        RecognizedBenchCharacter? target = null;
        if (command.Payload is GrailCharacterArgs args && !string.IsNullOrWhiteSpace(args.CharacterName))
        {
            target = bench.FirstOrDefault(item =>
                string.Equals(item.Character.Name, args.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return GrailCommandResult.Fail(command.Kind, $"备战席未识别到 {args.CharacterName}。");
            }
        }
        else
        {
            target = bench
                .OrderBy(item => item.BenchSlot)
                .FirstOrDefault(item => GrailOperationExecutor.IsPureFiveCostCharacter(item.Character));
            if (target is null)
            {
                return GrailCommandResult.Fail(command.Kind, "备战席没有纯 5 费角色（无参 A5 仅服务 N12 的 5 费置最左）。");
            }
        }

        if (target.BenchSlot == 0)
        {
            return GrailCommandResult.Ok(command.Kind,
                $"{target.Character.Name} 已在备战席第 1 格（最左）。");
        }

        var moved = await preparationBoard.GrailMoveToLeftmostBenchAsync(
            context.WindowHandle,
            target,
            context.PreparationPageId,
            cancellationToken);
        return moved
            ? GrailCommandResult.Ok(command.Kind, $"已把 {target.Character.Name} 移到备战席最左（第 1 格）。")
            : GrailCommandResult.Fail(command.Kind, $"置最左拖拽未通过验证：{target.Character.Name}。");
    }

    private async Task<GrailCommandResult> DeployAsync(        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        if (command.Payload is not GrailDeployArgs args || string.IsNullOrWhiteSpace(args.CharacterName))
        {
            return GrailCommandResult.Fail(command.Kind, "缺少 GrailDeployArgs(角色名 + 前台/后台)。");
        }

        // 读备战席定位目标是必要的输入解析（槽位坐标沉在组件内），不是操作后确认。
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            context.WindowHandle,
            context.PreparationPageId,
            cancellationToken);
        var candidate = bench?.FirstOrDefault(item =>
            string.Equals(item.Character.Name, args.CharacterName, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            return GrailCommandResult.Fail(command.Kind, $"备战席未识别到 {args.CharacterName}。");
        }

        // 显式槽位模式（2026-09-02 用户令：决策层传槽位号=精确落位；落到占用槽=有意互换，
        // 结果由部署验证像素差分回报）。占用序模式保持原口径：槽满=事实回传，失败不计数。
        int slot;
        var explicitSlot = args.TargetSlot is not null;
        if (args.TargetSlot is { } requestedSlot)
        {
            slot = requestedSlot;
            var limit = args.Lane == PreparationLane.Front ? 4 : 6;
            if (slot < 0 || slot >= limit)
            {
                return GrailCommandResult.Fail(command.Kind,
                    $"槽位号 {slot + 1} 超出{(args.Lane == PreparationLane.Front ? "前台 1-4" : "后台 1-6")}范围。");
            }
        }
        else if (args.Lane == PreparationLane.Front)
        {
            if (_frontDeployCount >= 4)
            {
                return GrailCommandResult.Fail(command.Kind, "前台 4 槽已占满，无法再上场（处置归决策层）。");
            }

            slot = _frontDeployCount;
        }
        else
        {
            if (_backDeployCount >= 6)
            {
                return GrailCommandResult.Fail(command.Kind, "后台 6 槽已占满，无法再上场（处置归决策层）。");
            }

            slot = _backDeployCount;
        }

        var deployed = await preparationBoard.GrailDeployBenchCharacterAsync(
            context.WindowHandle,
            candidate,
            args.Lane,
            slot,
            context.PreparationPageId,
            cancellationToken);
        if (!deployed)
        {
            return GrailCommandResult.Fail(command.Kind, $"{args.CharacterName} 拖放输入未完成（组件返回失败）。");
        }

        if (!explicitSlot)
        {
            if (args.Lane == PreparationLane.Front)
            {
                _frontDeployCount++;
            }
            else
            {
                _backDeployCount++;
            }
        }

        return GrailCommandResult.Ok(command.Kind, new GrailCharacterFact(args.CharacterName, null, $"前台{slot + 1}"));
    }

    private async Task<GrailCommandResult> SellBenchAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        // 位置语义（2026-09-02 用户设计定案）：A3 槽位号(1基)=卖备战席该号位角色。
        if (command.Payload is GrailBenchSlotArgs slotArgs)
        {
            var benchBySlot = await preparationBoard.ReadStableBenchCharactersAsync(
                context.WindowHandle,
                context.PreparationPageId,
                cancellationToken);
            var bySlot = benchBySlot?.FirstOrDefault(item => item.BenchSlot == slotArgs.SlotIndex);
            if (bySlot is null)
            {
                return GrailCommandResult.Fail(command.Kind,
                    $"备战席 {slotArgs.SlotIndex + 1} 号位没有角色。");
            }

            var slotSold = await preparationBoard.GrailSellBenchCharacterAsync(
                context.WindowHandle,
                bySlot,
                context.PreparationPageId,
                cancellationToken);
            return slotSold
                ? GrailCommandResult.Ok(command.Kind,
                    $"已出售备战席 {slotArgs.SlotIndex + 1} 号位 {bySlot.Character.Name}。")
                : GrailCommandResult.Fail(command.Kind,
                    $"{bySlot.Character.Name} 出售拖放未完成（组件返回失败）。");
        }

        if (command.Payload is not GrailCharacterArgs args || string.IsNullOrWhiteSpace(args.CharacterName))
        {
            return GrailCommandResult.Fail(command.Kind, "缺少参数（备战席槽位号 或 角色名）。");
        }

        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            context.WindowHandle,
            context.PreparationPageId,
            cancellationToken);
        var candidate = bench?.FirstOrDefault(item =>
            string.Equals(item.Character.Name, args.CharacterName, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            return GrailCommandResult.Fail(command.Kind, $"备战席未识别到 {args.CharacterName}。");
        }

        var sold = await preparationBoard.GrailSellBenchCharacterAsync(
            context.WindowHandle,
            candidate,
            context.PreparationPageId,
            cancellationToken);
        return sold
            ? GrailCommandResult.Ok(command.Kind, args.CharacterName)
            : GrailCommandResult.Fail(command.Kind, $"{args.CharacterName} 出售拖放未完成（组件返回失败）。");
    }

    private async Task<GrailCommandResult> BuyXpAsync(
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        var done = await executor.ExecuteBuyXpAsync(context.WindowHandle, cancellationToken);
        return done
            ? GrailCommandResult.Ok(GrailCommandKind.A6)
            : GrailCommandResult.Fail(GrailCommandKind.A6, "买经验输入未完成（组件返回失败；金币是否足够由决策层对账）。");
    }

    private async Task<GrailCommandResult> AbandonAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        if (runAbandoner is null)
        {
            return GrailCommandResult.Fail(GrailCommandKind.A9, "未注入 IRunAbandoner，无法弃局。");
        }

        Task<RejectedOpeningRecoveryResult> abandonTask;
        try
        {
            abandonTask = runAbandoner.AbandonCurrentRunAsync(
                context.WindowHandle,
                cancellationToken,
                // 1.2.119（审计簇 A3/D1）：决策层可经 A9 Payload 携带弃局原因
                //（如四.13a 策略弃局"血X≤86/策略Y"）——无 Payload 时回退字面量。
                reason: command.Payload as string ?? "A9:决策层弃局指令");
        }
        catch (Exception abandonStartError) when (abandonStartError is not OperationCanceledException)
        {
            // P1-2（1.2.71 运行时审计）：弃局链组件异常（GPU TDR/COM 失效类）照 M1
            // 先例包装成失败事实——裸抛会让决策层整循环死亡（一次异常即死且无自愈）。
            return GrailCommandResult.Fail(
                GrailCommandKind.A9,
                $"弃局序列启动异常（{abandonStartError.GetType().Name}）：{abandonStartError.Message}（常见诱因=游戏窗口失效/GPU 崩溃，可先 I1 核实画面）。");
        }

        GrailCommandResult abandonResult;
        try
        {
            var result = await abandonTask;
            // 审查 P2：弃局序列失败时必须如实报失败（防"局没弃掉回执却成功"掩盖看门狗目标）。
            abandonResult = result.Status == RejectedOpeningRecoveryStatus.Recovered
                ? GrailCommandResult.Ok(GrailCommandKind.A9, result)
                : GrailCommandResult.Fail(GrailCommandKind.A9, $"弃局未完成（{result.Status}）：{result.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception abandonRunError)
        {
            // P1-2（1.2.71）：在途异常同样包装（同上）。
            abandonResult = GrailCommandResult.Fail(
                GrailCommandKind.A9,
                $"弃局序列执行异常（{abandonRunError.GetType().Name}）：{abandonRunError.Message}（常见诱因=游戏窗口失效/GPU 崩溃，可先 I1 核实画面）。");
        }

        return abandonResult;
    }

    /// <summary>
    /// A16 盛会之星升档选择框消除（1.2.115，用户令单功能隔离测试）：弹框在屏才应答
    /// （任选角色+确认选择），不在屏=Ok(NotOnScreen) 零点击；绝不弃局、不碰状态机。
    /// 回执区分"已消除/不在屏"，失败如实 Fail。
    /// </summary>
    private async Task<GrailCommandResult> DismissGalaPopupAsync(
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        if (galaPopupHandler is null)
        {
            return GrailCommandResult.Fail(GrailCommandKind.A16, "未注入 IGalaBondPopupHandler，无法消除。");
        }

        try
        {
            return await galaPopupHandler.DismissGalaBondPopupIfUpAsync(
                    context.WindowHandle, cancellationToken) switch
            {
                GalaBondDismissOutcome.Dismissed => GrailCommandResult.Ok(
                    GrailCommandKind.A16,
                    "盛会之星升档选择框已应答关闭（任选角色+确认选择）。"),
                GalaBondDismissOutcome.NotOnScreen => GrailCommandResult.Ok(
                    GrailCommandKind.A16,
                    "盛会之星升档选择框不在屏（识别未命中，零点击）。"),
                _ => GrailCommandResult.Fail(
                    GrailCommandKind.A16,
                    "应答后弹框仍在屏——候选点位耗尽（见 RecoveryGalaPopupDismissFailed 事件）。"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // 与 A9 P1-2 同款包装：组件异常（GPU TDR/窗口失效类）回事实不裸抛。
            return GrailCommandResult.Fail(
                GrailCommandKind.A16,
                $"消除序列异常（{error.GetType().Name}）：{error.Message}（常见诱因=游戏窗口失效/GPU 崩溃，可先 I1 核实画面）。");
        }
    }

    private async Task<GrailCommandResult> SelectStrategyAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        if (command.Payload is not GrailStrategyArgs args || args.PreferredStrategyIds.Count == 0)
        {
            return GrailCommandResult.Fail(command.Kind, "缺少 GrailStrategyArgs(策略 ID 集)。");
        }

        var result = await rewardStage.SelectInvestmentStrategyAsync(
            context.WindowHandle,
            args.PreferredStrategyIds,
            cancellationToken);
        return GrailCommandResult.Ok(command.Kind, result);
    }
}
