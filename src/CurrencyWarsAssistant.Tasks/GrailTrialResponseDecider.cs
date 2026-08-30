namespace CurrencyWarsAssistant.Tasks;

/// <summary>祈愿弹框响应动作类型（对应定稿树 F5/F9/F11/F12/F14/F15a/F16/F17）。</summary>
public enum GrailTrialResponseKind
{
    /// <summary>F9：本局首次祈愿（2 档激活），不识别、直接选左。</summary>
    BlindPickLeft,

    /// <summary>F14：选择奇迹代偿（血≥89 且场上已有 5 费本体）。</summary>
    SelectMiracleCompensation,

    /// <summary>F15a/F12：选择另一侧（奇迹代偿不可选 / 令咒侧奖励非聘用书）。</summary>
    SelectOtherSide,

    /// <summary>F11：选择聘用书奖励侧；选完立刻打开两个五费聘用书。</summary>
    SelectLetterTrial,

    /// <summary>F16：单人模式选无限之釜 → 选完收工。</summary>
    SelectCauldronFinish,

    /// <summary>F17：全员模式选无限之釜 → 选完视为获得一个 5 费角色，继续。</summary>
    SelectCauldronContinue,

    /// <summary>F5：四个关键试炼均未出现，默认选左推进。</summary>
    PickLeftFallback,

    /// <summary>
    /// 防御分支（F2 识别不完整）：试炼名或决策所需的奖励名未读出时不得盲点——
    /// 左侧未识别时右侧可能是血不足的奇迹代偿（点选被游戏拒绝卡死），
    /// 令咒侧奖励未读出时可能漏掉五费聘用书。仅重试 OCR，不点击。
    /// </summary>
    RecognitionUncertain,
}

/// <summary>祈愿弹框响应决策结果。Side=要点选的一侧；OpenLettersAfter=选完立刻开聘用书（F11a）。</summary>
public sealed record GrailTrialResponse(
    GrailTrialResponseKind Kind,
    GrailTrialSide? Side,
    string SelectedTrialName,
    bool OpenLettersAfter,
    string Reason,
    string TreeNode);

/// <summary>弹框左右两侧的识别事实（F2：试炼名 + 奖励名，OCR 容错由匹配层负责）。</summary>
public sealed record GrailTrialPairContext(
    string? LeftTrialName,
    string? LeftRewardName,
    string? RightTrialName,
    string? RightRewardName)
{
    public string? NameOf(GrailTrialSide side) =>
        side == GrailTrialSide.Left ? LeftTrialName : RightTrialName;

    public string? RewardOf(GrailTrialSide side) =>
        side == GrailTrialSide.Left ? LeftRewardName : RightRewardName;

    public GrailTrialSide Other(GrailTrialSide side) =>
        side == GrailTrialSide.Left ? GrailTrialSide.Right : GrailTrialSide.Left;
}

/// <summary>
/// 定稿决策树「命运圣杯祈愿试炼识别流程」（F1~F17）纯逻辑实现。
/// 路由优先级（F17 括号语义推广）：奇迹代偿 &gt; 令咒聘用书侧（回路过载/行为限制）&gt; 无限之釜 &gt; 默认选左。
/// 弹框强制二选一：任何分支都必须给出一个可点选的侧，不允许“不选”（用户 2026-08-29 拍板）。
/// </summary>
public static class GrailTrialResponseDecider
{
    public const string MiracleKeyword = "奇迹代偿";
    public const string OverloadKeyword = "回路过载";
    public const string RestrictKeyword = "行为限制";
    public const string CauldronKeyword = "无限之釜";
    public const string LetterRewardKeyword = "五费聘用书";

    /// <summary>"四费聘用书"与"五费聘用书"编辑距离为 1，模糊匹配必须显式排除（数据中确有四费聘用书奖励）。</summary>
    public const string FourCostLetterKeyword = "四费聘用书";

