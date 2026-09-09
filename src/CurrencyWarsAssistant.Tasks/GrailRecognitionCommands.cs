using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 识别层指令适配器（I1~I10）：要数据，不动游戏。
/// 优先读当帧识别（监听器最新分析）；当帧 Unknown 时回退持有器 last-known 缓存，
/// 陈旧度窗口与 GrailRunLoop 的组装口径一致（15 秒），由决策层据 CapturedAt 统一做防御。
/// 整快照类读数（I7/I9/I10）直接复用 GrailSnapshotAssembler，不另起识别管线。
/// 识别失败返回「未知」事实，不自行重试上升（思想七；既有组件内部的重试保持不变）。
/// </summary>
public sealed class GrailRecognitionCommands(
    GrailRecognitionListener listener,
    GrailRunStateHolder stateHolder,
    GameDataCatalog gameData,
    PreparationBoardController preparationBoard,
    RewardStageAutomationController rewardStage,
    WishTrialSelectionAutomation trialSelection,
    IGameCapture? capture = null,
    IGameWindowService? windowService = null) : IGrailCommandHandler
{
    /// <summary>last-known 缓存的陈旧度窗口（与 GrailRunLoop.AssembleLatest 的 15 秒口径一致）。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    public async Task<GrailCommandResult> HandleAsync(
        GrailCommand command,
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        return command.Kind switch
        {
            GrailCommandKind.I1 => ReadPage(),
            GrailCommandKind.I2 => await ReadShelfAsync(context.WindowHandle, cancellationToken),
            GrailCommandKind.I3 => await ReadBenchAsync(context, cancellationToken),
            GrailCommandKind.I4 => ReadField(),
            GrailCommandKind.I5 => ReadMeter(isHealth: true),
            GrailCommandKind.I6 => ReadMeter(isHealth: false),
            GrailCommandKind.I7 => await ReadBadgesAsync(context, cancellationToken),
            GrailCommandKind.I9 or GrailCommandKind.I10 =>
                AssembleFromSnapshot(command.Kind, context),
            GrailCommandKind.I8 => ReadWishDialog(),
            _ => GrailCommandResult.Fail(command.Kind, $"识别层不受理指令 {command.Kind}。"),
        };
    }

    /// <summary>
    /// I7 未携带星徽（1.2.98，用户 2026-09-03 拍板"星徽识别只修一件事"落地）：
    /// 装备识别链（坑 36"多件只认第一件"回归）对物品栏星徽恒读 0——2026-09-06 实锤
    /// （4 个 019 局物品栏有徽而 I7 全 0、A4 全晚未发；Locator 对同日实拍帧回放 HIT 0.62~0.85）。
    /// 修复=快照读 0 时用 StarBadgeLocator 实拍定位接管（A4 组件本体本就直接走 Locator，
    /// 全链唯一断点在此）；Locator 未命中/异常时回落装备识别链读数（诚实兜底）。
    /// </summary>
    private async Task<GrailCommandResult> ReadBadgesAsync(
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        var result = AssembleFromSnapshot(GrailCommandKind.I7, context);
        if (result.Error is not null || result.Payload is not GrailBadgeFact snapshotFact)
        {
            return result;
        }

        if (capture is not null && windowService is not null && snapshotFact.Uncarried == 0)
        {
            try
            {
                var window = windowService.Refresh(context.WindowHandle);
                if (window is not null && window.IsReadyForAutomation)
                {
                    var frame = await capture.CaptureAsync(window, cancellationToken);
                    if (StarBadgeLocator.TryLocate(frame, out var center, out var score))
                    {
                        rewardStage.PublishGrailTelemetry(
                            "I7StarBadgeLocated",
                            $"物品栏实拍定位到命运圣杯星徽（{center.X},{center.Y}，模板分 {score:F2}）——装备识别链读 0 已由定位器接管，未携带=1。");
                        var carried = snapshotFact.TotalObtained - snapshotFact.Uncarried;
                        return GrailCommandResult.Ok(
                            GrailCommandKind.I7,
                            new GrailBadgeFact(carried + 1, 1));
                    }

                    rewardStage.PublishGrailTelemetry(
                        "I7StarBadgeNotLocated",
                        $"物品栏实拍未定位到星徽（模板分峰值 {score:F2}）——维持装备识别链读数（未携带=0）。");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                rewardStage.PublishGrailTelemetry(
                    "I7StarBadgeProbeFailed",
                    $"星徽实拍探测异常（{exception.Message}）——回落装备识别链读数。",
                    TaskEventLevel.Warning);
            }
        }

        return result;
    }

    private GrailCommandResult ReadPage()
    {
        var analysis = listener.LatestAnalysis;
        // 帧龄必须随页一并返回（2026-09-02 实测事故）：识别流冻结时 LatestAnalysis
        // 停在最后一帧，I1 若只报页 ID，下游会把几分钟前的旧帧当现状。
        var capturedAt = analysis?.Snapshot.AsOf;
        var isStale = capturedAt is null ||
                      DateTimeOffset.Now - capturedAt.Value > StaleAfter;
        return GrailCommandResult.Ok(
            GrailCommandKind.I1,
            new GrailPageFact(
                analysis?.Snapshot.PageId.Value,
                listener.IsWishDialogOpen,
                capturedAt,
                isStale));
    }

    private async Task<GrailCommandResult> ReadShelfAsync(
        nint windowHandle,
        CancellationToken cancellationToken)
    {
        // 被动读货架：商店未打开时返回失败事实，绝不主动开店（开店是 A 类动作）。
        var slots = await rewardStage.ReadStableShopAsync(windowHandle, consumedSlots: null, cancellationToken);
        if (slots is null)
        {
            return GrailCommandResult.Fail(GrailCommandKind.I2, "商店未打开或快照读取失败（识别层不主动开店）。");
        }

        var facts = slots
            .Select((slot, index) => new GrailCharacterFact(
                slot.Character?.Name,
                slot.Character?.Costs?.Min(),
                $"货架{index + 1}"))
            .ToList();
        return GrailCommandResult.Ok(GrailCommandKind.I2, facts);
    }

    private async Task<GrailCommandResult> ReadBenchAsync(
        GrailCommandContext context,
        CancellationToken cancellationToken)
    {
        var bench = await preparationBoard.ReadStableBenchCharactersAsync(
            context.WindowHandle,
            context.PreparationPageId,
            cancellationToken);
        if (bench is null)
        {
            return GrailCommandResult.Fail(GrailCommandKind.I3, "备战席读取失败（页门禁不符或识别不稳定）。");
        }

        var facts = bench
            .Select(item => new GrailCharacterFact(
                item.Character.Name,
                item.Character.Costs?.Min(),
                $"备战席{item.BenchSlot + 1}"))
            .ToList();
        return GrailCommandResult.Ok(GrailCommandKind.I3, facts);
    }

    private GrailCommandResult ReadField()
    {
        var analysis = listener.LatestAnalysis;
        if (analysis is not { } frame || !IsPreparationFrame(frame))
        {
            return GrailCommandResult.Fail(GrailCommandKind.I4, "当前非备战页帧，阵容读数不可信（门禁与 RunLoop 同口径）。");
        }

        var formation = frame.OperationalState?.Formation;
        if (formation?.Status is not (ObservationStatus.Known or ObservationStatus.Stale)
            || formation.Value is null)
        {
            return GrailCommandResult.Fail(GrailCommandKind.I4, "无可用阵容识别帧。");
        }

        var facts = new List<GrailCharacterFact>();
        var index = 0;
        foreach (var slot in formation.Value)
        {
            if (slot.Zone is not (FormationZone.Front or FormationZone.Back)
                || string.IsNullOrWhiteSpace(slot.CharacterId))
            {
                continue;
            }

            var character = Resolve(slot.CharacterId);
            facts.Add(new GrailCharacterFact(
                character?.Name,
                character?.Costs?.Min(),
                $"{(slot.Zone == FormationZone.Front ? "前台" : "后台")}{slot.SlotIndex + 1}"));
            index++;
        }

        return GrailCommandResult.Ok(GrailCommandKind.I4, facts);
    }

    /// <summary>I5/I6：当帧 Known 则回填持有器并采用；否则回 last-known 缓存（可能为 null=未知）。</summary>
    private GrailCommandResult ReadMeter(bool isHealth)
    {
        var now = DateTimeOffset.Now;
        // 09-10 夜审（终态金 1→32→1 幻想实锤）：LatestAnalysis 是管线缓存帧，可能
        // 滞后真实盘面数十秒——捕获与回执时间戳一律用帧自身时刻（AsOf，与 I1 同
        // 口径），墙钟仅在无帧时兜底；否则滞后帧以"新钟"伪装新鲜污染持有器。
        var asOf = listener.LatestAnalysis?.Snapshot.AsOf ?? now;
        if (isHealth)
        {
            var state = listener.LatestAnalysis?.OperationalState;
            if (state is not null && state.Health.Status == ObservationStatus.Known)
            {
                stateHolder.CaptureHealth(state.Health.Value, asOf);
                return GrailCommandResult.Ok(GrailCommandKind.I5, new GrailMeterFact(state.Health.Value, asOf));
            }

            var (health, healthAt) = stateHolder.PeekHealth();
            return GrailCommandResult.Ok(GrailCommandKind.I5, new GrailMeterFact(health, healthAt));
        }

        var economy = listener.LatestAnalysis?.Snapshot.Economy;
        if (economy is { Status: ObservationStatus.Known })
        {
            stateHolder.CaptureGold(economy.Value, asOf);
            return GrailCommandResult.Ok(GrailCommandKind.I6, new GrailMeterFact(economy.Value, asOf));
        }

        var (gold, goldAt) = stateHolder.PeekGold();
        return GrailCommandResult.Ok(GrailCommandKind.I6, new GrailMeterFact(gold, goldAt));
    }

    private GrailCommandResult ReadWishDialog() =>
        GrailCommandResult.Ok(GrailCommandKind.I8, trialSelection.LatestTrialInfo);

    /// <summary>备战页门禁（与 GrailRunLoop.AssembleLatest 同口径）：商店/战斗/结算帧的阵容被识别层
    /// 强制 Unknown，用其组装会产出空阵容假事实，必须拒绝。</summary>
    private static bool IsPreparationFrame(ScreenshotAnalysisResult? analysis) =>
        !string.IsNullOrEmpty(analysis?.Snapshot.PageId.Value)
        && analysis.Snapshot.PageId.Value.StartsWith("preparation_", StringComparison.OrdinalIgnoreCase);

    /// <summary>I7/I9/I10：复用组装器产出整快照，再按指令投影（组装是纯计算，不触碰游戏）。</summary>
    private GrailCommandResult AssembleFromSnapshot(GrailCommandKind kind, GrailCommandContext context)
    {
        var analysis = listener.LatestAnalysis;
        if (analysis is not { } frame || !IsPreparationFrame(frame))
        {
            return GrailCommandResult.Fail(kind, "当前非备战页帧，快照读数不可信（门禁与 RunLoop 同口径）。");
        }

        if (frame.OperationalState is not { } state)
        {
            return GrailCommandResult.Fail(kind, "无可用识别帧，无法组装快照。");
        }

        // 1.2.31 帧龄门禁（审查修复①）：识别流停滞时心跳会持续供给旧帧，
        // 阵容读数必须与 I1 同口径拒绝陈旧（血/金/人口本就有 15s 窗口，阵容此前无门禁）。
        if (frame.Snapshot.AsOf is { } frameAt
            && DateTimeOffset.Now - frameAt > StaleAfter)
        {
            return GrailCommandResult.Fail(kind,
                $"阵容识别帧陈旧（{(DateTimeOffset.Now - frameAt).TotalSeconds:F0} 秒无新帧），读数不可信，请稍后重试。");
        }

        var snapshot = GrailSnapshotAssembler.Assemble(
            state,
            frame.Snapshot,
            gameData,
            stateHolder,
            context.Goal,
            DateTimeOffset.Now,
            StaleAfter);
        return kind switch
        {
            GrailCommandKind.I7 => GrailCommandResult.Ok(kind,
                new GrailBadgeFact(snapshot.TotalStarBadgesObtained, snapshot.UncarriedStarBadges)),
            GrailCommandKind.I9 => GrailCommandResult.Ok(kind,
                new GrailTierFact(snapshot.BondMemberCount, snapshot.DeployedBondMembers)),
            _ => GrailCommandResult.Ok(kind, snapshot),
        };
    }

    private CurrencyWarsCharacterData? Resolve(string? characterId) =>
        string.IsNullOrWhiteSpace(characterId)
            ? null
            : gameData.CurrencyWarsCharacters.FirstOrDefault(item =>
                string.Equals(item.Id, characterId, StringComparison.OrdinalIgnoreCase));
}
