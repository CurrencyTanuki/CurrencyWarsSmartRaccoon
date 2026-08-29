using System;
using System.Collections.Generic;
using System.Linq;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 把识别层产出（<see cref="Phase2OperationalState"/>）组装成决策层输入
/// （<see cref="FateGrailRunEngine.Snapshot"/>）的接线器（隔离新增，不碰主识别管线）。
/// <para>
/// 生产侧调用方式（执行层接入后）：
/// <code>
///   var snapshot = FateGrailSnapshotAssembler.Assemble(
///       state, gameData, goal, line, leftTrial, rightTrial, trialOneTaken, trialTwoTaken);
/// </code>
/// 本类只做"识别结果 → 决策事实"的搬运与换算，不触屏、不识别、不做决策。
/// </para>
/// </summary>
public static class FateGrailSnapshotAssembler
{
    /// <summary>
    /// 组装一局决策快照。
    /// </summary>
    /// <param name="state">识别层当前产出（环境/血量/阵容/策略…）。</param>
    /// <param name="gameData">游戏数据目录（角色费用/名字查询）。</param>
    /// <param name="goal">用户目标（A 单只 / B 全员）。</param>
    /// <param name="gold">当前金币（识别层金币读自 RunSnapshot.Economy / PreparationFormation，由上层传入）。</param>
    /// <param name="line">已确定的终点线（未知可传 <see cref="FateGrailRunEngine.EndLine.Undetermined"/>）。</param>
    /// <param name="leftTrial">祈愿试炼左侧名称（WishTrialSelectionAutomation 识别；无弹框传 null）。</param>
    /// <param name="rightTrial">祈愿试炼右侧名称（同上）。</param>
    /// <param name="trialOneTaken">试炼计数①：第 1 个祈愿试炼已选过（协调器内部状态）。</param>
    /// <param name="trialTwoTaken">试炼计数②：第 2 个祈愿试炼已选过（协调器内部状态）。</param>
    /// <param name="diodeTaken">是否已吃过「二极管276」+10（协调器/执行层维护）。</param>
    /// <param name="bodyAcquisitionFailed">J2：确认本体后仍失败（执行层/协调器维护）。</param>
    /// <returns>决策层快照；环境/血量/试炼名等缺识别时取安全默认（引擎内判重刷/推进）。</returns>
    public static FateGrailRunEngine.Snapshot Assemble(
        Phase2OperationalState state,
        GameDataCatalog gameData,
        FateGrailRunEngine.UserGoal goal,
        int gold = 0,
        FateGrailRunEngine.EndLine line = FateGrailRunEngine.EndLine.Undetermined,
        string? leftTrial = null,
        string? rightTrial = null,
        bool trialOneTaken = false,
        bool trialTwoTaken = false,
        bool diodeTaken = false,
        bool bodyAcquisitionFailed = false,
        bool hasStarBadge = false,
        int population = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(gameData);

        var bodyIds = CollectFiveCostBodyNames(state, gameData);
        var owned = CollectOwnedMembers(state, gameData);
        // 祈愿档位 BondTier = 已收集的圣杯羁绊角色数（用户 2026-08-25：每收集一个
        // 圣杯羁绊角色 → 羁绊等级 +1 → 获得一次抽取机会）。圣杯成员=凛/闪/Saber/Archer；
        // 第 5 档触发件=持有「命运圣杯星徽」(装备 _001，由上层识别后经 hasStarBadge 传入)。
        var memberCount = FateGrailShoppingPolicy.FateGrailMemberNames
            .Count(name => owned.Contains(name, StringComparer.OrdinalIgnoreCase));
        var bondTier = memberCount + (hasStarBadge ? 1 : 0);

        return new FateGrailRunEngine.Snapshot(
            EnvironmentId: KnownOrNull(state.InvestmentEnvironmentId),
            Hp: KnownOr(state.Health, 0),
            Gold: gold,
            OwnedMembers: owned,
            AvailableStrategyIds: CollectStrategyIds(state),
            Goal: goal,
            Line: line,
            HasBody5Cost: bodyIds.Count > 0,
            HasEightProjectors: false, // 已废弃：选奇迹代偿即保证 8 投影，无需识别确认
            DiodeTaken: diodeTaken,
            LeftTrial: leftTrial,
            RightTrial: rightTrial,
            TrialOneTaken: trialOneTaken,
            TrialTwoTaken: trialTwoTaken,
            HasStarBadge: hasStarBadge,// 星徽识别为普通装备链路（equipment_001）；上层把识别结果经本参数传入
            BodyAcquisitionFailed: bodyAcquisitionFailed,
            BondTier: bondTier,        // = 圣杯成员收集数（含星徽第5档），跟随进度自动推进
            FiveCostBodyIds: bodyIds,
            Population: population);
    }

    /// <summary>
    /// 从识别阵容（前台/后台/备战席/特殊）收集 5 费角色名集合。
    /// 用户 2026-08-25 定义：只要有 5 费角色（任一区域、1 星即可）即算"本体在池"；
    /// 含昔涟则奇迹代偿达成 = 全员三星五费（决策层分叉）。
    /// </summary>
    internal static IReadOnlySet<string> CollectFiveCostBodyNames(
        Phase2OperationalState state,
        GameDataCatalog gameData)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in FormationSlots(state))
        {
            var character = ResolveCharacter(gameData, slot.CharacterId);
            if (character is null)
            {
                continue;
            }

            if ((character.Costs ?? Array.Empty<int>()).Contains(5))
            {
                names.Add(character.Name);
            }
        }

        return names;
    }

    /// <summary>识别阵容里所有角色槽（前台/后台/备战席/特殊任一区域）。</summary>
    private static IEnumerable<FormationCharacterState> FormationSlots(
        Phase2OperationalState state)
    {
        var formation = state.Formation;
        if (formation?.Status != ObservationStatus.Known || formation.Value is null)
        {
            return [];
        }

        return formation.Value
            .Where(slot => !string.IsNullOrWhiteSpace(slot.CharacterId));
    }

    /// <summary>已持有角色名集合：识别到即持有（决策层 StorePurchaseNames 按名字匹配）。</summary>
    private static IReadOnlySet<string> CollectOwnedMembers(
        Phase2OperationalState state,
        GameDataCatalog gameData)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in FormationSlots(state))
        {
            var character = ResolveCharacter(gameData, slot.CharacterId);
            if (character is not null)
            {
                names.Add(character.Name);
            }
        }

        return names;
    }

    private static IReadOnlySet<string> CollectStrategyIds(
        Phase2OperationalState state) =>
        new HashSet<string>(
            KnownOr(state.InvestmentStrategyIds, []),
            StringComparer.OrdinalIgnoreCase);

    private static CurrencyWarsCharacterData? ResolveCharacter(
        GameDataCatalog gameData,
        string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return null;
        }

        return gameData.CurrencyWarsCharacters.FirstOrDefault(
            item => string.Equals(
                item.Id,
                characterId,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? KnownOrNull(
        Observation<string> observation) =>
        observation.Status == ObservationStatus.Known ? observation.Value : null;

    private static int KnownOr(
        Observation<int> observation,
        int fallback) =>
        observation.Status == ObservationStatus.Known ? observation.Value : fallback;

    private static IReadOnlyList<T> KnownOr<T>(
        Observation<IReadOnlyList<T>> observation,
        IReadOnlyList<T> fallback) =>
        observation.Status == ObservationStatus.Known
            ? observation.Value ?? fallback
            : fallback;
}