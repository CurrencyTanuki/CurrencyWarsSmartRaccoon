using System.IO;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.App;

public sealed record HistoricalDetailFieldRow(
    string Label,
    string Value,
    string Meta = "",
    string? IconPath = null)
{
    public bool HasMeta => !string.IsNullOrWhiteSpace(Meta);
}

public sealed record HistoricalDetailSectionViewModel(
    string Title,
    IReadOnlyList<HistoricalDetailFieldRow> Fields);

public sealed record HistoricalDetailNodeViewModel(
    string NodeId,
    string Subtitle,
    IReadOnlyList<HistoricalDetailSectionViewModel> Sections,
    bool IsFinalized);

public sealed record CompletedRunViewModel(
    string RunId,
    string Summary,
    DateTimeOffset CompletedAt,
    IReadOnlyList<HistoricalDetailFieldRow> ArchiveRows,
    IReadOnlyList<HistoricalDetailFieldRow> IdentityRows,
    IReadOnlyList<HistoricalDetailNodeViewModel> Nodes,
    string? DamageLine = null,
    string? GoldLine = null,
    string? HealthLine = null,
    IReadOnlyList<HistoricalNodeCardViewModel> NodeCards = null!);

/// <summary>
/// 历史对局窗口的"节点明细"卡片：一个节点一张卡，
/// 展示该节点变化的数值（金币/血量/行动/伤害/结算）与阵容/装备图标。
/// </summary>
public sealed record HistoricalNodeCardViewModel(
    string NodeId,
    string GoldDisplay,
    string HealthDisplay,
    string ActionDisplay,
    string DamageDisplay,
    string SettlementDisplay,
    IReadOnlyList<string> FrontIcons,
    IReadOnlyList<string> BackIcons,
    IReadOnlyList<string> BenchIcons,
    IReadOnlyList<string> EquipmentIcons);

public sealed record HistoricalDetailEconomyProjection(
    int? AbsoluteGold,
    int? GoldDelta,
    int? GoldSpent,
    int? GoldReward)
{
    public static HistoricalDetailEconomyProjection FromDashboard(
        HistoricalNodeDashboardEntry entry) => new(
        entry.AbsoluteGold,
        entry.GoldDeltaSincePreviousNode,
        entry.GoldSpentSincePreviousNode,
        entry.GoldReward);
}

