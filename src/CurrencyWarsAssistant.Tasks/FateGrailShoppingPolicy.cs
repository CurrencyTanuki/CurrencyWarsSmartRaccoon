using System.Collections.Generic;
using System.Linq;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」中商店阶段所需的命运圣杯羁绊采购策略。
/// <para>
/// 对应决策树「商店=只允许买命运圣杯羁绊成员、且严禁卖出圣杯羁绊角色」这一条特殊配置。
/// 机制要点（2026-08-24 核对 data/4.4/currency-wars-characters.json bond=命运圣杯 成员）：
/// <list type="bullet">
///   <item>圣杯羁绊成员 = 远坂凛(1费)、吉尔伽美什(2费)、Saber(3费)、Archer(5费)。</item>
///   <item><b>Archer 不进商店购买名单</b>：商店 5 费概率在 1-3(等级&lt;7) 为 0%，买不到；
///   且 Archer 必须可上场(走祈愿「登场Archer」/5费聘书)，不能靠商店拿——见 G_NEED / B_ONDA。</item>
///   <item>「禁卖」= 把命杯成员并入 RetainedCharacterNames(保留/绝不自动卖出名单)。</item>
/// </list>
/// 本类纯名单逻辑、不读取任何待实证数据，可独立单测；不修改任何既有流水线行为。
/// </para>
/// </summary>
public static class FateGrailShoppingPolicy
{
    /// <summary>
    /// 命运圣杯羁绊成员的全集（含 5 费 Archer，仅作参考/禁卖集合用）。
    /// 注意：<see cref="StorePurchaseNames"/> 由本名单的前 3 项派生，
    /// 增删成员只需改本处一处。
    /// </summary>
    public static IReadOnlyList<string> FateGrailMemberNames { get; } =
        new[]
        {
            "远坂凛",     // 1 费
            "吉尔伽美什", // 2 费
            "Saber",      // 3 费
            "Archer",     // 5 费（商店买不到，需祈愿/聘书）
        };

    /// <summary>
    /// 商店可（应）购买的命杯成员 = 1/2/3 费成员（<b>不含 Archer</b>），
    /// 由 <see cref="FateGrailMemberNames"/> 派生，与全员名单保持同步。
    /// </summary>
    public static IReadOnlyList<string> StorePurchaseNames { get; } =
        FateGrailMemberNames.Take(3).ToArray();

    /// <summary>
    /// 自动购买名单 = 商店可买命杯成员（1/2/3 费）。（<b>L2 修正</b>）<br/>
    /// 在 <paramref name="alreadyHeld"/> 里<b>已确认拥有的成员会从名单剔除</b>，避免重复购买浪费金币
    /// （对应审查报告 L2：AutoPurchase 不再恒固定 3 人）。传 null / 空 = 全部纳入。
    /// </summary>
    public static IReadOnlySet<string> BuildAutoPurchaseNames(
        IEnumerable<string>? alreadyHeld = null)
    {
        var held = new HashSet<string>(
            (alreadyHeld ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrEmpty(name)),
            StringComparer.OrdinalIgnoreCase);
        if (held.Count == 0)
            return StorePurchaseNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return StorePurchaseNames
            .Where(name => !held.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// （保留）自动购买名单 = 商店可买命杯成员（1/2/3 费），不剔除已持有。
    /// 仅用于不关注已持有场景的旧调用；新调用请用带参版 <see cref="BuildAutoPurchaseNames(IEnumerable{string})"/>。
    /// </summary>
    public static IReadOnlySet<string> BuildAllStorePurchaseNames() =>
        StorePurchaseNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 保留/禁卖名单 = 全部 4 名命杯成员（含 Archer）。
    /// <para><b>N5 修复</b>：Archer（5 费本体）无条件禁卖——它靠祈愿/聘书到手后是本局复制目标，
    /// 若生产路径未传持有名单（HeldOrOwnedNames=null）时被排除在禁卖名单外，
    /// 商店/备战自动卖出可能把它卖掉，导致“扣完金币无复制目标”。</para>
    /// <para>禁卖名单语义是“若在场则绝不自动卖出”，未持有的名字列入无副作用；
    /// <paramref name="alreadyHeldOrOwned"/> 仅为兼容保留，不影响结果。</para>
    /// </summary>
    public static IReadOnlySet<string> BuildRetainedNames(
        IEnumerable<string> alreadyHeldOrOwned)
    {
        // 4 名命杯成员（凛/闪/Saber/Archer）一律禁卖，不依赖“是否已持有”的识别输入。
        return FateGrailMemberNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把命运圣杯采购策略应用到给定 <see cref="RewardStageAutomationOptions"/>，
    /// 返回一份<b>新</b>选项（不改动原对象；纯内存派生）。
    /// <b>覆盖而非合并</b>：<see cref="RewardStageAutomationOptions.AutoPurchaseCharacterNames"/>
    /// 会被替换为命杯成员(1/2/3费)。这是决策树明确要求——“商店只允许买命运圣杯羁绊成员”，
    /// 因此 source 里原有的非命杯自动购买项不再自动买（它们仍可在 Retained 里被保留/禁卖）。
    /// 警告：调用方若依赖旧自动购买名单，应自行合并后传入。
    /// </summary>
    public static RewardStageAutomationOptions ApplyTo(
        RewardStageAutomationOptions source,
        IEnumerable<string>? alreadyHeldOrOwned = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        // 注意：alreadyHeldOrOwned 传 null 用 InitialOwnedCharacters；传空数组=视为无持有。
        // （M1 修正）防御 source.InitialOwnedCharacters 为 null / item.Character 为 null，避免 NRE。
        var held = (alreadyHeldOrOwned ??
                source.InitialOwnedCharacters
                    .Select(item => item.Character?.Name))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)   // 已过滤，安全断言非空
            .ToArray();

        var retained = new HashSet<string>(
            source.RetainedCharacterNames,
            StringComparer.OrdinalIgnoreCase);
        foreach (var n in BuildRetainedNames(held))
            retained.Add(n);

        // （L2 修正）自动购买名单剔除已持有成员，避免重复购买。
        var autoPurchase = BuildAutoPurchaseNames(held);

        return new RewardStageAutomationOptions
        {
            EnableEarlyStrongFormationPurchase =
                source.EnableEarlyStrongFormationPurchase,
            EnableGalaxyScholarRewardStrategy =
                source.EnableGalaxyScholarRewardStrategy,
            InitialOwnedCharacters = source.InitialOwnedCharacters,
            AutoPurchaseCharacterNames = autoPurchase,
            RetainedCharacterNames = retained,
            FormationCharacterNames = source.FormationCharacterNames,
            InitialFormationPlacements = source.InitialFormationPlacements,
            PreparationCompletionOptions = source.PreparationCompletionOptions,
            PreferredInvestmentStrategyIds =
                source.PreferredInvestmentStrategyIds,
            SelectedInvestmentEnvironmentId =
                source.SelectedInvestmentEnvironmentId,
        };
    }
}
