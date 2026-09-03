using System;
using System.Collections.Generic;
using System.Linq;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 把识别层产出组装成新决策树输入（<see cref="GrailRunSnapshot"/>）的接线器
/// （替代旧 <see cref="FateGrailSnapshotAssembler"/>，输出按定稿决策树 2026-08-29 的字段）。
/// <para>
/// 组装规则：
/// ① 血量/人口/金币——当帧 Known 则回填持有器缓存并采用；当帧 Unknown（弹框/过渡期）取持有器
///    缓存，超过 <paramref name="staleAfter"/> 视为未识别（组装为 null，决策器走防御口径）；
/// ② 阵容——接受 Known 或 Stale（tracker 在弹框期 carry 上一备战帧）；
/// ③ 羁绊成员只计上场（前台/后台），5 费/昔涟在场判定含备战席；
/// ④ 无限之釜已选 ⇒ 场上必有 5 费本体（釜=全三星圣杯角色，选择动作的游戏事实，不依赖识别）；
/// ⑤ 可卖数 = 备战席中非命杯成员、非 5 费的角色数，再扣除保留线（保留数=星徽总数）。
/// </para>
/// </summary>
public static class GrailSnapshotAssembler
{
    /// <summary>命运圣杯星徽的装备 ID（S1A/S1B 保留线与携带者判定的锚点）。</summary>
    public const string StarBadgeEquipmentId = "currency_wars_equipment_001";

    // 前台/后台槽位标准区域（1920×1080 参考系像素，与 PreparationFormation 前台/后台槽位表同源）。
    // 识别层 formation CardRegion 带旧布局 Y 残留（实测较真实卡位下移约 187px），
    // 卖出/拖拽类操作一律以标准槽位几何为准（2026-09-02 阿格莱雅两次卖出失败实证）。
    private static readonly RelativeRegion[] CanonicalFrontSlotRegions =
    [
        new(681d / 1920, 329d / 1080, 128d / 1920, 140d / 1080),
        new(827d / 1920, 329d / 1080, 122d / 1920, 140d / 1080),
        new(972d / 1920, 329d / 1080, 120d / 1920, 140d / 1080),
        new(1114d / 1920, 329d / 1080, 120d / 1920, 140d / 1080),
    ];

    private static readonly RelativeRegion[] CanonicalBackSlotRegions =
    [
        new(535d / 1920, 600d / 1080, 140d / 1920, 145d / 1080),
        new(687d / 1920, 600d / 1080, 130d / 1920, 145d / 1080),
        new(829d / 1920, 600d / 1080, 130d / 1920, 145d / 1080),
        new(966d / 1920, 600d / 1080, 130d / 1920, 145d / 1080),
        new(1108d / 1920, 600d / 1080, 130d / 1920, 145d / 1080),
        new(1258d / 1920, 600d / 1080, 130d / 1920, 145d / 1080),
    ];

    /// <summary>用标准槽位几何替代识别层 CardRegion（0..1 相对客户区）；越界或未知区划返回 null 回退原值。</summary>
    public static RelativeRegion? ResolveCanonicalSlotRegion(FormationZone zone, int slotIndex)
    {
        var table = zone == FormationZone.Front ? CanonicalFrontSlotRegions
            : zone == FormationZone.Back ? CanonicalBackSlotRegions
            : null;
        return table is not null && (uint)slotIndex < (uint)table.Length
            ? table[slotIndex]
            : null;
    }

    /// <summary>装备 ID 转短名：星徽用用户语义名，其余取 ID 尾码（data/4.4 无装备名映射，诚实报 ID 不编名）。</summary>
    private static string ShortEquipmentName(string equipmentId)
    {
        if (string.Equals(equipmentId, StarBadgeEquipmentId, StringComparison.OrdinalIgnoreCase))
        {
            return "星徽";
        }

        const string prefix = "currency_wars_equipment_";
        return equipmentId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? "装备" + equipmentId[prefix.Length..]
            : equipmentId;
    }

    /// <summary>昔涟的角色 ID（全员模式最终目标本体）。</summary>
    public const string XilianCharacterId = "currency_wars_character_19";