internal sealed class HistoricalDetailPresentationBuilder(GameDataCatalog catalog)
{
    private readonly IReadOnlyDictionary<string, string> characterNames =
        catalog.CurrencyWarsCharacters.ToDictionary(
            item => item.Id,
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> environmentNames =
        catalog.InvestmentEnvironments.ToDictionary(
            item => item.Id,
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> strategyNames =
        catalog.InvestmentStrategies.ToDictionary(
            item => item.Id,
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> affixNames =
        catalog.EnemyAffixes.ToDictionary(
            item => item.Id,
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> competitorNames =
        catalog.Competitors.ToDictionary(
            item => item.Id,
            item => item.Name,
            StringComparer.OrdinalIgnoreCase);

    public HistoricalDetailSectionViewModel BuildIdentity(
        RunIdentityEvidence identity)
    {
        var rows = new List<HistoricalDetailFieldRow>
        {
            new(
                "投资环境",
                identity.InvestmentEnvironmentId is null
                    ? "未记录"
                    : ResolveName(
                        environmentNames,
                        identity.InvestmentEnvironmentId),
                identity.InvestmentEnvironmentId is null ? "未识别" : "已记录",
                ResolveIconPath("environment", identity.InvestmentEnvironmentId))
        };
        AddIdList(rows, "敌人阵营", identity.EnemyIds, competitorNames);
        AddIdList(rows, "负面词条", identity.EnemyAffixIds, affixNames, "affix");
        AddIdList(rows, "投资策略", identity.InvestmentStrategyIds, strategyNames, "strategy");
        return new HistoricalDetailSectionViewModel("开局信息", rows);
    }

    public HistoricalDetailSectionViewModel BuildArchiveMetadata(
        CompletedRunRecord run)
    {
        var rows = new List<HistoricalDetailFieldRow>
        {
            new("运行 ID", run.RunId),
            new("数据 Schema", run.SchemaVersion),
            new("归档版本", run.ArchiveVersion.ToString()),
            new("完成时间", run.CompletedAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")),
            new("归档状态", run.IsFinal ? "已完成" : "未完成"),
            new("结束页面", ValueOrUnknown(run.CompletionPageId)),
            new("结束节点", ValueOrUnknown(run.CompletionNodeId)),
            new("结束截图", ValueOrUnknown(run.CompletionScreenshotFile)),
            new("对局评级", ValueOrUnknown(run.RatingText)),
            new("节点数量", run.Nodes.Count.ToString()),
            new("来源分析文件", ListValue(run.SourceAnalysisFiles)),
            new("来源修订", ValueOrUnknown(run.SourceRevision)),
            new("归档不确定项", ListValue(run.Uncertainty)),
            new(
                "末帧快照",
                run.LastSnapshot is null
                    ? "未记录"
                    : $"{run.LastSnapshot.Stage.Value ?? "节点未知"} · " +
                      $"{run.LastSnapshot.PageId.Value ?? "页面未知"}",
                run.LastSnapshot is null
                    ? "原因=completed-run 未保存末帧快照"
                    : $"时间={run.LastSnapshot.AsOf:O}；" +
                      $"Schema={run.LastSnapshot.SchemaVersion}"),
            new(
                "末帧运行状态",
                run.LastOperationalState is null
                    ? "未记录"
                    : $"{run.LastOperationalState.PageFamily} / " +
                      $"{run.LastOperationalState.PageId ?? "页面未知"}",
                run.LastOperationalState is null
                    ? "原因=completed-run 未保存末帧运行状态"
                    : $"节点状态={ObservationSummary(run.LastOperationalState.NodeId)}")
        };
        return new HistoricalDetailSectionViewModel("对局归档", rows);
    }

    /// <summary>
    /// 构建历史对局窗口的"节点明细"卡片列表：一个节点一张卡，
    /// 展示该节点变化的数值（金币/血量/行动/伤害/结算）与阵容/装备图标。
    /// </summary>
    public IReadOnlyList<HistoricalNodeCardViewModel> BuildNodeCards(
        IReadOnlyList<CompletedRunNodeRecord> nodes)
    {
        var cards = new List<HistoricalNodeCardViewModel>(nodes.Count);
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var previous = index > 0 ? nodes[index - 1] : null;
            var snapshot = node.FinalPreparationSnapshot;
            var state = node.FinalPreparationState;
            var battle = node.FinalBattle;

            var gold = KnownValue(snapshot?.Economy);
            var previousGold = KnownValue(previous?.FinalPreparationSnapshot?.Economy);
            var goldDisplay = gold is null
                ? "未记录"
                : previousGold is { } pg && pg != gold
                    ? $"{pg} → {gold}"
                    : gold.Value.ToString();

            var health = KnownValue(snapshot?.Health);
            var previousHealth = KnownValue(previous?.FinalPreparationSnapshot?.Health);
            var healthDisplay = health is null
                ? "未记录"
                : previousHealth is { } ph
                    ? $"{health.Value}（{(health.Value - ph):+0;-0;0}）"
                    : health.Value.ToString();

            var action = KnownValue(snapshot?.ActionPoints);
            var actionDisplay = action is null ? "未记录" : action.Value.ToString();

            var damage = battle?.TotalDamage;
            var damageDisplay = damage is null
                ? "未记录"
                : damage.Value >= 1_000_000
                    ? $"{damage.Value / 1_000_000d:0.##}百万"
                    : $"{damage.Value:N0}";

            var topThree = battle?.FinalSettlementTopThree ?? [];
            var settlementDisplay = topThree.Count == 0
                ? "未记录"
                : string.Join(" / ", topThree
                    .OrderBy(item => item.Rank)
                    .Select(item => CharacterName(item.CharacterId ?? item.TemporaryId ?? "未知")));

            var formation = state?.Formation.Value ?? [];
            var front = ToIconPaths(formation, FormationZone.Front);
            var back = ToIconPaths(formation, FormationZone.Back);
            var bench = ToIconPaths(formation, FormationZone.Bench);

            var equipment = (snapshot?.EquipmentIds?.Value ?? Array.Empty<string>())
                .Select(id => ResolveIconPath("equipment", id))
                .Where(path => path is not null)
                .Cast<string>()
                .Take(8)
                .ToArray();

            cards.Add(new HistoricalNodeCardViewModel(
                node.NodeId,
                goldDisplay,
                healthDisplay,
                actionDisplay,
                damageDisplay,
                settlementDisplay,
                front,
                back,
                bench,
                equipment));
        }

        return cards;
    }

    private static string[] ToIconPaths(
        IReadOnlyList<FormationCharacterState> formation,
        FormationZone zone) =>
        formation
            .Where(character => character.Zone == zone)
            .OrderBy(character => character.SlotIndex)
            .Select(character => ResolveIconPath("character", character.CharacterId))
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();

    public static HistoricalDetailEconomyProjection BuildCompletedEconomy(
        IReadOnlyList<CompletedRunNodeRecord> nodes,
        int index)
    {
        var current = nodes[index];
        var previous = index > 0 ? nodes[index - 1] : null;
        var next = index + 1 < nodes.Count ? nodes[index + 1] : null;
        var currentGold = KnownValue(current.FinalPreparationSnapshot?.Economy);
        var previousGold = KnownValue(previous?.FinalPreparationSnapshot?.Economy);
        var currentSpend = KnownValue(current.FinalPreparationState?.CumulativeSpend) ??
                           KnownValue(current.FinalPreparationSnapshot?.CumulativeSpend);
        var previousSpend = KnownValue(previous?.FinalPreparationState?.CumulativeSpend) ??
                            KnownValue(previous?.FinalPreparationSnapshot?.CumulativeSpend);
        var endingGold = KnownValue(next?.FinalPreparationSnapshot?.Economy);
        return new HistoricalDetailEconomyProjection(
            endingGold,
            currentGold.HasValue && previousGold.HasValue
                ? currentGold.Value - previousGold.Value
                : null,
            index == 0
                ? currentSpend
                : currentSpend.HasValue && previousSpend.HasValue &&
                  currentSpend.Value >= previousSpend.Value
                    ? currentSpend.Value - previousSpend.Value
                    : null,
            current.FinalBattle?.GoldReward);
    }

    private static string ResolveName(
        IReadOnlyDictionary<string, string> names,
        string id) =>
        names.TryGetValue(id, out var name) ? name : id;

    private static void AddIdList(
        List<HistoricalDetailFieldRow> rows,
        string label,
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, string> names,
        string? iconCategory = null)
    {
        rows.Add(new HistoricalDetailFieldRow(
            label,
            ids.Count == 0
                ? "未记录"
                : string.Join(
                    "、",
                    ids.Select(id => ResolveName(names, id))),
            ids.Count == 0 ? "未识别" : "已记录",
            ids.Count == 1 ? ResolveIconPath(iconCategory, ids[0]) : null));
    }

    public HistoricalDetailNodeViewModel Build(
        HistoricalNodeDetailEntry entry,
        HistoricalDetailEconomyProjection? economy = null)
    {
        var snapshot = entry.LatestSnapshot;
        var state = entry.LatestState;
        var preparation = entry.LatestPreparationState ?? state;
        var battle = entry.FinalBattle ?? state?.FinalBattle.Value;
        var sections = new List<HistoricalDetailSectionViewModel>
        {
            Section("节点概况", BuildOverview(entry, snapshot, state, battle)),
            Section("经济与成长", BuildEconomy(snapshot, state, battle, economy)),
            Section("历史阵容与角色装备", BuildFormation(snapshot, preparation)),
            Section("装备栏与局内资源", BuildInventory(snapshot, preparation)),
            Section("构筑、羁绊与敌情", BuildBuildState(snapshot, preparation)),
            Section("最终战斗与表格数据", BuildBattle(battle)),
            Section("伤害来源明细", BuildDamage(battle)),
            Section("节点事件链", BuildNodeHistory(snapshot)),
            Section("识别状态、原始 OCR 与诊断", BuildRecognition(entry, state))
        };
        return new HistoricalDetailNodeViewModel(
            entry.NodeId,
            battle is null
                ? $"实时记录中 · 更新 {entry.UpdatedAt:HH:mm:ss}"
                : $"节点已封存 · 更新 {entry.UpdatedAt:HH:mm:ss}",
            sections,
            battle is not null);
    }

    private IReadOnlyList<HistoricalDetailFieldRow> BuildOverview(
        HistoricalNodeDetailEntry entry,
        RunSnapshot? snapshot,
        Phase2OperationalState? state,
        FinalNodeBattleState? battle)
    {
        var rows = new List<HistoricalDetailFieldRow>
        {
            new("运行 ID", entry.RunId),
            new("节点", entry.NodeId),
            new("更新时间", entry.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss.fff")),
            new("快照 Schema", snapshot?.SchemaVersion ?? "未记录"),
            new("快照时间", snapshot?.AsOf.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "未记录"),
            new("页面族", state?.PageFamily.ToString() ?? "未记录"),
            new("页面 ID", state?.PageId ?? snapshot?.PageId.Value ?? "未记录"),
            new("节点类型", battle is null
                ? "数据不足"
                : battle.IsRewardNode ? "奖励节点" : "战斗节点",
                battle is null ? "原因=节点尚未封存，无法确认最终类型" : ""),
            new("节点最终状态", battle is null
                ? "未封存"
                : battle.IsComplete ? "已封存" : "残缺封存",
                battle is null ? "原因=尚未收到最终战斗状态" : "")
        };
        Add(rows, "节点识别", snapshot?.Stage);
        Add(rows, "当前血量", snapshot?.Health);
        Add(rows, "当前行动", snapshot?.ActionPoints);
        Add(rows, "当前节点伤害", snapshot?.CurrentNodeDamage, value =>
            FormatDamage(value));
        Add(rows, "敌人难度", state?.EnemyDifficulty);
        Add(rows, "利息", state?.Interest);
        if (battle is not null)
        {
            rows.Add(new HistoricalDetailFieldRow(
                "封存质量",
                battle.IsComplete && battle.CanDriveDecisions
                    ? "完整可信"
                    : "残缺/待复查",
                $"证据置信度 {FormatConfidence(battle.Evidence.Confidence)}"));
        }

        return rows;
    }

    private static IReadOnlyList<HistoricalDetailFieldRow> BuildEconomy(
        RunSnapshot? snapshot,
        Phase2OperationalState? state,
        FinalNodeBattleState? battle,
        HistoricalDetailEconomyProjection? economy)
    {
        var rows = new List<HistoricalDetailFieldRow>();
        Add(rows, "出战前金币", snapshot?.Economy);
        Add(rows, "累计花费", state?.CumulativeSpend ?? snapshot?.CumulativeSpend);
        rows.Add(OptionalIntegerRow(
            "节点最终金币",
            economy?.AbsoluteGold,
            "尚未取得下一节点备战金币，或该字段未进入归档"));
        rows.Add(SignedIntegerRow(
            "节点金币变化",
            economy?.GoldDelta,
            "缺少相邻节点的可靠金币快照"));
        rows.Add(OptionalIntegerRow(
            "节点金币花费",
            economy?.GoldSpent,
            "缺少相邻节点的可靠累计花费"));
        rows.Add(OptionalIntegerRow(
            "节点奖励",
            economy?.GoldReward ?? battle?.GoldReward,
            "结算页奖励未识别且无法由可靠节点差分回填"));
        Add(rows, "利息", state?.Interest);
        Add(rows, "等级与经验", state?.PlayerProgress, progress =>
            $"Lv.{progress.Level} · {progress.Experience}/{progress.ExperienceToNextLevel}");
        return rows;
    }

    private IReadOnlyList<HistoricalDetailFieldRow> BuildFormation(
        RunSnapshot? snapshot,
        Phase2OperationalState? state)
    {
        var rows = new List<HistoricalDetailFieldRow>();
        var formation = state?.Formation;
        if (formation?.Value is { Count: > 0 } characters)
        {
            AddFormationSummary(rows, "前台角色", characters, FormationZone.Front);
            AddFormationSummary(rows, "后台角色", characters, FormationZone.Back);
            AddFormationSummary(rows, "备战席角色", characters, FormationZone.Bench);
            rows.Add(new HistoricalDetailFieldRow(
                "出阵角色",
                CharacterList(characters
                    .Where(item => item.Zone is FormationZone.Front or FormationZone.Back)
                    .Select(item => item.CharacterId)),
                "按角色区域从最终备战阵容投影；不把备战席计入出阵角色"));
            foreach (var character in characters
                         .OrderBy(item => item.Zone)
                         .ThenBy(item => item.SlotIndex))
            {
                var equipment = character.EquipmentIds.Count == 0
                    ? "未记录"
                    : string.Join("、", character.EquipmentIds);
                rows.Add(new HistoricalDetailFieldRow(
                    $"{ZoneName(character.Zone)} {character.SlotIndex + 1}",
                    $"{CharacterName(character.CharacterId)} · " +
                    $"{(character.StarLevel.HasValue ? $"{character.StarLevel}星" : "星级未知")} · " +
                    $"装备：{equipment}",
                    $"{(character.CanDriveDecisions ? "已确认" : "候选")}（技术细节见诊断区）",
                    ResolveIconPath("character", character.CharacterId)));

                for (var slot = 0; slot < 3; slot++)
                {
                    var slotState = character.FinalEquipmentSlots.FirstOrDefault(item =>
                        item.SlotIndex == slot);
                    var equipmentId = slotState?.EquipmentId ??
                                      character.EquipmentIds.ElementAtOrDefault(slot);
                    var value = slotState?.Occupancy switch
                    {
                        EquipmentSlotOccupancy.Empty => "空槽",
                        EquipmentSlotOccupancy.Occluded => "暂不可见",
                        EquipmentSlotOccupancy.Unknown => "未记录",
                        EquipmentSlotOccupancy.Equipped => equipmentId ?? "未知装备",
                        _ => equipmentId ?? "未记录"
                    };
                    rows.Add(new HistoricalDetailFieldRow(
                        $"{ZoneName(character.Zone)} {character.SlotIndex + 1} · 装备槽 {slot + 1}",
                        value,
                        slotState is null
                            ? equipmentId is null
                                ? "当前帧未提供该槽状态，不能把缺失当作空槽"
                                : "旧格式仅保存装备ID；槽位状态未记录"
                            : slotState.Occupancy == EquipmentSlotOccupancy.Equipped
                                ? "已记录"
                                : "未识别",
                        ResolveIconPath("equipment", equipmentId)));
                }
            }
        }
        else
        {
            rows.Add(UnknownRow("阵容", formation));
            rows.Add(new HistoricalDetailFieldRow("前台角色", "未记录", "原因=阵容识别不可用"));
            rows.Add(new HistoricalDetailFieldRow("后台角色", "未记录", "原因=阵容识别不可用"));
            rows.Add(new HistoricalDetailFieldRow("备战席角色", "未记录", "原因=阵容识别不可用"));
            rows.Add(new HistoricalDetailFieldRow("出阵角色", "未记录", "原因=阵容识别不可用"));
        }

        // 原始内部 ID 集合移至诊断区（BuildRecognition）展示，普通界面不暴露内部 ID。
        return rows;
    }

    private void AddFormationSummary(
        ICollection<HistoricalDetailFieldRow> rows,
        string label,
        IReadOnlyList<FormationCharacterState> characters,
        FormationZone zone)
    {
        var selected = characters
            .Where(item => item.Zone == zone)
            .OrderBy(item => item.SlotIndex)
            .Select(item => item.CharacterId)
            .ToArray();
        rows.Add(new HistoricalDetailFieldRow(
            label,
            selected.Length == 0 ? "未记录" : CharacterList(selected),
            selected.Length == 0
                ? "尚未识别到角色"
                : $"已确认 {selected.Length} 个角色"));
    }

    private static IReadOnlyList<HistoricalDetailFieldRow> BuildInventory(
        RunSnapshot? snapshot,
        Phase2OperationalState? state)
    {
        var rows = new List<HistoricalDetailFieldRow>();
        Add(rows, "背包/装备栏", snapshot?.EquipmentIds, ListValue);
        Add(rows, "简易装备", state?.SimpleEquipmentIds, ListValue);
        Add(rows, "拆解工具", state?.DismantleToolCount);
        Add(rows, "特殊物品", snapshot?.SpecialItemIds, ListValue);
        Add(rows, "专家顾问", snapshot?.ExpertAdvisorIds, ListValue);
        var inventory = state?.InventorySlots ?? snapshot?.InventorySlots;
        if (inventory?.Value is { Count: > 0 } slots)
        {
            foreach (var slot in slots.OrderBy(item => item.SlotIndex))
            {
                var value = slot.Occupancy switch
                {
                    EquipmentSlotOccupancy.Empty => "空槽",
                    EquipmentSlotOccupancy.Equipped =>
                        slot.ItemId ?? "未知物品",
                    EquipmentSlotOccupancy.Occluded => "暂不可见",
                    _ => "识别失败"
                };
                rows.Add(new HistoricalDetailFieldRow(
                    $"背包槽位 {slot.SlotIndex + 1}",
                    value,
                    slot.Occupancy == EquipmentSlotOccupancy.Unknown
                        ? "未识别"
                        : $"类型={slot.ItemKind}（技术细节见诊断区）"));
            }
        }
        else
        {
            rows.Add(new HistoricalDetailFieldRow(
                "背包逐槽状态",
                "未记录",
                inventory is null
                    ? "原因=节点没有背包状态"
                    : $"状态={inventory.Status}；原因={ListValue(inventory.Uncertainty)}"));
        }
        return rows;
    }

    private IReadOnlyList<HistoricalDetailFieldRow> BuildBuildState(
        RunSnapshot? snapshot,
        Phase2OperationalState? state)
    {
        var rows = new List<HistoricalDetailFieldRow>();
        var environmentId = state?.InvestmentEnvironmentId ??
            snapshot?.InvestmentEnvironmentId;
        var environmentValue = environmentId?.Value;
        rows.Add(new HistoricalDetailFieldRow(
            "投资环境",
            string.IsNullOrWhiteSpace(environmentValue)
                ? "未记录"
                : EnvironmentName(environmentValue),
            string.IsNullOrWhiteSpace(environmentValue) ? "未识别" : "已记录",
            ResolveIconPath("environment", environmentValue)));
        var strategies = state?.InvestmentStrategyIds ?? snapshot?.InvestmentStrategyIds;
        Add(rows, "本节点已获得投资策略", strategies, values =>
            ListValue(values.Select(StrategyName)));
        if (strategies?.Value is { } strategyIds)
        {
            for (var index = 0; index < strategyIds.Count; index++)
            {
                rows.Add(new HistoricalDetailFieldRow(
                    $"投资策略 {index + 1}",
                    StrategyName(strategyIds[index]),
                    "已记录",
                    ResolveIconPath("strategy", strategyIds[index])));
            }
        }

        var affixes = state?.NegativeAffixIds;
        Add(rows, "已确认负面词条集合", affixes, values =>
            ListValue(values.Select(AffixName)));
        var affixSlots = (state?.NamedContent ?? [])
            .Where(item => item.Kind == Phase2NamedContentKind.NegativeAffix)
            .GroupBy(item => item.SlotKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Confidence).First(),
                StringComparer.OrdinalIgnoreCase);
        var pendingAffixSlots = (state?.PendingIcons ?? [])
            .Where(item => item.Category == PendingIconCategory.NegativeAffix)
            .GroupBy(item => item.SlotKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Confidence).First(),
                StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 4; index++)
        {
            var slotKey = $"{Phase2NamedContentKind.NegativeAffix}-{index + 1}";
            affixSlots.TryGetValue(slotKey, out var recognized);
            pendingAffixSlots.TryGetValue(slotKey, out var pending);
            var affixId = recognized?.ObjectId ?? pending?.TemplateId;
            rows.Add(new HistoricalDetailFieldRow(
                $"负面词条槽位 {index + 1}",
                affixId is null ? "未记录" : AffixName(affixId),
                affixId is null ? "未识别" : "已记录",
                ResolveIconPath("affix", affixId)));
        }
        Add(rows, "羁绊 ID", snapshot?.SynergyIds, ListValue);
        if (state?.ActiveSynergies.Value is { Count: > 0 } synergies)
        {
            foreach (var synergy in synergies)
            {
                rows.Add(new HistoricalDetailFieldRow(
                    $"羁绊 {synergy.SlotKey}",
                    $"{synergy.SynergyId ?? "未知"} · {synergy.ActiveCount?.ToString() ?? "?"}/" +
                    $"{synergy.NextThreshold?.ToString() ?? "?"}",
                    "已记录",
                    ResolveIconPath("bond", synergy.SynergyId)));
            }
        }
        else
        {
            rows.Add(UnknownRow("激活羁绊", state?.ActiveSynergies));
        }

        Add(rows, "敌人阵营", snapshot?.EnemyIds, values =>
            ListValue(values.Select(CompetitorName)));
        foreach (var named in state?.NamedContent ?? [])
        {
            var namedIcon = named.Kind switch
            {
                Phase2NamedContentKind.NegativeAffix =>
                    ResolveIconPath("affix", named.ObjectId ?? named.StandardName),
                Phase2NamedContentKind.InvestmentStrategy =>
                    ResolveIconPath("strategy", named.ObjectId ?? named.StandardName),
                Phase2NamedContentKind.InvestmentEnvironment =>
                    ResolveIconPath("environment", named.ObjectId ?? named.StandardName),
                _ => null
            };
            rows.Add(new HistoricalDetailFieldRow(
                $"命名内容 {named.Kind}/{named.SlotKey}",
                named.StandardName ?? named.ObjectId ?? "未解析",
                named.Status == ObservationStatus.Known ? "已记录" : "未识别",
                namedIcon));
        }

        return rows;
    }

    private static IReadOnlyList<HistoricalDetailFieldRow> BuildBattle(
        FinalNodeBattleState? battle)
    {
        if (battle is null)
        {
            return
            [
                new("最终战斗", "未记录", "原因=节点尚未封存或最终战斗识别失败"),
                new("最终伤害", "未记录", "原因=没有可用最终战斗"),
                new("最终剩余行动值", "未记录", "原因=没有可用最终战斗"),
                new("理论出伤极限", "未记录", "原因=缺少最终伤害或行动值"),
                new("完美通关状态", "数据不足", "不能把 Unknown 当作未完美"),
                new("战前/战后血量", "未记录", "原因=没有可用最终战斗"),
                new("节点奖励", "未记录", "原因=结算奖励未识别")
            ];
        }

        return
        [
            new("节点", battle.NodeId),
            new("节点类型", battle.IsRewardNode ? "奖励节点" : "战斗节点"),
            new("最终记录状态", battle.IsComplete ? "完整" : "残缺"),
            new("可驱动决策", battle.CanDriveDecisions ? "是" : "否"),
            new("原始总伤害", FormatDamage(battle.TotalDamage)),
            new("已记录来源合计", FormatDamage(battle.AllRecordedDamage)),
            new("最终伤害", FormatDamage(battle.SelectedDamage ?? battle.TotalDamage)),
            new("战斗画面候选", FormatDamage(battle.BattleScreenDamageCandidate)),
            new("结算页候选", FormatDamage(battle.SettlementScreenDamageCandidate)),
            new("最终采用来源", battle.SelectedDamageSource.ToString()),
            new("剩余轮数", battle.RemainingActionValue?.RemainingRounds.ToString() ?? "未记录"),
            new("当前轮行动值", battle.RemainingActionValue?.CurrentRoundActionValue.ToString() ?? "未记录"),
            new("最终剩余行动值", battle.RemainingActionValue?.TotalActionValue.ToString() ?? "未记录"),
            new("节点奖励", battle.GoldReward?.ToString() ?? "未记录"),
            new("战前血量", battle.PreBattleHealth?.ToString() ?? "未记录"),
            new("战后血量", battle.HealthDepleted
                ? "已耗尽（具体变化未知）"
                : battle.PostBattleHealth?.ToString() ?? "未记录"),
            new("血量变化", battle.HealthDepleted
                ? "下降（具体数值未知）"
                : Signed(battle.HealthDelta)),
            new("完美通关状态", battle.ClearStatus switch
            {
                NodeClearStatus.Perfect => "完美通关",
                NodeClearStatus.NotPerfect => "未完美通关",
                _ => "数据不足"
            }),
            new("理论出伤极限", FormatDamage(battle.TheoreticalDamageLimit)),
            new("理论值规则", battle.TheoreticalDamageRule ?? "未记录"),
            new("理论值质量", battle.TheoreticalDamageQuality.ToString()),
            new("基础/有效最大行动", $"{battle.BaseMaximumActionValue?.ToString() ?? "?"} / " +
                $"{battle.EffectiveMaximumActionValue?.ToString() ?? "?"}"),
            new("确认增加行动", battle.ConfirmedActionIncrease?.ToString() ?? "未记录"),
            new("奖励节点", battle.IsRewardNode ? "是" : "否"),
            new("血量耗尽", battle.HealthDepleted ? "是" : "否"),
            new("捕获时间", battle.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss.fff")),
            new("最终战斗证据", EvidenceSummary(battle.Evidence)),
            new("最终战斗不确定项", ListValue(battle.FinalUncertainty))
        ];
    }

    private IReadOnlyList<HistoricalDetailFieldRow> BuildDamage(
        FinalNodeBattleState? battle)
    {
        if (battle is null)
        {
            return
            [
                new HistoricalDetailFieldRow(
                    "各角色最终伤害",
                    "未记录",
                    "原因=节点尚未封存或最终伤害面板未识别"),
                new HistoricalDetailFieldRow(
                    "羁绊最终伤害",
                    "未记录",
                    "原因=节点尚未封存或羁绊伤害图标未识别"),
                new HistoricalDetailFieldRow(
                    "未知来源最终伤害",
                    "未记录",
                    "未知来源会保留临时编号、候选和裁剪证据")
            ];
        }

        var rows = new List<HistoricalDetailFieldRow>();
        rows.AddRange(battle.CharacterDamage.OrderBy(item => item.Rank).Select(item =>
            new HistoricalDetailFieldRow(
                $"角色第 {item.Rank} 名",
                $"{CharacterName(item.CharacterId ?? item.TemporaryId)} · {FormatDamage(item.Damage)}",
                $"{(item.CanDriveDecisions ? "已确认" : "候选")}（技术细节见诊断区）",
                ResolveIconPath("character", item.CharacterId ?? item.TemporaryId))));
        rows.AddRange(battle.FinalSynergyDamage.OrderBy(item => item.Rank).Select(item =>
            new HistoricalDetailFieldRow(
                $"羁绊第 {item.Rank} 名",
                $"{item.SynergyId ?? item.TemporaryId ?? "未知"} · {FormatDamage(item.Damage)}",
                $"{(item.CanDriveDecisions ? "已确认" : "候选")}（技术细节见诊断区）")));
        rows.AddRange(battle.FinalUnresolvedDamage.OrderBy(item => item.Rank).Select(item =>
            new HistoricalDetailFieldRow(
                $"未解析来源第 {item.Rank} 名",
                $"{item.SourceId ?? item.TemporaryId} · {FormatDamage(item.Damage)}",
                "未解析（技术细节见诊断区）")));
        rows.AddRange(battle.FinalSettlementTopThree.OrderBy(item => item.Rank).Select(item =>
            new HistoricalDetailFieldRow(
                $"结算页第 {item.Rank} 名",
                $"{CharacterName(item.CharacterId ?? item.TemporaryId)} · {FormatDamage(item.Damage)}",
                $"{(item.CanDriveDecisions ? "已确认" : "候选")}（技术细节见诊断区）")));
        // 待解析图标/残缺字段/不确定项移至诊断区（BuildRecognition）展示。
        return rows.Count == 0
            ? [new HistoricalDetailFieldRow("伤害来源", "没有可用明细")]
            : rows;
    }

    private static IReadOnlyList<HistoricalDetailFieldRow> BuildNodeHistory(
        RunSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return [new HistoricalDetailFieldRow(
                "节点事件链",
                "未记录",
                "原因=节点没有最终快照")];
        }

        var rows = new List<HistoricalDetailFieldRow>
        {
            new(
                "已应用事件",
                ListValue(snapshot.AppliedEventIds),
                snapshot.AppliedEventIds.Count == 0
                    ? "当前快照没有保存事件ID"
                    : $"共 {snapshot.AppliedEventIds.Count} 项"),
            new(
                "快照诊断",
                ListValue(snapshot.Diagnostics),
                snapshot.Diagnostics.Count == 0
                    ? "当前快照没有诊断文本"
                    : $"共 {snapshot.Diagnostics.Count} 项")
        };
        foreach (var node in snapshot.Nodes)
        {
            rows.Add(new HistoricalDetailFieldRow(
                $"节点记录 {node.NodeId}",
                $"开始={node.StartedAt:O}；结束={node.EndedAt?.ToString("O") ?? "未记录"}",
                $"Schema={node.SchemaVersion}；伤害={ObservationSummary(node.Damage)}；" +
                $"金币={ObservationSummary(node.Economy)}；" +
                $"行动={ObservationSummary(node.RemainingActionPoints)}；" +
                $"阵容={ObservationSummary(node.LineupIds)}；" +
                $"事件={ListValue(node.EventIds)}"));
        }

        if (snapshot.Nodes.Count == 0)
        {
            rows.Add(new HistoricalDetailFieldRow(
                "嵌套节点记录",
                "未记录",
                "当前快照未携带 NodeRecord；节点最终数据仍由归档节点保存"));
        }

        return rows;
    }

    private static IReadOnlyList<HistoricalDetailFieldRow> BuildRecognition(
        HistoricalNodeDetailEntry entry,
        Phase2OperationalState? state)
    {
        var rows = new List<HistoricalDetailFieldRow>
        {
            new(
                "备战分析文件",
                ValueOrUnknown(entry.PreparationAnalysisFile),
                string.IsNullOrWhiteSpace(entry.PreparationAnalysisFile)
                    ? "原因=实时节点或旧归档未保存备战分析文件路径"
                    : "归档来源文件；用于证据追溯"),
            new(
                "最终战斗文件",
                ValueOrUnknown(entry.FinalBattleFile),
                string.IsNullOrWhiteSpace(entry.FinalBattleFile)
                    ? "原因=实时节点、未封存节点或旧归档未保存最终战斗文件路径"
                    : "归档来源文件；用于证据追溯")
        };
        if (entry.LatestAnalysis is { } analysis)
        {
            rows.Add(new HistoricalDetailFieldRow(
                "分析记录",
                analysis.AnalysisId,
                $"Schema={analysis.SchemaVersion}；程序版本={analysis.ApplicationVersion ?? "未记录"}"));
            foreach (var route in analysis.RouteCandidates)
            {
                rows.Add(new HistoricalDetailFieldRow(
                    $"流派候选 {route.ArchetypeName}",
                    $"{route.GuideId} · 分数={route.Score:0.###}",
                    $"可用={route.Eligible}；置信度={FormatConfidence(route.Confidence)}；" +
                    $"警告={ListValue(route.Warnings)}；" +
                    $"缺失={ListValue(route.MissingInformation)}；" +
                    $"评分={ListValue(route.Components.Select(component =>
                        $"{component.Name}:{component.Score:0.###}×{component.Weight:0.###}({component.Explanation})"))}"));
            }
        }

        var snapshot = entry.LatestSnapshot;
        if (snapshot is not null)
        {
            Add(rows, "原始前后台角色集合", snapshot.BoardCharacterIds, ListValue, diagnostic: true);
            Add(rows, "原始备战席角色集合", snapshot.BenchCharacterIds, ListValue, diagnostic: true);
            Add(rows, "原始 Lineup 集合", snapshot.LineupIds, ListValue, diagnostic: true);
            Add(rows, "商店角色 ID", snapshot.ShopCharacterIds, ListValue, diagnostic: true);
        }

        var battle = entry.FinalBattle;
        if (battle is not null)
        {
            foreach (var degraded in battle.FinalDegradedObservations)
            {
                rows.Add(PendingIconRow(degraded, "最终战斗待解析图标"));
            }
            foreach (var partial in battle.FinalPartialFields)
            {
                rows.Add(PartialFieldRow(partial, "最终战斗残缺字段"));
            }
            foreach (var uncertainty in battle.FinalUncertainty)
            {
                rows.Add(new HistoricalDetailFieldRow("战斗不确定项", uncertainty));
            }
        }

        if (state is not null)
        {
            Add(rows, "运行态角色伤害", state.BattleDamage, values =>
                $"{values.Count} 条（详见最终伤害区）", diagnostic: true);
            Add(rows, "运行态羁绊伤害", state.BattleSynergyDamage, values =>
                $"{values.Count} 条（详见最终伤害区）", diagnostic: true);
            Add(rows, "运行态未知伤害", state.BattleUnresolvedDamage, values =>
                $"{values.Count} 条（详见最终伤害区）", diagnostic: true);
            Add(rows, "战斗画面总伤害候选", state.BattleScreenDamageCandidate,
                value => FormatDamage(value), diagnostic: true);
            Add(rows, "结算页前三伤害", state.SettlementDamage, values =>
                $"{values.Count} 条（详见最终伤害区）", diagnostic: true);
            Add(rows, "结算页总伤害候选", state.SettlementScreenDamageCandidate,
                value => FormatDamage(value), diagnostic: true);
            Add(rows, "结算金币奖励候选", state.SettlementGoldReward, diagnostic: true);
            Add(rows, "运行态剩余行动值", state.RemainingActionValue, value =>
                $"{value.RemainingRounds}轮 + {value.CurrentRoundActionValue} = {value.TotalActionValue}",
                diagnostic: true);
            Add(rows, "最终战斗观察", state.FinalBattle, value =>
                $"节点 {value.NodeId} · {(value.IsComplete ? "完整" : "残缺")}",
                diagnostic: true);
        }

        foreach (var trace in state?.RecognitionTrace ?? [])
        {
            rows.Add(new HistoricalDetailFieldRow(
                trace.Field,
                trace.NormalizedValue ?? "未得到标准化结果",
                $"页面={trace.SourcePageId ?? "未知"}；状态={trace.Status}；" +
                $"置信度={FormatConfidence(trace.Confidence)}；第{trace.Attempt}次；" +
                $"节点={trace.NodeId ?? "未知"}；原始OCR={ListValue(trace.RawOcr)}；" +
                $"降级原因={trace.DegradationReason ?? "无"}；" +
                $"区域={RegionSummary(trace.Region)}；裁剪={trace.CropFile ?? "未保存"}；" +
                $"捕获={trace.CapturedAt:O}"));
        }

        foreach (var pending in state?.PendingIcons ?? [])
        {
            rows.Add(PendingIconRow(pending, "待解析图标"));
        }

        foreach (var partial in state?.PartialFields ?? [])
        {
            rows.Add(PartialFieldRow(partial, "残缺字段"));
        }

        foreach (var diagnostic in (state?.Diagnostics ?? [])
                     .Concat(entry.LatestSnapshot?.Diagnostics ?? [])
                     .Distinct(StringComparer.Ordinal))
        {
            rows.Add(new HistoricalDetailFieldRow("诊断", diagnostic));
        }

        foreach (var warning in entry.LatestAnalysis?.Warnings ?? [])
        {
            rows.Add(new HistoricalDetailFieldRow("警告", warning));
        }

        foreach (var unknown in entry.LatestAnalysis?.UnknownFields ?? [])
        {
            rows.Add(new HistoricalDetailFieldRow("未知字段", unknown));
        }

        foreach (var recommendation in entry.LatestAnalysis?.Recommendations ?? [])
        {
            rows.Add(new HistoricalDetailFieldRow(
                $"建议 P{recommendation.Priority}",
                recommendation.Action,
                $"置信度={FormatConfidence(recommendation.Confidence)}；" +
                $"缺失信息={ListValue(recommendation.MissingInformation)}"));
        }

        return rows.Count == 0
            ? [new HistoricalDetailFieldRow("识别状态", "当前没有额外诊断或残缺字段")]
            : rows;
    }

    private static HistoricalDetailFieldRow PendingIconRow(
        PendingIconObservation pending,
        string prefix) =>
        new(
            $"{prefix} {pending.Category}/{pending.SlotKey}",
            pending.TemplateId ?? pending.TemporaryId ?? "未解析",
            $"状态={pending.Status}；置信度={FormatConfidence(pending.Confidence)}；" +
            $"候选={ListValue(pending.CandidateTemplateIds ?? [])}；" +
            $"临时ID={pending.TemporaryId ?? "无"}；" +
            $"已读字段={ListValue((pending.RecognizedFields ?? new Dictionary<string, string>())
                .Select(item => $"{item.Key}={item.Value}"))}；" +
            $"可驱动决策={pending.CanDriveDecisions}；" +
            $"区域={RegionSummary(pending.Region)}；裁剪={pending.CropFile ?? "未保存"}；" +
            EvidenceSummary(pending.Evidence));

