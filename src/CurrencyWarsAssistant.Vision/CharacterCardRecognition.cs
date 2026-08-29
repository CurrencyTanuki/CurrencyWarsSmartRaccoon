using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public sealed record CharacterCardTemplateDefinition(
    string CharacterId,
    string DisplayName,
    string File,
    CharacterCardTemplateKind Kind = CharacterCardTemplateKind.Character);

public enum CharacterCardTemplateKind
{
    Character,
    SpecialOccupied
}

/// <summary>星级星带定位方式（2026-08-08：实测前台星在卡面下部中央、
/// 后台/备战席星在卡面右下——UI 布局不同，分带识别）。</summary>
public enum StarBand
{
    /// <summary>前台：卡面下部中央（X 0.30-0.70, Y 0.70-0.95）列投影。</summary>
    FrontCenter,
    /// <summary>后台：卡面右下（X 0.75-1.05, Y 0.65-0.82）星核连通域。</summary>
    BackRight,
    /// <summary>备战席：右下（X 0.30-1.05, Y 0.60-0.95）星核连通域。</summary>
    BenchRight,
    /// <summary>完整场上列表：前 4 槽用前台带，其余槽用后台带。</summary>
    BoardBySlotIndex
}

public enum CharacterCardSlotState
{
    Empty,
    Recognized,
    SpecialOccupied,
    Uncertain
}

public sealed record CharacterCardSlotRecognition(
    int SlotIndex,
    PixelRect ReferenceBounds,
    CharacterCardSlotState State,
    string? CharacterId,
    string? DisplayName,
    double Confidence,
    double RunnerUpConfidence,
    double VisualStandardDeviation,
    string? RunnerUpCharacterId = null,
    string? RunnerUpDisplayName = null,
    string? MatchedTemplateId = null,
    int? StarLevel = null,
    double StarConfidence = 0,
    string? MatchedVariantId = null,
    int? CurrentCost = null,
    string? MatchedCostVariantId = null,
    double CurrentCostConfidence = 0,
    double CurrentCostRunnerUpConfidence = 0);

public readonly record struct CharacterCardRecognitionOptions(
    double HorizontalTemplateScale = 1,
    double VerticalTemplateScale = 1,
    // 避开卡牌底部比例（0-1）：被应援（打call）的角色底部有应援棒
    // 发光污染，模板匹配时排除底部该比例区域（用户 2026-08-07：
    // 000036/000037 银狼被应援 → 匹配分 0.43 卡阈值失败）。
    double ExcludeBottomRatio = 0,
    // 同时避开左右两侧（应援棒在卡牌左右边缘，000036 实测左右发光棒
    // 污染匹配——ExcludeBottomRatio 只避底部不够）。
    bool ExcludeSides = false,
    // 星级星带定位（2026-08-08：前台中央/后台右下/备战席右下）。
    StarBand StarBand = StarBand.FrontCenter,
    // 2026-08-19 用户拍板（方案 A + 第一步）：后台 warp 行。意义有二：
    // ① Recognize 对后台槽用「卡面内缩 6px」的裁框做角色/模板匹配（剔除
    //    卡面边缘能量地块淡淡特效）——probe(BACK_INSET.txt) 实证内缩 6 让
    //    后台槽0 从「误判开拓者 0.499」纠正为「正确霍霍 0.503~0.538」；
    //    星级检测仍用原槽位（不内缩，保护 000032 金标准星级）。
    // ② Match 对后台用宽松颜色惩罚触发线(0.80)，前台/备战席保留 0.90——
    //    保证相似角色区分的综合正确率高于方案 B(全局删惩罚)。
    bool BackRow = false)
{
    public CharacterCardRecognitionOptions(double uniformTemplateScale)
        : this(uniformTemplateScale, uniformTemplateScale)
    {
    }

    public static CharacterCardRecognitionOptions Standard => new(1, 1);

    public static CharacterCardRecognitionOptions RewardShopCompact =>
        new(0.80, 0.80);
}

public interface ICharacterCardRecognizer
{
    IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots);

    IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots,
        CharacterCardRecognitionOptions options) =>
        Recognize(frame, templates, referenceSlots);
}

