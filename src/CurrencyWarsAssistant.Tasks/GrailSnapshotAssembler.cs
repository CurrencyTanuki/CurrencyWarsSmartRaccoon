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
        var slots = FormationSlots(state);
        var deployedBondMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var badgeCarriersNonMembers = 0;
        var carriedBadges = 0;
        var hasFiveCost = false;
        var xilianOnField = false;
        var benchSellablePool = 0;

        foreach (var slot in slots)
        {
            var character = ResolveCharacter(gameData, slot.CharacterId);
            if (character is null)
            {
                continue;
            }

            ownedNames.Add(character.Name);
            var isBondMember = IsBondMember(character);
            var isFiveCost = (character.Costs ?? Array.Empty<int>()).Contains(5);
            var deployed = slot.Zone is FormationZone.Front or FormationZone.Back;

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

            // 星徽携带者：仅上场角色的装备槽被识别（备战席不识别装备是既有识别边界）
            var carriesBadge = (slot.EquipmentSlots ?? Array.Empty<CharacterEquipmentSlotState>())
                .Any(equipment => string.Equals(equipment.EquipmentId, StarBadgeEquipmentId, StringComparison.OrdinalIgnoreCase));
            if (carriesBadge)
            {
                carriedBadges++;
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

        // ---- 物品栏：未携带星徽 ----
        var uncarriedBadges = (state.InventorySlots.Status is ObservationStatus.Known or ObservationStatus.Stale
                ? state.InventorySlots.Value ?? []
                : [])
            .Count(item => string.Equals(item.ItemId, StarBadgeEquipmentId, StringComparison.OrdinalIgnoreCase));

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
        // 保留线：留 N 个非命杯非5费备战席角色作星徽携带者候选（N=星徽总数），其余可卖
        var sellableBeyondKeepLine = Math.Max(0, benchSellablePool - Math.Min(totalBadges, benchSellablePool));

        return new GrailRunSnapshot
        {
            Goal = goal,
            TeamHealth = health,
            Population = population,
            Gold = gold,
            DeployedBondMembers = deployedBondMembers,
            BadgeCarrierNonMembers = badgeCarriersNonMembers,
            UncarriedStarBadges = uncarriedBadges,
            TotalStarBadgesObtained = totalBadges,
            OwnedCharacterNames = ownedNames,
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
            SellableBeyondKeepLineCount = sellableBeyondKeepLine,
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