    private static HistoricalDetailFieldRow PartialFieldRow(
        Phase2PartialFieldObservation partial,
        string prefix) =>
        new(
            $"{prefix} {partial.Field}/{partial.TemporaryId}",
            ListValue(partial.RecognizedFields.Select(item => $"{item.Key}={item.Value}")),
            $"原始={ListValue(partial.RawTexts)}；候选={ListValue(partial.CandidateIds)}；" +
            $"置信度={FormatConfidence(partial.Confidence)}；原因={partial.FailureReason}；" +
            $"可驱动决策={partial.CanDriveDecisions}；" +
            $"区域={RegionSummary(partial.Region)}；{EvidenceSummary(partial.Evidence)}");

    private static HistoricalDetailSectionViewModel Section(
        string title,
        IReadOnlyList<HistoricalDetailFieldRow> fields) =>
        new(title, fields.Count == 0
            ? [new HistoricalDetailFieldRow("状态", "未收集到数据")]
            : fields);

    /// <summary>
    /// 把内部 ID 解析为数据目录中的图标文件绝对路径（不存在时返回 null，
    /// UI 侧自动降级为纯文本，不显示破图）。
    /// </summary>
    private static string? ResolveIconPath(string? category, string? id)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // 仅允许安全文件名字符（数据目录 ID 均为字母/数字/下划线/连字符，
        // 中文名称属于 Unicode 字母），防止归档/OCR 数据中的路径分隔符
        // 构造目录穿越（security review LOW 建议修复）。
        if (id.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '_' or '-')))
        {
            return null;
        }

        var fileName = category switch
        {
            "character" => $"{id}__default.png",
            "environment" when int.TryParse(id, out var number) =>
                $"investment_environment_{number:D3}.png",
            _ => $"{id}.png"
        };
        var folder = category switch
        {
            // 角色使用方形大头像卡片（character-card-templates），
            // 更贴合游戏内角色阵容的表达；小头像在部分角色缺失。
            "character" => Path.Combine("4.4", "character-card-templates"),
            "strategy" => Path.Combine("4.4", "phase2-icon-assets", "standardized", "investment_strategy"),
            "affix" => Path.Combine("4.4", "phase2-icon-assets", "standardized", "enemy_affix"),
            "environment" => Path.Combine("4.4", "phase2-icon-assets", "standardized", "investment_environment"),
            "bond" => Path.Combine("4.4", "phase2-icon-assets", "standardized", "bond_state"),
            "equipment" => Path.Combine("raw", "4.4", "equipment", "890ae486642e979b", "assets", "currency_wars_equipment_icons"),
            _ => category
        };
        var full = Path.Combine(
            AppContext.BaseDirectory, "data", folder, fileName);
        return File.Exists(full) ? full : null;
    }

    private static void Add<T>(
        ICollection<HistoricalDetailFieldRow> rows,
        string label,
        Observation<T>? observation,
        Func<T, string>? formatter = null,
        bool diagnostic = false)
    {
        if (observation is null)
        {
            rows.Add(new HistoricalDetailFieldRow(
                label,
                "未记录",
                diagnostic ? "状态=Unknown" : "未识别"));
            return;
        }

        var canShowValue = observation.Status == ObservationStatus.Known ||
                           observation.Status == ObservationStatus.Stale ||
                           (!typeof(T).IsValueType && observation.Value is not null);
        var value = !canShowValue || observation.Value is null
            ? "未记录"
            : formatter?.Invoke(observation.Value) ?? observation.Value.ToString() ?? "未记录";
        if (observation.Status != ObservationStatus.Known && value != "未记录")
        {
            value = $"候选：{value}";
        }

        // 普通界面只显示可读状态；状态/置信度/证据等技术细节仅在诊断区展示。
        rows.Add(new HistoricalDetailFieldRow(
            label,
            value,
            diagnostic
                ? $"状态={observation.Status}；置信度={FormatConfidence(observation.Confidence)}；" +
                  $"观察时间={observation.ObservedAt?.ToString("O") ?? "未记录"}；" +
                  $"原因={ListValue(observation.Uncertainty)}；" +
                  $"证据={EvidenceListSummary(observation.Evidence)}"
                : observation.Status == ObservationStatus.Known
                    ? "已记录"
                    : "未识别"));
    }

    private static HistoricalDetailFieldRow UnknownRow<T>(
        string label,
        Observation<T>? observation,
        bool diagnostic = false) =>
        new(
            label,
            observation?.Status == ObservationStatus.Known &&
            observation.Value is not null
                ? observation.Value.ToString() ?? "未记录"
                : "未记录",
            diagnostic
                ? $"状态={observation?.Status.ToString() ?? "Unknown"}；" +
                  $"置信度={FormatConfidence(observation?.Confidence)}；" +
                  $"原因={ListValue(observation?.Uncertainty ?? [])}；" +
                  $"证据={EvidenceListSummary(observation?.Evidence ?? [])}"
                : "未识别");

    private string CharacterList(IEnumerable<string> ids) =>
        ListValue(ids.Select(CharacterName));

    private string CharacterName(string? id) => Resolve(id, characterNames);
    private string EnvironmentName(string id) => Resolve(id, environmentNames);
    private string StrategyName(string id) => Resolve(id, strategyNames);
    private string AffixName(string id) => Resolve(id, affixNames);
    private string CompetitorName(string id) => Resolve(id, competitorNames);

    private static string Resolve(
        string? id,
        IReadOnlyDictionary<string, string> names) =>
        string.IsNullOrWhiteSpace(id)
            ? "未知"
            : names.TryGetValue(id, out var name)
                ? $"{name}（{id}）"
                : id;

    private static string ZoneName(FormationZone zone) => zone switch
    {
        FormationZone.Front => "前台",
        FormationZone.Back => "后台",
        FormationZone.Bench => "备选席",
        _ => zone.ToString()
    };

    private static string ListValue(IEnumerable<string> values)
    {
        var items = values.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        return items.Length == 0 ? "未记录" : string.Join("、", items);
    }

    private static string FormatDamage(long? value) => value switch
    {
        null => "未知",
        >= 100_000_000 => $"{value.Value / 100_000_000d:0.##}亿（{value:N0}）",
        >= 10_000 => $"{value.Value / 10_000d:0.##}万（{value:N0}）",
        _ => value.Value.ToString("N0")
    };

    private static string FormatConfidence(double? value) =>
        value.HasValue ? $"{value.Value:P1}" : "未知";

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未记录" : value;

    private static string ObservationSummary<T>(Observation<T> observation)
    {
        if (observation.Status != ObservationStatus.Known &&
            observation.Status != ObservationStatus.Stale)
        {
            return $"未记录（{observation.Status}, {FormatConfidence(observation.Confidence)}）";
        }

        var value = observation.Value switch
        {
            null => "未记录",
            IEnumerable<string> values => ListValue(values),
            _ => observation.Value.ToString() ?? "未记录"
        };
        return $"{value}（{observation.Status}, {FormatConfidence(observation.Confidence)}）";
    }

    private static T? KnownValue<T>(Observation<T>? observation)
        where T : struct => observation?.Status == ObservationStatus.Known
        ? observation.Value
        : null;

    private static HistoricalDetailFieldRow OptionalIntegerRow(
        string label,
        int? value,
        string missingReason) => new(
        label,
        value?.ToString() ?? "未记录",
        value.HasValue ? "状态=Known" : $"状态=Unknown；原因={missingReason}");

    private static HistoricalDetailFieldRow SignedIntegerRow(
        string label,
        int? value,
        string missingReason) => new(
        label,
        Signed(value),
        value.HasValue ? "状态=Known" : $"状态=Unknown；原因={missingReason}");

    private static string EvidenceListSummary(
        IEnumerable<EvidenceReference> evidence)
    {
        var items = evidence.Select(EvidenceSummary).ToArray();
        return items.Length == 0 ? "未记录" : string.Join(" | ", items);
    }

    private static string EvidenceSummary(EvidenceReference evidence) =>
        $"来源={evidence.SourceId}；定位={evidence.Locator}；" +
        $"摘要={evidence.Summary ?? "无"}；" +
        $"捕获={evidence.CapturedAt?.ToString("O") ?? "未记录"}；" +
        $"置信度={FormatConfidence(evidence.Confidence)}";

    private static string RegionSummary(RelativeRegion? region) => region is null
        ? "未记录"
        : $"x={region.X:0.####}, y={region.Y:0.####}, " +
          $"w={region.Width:0.####}, h={region.Height:0.####}";

    private static string Signed(int? value) => value switch
    {
        > 0 => $"+{value}",
        0 => "±0",
        < 0 => value.Value.ToString(),
        _ => "未知"
    };
}