public sealed class OpenCvCharacterCardRecognizer :
    ICharacterCardRecognizer,
    IDisposable
{
    private const double EmptyVisualStandardDeviation = 18;
    // 阈值放宽（2026-08-06 用户要求识别层修复）：实测真实备战帧
    // Bench 槽位最佳候选 conf 0.56-0.57，旧阈值 0.58 全部判 Uncertain →
    // 阵容/瓦尔特/羁绊数据丢失。降到 0.55 + 区分度 0.04：接近阈值的
    // 槽位按最佳候选识别（宁可低置信有数据，不要整条 Unknown）。
    // 银狼（变费角色）实测 Match 分 ~0.50 无法稳定过线——根因是变费
    // 背景色 3/4/5 费不同 + 特殊装备图标 + 倾斜，需变费适配（用户
    // 2026-08-07 已定性"之后做适配"），非阈值问题。维持 0.55 防误判。
    private const double MinimumCharacterConfidence = 0.55;
    // 变费角色（银狼 3/4/5 费背景色不同）专用置信度门槛（用户 2026-08-07
    // 适配）：银狼卡面背景随费级变化，与单一费级模板匹配分天然偏低
    //（detail 5/5/15 下实测 0.465——不靠裁剪，裁剪会伤其他角色）。
    // 仅银狼放宽到 0.46（最佳候选稳定是银狼、runner-up 分差大），
    // 其他角色保持 0.55。
    private const double LenientConfidenceForVariableCost = 0.39;
    // 区分度门默认 0.04（保守）：白厄/飞霄等与开拓者 user 模板相似的角色，
    // runner-up 分差被压到 0.013-0.047（混淆风险）——全局放宽会导致误认。
    // 方案（用户 2026-08-07）：仅对白厄/飞霄/开拓者这类"银灰发系相似角色"
    // 单独放宽区分度门到 0.01（实测白厄分差仅 0.013，0.02 仍不够），
    // 其他角色保持 0.04 保守。接受这些相似角色的少量误认风险（用户授权）。
    private const double MinimumLeadOverRunnerUp = 0.040;
    private const double LenientLeadOverRunnerUp = 0.010;
    private const double ColorSimilarityGrace = 0.90;
    // 方案 A（2026-08-19 用户拍板）：后台 warp 槽专属宽松触发线。后台卡边
    // 能量地块淡淡特效把 colorSim 压到 0.815~0.823，旧 0.90 触发惩罚(扣0.04)。
    // 后台放宽到 0.80 = 只惩罚"明显不像"；前台/备战席保留 0.90（保护相似角色
    // 区分，综合正确率高于"全局删惩罚"方案 B）。
    private const double BackRowColorSimilarityGrace = 0.80;
    // 内缩像素（2026-08-19）：后台 warp 行剔除卡面边缘能量地块特效。
    private const int BackRowMatchInset = 6;
    // 2026-08-19 用户拍板"后台降一点阈值"：后台 warp 行置信度阈值 0.55→0.50、
    // runner-up 区分度门 0.04→0.015（实测内缩后霍霍 lead 实际 ~0.019 略低于
    // 0.02 边界卡死；0.015 留足余量，仍保留角色区分）。配合内缩剔除能量地块
    // 特效后的天然较低本质分。
    private const double BackRowConfidenceThreshold = 0.50;
    private const double BackRowLeadOverRunnerUp = 0.015;
    // 前台 1 号位(Slot0) 专属置信度阈值（2026-08-19 用户拍板）：能量地块特效
    // 只压这个位置卡面的识别分（实测直连最佳 conf 0.58、runner-up 差 0.16，只降
    // 分不乱认），关能量后立即恢复。前台 1 号位降到 0.52 即可救回；其他前台槽/
    // 后台/备战席不受影响（保持 0.55）。
    private const double FrontSlotZeroConfidenceThreshold = 0.52;
    private const double ColorMismatchPenaltyWeight = 0.50;
    private const int StandardTemplateWidth = 111;
    private const int StandardTemplateHeight = 127;
    private const int HorizontalSearchPadding = 10;
    private readonly ConcurrentDictionary<string, Mat> _templates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(
        string File,
        int HorizontalScalePermille,
        int VerticalScalePermille), Mat>
        _scaledTemplates = new();
    private readonly ConcurrentDictionary<string, float[]> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly int _candidateLimit;
    // 区分度门放宽的角色集合（用户 2026-08-07）：白厄/飞霄/开拓者等
    // 银灰发系相似角色，与用户实机模板 runner-up 分差小（0.013-0.047），
    // 全局 0.04 会判 Uncertain——仅这些角色放宽到 0.02，其他保持 0.04。
    private readonly HashSet<string> _lenientLeadOverCharacterIds;
    // 变费角色（银狼）专用置信度门槛集合（用户 2026-08-07 适配）。
    private readonly HashSet<string> _lenientConfidenceCharacterIds;

    public OpenCvCharacterCardRecognizer(
        int candidateLimit = 32,
        IEnumerable<string>? lenientLeadOverCharacterIds = null,
        IEnumerable<string>? lenientConfidenceCharacterIds = null)
    {
        if (candidateLimit < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateLimit),
                "At least two candidates are required for the runner-up gate.");
        }

        _candidateLimit = candidateLimit;
        _lenientLeadOverCharacterIds =
            new HashSet<string>(
                lenientLeadOverCharacterIds ?? [],
                StringComparer.OrdinalIgnoreCase);
        _lenientConfidenceCharacterIds =
            new HashSet<string>(
                lenientConfidenceCharacterIds ?? [],
                StringComparer.OrdinalIgnoreCase);
        // 2026-08-08：特殊单位模板（佩佩等）卡面与角色模板差异大、匹配分
        // 天然偏低（000032 佩佩 conf 0.472 vs 常规 0.55），内置 lenient
        // 阈值——特殊单位卡面独特、runner-up 分差大，无误判风险。
        _lenientConfidenceCharacterIds.Add("special_unit_peipei");
        // 后台特殊单位（2026-08-15 素材包）：牌面与角色模板差异大、匹配分
        // 天然偏低（佩佩先例），全部按宽松置信度处理。
        for (var n = 101; n <= 113; n++)
        {
            _lenientConfidenceCharacterIds.Add($"special_unit_tanuki_{n}");
        }

        for (var n = 901; n <= 903; n++)
        {
            _lenientConfidenceCharacterIds.Add($"special_unit_owlbert_{n}");
        }
        _lenientConfidenceCharacterIds.Add(
            "bench_special_privilege_armament_box");
    }

    private double LeadOverThresholdFor(
        CharacterCardTemplateDefinition best,
        bool backRow = false) =>
        // 特殊单位（狸猫/叽米/佩佩等）同类卡面彼此相似（如 Gemi狸 111/112
        // 两张、叽米三个功能变体），压制 runner-up 的阈值必然卡死——
        // 不要求压制（2026-08-15 实机：tanuki_112 0.683 因 LeadOver 差
        // 0.04 被判 Uncertain）。
        best.Kind == CharacterCardTemplateKind.SpecialOccupied
            ? 0
            // 后台 warp 行（2026-08-19 用户拍板"后台降一点阈值"）：内缩剔除
            // 能量地块特效后本质 conf/lead 天然低于前台（实测霍霍 0.538/lead0.019），
            // 后台放宽 lead 到 0.015（保留区分，0.019 即可过）；前台/备战席不变。
            : backRow
                ? BackRowLeadOverRunnerUp
                : _lenientLeadOverCharacterIds.Contains(best.CharacterId)
                    ? LenientLeadOverRunnerUp
                    : MinimumLeadOverRunnerUp;

    private double ConfidenceThresholdFor(
        CharacterCardTemplateDefinition best,
        bool backRow = false) =>
        // 后台 warp 行（2026-08-19 用户拍板"后台降一点阈值"）：0.55→0.50，
        // 让内缩后正确后台槽(实测霍霍 0.538 / analyze 0.52)能过关；前台/备战席
        // 保持 0.55。银狼变费的 lenient 0.39 优先保留（后台银狼仍用更低门槛）。
        backRow
            ? Math.Min(BackRowConfidenceThreshold, ConfidenceThresholdLenientFor(best))
            : ConfidenceThresholdLenientFor(best);

    private double ConfidenceThresholdLenientFor(
        CharacterCardTemplateDefinition best) =>
        _lenientConfidenceCharacterIds.Contains(best.CharacterId)
            ? LenientConfidenceForVariableCost
            : MinimumCharacterConfidence;

    private int _lastExactComparisonCount;
    private int _lastDecisiveShortlistCount;
    public int LastExactComparisonCount =>
        System.Threading.Volatile.Read(ref _lastExactComparisonCount);
    public int LastDecisiveShortlistCount =>
        System.Threading.Volatile.Read(ref _lastDecisiveShortlistCount);

    /// <summary>
    /// Preloads the compact search index and normalized card templates so the
    /// first preparation frame does not pay the one-time disk/decode cost.
    /// </summary>
    public Task WarmUpAsync(
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return Task.Run(
            () => Parallel.ForEach(
                templates
                    .GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4
                },
                template =>
                {
                    _ = _descriptors.GetOrAdd(
                        template.File,
                        _ => CreateCompactDescriptor(
                            LoadTemplate(template.File)));
                }),
            cancellationToken);
    }

    public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots) => Recognize(
        frame,
        templates,
        referenceSlots,
        CharacterCardRecognitionOptions.Standard);

    public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> referenceSlots,
        CharacterCardRecognitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(referenceSlots);
        if (!OpenCvTemplateMatcher.HasSupportedAspectRatio(
                frame.Width,
                frame.Height))
        {
            return referenceSlots
                .Select((slot, index) => Uncertain(index, slot, 0))
                .ToArray();
        }

        using var normalized = Normalize(frame);
        System.Threading.Interlocked.Exchange(ref _lastDecisiveShortlistCount, 0);
        IReadOnlyList<CharacterCardSlotRecognition> results;
        var resultArr = new CharacterCardSlotRecognition[referenceSlots.Count];
        // 2026-08-20 性能：多槽识别互不依赖（模板缓存 ConcurrentDictionary、
        // 只读 normalized/frame），并行识别大幅降低热身全量帧耗时。
        System.Threading.Tasks.Parallel.For(
            0,
            referenceSlots.Count,
            index =>
        {
            var slot = referenceSlots[index];
            if (slot.IsEmpty ||
                slot.X < 0 ||
                slot.Y < 0 ||
                slot.Right > normalized.Width ||
                slot.Bottom > normalized.Height)
            {
                resultArr[index] = Uncertain(index, slot, 0);
                return;
            }

            using var slotImage = new Mat(
                normalized,
                new Rect(slot.X, slot.Y, slot.Width, slot.Height));
            using var grayscale = new Mat();
            Cv2.CvtColor(
                slotImage,
                grayscale,
                ColorConversionCodes.BGR2GRAY);
            var insetX = Math.Max(4, (int)Math.Round(slot.Width * 0.08));
            var insetTop = Math.Max(4, (int)Math.Round(slot.Height * 0.08));
            var insetBottom = Math.Max(
                4,
                (int)Math.Round(slot.Height * 0.12));
            using var occupancyInterior = new Mat(
                grayscale,
                new Rect(
                    insetX,
                    insetTop,
                    Math.Max(1, grayscale.Width - insetX * 2),
                    Math.Max(1, grayscale.Height - insetTop - insetBottom)));
            Cv2.MeanStdDev(
                occupancyInterior,
                out _,
                out var standardDeviation);
            var visualStandardDeviation = standardDeviation.Val0;
            if (visualStandardDeviation <= EmptyVisualStandardDeviation)
            {
                resultArr[index] = new CharacterCardSlotRecognition(
                    index,
                    slot,
                    CharacterCardSlotState.Empty,
                    null,
                    null,
                    0,
                    0,
                    visualStandardDeviation);
                return;
            }

            // 星级：后台/备战席用原始分辨率星核检测（Normalize 的 Area
            // 插值稀释小星核——2026-08-08）；前台用 Normalize 后双带。
            var starBand = options.StarBand == StarBand.BoardBySlotIndex
                ? index < 4
                    ? StarBand.FrontCenter
                    : StarBand.BackRight
                : options.StarBand;
            var starRecognition =
                starBand is StarBand.BackRight or StarBand.BenchRight
                    ? CountStarCoresFromFrame(frame, slot, starBand)
                    : RecognizeStarLevel(slotImage, starBand);

            // 2026-08-19 用户拍板：后台 warp 槽 + 前台能量地块都需「卡面边缘
            // 内缩」剔除能量地块特效后才稳定识别（用户控制实验实锤：能量激活
            // 时前台 1 号位 unknown、关闭后恢复）。星级检测仍用原槽位（不内缩，
            // 保护 000032 金标准星级）；内缩后需保证 searchImage ≥ 模板
            // (111x127)，太小则回退不加。
            var useBackRowInset =
                options.BackRow &&
                slot.X + BackRowMatchInset >= 0 &&
                slot.Y + BackRowMatchInset >= 0 &&
                slot.Width - BackRowMatchInset * 2 >= StandardTemplateWidth &&
                slot.Height - BackRowMatchInset * 2 >= StandardTemplateHeight;
            var matchRect = useBackRowInset
                ? new PixelRect(
                    slot.X + BackRowMatchInset,
                    slot.Y + BackRowMatchInset,
                    slot.Width - BackRowMatchInset * 2,
                    slot.Height - BackRowMatchInset * 2)
                : slot;
            var searchLeft = Math.Max(0, matchRect.X - HorizontalSearchPadding);
            var searchRight = Math.Min(
                normalized.Width,
                matchRect.Right + HorizontalSearchPadding);
            using var searchImage = new Mat(
                normalized,
                new Rect(
                    searchLeft,
                    matchRect.Y,
                    searchRight - searchLeft,
                    matchRect.Height));

            var shortlisted = Shortlist(slotImage, templates);
            System.Threading.Interlocked.Exchange(ref _lastExactComparisonCount, shortlisted.Count);
            var ranked = Rank(searchImage, shortlisted, options);
            if (IsDecisive(ranked))
            {
                System.Threading.Interlocked.Increment(ref _lastDecisiveShortlistCount);
            }
            else if (shortlisted.Count < templates.Count)
            {
                // Uncertain slots are evidence too. Preserve the exhaustive
                // best/runner-up candidates for degraded records while the
                // high-confidence common path stays bounded by the shortlist.
                ranked = Rank(searchImage, templates, options);
                System.Threading.Interlocked.Exchange(ref _lastExactComparisonCount, templates.Count);
            }
            var best = ranked.FirstOrDefault();
            // runner-up 取第一个"不同角色"的候选（银狼等变费角色有多个变体
            // 模板，同角色变体不算对手——review 2026-08-07 should-fix）。
            var runnerUp = 0d;
            if (best.Definition is not null)
            {
                for (var i = 1; i < ranked.Length; i++)
                {
                    if (!string.Equals(
                            ranked[i].Definition.CharacterId,
                            best.Definition.CharacterId,
                            StringComparison.Ordinal))
                    {
                        runnerUp = ranked[i].Confidence;
                        break;
                    }
                }
            }
            if (best.Definition is not null)
            {
                var confidenceThreshold =
                    ConfidenceThresholdFor(best.Definition, options.BackRow);
                // 前台 1 号位(Slot0) 能量地块特效降分 → 专属降阈值(2026-08-19)。
                if (index == 0 && !options.BackRow)
                {
                    confidenceThreshold = Math.Min(
                        confidenceThreshold, FrontSlotZeroConfidenceThreshold);
                }
                var leadPass =
                    best.Confidence - runnerUp >=
                    LeadOverThresholdFor(best.Definition, options.BackRow);
                if (best.Confidence >= confidenceThreshold && leadPass)
                {
                var isSpecialOccupied =
                    best.Definition.Kind ==
                    CharacterCardTemplateKind.SpecialOccupied;
                var matchedVariant = ResolveMatchedVariant(
                    searchImage,
                    templates,
                    best.Definition,
                    options);
                resultArr[index] = new CharacterCardSlotRecognition(
                    index,
                    slot,
                    isSpecialOccupied
                        ? CharacterCardSlotState.SpecialOccupied
                        : CharacterCardSlotState.Recognized,
                    // 2026-08-08：SpecialOccupied 也输出模板 id（如
                    // special_unit_peipei / bench_special_privilege_armament_box），
                    // 否则特殊单位（佩佩等）无法在报告显示名字。
                    best.Definition.CharacterId,
                    best.Definition.DisplayName,
                    best.Confidence,
                    runnerUp,
                    visualStandardDeviation,
                    ranked.Length > 1
                        ? ranked[1].Definition.CharacterId
                        : null,
                    ranked.Length > 1
                        ? ranked[1].Definition.DisplayName
                        : null,
                    best.Definition.CharacterId,
                    // 特殊单位（佩佩/特权武装箱）无星级——避免金色装饰
                    // 在星带内计为虚假星级（review 2026-08-08 nit）。
                    isSpecialOccupied
                        ? null
                        : starRecognition.Level,
                    isSpecialOccupied
                        ? 0
                        : starRecognition.Confidence,
                    MatchedVariantId:
                        Path.GetFileNameWithoutExtension(
                            matchedVariant.Definition.File),
                    CurrentCost: isSpecialOccupied
                        ? null
                        : matchedVariant.CurrentCost,
                    MatchedCostVariantId: matchedVariant.CostVariantId,
                    CurrentCostConfidence: matchedVariant.CostConfidence,
                    CurrentCostRunnerUpConfidence:
                        matchedVariant.CostRunnerUpConfidence);
                return;
            }
            }

            resultArr[index] = new CharacterCardSlotRecognition(
                index,
                slot,
                CharacterCardSlotState.Uncertain,
                best.Definition?.CharacterId,
                best.Definition?.DisplayName,
                best.Confidence,
                runnerUp,
                visualStandardDeviation,
                ranked.Length > 1
                    ? ranked[1].Definition.CharacterId
                    : null,
                ranked.Length > 1
                    ? ranked[1].Definition.DisplayName
                    : null,
                StarLevel: starRecognition.Level,
                StarConfidence: starRecognition.Confidence);
        });

        results = resultArr;
        return results;
    }

    private (
        CharacterCardTemplateDefinition Definition,
        int? CurrentCost,
        string? CostVariantId,
        double CostConfidence,
        double CostRunnerUpConfidence)
        ResolveMatchedVariant(
            Mat searchImage,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            CharacterCardTemplateDefinition identityWinner,
            CharacterCardRecognitionOptions options)
    {
        var sameCharacterVariants = templates
            .Where(item => string.Equals(
                item.CharacterId,
                identityWinner.CharacterId,
                StringComparison.Ordinal))
            .ToArray();
        if (sameCharacterVariants.Length <= 1)
        {
            return (identityWinner, null, null, 0, 0);
        }

        var explicitCostVariants = sameCharacterVariants
            .Where(item => ResolveExplicitCurrentCost(item) is not null)
            .ToArray();
        if (explicitCostVariants
                .Select(ResolveExplicitCurrentCost)
                .Distinct()
                .Count() < 2)
        {
            return (identityWinner, null, null, 0, 0);
        }

        // Shortlist 为保证角色覆盖，每个角色只保留一个模板；身份确定后仅
        // 银狼的显式费用模板做二次比较。其他多皮肤角色不增加匹配开销。
        var rankedCosts = Rank(searchImage, explicitCostVariants, options);
        var costWinner = rankedCosts[0];
        var winnerCost = ResolveExplicitCurrentCost(costWinner.Definition);
        var differentCostRunner = rankedCosts.FirstOrDefault(item =>
            ResolveExplicitCurrentCost(item.Definition) is int candidateCost &&
            candidateCost != winnerCost);
        var runnerConfidence = differentCostRunner.Definition is null
            ? 0
            : differentCostRunner.Confidence;
        var costIsDecisive = costWinner.Confidence >= 0.39 &&
            costWinner.Confidence - runnerConfidence >= 0.04;
        return (
            identityWinner,
            // 当前只有用户逐图确认的 5 费复杂卡面进入业务状态；3/4 费
            // 模板仍作为反例竞争者，避免把蓝色/紫色受全屏色调影响的卡面
            // 强行报成错误费用。补齐各自真实真值集前保持 null 更安全。
            costIsDecisive && winnerCost == 5 ? winnerCost : null,
            Path.GetFileNameWithoutExtension(costWinner.Definition.File),
            costWinner.Confidence,
            runnerConfidence);
    }

    private static int? ResolveExplicitCurrentCost(
        CharacterCardTemplateDefinition definition)
    {
        // 银狼 LV.999 的 3/4/5 费会切换卡面颜色。费用只来自已经赢得
        // 角色匹配的明确变体模板；不对普通角色或模糊模板猜测。
        if (!string.Equals(
                definition.CharacterId,
                "currency_wars_character_05",
                StringComparison.Ordinal))
        {
            return null;
        }

        var variant = Path.GetFileNameWithoutExtension(definition.File);
        if (variant.EndsWith("__user_4cost", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (variant.EndsWith("__user_5cost", StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        // default 是基础蓝色候选；user_complex 不带费用语义，仍排除。
        return variant.EndsWith("__default", StringComparison.OrdinalIgnoreCase)
            ? 3
            : null;
    }

    private static (int? Level, double Confidence) RecognizeStarLevel(
        Mat slotImage,
        StarBand starBand = StarBand.FrontCenter)
    {
        // 2026-08-08 分区：前台星带 = 卡面下部中央（固定带+卡底扩展带，
        // 多数卡星在 0.66-0.94H、个别在 0.90-1.0H）；后台/备战席星带 =
        // 卡面右下（星核连通域，实测刃/开拓者/爻光星在 X 0.75-1.05、
        // Y 0.65-0.82，前台中央带在后台检出 0 星）。
        // 注意：后台/备战席星核检测必须用原始分辨率帧（Normalize 的
        // Area 插值会把小星核平均稀释，RGB 阈值检不出——2026-08-08）。
        if (starBand is StarBand.BackRight or StarBand.BenchRight)
        {
            // 由 Recognize 主流程在原始帧上调用 CountStarCoresFromFrame
            return (null, 0);
        }

        // 前台双带（原逻辑）：固定主带 0.66-0.94H，主带 0 峰时卡底带
        // 0.90-1.0H（刃等卡底星）。
        var left = Math.Max(0, (int)Math.Round(slotImage.Width * 0.16));
        var width = Math.Min(
            slotImage.Width - left,
            Math.Max(1, (int)Math.Round(slotImage.Width * 0.68)));
        if (width < 8)
        {
            return (null, 0);
        }

        var primary = CountStarPeaksInBand(
            slotImage,
            left,
            width,
            0.66,
            0.28);
        if (primary.Level is not null)
        {
            return primary;
        }

        var bottom = CountStarPeaksInBand(
            slotImage,
            left,
            width,
            0.90,
            0.10);
        if (bottom.Level is not null)
        {
            return bottom;
        }

        // Animated front-card lighting can dilute both star cores just below
        // the primary threshold. A slightly lower, narrower band recovers the
        // real cores without widening into the equipment/effect area.
        var animated = CountStarPeaksInBand(
            slotImage,
            left,
            width,
            0.68,
            0.26);
        if (animated.Level is not null)
        {
            return animated;
        }

        return (null, 0);
    }

    /// <summary>
    /// 后台/备战席星带（原始分辨率）：卡面右下，星核（超亮金黄
    /// R&gt;180 G&gt;130 B&lt;130，原始帧）连通域计数——Normalize 的
    /// Area 插值会把小星核平均稀释，故必须用原始帧（2026-08-08 像素
    /// 标定：爻光 2 星并排、其他 1 星，聚类阈值 11px 区分相邻星与同星
    /// 碎块）。
    /// </summary>
    private static (int? Level, double Confidence) CountStarCoresFromFrame(
        CaptureFrame frame,
        PixelRect slot,
        StarBand starBand)
    {
        var (xStartRatio, xEndRatio, yStartRatio, yEndRatio) = starBand switch
        {
            // 实测（C# 槽位左上角坐标系，2026-08-08）：后台/备战席星在
            // 槽内 X 0.30-0.70 中央带（用户"卡面没有区别"正确——前后台
            // 星都在中央），后台 Y 0.65-0.82、备战席 Y 0.65-0.95。
            StarBand.BackRight => (0.30, 0.70, 0.65, 0.82),
            _ => (0.30, 0.70, 0.65, 0.95)
        };
        var scaleX = frame.Width / 1920d;
        var scaleY = frame.Height / 1080d;
        var px0 = (int)Math.Round((slot.X + slot.Width * xStartRatio) * scaleX);
        var px1 = (int)Math.Round((slot.X + slot.Width * xEndRatio) * scaleX);
        var py0 = (int)Math.Round((slot.Y + slot.Height * yStartRatio) * scaleY);
        var py1 = (int)Math.Round((slot.Y + slot.Height * yEndRatio) * scaleY);
        px0 = Math.Max(0, px0);
        py0 = Math.Max(0, py0);
        px1 = Math.Min(frame.Width, px1);
        py1 = Math.Min(frame.Height, py1);
        var width = px1 - px0;
        var height = py1 - py0;
        if (width < 8 || height < 8)
        {
            return (null, 0);
        }

        // 星核掩码（原始 BGRA 像素直判：超亮白黄 R>245 G>225——像素标定
        // 星中心 RGB 255/239/203，非金黄（B 高），B<130 阈值会漏检）
        var cores = new List<(double X, double Y)>();
        var visited = new bool[height, width];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = (py0 + y) * frame.Stride + px0 * 4;
            for (var x = 0; x < width; x++)
            {
                if (visited[y, x])
                {
                    continue;
                }

                var offset = rowOffset + x * 4;
                var g = frame.BgraPixels[offset + 1];
                var r = frame.BgraPixels[offset + 2];
                if (r <= 245 || g <= 225)
                {
                    continue;
                }

                // BFS 8 邻域连通域
                var queue = new Queue<(int Y, int X)>();
                queue.Enqueue((y, x));
                visited[y, x] = true;
                var sumX = 0d;
                var sumY = 0d;
                var count = 0;
                var minX = int.MaxValue;
                var minY = int.MaxValue;
                var maxX = int.MinValue;
                var maxY = int.MinValue;
                while (queue.Count > 0)
                {
                    var (cy, cx) = queue.Dequeue();
                    sumX += cx;
                    sumY += cy;
                    count++;
                    minX = Math.Min(minX, cx);
                    minY = Math.Min(minY, cy);
                    maxX = Math.Max(maxX, cx);
                    maxY = Math.Max(maxY, cy);
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dy == 0 && dx == 0)
                            {
                                continue;
                            }

                            var ny = cy + dy;
                            var nx = cx + dx;
                            if (ny < 0 || ny >= height ||
                                nx < 0 || nx >= width ||
                                visited[ny, nx])
                            {
                                continue;
                            }

                            var no = (py0 + ny) * frame.Stride + (px0 + nx) * 4;
                            if (frame.BgraPixels[no + 1] <= 225 ||
                                frame.BgraPixels[no + 2] <= 245)
                            {
                                continue;
                            }

                            visited[ny, nx] = true;
                            queue.Enqueue((ny, nx));
                        }
                    }
                }

                var componentWidth = maxX - minX + 1;
                var componentHeight = maxY - minY + 1;
                // 形状判别（2026-08-16 爻光 2→3 回归根因）：星核是圆/方
                // 形，卡牌顶部亮横条（实测 28×3 长条）经 warp 立方插值后
                // 亮度抬过阈值、恰好扫进星带顶边，被误当星 → 宽高比
                // ≤3:1 过滤长条（星 4×4 比值 1 不受影响）。
                var aspectRatio = (double)Math.Max(componentWidth, componentHeight) /
                    Math.Max(1, Math.Min(componentWidth, componentHeight));
                // C 步（2026-08-17）：warp 透视（WarpBackRow Linear 插值）
                // 会把卡面顶部某个亮元素抬过 RGB 阈值，在星带顶边额外造出
                // 一个接近方形的小亮核（实测：三月七应 1 星被识别 2 星，
                // 伪核在星带顶部 4×3、真核在星带底部下沿 4×4）。真星核恒
                // 贴星带下沿（爻光真 2 星相对 y=0.92、三月七真星 0.917），
                // 伪影恒在顶部（爻光横条 0.0、三月七伪核 0.042）→ 后台
                // 星带只保留下部（相对高度 ≥0.5）的星核。仅 BackRight（
                // warp 路径）启用，备战席/前台不带伪影证据不动。
                var yRatio = (sumY / count) / Math.Max(1d, height);
                var passesPosition =
                    starBand is not StarBand.BackRight || yRatio >= 0.5d;
                if (count >= 3 &&
                    componentWidth >= 3 &&
                    componentHeight >= 3 &&
                    aspectRatio <= 3d &&
                    passesPosition)
                {
                    cores.Add((sumX / count, sumY / count));
                }
            }
        }

        // 聚类（星间距 11px 阈值：爻光 2 星间距 12 分开、同星碎块 ≤10 合并）
        var merged = new List<(double X, double Y)>();
        foreach (var core in cores.OrderBy(c => c.X))
        {
            var index = -1;
            for (var i = 0; i < merged.Count; i++)
            {
                if (Math.Abs(merged[i].X - core.X) < 11 &&
                    Math.Abs(merged[i].Y - core.Y) < 11)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                merged.Add(core);
            }
            else
            {
                merged[index] = (
                    (merged[index].X + core.X) / 2,
                    (merged[index].Y + core.Y) / 2);
            }
        }

        if (merged.Count is < 1 or > 3)
        {
            return (null, 0);
        }

        var confidence = Math.Clamp(
            0.55 + (merged.Count - 1) * 0.1,
            0.55,
            0.85);
        return (merged.Count, confidence);
    }

    /// <summary>在指定 y 带（相对卡高）内做金色掩码列投影峰值计数。</summary>
    private static (int? Level, double Confidence) CountStarPeaksInBand(
        Mat slotImage,
        int left,
        int width,
        double topRatio,
        double heightRatio)
    {
        var top = Math.Max(0, (int)Math.Round(slotImage.Height * topRatio));
        var height = Math.Min(
            slotImage.Height - top,
            Math.Max(1, (int)Math.Round(slotImage.Height * heightRatio)));
        if (width < 8 || height < 8)
        {
            return (null, 0);
        }

        using var band = new Mat(slotImage, new Rect(left, top, width, height));
        using var hsv = new Mat();
        using var goldMask = new Mat();
        Cv2.CvtColor(band, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(
            hsv,
            new Scalar(5, 55, 165),
            new Scalar(42, 255, 255),
            goldMask);

        using var projectionMat = new Mat();
        Cv2.Reduce(
            goldMask,
            projectionMat,
            ReduceDimension.Row,
            ReduceTypes.Sum,
            MatType.CV_32SC1.Value);
        var rawProjection = new double[goldMask.Width];
        for (var x = 0; x < goldMask.Width; x++)
        {
            rawProjection[x] = projectionMat.At<int>(0, x) / 255d;
        }

        var projection = new double[rawProjection.Length];
        for (var x = 0; x < rawProjection.Length; x++)
        {
            var start = Math.Max(0, x - 2);
            var end = Math.Min(rawProjection.Length - 1, x + 2);
            var sum = 0d;
            for (var sample = start; sample <= end; sample++)
            {
                sum += rawProjection[sample];
            }

            projection[x] = sum / (end - start + 1);
        }

        var minimumPeak = Math.Max(2.4, goldMask.Height * 0.10);
        var minimumDistance = Math.Max(7, goldMask.Width / 10);
        var peaks = Enumerable.Range(1, Math.Max(0, projection.Length - 2))
            .Where(x => projection[x] >= minimumPeak &&
                        projection[x] >= projection[x - 1] &&
                        projection[x] >= projection[x + 1])
            .OrderByDescending(x => projection[x])
            .ThenBy(x => x)
            .Aggregate(
                new List<int>(),
                (selected, candidate) =>
                {
                    if (selected.All(existing =>
                            Math.Abs(existing - candidate) >= minimumDistance))
                    {
                        selected.Add(candidate);
                    }

                    return selected;
                })
            .Take(3)
            .OrderBy(x => x)
            .ToArray();

        // 三颗真星的间距近似均匀；与其余两峰显著隔开的边缘峰
        // 来自装备/应援金色特效，不应计为第三颗星。
        if (peaks.Length == 3)
        {
            var middleStrength = projection[peaks[1]];
            var edgeStrength = Math.Min(
                projection[peaks[0]],
                projection[peaks[2]]);
            if (middleStrength <= edgeStrength * 0.60)
            {
                // A weak highlight between two strong, evenly spaced star cores
                // is card art, not a third star (frozen 1-6/1-9 Huohuo frames).
                peaks = [peaks[0], peaks[2]];
            }
            else
            {
                var leftGap = peaks[1] - peaks[0];
                var rightGap = peaks[2] - peaks[1];
                if (rightGap - leftGap >= minimumDistance)
                {
                    peaks = peaks[..2];
                }
                else if (leftGap - rightGap >= minimumDistance)
                {
                    peaks = peaks[1..];
                }
            }
        }

        if (peaks.Length is < 1 or > 3)
        {
            return (null, 0);
        }

        var weakestPeak = peaks.Min(x => projection[x]);
        var confidence = Math.Clamp(
            0.55 + (weakestPeak - minimumPeak) /
            Math.Max(1, goldMask.Height) * 0.9,
            0.55,
            0.96);
        return (peaks.Length, confidence);
    }

    public void Dispose()
    {
        foreach (var template in _templates.Values)
        {
            template.Dispose();
        }

        _templates.Clear();
        foreach (var template in _scaledTemplates.Values)
        {
            template.Dispose();
        }

        _scaledTemplates.Clear();
        _descriptors.Clear();
    }

    private IReadOnlyList<CharacterCardTemplateDefinition> Shortlist(
        Mat slotImage,
        IReadOnlyList<CharacterCardTemplateDefinition> templates)
    {
        if (templates.Count <= _candidateLimit)
        {
            return templates;
        }

        var query = CreateCompactDescriptor(slotImage);
        // 短名单按"角色"聚合（2026-08-07 修复）：多变体加载后（银狼 3/4/5费、
        // 开拓者记忆/欢愉、用户实机变体），同角色多个模板若各占一个名额，
        // 会挤掉其他角色（如刻律德菈被挤出前 32 → Uncertain，ExpandedShop
        // 回归失败）。改为：每个角色先取 descriptor 相似度最高的模板，
        // 再按角色相似度取前 _candidateLimit 个角色（保留各角色 best 模板）——
        // 保证角色覆盖不缩水，同角色多变体由 Rank 层取最高 Match 分。
        return templates
            .GroupBy(
                item => item.CharacterId,
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => DescriptorSimilarity(
                    query,
                    _descriptors.GetOrAdd(
                        item.File,
                        _ => CreateCompactDescriptor(
                            LoadTemplate(item.File)))))
                .First())
            .OrderByDescending(item => DescriptorSimilarity(
                query,
                _descriptors.GetOrAdd(
                    item.File,
                    _ => CreateCompactDescriptor(
                        LoadTemplate(item.File)))))
            .ThenBy(item => item.CharacterId, StringComparer.Ordinal)
            .Take(_candidateLimit)
            .Select(item => item)
            .ToArray();
    }

    private (CharacterCardTemplateDefinition Definition, double Confidence)[]
        Rank(
            Mat searchImage,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            CharacterCardRecognitionOptions options) =>
        templates
            .Select(template => (
                Definition: template,
                Confidence: Match(
                    searchImage,
                    LoadTemplate(template.File, options),
                    options.ExcludeBottomRatio,
                    options.ExcludeSides,
                    options.BackRow)))
            .OrderByDescending(item => item.Confidence)
            // 取 top 5 而非 top 2：多变体加载后（银狼 3/4/5 费、开拓者
            // 记忆/欢愉），top2 可能都是同一角色模板，IsDecisive 需要
            // "不同角色"的 runner-up 分数做区分度判定（2026-08-07）。
            .Take(5)
            .ToArray();

    private bool IsDecisive(
        IReadOnlyList<(
            CharacterCardTemplateDefinition Definition,
            double Confidence)> ranked) =>
        ranked.Count > 0 &&
        ranked[0].Confidence >= ConfidenceThresholdFor(ranked[0].Definition) &&
        // 区分度 runner-up 必须取"不同角色"的最高分：多变体加载后
        //（银狼 3/4/5 费、开拓者记忆/欢愉），同一角色不同形态模板互相
        // 接近是预期（同一角色），不应作为区分度惩罚——否则银狼
        // 0.558 vs 同角色 0.538 分差 0.020 被浮点误差判失败（2026-08-07
        // 实测 Front#0 银狼 runnerupChar=character_05 即此因）。
        // 区分度门按角色：白厄/飞霄/开拓者等相似角色用 0.02，其他 0.04
        //（用户 2026-08-07：仅相似角色放宽，其他不降）。
        ranked[0].Confidence - (ranked.Skip(1)
            .FirstOrDefault(item =>
                item.Definition.CharacterId != ranked[0].Definition.CharacterId)
            .Confidence) >= LeadOverThresholdFor(ranked[0].Definition);

    private static float[] CreateCompactDescriptor(Mat source)
    {
        const int side = 24;
        using var reduced = new Mat();
        Cv2.Resize(
            source,
            reduced,
            new Size(side, side),
            interpolation: InterpolationFlags.Area);
        var pixels = new byte[checked(side * side * reduced.Channels())];
        Marshal.Copy(reduced.Data, pixels, 0, pixels.Length);
        var mean = pixels.Average(value => (double)value);
        var descriptor = new float[pixels.Length];
        var squaredNorm = 0d;
        for (var index = 0; index < pixels.Length; index++)
        {
            var centered = pixels[index] - mean;
            descriptor[index] = (float)centered;
            squaredNorm += centered * centered;
        }

        if (squaredNorm <= double.Epsilon)
        {
            return descriptor;
        }

        var inverseNorm = 1d / Math.Sqrt(squaredNorm);
        for (var index = 0; index < descriptor.Length; index++)
        {
            descriptor[index] = (float)(descriptor[index] * inverseNorm);
        }

        return descriptor;
    }

    private static double DescriptorSimilarity(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        var score = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            score += left[index] * right[index];
        }

        return score;
    }

    private Mat LoadTemplate(string path) =>
        _templates.GetOrAdd(
            path,
            static file =>
            {
                var bytes = File.ReadAllBytes(file);
                var template = Cv2.ImDecode(bytes, ImreadModes.Color);
                if (template.Empty())
                {
                    template.Dispose();
                    throw new InvalidDataException(
                        $"角色卡牌模板无法读取：{file}");
                }

                if (template.Width != StandardTemplateWidth ||
                    template.Height != StandardTemplateHeight)
                {
                    var normalized = new Mat();
                    Cv2.Resize(
                        template,
                        normalized,
                        new Size(
                            StandardTemplateWidth,
                            StandardTemplateHeight),
                        interpolation: InterpolationFlags.Area);
                    template.Dispose();
                    return normalized;
                }

                return template;
            });

    private Mat LoadTemplate(
        string path,
        CharacterCardRecognitionOptions options)
    {
        if (Math.Abs(options.HorizontalTemplateScale - 1) < 0.001 &&
            Math.Abs(options.VerticalTemplateScale - 1) < 0.001)
        {
            return LoadTemplate(path);
        }

        if (options.HorizontalTemplateScale is < 0.5 or > 1.5 ||
            options.VerticalTemplateScale is < 0.5 or > 1.5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Character-card template scale must stay within a safe range.");
        }

        var horizontalScalePermille = (int)Math.Round(
            options.HorizontalTemplateScale * 1000);
        var verticalScalePermille = (int)Math.Round(
            options.VerticalTemplateScale * 1000);
        return _scaledTemplates.GetOrAdd(
            (path, horizontalScalePermille, verticalScalePermille),
            key =>
            {
                var source = LoadTemplate(key.File);
                var scaled = new Mat();
                Cv2.Resize(
                    source,
                    scaled,
                    new Size(
                        Math.Max(1, (int)Math.Round(
                            StandardTemplateWidth *
                            key.HorizontalScalePermille / 1000d)),
                        Math.Max(1, (int)Math.Round(
                            StandardTemplateHeight *
                            key.VerticalScalePermille / 1000d))),
                    interpolation: InterpolationFlags.Area);
                return scaled;
            });
    }

    private static double Match(
        Mat search,
        Mat template,
        double excludeBottomRatio = 0,
        bool excludeSides = false,
        bool backRow = false)
    {
        if (template.Width > search.Width ||
            template.Height > search.Height)
        {
            return 0;
        }

        using var scores = new Mat();
        Cv2.MatchTemplate(
            search,
            template,
            scores,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(
            scores,
            out _,
            out var maximum,
            out _,
            out var maximumLocation);

        // detail 区（2026-08-07 回退到原值 5/5/15）：早期为救银狼改过
        // 45/20/32 和 18/8/32，导致 ExpandedShop 刻律德菈等槽位从
        // Recognized 变 Uncertain（0.2.837 基线通过）——detail 区裁剪
        // 影响立绘主体匹配，恢复原始范围。银狼/猎星人/打call由独立
        // 检测处理（DetectHunterStar/DetectCallEffect），不靠裁剪。
        var bottomExclude = Math.Max(
            15,
            (int)Math.Round(template.Height * excludeBottomRatio));
        var detailBounds = excludeSides
            ? new Rect(
                Math.Max(10, (int)Math.Round(template.Width * 0.15)),
                5,
                Math.Max(1, template.Width -
                    2 * Math.Max(10, (int)Math.Round(template.Width * 0.15)) - 5),
                Math.Max(1, template.Height - bottomExclude - 5))
            // 默认路径保持原版 detail 区（Width-25-5，2026-08-08 修复：
            // 改动默认宽度公式导致 character_60 等 conf 从 0.58 降到 0.54
            // 卡阈值——回归）。
            : new Rect(
                5,
                5,
                Math.Max(1, template.Width - 25 - 5),
                Math.Max(1, template.Height - bottomExclude - 5));
        using var templateDetail = new Mat(template, detailBounds);
        using var searchDetail = new Mat(
            search,
            new Rect(
                maximumLocation.X + detailBounds.X,
                maximumLocation.Y + detailBounds.Y,
                detailBounds.Width,
                detailBounds.Height));
        using var detailScore = new Mat();
        Cv2.MatchTemplate(
            searchDetail,
            templateDetail,
            detailScore,
            TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(detailScore, out _, out var detailMaximum, out _, out _);
        // 颜色相似度只在立绘主体区（detailBounds）计算——排除左上角
        // 特殊装备图标与底部星级区的颜色差异（这些是每帧可变的 UI，
        // 不该惩罚角色匹配；用户 2026-08-07 实机验证银狼 0.458 即此因）。
        using var alignedSearch = new Mat(
            search,
            new Rect(
                maximumLocation.X + detailBounds.X,
                maximumLocation.Y + detailBounds.Y,
                detailBounds.Width,
                detailBounds.Height));
        using var absoluteDifference = new Mat();
        Cv2.Absdiff(alignedSearch, templateDetail, absoluteDifference);
        var meanDifference = Cv2.Mean(absoluteDifference);
        var normalizedMeanDifference =
            (meanDifference.Val0 +
             meanDifference.Val1 +
             meanDifference.Val2) /
            (3d * byte.MaxValue);
        var absoluteColorSimilarity = 1 - normalizedMeanDifference;
        var colorSimilarityGrace = backRow
            ? BackRowColorSimilarityGrace
            : ColorSimilarityGrace;
        var colorMismatchPenalty = ColorMismatchPenaltyWeight *
            Math.Max(0, colorSimilarityGrace - absoluteColorSimilarity);
        return maximum * 0.55 + detailMaximum * 0.45 -
               colorMismatchPenalty;
    }

    private static Mat Normalize(CaptureFrame frame)
    {
        using var bgra = new Mat(
            frame.Height,
            frame.Width,
            MatType.CV_8UC4);
        Marshal.Copy(
            frame.BgraPixels,
            0,
            bgra.Data,
            frame.BgraPixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        if (frame.Width == OpenCvTemplateMatcher.ReferenceWidth &&
            frame.Height == OpenCvTemplateMatcher.ReferenceHeight)
        {
            return bgr;
        }

        var normalized = new Mat();
        Cv2.Resize(
            bgr,
            normalized,
            new Size(
                OpenCvTemplateMatcher.ReferenceWidth,
                OpenCvTemplateMatcher.ReferenceHeight),
            interpolation: InterpolationFlags.Area);
        bgr.Dispose();
        return normalized;
    }

    private static CharacterCardSlotRecognition Uncertain(
        int slotIndex,
        PixelRect slot,
        double visualStandardDeviation) =>
        new(
            slotIndex,
            slot,
            CharacterCardSlotState.Uncertain,
            null,
            null,
            0,
            0,
            visualStandardDeviation);
}