    /// <summary>对一个弹框做响应决策（F2 路由）。纯函数，不改状态；状态落地见 <see cref="ApplyTo"/>。</summary>
    public static GrailTrialResponse Decide(GrailRunSnapshot s, GrailTrialPairContext ctx)
    {
        // F9：本局第一次进入祈愿识别（2 档激活）——直接选左边，不进行识别
        if (s.IsFirstWishPending)
            return new(
                GrailTrialResponseKind.BlindPickLeft,
                GrailTrialSide.Left,
                ctx.NameOf(GrailTrialSide.Left) ?? string.Empty,
                OpenLettersAfter: false,
                "本局首次祈愿（2 档激活），按既定策略盲选左、不识别。",
                "F9");

        // F2 防御：任一侧试炼名未读出时无法安全路由（未读侧可能藏着血不足的奇迹代偿）——
        // 不盲点，仅重试识别（与既有 WishTrialSelectionAutomation 对双名全空拒绝点击的行为对齐）
        if (string.IsNullOrWhiteSpace(ctx.LeftTrialName) || string.IsNullOrWhiteSpace(ctx.RightTrialName))
            return new(
                GrailTrialResponseKind.RecognitionUncertain,
                Side: null,
                SelectedTrialName: string.Empty,
                OpenLettersAfter: false,
                "弹框试炼名未完整识别，不盲点，等待识别重试。",
                "F2防御");

        // F6 → F13：奇迹代偿（三个关键试炼中优先级最高）
        if (FindSide(ctx, MiracleKeyword) is { } miracle)
        {
            // F13：血量识别 >88（即 ≥89）。识别失败按不满足处理——点了会被游戏拒绝并卡死流程。
            // 用户 2026-08-30 拍板：血≥89 即必选奇迹代偿——5 费本体不作当场前置
            //（弹框强制二选一，拒选=永久失去唯一机会；本体选完之后走 L 系列继续凑，
            //  聘用书/067/采购专员刷出均可，实在拿不到才山穷水尽重开）。
            if ((s.TeamHealth ?? 0) >= GrailFinalJudge.MiracleHealthThreshold)
            {
                return new(
                    GrailTrialResponseKind.SelectMiracleCompensation,
                    miracle,
                    ctx.NameOf(miracle) ?? MiracleKeyword,
                    OpenLettersAfter: false,
                    s.HasFiveCostBody
                        ? "血量≥89，选择奇迹代偿（场上已有 5 费本体）。"
                        : "血量≥89，选择奇迹代偿（本体暂缺，选完继续凑：聘用书/067/采购专员）。",
                    "F13→F14");
            }

            return OtherSideResponse(
                ctx,
                miracle,
                "血量 <89（或未识别到血量），点奇迹代偿会被游戏拒绝并卡死流程，必须选另一侧。",
                "F13→F15→F15a");
        }

        // F4/F7 → F10：令咒决议·回路过载 / 行为限制——识别奖励是否为五费聘用书
        if (FindCurseSide(ctx) is { } curseSide)
        {
            // 命中聘用书奖励的令咒侧优先；双侧都带聘用书时取左（与既有 PickWinningSide 的取左约定一致）
            if (FindLetterCurseSide(ctx) is { } letterSide)
                return new(
                    GrailTrialResponseKind.SelectLetterTrial,
                    letterSide,
                    ctx.NameOf(letterSide) ?? LetterRewardKeyword,
                    OpenLettersAfter: true,
                    "令咒决议侧的奖励是五费聘用书，选择它，选完立刻打开两个聘用书。",
                    "F10→F11");

            // F10 防御：令咒侧的奖励未读出时不能按“非聘用书”处理（可能漏掉唯一的聘用书机会）
            if (string.IsNullOrWhiteSpace(ctx.RewardOf(curseSide)))
                return new(
                    GrailTrialResponseKind.RecognitionUncertain,
                    Side: null,
                    SelectedTrialName: string.Empty,
                    OpenLettersAfter: false,
                    "令咒决议侧的奖励未识别，无法判定是否为五费聘用书，不盲点，等待识别重试。",
                    "F10防御");

            // F12：本侧不选（奖励非聘用书），但弹框强制二选一 → 选另一侧；
            // 若另一侧恰为无限之釜等关键试炼，旗标由 ApplyTo 按实际试炼名落地，判定由 L1 收口。
            return OtherSideResponse(
                ctx,
                curseSide,
                "令咒决议侧的奖励不是五费聘用书，不值得选；选另一侧关闭弹框。",
                "F10→F12");
        }

        // F8：无限之釜（若与其他三个关键试炼同现，上方分支已优先处理）
        if (FindSide(ctx, CauldronKeyword) is { } cauldron)
        {
            return s.Goal == GrailUserGoal.Single
                ? new(
                    GrailTrialResponseKind.SelectCauldronFinish,
                    cauldron,
                    ctx.NameOf(cauldron) ?? CauldronKeyword,
                    OpenLettersAfter: false,
                    "单人目标：无限之釜直接完成愿望，选完收工。",
                    "F8→F16")
                : new(
                    GrailTrialResponseKind.SelectCauldronContinue,
                    cauldron,
                    ctx.NameOf(cauldron) ?? CauldronKeyword,
                    OpenLettersAfter: false,
                    "全员目标：无限之釜只是辅助（视为获得一个5费角色），选完继续追奇迹代偿+本体昔涟。",
                    "F8→F17");
        }

        // F5：以上 4 个均未命中（含池子里全是红A！等一般试炼）→ 默认选左
        return new(
            GrailTrialResponseKind.PickLeftFallback,
            GrailTrialSide.Left,
            ctx.NameOf(GrailTrialSide.Left) ?? string.Empty,
            OpenLettersAfter: false,
            "四个关键试炼均未出现，默认选左推进。",
            "F3→F5");
    }

