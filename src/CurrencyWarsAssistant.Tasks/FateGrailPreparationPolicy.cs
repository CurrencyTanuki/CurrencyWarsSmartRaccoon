using System.Collections.Generic;
using System.Linq;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」备战补员阶段的命杯禁卖策略（纯函数，可独立单测）。
/// <para>对应决策树「1-2 备战 = 复用软件已有备战流程，仅多一条：只买圣杯羁绊角色、禁卖圣杯羁绊角色」。
/// 把命运圣杯羁绊成员并入备战补员选项的保留/必留名单，使任何备战自动卖出都不会卖掉圣杯羁绊成员。</para>
/// 语义说明（与 <see cref="PreparationBoardOptions"/> 对齐）：<see cref="PreparationBoardOptions.RetainedCharacterNames"/>
/// = 保留不自动卖出；<see cref="PreparationBoardOptions.RequiredRetainedCharacterNames"/> = 必须保留（缺失会被判不可完成）。
/// 注意：<b>RequiredRetainedCharacterNames 是强约束</b>，若某命杯成员根本未持有/买不到，会被判缺必留。
/// 因此本策略<b>只把「确实在场或必买」的命杯成员</b>放入 RequiredRetained，其余只管保留（Retained），避免误判。</para>
/// </summary>
public static class FateGrailPreparationPolicy
{
    /// <summary>
    /// 返回新 <see cref="PreparationBoardOptions"/>：命杯成员并入 Retained（禁卖），
    /// 已在场（held）的命杯成员并入 RequiredRetained（必留）。
    /// 不改动原对象；纯派生。
    /// </summary>
    public static PreparationBoardOptions ApplyRetained(
        PreparationBoardOptions source,
        IEnumerable<string>? heldBenchNames)
    {
        ArgumentNullException.ThrowIfNull(source);

        var held = new HashSet<string>(
            (heldBenchNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrEmpty(name)),
            System.StringComparer.OrdinalIgnoreCase);

        var retainedAll = new HashSet<string>(
            source.RetainedCharacterNames ?? Enumerable.Empty<string>(),
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var n in FateGrailShoppingPolicy.FateGrailMemberNames)
            retainedAll.Add(n); // 所有命杯成员一律禁卖

        var required = new HashSet<string>(
            source.RequiredRetainedCharacterNames ?? Enumerable.Empty<string>(),
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var n in FateGrailShoppingPolicy.FateGrailMemberNames)
        {
            // 只有确实在手上的命杯成员才标必留，避免强约束误判
            if (held.Contains(n))
                required.Add(n);
        }

        return new PreparationBoardOptions
        {
            EligibleCharacterNames = source.EligibleCharacterNames,
            EnableGalaxyScholarPairFormation =
                source.EnableGalaxyScholarPairFormation,
            RetainedCharacterNames = retainedAll,
            RequiredRetainedCharacterNames = required,
            EnableEarlyStrongFormationRetention =
                source.EnableEarlyStrongFormationRetention,
            DeferBenchSaleUntilShopCompletion =
                source.DeferBenchSaleUntilShopCompletion,
            ProtectFirstBenchSlotFromSale =
                source.ProtectFirstBenchSlotFromSale,
            BenchSaleMode = source.BenchSaleMode,
            InterestThreshold = source.InterestThreshold,
            FastReroll = source.FastReroll,
            EnablePresetPriorityDeployment =
                source.EnablePresetPriorityDeployment,
        };
    }
}