    public static GrailRunSnapshot Assemble(
        Phase2OperationalState state,
        RunSnapshot? economySnapshot,
        GameDataCatalog gameData,
        GrailRunStateHolder holder,
        GrailUserGoal goal,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(holder);

        // ---- 识别值捕获 + 陈旧度取值 ----
        if (state.Health.Status == ObservationStatus.Known)
        {
            holder.CaptureHealth(state.Health.Value, now);
        }

        if (state.Population.Status == ObservationStatus.Known)
        {
            holder.CapturePopulation(state.Population.Value, now);
        }

        var economy = economySnapshot?.Economy;
        if (economy is { Status: ObservationStatus.Known })
        {
            holder.CaptureGold(economy.Value, now);
        }

        var health = FreshOrUnknown(holder.PeekHealth(), now, staleAfter);
        var population = FreshOrUnknown(holder.PeekPopulation(), now, staleAfter) ?? GrailRunSnapshot.BasePopulation;
        var gold = FreshOrUnknown(holder.PeekGold(), now, staleAfter) ?? 0;

        // ---- 阵容解析（Known 或 Stale 均接受）----
        var anomalyNotes = new System.Text.StringBuilder();
        var nameSlots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var slots = FormationSlots(state);
        var deployedBondMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1.2.31：已购并集（holder.RecordPurchased 的持久集合）——识别漏名不再让已购名单抖动
        var ownedNames = new HashSet<string>(holder.PurchasedNames(), StringComparer.OrdinalIgnoreCase);
        var badgeCarriersNonMembers = 0;
        var carriedBadges = 0;
        var hasFiveCost = false;
        var xilianOnField = false;
        var benchSellablePool = 0;
        var deployedSellablePool = 0;
        var deployedCount = 0;
        var occupiedFrontSlots = new HashSet<int>();
        var occupiedBackSlots = new HashSet<int>();

        var deployedNonGrail = new List<GrailDeployedCharacter>();
        var deployedCharacterDetails = new List<string>();
        var benchCharacterDetails = new List<string>();
        foreach (var slot in slots)
        {
            // 槽位占用记录（N14 部署用实际空槽）：CharacterId 非空即算已占（含 Uncertain 占位槽，
            // 宁可保守不选该槽，避免把识别缺位误判为空槽造成互换）。须在 ResolveCharacter 之前——
            // unknown-formation-unit 等占位角色 ResolveCharacter 返回 null 会 continue，但槽位仍被占。
            if (slot.Zone == FormationZone.Front)
            {
                occupiedFrontSlots.Add(slot.SlotIndex);
            }
            else if (slot.Zone == FormationZone.Back)
            {
                occupiedBackSlots.Add(slot.SlotIndex);
            }

            var character = ResolveCharacter(gameData, slot.CharacterId);
            if (character is null)
            {
                continue;
            }

            ownedNames.Add(character.Name);
            var isBondMember = IsBondMember(character);
            // 5 费判定：银狼LV.999 是唯一多费用角色（costs=[3,4,5]），按纯 [5] 严格判
            // 银狼LV.999（costs=[3,4,5] 变费）按用户拍板一律视作 3 费：5 费判定=纯 [5]
            var isFiveCost = (character.Costs ?? Array.Empty<int>()).Count == 1
                && (character.Costs ?? Array.Empty<int>())[0] == 5;
            var deployed = slot.Zone is FormationZone.Front or FormationZone.Back;

            // 星徽携带者：仅上场角色的装备槽被识别（备战席不识别装备是既有识别边界）
            var carriesBadge = (slot.EquipmentSlots ?? Array.Empty<CharacterEquipmentSlotState>())
                .Any(equipment => string.Equals(equipment.EquipmentId, StarBadgeEquipmentId, StringComparison.OrdinalIgnoreCase));
            if (carriesBadge)
            {
                carriedBadges++;
            }

            // 全部已携带装备（2026-09-02 用户令：阵容明细必须含"谁带什么装备"）
            var carriedEquipment = (slot.EquipmentSlots ?? Array.Empty<CharacterEquipmentSlotState>())
                .Where(equipment => !string.IsNullOrWhiteSpace(equipment.EquipmentId))
                .Select(equipment => ShortEquipmentName(equipment.EquipmentId!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (deployed)
            {
                deployedCount++;
                if (!nameSlots.TryGetValue(character.Name, out var deployedPositions))
                {
                    deployedPositions = new List<string>();
                    nameSlots[character.Name] = deployedPositions;
                }

                deployedPositions.Add((slot.Zone == FormationZone.Front ? "F" : "B") + (slot.SlotIndex + 1));
                deployedCharacterDetails.Add(
                    $"{(slot.Zone == FormationZone.Front ? "F" : "B")}{slot.SlotIndex + 1}:{character.Name}"
                    + (carriedEquipment.Count > 0 ? $"[{string.Join("+", carriedEquipment)}]" : ""));
                // 可卖场上候选：非命杯、非 5 费、非星徽携带者（星徽携带者=羁绊计数，绝不卖——用户拍板）
                if (!isBondMember && !isFiveCost && !carriesBadge)
                {
                    deployedSellablePool++;
                    var cardRegion = ResolveCanonicalSlotRegion(slot.Zone, slot.SlotIndex)
                                     ?? slot.CardRegion;
                    if (cardRegion is not null)
                    {
                        deployedNonGrail.Add(new GrailDeployedCharacter(
                            character.Name, isBondMember, isFiveCost, cardRegion,
                            SaleValue: (character.Costs ?? Array.Empty<int>()).DefaultIfEmpty(0).Min()));
                    }
                }
            }
            else if (slot.Zone == FormationZone.Bench)
            {
                if (!nameSlots.TryGetValue(character.Name, out var benchPositions))
                {
                    benchPositions = new List<string>();
                    nameSlots[character.Name] = benchPositions;
                }

                benchPositions.Add($"B{slot.SlotIndex + 1}");
                benchCharacterDetails.Add($"{slot.SlotIndex}:{character.Name}");
            }

            if (isFiveCost)
            {
                hasFiveCost = true;
            }

            if (string.Equals(character.Id, XilianCharacterId, StringComparison.OrdinalIgnoreCase))
            {
                xilianOnField = true;
            }

            if (isBondMember)
            {
                ownedNames.Add(character.Name);
            }

            if (deployed)
            {
                if (isBondMember)
                {
                    deployedBondMembers.Add(character.Name);
                }
                else if (carriesBadge)
                {
                    badgeCarriersNonMembers++;
                }
            }

            if (slot.Zone == FormationZone.Bench && !isBondMember && !isFiveCost)
            {
                benchSellablePool++;
            }
        }

        // ---- 物品栏：未携带星徽（含区域，N2 星徽装配的拖拽源点）----
        var uncarriedBadgeSlots = (state.InventorySlots.Status is ObservationStatus.Known or ObservationStatus.Stale
                ? state.InventorySlots.Value ?? []
                : [])
            .Where(item => string.Equals(item.ItemId, StarBadgeEquipmentId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var uncarriedBadges = uncarriedBadgeSlots.Length;

        // ---- 投资策略：识别层单调保留的当前已选策略（N12 采购专员刷牌机制锚点）----
        var activeStrategies = state.InvestmentStrategyIds.Status is ObservationStatus.Known
            ? state.InvestmentStrategyIds.Value ?? []
            : [];

        // ---- 商店 sighting：未拥有的命杯成员出现在商店 ⇒ 第 5 成员出现信号之一 ----
        var shopIds = economySnapshot?.ShopCharacterIds;
        if (shopIds is { Status: ObservationStatus.Known })
        {
            foreach (var id in shopIds.Value ?? [])
            {
                var character = ResolveCharacter(gameData, id);
                if (character is not null
                    && IsBondMember(character)
                    && !ownedNames.Contains(character.Name))
                {
                    holder.MarkNewBondMemberAvailable();
                    break;
                }
            }
        }

        if (uncarriedBadges > 0)
        {
            holder.MarkNewBondMemberAvailable();
        }

        var eventState = holder.PeekEventState();
        var totalBadges = uncarriedBadges + carriedBadges;
        // 可卖池 = 场上(非命杯、非5费、非星徽携带者) + 备战席(非命杯、非5费)。
        // 保留线 = 物品栏未装配星徽数：用户机制“星徽获得即装配到角色并上场”，
        // 已装配的携带者已在场上被排除出可卖池（天然受保护），只有未装配的星徽
        // 才需要留对应数量的非命杯角色作装配候选（决策树 S1A/S1B 保留线精神的等价形式）。
        var sellablePool = deployedSellablePool + benchSellablePool;
        var sellableBeyondKeepLine = Math.Max(0, sellablePool - Math.Min(uncarriedBadges, sellablePool));

        // 1.2.31：同名多处=识别身份事故标记（坑 34/审计症状 D），供决策层拒采
        foreach (var kv in nameSlots)
        {
            if (kv.Value.Count > 1)
            {
                anomalyNotes.Append($"⚠同名多处:{kv.Key}@{string.Join("/", kv.Value)} ");
            }
        }

        return new GrailRunSnapshot
        {
            Goal = goal,
            TeamHealth = health,
            Population = population,
            Gold = gold,
            DeployedBondMembers = deployedBondMembers,
            DeployedNonGrailCharacters = deployedNonGrail,
            DeployedCount = deployedCount,
            DeployedCharacterDetails = deployedCharacterDetails,
            BenchCharacterDetails = benchCharacterDetails,
            OccupiedFrontSlots = occupiedFrontSlots,
            OccupiedBackSlots = occupiedBackSlots,
            BadgeCarrierNonMembers = badgeCarriersNonMembers,
            UncarriedStarBadges = uncarriedBadges,
            InventoryStarBadgeRegions = uncarriedBadgeSlots
                .Select(item => item.Region)
                .ToArray(),
            TotalStarBadgesObtained = totalBadges,
            OwnedCharacterNames = ownedNames,
            ActiveInvestmentStrategyIds = activeStrategies.ToHashSet(StringComparer.OrdinalIgnoreCase),
            // 无限之釜选中即保证 5 费本体在场（全三星圣杯角色），不依赖识别是否捕捉到
            HasFiveCostBody = hasFiveCost || eventState.CauldronSelected,
            XilianOnField = xilianOnField,
            LettersObtained = eventState.LettersObtained,
            LettersOpened = eventState.LettersOpened,
            MiracleCompensationSelected = eventState.MiracleSelected,
            MiracleCompensationSelectedAtHealth = eventState.MiracleAtHealth,
            InfiniteCauldronSelected = eventState.CauldronSelected,
            WishesResponded = eventState.WishesResponded,
            FiveBondGivenUp = eventState.FiveBondGivenUp,
            NewBondMemberAvailable = eventState.NewBondMemberAvailable,
            RefreshGoldCost = GrailRunSnapshot.MinRefreshGold + (holder.PeekRefreshSurcharge() ? 1 : 0),
            XpPurchaseTotalCost = GrailRunSnapshot.XpPurchaseGoldCost + (holder.PeekXpSurcharge() ? 2 : 0),
            SellableBeyondKeepLineCount = sellableBeyondKeepLine,
            AnomalyNotes = anomalyNotes.ToString(),
            CapturedAt = now,
        };
    }

    /// <summary>缓存值在陈旧度窗口内则返回，否则视为未识别（null）。</summary>
    private static int? FreshOrUnknown((int? Value, DateTimeOffset? CapturedAt) cached, DateTimeOffset now, TimeSpan staleAfter)
    {
        if (cached.Value is null || cached.CapturedAt is null)
        {
            return null;
        }

        return now - cached.CapturedAt.Value <= staleAfter ? cached.Value : null;
    }

    private static bool IsBondMember(CurrencyWarsCharacterData character) =>
        character.BondNames.Any(name => name is not null && name.Contains("命运圣杯", StringComparison.Ordinal));

    private static IEnumerable<FormationCharacterState> FormationSlots(Phase2OperationalState state)
    {
        var formation = state.Formation;
        // Known 与 Stale（弹框期 tracker carry 的上一备战帧）均接受；Unknown/缺失返回空
        if (formation?.Status is not (ObservationStatus.Known or ObservationStatus.Stale)
            || formation.Value is null)
        {
            return [];
        }

        return formation.Value
            .Where(slot => !string.IsNullOrWhiteSpace(slot.CharacterId));
    }

    private static CurrencyWarsCharacterData? ResolveCharacter(GameDataCatalog gameData, string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return null;
        }

        return gameData.CurrencyWarsCharacters.FirstOrDefault(
            item => string.Equals(item.Id, characterId, StringComparison.OrdinalIgnoreCase));
    }
}