    /// <summary>
    /// 把一次弹框响应落地到运行状态（每次弹框必然消耗一次祈愿机会）。
    /// healthAtSelection 传决策那一刻识别到的血量（L2 前置的取证口径；必须取点击前的值，
    /// 奇迹代偿确认后血量即扣 88，再取就永久失真）。
    /// 无限之釜选中即视为“场上已有 5 费本体”：釜的效果=全三星圣杯角色（含 Archer 上场），
    /// 这是选择动作本身带来的游戏事实，不依赖后续识别是否捕捉到新角色。
    /// 注：聘用书“打开完成”由执行层在 F11a 每成功开一本后另行 +1 回填 LettersOpened；
    /// 非釜 granted 的阵容变化（买到昔涟等）由识别回填（本方法不伪造事实）。
    /// </summary>
    public static GrailRunSnapshot ApplyTo(GrailRunSnapshot s, GrailTrialResponse response, int? healthAtSelection)
    {
        var selected = response.SelectedTrialName ?? string.Empty;
        var isMiracle = GrailFuzzyText.ContainsFuzzy(selected, MiracleKeyword);
        var isCauldron = GrailFuzzyText.ContainsFuzzy(selected, CauldronKeyword);
        return s with
        {
            WishesResponded = s.WishesResponded + 1,
            MiracleCompensationSelected = s.MiracleCompensationSelected || isMiracle,
            MiracleCompensationSelectedAtHealth = isMiracle ? healthAtSelection : s.MiracleCompensationSelectedAtHealth,
            InfiniteCauldronSelected = s.InfiniteCauldronSelected || isCauldron,
            HasFiveCostBody = s.HasFiveCostBody || isCauldron,
            LettersObtained = s.LettersObtained + (response.OpenLettersAfter ? 2 : 0),
        };
    }

    /// <summary>选“被否决侧”的另一侧。若另一侧的奖励是聘用书，同样按 F11 规则选完立刻打开。</summary>
    private static GrailTrialResponse OtherSideResponse(
        GrailTrialPairContext ctx,
        GrailTrialSide blocked,
        string reason,
        string treeNode)
    {
        var other = ctx.Other(blocked);
        return new(
            GrailTrialResponseKind.SelectOtherSide,
            other,
            ctx.NameOf(other) ?? string.Empty,
            OpenLettersAfter: MatchesLetterReward(ctx.RewardOf(other)),
            reason,
            treeNode);
    }

    private static GrailTrialSide? FindSide(GrailTrialPairContext ctx, string keyword) =>
        Matches(ctx.LeftTrialName, keyword)
            ? GrailTrialSide.Left
            : Matches(ctx.RightTrialName, keyword)
                ? GrailTrialSide.Right
                : null;

    /// <summary>是否存在令咒决议类试炼（回路过载/行为限制），先左后右。</summary>
    private static GrailTrialSide? FindCurseSide(GrailTrialPairContext ctx)
    {
        if (Matches(ctx.LeftTrialName, OverloadKeyword) || Matches(ctx.LeftTrialName, RestrictKeyword))
            return GrailTrialSide.Left;
        if (Matches(ctx.RightTrialName, OverloadKeyword) || Matches(ctx.RightTrialName, RestrictKeyword))
            return GrailTrialSide.Right;
        return null;
    }

    /// <summary>奖励为五费聘用书的令咒决议侧（须同时是令咒类试炼），双侧命中取左。</summary>
    private static GrailTrialSide? FindLetterCurseSide(GrailTrialPairContext ctx)
    {
        if (IsCurseSide(ctx, GrailTrialSide.Left) && Matches(ctx.RewardOf(GrailTrialSide.Left), LetterRewardKeyword))
            return GrailTrialSide.Left;
        if (IsCurseSide(ctx, GrailTrialSide.Right) && Matches(ctx.RewardOf(GrailTrialSide.Right), LetterRewardKeyword))
            return GrailTrialSide.Right;
        return null;
    }

    private static bool IsCurseSide(GrailTrialPairContext ctx, GrailTrialSide side) =>
        Matches(ctx.NameOf(side), OverloadKeyword) || Matches(ctx.NameOf(side), RestrictKeyword);

    private static bool Matches(string? text, string keyword) =>
        GrailFuzzyText.ContainsFuzzy(text, keyword);

    /// <summary>聘用书奖励判定：模糊匹配"五费聘用书"，但显式排除"四费聘用书"（距离1，模糊匹配会误命中）。</summary>
    private static bool MatchesLetterReward(string? text) =>
        GrailFuzzyText.ContainsFuzzy(text, LetterRewardKeyword)
        && !(text ?? string.Empty).Contains(FourCostLetterKeyword, StringComparison.Ordinal);
}

/// <summary>
/// 试炼名/奖励名的容错匹配：子串命中，或滑动窗口编辑距离 ≤1（兼容 OCR 差一字，
/// 与既有 WishTrialSelectionAutomation 的 ContainsFuzzy 语义一致）。
/// </summary>
public static class GrailFuzzyText
{
    public static bool ContainsFuzzy(string? text, string keyword, int maxEditDistance = 1)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
            return false;
        if (text.Contains(keyword, StringComparison.Ordinal))
            return true;

        for (var length = keyword.Length - 1; length <= keyword.Length + 1; length++)
        {
            if (length <= 0 || length > text.Length)
                continue;
            for (var start = 0; start + length <= text.Length; start++)
            {
                if (EditDistance(text.Substring(start, length), keyword) <= maxEditDistance)
                    return true;
            }
        }

        return false;
    }

    private static int EditDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), substitution);
            }
        }

        return dp[a.Length, b.Length];
    }
}
