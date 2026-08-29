using System;
using System.Collections.Generic;
using System.Linq;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 「1-3 三星五费」祈愿试炼择优辅助（纯关键字逻辑，可独立单测）。
/// <para>对应决策树「试炼多个时优先选『立即获得 X』类；绝对禁选『出售全部圣杯/拆散羁绊』类」这一段。
/// 根据左右两个试炼的标题/效果文本，决定选左 / 选右 / 任选其一 / 都不可选。</para>
/// 注意：本类只做关键字判读，不识别屏幕；识别由既有的祈愿试炼弹框识别模块负责。
/// </summary>
public static class PrayTrialPreference
{
    /// <summary>命中即视为「立即获得类 / 高价值试炼」（优先选）。
    /// （L1 修正）收窄关键词：去掉过宽的「圣杯」「Archer」「投影仪」裸词，
    /// 只保留有判别力的胜利档 / 立即获得短语，避免中性试炼被高估。</summary>
    private static readonly string[] ValuableKeywords =
    {
        "立即获得", "直接获得", "登场Archer", "5费聘用书",
        "五费聘用书", "聘用书", "完美投影仪", "无限之釜", "奇迹代偿",
    };

    /// <summary>命中即视为「危险/禁选」类（出售圣杯、拆散羁绊）。</summary>
    private static readonly string[] ForbiddenKeywords =
    {
        "出售所有圣杯", "出售圣杯", "拆散", "移除羁绊", "清除羁绊",
    };

    public enum TrialChoice
    {
        Left,
        Right,
        Either,     // 任选其一（默认左）
        None,       // 都不可选（危险）
    }

    /// <summary>
    /// 依左右试炼文本给出择优建议。
    /// 优先级：两侧都含黑名单→None；一侧黑名单→选另一侧；
    /// 一侧含高价值且另一侧不含→选高价值侧；两侧都高价值或都不含→Either。
    /// </summary>
    public static TrialChoice Choose(
        string? leftTrial,
        string? rightTrial)
    {
        var left = leftTrial ?? string.Empty;
        var right = rightTrial ?? string.Empty;

        var leftForbidden = ContainsAny(left, ForbiddenKeywords);
        var rightForbidden = ContainsAny(right, ForbiddenKeywords);

        // 两侧都是危险选项：都不选
        if (leftForbidden && rightForbidden)
            return TrialChoice.None;

        // 一侧危险 -> 选另一侧
        if (leftForbidden)
            return TrialChoice.Right;
        if (rightForbidden)
            return TrialChoice.Left;

        var leftValuable = ContainsAny(left, ValuableKeywords);
        var rightValuable = ContainsAny(right, ValuableKeywords);

        // 一侧高价值 -> 选高价值侧
        if (leftValuable && !rightValuable)
            return TrialChoice.Left;
        if (rightValuable && !leftValuable)
            return TrialChoice.Right;

        // 两侧都高价值，或都无显著 -> 任选其一（默认左）
        return TrialChoice.Either;
    }

    /// <summary>
    /// 判断某条试炼文本是否属于「危险/禁选」类（出售圣杯、拆散羁绊）。
    /// 供决策引擎在「奇迹代偿不可点需改选另一侧」时校验另一侧是否可点（N2→C2 分支）。
    /// </summary>
    public static bool IsForbidden(string? trialText) =>
        ContainsAny(trialText ?? string.Empty, ForbiddenKeywords);

    private static bool ContainsAny(string text, IEnumerable<string> keywords) =>
        keywords.Any(keyword =>
            text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
}
