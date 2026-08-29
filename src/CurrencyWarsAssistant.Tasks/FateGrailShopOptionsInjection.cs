using System;
using System.Collections.Generic;
using System.Linq;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 把「1-3 三星五费」决策层算出的投资策略选择<b>真正写入</b>商店 <see cref="RewardStageAutomationOptions"/>。
/// <para>
/// 对应审查报告 <b>H2</b>：此前 <see cref="FateGrailFlowDecider"/> 算出的
/// <see cref="FateGrailShopDecision.ChosenStrategyIds"/>（二极管 276 / 采购专员 051/238）只进编排器 Result 上报，
/// 从未写入 <see cref="RewardStageAutomationOptions.PreferredInvestmentStrategyIds"/>，导致连算出来的策略都没生效。
/// 本类把决策注入真实 Options：目标环境 id 与首选策略 id 集合都写进配置，供商店/投资策略执行器消费。
/// </para>
/// 纯派生：不改动传入对象，返回新 Options。
/// </summary>
public static class FateGrailShopOptionsInjection
{
    /// <summary>
    /// 返回新 <see cref="RewardStageAutomationOptions"/>：其 <see cref="RewardStageAutomationOptions.SelectedInvestmentEnvironmentId"/>
    /// （本次决策指定时）<b>更新</b>，<see cref="RewardStageAutomationOptions.PreferredInvestmentStrategyIds"/> 与本类传入的策略 <b>合并</b>（保留 base 已有，追加本次决策，非替换）。
    /// 其余字段原样透传（不覆盖）。
    /// </summary>
    /// <param name="source">基础配置（通常为真实商店 Options 已套用命杯名单后的结果）。</param>
    /// <param name="strategyIds">本次决策要纳入的投资策略 id 全集（可含 276 与 051/238）。</param>
    /// <param name="selectedEnvironmentId">本局选定的投资环境 id（无则 null，不改写环境）。</param>
    public static RewardStageAutomationOptions ApplyStrategySelection(
        RewardStageAutomationOptions source,
        IEnumerable<string>? strategyIds,
        string? selectedEnvironmentId = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var chosen = (strategyIds ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // （评审修正）与 base 已有策略<b>合并</b>而非替换：清空时保留调用方既有配置，避免误清。
        var preferred = new HashSet<string>(
            source.PreferredInvestmentStrategyIds ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        preferred.UnionWith(chosen);

        // 若调用方未指定环境，沿用 source 现有的；否则更新。
        var environmentId = selectedEnvironmentId ?? source.SelectedInvestmentEnvironmentId;

        return new RewardStageAutomationOptions
        {
            EnableEarlyStrongFormationPurchase =
                source.EnableEarlyStrongFormationPurchase,
            EnableGalaxyScholarRewardStrategy =
                source.EnableGalaxyScholarRewardStrategy,
            InitialOwnedCharacters = source.InitialOwnedCharacters,
            AutoPurchaseCharacterNames = source.AutoPurchaseCharacterNames,
            RetainedCharacterNames = source.RetainedCharacterNames,
            FormationCharacterNames = source.FormationCharacterNames,
            InitialFormationPlacements = source.InitialFormationPlacements,
            PreparationCompletionOptions = source.PreparationCompletionOptions,
            PreferredInvestmentStrategyIds = preferred,
            SelectedInvestmentEnvironmentId = environmentId,
        };
    }
}
