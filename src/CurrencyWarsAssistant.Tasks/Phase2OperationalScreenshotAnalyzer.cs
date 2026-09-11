using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tasks;

/// <summary>
/// 2026-08-11 增量识别选择：非 null 时只识别 RecognizeFields 列出的字段，
/// 其余字段返回 Unknown（StateTracker 保留旧值）。仅用于画面静止的增量帧
/// ——画面没变则跳过字段的值不可能变，绝对安全；画面变化/2s 周期到/关键帧
/// 走全量（incremental=null）捕捉变化。
/// </summary>
public sealed record Phase2IncrementalSelection(
    IReadOnlySet<string> RecognizeFields,
    string? KnownPageId = null,
    IReadOnlySet<string>? FormationSlotKeys = null,
    long? FormationTransactionId = null,
    bool FormationSlotsAreAtomic = false,
    int? BackSlotCount = null)
{
    public bool ShouldRecognize(string field) =>
        RecognizeFields.Contains(field);

    public bool ShouldRecognizeFormationSlot(FormationZone zone, int slotIndex) =>
        FormationSlotKeys is null ||
        FormationSlotKeys.Contains(Phase2FormationSlotKey.Format(zone, slotIndex));
}

/// <summary>增量识别字段名常量。</summary>
public static class Phase2IncrementalFields
{
    public const string Health = "health";
    public const string StoreLevel = "store-level";
    public const string Population = "population";
    public const string Formation = "formation";
    public const string Synergy = "synergy";
    // 2026-08-18 增量覆盖扩展（B 提速补强，用户授权"保证正确性下可跳过部分读取"）：
    // 这 5 个 OCR 字段在增量帧本会全量重读（约 1.5s），但画面静止时值不变——
    // 接入"成功后跳过、失败才重试"（与 Health/StoreLevel 同机制），2s 全量兜底保底。
    // 注意：Node 不在此列——review 建议其消费面广（StateTracker 页面/战斗边界锚点），
    // 跳过致 Stale 化风险高，保持全量读取。
    public const string Difficulty = "difficulty";
    public const string Interest = "interest";
    public const string Spend = "spend";
    public const string Progress = "progress";
    public const string Tools = "tools";
}

public sealed partial class Phase2OperationalScreenshotAnalyzer(
    ICharacterCardRecognizer characterRecognizer,
    IReadOnlyList<CharacterCardTemplateDefinition> characterTemplates,
    IPhase2IconRecognizer iconRecognizer,
    IReadOnlyList<Phase2IconTemplateDefinition> iconTemplates,
    IOfflineOcr ocr,
    GameDataCatalog gameData,
    IOfflineOcr? numericOcr = null,
    PpOcrOfflineOcr? storeLevelOcr = null,
    GameDataNameMatcher? nameMatcher = null,
    IGamePageClassifier? pageClassifier = null,
    bool enableRobustFallback = true,
    IHorizontalSpecialUnitRecognizer? horizontalSpecialUnitRecognizer = null)
{
    private const int MaximumActionCandidatesToRead = 1;
    private const string BenchSpecialItemCategory = "bench-special-item";
    // 2026-08-12 live calibration: verified 004=0.630376 while the strongest
    // of 57 other unresolved bench crops was 0.398845 in this comparison mode.
    private const double BenchSpecialItemMinimumConfidence = 0.50;
    private const double BenchSpecialItemPossibleConfidence = 0.42;
    // D 步（2026-08-17）：备战席/后台"武装箱/聘用书"特殊单位。
    // catalog（LoadSpecialItemTemplates）里三档武装箱因与装备库同名被改写为
    // 装备 id（156 特权 / 157 简易 / 158 进阶），三档聘书保留 special_item_020/021/022。
    private static readonly string[] BenchSpecialItemTemplateIds =
    [
        "currency_wars_equipment_156", // 特权武装箱
        "currency_wars_equipment_157", // 简易武装箱
        "currency_wars_equipment_158", // 进阶武装箱
        "special_item_020",            // 3费聘用书
        "special_item_021",            // 4费聘用书
        "special_item_022",            // 5费聘用书
        "special_item_010",            // 冶金炉（2026-08-18 用户提供 BWiki 原图接入）
    ];
    // catalog 匹配 id →（记录/显示用 special_item id, 中文名）。武装箱在 catalog
    // 里是装备 id，但呈现层按 standardized/special_item/{id}.png 解析图标，
    // 故记录回特殊物品 id（004/005/006 = 简易/进阶/特权武装箱）。
    private static readonly IReadOnlyDictionary<string, (string RecordId, string DisplayName)>
        BenchSpecialItemCatalogNames = new Dictionary<string, (string, string)>(
            StringComparer.Ordinal)
        {
            ["currency_wars_equipment_156"] = ("special_item_006", "特权武装箱"),
            ["currency_wars_equipment_157"] = ("special_item_004", "简易武装箱"),
            ["currency_wars_equipment_158"] = ("special_item_005", "进阶武装箱"),
            ["special_item_020"] = ("special_item_020", "3费聘用书"),
            ["special_item_021"] = ("special_item_021", "4费聘用书"),
            ["special_item_022"] = ("special_item_022", "5费聘用书"),
            ["special_item_010"] = ("special_item_010", "冶金炉"),
        };

    private static (string RecordId, string DisplayName)? ResolveBenchSpecialItem(
        string catalogId)
    {
        // 按 id 长度降序匹配：长 id（如 currency_wars_equipment_157）先匹配，
        // 避免未来加入更短前缀变体（如 special_item_020）时发生歧义。
        foreach (var candidate in BenchSpecialItemCatalogNames.Keys
                     .OrderByDescending(key => key.Length))
        {
            // catalog 模板 id 可能带变体后缀（如 {id}__live），按前缀精确匹配。
            if (string.Equals(catalogId, candidate, StringComparison.Ordinal) ||
                catalogId.StartsWith(
                    candidate + "__",
                    StringComparison.Ordinal))
            {
                return BenchSpecialItemCatalogNames[candidate];
            }
        }

        return null;
    }
    private readonly bool _enableRobustFallback = enableRobustFallback;
    private readonly IHorizontalSpecialUnitRecognizer?
        _horizontalSpecialUnitRecognizer = horizontalSpecialUnitRecognizer;
    private static bool EnableTimingDiagnostics =>
        string.Equals(
            Environment.GetEnvironmentVariable("CURRENCY_WARS_PHASE2_TIMING"),
            "1",
            StringComparison.Ordinal);
    // 2026-08-19：unknown 帧 OCR fallback 判页节流（2s 一次）。
    // 真 unknown 帧跑完 5 区域 OCR 判页仍 unknown，白花 ~2.5s 是帧积压主因；
    // OCR fallback 保留在 2s 兜底节奏，确保"挑战结束"结算等兜底判页
    // 最迟 2s 内生效（页面切换帧会触发全量路径）。
    private DateTime _lastOcrFallbackAtUtc = DateTime.MinValue;
    private static readonly TimeSpan OcrFallbackThrottle = TimeSpan.FromSeconds(2);
    private static readonly IReadOnlyDictionary<string, string> KnownSpecialUnitIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Gemi狸"] = "special_unit_gemi_li",
            ["佩佩"] = "special_unit_peipei",
            ["姵姵"] = "special_unit_variant_peipei",
            ["叽米"] = "special_unit_jimi",
            ["狸狸"] = "special_unit_lili",
            ["狸小龙"] = "special_unit_li_xiaolong",
            ["狸小虎"] = "special_unit_li_xiaohu",
            // 2026-08-15 素材包补齐（除佩佩外的全部后台特殊单位牌面）：
            ["步狸人"] = "special_unit_tanuki_101",
            ["狸职狸狸"] = "special_unit_tanuki_102",
            ["狸财经狸"] = "special_unit_tanuki_103",
            ["普狸策"] = "special_unit_tanuki_104",
            ["幻太子"] = "special_unit_tanuki_105",
            ["尤狸安"] = "special_unit_tanuki_106",
            ["佛狸"] = "special_unit_tanuki_107",
            ["比狸比狸"] = "special_unit_tanuki_110",
            ["胡构狸"] = "special_unit_tanuki_113",
            ["金币大佬叽米"] = "special_unit_owlbert_901",
            ["星徽大佬叽米"] = "special_unit_owlbert_902",
            ["环保大佬叽米"] = "special_unit_owlbert_903"
        };
    private readonly IReadOnlyDictionary<string, string> _standingByCharacterId =
        gameData.CurrencyWarsCharacters.ToDictionary(
            item => item.Id,
            item => item.Position,
            StringComparer.Ordinal);
    // 打call特效检测开关（用户 2026-08-07）：仅当形成内有后台/备战席开拓者
    //（欢愉形态）时置 true，对每个槽位检测左右两侧应援棒（左冰蓝+右暖黄）。
    private bool _callEffectEnabled;
    private readonly IReadOnlyDictionary<string, InventoryItemKind>
        _inventoryKindById = BuildInventoryKindMap(iconTemplates);
    private readonly IReadOnlyList<Phase2IconTemplateDefinition>
        _benchSpecialItemTemplates = BuildBenchSpecialItemTemplates(iconTemplates);
    private readonly IOfflineOcr _numericOcr = numericOcr ?? ocr;
    private readonly IOfflineOcr _storeLevelOcr = storeLevelOcr ?? numericOcr ?? ocr;
    private readonly OpenCvUiDigitSequenceRecognizer _uiDigitRecognizer = new();
    private readonly Phase2NodeFormationFusion _nodeFormationFusion = new();
    private readonly GameDataNameMatcher _nameMatcher =
        nameMatcher ?? new GameDataNameMatcher();
    private readonly IReadOnlyList<NamedCatalogItem> _negativeAffixes =
        gameData.EnemyAffixes
            .Select(item => new NamedCatalogItem(item.Id, item.Name))
            .ToArray();
    private readonly IReadOnlySet<string> _negativeAffixIds =
        gameData.EnemyAffixes
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
    private readonly IReadOnlyList<NamedCatalogItem> _investmentEnvironments =
        gameData.InvestmentEnvironments
            .Select(item => new NamedCatalogItem(item.Id, item.Name))
            .ToArray();
    private readonly IReadOnlyDictionary<string, InvestmentEnvironmentData>
        _investmentEnvironmentById = gameData.InvestmentEnvironments.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
    private readonly IReadOnlyList<NamedCatalogItem> _investmentStrategies =
        gameData.InvestmentStrategies
            .Select(item => new NamedCatalogItem(item.Id, item.Name))
            .ToArray();
    private readonly IReadOnlyDictionary<string, InvestmentStrategyData>
        _investmentStrategyById = gameData.InvestmentStrategies.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
    private readonly IReadOnlyList<NamedCatalogItem> _synergies =
        gameData.CurrencyWarsCharacters
            .SelectMany(item => item.BondNames)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new NamedCatalogItem($"bond_{name}", name))
            .ToArray();

    public async Task<Phase2OperationalState> AnalyzeAsync(
        CaptureFrame frame,
        string pageId,
        string evidenceSourceId,
        RunSnapshot baseSnapshot,
        CancellationToken cancellationToken,
        Phase2IncrementalSelection? incremental = null,
        int? backSlotCount = null)
    {
        var frameOcrCache = new ConcurrentDictionary<
            NormalizedRect,
            Lazy<Task<IReadOnlyList<string>>>>();
        var page = await DetectPageFamilyAsync(
            frame,
            pageId,
            frameOcrCache,
            cancellationToken).ConfigureAwait(false);
        var evidence = new EvidenceReference(
            evidenceSourceId,
            "screenshot:full-frame",
            $"{frame.Width}x{frame.Height}",
            frame.CapturedAt);
        var state = new Phase2OperationalState
        {
            PageFamily = page,
            PageId = pageId,
            Diagnostics = page == Phase2PageFamily.Unknown
                ? [$"页面 {pageId} 不属于已确认的第二阶段采集页面。"]
                : []
        };

        if (page is Phase2PageFamily.Preparation or Phase2PageFamily.Supply)
        {
            return await AnalyzePreparationAsync(
                    frame,
                    state,
                    evidence,
                    baseSnapshot,
                    pageId,
                    cancellationToken,
                    incremental,
                    backSlotCount)
                .ConfigureAwait(false);
        }

        if (page == Phase2PageFamily.Battle)
        {
            return await AnalyzeBattleAsync(
                    frame,
                    state,
                    evidence,
                    baseSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (page == Phase2PageFamily.BattleSettlement)
        {
            return await AnalyzeSettlementAsync(
                    frame,
                    state,
                    evidence,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // The mode landing page has no per-run operational fields to collect.
        // Treating it like an unknown/transition page needlessly OCRs battle and
        // preparation regions and creates misleading partial-field warnings.
        if (page == Phase2PageFamily.Main)
        {
            return state;
        }

        // 2026-08-11：unknown 页不再做降级 OCR（原 AnalyzeUnknownRegionsAsync
        // 对 5 个区域全量 OCR，每 2s 周期一次耗时 2.6s，阻塞识别管线——
        // 用户在非备战/战斗界面打开或跳转时"识别管线阻塞"即此根因）。
        // PartialFields 仅被历史详情页展示引用，无识别/决策功能依赖，
        // 故 unknown 页直接返回状态，不识别任何区域。
        return state;
    }


    public Task<Phase2PageFamily> DetectPageFamilyAsync(
        CaptureFrame frame,
        string configuredPageId,
        CancellationToken cancellationToken) =>
        DetectPageFamilyAsync(
            frame,
            configuredPageId,
            new ConcurrentDictionary<
                NormalizedRect,
                Lazy<Task<IReadOnlyList<string>>>>(),
            cancellationToken);

    private async Task<Phase2PageFamily> DetectPageFamilyAsync(
        CaptureFrame frame,
        string configuredPageId,
        ConcurrentDictionary<NormalizedRect, Lazy<Task<IReadOnlyList<string>>>>
            frameOcrCache,
        CancellationToken cancellationToken)
    {
        var configured = MapPage(configuredPageId);
        if (configured != Phase2PageFamily.Unknown)
        {
            return configured;
        }

        // The composite screenshot analyzer has already run this same
        // classifier before passing the sentinel below. Avoid paying for an
        // identical full-frame template pass a second time on transition
        // frames; standalone callers that pass "unknown" retain the fallback.
        var classified = string.Equals(
                configuredPageId,
                "__classifier-miss__",
                StringComparison.Ordinal)
            ? null
            : pageClassifier?.Classify(frame);
        var classifiedPage = MapPage(classified?.PageId ?? string.Empty);
        if (classifiedPage != Phase2PageFamily.Unknown)
        {
            return classifiedPage;
        }

        if (pageClassifier is IGamePageClassifierDiagnostics diagnostics)
        {
            var inferred = Phase2PageDiagnosticFallback.TryInfer(
                diagnostics.LastDiagnostics);
            if (inferred is not null)
            {
                return inferred.Value.PageFamily;
            }
        }

        if (!ocr.IsAvailable)
        {
            return Phase2PageFamily.Unknown;
        }

        // 2026-08-19：unknown 帧 OCR fallback 判页节流（2s 一次）。
        // 真 unknown 帧（实测约 2/3）跑完 5 区域 OCR 判页仍 unknown，白花
        // ~2.5s，是"分析跟不上游戏进展"的积压主因；节流后这些帧快速返回。
        // 判页兜底仍按 2s 节奏运行，保证"挑战结束"结算等兜底最迟 2s 生效。
        if (DateTime.UtcNow - _lastOcrFallbackAtUtc < OcrFallbackThrottle)
        {
            return Phase2PageFamily.Unknown;
        }
        _lastOcrFallbackAtUtc = DateTime.UtcNow;

        // Probe the three discriminating regions first. Battle effects often
        // hide a template anchor for a frame; waiting for every preparation
        // and main-page OCR crop before checking the battle panel doubled the
        // latency of those otherwise usable frames.
        var primaryRegions = new[]
        {
            Phase2RecognitionRegions.SettlementTitle,
            Phase2RecognitionRegions.BattleDamageHeader,
            Phase2RecognitionRegions.PreparationNode,
            Phase2RecognitionRegions.BattleNodeIdentity,
            Phase2RecognitionRegions.BattleActionIndicator
        };
        var primaryEvidence = await Task.WhenAll(primaryRegions.Select(region =>
            ReadTextCachedAsync(
                frame,
                region,
                frameOcrCache,
                cancellationToken))).ConfigureAwait(false);

        var settlementText = primaryEvidence[0];
        var settlementJoined = string.Join(" ", settlementText);
        if (settlementJoined.Contains("挑战成功", StringComparison.Ordinal) ||
            settlementJoined.Contains("挑战失败", StringComparison.Ordinal) ||
            // 2026-08-11："挑战结束"是失败流程的结算过渡页标题（实测
            // challenge_ended_title 分类 0.839<0.840 临界 miss，靠 OCR 兜底
            // 判结算页，避免结算识别失败导致战斗未 finalize）。
            settlementJoined.Contains("挑战结束", StringComparison.Ordinal) ||
            settlementJoined.Contains("数据统计", StringComparison.Ordinal) ||
            settlementJoined.Contains("获得金币总览", StringComparison.Ordinal))
        {
            return Phase2PageFamily.BattleSettlement;
        }

        var battleText = primaryEvidence[1];
        var battleJoined = string.Join(" ", battleText);
        var compactBattleJoined = string.Concat(
            battleJoined.Where(character => !char.IsWhiteSpace(character)));
        var battleDamageLabels =
            battleJoined.Contains("伤害", StringComparison.Ordinal) &&
            battleJoined.Contains("羁绊", StringComparison.Ordinal);
        var damageValuesWithUnits = DamagePattern().Matches(battleJoined)
            .Count(match => match.Groups["unit"].Value.Length > 0);
        // The in-game diagnostic overlay can contain isolated words such as
        // "伤害" or "羁绊" while the actual page is the Currency Wars home
        // screen. Keep the battle result as a candidate until preparation and
        // main-page regions have been checked, and require combined evidence
        // instead of promoting one generic OCR token to a whole-page result.
        var hasBattleEvidence =
            (battleDamageLabels && damageValuesWithUnits >= 1) ||
            damageValuesWithUnits >= 2;
        var battleNodeValues = ParseNodeValues(primaryEvidence[3]);
        var hasBattleDamageHeader =
            compactBattleJoined.Contains("总伤害", StringComparison.Ordinal) ||
            compactBattleJoined.Contains("伤害", StringComparison.Ordinal);
        var actionJoined = string.Concat(
            primaryEvidence[4]
                .SelectMany(text => text)
                .Where(character => !char.IsWhiteSpace(character)));
        var hasBattleActionIndicator =
            actionJoined.Contains("我方行动", StringComparison.Ordinal) ||
            actionJoined.Contains("敌方行动", StringComparison.Ordinal);
        // At battle start every damage row is legitimately zero, so the old
        // unit-bearing-number requirement rejected an otherwise complete HUD.
        // Combine the battle-only top node position with the damage header;
        // neither signal is accepted alone and preparation/settlement use
        // different node regions.
        hasBattleEvidence |=
            battleNodeValues.Length == 1 &&
            (hasBattleDamageHeader || hasBattleActionIndicator) ||
            hasBattleDamageHeader && hasBattleActionIndicator;

        var preparationText = primaryEvidence[2];

        var preparationJoined = string.Join(" ", preparationText);
        if (preparationJoined.Contains("备战阶段", StringComparison.Ordinal) ||
            preparationJoined.Contains("前台区域", StringComparison.Ordinal) ||
            preparationJoined.Contains("后台区域", StringComparison.Ordinal) ||
            preparationJoined.Contains("购买经验", StringComparison.Ordinal))
        {
            return Phase2PageFamily.Preparation;
        }

        var secondaryRegions = new[]
        {
            new NormalizedRect(0.430, 0.190, 0.220, 0.180),
            Phase2RecognitionRegions.LevelAndExperience,
            Phase2RecognitionRegions.MainTitle,
            Phase2RecognitionRegions.MainStartAction
        };
        var secondaryEvidence = await Task.WhenAll(secondaryRegions.Select(region =>
            ReadTextCachedAsync(
                frame,
                region,
                frameOcrCache,
                cancellationToken))).ConfigureAwait(false);

        preparationText = preparationText
            .Concat(secondaryEvidence.Take(2).SelectMany(texts => texts))
            .ToArray();
        var expandedPreparationJoined = string.Join(" ", preparationText);
        if (expandedPreparationJoined.Contains("备战阶段", StringComparison.Ordinal) ||
            expandedPreparationJoined.Contains("前台区域", StringComparison.Ordinal) ||
            expandedPreparationJoined.Contains("后台区域", StringComparison.Ordinal) ||
            expandedPreparationJoined.Contains("购买经验", StringComparison.Ordinal))
        {
            return Phase2PageFamily.Preparation;
        }

        var mainText = secondaryEvidence
            .Skip(2)
            .SelectMany(texts => texts)
            .ToArray();

        var mainJoined = string.Join(" ", mainText);
        if (mainJoined.Contains("货币战争", StringComparison.Ordinal) &&
            (mainJoined.Contains("开始", StringComparison.Ordinal) ||
             mainJoined.Contains("创业指南", StringComparison.Ordinal)))
        {
            return Phase2PageFamily.Main;
        }

        // Character cards at the preparation board/bench positions are a
        // stronger page-family signal than generic damage text or a visually
        // similar action-row candidate.  Resolve this before accepting battle
        // evidence so the left-side synergy list cannot turn a preparation
        // frame into a false battle page.
        var boardSlots = RecognizeCharactersSafely(
            frame,
            characterTemplates,
            Phase2RecognitionRegions.PreparationCharacterSlots1920);
        var benchSlots = RecognizeCharactersSafely(
            frame,
            characterTemplates,
            Phase2RecognitionRegions.BenchCharacterSlots1920);
        if (boardSlots.Concat(benchSlots).Any(item =>
                item.State == CharacterCardSlotState.Recognized))
        {
            return Phase2PageFamily.Preparation;
        }

        if (hasBattleEvidence)
        {
            return Phase2PageFamily.Battle;
        }

        // Older 16:9 battle layouts place the node label outside the current
        // compact OCR crop, while the colored remaining-action row is still
        // present on the left timeline.  Use its existing visual locator only
        // after settlement/main/preparation evidence has been excluded.  The
        // locator is deliberately not sufficient before the preparation-card
        // check because the preparation synergy list can look similar.
        if (Phase2ActionIndicatorLocator.Locate(frame, iconTemplates) is not null)
        {
            var damagePanelText = await ReadTextCachedAsync(
                    frame,
                    Phase2RecognitionRegions.BattleDamagePanel,
                    frameOcrCache,
                    cancellationToken)
                .ConfigureAwait(false);
            var damagePanelJoined = string.Join(" ", damagePanelText);
            var readableDamageRows = DamagePattern().Matches(damagePanelJoined)
                .Count(match => match.Groups["unit"].Value.Length > 0);
            if (readableDamageRows >= 1)
            {
                return Phase2PageFamily.Battle;
            }
        }

        return Phase2PageFamily.Unknown;
    }

    private async Task<Phase2OperationalState> AnalyzePreparationAsync(
        CaptureFrame frame,
        Phase2OperationalState state,
        EvidenceReference evidence,
        RunSnapshot baseSnapshot,
        string configuredPageId,
        CancellationToken cancellationToken,
        Phase2IncrementalSelection? incremental = null,
        int? backSlotCount = null)
    {
        var totalStarted = Stopwatch.GetTimestamp();
        var isSupplyPage = string.Equals(
            configuredPageId,
            "reward_shop",
            StringComparison.Ordinal);
        var nodeTask = isSupplyPage
            ? SkipTimed(Observation<string>.Unknown(
                "商店覆盖层不采样备战节点；沿用进入商店前的确认值。",
                [evidence],
                frame.CapturedAt))
            : Task.Run(() => MeasureAsync(() => ReadNodeAsync(
                frame,
                Phase2RecognitionRegions.PreparationNodeValue,
                "preparation-node",
                evidence,
                cancellationToken)));
        var difficultyTask = ShouldSkipIncrementField(
                incremental, Phase2IncrementalFields.Difficulty)
            ? SkipTimed(Observation<int>.Unknown(
                "增量帧：难度已确认且画面静止，保留上次值。",
                [evidence],
                frame.CapturedAt))
            : Task.Run(() => MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
                frame,
                Phase2RecognitionRegions.PreparationDifficultyValue,
                Phase2RecognitionRegions.PreparationDifficultyDigits,
                100,
                999,
                "enemy-difficulty",
                evidence,
                cancellationToken,
                UiDigitForegroundStyle.BrightOnDark)));
        var interestTask = ShouldSkipIncrementField(
                incremental, Phase2IncrementalFields.Interest)
            ? SkipTimed(Observation<int>.Unknown(
                "增量帧：利息已确认且画面静止，保留上次值。",
                [evidence],
                frame.CapturedAt))
            : Task.Run(() => MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
                frame,
                Phase2RecognitionRegions.Interest,
                Phase2RecognitionRegions.InterestValue,
                0,
                5,
                "interest",
                evidence,
                cancellationToken,
                UiDigitForegroundStyle.GoldSaturated,
                UiDigitForegroundStyle.DarkOnLight)));
        var cumulativeSpendTask = ShouldSkipIncrementField(
                incremental, Phase2IncrementalFields.Spend)
            ? SkipTimed(Observation<int>.Unknown(
                "增量帧：累计花费已确认且画面静止，保留上次值。",
                [evidence],
                frame.CapturedAt))
            : Task.Run(() => MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
                frame,
                Phase2RecognitionRegions.CumulativeSpend,
                Phase2RecognitionRegions.CumulativeSpendValue,
                0,
                100,
                "cumulative-spend",
                evidence,
                cancellationToken,
                UiDigitForegroundStyle.BrightOnDark)));
        var progressTask = isSupplyPage
            ? SkipTimed(Observation<PlayerProgressState>.Unknown(
                "商店覆盖层不采样玩家等级与经验；沿用进入商店前的确认值。",
                [evidence],
                frame.CapturedAt))
            : ShouldSkipIncrementField(
                incremental, Phase2IncrementalFields.Progress)
                ? SkipTimed(Observation<PlayerProgressState>.Unknown(
                    "增量帧：玩家进度已确认且画面静止，保留上次值。",
                    [evidence],
                    frame.CapturedAt))
                : Task.Run(() => MeasureAsync(() => ReadProgressAsync(
                    frame,
                    evidence,
                    cancellationToken)));
        var toolsTask = ShouldSkipIncrementField(
                incremental, Phase2IncrementalFields.Tools)
            ? SkipTimed(Observation<int>.Unknown(
                "增量帧：拆除工具数已确认且画面静止，保留上次值。",
                [evidence],
                frame.CapturedAt))
            : MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
                frame,
                Phase2RecognitionRegions.DismantleToolCountValue,
                Phase2RecognitionRegions.DismantleToolCountValue,
                0,
                99,
                "dismantle-tools",
                evidence,
                cancellationToken,
                UiDigitForegroundStyle.BrightOnDark));
        // 商店等级（备战页左下角"购买经验 Lv.N"，用户 2026-08-06 要求补录）。
        // 此前该识别只存在于即时识别分析器（SituationScreenshotAnalysis），
        // 实时记录流分析器无此代码 → 节点历史/报告商店 Lv 恒"未记录"。
        // 2026-08-08：区域已重标定（旧 (0.18,0.83) 错位），且单数字 OCR 对
        // 小裁剪识别率低——改用文本 OCR + LevelPattern（读 "Lv.7" 整串）。
        // 2026-08-11 增量：商店等级已 Known 时跳过识别（画面静止值不变，
        // StateTracker 保留旧值；2s 全量兜底捕捉变化）。
        var storeLevelTask = incremental is not null &&
            !incremental.ShouldRecognize(Phase2IncrementalFields.StoreLevel)
            ? SkipTimed(Observation<int>.Unknown(
                "增量帧跳过商店等级识别（画面静止，保留上次结果）。",
                [evidence],
                frame.CapturedAt))
            : MeasureAsync(() => ReadStoreLevelAsync(
                frame,
                evidence,
                cancellationToken));
        // 备战页玩家血量（顶部中央偏右，用户 2026-08-07：此前未接入识别）
        // 2026-08-08 修复：旧调用把 digitRegion 传成血量图标区（PreparationHealth，
        // 心形图标非数字）→ 模板兜底必失败。统一用数字区。
        // 2026-08-11：商店页（reward_shop）跳过血量识别（商店 UI 遮挡，
        // 用户要求：商店页不识别阵容/血量/装备）。返回 Unknown 空结果，
        // 血量 Unknown 时 ObserveHealth 不写入确认字典，旧已知值保留。
        // 2026-08-11 增量：血量已 Known 时跳过识别（画面静止值不变）。
        var healthTask = isSupplyPage ||
            (incremental is not null &&
             !incremental.ShouldRecognize(Phase2IncrementalFields.Health))
            ? SkipTimed(Observation<int>.Unknown(
                isSupplyPage
                    ? "商店页不识别血量（商店 UI 遮挡）。"
                    : "增量帧跳过血量识别（画面静止，保留上次结果）。",
                [evidence],
                frame.CapturedAt))
            : MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
                frame,
                Phase2RecognitionRegions.PreparationHealthValue,
                Phase2RecognitionRegions.PreparationHealthValue,
                1,
                1000,
                "health",
                evidence,
                cancellationToken,
                UiDigitForegroundStyle.BrightOnDark));

        // 人口 N/N（备战页中上区域，如 8/8；2026-08-08 新增独立字段——
        // 旧 ReadProgressAsync 只在"等级"验证里顺带解析容量，无独立输出，
        // 且等级失败时人口一起丢）。独立于等级/经验读取。
        // 2026-08-11 增量：人口已 Known 时跳过识别。
        var populationTask = incremental is not null &&
            !incremental.ShouldRecognize(Phase2IncrementalFields.Population)
            ? SkipTimed(Observation<int>.Unknown(
                "增量帧跳过人口识别（画面静止，保留上次结果）。",
                [evidence],
                frame.CapturedAt))
            : MeasureAsync(() => ReadPopulationAsync(
                frame,
                evidence,
                cancellationToken));

        // Hybrid supplies its cached topology, so the hot path does not run the
        // expensive detector every frame. Direct/legacy full analysis has no
        // scheduler metadata and must detect once for this analysis instead of
        // silently assuming six slots (which loses valid 7-9 slot formations).
        // 槽数判定（用户 2026-08-15 逻辑）：后台槽位 = 6 + (人口 - 商店等级)，
        // n = 人口 - 商店等级 ∈ {0,1,2,3}。人口/商店等级是备战页常驻 UI，
        // 比图像检测稳定；两者都 Known 时用派生值，否则回退检测器。
        var derivedBackSlots = DeriveBackSlotCount(
            (await populationTask.ConfigureAwait(false)).Value,
            (await storeLevelTask.ConfigureAwait(false)).Value);
        // 后台格数：优先按用户规则 后台格数 = 6 + (人口 − 商店Lv)（DeriveBackSlotCount），
        // 只可能是 6/7/8/9。推导所需人口/商店Lv 任一 Unknown/不在0..3 时推导不出，
        // 回退：上次留存的格数 → 列方差检测(DetectBackSlotCount) 兜底；始终≥6。
        //（2026-08-20 曾尝试删除列方差 fallback，实测人口 Unknown 帧推导不出→全部
        //  后排 unknown 更差，故保留兜底；目标=修人口识别让 Default 公式尽量接管。）
        backSlotCount = Math.Clamp(
            backSlotCount ??
            derivedBackSlots ??
            incremental?.BackSlotCount ??
            Phase2SlotDetector.DetectBackSlotCount(frame),
            6,
            9);
        var boardReferenceSlots = string.Equals(
            configuredPageId,
            "reward_shop",
            StringComparison.Ordinal)
            ? Phase2RecognitionRegions.RewardShopCharacterSlots1920
            : Phase2RecognitionRegions.PreparationCharacterSlots1920
                .Take(4)
                .Concat(Phase2RecognitionRegions.BackCharacterSlots1920(backSlotCount.Value))
                .ToArray();

        // Character matching is independent from named-content/icon OCR. Run
        // it on a separate worker so a preparation frame does not pay both
        // CPU-heavy passes serially on high-core desktop machines.
        var formationTask = Task.Run(() =>
        {
            var formationStarted = Stopwatch.GetTimestamp();
            // 2026-08-11 增量：阵容已 Known 时跳过识别（画面静止值不变，
            // StateTracker MergeFormationObservations 保留旧阵容）。
            if (incremental is not null &&
                !incremental.ShouldRecognize(Phase2IncrementalFields.Formation))
            {
                return (
                    Formation: Observation<
                        IReadOnlyList<FormationCharacterState>>.Unknown(
                        "增量帧跳过阵容识别（画面静止，保留上次结果）。",
                        [evidence],
                        frame.CapturedAt),
                    Pending: new List<PendingIconObservation>(),
                    BenchSpecialItems: new BenchSpecialItemObservation(
                        Observation<IReadOnlyList<string>>.Unknown(
                            "增量帧跳过备战席特殊物品识别。",
                            [evidence],
                            frame.CapturedAt),
                        new HashSet<int>(),
                        WasEvaluated: false),
                    SlotObservations: Array.Empty<Phase2FormationSlotObservation>(),
                    Elapsed: Stopwatch.GetElapsedTime(formationStarted));
            }

            var isRewardShopPage = string.Equals(
                configuredPageId,
                "reward_shop",
                StringComparison.Ordinal);
            // 应援(打call)识别已按用户要求全删（2026-08-21）：此前前置应援检测
            // + 欢愉门控重识别会因"能量地块亮蓝"误报而覆盖正确角色（阿格莱雅/
            // 星期日被判 unknown）。cheeredBoardIndices 恒空，角色一律走标准路径。
            var cheeredBoardIndices = new HashSet<int>();
            var boardOptions = isRewardShopPage
                ? CharacterCardRecognitionOptions.RewardShopCompact
                : CharacterCardRecognitionOptions.Standard;
            var requestedFormationSlots = incremental?.FormationSlotKeys;
            var selectedBoardIndices = Enumerable.Range(0, boardReferenceSlots.Count)
                .Where(index => requestedFormationSlots is null ||
                    requestedFormationSlots.Contains(Phase2FormationSlotKey.Format(
                        index < 4 ? FormationZone.Front : FormationZone.Back,
                        index)))
                .ToArray();
            var selectedBenchIndices = Enumerable.Range(
                    0,
                    Phase2RecognitionRegions.BenchCharacterSlots1920.Count)
                .Where(index => requestedFormationSlots is null ||
                    requestedFormationSlots.Contains(Phase2FormationSlotKey.Format(
                        FormationZone.Bench,
                        index)))
                .ToArray();
            // 2026-08-11：商店页（reward_shop）跳过场上阵容识别（商店 UI
            // 遮挡）；备战席（benchSlots）保留正常识别（用户要求）。装备
            // 识别挂在场上角色槽位循环里，随场上阵容一并跳过。
            var boardSlots = isRewardShopPage
                ? Array.Empty<CharacterCardSlotRecognition>()
                : RecognizeBoardWithBackRowPerspective(
                    frame,
                    characterTemplates,
                    selectedBoardIndices,
                    boardReferenceSlots,
                    boardOptions,
                    cheeredBoardIndices,
                    requestedFormationSlots,
                    backSlotCount ?? 9);
            var benchSlots = RecognizeCharactersSafely(
                frame,
                characterTemplates,
                selectedBenchIndices
                    .Select(index =>
                        Phase2RecognitionRegions.BenchCharacterSlots1920[index])
                    .ToArray(),
                starBand: StarBand.BenchRight,
                absoluteSlotIndices: requestedFormationSlots is null
                    ? null
                    : selectedBenchIndices);
            var localPending = new List<PendingIconObservation>();
            var benchSpecialItems = ObserveBenchSpecialItems(
                frame,
                benchSlots,
                evidence,
                localPending);
            // A confirmed item is not a character. Remove that slot from the
            // formation projection; its identity and position are carried by
            // SpecialItemIds plus non-decision PendingIcon evidence instead.
            var projectedBenchSlots = benchSlots
                .Select(slot => benchSpecialItems.RecognizedSlotIndices.Contains(
                        slot.SlotIndex)
                    ? slot with
                    {
                        State = CharacterCardSlotState.Empty,
                        CharacterId = null,
                        DisplayName = null,
                        RunnerUpCharacterId = null,
                        RunnerUpDisplayName = null,
                        MatchedTemplateId = null
                    }
                    : slot)
                .ToArray();
            var slotObservations = BuildFormationSlotObservations(
                boardSlots,
                projectedBenchSlots);
            var recognizedFormation = ObserveFormation(
                frame,
                boardSlots,
                projectedBenchSlots,
                evidence,
                frame.CapturedAt,
                localPending,
                string.Equals(
                    configuredPageId,
                    "reward_shop",
                    StringComparison.Ordinal),
                backSlotCount ?? 6);
            // 用户方案（2026-08-18 确认）：佩佩/狸猫等特殊单位只可能出现在后台，
            // 因此【不】用横向识别器在页面右上角(340,295)等位置"全图找佩佩"——
            // 该处其实是未适配的命途/阿哈单位，曾把其误判成佩佩(conf 0.99)并
            // 渲染进备战席。佩佩/狸猫统一由后台阵容识别管线(Back 槽 CharacterCard)
            // 识别；此处不再追加横向佩佩成员。
            return (
                Formation: recognizedFormation,
                Pending: localPending,
                BenchSpecialItems: benchSpecialItems,
                SlotObservations: slotObservations,
                Elapsed: Stopwatch.GetElapsedTime(formationStarted));
        }, cancellationToken);

        // Node OCR runs concurrently with the other preparation readers, but
        // its result must be observed before planning stable-content scans.
        // Otherwise a generic preparation page discovers the new node only
        // after this frame has already skipped the strategy HUD.
        var nodeResult = await nodeTask.ConfigureAwait(false);
        // 节点号读取（页面/数字解耦，用户方案）：
        // 备战页 1-1~3-9 的页面结构与 UI 完全一致，节点差异只在顶部
        // "备战阶段 X-X" 的数字。因此不按节点做模板，而是：
        //   1. 页面状态 = 分类器（preparation_generic 或任意 preparation_*）
        //      确认这是备战页；
        //   2. 节点数字 = PaddleOCR 读"备战阶段 X-X"整行（大区域，带
        //      "备战阶段"上下文，1/9 区分远强于小区域数字模板）；
        //   3. 自研数字识别器（小区域）仅在后备。
        // 实测：旧方案按节点做模板，1-3/1-4 备战帧会被 1-2 模板误匹配
        // （"备战阶段"标题相似度 0.914>0.9），污染节点历史。
        var nodeObservation = isSupplyPage
            ? nodeResult.Value
            : await ResolvePreparationNodeAsync(
                frame,
                configuredPageId,
                nodeResult.Value,
                evidence,
                cancellationToken).ConfigureAwait(false);
        if (state.PageFamily == Phase2PageFamily.Preparation)
        {
            ObservePreparationNode(baseSnapshot.RunId, nodeObservation);
        }

        var formationNodeSignature = isSupplyPage
            ? (ulong?)null
            : CreateStableRegionSignature(
                frame,
                Phase2RecognitionRegions.PreparationNodeValue);
        var formationFallbackNodeId =
            incremental?.FormationSlotKeys is { Count: > 0 } &&
            nodeObservation.Status != ObservationStatus.Known
                ? LastStablePreparationNodeId(baseSnapshot.RunId)
                : null;

        var namedContentStarted = Stopwatch.GetTimestamp();
        var stablePlan = isSupplyPage
            ? ReuseStableRecognitionsForCoveredOverlay(baseSnapshot.RunId)
            : PlanStableRecognitions(frame, baseSnapshot.RunId);
        var affixPending = new List<PendingIconObservation>();
        var environmentPending = new List<PendingIconObservation>();
        var strategyPending = new List<PendingIconObservation>();
        var synergyPending = new List<PendingIconObservation>();
        var affixTask = stablePlan.RecognizeNegativeAffixes
            ? ObserveNamedContentAsync(
                frame,
                "negative-affix",
                Phase2NamedContentKind.NegativeAffix,
                PendingIconCategory.NegativeAffix,
                Phase2RecognitionRegions.NegativeAffixSlots,
                Phase2RecognitionRegions.NegativeAffixTextSlots,
                _negativeAffixes,
                evidence,
                affixPending,
                cancellationToken)
            : Task.FromResult(stablePlan.CachedAffixContent);
        var environmentTask = stablePlan.RecognizeEnvironment
            ? ObserveNamedContentAsync(
                frame,
                "investment-environment",
                Phase2NamedContentKind.InvestmentEnvironment,
                PendingIconCategory.InvestmentEnvironment,
                [Phase2RecognitionRegions.InvestmentIconSlots[0]],
                [Phase2RecognitionRegions.InvestmentTextSlots[0]],
                _investmentEnvironments,
                evidence,
                environmentPending,
                cancellationToken)
            : Task.FromResult(stablePlan.CachedEnvironmentContent);
        var strategyTask = stablePlan.RecognizeStrategies
            ? ObserveNamedContentAsync(
                frame,
                "investment-strategy",
                Phase2NamedContentKind.InvestmentStrategy,
                PendingIconCategory.InvestmentStrategy,
                Phase2RecognitionRegions.InvestmentIconSlots.Skip(1).ToArray(),
                Phase2RecognitionRegions.InvestmentTextSlots.Skip(1).ToArray(),
                _investmentStrategies,
                evidence,
                strategyPending,
                cancellationToken)
            : Task.FromResult(stablePlan.CachedStrategyContent);
        // 2026-08-20 用户拍板：阵容识别已足够稳定，羁绊只从阵容推算
        //（ComputeSynergiesFromFormation，下方 MergeFormationAuthoritativeSynergies 以
        //  阵容为权威）。**禁用羁绊的图标/文字 OCR 识别**：synergyTask 恒空，管线不跑
        // 这一部分的 OCR（避免 PpOcr/窗台拖慢且“无阵容只有羁绊意义不大”）。代码保留不删。
        var synergyTask = Task.FromResult<IReadOnlyList<Phase2NamedContentRecognition>>(
            Array.Empty<Phase2NamedContentRecognition>());
        //（原实现：羁绊已知时跳过 / 否则 ObserveNamedContentAsync 读羂绊图标+文字——已按用户
        //   指示禁用，保留供参考/恢复。）
        await Task.WhenAll(
                affixTask,
                environmentTask,
                strategyTask,
                synergyTask)
            .ConfigureAwait(false);
        var namedContentElapsed = Stopwatch.GetElapsedTime(namedContentStarted);
        var affixContent = await affixTask.ConfigureAwait(false);
        var environmentContent = await environmentTask.ConfigureAwait(false);
        var strategyContent = await strategyTask.ConfigureAwait(false);
        var synergyContent = await synergyTask.ConfigureAwait(false);
        var formationResult = await formationTask.ConfigureAwait(false);
        var formation = formationResult.Formation;
        var formationElapsed = formationResult.Elapsed;
        var pending = formationResult.Pending;
        var benchSpecialItems = formationResult.BenchSpecialItems;
        var formationSlotObservations = formationResult.SlotObservations;
        var formationObservationsArePartial =
            isSupplyPage ||
            incremental?.FormationSlotKeys is { Count: > 0 };
        pending.AddRange(affixPending);
        pending.AddRange(environmentPending);
        pending.AddRange(strategyPending);
        pending.AddRange(synergyPending);
        var currentAffixes = stablePlan.RecognizeNegativeAffixes
            ? ToListObservation(
                affixContent,
                "negative-affix",
                frame.CapturedAt)
            : stablePlan.CachedAffixes;
        var currentEnvironment = stablePlan.RecognizeEnvironment
            ? ToSingleObservation(
                environmentContent,
                "investment-environment",
                frame.CapturedAt)
            : stablePlan.CachedEnvironment;
        var currentStrategies = stablePlan.RecognizeStrategies
            ? ToListObservation(
                strategyContent,
                "investment-strategy",
                frame.CapturedAt)
            : stablePlan.CachedStrategies;
        var stable = CommitStableRecognitions(
            baseSnapshot.RunId,
            stablePlan,
            affixContent,
            currentAffixes,
            environmentContent,
            currentEnvironment,
            strategyContent,
            currentStrategies);
        affixContent = stable.AffixContent;
        environmentContent = stable.EnvironmentContent;
        strategyContent = stable.StrategyContent;
        var confirmedAffixSlots = affixContent
            .Where(item =>
                item.Status == ObservationStatus.Known &&
                item.ObjectId is not null)
            .Select(item => item.SlotKey)
            .ToHashSet(StringComparer.Ordinal);
        pending.RemoveAll(item =>
            item.Category == PendingIconCategory.NegativeAffix &&
            confirmedAffixSlots.Contains(item.SlotKey));
        var affixes = stable.Affixes;
        var environment = stable.Environment;
        var strategies = stable.Strategies;
        ApplySpecialUnitContext(environment, strategies, pending);
        formation = _nodeFormationFusion.Merge(
            baseSnapshot.RunId,
            nodeObservation,
            formation,
            formationSlotObservations,
            incremental?.FormationSlotsAreAtomic == true,
            formationObservationsArePartial,
            formationFallbackNodeId,
            formationNodeSignature);
        formation = ReconcileCallEffectFromCompleteFormation(
            frame,
            formation);
        var synergies = ToSynergyObservation(
            synergyContent,
            frame.CapturedAt);
        // 前台/后台阵容是羁绊权威来源；右侧列表仅在阵容完全无法推算时后备。
        // 装备或特殊单位提供的额外羁绊必须由对应语义模型计算，不能由面板 OCR 猜测。
        var computedSynergies = ComputeSynergiesFromFormation(
            formation,
            evidence,
            frame.CapturedAt);
        synergies = MergeFormationAuthoritativeSynergies(synergies, computedSynergies);

        var equipmentStarted = Stopwatch.GetTimestamp();
        // 2026-08-11 增量：库存物品（扳手/令牌等）跳过识别（画面静止）。
        var inventory = incremental is not null
            ? new InventoryObservation(
                Observation<IReadOnlyList<string>>.Unknown(
                    "增量帧跳过库存识别（画面静止，保留上次结果）。",
                    [evidence],
                    frame.CapturedAt),
                Observation<IReadOnlyList<string>>.Unknown(
                    "增量帧跳过库存识别（画面静止，保留上次结果）。",
                    [evidence],
                    frame.CapturedAt),
                Observation<IReadOnlyList<InventorySlotState>>.Unknown(
                    "增量帧跳过库存识别（画面静止，保留上次结果）。",
                    [evidence],
                    frame.CapturedAt))
            : await ObserveInventory(
                frame, evidence, pending, cancellationToken).ConfigureAwait(false);
        var specialItemIds = MergeSpecialItemObservations(
            inventory.SpecialItemIds,
            benchSpecialItems,
            frame.CapturedAt);
        var equipmentElapsed = Stopwatch.GetElapsedTime(equipmentStarted);
        var difficultyResult = await difficultyTask.ConfigureAwait(false);
        var interestResult = await interestTask.ConfigureAwait(false);
        var cumulativeSpendResult = await cumulativeSpendTask.ConfigureAwait(false);
        var progressResult = await progressTask.ConfigureAwait(false);
        var populationResult = await populationTask.ConfigureAwait(false);
        var toolsResult = await toolsTask.ConfigureAwait(false);
        var storeLevelResult = await storeLevelTask.ConfigureAwait(false);
        var healthResult = await healthTask.ConfigureAwait(false);
        var node = nodeObservation;
        var difficulty = difficultyResult.Value;
        var interest = interestResult.Value;
        var cumulativeSpend = cumulativeSpendResult.Value;
        var progress = progressResult.Value;
        var population = populationResult.Value;
        var tools = toolsResult.Value;
        var storeLevel = storeLevelResult.Value;
        var health = healthResult.Value;
        var diagnostics = StableRecognitionDiagnostics(stable)
            .Append(baseSnapshot.Economy.Status == ObservationStatus.Known
                ? $"金币沿用现有识别结果：{baseSnapshot.Economy.Value}。"
                : "金币现有识别结果不确定；未将其猜成确定值。")
            .ToList();
        if (EnableTimingDiagnostics)
        {
            diagnostics.Add(
                $"perf:preparation node={nodeResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"difficulty={difficultyResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"interest={interestResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"spend={cumulativeSpendResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"progress={progressResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"tools={toolsResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"formation={formationElapsed.TotalMilliseconds:F1}ms; " +
                $"named={namedContentElapsed.TotalMilliseconds:F1}ms; " +
                $"equipment={equipmentElapsed.TotalMilliseconds:F1}ms; " +
                $"total={Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds:F1}ms");
        }

        return state with
        {
            NodeId = node,
            EnemyDifficulty = difficulty,
            Interest = interest,
            CumulativeSpend = cumulativeSpend,
            PlayerProgress = progress,
            Population = populationResult.Value,
            StoreLevel = storeLevel,
            Health = health,
            Formation = formation,
            FormationSlotObservations = formationSlotObservations,
            FormationSlotObservationsArePartial = formationObservationsArePartial,
            FormationSlotObservationsAreAtomic =
                incremental?.FormationSlotsAreAtomic == true,
            FormationSlotTransactionId = incremental?.FormationTransactionId,
            ActiveSynergies = synergies,
            DismantleToolCount = tools,
            SimpleEquipmentIds = inventory.SimpleEquipmentIds,
            SpecialItemIds = specialItemIds,
            InventorySlots = inventory.Slots,
            NegativeAffixIds = affixes,
            InvestmentEnvironmentId = environment,
            InvestmentStrategyIds = strategies,
            NamedContent = affixContent
                .Concat(environmentContent)
                .Concat(strategyContent)
                .Concat(synergyContent)
                .ToArray(),
            PendingIcons = pending,
            Diagnostics = diagnostics
        };
    }

    private static IEnumerable<string> StableRecognitionDiagnostics(
        StableRecognitionResult stable)
    {
        if (stable.ReusedAffixes)
        {
            yield return "敌人负面词条身份沿用本局开局证据；当前帧未重复识别。";
        }

        if (stable.ReusedEnvironment)
        {
            yield return "投资环境沿用本局已确认结果；当前帧未重复识别。";
        }

        if (stable.ReusedStrategies)
        {
            yield return "投资策略集合没有触发新增事件；当前帧未重复识别。";
        }
    }

    private void ApplySpecialUnitContext(
        Observation<string> environment,
        Observation<IReadOnlyList<string>> strategies,
        IList<PendingIconObservation> pending)
    {
        var strategyUnits = (strategies.Value ?? [])
            .Select(id => _investmentStrategyById.GetValueOrDefault(id))
            .Where(item => item is not null)
            .SelectMany(item => TriggeredSpecialUnits(item!));
        var environmentUnits = environment.Value is not null &&
                               _investmentEnvironmentById.TryGetValue(
                                   environment.Value,
                                   out var environmentData)
            ? TriggeredSpecialUnits(environmentData)
            : [];
        var triggered = strategyUnits
            .Concat(environmentUnits)
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (triggered.Length == 0)
        {
            return;
        }

        for (var index = 0; index < pending.Count; index++)
        {
            var item = pending[index];
            if (!item.SlotKey.StartsWith("formation-Back-", StringComparison.Ordinal))
            {
                continue;
            }

            var candidates = (item.CandidateTemplateIds ?? [])
                .Concat(triggered.Select(unit => unit.Id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var fields = new Dictionary<string, string>(
                item.RecognizedFields ??
                new Dictionary<string, string>(),
                StringComparer.Ordinal)
            {
                ["sourceType"] = "special-unit",
                ["triggeredSpecialUnits"] = string.Join(",", triggered.Select(
                    unit => $"{unit.Id}:{unit.Name}"))
            };
            pending[index] = item with
            {
                CandidateTemplateIds = candidates,
                RecognizedFields = fields,
                Status = "special-unit-template-pending"
            };
        }
    }

    internal static IEnumerable<(string Id, string Name)> TriggeredSpecialUnits(
        InvestmentStrategyData strategy)
        => TriggeredSpecialUnits(strategy.Id, strategy.Effect);

    internal static IEnumerable<(string Id, string Name)> TriggeredSpecialUnits(
        InvestmentEnvironmentData environment)
        => TriggeredSpecialUnits(environment.Id, environment.Effect);

    private static IEnumerable<(string Id, string Name)> TriggeredSpecialUnits(
        string triggerId,
        string effect)
    {
        var bracketNames = Regex.Matches(effect, @"【(?<name>[^】]+)】")
            .Select(match => match.Groups["name"].Value.Trim());
        var mentionedKnownNames = KnownSpecialUnitIds.Keys
            .Where(name => effect.Contains(name, StringComparison.Ordinal));
        var names = bracketNames
            .Concat(mentionedKnownNames)
            .Where(name => name.Length > 0)
            .Where(name =>
                name.EndsWith("狸", StringComparison.Ordinal) ||
                KnownSpecialUnitIds.ContainsKey(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            var id = KnownSpecialUnitIds.GetValueOrDefault(name) ??
                     $"special_unit_candidate_{triggerId}_{index + 1}";
            yield return (id, name);
        }
    }


    private async Task<Phase2OperationalState> AnalyzeBattleAsync(
        CaptureFrame frame,
        Phase2OperationalState state,
        EvidenceReference evidence,
        RunSnapshot baseSnapshot,
        CancellationToken cancellationToken)
    {
        var totalStarted = Stopwatch.GetTimestamp();
        var nodeTask = MeasureAsync(() => ReadNodeAsync(
            frame,
            Phase2RecognitionRegions.BattleNodeIdentity,
            "battle-node",
            evidence,
            cancellationToken));
        var actionTask = MeasureAsync(() => ReadRemainingActionValueAsync(
            frame,
            evidence,
            cancellationToken));
        var damageTask = MeasureAsync(() => ReadBattleDamageAsync(
            frame,
            evidence,
            cancellationToken));
        var nodeResult = await nodeTask.ConfigureAwait(false);
        var actionResult = await actionTask.ConfigureAwait(false);
        var damageResult = await damageTask.ConfigureAwait(false);
        // 战斗页节点号优先继承备战页已确认节点（程序刚退出备战即进入战斗，
        // 节点号必然一致）；OCR 仅在无继承时兜底——实测战斗页数字 OCR 会把
        // "1-1"读成"1-9"，污染对局归档（节点 1-9 战斗开始）。
        var inheritedNode = GetStableRun(baseSnapshot.RunId)
            .LastPreparationNodeId;
        var node = string.IsNullOrWhiteSpace(inheritedNode)
            ? nodeResult.Value
            : Observation<string>.Known(
                inheritedNode,
                0.9,
                [evidence with
                {
                    Locator = "inherited:preparation-node",
                    Summary = $"战斗页节点号继承备战页确认值 {inheritedNode}"
                }],
                frame.CapturedAt);
        var action = actionResult.Value;
        var (damage, synergyDamage, unresolvedDamage, totalCandidate, pending) =
            damageResult.Value;
        var diagnostics = baseSnapshot.Health.Status == ObservationStatus.Known
            ? new List<string>
            {
                $"生命值沿用现有识别结果：{baseSnapshot.Health.Value}。"
            }
            : [];
        if (EnableTimingDiagnostics)
        {
            diagnostics.Add(
                $"perf:battle node={nodeResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"action={actionResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"damage={damageResult.Elapsed.TotalMilliseconds:F1}ms; " +
                $"total={Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds:F1}ms");
        }

        return state with
        {
            NodeId = node,
            BattleDamage = damage,
            BattleSynergyDamage = synergyDamage,
            BattleUnresolvedDamage = unresolvedDamage,
            BattleScreenDamageCandidate = totalCandidate,
            RemainingActionValue = action,
            PendingIcons = pending,
            Diagnostics = diagnostics
        };
    }

    private async Task<Phase2OperationalState> AnalyzeSettlementAsync(
        CaptureFrame frame,
        Phase2OperationalState state,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var nodeTask = ReadNodeAsync(
            frame,
            Phase2RecognitionRegions.SettlementNodeValue,
            "settlement-node",
            evidence,
            cancellationToken);
        var damageTask = ReadSettlementDamageAsync(
            frame,
            evidence,
            cancellationToken);
        var node = await nodeTask.ConfigureAwait(false);
        var (damage, totalCandidate, pending) =
            await damageTask.ConfigureAwait(false);
        return state with
        {
            NodeId = node,
            SettlementDamage = damage,
            SettlementScreenDamageCandidate = totalCandidate,
            PendingIcons = pending
        };
    }

    private async Task<(
        Observation<IReadOnlyList<CharacterDamageState>> Damage,
        Observation<long> TotalCandidate,
        IReadOnlyList<PendingIconObservation> Pending)>
        ReadSettlementDamageAsync(
            CaptureFrame frame,
            EvidenceReference evidence,
            CancellationToken cancellationToken)
    {
        var avatarSlots = Enumerable.Range(0, 3)
            .Select(Phase2RecognitionRegions.SettlementDamageAvatar)
            .ToArray();
        var avatars = RecognizeIconsSafely(
            frame,
            "character-avatar",
            avatarSlots,
            iconTemplates);
        var damageTextTasks = Enumerable.Range(0, 3)
            .Select(row => ReadNumericTextAsync(
                frame,
                Phase2RecognitionRegions.SettlementDamageValue(row),
                cancellationToken))
            .ToArray();
        await Task.WhenAll(damageTextTasks).ConfigureAwait(false);
        var rows = new List<CharacterDamageState>();
        var pending = new List<PendingIconObservation>();
        var missingValue = false;
        var ambiguousScale = false;
        for (var row = 0; row < 3; row++)
        {
            var damageRegion = Phase2RecognitionRegions.SettlementDamageValue(row);
            var texts = damageTextTasks[row].Result;
            var candidates = texts.SelectMany(ParseSettlementDamageCandidates)
                .OrderByDescending(item => item.Score)
                .ToArray();
            var avatar = avatars[row];
            if (candidates.Length == 0 &&
                (avatar.IsKnown ||
                 avatar.Confidence >= 0.25 ||
                 HasDetailedForeground(frame, avatarSlots[row])) &&
                !HasVisibleSettlementDamageBar(frame, row))
            {
                candidates = [(0, 2, "0 (empty settlement damage bar)")];
            }

            if (candidates.Length == 0)
            {
                missingValue = true;
                continue;
            }

            var best = candidates[0];
            var hasExplicitScale = best.Value == 0 ||
                                   HasExplicitDamageScaleSafe(best.Text);
            ambiguousScale |= !hasExplicitScale;
            var damageConfidence = hasExplicitScale
                ? best.Score >= 3 ? 0.75 : 0.45
                : 0.30;
            if (avatar.IsKnown && avatar.TemplateId is not null)
            {
                rows.Add(new CharacterDamageState(
                    row + 1,
                    avatar.TemplateId,
                    best.Value,
                    best.Text,
                    avatar.Confidence,
                    damageConfidence,
                    ToRelative(avatarSlots[row]),
                    ToRelative(damageRegion),
                    evidence with
                    {
                        Locator = $"ocr:settlement-damage-row-{row + 1}",
                        Summary = best.Text,
                        Confidence = Math.Min(avatar.Confidence, damageConfidence)
                    }));
                continue;
            }

            var temporaryId = $"unknown-settlement-character-slot-{row + 1}";
            var avatarCandidates = (avatar.CandidateTemplateIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            rows.Add(new CharacterDamageState(
                row + 1,
                temporaryId,
                best.Value,
                best.Text,
                avatar.Confidence,
                damageConfidence,
                ToRelative(avatarSlots[row]),
                ToRelative(damageRegion),
                evidence with
                {
                    Locator = $"partial:settlement-damage-row-{row + 1}",
                    Summary = best.Text,
                    Confidence = Math.Min(avatar.Confidence, damageConfidence)
                },
                temporaryId,
                avatarCandidates,
                "结算伤害数值可读，但角色或特殊单位头像无法唯一识别。",
                false));
            pending.Add(new PendingIconObservation(
                PendingIconCategory.CharacterAvatar,
                $"settlement-damage-character-{row + 1}",
                ToRelative(avatarSlots[row]),
                avatar.TemplateId,
                avatar.Confidence,
                evidence with
                {
                    Locator = $"crop:settlement-damage-character:{row + 1}",
                    Summary = best.Text,
                    Confidence = avatar.Confidence
                },
                "settlement-character-unresolved",
                avatarCandidates,
                temporaryId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["damage"] = best.Value.ToString(CultureInfo.InvariantCulture),
                    ["rawText"] = best.Text,
                    ["sourceType"] = "character-or-special-unit"
                },
                false));
        }

        var complete = !missingValue && rows.Count == 3;
        // 2026-08-19「S3 伤害数字与头像解耦」：用户规则——结算伤害数字是独立
        // 逻辑，只要数字读到（complete）且尺度/单位明确即可确认为最终伤害，
        // 不再要求"每行头像身份都已知"。头像 unknown（CanDriveDecisions=false）
        // 只影响该行的角色归属，不影响伤害数字本身；行内 Damage 值仍保留
        // 进榜单，头像作为 pending 由下游单独标记。
        var damageScaleResolved = complete && !ambiguousScale;
        if (damageScaleResolved)
        {
            var knownDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
                    rows,
                    rows.Average(item => Math.Min(
                        item.AvatarConfidence,
                        item.DamageConfidence)),
                    rows.Select(item => item.Evidence),
                    frame.CapturedAt);
            return (
                knownDamage,
                Observation<long>.Known(
                    rows.Sum(item => item.Damage),
                    0.75,
                    rows.Select(item => item.Evidence),
                    frame.CapturedAt),
                pending);
        }

        var reason = ambiguousScale
            ? "结算伤害单位未识别；保留原始 OCR，但该数量级不作为最终伤害。"
            : !complete
                ? "结算前三名有伤害数值暂不可见；已保留其余可读行。"
                : "结算前三名伤害数值完整，但至少一个头像身份未知。";
        var partialDamage = PartialUnknown<IReadOnlyList<CharacterDamageState>>(
                rows,
                reason,
                evidence with { Locator = "partial:settlement-top-three" },
                frame.CapturedAt);
        var partialTotal = rows.Count == 0
            ? Observation<long>.Unknown(
                "结算前三名没有可求和的伤害数值。",
                [evidence with
                {
                    Locator = "ocr:settlement-total-candidate",
                    Summary = string.Empty
                }],
                frame.CapturedAt)
            : ambiguousScale
                ? PartialUnknown(
                    rows.Sum(item => item.Damage),
                    "结算伤害单位未识别；禁止把推测的万位值作为最终伤害。",
                    evidence with { Locator = "partial:settlement-total-ambiguous-scale" },
                    frame.CapturedAt)
            : complete
                ? Observation<long>.Known(
                    rows.Sum(item => item.Damage),
                    0.70,
                    rows.Select(item => item.Evidence),
                    frame.CapturedAt)
                : PartialUnknown(
                    rows.Sum(item => item.Damage),
                    "结算前三名数值不完整；当前和仅作残缺候选。",
                    evidence with { Locator = "partial:settlement-total-candidate" },
                    frame.CapturedAt);
        return (
            partialDamage,
            partialTotal,
            pending);
    }

    /// <summary>
    /// 结算金币奖励识别已废弃（用户 2026-08-06 确认删除：识别率低且无用途）。
    /// SettlementGoldReward 保持 Unknown，不再采集。
    /// </summary>
    private Task<Observation<int>> ReadSettlementGoldAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            Observation<int>.Unknown("结算金币奖励识别已废弃。",
                [evidence with { Locator = "deprecated:settlement-gold-reward" }],
                frame.CapturedAt));

    private static (int Value, int Count)[] RankRepeatedIntegers(
        OcrTextResult recognized,
        int minimum = 0,
        int maximum = 9999) =>
        recognized.Lines.Prepend(recognized.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .SelectMany(text => IntegerPattern().Matches(text)
                .Select(match => int.TryParse(
                    match.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value)
                    ? value
                    : -1))
            .Where(value => value >= minimum && value <= maximum)
            .GroupBy(value => value)
            .Select(group => (Value: group.Key, Count: group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Value)
            .ToArray();

    internal static IEnumerable<(long Value, int Score, string Text)>
        ParseBattleDamageCandidates(string text)
    {
        const string inferredWanSuffix = " (settlement unit inferred as 万)";
        foreach (var candidate in ParseSettlementDamageCandidates(text))
        {
            var inferredWan = candidate.Text.EndsWith(
                inferredWanSuffix,
                StringComparison.Ordinal);
            var sourceText = inferredWan
                ? candidate.Text[..^inferredWanSuffix.Length]
                : candidate.Text;
            var canonicalBaseValue = CanonicalThousandsPattern().IsMatch(
                sourceText.Trim());
            if (canonicalBaseValue && inferredWan)
            {
                continue;
            }

            var score = canonicalBaseValue
                ? Math.Max(candidate.Score, 3)
                : candidate.Score;
            yield return (candidate.Value, score, candidate.Text);
        }
    }

    internal static IEnumerable<(long Value, int Score, string Text)>
        ParseSettlementDamageCandidates(string text)
    {
        foreach (var candidate in ParseDamageCandidates(text))
        {
            yield return candidate;
        }

        foreach (Match match in SettlementAsciiUnitPattern().Matches(text))
        {
            var rawNumber = NormalizeDamageNumber(
                match.Groups["number"].Value);
            if (!decimal.TryParse(
                    rawNumber,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var numeric))
            {
                continue;
            }

            var scaled = numeric * 10_000m;
            // 上限放宽（C 修复，用户 2026-08-06 深夜确认）：原 1000 亿
            //（100_000_000_000）会丢弃 3-7 等首领节点大伤害（数千亿量级），
            // 放宽到 1 万亿（long 最大 9.2e18，安全）。
            if (scaled is < 0 or > 10_000_000_000_000m)
            {
                continue;
            }

            yield return (
                decimal.ToInt64(decimal.Round(
                    scaled,
                    0,
                    MidpointRounding.AwayFromZero)),
                8,
                match.Value.Trim());
        }

        if (!text.Contains('万') &&
            !text.Contains('亿') &&
            !text.Contains('億'))
        {
            foreach (Match match in SettlementDecimalPattern().Matches(text))
            {
                var rawNumber = NormalizeDamageNumber(
                    match.Groups["number"].Value);
                if (!decimal.TryParse(
                        rawNumber,
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var numeric))
                {
                    continue;
                }

                yield return (
                    decimal.ToInt64(decimal.Round(
                        numeric * 10_000m,
                        0,
                        MidpointRounding.AwayFromZero)),
                    7,
                    $"{match.Value.Trim()} (settlement unit inferred as 万)");
            }
        }
    }

    private static bool HasVisibleSettlementDamageBar(
        CaptureFrame frame,
        int row)
    {
        var region = Phase2RecognitionRegions.SettlementDamageBar(row)
            .ToPixels(frame.Width, frame.Height);
        var colored = CountPixels(frame, region, static (blue, green, red) =>
            red >= 150 &&
            blue >= 150 &&
            red >= green + 20 &&
            blue >= green + 20);
        return colored >= Math.Max(8, region.Width * region.Height / 200);
    }

    internal static bool HasExplicitDamageScaleSafe(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.Contains("inferred", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Contains('.', StringComparison.Ordinal) ||
               text.Any(character => character is '\u4E07' or '\u4EBF') ||
               CanonicalThousandsPattern().IsMatch(text.Trim());
    }

    internal static (
        long Value,
        int Score,
        string Text,
        bool HasConflict) ReconcileSuspiciousBattleDamageCandidate(
        long primaryValue,
        int primaryScore,
        string primaryText,
        long secondaryValue,
        int secondaryScore,
        string secondaryText,
        bool hasVisibleDamageBar)
    {
        if (primaryValue == secondaryValue)
        {
            return (
                primaryValue,
                Math.Max(primaryScore, secondaryScore),
                $"{primaryText} (secondary confirmed)",
                false);
        }

        if (!HasExplicitDamageScaleSafe(primaryText) &&
            HasExplicitDamageScaleSafe(secondaryText))
        {
            return (secondaryValue, secondaryScore, secondaryText, false);
        }

        if (secondaryValue == 0 && primaryValue > 0 && !hasVisibleDamageBar)
        {
            return (
                0,
                Math.Max(2, secondaryScore),
                "0 (secondary OCR confirmed empty battle row)",
                false);
        }

        return (
            primaryValue,
            primaryScore,
            $"{primaryText} (secondary conflict: {secondaryText})",
            true);
    }

    internal static bool CanConfirmEmptyBattleDamageRow(
        bool secondaryReturnedBlank,
        bool hasVisibleDamageBar,
        bool hasKnownCharacter,
        bool hasKnownSynergy) =>
        secondaryReturnedBlank &&
        !hasVisibleDamageBar &&
        !hasKnownCharacter &&
        !hasKnownSynergy;

    /// <summary>
    /// 打call特效检测（用户 2026-08-07）：应援棒在卡牌左右两侧边缘
    ///（左冰蓝发光棒 + 右暖黄发光棒，动态摇晃）。检测两侧边缘区域
    /// 的高饱和发光色像素占比：左侧命中冰蓝 + 右侧命中暖黄 → 判定
    /// 该角色被开拓者•欢愉打call加持。仅备战页调用（战斗页无特效）。
    /// 由 _callEffectEnabled（形成内有后台开拓者）门控。
    /// </summary>
    private static bool DetectCallEffect(
        CaptureFrame frame,
        CharacterCardSlotRecognition slot) =>
        DetectCallEffect(frame, slot.ReferenceBounds);

    /// <summary>
    /// 应援检测（PixelRect 版）：识别前用固定槽位坐标前置检测，
    /// 被应援的槽位在角色匹配时避开底部应援棒区域（用户 2026-08-07：
    /// 000036/000037 银狼被应援 → 底部污染 → 匹配分 0.43 卡阈值失败）。
    /// 2026-08-07 深夜用用户录屏 call.mp4（应援棒动画 40 帧）重标定：
    /// 应援棒实际是"左右两侧高亮蓝色发光棒"（B>140、B≥R+40、亮度>110，
    /// 左右 20% 宽、40-95% 高区域），不是之前假设的"左蓝右黄"——
    /// 旧阈值对录屏 0/40 全漏检（右黄比例仅 0.02），新特征 40/40 全检出
    /// 且无应援帧（000035）0.08 不误判（阈值 0.10 干净分离）。
    /// </summary>
    private static bool DetectCallEffect(
        CaptureFrame frame,
        PixelRect bounds)
        => MeasureCallEffectStrength(frame, bounds) >= 0.10;

    private static double MeasureCallEffectStrength(
        CaptureFrame frame,
        PixelRect referenceBounds)
    {
        var bounds = ScaleReferenceBounds(frame, referenceBounds);
        var left = new PixelRect(
            bounds.X,
            bounds.Y + (int)(bounds.Height * 0.40),
            Math.Max(2, (int)(bounds.Width * 0.20)),
            (int)(bounds.Height * 0.55));
        var right = new PixelRect(
            bounds.Right - Math.Max(2, (int)(bounds.Width * 0.20)),
            bounds.Y + (int)(bounds.Height * 0.40),
            Math.Max(2, (int)(bounds.Width * 0.20)),
            (int)(bounds.Height * 0.55));
        // 亮蓝棒：高亮 + 蓝强于红（应援棒发光蓝，录屏标定 B≈190 R≈80）
        var leftBlueRatio = ColoredPixelRatio(
            frame,
            left,
            static (b, g, r) => b > 140 && b >= r + 40 && (r + g + b) / 3 > 110);
        var rightBlueRatio = ColoredPixelRatio(
            frame,
            right,
            static (b, g, r) => b > 140 && b >= r + 40 && (r + g + b) / 3 > 110);
        // 两侧都必须明显发光；较弱一侧决定应援证据强度。
        return Math.Min(leftBlueRatio, rightBlueRatio);
    }

    internal static Observation<IReadOnlyList<FormationCharacterState>>
        ReconcileCallEffectFromCompleteFormation(
            CaptureFrame frame,
            Observation<IReadOnlyList<FormationCharacterState>> formation) =>
        ReconcileCallEffectFromCompleteFormation(
            formation,
            character => character.CardRegion is { } region
                ? MeasureCallEffectStrength(
                    frame,
                    new PixelRect(
                        (int)Math.Round(region.X * 1920),
                        (int)Math.Round(region.Y * 1080),
                        Math.Max(1, (int)Math.Round(region.Width * 1920)),
                        Math.Max(1, (int)Math.Round(region.Height * 1080))))
                : 0);

    internal static Observation<IReadOnlyList<FormationCharacterState>>
        ReconcileCallEffectFromCompleteFormation(
            Observation<IReadOnlyList<FormationCharacterState>> formation,
            Func<FormationCharacterState, double> strengthSelector)
    {
        ArgumentNullException.ThrowIfNull(formation);
        ArgumentNullException.ThrowIfNull(strengthSelector);
        if (formation.Value is not { Count: > 0 } characters)
        {
            return formation;
        }

        var hasJoyTrailblazer = characters.Any(item =>
            item.Zone is FormationZone.Back or FormationZone.Bench &&
            string.Equals(
                item.CharacterId,
                "currency_wars_character_trailblazer",
                StringComparison.OrdinalIgnoreCase));
        FormationCharacterState? selected = null;
        if (hasJoyTrailblazer)
        {
            selected = characters
                .Where(item =>
                    item.Zone is FormationZone.Front or FormationZone.Back or
                        FormationZone.Bench)
                .Select(item => new
                {
                    Character = item,
                    Strength = strengthSelector(item)
                })
                .Where(item => item.Strength >= 0.10)
                .OrderByDescending(item => item.Strength)
                .ThenByDescending(item => item.Character.Confidence)
                .Select(item => item.Character)
                .FirstOrDefault();
        }

        return formation with
        {
            Value = characters
                .Select(item => item with
                {
                    IsCheered = selected is not null &&
                        item.Zone == selected.Zone &&
                        item.SlotIndex == selected.SlotIndex
                })
                .ToArray()
        };
    }

    /// <summary>
    /// 猎星人标记检测（用户 2026-08-07）：星核猎手羁绊中装备最多的角色
    /// 成为猎星人，卡牌左上角出现金色徽章（米黄十字形凸起+中心黑环+
    /// 四角星）。独立状态标记（非装备）。检测左上角区域的金色像素占比
    ///（徽章主色米黄/浅金）——背景为卡牌深色时金色徽章显著。
    /// </summary>
    private static bool DetectHunterStar(
        CaptureFrame frame,
        CharacterCardSlotRecognition slot)
    {
        var bounds = ScaleReferenceBounds(frame, slot.ReferenceBounds);
        // 特殊装备与猎星人标签会在左上角纵向排列：特殊装备在上，
        // 猎星人在下。只检测下方标签带，避免把病毒防火墙等金色特殊
        // 装备边框误当成猎星人。
        var region = new PixelRect(
            bounds.X + (int)(bounds.Width * 0.02),
            bounds.Y + (int)(bounds.Height * 0.25),
            Math.Max(3, (int)(bounds.Width * 0.24)),
            (int)(bounds.Height * 0.32));
        // 金色徽章：R、G 高（米黄 ~170+），B 明显低（<140），与卡牌深色
        // 背景/蓝紫边框区分。参考图实测徽章为米黄(约 190,170,120 附近)。
        var goldRatio = ColoredPixelRatio(
            frame,
            region,
            static (b, g, r) => r >= 150 && g >= 120 && r >= b + 40);
        return goldRatio >= 0.30;
    }

    private static PixelRect ScaleReferenceBounds(
        CaptureFrame frame,
        PixelRect referenceBounds) =>
        new(
            (int)Math.Round(referenceBounds.X * frame.Width / 1920d),
            (int)Math.Round(referenceBounds.Y * frame.Height / 1080d),
            Math.Max(1, (int)Math.Round(
                referenceBounds.Width * frame.Width / 1920d)),
            Math.Max(1, (int)Math.Round(
                referenceBounds.Height * frame.Height / 1080d)));

    private static double ColoredPixelRatio(
        CaptureFrame frame,
        PixelRect region,
        Func<byte, byte, byte, bool> predicate)
    {
        var total = 0;
        var hit = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            if (y < 0 || y >= frame.Height)
            {
                continue;
            }

            for (var x = region.X; x < region.Right; x++)
            {
                if (x < 0 || x >= frame.Width)
                {
                    continue;
                }

                total++;
                var offset = y * frame.Stride + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                if (predicate(blue, green, red))
                {
                    hit++;
                }
            }
        }

        return total == 0 ? 0 : (double)hit / total;
    }

    internal static bool HasVisibleBattleDamageBar(
        CaptureFrame frame,
        int row)
    {
        var region = Phase2RecognitionRegions.BattleDamageBar(row)
            .ToPixels(frame.Width, frame.Height);
        var longestRun = 0;
        var currentRun = 0;
        var minimumColoredPixelsPerColumn = Math.Max(2, region.Height / 5);
        for (var x = region.X; x < region.Right; x++)
        {
            var coloredInColumn = 0;
            for (var y = region.Y; y < region.Bottom; y++)
            {
                var offset = y * frame.Stride + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                if (red >= 140 && blue >= 120 && red >= green + 15)
                {
                    coloredInColumn++;
                }
            }

            if (coloredInColumn >= minimumColoredPixelsPerColumn)
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        // A real fill is a horizontal run. Bright battle effects can create a
        // few scattered magenta pixels behind the translucent panel, which the
        // former aggregate pixel threshold incorrectly treated as a missed row.
        return longestRun >= Math.Max(4, region.Width / 40);
    }

    private static bool HasColorfulPixels(
        CaptureFrame frame,
        NormalizedRect normalized)
    {
        var region = normalized.ToPixels(frame.Width, frame.Height);
        var colorful = CountPixels(frame, region, static (blue, green, red) =>
        {
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            return maximum >= 90 && maximum - minimum >= 45;
        });
        return colorful >= Math.Max(12, region.Width * region.Height * 3 / 100);
    }

    private static bool HasDetailedForeground(
        CaptureFrame frame,
        NormalizedRect normalized)
    {
        var region = normalized.ToPixels(frame.Width, frame.Height);
        if (region.Width < 2 || region.Height < 2)
        {
            return false;
        }

        long sum = 0;
        long sumOfSquares = 0;
        var transitions = 0;
        var comparisons = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var rowOffset = y * frame.Stride;
            var previous = -1;
            for (var x = region.X; x < region.Right; x++)
            {
                var offset = rowOffset + x * 4;
                var luminance = (frame.BgraPixels[offset] * 29 +
                                 frame.BgraPixels[offset + 1] * 150 +
                                 frame.BgraPixels[offset + 2] * 77) >> 8;
                sum += luminance;
                sumOfSquares += luminance * luminance;
                if (previous >= 0)
                {
                    comparisons++;
                    if (Math.Abs(luminance - previous) >= 25)
                    {
                        transitions++;
                    }
                }

                if (y > region.Y)
                {
                    var upperOffset = offset - frame.Stride;
                    var upper = (frame.BgraPixels[upperOffset] * 29 +
                                 frame.BgraPixels[upperOffset + 1] * 150 +
                                 frame.BgraPixels[upperOffset + 2] * 77) >> 8;
                    comparisons++;
                    if (Math.Abs(luminance - upper) >= 25)
                    {
                        transitions++;
                    }
                }

                previous = luminance;
            }
        }

        var count = region.Width * region.Height;
        var mean = sum / (double)count;
        var variance = Math.Max(0, sumOfSquares / (double)count - mean * mean);
        var transitionRatio = comparisons == 0
            ? 0
            : transitions / (double)comparisons;
        // 阈值放宽（2026-08-07 调查结论）：原值 stddev≥18 && transitionRatio≥0.035
        // 对 Back 行小装备图标/低对比区域误判无内容，导致装备漏识别；
        // 放宽到 14/0.025 后空槽（纯背景低方差）仍被过滤，但 Back 行
        // 小图标能通过。
        return Math.Sqrt(variance) >= 14 && transitionRatio >= 0.025;
    }

    private static bool HasCenteredEquipmentForeground(
        CaptureFrame frame,
        NormalizedRect normalized)
    {
        const double horizontalInset = 0.15;
        var centered = normalized with
        {
            X = normalized.X + normalized.Width * horizontalInset,
            Width = normalized.Width * (1 - 2 * horizontalInset)
        };
        return HasDetailedForeground(frame, centered);
    }

    private static int CountPixels(
        CaptureFrame frame,
        PixelRect region,
        Func<byte, byte, byte, bool> predicate)
    {
        var count = 0;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var rowOffset = y * frame.Stride;
            for (var x = region.X; x < region.Right; x++)
            {
                var offset = rowOffset + x * 4;
                if (predicate(
                    frame.BgraPixels[offset],
                    frame.BgraPixels[offset + 1],
                    frame.BgraPixels[offset + 2]))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private Observation<IReadOnlyList<FormationCharacterState>> ObserveFormation(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardSlotRecognition> board,
        IReadOnlyList<CharacterCardSlotRecognition> bench,
        EvidenceReference evidence,
        DateTimeOffset observedAt,
        ICollection<PendingIconObservation> pending,
        bool compactBoardLayout,
        int backSlotCount)
    {
        // 打call特效启用条件（用户 2026-08-07）：识别到开拓者且在后台/
        // 备战席（欢愉形态）时，才检测其他角色卡牌两侧的应援棒特效。
        // 战斗页无此特效，不检测（ObserveFormation 仅备战页调用）。
        _callEffectEnabled = board.Concat(bench)
            .Any(item => string.Equals(
                item.CharacterId,
                "currency_wars_character_trailblazer",
                StringComparison.OrdinalIgnoreCase));
        // 应援唯一化（用户 2026-08-07：应援角色全场只能有一个——后台
        // 开拓者触发时只应援一个角色）。收集所有 DetectCallEffect=True
        // 的槽位，取识别置信度最高的唯一一个标记 IsCheered，其余 false
        //（此前所有槽位都检测，卡牌蓝边框误判导致多个角色标应援）。
        string? cheeredSlotKey = null;
        if (_callEffectEnabled)
        {
            // 应援唯一化（用户 2026-08-07：应援角色全场只能有一个）：
            // 收集所有 DetectCallEffect=True 的槽位，取识别置信度最高的
            // 唯一一个标记 IsCheered（此前所有槽位都检测，卡牌蓝边框
            // 误判导致多个角色标应援）。
            var candidates = new List<(
                string Zone,
                int SlotIndex,
                double EffectStrength,
                double CharacterConfidence)>();
            for (var i = 0; i < board.Count; i++)
            {
                if (board[i].State is CharacterCardSlotState.Recognized or
                    CharacterCardSlotState.Uncertain)
                {
                    var effectStrength = MeasureCallEffectStrength(
                        frame,
                        board[i].ReferenceBounds);
                    if (effectStrength >= 0.10)
                    {
                        candidates.Add((
                            board[i].SlotIndex < 4 ? "Front" : "Back",
                            board[i].SlotIndex,
                            effectStrength,
                            board[i].Confidence));
                    }
                }
            }

            for (var i = 0; i < bench.Count; i++)
            {
                if (bench[i].State is CharacterCardSlotState.Recognized or
                    CharacterCardSlotState.Uncertain)
                {
                    var effectStrength = MeasureCallEffectStrength(
                        frame,
                        bench[i].ReferenceBounds);
                    if (effectStrength >= 0.10)
                    {
                        candidates.Add((
                            "Bench",
                            bench[i].SlotIndex,
                            effectStrength,
                            bench[i].Confidence));
                    }
                }
            }

            var best = candidates
                .OrderByDescending(c => c.EffectStrength)
                .ThenByDescending(c => c.CharacterConfidence)
                .FirstOrDefault();
            if (best.Zone is not null)
            {
                cheeredSlotKey = $"{best.Zone}:{best.SlotIndex}";
            }
        }

        // 注意：识别时（AnalyzePreparationAsync 前置检测）所有应援候选
        // 都已走"避开底部+两侧"路径；此处仅决定 IsCheered 标记（唯一）。

        // 猎星人三强校验（用户 2026-08-19）：
        //  ① 仅前台/后台（赛场；备战席不可能，Add 只对 board 收集、bench 不含）
        //  ② 需激活「星核猎手」羁绊（场上星核猎手成员 ≥2）
        //  ③ 只有星核猎手羁绊成员才能成为猎星人（阿格莱雅等非星核猎手排除）
        //  ④ 全场唯一，最多 1 个；若多个候选 → 全不显示（治标兜底）。
        string? hunterStarSlotKey = null;
        {
            var starHunterNames = new HashSet<string>(
                ["千冶•刃", "银狼LV.999", "流萤", "银狼", "卡芙卡", "刃"],
                StringComparer.Ordinal);
            var onField = new List<CharacterCardSlotRecognition>();
            foreach (var s in board)
            {
                if (s.State is CharacterCardSlotState.Recognized or
                    CharacterCardSlotState.Uncertain)
                {
                    onField.Add(s);
                }
            }
            var starHunterOnField = onField.Count(s =>
                s.DisplayName is not null &&
                starHunterNames.Contains(s.DisplayName));
            var hunterCandidates = onField
                .Where(s =>
                    s.DisplayName is not null &&
                    starHunterNames.Contains(s.DisplayName) &&
                    DetectHunterStar(frame, s))
                .ToList();
            if (starHunterOnField >= 2 && hunterCandidates.Count == 1)
            {
                var h = hunterCandidates[0];
                hunterStarSlotKey =
                    $"{(h.SlotIndex < 4 ? "Front" : "Back")}:{h.SlotIndex}";
            }
        }

        var states = new List<FormationCharacterState>();
        Add(board.Where(item => item.SlotIndex < 4), FormationZone.Front, states);
        Add(board.Where(item => item.SlotIndex >= 4), FormationZone.Back, states);
        Add(bench, FormationZone.Bench, states);
        var uncertainCount = board.Concat(bench)
            .Count(item => item.State is CharacterCardSlotState.Uncertain or
                CharacterCardSlotState.SpecialOccupied);
        var confidence = states.Count == 0
            ? (uncertainCount == 0 ? 1 : 0)
            : states.Average(item => item.Confidence) *
              (uncertainCount == 0 ? 1 : 0.8);
        var formationEvidence = evidence with
        {
            Locator = "vision:formation-slots",
            Summary = uncertainCount == 0
                ? $"识别到 {states.Count} 个角色。"
                : $"识别到 {states.Count} 个角色；{uncertainCount} 个槽位暂不可见或不确定。"
        };
        if (uncertainCount == 0)
        {
            return Observation<IReadOnlyList<FormationCharacterState>>.Known(
                states,
                confidence,
                [formationEvidence],
                observedAt);
        }

        // 阵容分级判定（用户 2026-08-06 深夜"15号"最高优先级修复）：
        // 旧逻辑只要有 1 个槽位 Uncertain/SpecialOccupied 就整体判 Unknown，
        // 导致每帧都有少量失败槽位时，识别成功的角色（如 2-5 帧 547 槽位）
        // 全部被连累丢弃 → UI/报告显示"阵容未记录/未知角色"（全 run 0 Known）。
        // 修复：识别到 ≥1 个真实角色时降为 Known（保留 value + Uncertainty 注明残缺），
        // 让已识别角色正常参与 UI 显示与羁绊累加；仅全部槽位失败/空才 Unknown。
        // 注意：states 含 Uncertain 占位槽（unknown-formation-unit），必须按
        // 真实角色数判定，不能按 states.Count（否则全失败也会误判 Known）。
        var recognizedCharacterCount = states.Count(item =>
            !string.IsNullOrWhiteSpace(item.CharacterId) &&
            !item.CharacterId.StartsWith(
                "unknown-formation-unit",
                StringComparison.Ordinal));
        if (recognizedCharacterCount > 0)
        {
            return new Observation<IReadOnlyList<FormationCharacterState>>
            {
                Status = ObservationStatus.Known,
                Value = states,
                Confidence = confidence,
                Evidence = [formationEvidence],
                Uncertainty =
                [
                    $"阵容包含 {uncertainCount} 个未识别角色或特殊占用单位；" +
                    "已识别槽位仍作为有效阵容保留（分级判定）。"
                ],
                ObservedAt = observedAt
            };
        }

        return new Observation<IReadOnlyList<FormationCharacterState>>
        {
            Status = ObservationStatus.Unknown,
            Value = states,
            Confidence = 0,
            Evidence = [formationEvidence],
            Uncertainty =
            ["阵容包含未识别角色或特殊占用单位；已识别槽位仍作为残缺证据保留。"],
            ObservedAt = observedAt
        };

        void Add(
            IEnumerable<CharacterCardSlotRecognition> slots,
            FormationZone zone,
            ICollection<FormationCharacterState> target)
        {
            foreach (var slot in slots.Where(item =>
                         item.State is CharacterCardSlotState.Uncertain or
                             CharacterCardSlotState.SpecialOccupied))
            {
                var region = ToRelative(slot.ReferenceBounds);
                var isKnownSpecial =
                    slot.State == CharacterCardSlotState.SpecialOccupied;
                var candidates = new[]
                    {
                        slot.MatchedTemplateId,
                        slot.CharacterId,
                        slot.RunnerUpCharacterId
                    }
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var temporaryId =
                    isKnownSpecial
                        ? $"special-formation-unit-{zone}-{slot.SlotIndex + 1}"
                        : $"unknown-formation-unit-{zone}-{slot.SlotIndex + 1}";
                // 2026-08-08：特殊单位（佩佩等）输出模板 id（如
                // special_unit_peipei）作为 CharacterId，报告才能显示名字；
                // 普通 Uncertain 槽位仍用 unknown-formation-unit 临时 id。
                var resolvedCharacterId = isKnownSpecial
                    ? slot.CharacterId ?? temporaryId
                    : temporaryId;
                // Bench occupants never carry normal character equipment.
                // Special bench items are recognized by their dedicated path
                // after formation analysis; running dHash here only adds cost
                // and can create unrelated pending equipment observations.
                var equipmentSlots = zone is FormationZone.Front or FormationZone.Back
                    ? RecognizeEquipmentSlots(
                        slot, zone, resolvedCharacterId, pending).Slots
                    : Array.Empty<CharacterEquipmentSlotState>();
                target.Add(new FormationCharacterState(
                    zone,
                    slot.SlotIndex,
                    resolvedCharacterId,
                    slot.StarLevel,
                    "special-unit",
                    [],
                    slot.Confidence,
                    evidence with
                    {
                        Locator = $"partial:formation:{zone}:{slot.SlotIndex + 1}",
                        Summary = slot.DisplayName,
                        Confidence = slot.Confidence
                    },
                    temporaryId,
                    candidates,
                    isKnownSpecial
                        ? "已识别为特殊占用单位；不按普通角色驱动决策。"
                        : "角色或特殊单位头像无法唯一识别；槽位和裁剪已保留。",
                    false,
                    region,
                    equipmentSlots));
                pending.Add(new PendingIconObservation(
                    PendingIconCategory.CharacterAvatar,
                    $"formation-{zone}-{slot.SlotIndex + 1}",
                    region,
                    slot.CharacterId,
                    slot.Confidence,
                    evidence with
                    {
                        Locator = $"crop:formation:{zone}:{slot.SlotIndex + 1}",
                        Summary = slot.DisplayName,
                        Confidence = slot.Confidence
                    },
                    isKnownSpecial
                        ? "special-unit-recognized"
                        : "character-or-special-unit-unresolved",
                    candidates,
                    temporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["zone"] = zone.ToString(),
                        ["slotIndex"] = slot.SlotIndex.ToString(
                            CultureInfo.InvariantCulture),
                        ["sourceType"] = "formation-unit"
                    },
                    false));
            }

            foreach (var slot in slots.Where(item =>
                         item.State == CharacterCardSlotState.Recognized &&
                         item.CharacterId is not null))
            {
                var id = slot.CharacterId!;
                // 装备只属于场上角色（用户 2026-08-07：备战席角色不穿装备，
                // 不识别备战席装备——此前备战席角色被误判出 3 件装备）。
                var isOnField = zone is FormationZone.Front or FormationZone.Back;
                // 2026-08-16 修复（验收实锤）：Back 角色在 warped 帧识别，
                // 但装备带（卡牌下方）不在 WarpBackRow 源区域内——warped
                // 帧里没有装备带。装备/特殊装备识别必须回原帧 + 原帧
                // BackCharacterSlots1920 坐标（此前用 warped 坐标当原帧
                // 坐标 → 裁到原帧顶部噪声 → 假匹配金垃圾袋/真装备漏检）。
                var equipmentOwner = slot;
                if (zone == FormationZone.Back && slot.SlotIndex >= 4)
                {
                    var origIdx = slot.SlotIndex - 4;
                    var origSlots = Phase2RecognitionRegions
                        .BackCharacterSlots1920(backSlotCount);
                    if (origIdx >= 0 && origIdx < origSlots.Count)
                    {
                        equipmentOwner = slot with
                        {
                            ReferenceBounds = origSlots[origIdx]
                        };
                    }
                }

                var equipment = isOnField
                    ? RecognizeEquipmentSlots(equipmentOwner, zone, id, pending)
                    : (EquipmentIds: Array.Empty<string>(),
                       Slots: Array.Empty<CharacterEquipmentSlotState>());
                // 特殊装备同时最多一件。视觉相同的内部版本保留为候选组，
                // 不把候选 ID 伪装成多件已装备物品。
                var specialEquipment = isOnField
                    ? RecognizeSpecialEquipment(
                        frame,
                        equipmentOwner,
                        zone,
                        id,
                        evidence,
                        pending)
                    : null;
                var specialEquipmentIds = specialEquipment is
                    { Occupancy: EquipmentSlotOccupancy.Equipped,
                      EquipmentId: not null }
                    ? new[] { specialEquipment.EquipmentId }
                    : [];
                target.Add(new FormationCharacterState(
                    zone,
                    slot.SlotIndex,
                    id,
                    slot.StarLevel,
                    _standingByCharacterId.GetValueOrDefault(id, "unknown"),
                    equipment.EquipmentIds,
                    slot.Confidence,
                    evidence with
                    {
                        Locator = $"vision:formation:{zone}:{slot.SlotIndex}",
                        Summary = slot.DisplayName
                    },
                    // 2026-08-19 A45：CardRegion 用装备识别的 owner 坐标。
                    // 后台槽在 warped 帧识别，slot.ReferenceBounds 是 warped
                    // 坐标系（probe 实测卡位置 (0,0)），而装备识别已用
                    // equipmentOwner（Back 时覆盖为原帧 BackCharacterSlots1920）
                    // 坐标。CardRegion 是可视化/报告定位用，必须用原帧坐标，
                    // 否则后台角色位置显示在 (0,0)。
                    CardRegion: ToRelative(equipmentOwner.ReferenceBounds),
                    EquipmentSlots: equipment.Slots,
                    // 打call特效检测（用户 2026-08-07）：仅当形成内有后台/
                    // 备战席开拓者（欢愉形态）时启用——应援棒在卡牌左右两侧
                    // 边缘（左冰蓝+右暖黄发光棒），仅备战页存在。
                    IsCheered: cheeredSlotKey is not null &&
                        string.Equals(
                            cheeredSlotKey,
                            $"{zone}:{slot.SlotIndex}",
                            StringComparison.Ordinal),
                    // 猎星人标记（星核猎手羁绊机制，非装备）：卡牌左上角
                    // 金色徽章（米黄十字形凸起+中心四角星）。独立于打call
                    // 检测，任何角色都可能成为猎星人（用户 2026-08-07）。
                    // 2026-08-19「S5 猎星人 zone 约束」：用户规则——后台(Back)
                    // 角色也可能是猎星人（正确），但备战席(Bench)不可能标猎星人
                    //（备战席无竞技羁绊生效）。实测阿格莱雅(金色系卡面)左上角
                    // 金色像素 36.8%>30% 被误判猎星人，且出现在备战席。故
                    // 2026-08-19「猎星人三强校验」：改由预收集的 hunterStarSlotKey
                    // 决定——仅星核猎手羁绊成员 + 场上星核猎手≥2(羁绊激活) + 全场
                    // 唯一(最多1个,多候选全不显示)；备战席(Bench)永不。
                    IsHunterStar: hunterStarSlotKey is not null &&
                        hunterStarSlotKey == $"{zone}:{slot.SlotIndex}",
                    SpecialEquipmentIds: specialEquipmentIds,
                    SpecialEquipment: specialEquipment,
                    CurrentCost: slot.CurrentCost));
            }
        }

        (IReadOnlyList<string> EquipmentIds,
            IReadOnlyList<CharacterEquipmentSlotState> Slots)
            RecognizeEquipmentSlots(
                CharacterCardSlotRecognition owner,
                FormationZone zone,
                string ownerId,
                ICollection<PendingIconObservation> pendingTarget)
        {
            // 装备识别（2026-08-10 重做）：装备槽是居中对称布局（1件居中/
            // 2件对称/3件等距），固定 3 槽会把横跨槽边界的图标误判成多件
            //（爻光 2 件被识别成 3 件）。方案：固定 3 槽识别 + 相邻槽
            // 前景连续性合并（横跨边界的图标合并为 1 件），数量自适应。
            // 装备识别（2026-08-10 v7，用户方案）：备战页背景是蓝色，装备
            // 图标是非蓝彩色——检测装备带内"非蓝色段"即图标位置（数个数），
            // 每段裁剪做模板匹配（种类）。1件居中/2件对称/3件等距自适应。
            // 装备识别最终版（2026-08-20 用户方案：固定位置逐槽匹配 + conf 过滤，
            // 段边界作为互补第二路）：
            //   · 段边界（"非蓝段"检测）：对非蓝/彩色图标准确，但**蓝色系装备图标
            //     （如缇宝追逐星尘 080，与蓝背景难分）漏检**——011854 缇宝 3 件
            //     只数出 2 段、追逐星尘 conf0.00。
            //   · 固定 3 件坐标（0.03/0.39/0.72(背 0.03/0.34/0.68)、槽宽 0.26）：
            //     逐槽 dHash 对实机图标位置稳定，缇宝 3 件全命中（075/080/075）——
            //     但单独用会漏段检测能检的中间件（阿格莱雅 051 固定 0.39 槽 conf 低）。
            //   双路并集 + conf≥0.90 + 按 x 中心去重 → 缇宝(固定路 080)与
            //   阿格莱雅(段路 051)都不漏，1/0 件角色不误检（探针实测 011854 全角色）。
            var band = Phase2RecognitionRegions.CharacterEquipmentIconBand(
                owner.ReferenceBounds);
            var segPixel = band.ToPixels(frame.Width, frame.Height);
            var segList = LocateEquipmentSegments(frame, segPixel);
            segList = MergeAdjacentSegments(segList);
            // 段边界 slot（定位非蓝段真实位置）
            var segSlots = BuildEquipmentSlotsFromSegments(
                owner.ReferenceBounds, segList, frame.Width, frame.Height);
            // 装备槽坐标：用户 2026-08-21 三文档标定四角（FrontEquip/BackEquip，
            // 1/2/3 件套）。程序先对这卡所有 1/2/3 套四角做正交投影 dHash，判定实有几件
            // 装备（conf≥0.90 去重），再选用对应套，有几件报几件、不补空槽。绝不再
            // min/max 压正方形、不再相对比例推导、不再固定 3 槽补空（历史遗留）。
            int calibCard = zone == FormationZone.Back
                ? owner.SlotIndex - 4
                : owner.SlotIndex;
            var quadCandidates = new List<int[][]>();
            for (var ec = 1; ec <= 3; ec++)
            {
                int[][][][]? set = zone == FormationZone.Back
                    ? (CalibrationSlots.BackEquip.TryGetValue(
                        (ec, Math.Clamp(backSlotCount, 6, 9)), out var bs) ? bs : null)
                    : (CalibrationSlots.FrontEquip.TryGetValue(ec, out var fs) ? fs : null);
                if (set is null || calibCard < 0 || calibCard >= set.Length)
                {
                    continue;
                }
                quadCandidates.AddRange(set[calibCard]);
            }
            if (quadCandidates.Count == 0)
            {
                // 该卡无任何标定四角：返回恒 3 空槽，兼容显示层（与无装备分支一致）。
                var emptyCand = new List<CharacterEquipmentSlotState>();
                for (var pi = 0; pi < 3; pi++)
                {
                    emptyCand.Add(new CharacterEquipmentSlotState(
                        pi,
                        EquipmentSlotOccupancy.Empty,
                        null,
                        [],
                        0,
                        new RelativeRegion(0, 0, 0, 0),
                        evidence with
                        {
                            Locator = $"crop:advanced-equipment:{zone}:{owner.SlotIndex + 1}:{pi + 1}",
                            Confidence = 0
                        }));
                }
                return (Array.Empty<string>(), emptyCand);
            }
            // 正交投影到目标尺寸：取四角参考系 AABB 放大到实机清晰度（dHash 对
            // 缩放免疫，尺寸只须覆盖图标；固定 52×52 近似角色 111×127 的比例精神）。
            var warpW = 52;
            var warpH = 52;

            // 双路逐槽 dHash，conf≥阈值且 IsKnown 时收集（按帧像素 x 中心）。
            const double slotConfidenceThreshold = 0.90;
            var matched = new List<(double CxPx, double Conf, Phase2IconRecognition Res)>();
            foreach (var quad in quadCandidates)
            {
                try
                {
                    using var warp = CalibratedPerspective.WarpOneQuad(
                        frame.BgraPixels, frame.Width, frame.Height,
                        quad, warpW, warpH, sourceReuse: null);
                    var warpPx = new byte[checked(warpW * 4 * warpH)];
                    for (var row = 0; row < warpH; row++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            warp.Ptr(row), warpPx, row * warpW * 4, warpW * 4);
                    }
                    var slotFrame = new CaptureFrame(
                        warpW, warpH, warpW * 4, warpPx,
                        new PixelRect(0, 0, warpW, warpH), frame.CapturedAt);
                    var res = iconRecognizer.RecognizeEquipmentSlotByDHash(
                        slotFrame, "advanced-equipment",
                        new NormalizedRect(0, 0, 1, 1), iconTemplates);
                    if (res.IsKnown && res.TemplateId is not null &&
                        res.Confidence >= slotConfidenceThreshold)
                    {
                        var xs = quad.Select(p => p[0]).ToArray();
                        var ys = quad.Select(p => p[1]).ToArray();
                        var cxRef = (xs.Min() + xs.Max()) / 2d;
                        var px = cxRef * frame.Width / 1920d;
                        matched.Add((px, res.Confidence, res));
                    }
                }
                catch (Exception)
                {
                    // 模板缺失/解码失败时降级跳过该槽，不让单槽异常拖垮整帧。
                }
            }

            // 按 x 中心排序、相邻(≤30px)去重（同一图标出现于段边界与固定两路时，
            // 保留 conf 更高者）。
            var ordered = matched.OrderBy(m => m.CxPx).ToList();
            var dedup = new List<(double CxPx, double Conf, Phase2IconRecognition Res)>();
            foreach (var h in ordered)
            {
                if (dedup.Count > 0)
                {
                    var last = dedup[^1];
                    if (Math.Abs(h.CxPx - last.CxPx) <= 30)
                    {
                        if (h.Conf > last.Conf)
                        {
                            dedup[^1] = (h.CxPx, h.Conf, h.Res);
                        }
                        continue;
                    }
                }
                dedup.Add((h.CxPx, h.Conf, h.Res));
            }
                $"segSegments={segList.Count} segSlots={segSlots.Count} cand={quadCandidates.Count} " +
                $"matched={matched.Count}({string.Join(",", matched.Select(m => $"{m.Res.TemplateId}:{m.Res.Confidence:F2}@{(int)m.CxPx}"))}) " +
                $"final={dedup.Count}");
            // 判定装备数量 = conf≥0.90 去重后的槽数；据其选用三文档对应套
            // （FrontEquip[count] / BackEquip[(count,pop)]），按该套四角映射，几件报几件。
            var count = dedup.Count == 0 ? 0 : Math.Min(dedup.Count, 3);
            int[][][][]? chosenSet = zone == FormationZone.Back
                ? (CalibrationSlots.BackEquip.TryGetValue(
                    (count, Math.Clamp(backSlotCount, 6, 9)), out var cs2) ? cs2 : null)
                : (CalibrationSlots.FrontEquip.TryGetValue(count, out var cs1) ? cs1 : null);
            int[][][] chosenQuads = chosenSet is null ||
                calibCard < 0 || calibCard >= chosenSet.Length
                ? []
                : chosenSet[calibCard];
            if (chosenQuads.Length == 0)
            {
                // 无装备：返回恒 3 槽（全空），兼容显示层。识别不改，仅输出适配。
                var empty3 = new List<CharacterEquipmentSlotState>();
                for (var pi = 0; pi < 3; pi++)
                {
                    empty3.Add(new CharacterEquipmentSlotState(
                        pi,
                        EquipmentSlotOccupancy.Empty,
                        null,
                        [],
                        0,
                        new RelativeRegion(0, 0, 0, 0),
                        evidence with
                        {
                            Locator = $"crop:advanced-equipment:{zone}:{owner.SlotIndex + 1}:{pi + 1}",
                            Confidence = 0
                        }));
                }
                return (Array.Empty<string>(), empty3);
            }
            var chosenCentersPx = new double[chosenQuads.Length];
            for (var i = 0; i < chosenQuads.Length; i++)
            {
                var xs = chosenQuads[i].Select(p => p[0]).ToArray();
                chosenCentersPx[i] = (xs.Min() + xs.Max()) / 2d * frame.Width / 1920d;
            }
            // 每个判定槽位最贴合的识别（同槽多个取 conf 高者）。
            var slotRes = new Phase2IconRecognition?[chosenQuads.Length];
            foreach (var h in dedup)
            {
                var best = 0;
                var bestDist = double.MaxValue;
                for (var i = 0; i < chosenCentersPx.Length; i++)
                {
                    var d = Math.Abs(h.CxPx - chosenCentersPx[i]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = i;
                    }
                }
                if (slotRes[best] is null || h.Conf > slotRes[best]!.Confidence)
                {
                    slotRes[best] = h.Res;
                }
            }

            var slotStates = new List<CharacterEquipmentSlotState>(chosenQuads.Length);
            var equipmentIds = new List<string>(chosenQuads.Length);
            for (var index = 0; index < chosenQuads.Length; index++)
            {
                var item = slotRes[index];
                var locIndex = owner.SlotIndex + 1;
                if (item is null)
                {
                    // 空槽位：补 Empty（槽位骨架=用户判定套四角，缺件即空槽）。
                    var qx = chosenQuads[index].Select(p => p[0]).ToArray();
                    var qy = chosenQuads[index].Select(p => p[1]).ToArray();
                    var emptyRegion = new RelativeRegion(
                        qx.Min() / 1920d, qy.Min() / 1080d,
                        (qx.Max() - qx.Min()) / 1920d, (qy.Max() - qy.Min()) / 1080d);
                    slotStates.Add(new CharacterEquipmentSlotState(
                        index,
                        EquipmentSlotOccupancy.Empty,
                        null,
                        [],
                        0,
                        emptyRegion,
                        evidence with
                        {
                            Locator = $"crop:advanced-equipment:{zone}:{locIndex}:{index + 1}",
                            Confidence = 0
                        }));
                    continue;
                }

                var relativeRegion = ToFrameRelative(item.Region, frame);
                if (item.IsKnown && item.TemplateId is not null)
                {
                    equipmentIds.Add(item.TemplateId);
                    slotStates.Add(new CharacterEquipmentSlotState(
                        index,
                        EquipmentSlotOccupancy.Equipped,
                        item.TemplateId,
                        item.CandidateTemplateIds ?? [],
                        item.Confidence,
                        relativeRegion,
                        evidence with
                        {
                            Locator = $"crop:advanced-equipment:{zone}:{locIndex}:{index + 1}",
                            Confidence = item.Confidence
                        }));
                    continue;
                }

                var candidates = item.CandidateTemplateIds ?? [];
                var temporaryId =
                    $"unknown-equipment-{zone}-{owner.SlotIndex + 1}-{index + 1}";
                var failureReason = candidates.Count > 1
                    ? "装备图标存在多个近似候选，未强行匹配"
                    : "装备槽有内容，但图标未达到可靠识别阈值";
                slotStates.Add(new CharacterEquipmentSlotState(
                    index,
                    EquipmentSlotOccupancy.Unknown,
                    null,
                    candidates,
                    item.Confidence,
                    relativeRegion,
                    evidence with
                    {
                        Locator = $"crop:advanced-equipment:{zone}:{locIndex}:{index + 1}",
                        Confidence = item.Confidence
                    },
                    failureReason,
                    false));
                pendingTarget.Add(new PendingIconObservation(
                    PendingIconCategory.AdvancedEquipment,
                    $"{zone}-{owner.SlotIndex + 1}-equipment-{index + 1}",
                    relativeRegion,
                    item.TemplateId,
                    item.Confidence,
                    evidence with
                    {
                        Locator = $"crop:advanced-equipment:{zone}:{locIndex}:{index + 1}",
                        Confidence = item.Confidence
                    },
                    candidates.Count > 1
                        ? "ambiguous-visual-identity"
                        : "unresolved",
                    candidates,
                    temporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ownerCharacterId"] = ownerId,
                        ["zone"] = zone.ToString(),
                        ["slotIndex"] = owner.SlotIndex.ToString(
                            CultureInfo.InvariantCulture),
                        ["equipmentIndex"] = index.ToString(
                            CultureInfo.InvariantCulture)
                    },
                    false));
            }

            // 输出适配（用户 2026-08-21 方案：识别不改，仅补足槽列表）：判定 0/1/2 件
            // 时分别补 3/2/1 个空槽，保证恒为 3 槽（HTML/显示层无需改动，兼容旧契约）。
            var locBase = owner.SlotIndex + 1;
            while (slotStates.Count < 3)
            {
                var padIndex = slotStates.Count;
                slotStates.Add(new CharacterEquipmentSlotState(
                    padIndex,
                    EquipmentSlotOccupancy.Empty,
                    null,
                    [],
                    0,
                    new RelativeRegion(0, 0, 0, 0),
                    evidence with
                    {
                        Locator = $"crop:advanced-equipment:{zone}:{locBase}:{padIndex + 1}",
                        Confidence = 0
                    }));
            }

            return (equipmentIds, slotStates);
        }
    }

    private static Observation<IReadOnlyList<FormationCharacterState>>
        AppendHorizontalSpecialUnits(
            Observation<IReadOnlyList<FormationCharacterState>> formation,
            IReadOnlyList<HorizontalSpecialUnitRecognition> recognitions,
            EvidenceReference evidence,
            DateTimeOffset observedAt)
    {
        if (recognitions.Count == 0)
        {
            return formation;
        }

        var specialStates = recognitions
            .Select((item, index) => new FormationCharacterState(
                FormationZone.Special,
                index,
                item.SpecialUnitId,
                null,
                "special-unit",
                [],
                item.Confidence,
                evidence with
                {
                    Locator = $"vision:formation:Special:{index}",
                    Summary = item.DisplayName,
                    Confidence = item.Confidence
                },
                TemporaryId: $"special-formation-unit-Special-{index + 1}",
                CandidateCharacterIds: [item.SpecialUnitId],
                FailureReason: "横向特殊单位独立识别；不按普通角色驱动决策。",
                CanDriveDecisions: false,
                CardRegion: ToRelative(item.ReferenceBounds),
                EquipmentSlots: []))
            .ToArray();
        var specialEvidence = specialStates
            .Select(item => item.Evidence)
            .ToArray();

        return formation with
        {
            Value = (formation.Value ?? []).Concat(specialStates).ToArray(),
            Evidence = formation.Evidence.Concat(specialEvidence).ToArray(),
            ObservedAt = formation.ObservedAt ?? observedAt
        };
    }

    private static string? ResolveEquipmentIdentityByPrivilegeMarker(
        Phase2IconRecognition recognition,
        bool isPrivileged)
    {
        var candidateIds = recognition.CandidateTemplateIds;
        var displayNames = recognition.CandidateDisplayNames;
        if (candidateIds is not { Count: 2 } ||
            displayNames is not { Count: 2 })
        {
            return null;
        }

        var privilegedIndex = displayNames
            .Select((name, index) => (name, index))
            .Where(item => item.name.EndsWith(
                "•特权",
                StringComparison.Ordinal))
            .Select(item => item.index)
            .SingleOrDefault(-1);
        var standardIndex = displayNames
            .Select((name, index) => (name, index))
            .Where(item => !item.name.EndsWith(
                "•特权",
                StringComparison.Ordinal))
            .Select(item => item.index)
            .SingleOrDefault(-1);
        if (privilegedIndex < 0 || standardIndex < 0)
        {
            return null;
        }

        return isPrivileged
            ? candidateIds[privilegedIndex]
            : candidateIds[standardIndex];
    }

    internal static bool DetectPrivilegeEquipmentOverlay(
        CaptureFrame frame,
        NormalizedRect region)
    {
        var bounds = region.ToPixels(frame.Width, frame.Height);
        if (bounds.IsEmpty)
        {
            return false;
        }

        // Real privilege artwork keeps the normal icon identity and adds two
        // independent signals: a cyan/blue frame and a white/blue V. Locked
        // gallery entries are dimmed, so the checks use colour relationships
        // instead of a fixed brightness threshold.
        var borderThickness = Math.Max(
            1,
            Math.Min(bounds.Width, bounds.Height) / 18);
        var cyanBorderPixels = 0;
        var borderPixels = 0;
        for (var y = bounds.Y; y < bounds.Bottom; y++)
        {
            var rowOffset = y * frame.Stride;
            for (var x = bounds.X; x < bounds.Right; x++)
            {
                if (x >= bounds.X + borderThickness &&
                    x < bounds.Right - borderThickness &&
                    y >= bounds.Y + borderThickness &&
                    y < bounds.Bottom - borderThickness)
                {
                    continue;
                }

                borderPixels++;
                var offset = rowOffset + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                if (blue >= 55 && green >= 45 &&
                    Math.Min(blue, green) >= red + 25)
                {
                    cyanBorderPixels++;
                }
            }
        }

        var hasPrivilegeFrame = cyanBorderPixels >= Math.Max(
            3,
            (int)Math.Ceiling(borderPixels * 0.08));
        if (!hasPrivilegeFrame)
        {
            return false;
        }

        return HasPrivilegeV(frame, bounds, rightAligned: true) ||
               HasPrivilegeV(frame, bounds, rightAligned: false);
    }

    private static bool HasPrivilegeV(
        CaptureFrame frame,
        PixelRect bounds,
        bool rightAligned)
    {
        var markerX = rightAligned
            ? bounds.X + bounds.Width * 3 / 5
            : bounds.X;
        var marker = new PixelRect(
            markerX,
            bounds.Y + bounds.Height * 3 / 5,
            Math.Max(1, bounds.Width * 2 / 5),
            Math.Max(1, bounds.Height * 2 / 5));
        var firstArm = 0;
        var secondArm = 0;
        for (var y = marker.Y; y < marker.Bottom; y++)
        {
            var v = (y - marker.Y) / (double)Math.Max(1, marker.Height - 1);
            var rowOffset = y * frame.Stride;
            for (var x = marker.X; x < marker.Right; x++)
            {
                var offset = rowOffset + x * 4;
                var blue = frame.BgraPixels[offset];
                var green = frame.BgraPixels[offset + 1];
                var red = frame.BgraPixels[offset + 2];
                var maximum = Math.Max(red, Math.Max(green, blue));
                var minimum = Math.Min(red, Math.Min(green, blue));
                var isWhiteOrBlueMarker =
                    (maximum >= 80 && maximum - minimum <= 55) ||
                    (blue >= 85 && blue >= red + 25 && green >= red + 10);
                if (!isWhiteOrBlueMarker)
                {
                    continue;
                }

                var u = (x - marker.X) / (double)Math.Max(1, marker.Width - 1);
                if (u <= 0.58 && Math.Abs(v - (0.10 + u * 1.45)) <= 0.18)
                {
                    firstArm++;
                }
                if (u >= 0.42 && Math.Abs(v - (1.55 - u * 1.45)) <= 0.18)
                {
                    secondArm++;
                }
            }
        }

        var minimumArmPixels = Math.Max(
            2,
            (int)Math.Ceiling(marker.Width * marker.Height * 0.012));
        return firstArm >= minimumArmPixels && secondArm >= minimumArmPixels;
    }

    /// <summary>
    private static IReadOnlyList<NormalizedRect> BuildEquipmentSlotsByCount(
        PixelRect referenceCharacterBounds,
        int count,
        bool compactFrontLayout,
        bool isBack = false)
    {
        if (count <= 0)
        {
            return [];
        }

        var cardX = referenceCharacterBounds.X;
        var cardW = referenceCharacterBounds.Width;
        var center = cardX + cardW / 2.0;
        var slotWidth = compactFrontLayout
            ? cardW * 0.34
            : cardW * 0.34;
        // y 对齐 CharacterEquipmentSlots（0.96H，高 0.32H——爻光匹配验证）
        var slotY = referenceCharacterBounds.Y +
                    referenceCharacterBounds.Height * 0.96;
        var slotH = referenceCharacterBounds.Height * 0.32;
        // 3 件：2026-08-10 用户实机确认坐标（034916 图，2559 系→1920 系）：
        // 装备带紧贴卡面底（原 0.96H 错位——实际 y 中心 = 卡底+0.08 卡高，
        // 非卡面上方）；3 图标 x 起点 = 卡x+0.03/0.33/0.64 卡宽、宽 0.28 卡宽；
        // y 中心 = 卡y+1.08 卡高、高 0.26 卡高。
        if (count >= 3)
        {
            // 2026-08-10 校准：Front 3 件 x 起点 0.03/0.39/0.75 卡宽
            //（丹恒实测 0.039/0.389/0.746 吻合）；Back 3 件用实测
            // 0.03/0.34/0.68（Back4 藿藿实测 0.027/0.338/0.679——
            // Back 卡实际比槽位窄（130→~120）且偏右，原 0.39/0.75
            // 第 2/3 件框偏右 8/12px，第 3 件切掉量产型装甲左缘
            // 22% → 047 误配 062（用户实机确认装备=072/074/047）。
            // 前后台图标尺寸实测相同，仅槽位参考框不同）。
            double[] startFractions = isBack
                ? new[] { 0.03, 0.34, 0.68 }
                : new[] { 0.03, 0.39, 0.75 };
            return startFractions.Select(fraction =>
            {
                var xStart = referenceCharacterBounds.X +
                             referenceCharacterBounds.Width * fraction;
                // 槽宽 0.33 卡宽（用户确认图标宽 53px@160 卡宽 ≈ 0.33）：
                // 0.28 太窄会切掉图标右侧（142→124 误配实证）
                var slotW = referenceCharacterBounds.Width * 0.33;
                var yCenter = referenceCharacterBounds.Y +
                              referenceCharacterBounds.Height * 1.08;
                var slotH = referenceCharacterBounds.Height * 0.26;
                return new NormalizedRect(
                    xStart / 1920d,
                    (yCenter - slotH / 2) / 1080d,
                    slotW / 1920d,
                    slotH / 1080d);
            }).ToArray();
        }

        // 1 件 = 卡中心（用户 2026-08-10 定稿：装备图标居中布局，1 件
        // = 卡中心偏移 0；原套用 3 件中间槽 x0.39 卡宽——卡宽 120/128
        // 不同时比例坐标误差不同，爻光图3（卡宽128）框偏右 14px 切掉
        // 量产型装甲左缘 → 误配步步生花。改卡中心后实测 152 vs 195 正确）。
        if (count == 1)
        {
            var singleCenterX = referenceCharacterBounds.X +
                                referenceCharacterBounds.Width / 2.0;
            var singleW = referenceCharacterBounds.Width * 0.33;
            var singleYC = referenceCharacterBounds.Y +
                           referenceCharacterBounds.Height * 1.08;
            var singleH = referenceCharacterBounds.Height * 0.26;
            return
            [
                new NormalizedRect(
                    (singleCenterX - singleW / 2) / 1920d,
                    (singleYC - singleH / 2) / 1080d,
                    singleW / 1920d,
                    singleH / 1080d)
            ];
        }

        // 2 件 = 卡中心 ± 固定像素偏移（用户 2026-08-10 定稿：图标大小
        // 三种情况相同，2 件对称布局——实测 Front2/Back7 藿藿偏移
        // −20/+23px（1920 系），用 ±21px 固定值。原 0.17/0.53 卡宽比例
        // 在卡宽 120 时 −21.6/+21.6px 恰好吻合（Front2 对），但卡宽
        // 130 时 −23.4/+23.4 略偏（Back7 仍对）——统一固定像素更稳）。
        if (count == 2)
        {
            var slotW2 = referenceCharacterBounds.Width * 0.30;
            var yCenter2 = referenceCharacterBounds.Y +
                           referenceCharacterBounds.Height * 1.08;
            var slotH2 = referenceCharacterBounds.Height * 0.26;
            var cardCenterX = referenceCharacterBounds.X +
                              referenceCharacterBounds.Width / 2.0;
            double[] offsets = { -21, 21 };
            return offsets.Select(offset =>
            {
                var xCenter = cardCenterX + offset;
                return new NormalizedRect(
                    (xCenter - slotW2 / 2) / 1920d,
                    (yCenter2 - slotH2 / 2) / 1080d,
                    slotW2 / 1920d,
                    slotH2 / 1080d);
            }).ToArray();
        }

        var centers = count switch
        {
            _ => new[] { center - cardW * 0.17, center + cardW * 0.17 },
        };

        return centers.Select(cx => new NormalizedRect(
                (cx - slotWidth / 2) / 1920d,
                slotY / 1080d,
                slotWidth / 1920d,
                slotH / 1080d))
            .ToArray();
    }

    internal static IReadOnlyList<NormalizedRect> BuildEquipmentSlotsFromSegments(
        PixelRect referenceCharacterBounds,
        IReadOnlyList<PixelRect> segments,
        int frameWidth,
        int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (frameWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        }

        if (frameHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameHeight));
        }

        var scaleX = frameWidth / 1920d;
        // 2026-08-16 修复（验收实锤）：padding 4px 把图标周边背景混入
        // 裁剪，近邻模板（金垃圾袋 039）优势扩大反超真值（幸运星 043：
        // 段 43=87 胜 vs 产品槽位 039=89 胜）。收窄到 1px。
        var horizontalPadding = Math.Max(1, (int)Math.Round(1 * scaleX));

        // 2026-08-16 修复（验收实锤）：槽位 y 直接用段中心——旧固定公式
        //（referenceCharacterBounds.Y + Height*1.08）与装备带实际位置错开
        // 约 290px，且 y 归一化分母误用 1080（应为 frameHeight）——匹配
        // 区域落在卡牌下方空白 → 噪声误配金垃圾袋（幸运星场景）、真装备
        //（生命之环/乱破 3 件）全部漏配。段检测已定位真实图标位置，槽位
        // 与段对齐（dHash 距离诊断：段上真值距离 74-97 全部在阈值内）。
        return segments
            .Take(3)
            .Select(segment =>
            {
                // 2026-08-16 修复（验收实锤）：standardWidth 下限（约 57px）
                // 把槽位撑得比段宽、混入背景，近邻模板反超（幸运星被
                // 金垃圾袋反超）。段本身就是图标包围盒，宽度直接用
                // 段宽 + 1px padding。
                var width = Math.Min(
                    frameWidth,
                    segment.Width + horizontalPadding * 2);
                var centerX = segment.X + segment.Width / 2d;
                var x = Math.Clamp(
                    (int)Math.Round(centerX - width / 2d),
                    0,
                    frameWidth - width);
                var slotHeight = Math.Max(
                    8,
                    segment.Height + horizontalPadding * 2);
                var centerY = segment.Y + segment.Height / 2d;
                var y = Math.Clamp(
                    (int)Math.Round(centerY - slotHeight / 2d),
                    0,
                    frameHeight - slotHeight);
                return new NormalizedRect(
                    x / (double)frameWidth,
                    y / (double)frameHeight,
                    width / (double)frameWidth,
                    slotHeight / (double)frameHeight);
            })
            .ToArray();
    }

    /// <summary>
    /// 装备个数检测（2026-08-10 用户方案）：备战页背景蓝色，装备图标是
    /// 非蓝彩色——统计装备带内"非蓝色列段"= 图标位置与个数（1件居中/
    /// 2件对称/3件等距自适应）。比彩色块检测可靠（蓝色背景是明确参照）。
    /// </summary>

    private static IReadOnlyList<PixelRect> LocateEquipmentSegments(
        CaptureFrame frame,
        PixelRect band)
    {
        var segments = new List<PixelRect>();
        var height = band.Height;
        if (height < 8 || band.Width < 16)
        {
            return segments;
        }
        // 每列"非蓝"像素数（装备图标列 vs 蓝色背景）
        var nonBlueCounts = new int[band.Width];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = (band.Y + y) * frame.Stride + band.X * 4;
            for (var x = 0; x < band.Width; x++)
            {
                var offset = rowOffset + x * 4;
                var b = frame.BgraPixels[offset];
                var g = frame.BgraPixels[offset + 1];
                var r = frame.BgraPixels[offset + 2];
                // 蓝色背景：B 明显 > R 且 > G
                var isBlue = b > r + 25 && b > g + 10 && b > 70;
                if (!isBlue)
                {
                    nonBlueCounts[x]++;
                }
            }
        }

        var threshold = Math.Max(2, height * 0.30);
        // 每列非蓝像素的 y 范围（图标实际 y——比固定槽 y 精确，避免切顶部）
        var colTop = new int[band.Width];
        var colBottom = new int[band.Width];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = (band.Y + y) * frame.Stride + band.X * 4;
            for (var x = 0; x < band.Width; x++)
            {
                if (nonBlueCounts[x] == 0)
                {
                    continue;
                }

                var offset = rowOffset + x * 4;
                var b = frame.BgraPixels[offset];
                var g = frame.BgraPixels[offset + 1];
                var r = frame.BgraPixels[offset + 2];
                var isBlue = b > r + 25 && b > g + 10 && b > 70;
                if (!isBlue)
                {
                    if (colTop[x] == 0)
                    {
                        colTop[x] = y + 1;
                    }

                    colBottom[x] = y + 1;
                }
            }
        }

        // 非蓝段（连续列非蓝占比超阈值）——y 用段内非蓝像素范围
        var inSegment = false;
        var start = 0;
        var segTop = int.MaxValue;
        var segBottom = 0;
        for (var x = 0; x < band.Width; x++)
        {
            var nonBlue = nonBlueCounts[x] >= threshold;
            if (nonBlue && !inSegment)
            {
                start = x;
                segTop = colTop[x];
                segBottom = colBottom[x];
                inSegment = true;
            }
            else if (nonBlue && inSegment)
            {
                if (colTop[x] > 0 && colTop[x] < segTop)
                {
                    segTop = colTop[x];
                }

                if (colBottom[x] > segBottom)
                {
                    segBottom = colBottom[x];
                }
            }
            else if (!nonBlue && inSegment)
            {
                var segWidth = x - start;
                var segHeight = segBottom - segTop + 1;
                // 过滤假段（review 2026-08-10）：卡底白框/深色边缘的非蓝段
                // 过宽（整条横线）或过高（卡底大块）——图标段宽 15-80px、
                // 高 8-55px（@1920，band 内像素按帧缩放），排除卡底干扰
                if (segWidth >= 8 && segTop <= segBottom &&
                    segWidth <= 80 * frame.Width / 1920d &&
                    segHeight <= 55 * frame.Height / 1080d)
                {
                    segments.Add(new PixelRect(
                        band.X + start,
                        band.Y + segTop - 1,
                        segWidth,
                        segHeight));
                }

                inSegment = false;
            }
        }

        if (inSegment && band.Width - start >= 8 && segTop <= segBottom &&
            band.Width - start <= 80 * frame.Width / 1920d &&
            segBottom - segTop + 1 <= 55 * frame.Height / 1080d)
        {
            segments.Add(new PixelRect(
                band.X + start,
                band.Y + segTop - 1,
                band.Width - start,
                segBottom - segTop + 1));
        }

        return segments;
    }

    // 2026-08-10：合并单件装备被内部蓝线切成的窄段（间隙<25px 且合并宽
    // <60px——爻光 1 件切成 x1548+16/x1584+16（间隙 20px）与 x960+17/
    // x989+23（间隙 12px），原阈值 12 均不合并→误 2 件，阈值 12→25 后
    // 合并为 1 件（合并宽 52<60）；藿藿真 2 件 53/54 合并宽 107>60、
    // 丹恒 3 件 >60 均不误合并）。@2559 系像素。

    private static List<PixelRect> MergeAdjacentSegments(
        IReadOnlyList<PixelRect> segments)
    {
        if (segments.Count <= 1)
        {
            return segments.ToList();
        }

        var result = new List<PixelRect>(segments.Count);
        var current = segments[0];
        for (var index = 1; index < segments.Count; index++)
        {
            var gap = segments[index].X - (current.X + current.Width);
            var mergedWidth = (segments[index].X + segments[index].Width) - current.X;
            if (gap < 25 && mergedWidth < 60)
            {
                current = new PixelRect(
                    current.X,
                    current.Y,
                    mergedWidth,
                    Math.Max(current.Height, segments[index].Height));
            }
            else
            {
                result.Add(current);
                current = segments[index];
            }
        }

        result.Add(current);
        return result;
    }

    /// <summary>
    /// 装备图标像素粗定位 v2（2026-08-10）：固定 y 图标带（卡内 0.93-1.06H，
    /// 实测装备图标 y 位置稳定），带内找彩色连通域定位 x 中心，bbox 用
    /// 固定高度 + 图标标准宽度（模板匹配需要完整图标含边框）。
    /// </summary>
    private static IReadOnlyList<PixelRect> LocateEquipmentIcons(
        CaptureFrame frame,
        PixelRect band)
    {
        var icons = new List<PixelRect>();
        var visited = new bool[band.Height, band.Width];
        for (var y = 0; y < band.Height; y++)
        {
            var rowOffset = (band.Y + y) * frame.Stride + band.X * 4;
            for (var x = 0; x < band.Width; x++)
            {
                if (visited[y, x])
                {
                    continue;
                }

                var offset = rowOffset + x * 4;
                var b = frame.BgraPixels[offset];
                var g = frame.BgraPixels[offset + 1];
                var r = frame.BgraPixels[offset + 2];
                var sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                // 2026-08-10 v3：sat 阈值 60→25（绿色系装备图标饱和度低），
                // 配合 y 带内过滤 + 域内分裂
                if (sat < 25)
                {
                    continue;
                }

                // BFS 8 邻域（彩色连通域）
                var queue = new Queue<(int Y, int X)>();
                queue.Enqueue((y, x));
                visited[y, x] = true;
                var minX = x; var maxX = x;
                var minY = y; var maxY = y;
                var count = 0;
                while (queue.Count > 0)
                {
                    var (cy, cx) = queue.Dequeue();
                    count++;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
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
                            if (ny < 0 || ny >= band.Height ||
                                nx < 0 || nx >= band.Width ||
                                visited[ny, nx])
                            {
                                continue;
                            }

                            var no = (band.Y + ny) * frame.Stride + (band.X + nx) * 4;
                            var nb = frame.BgraPixels[no];
                            var ng = frame.BgraPixels[no + 1];
                            var nr = frame.BgraPixels[no + 2];
                            if (Math.Max(nr, Math.Max(ng, nb)) -
                                Math.Min(nr, Math.Min(ng, nb)) < 60)
                            {
                                continue;
                            }

                            visited[ny, nx] = true;
                            queue.Enqueue((ny, nx));
                        }
                    }
                }

                var width = maxX - minX + 1;
                var height = maxY - minY + 1;
                // 过滤：太小噪声（<10px）/ 太大（卡框彩色装饰 >85px）
                if (count < 20 || width < 10 || width > 85 ||
                    height < 8 || height > 55)
                {
                    continue;
                }

                // y 固定为图标带（band 内 45%-80%——实测装备图标 y 稳定）
                var scaleX = frame.Width / 1920d;
                var scaleY = frame.Height / 1080d;
                var iconTop = band.Y + (int)Math.Round(band.Height * 0.45);
                var iconHeight = (int)Math.Round(52 * scaleY);

                // 域内列峰分裂（2026-08-10 v3）：装备居中布局 2 件时图标
                // 紧挨（间距 ~5px），彩色连通域合并成宽域——域宽 > 40px
                // 时按列峰分裂为多个图标
                var subWidth = width;
                if (subWidth > 40)
                {
                    // 收集域内每列彩色像素数
                    var colCounts = new int[width];
                    for (var dy2 = minY; dy2 <= maxY; dy2++)
                    {
                        var rowOff = (band.Y + dy2) * frame.Stride + (band.X + minX) * 4;
                        for (var dx2 = 0; dx2 < width; dx2++)
                        {
                            var no = rowOff + dx2 * 4;
                            var nb = frame.BgraPixels[no];
                            var ng = frame.BgraPixels[no + 1];
                            var nr = frame.BgraPixels[no + 2];
                            if (Math.Max(nr, Math.Max(ng, nb)) -
                                Math.Min(nr, Math.Min(ng, nb)) >= 25)
                            {
                                colCounts[dx2]++;
                            }
                        }
                    }

                    var peaks = new List<int>();
                    var last = -100;
                    for (var dx2 = 1; dx2 < width - 1; dx2++)
                    {
                        if (colCounts[dx2] >= 3 &&
                            colCounts[dx2] >= colCounts[dx2 - 1] &&
                            colCounts[dx2] >= colCounts[dx2 + 1])
                        {
                            if (dx2 - last >= 5)
                            {
                                peaks.Add(dx2);
                                last = dx2;
                            }
                        }
                    }

                    if (peaks.Count >= 2)
                    {
                        foreach (var peak in peaks)
                        {
                            var peakHalfW = Math.Max(15 * scaleX, 12);
                            var cx = minX + peak;
                            icons.Add(new PixelRect(
                                band.X + (int)Math.Round(cx - peakHalfW),
                                iconTop,
                                (int)Math.Round(peakHalfW * 2),
                                iconHeight));
                        }

                        continue;
                    }
                }

                var centerX = minX + width / 2;
                var halfW = Math.Max(15 * scaleX, 12);
                icons.Add(new PixelRect(
                    band.X + (int)Math.Round(centerX - halfW),
                    iconTop,
                    (int)Math.Round(halfW * 2),
                    iconHeight));
            }
        }

        return icons;
    }

    /// <summary>诊断用：暴露装备图标像素定位结果（测试/调试）。</summary>
    internal static IReadOnlyList<PixelRect> DebugLocateEquipmentIcons(
        CaptureFrame frame,
        PixelRect band) => LocateEquipmentIcons(frame, band);

    /// <summary>诊断用：暴露非蓝段检测结果。</summary>

    /// <summary>
    /// 2026-08-11 特殊装备可带角色判定（用户确认游戏机制）：
    /// 分身墨镜（022/023）只能银狼（05/27）戴；特殊装备只允许银狼、
    /// 姬子启行（01）、命运圣杯成员（03/04/14/33）携带——其余角色识别出
    /// 特殊装备均为误识别（大黑塔被误配墨镜实测）。
    /// </summary>
    private static bool IsSpecialEquipmentEligible(
        string ownerId,
        IReadOnlyList<string> candidates)
    {
        var isSilverWolf = ownerId is
            "currency_wars_character_05" or "currency_wars_character_27";
        var isSilverWolfOnlyComponent = candidates.Any(id => id is
            "currency_wars_equipment_022" or "currency_wars_equipment_023" or
            "currency_wars_equipment_032" or "currency_wars_equipment_033");
        if (isSilverWolfOnlyComponent && !isSilverWolf)
        {
            return false;
        }

        return isSilverWolf ||
               ownerId is
                   "currency_wars_character_01" or // 姬子•启行
                   "currency_wars_character_03" or // 吉尔伽美什（命运圣杯）
                   "currency_wars_character_04" or // 远坂凛
                   "currency_wars_character_14" or // Archer
                   "currency_wars_character_33";   // Saber
    }

    /// 特殊装备识别：卡牌左上角上方图标匹配 special-item 视觉组。
    /// 同图内部版本保持 Unknown + 候选名称；只有唯一视觉身份才能进入
    /// SpecialEquipmentIds 并驱动决策。
    /// </summary>
    private CharacterSpecialEquipmentState? RecognizeSpecialEquipment(
        CaptureFrame frame,
        CharacterCardSlotRecognition slot,
        FormationZone zone,
        string ownerId,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending)
    {
        var region = Phase2RecognitionRegions
            .CharacterSpecialEquipmentRegion(slot.ReferenceBounds);
        var icons = RecognizeIconsSafely(
            frame,
            "special-item",
            [region],
            iconTemplates);
        var item = icons.SingleOrDefault();
        if (item?.TemplateId is null)
        {
            return null;
        }

        var candidates = (item.CandidateTemplateIds ?? [item.TemplateId])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var displayNames = (item.CandidateDisplayNames ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // 2026-08-11 特殊装备规则（用户确认游戏机制）：
        // 1) 分身墨镜（022/023）只能银狼戴；
        // 2) 只有银狼/姬子启行/命运圣杯成员能带特殊装备——其余角色识别出
        //    特殊装备均为误识别（实测大黑塔被误配"分身墨镜"，违反规则）。
        if (!IsSpecialEquipmentEligible(ownerId, candidates))
        {
            return null;
        }
        // This crop always contains part of the character portrait, so the
        // equipment catalog's general 0.30 threshold is too permissive here.
        // The real special-equipment overlay is a tight icon match; weaker
        // portrait matches must not create equipment on unrelated characters.
        const double specialEquipmentMinimumConfidence = 0.60;
        var acceptedVisual = item.Confidence >= specialEquipmentMinimumConfidence &&
                             (item.IsKnown || displayNames.Length > 0);
        if (!acceptedVisual)
        {
            return null;
        }

        var relativeRegion = ToRelative(region);
        var specialEvidence = evidence with
        {
            Locator = $"crop:special-equipment:{zone}:{slot.SlotIndex + 1}",
            Summary = displayNames.Length > 0
                ? string.Join(" / ", displayNames)
                : item.TemplateId,
            Confidence = item.Confidence
        };
        if (item.IsKnown)
        {
            return new CharacterSpecialEquipmentState(
                EquipmentSlotOccupancy.Equipped,
                item.TemplateId,
                candidates,
                displayNames,
                item.Confidence,
                relativeRegion,
                specialEvidence);
        }

        var failureReason = "特殊装备图标对应多个视觉相同版本，保留候选待确认";
        pending.Add(new PendingIconObservation(
            PendingIconCategory.SpecialItem,
            $"{zone}-{slot.SlotIndex + 1}-special-equipment",
            relativeRegion,
            item.TemplateId,
            item.Confidence,
            specialEvidence,
            "ambiguous-visual-identity",
            candidates,
            $"unknown-special-equipment-{zone}-{slot.SlotIndex + 1}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ownerCharacterId"] = ownerId,
                ["candidateDisplayNames"] = string.Join(" / ", displayNames)
            },
            false));
        return new CharacterSpecialEquipmentState(
            EquipmentSlotOccupancy.Unknown,
            null,
            candidates,
            displayNames,
            item.Confidence,
            relativeRegion,
            specialEvidence,
            failureReason,
            false);
    }

    /// <summary>
    /// 羁绊下一级门槛（几人口激活下一级）从数据库 tier_effects 计算
    ///（用户 2026-08-07：bond_catalog.required_members，如星核猎手 2/3/4）。
    /// ActiveCount 后下一个 required_members 即下一级；已满级返回 null。
    /// </summary>
    private static int? ResolveNextBondTier(
        string bondName,
        int activeCount)
    {
        // SynergyId 形如 bond_星核猎手，目录 Name 是星核猎手——去掉前缀匹配
        var normalized = bondName.StartsWith("bond_", StringComparison.Ordinal)
            ? bondName[5..]
            : bondName;
        var bond = GameDataCatalog.BondCatalog
            .FirstOrDefault(item =>
                string.Equals(item.Name, normalized, StringComparison.Ordinal));
        var tiers = bond?.TierEffects
            ?.Select(tier => tier.RequiredMembers)
            .OrderBy(value => value)
            .ToArray() ?? [];
        return tiers.FirstOrDefault(value => value > activeCount) is > 0
            ? tiers.First(value => value > activeCount)
            : null;
    }

    private Observation<IReadOnlyList<ActiveSynergyState>> ObserveSynergies(
        CaptureFrame frame,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending)
    {
        var recognized = RecognizeIconsSafely(
            frame,
            "synergy",
            Phase2RecognitionRegions.SynergyIconSlots,
            iconTemplates);
        var values = recognized.Where(item => item.IsKnown)
            .Select(item => new ActiveSynergyState(
                item.TemplateId,
                null,
                null,
                $"synergy-{item.SlotIndex + 1}",
                item.Confidence,
                evidence with
                {
                    Locator = $"vision:synergy:{item.SlotIndex + 1}",
                    Confidence = item.Confidence
                }))
            .ToArray();
        AddPending(
            frame,
            PendingIconCategory.Synergy,
            Phase2RecognitionRegions.SynergyIconSlots,
            recognized,
            evidence,
            pending);
        return values.Length > 0
            ? Observation<IReadOnlyList<ActiveSynergyState>>.Known(
                values,
                values.Average(item => item.Confidence),
                values.Select(item => item.Evidence),
                frame.CapturedAt)
            : Observation<IReadOnlyList<ActiveSynergyState>>.Unknown(
                "羁绊图标模板尚未导入或未达到逐槽阈值",
                [evidence with { Locator = "vision:synergy-list" }],
                frame.CapturedAt);
    }

    private async Task<IReadOnlyList<Phase2NamedContentRecognition>>
        ObserveNamedContentAsync(
            CaptureFrame frame,
            string iconCategory,
            Phase2NamedContentKind kind,
            PendingIconCategory pendingCategory,
            IReadOnlyList<NormalizedRect> iconSlots,
            IReadOnlyList<NormalizedRect> textSlots,
            IReadOnlyList<NamedCatalogItem> catalog,
            EvidenceReference evidence,
            ICollection<PendingIconObservation> pending,
            CancellationToken cancellationToken)
    {
        if (iconSlots.Count != textSlots.Count)
        {
            throw new InvalidDataException(
                $"{kind} 的图标槽位与文字槽位数量不一致。");
        }

        var icons = RecognizeIconsSafely(
            frame,
            iconCategory,
            iconSlots,
            iconTemplates);
        var results = new List<Phase2NamedContentRecognition>(iconSlots.Count);
        for (var index = 0; index < iconSlots.Count; index++)
        {
            var icon = icons[index];
            if (!icon.IsKnown &&
                !HasDetailedForeground(frame, iconSlots[index]))
            {
                if (kind == Phase2NamedContentKind.NegativeAffix)
                {
                    var slotKey = $"{kind}-{index + 1}";
                    var slotEvidence = evidence with
                    {
                        Locator = $"vision:{kind}:{index + 1}",
                        Summary = "The expected negative-affix slot could not be resolved from this frame."
                    };
                    var unresolved = new Phase2NamedContentRecognition(
                        kind,
                        slotKey,
                        ObservationStatus.Unknown,
                        null,
                        null,
                        [],
                        0,
                        ToRelative(iconSlots[index]),
                        Phase2RecognitionEvidenceKind.Icon,
                        icon.CandidateTemplateIds ?? [],
                        [],
                        slotEvidence);
                    results.Add(unresolved);
                    pending.Add(new PendingIconObservation(
                        pendingCategory,
                        slotKey,
                        unresolved.Region,
                        icon.TemplateId,
                        icon.Confidence,
                        slotEvidence,
                        "unresolved-slot",
                        unresolved.CandidateIds));
                }

                continue;
            }

            // These fields normally appear as icons without visible names.
            // A decisive template match is complete evidence by itself; running
            // two OCR crops (and all OCR preprocessing variants) for every known
            // icon made preparation analysis needlessly expensive.
            if (icon.IsKnown)
            {
                // The synergy icon is the authoritative identity evidence, but
                // its adjacent text is still the only source for the active and
                // next activation counts (for example, 2/4/6/8). Keep this OCR
                // pass bounded and do not use it to override the icon identity.
                var progressTexts = kind == Phase2NamedContentKind.Synergy
                    ? await ReadTextAsync(
                            frame,
                            textSlots[index],
                            cancellationToken,
                            allowEnlargedFallback: false)
                        .ConfigureAwait(false)
                    : [];
                var iconOnly = Phase2NamedContentEvidenceResolver.Resolve(
                    kind,
                    $"{kind}-{index + 1}",
                    ToRelative(iconSlots[index]),
                    null,
                    icon,
                    evidence,
                    iconOnlyWithoutText: true);
                if (progressTexts.Count > 0)
                {
                    iconOnly = iconOnly with
                    {
                        RawOcrTexts = progressTexts,
                        Evidence = iconOnly.Evidence with
                        {
                            Summary = string.Join(" | ", progressTexts)
                        }
                    };
                }

                if (iconOnly.ObjectId is not null &&
                    iconOnly.StandardName is null)
                {
                    iconOnly = iconOnly with
                    {
                        StandardName = catalog.FirstOrDefault(item =>
                            string.Equals(
                                item.Id,
                                iconOnly.ObjectId,
                                StringComparison.Ordinal))?.Name
                    };
                }

                results.Add(iconOnly);
                continue;
            }

            // OCR remains a bounded fallback for an occupied but unresolved
            // icon slot (for example, when a tooltip happens to expose a name).
            var rawTexts = await ReadTextAsync(
                frame,
                textSlots[index],
                cancellationToken,
                allowEnlargedFallback:
                    kind == Phase2NamedContentKind.Synergy).ConfigureAwait(false);
            var matches = MatchNamedContent(rawTexts, catalog);
            Phase2NamedContentRecognition resolved;
            if (matches.Length > 1 &&
                matches[0].Confidence - matches[1].Confidence < 0.08)
            {
                var conflicts = matches
                    .Select(match =>
                        $"OCR 候选 {match.Value.Id}={match.Confidence:F3}")
                    .ToArray();
                resolved = new Phase2NamedContentRecognition(
                    kind,
                    $"{kind}-{index + 1}",
                    ObservationStatus.Conflict,
                    null,
                    null,
                    rawTexts,
                    0,
                    ToRelative(iconSlots[index]),
                    Phase2RecognitionEvidenceKind.Ocr,
                    matches.Select(match => match.Value.Id).ToArray(),
                    conflicts,
                    evidence with
                    {
                        Locator = $"ocr:{kind}:{index + 1}",
                        Summary = string.Join(" | ", rawTexts)
                    });
            }
            else
            {
                var ocrEvidence = matches.Length == 0
                    ? null
                    : new Phase2OcrNameEvidence(
                        matches[0].Value.Id,
                        matches[0].Value.Name,
                        rawTexts,
                        matches[0].Confidence);
                resolved = Phase2NamedContentEvidenceResolver.Resolve(
                    kind,
                    $"{kind}-{index + 1}",
                    ToRelative(iconSlots[index]),
                    ocrEvidence,
                    icon,
                    evidence);
            }

            if (resolved.Status == ObservationStatus.Known &&
                resolved.ObjectId is not null &&
                resolved.StandardName is null)
            {
                resolved = resolved with
                {
                    StandardName = catalog.FirstOrDefault(item =>
                        string.Equals(
                            item.Id,
                            resolved.ObjectId,
                            StringComparison.Ordinal))?.Name
                };
            }

            results.Add(resolved);
            if (resolved.Status == ObservationStatus.Known)
            {
                continue;
            }

            pending.Add(new PendingIconObservation(
                pendingCategory,
                resolved.SlotKey,
                resolved.Region,
                icon.TemplateId,
                icon.Confidence,
                resolved.Evidence,
                resolved.Status == ObservationStatus.Conflict
                    ? "ocr-icon-conflict"
                    : resolved.CandidateIds.Count > 1
                        ? "ambiguous-visual-identity"
                        : "unresolved",
                resolved.CandidateIds));
        }

        return results;
    }

    private NameMatch<NamedCatalogItem>[] MatchNamedContent(
        IReadOnlyList<string> rawTexts,
        IReadOnlyList<NamedCatalogItem> catalog) =>
        rawTexts
            .Append(string.Join(" ", rawTexts))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => _nameMatcher.FindBest(
                text,
                catalog,
                item => item.Name,
                0.72))
            .OfType<NameMatch<NamedCatalogItem>>()
            .GroupBy(match => match.Value.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(
                    match => match.Confidence)
                .First())
            .OrderByDescending(match => match.Confidence)
            .ToArray();

    private static Observation<IReadOnlyList<string>> ToListObservation(
        IReadOnlyList<Phase2NamedContentRecognition> content,
        string label,
        DateTimeOffset observedAt)
    {
        var conflicts = content
            .Where(item => item.Status == ObservationStatus.Conflict)
            .SelectMany(item => item.Conflicts)
            .ToArray();
        if (conflicts.Length > 0)
        {
            return Observation<IReadOnlyList<string>>.Conflict(
                conflicts,
                content.Select(item => item.Evidence),
                observedAt);
        }

        var known = content
            .Where(item => item.Status == ObservationStatus.Known &&
                           item.ObjectId is not null)
            .ToArray();
        return known.Length == 0
            ? Observation<IReadOnlyList<string>>.Unknown(
                $"{label} 没有得到可靠的 OCR 或用途限定图标证据",
                content.Select(item => item.Evidence),
                observedAt)
            : Observation<IReadOnlyList<string>>.Known(
                known.Select(item => item.ObjectId!).ToArray(),
                known.Average(item => item.Confidence),
                known.Select(item => item.Evidence),
                observedAt);
    }

    private static Observation<string> ToSingleObservation(
        IReadOnlyList<Phase2NamedContentRecognition> content,
        string label,
        DateTimeOffset observedAt)
    {
        var list = ToListObservation(content, label, observedAt);
        if (list.Status == ObservationStatus.Conflict)
        {
            return Observation<string>.Conflict(
                list.Uncertainty,
                list.Evidence,
                observedAt);
        }

        return list.Status == ObservationStatus.Known &&
               list.Value is { Count: 1 }
            ? Observation<string>.Known(
                list.Value[0],
                list.Confidence,
                list.Evidence,
                observedAt)
            : Observation<string>.Unknown(
                $"{label} 没有得到唯一可靠结果",
                list.Evidence,
                observedAt);
    }

    private static readonly IReadOnlyDictionary<string, string>
        EquipmentBondById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["currency_wars_equipment_001"] = "命运圣杯",
            ["currency_wars_equipment_021"] = "欢愉",
            ["currency_wars_equipment_123"] = "仙舟",
            ["currency_wars_equipment_124"] = "列车同行",
            ["currency_wars_equipment_125"] = "夜之半神",
            ["currency_wars_equipment_126"] = "星间旅人",
            ["currency_wars_equipment_127"] = "昼之半神",
            ["currency_wars_equipment_128"] = "狼狩",
            ["currency_wars_equipment_129"] = "盛会之星",
            ["currency_wars_equipment_130"] = "贝洛伯格",
            ["currency_wars_equipment_131"] = "银河学者",
            ["currency_wars_equipment_132"] = "减益",
            ["currency_wars_equipment_133"] = "击破",
            ["currency_wars_equipment_134"] = "战技点",
            ["currency_wars_equipment_135"] = "护盾",
            ["currency_wars_equipment_136"] = "持续伤害",
            ["currency_wars_equipment_137"] = "治疗",
            ["currency_wars_equipment_138"] = "燃血",
            ["currency_wars_equipment_139"] = "群攻",
            ["currency_wars_equipment_140"] = "能量",
            ["currency_wars_equipment_141"] = "追击",
            ["currency_wars_equipment_142"] = "量子同频"
        };

    private static readonly HashSet<string> JoyTapeIds =
        ["currency_wars_equipment_032", "currency_wars_equipment_033"];

    private Observation<IReadOnlyList<ActiveSynergyState>>?
        ComputeSynergiesFromFormation(
            Observation<IReadOnlyList<FormationCharacterState>> formation,
            EvidenceReference evidence,
            DateTimeOffset observedAt)
    {
        var slots = formation.Value ?? [];
        if (slots.Count == 0)
        {
            return null;
        }

        // 每个角色参与哪些羁绊（角色数据库 bonds 字段）→ 累加同羁绊角色数。
        // 用户 2026-08-07：只有场上（前台 Front / 后台 Back）角色计入羁绊，
        // 备战席（Bench）角色不计入（在备战席不算激活羁绊）。
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (slot.Zone is not (FormationZone.Front or FormationZone.Back) ||
                string.IsNullOrWhiteSpace(slot.CharacterId) ||
                slot.CharacterId.StartsWith(
                    "unknown-formation-unit",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var character = gameData.CurrencyWarsCharacters
                .FirstOrDefault(item => string.Equals(
                    item.Id,
                    slot.CharacterId,
                    StringComparison.OrdinalIgnoreCase));
            if (character is null)
            {
                continue;
            }
            var naturalBonds = character.BondNames.ToHashSet(StringComparer.Ordinal);

            foreach (var bond in character.BondNames)
            {
                if (string.IsNullOrWhiteSpace(bond))
                {
                    continue;
                }

                counts[bond] = counts.GetValueOrDefault(bond) + 1;
            }

            foreach (var bond in ResolveEquipmentBondCountIncrements(
                         slot,
                         naturalBonds))
            {
                counts[bond] = counts.GetValueOrDefault(bond) + 1;
            }
        }

        if (counts.Count == 0)
        {
            return null;
        }

        return Observation<IReadOnlyList<ActiveSynergyState>>.Known(
            counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ActiveSynergyState(
                    pair.Key,
                    pair.Value,
                    // 羁绊等级阈值（几人口激活几级）从数据库 tier_effects 计算
                    //（用户 2026-08-07：bond_catalog 有 required_members，
                    // 如星核猎手 2/3/4 人）。ActiveCount 后下一个 required_members
                    // 即下一级门槛。
                    NextThreshold: ResolveNextBondTier(pair.Key, pair.Value),
                    $"computed-{pair.Key}",
                    0.85,
                    evidence with
                    {
                        Locator = "computed:formation-synergy",
                        Summary = $"阵容羁绊累加：{pair.Key}×{pair.Value}"
                    }))
                .ToArray(),
            0.85,
            slots.Select(slot => evidence with
            {
                Locator = "vision:formation-synergy-source"
            }),
            observedAt);
    }

    internal static IReadOnlyList<string> ResolveEquipmentBondCountIncrements(
        FormationCharacterState slot,
        IReadOnlySet<string> naturalBonds)
    {
        if (slot.Zone is not (FormationZone.Front or FormationZone.Back))
        {
            return [];
        }

        var increments = new List<string>();
        var emblemBonds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var equipmentId in slot.EquipmentIds.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            if (EquipmentBondById.TryGetValue(equipmentId, out var bond) &&
                !naturalBonds.Contains(bond))
            {
                emblemBonds.Add(bond);
            }
        }
        increments.AddRange(emblemBonds);

        var specialIds = (slot.SpecialEquipmentIds ?? [])
            .Concat(slot.SpecialEquipment?.EquipmentId is { } specialEquipmentId
                ? [specialEquipmentId]
                : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var isSilverWolf = slot.CharacterId is
            "currency_wars_character_05" or "currency_wars_character_27";
        var hasDefinitiveJoyTape = isSilverWolf &&
            specialIds.Any(JoyTapeIds.Contains);
        var candidates = slot.SpecialEquipment?.CandidateEquipmentIds ?? [];
        var hasSemanticallyCertainJoyTape = isSilverWolf && candidates.Count > 0 &&
            candidates.All(JoyTapeIds.Contains);
        if (hasDefinitiveJoyTape || hasSemanticallyCertainJoyTape)
        {
            // 欢愉卡带：非欢愉角色加入欢愉；已是欢愉成员时计数仍额外 +1。
            increments.Add("欢愉");
        }

        return increments.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static Observation<IReadOnlyList<ActiveSynergyState>>
        ToSynergyObservation(
            IReadOnlyList<Phase2NamedContentRecognition> content,
            DateTimeOffset observedAt)
    {
        var conflicts = content
            .Where(item => item.Status == ObservationStatus.Conflict)
            .SelectMany(item => item.Conflicts)
            .ToArray();
        if (conflicts.Length > 0)
        {
            return Observation<IReadOnlyList<ActiveSynergyState>>.Conflict(
                conflicts,
                content.Select(item => item.Evidence),
                observedAt);
        }

        var values = content
            .Where(item => item.Status == ObservationStatus.Known)
            .Select(item =>
            {
                var progress = ParseSynergyProgress(item.RawOcrTexts);
                return new ActiveSynergyState(
                    item.ObjectId,
                    progress.Active,
                    progress.Next,
                    item.SlotKey,
                    item.Confidence,
                    item.Evidence);
            })
            .ToArray();
        return values.Length == 0
            ? Observation<IReadOnlyList<ActiveSynergyState>>.Unknown(
                "羁绊没有得到可靠的 OCR 或备战页图标证据",
                content.Select(item => item.Evidence),
                observedAt)
            : Observation<IReadOnlyList<ActiveSynergyState>>.Known(
                values,
                values.Average(item => item.Confidence),
                values.Select(item => item.Evidence),
                observedAt);
    }

    internal static (int? Active, int? Next) ParseSynergyProgress(
        IEnumerable<string> rawTexts)
    {
        var match = rawTexts
            .Select(text => SynergyProgressPattern().Match(text))
            .FirstOrDefault(candidate => candidate.Success);
        return match?.Success == true
            ? (
                int.Parse(
                    match.Groups["active"].Value,
                    CultureInfo.InvariantCulture),
                int.Parse(
                    match.Groups["next"].Value,
                    CultureInfo.InvariantCulture))
            : (null, null);
    }

    internal static Observation<IReadOnlyList<ActiveSynergyState>>
        MergeFormationAuthoritativeSynergies(
            Observation<IReadOnlyList<ActiveSynergyState>> observed,
            Observation<IReadOnlyList<ActiveSynergyState>>? computed)
    {
        if (computed?.Status != ObservationStatus.Known ||
            computed.Value is not { Count: > 0 } computedValues)
        {
            return observed;
        }

        return Observation<IReadOnlyList<ActiveSynergyState>>.Known(
            computedValues.ToArray(),
            computed.Confidence,
            computed.Evidence,
            computed.ObservedAt);
    }

    /// <summary>
    /// 羁绊 id 归一化（P2-11）：实机识别 ObjectId 形如 "bond_头号玩家"
    ///（_synergies 目录按 bond_{name} 构造），阵容推算 SynergyId 是角色
    /// BondNames 中文名（无前缀）——合并 ActiveCount 前统一为中文名。
    /// </summary>
    private static string NormalizeSynergyId(string? synergyId) =>
        synergyId?.StartsWith(
            "bond_",
            StringComparison.Ordinal) == true
            ? synergyId["bond_".Length..]
            : synergyId ?? string.Empty;

    private async Task<InventoryObservation> ObserveInventory(
        CaptureFrame frame,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending,
        CancellationToken cancellationToken)
    {
        // 2026-08-19 物品栏扫描：右列之外补左列。冶金炉/好运令牌/特权赋予卡/
        // 机密拆装扳手等特殊物品常落在物品栏左侧列(现有 InventoryIconSlots 仅
        // 右列单列,冶金炉 L0 从未被扫描)。左列=右列每格向左平移一格宽。
        var baseSlotRects = Phase2RecognitionRegions
            .InventoryIconSlots.ToArray();
        var rightColumn = baseSlotRects;
        var leftColumn = baseSlotRects
            .Select(rect => rect with
            {
                X = rect.X - baseSlotRects[0].Width
            })
            .ToArray();
        var slots = rightColumn.Concat(leftColumn).ToArray();
        var occupiedIndices = Enumerable.Range(0, slots.Length)
            .Where(index => HasDetailedForeground(frame, slots[index]))
            .ToArray();
        var occupiedRecognitions = RecognizeIconsSafely(
            frame,
            "inventory-item",
            occupiedIndices.Select(index => slots[index]).ToArray(),
            iconTemplates);
        var recognitionBySlot = occupiedIndices
            .Select((slotIndex, resultIndex) => (
                SlotIndex: slotIndex,
                Result: occupiedRecognitions[resultIndex]))
            .ToDictionary(item => item.SlotIndex, item => item.Result);
        // 2026-08-19 堆叠数量：只有会堆叠的特殊物品（冶金炉/拆装扳手/机密
        // 拆装扳手）右下角有角标数字（×N）；好运令牌/特权赋予卡及其他普通装备
        // 不堆叠、无角标。角标区=格内右下 (0.50,0.44)-(1,1)。OCR 只对命中
        // 可堆叠物品的格读取，其余 Quantity=null（按 1 计、不显示角标）。
        var stackableItemId = (Phase2IconRecognition? recognition) =>
        {
            var id = recognition?.TemplateId;
            return id is not null &&
                (id.Contains("_149", StringComparison.Ordinal) ||    // 冶金炉
                 id.Contains("_153", StringComparison.Ordinal) ||    // 拆装扳手
                 id.Contains("_155", StringComparison.Ordinal) ||    // 机密拆装扳手
                 id.Contains("_150", StringComparison.Ordinal) ||    // 员工投影仪（special_item_002）
                 id.Contains("_152", StringComparison.Ordinal) ||    // 完美投影仪（special_item_003）
                 id.StartsWith("special_item_010", StringComparison.Ordinal) ||
                 id.StartsWith("special_item_002", StringComparison.Ordinal) ||
                 id.StartsWith("special_item_003", StringComparison.Ordinal));
        };
        var quantities = new Dictionary<int, int>();
        foreach (var occupiedIndex in occupiedIndices)
        {
            if (!recognitionBySlot.TryGetValue(occupiedIndex, out var occupied) ||
                !stackableItemId(occupied))
            {
                continue;
            }

            var slotRect = slots[occupiedIndex];
            var badgeRegion = new NormalizedRect(
                slotRect.X + slotRect.Width * 0.50,
                slotRect.Y + slotRect.Height * 0.44,
                slotRect.Width * 0.50,
                slotRect.Height * 0.56);
            if (badgeRegion.ToPixels(frame.Width, frame.Height).Width <= 4 ||
                badgeRegion.ToPixels(frame.Width, frame.Height).Height <= 4)
            {
                continue;
            }

            var badgeLines = await ReadTextRobustAsync(
                    frame,
                    badgeRegion,
                    _storeLevelOcr,
                    cancellationToken,
                    scale: 4)
                .ConfigureAwait(false);
            var quantity = ParseIntegerValues(badgeLines, 1, 999).FirstOrDefault();
            // 用户规则：拆装扳手/冶金炉数量只有 1 个时无角标数字；角标仅在
            // 堆叠 ≥2 时出现（如扳手×4 显示"4"）。故只存 ≥2，读到 1 视为无角标。
            if (quantity >= 2)
            {
                quantities[occupiedIndex] = quantity;
            }
        }

        var values = new List<InventorySlotState>(slots.Length);

        for (var index = 0; index < slots.Length; index++)
        {
            var relativeRegion = ToRelative(slots[index]);
            if (!recognitionBySlot.TryGetValue(index, out var item))
            {
                values.Add(new InventorySlotState(
                    index,
                    EquipmentSlotOccupancy.Empty,
                    InventoryItemKind.Unknown,
                    null,
                    [],
                    0.90,
                    relativeRegion,
                    evidence with
                    {
                        Locator = $"vision:inventory-empty:{index + 1}",
                        Confidence = 0.90
                    }));
                continue;
            }

            var candidates = (item.CandidateTemplateIds ?? [])
                .Append(item.TemplateId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidateKinds = candidates
                .Select(id => _inventoryKindById.GetValueOrDefault(
                    id,
                    InventoryItemKind.Unknown))
                .Distinct()
                .ToArray();
            var itemKind = candidateKinds.Length == 1
                ? candidateKinds[0]
                : InventoryItemKind.Unknown;
            var slotEvidence = evidence with
            {
                Locator = $"crop:inventory-item:{index + 1}",
                Confidence = item.Confidence
            };
            if (item.IsKnown && itemKind != InventoryItemKind.Unknown)
            {
                quantities.TryGetValue(index, out var quantity);
                values.Add(new InventorySlotState(
                    index,
                    EquipmentSlotOccupancy.Equipped,
                    itemKind,
                    item.TemplateId,
                    candidates,
                    item.Confidence,
                    relativeRegion,
                    slotEvidence,
                    Quantity: quantity > 0 ? quantity : null));
                continue;
            }

            var reason = candidates.Count() > 1
                ? "背包图标存在多个视觉相同或近似候选，已保留全部候选。"
                : "背包槽有内容，但图标未达到可靠识别条件。";
            values.Add(new InventorySlotState(
                index,
                EquipmentSlotOccupancy.Unknown,
                itemKind,
                null,
                candidates,
                item.Confidence,
                relativeRegion,
                slotEvidence,
                reason,
                false));
            pending.Add(new PendingIconObservation(
                PendingIconCategory.InventoryItem,
                $"inventory-{index + 1}",
                relativeRegion,
                item.TemplateId,
                item.Confidence,
                slotEvidence,
                candidates.Count() > 1
                    ? "ambiguous-visual-identity"
                    : "unresolved",
                candidates,
                $"unknown-inventory-{index + 1}",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["inventorySlot"] = (index + 1)
                        .ToString(CultureInfo.InvariantCulture),
                    ["itemKind"] = itemKind.ToString()
                },
                false));
        }

        // 物品栏槽位显示=主列（右列 InventoryIconSlots，7 槽）——
        // 左列(补充特殊物品扫描)不占显示槽位，仅用于补全 Simple/Special ids。
        var displayValues = values.Take(rightColumn.Length).ToArray();
        var allResolved = displayValues.All(item =>
            item.Occupancy is EquipmentSlotOccupancy.Empty or
                EquipmentSlotOccupancy.Equipped);
        var slotsObservation = allResolved
            ? Observation<IReadOnlyList<InventorySlotState>>.Known(
                displayValues,
                displayValues.Average(item => item.Confidence),
                displayValues.Select(item => item.Evidence),
                frame.CapturedAt)
            : new Observation<IReadOnlyList<InventorySlotState>>
            {
                Status = ObservationStatus.Unknown,
                Value = displayValues,
                Confidence = displayValues.Length == 0
                    ? 0
                    : displayValues.Average(item => item.Confidence),
                Evidence = displayValues.Select(item => item.Evidence).ToArray(),
                Uncertainty = ["背包仅得到部分可靠结果；已保留空槽、已知项和未知候选。"],
                ObservedAt = frame.CapturedAt
            };
        return new InventoryObservation(
            ProjectInventoryIds(
                values,
                InventoryItemKind.SimpleEquipment,
                allResolved,
                evidence,
                frame.CapturedAt),
            ProjectInventoryIds(
                values,
                [InventoryItemKind.SpecialItem, InventoryItemKind.DismantleTool],
                allResolved,
                evidence,
                frame.CapturedAt),
            slotsObservation);
    }

    private BenchSpecialItemObservation ObserveBenchSpecialItems(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardSlotRecognition> bench,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending)
    {
        if (_benchSpecialItemTemplates.Count == 0)
        {
            return new BenchSpecialItemObservation(
                Observation<IReadOnlyList<string>>.Unknown(
                    "未经实测验证的备战席特殊物品模板未启用。",
                    [evidence],
                    frame.CapturedAt),
                new HashSet<int>(),
                WasEvaluated: false);
        }

        // The normal card pass remains authoritative for characters. This
        // narrow second pass only examines unresolved/special-occupied bench
        // slots and contains exactly the live-verified 004 template.
        var candidates = bench.Where(item => item.State is
                CharacterCardSlotState.Uncertain or
                CharacterCardSlotState.SpecialOccupied)
            .ToArray();
        var observationEvidence = evidence with
        {
            Locator = "vision:bench-special-items",
            Summary = "备战席特殊物品局部二次识别"
        };
        if (candidates.Length == 0)
        {
            return new BenchSpecialItemObservation(
                Observation<IReadOnlyList<string>>.Known(
                    [],
                    1,
                    [observationEvidence],
                    frame.CapturedAt),
                new HashSet<int>(),
                WasEvaluated: true);
        }

        var regions = candidates
            .Select(item => ToNormalized(item.ReferenceBounds))
            .ToArray();
        var recognitions = RecognizeIconsSafely(
            frame,
            BenchSpecialItemCategory,
            regions,
            _benchSpecialItemTemplates);
        var ids = new List<string>();
        var confidences = new List<double>();
        var recognizedSlots = new HashSet<int>();
        var hasUnresolvedLikelyItem = false;

        for (var index = 0; index < candidates.Length; index++)
        {
            var slot = candidates[index];
            var item = index < recognitions.Count
                ? recognitions[index]
                : null;
            if (item is { IsKnown: true } &&
                item.TemplateId is not null)
            {
                var resolved =
                    ResolveBenchSpecialItem(item.TemplateId);
                if (resolved is null || item.Confidence <
                    BenchSpecialItemMinimumConfidence)
                {
                    hasUnresolvedLikelyItem |=
                        item.Confidence >= BenchSpecialItemPossibleConfidence;
                    continue;
                }

                // D 步（2026-08-17）：武装箱/聘用书特殊单位。catalog 模板 id
                // 是装备 id（156/157/158）或聘书 special_item_020/021/022，
                // 这里映射为记录/显示用的 special_item id + 中文名。
                ids.Add(resolved.Value.RecordId);
                confidences.Add(item.Confidence);
                recognizedSlots.Add(slot.SlotIndex);
                var slotEvidence = evidence with
                {
                    Locator = $"crop:bench-special-item:{slot.SlotIndex + 1}",
                    Summary = resolved.Value.DisplayName,
                    Confidence = item.Confidence
                };
                pending.Add(new PendingIconObservation(
                    PendingIconCategory.SpecialItem,
                    $"bench-special-item-{slot.SlotIndex + 1}",
                    ToRelative(slot.ReferenceBounds),
                    resolved.Value.RecordId,
                    item.Confidence,
                    slotEvidence,
                    "bench-special-item-recognized",
                    [resolved.Value.RecordId],
                    $"bench-special-item-Bench-{slot.SlotIndex + 1}",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["zone"] = "Bench",
                        ["slotIndex"] = slot.SlotIndex.ToString(
                            CultureInfo.InvariantCulture),
                        ["itemId"] = resolved.Value.RecordId,
                        ["sourceType"] = BenchSpecialItemCategory
                    },
                    false));
                continue;
            }

            // A first-pass SpecialOccupied result is definitely not a normal
            // character. A near-threshold 004 match may be an animated frame.
            // Keep the observation Unknown in either case so StateTracker
            // carries the previous reliable value instead of erasing it.
            hasUnresolvedLikelyItem |=
                slot.State == CharacterCardSlotState.SpecialOccupied ||
                item is { Confidence: >= BenchSpecialItemPossibleConfidence };
        }

        var itemObservation = hasUnresolvedLikelyItem
            ? new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = ids,
                Confidence = 0,
                Evidence = [observationEvidence],
                Uncertainty =
                [
                    "备战席仍有特殊占用或接近阈值的槽位；已确认物品被保留为部分证据。"
                ],
                ObservedAt = frame.CapturedAt
            }
            : Observation<IReadOnlyList<string>>.Known(
                ids,
                confidences.Count == 0 ? 1 : confidences.Average(),
                [observationEvidence],
                frame.CapturedAt);
        return new BenchSpecialItemObservation(
            itemObservation,
            recognizedSlots,
            WasEvaluated: true);
    }

    private static Observation<IReadOnlyList<string>> MergeSpecialItemObservations(
        Observation<IReadOnlyList<string>> inventory,
        BenchSpecialItemObservation bench,
        DateTimeOffset observedAt)
    {
        if (!bench.WasEvaluated)
        {
            return inventory;
        }

        var ids = (inventory.Value ?? [])
            .Concat(bench.ItemIds.Value ?? [])
            .ToArray();
        var evidence = inventory.Evidence
            .Concat(bench.ItemIds.Evidence)
            .Distinct()
            .ToArray();
        if (inventory.Status == ObservationStatus.Known &&
            bench.ItemIds.Status == ObservationStatus.Known)
        {
            return Observation<IReadOnlyList<string>>.Known(
                ids,
                (inventory.Confidence + bench.ItemIds.Confidence) / 2,
                evidence,
                observedAt);
        }

        var uncertainty = inventory.Uncertainty
            .Concat(bench.ItemIds.Uncertainty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new Observation<IReadOnlyList<string>>
        {
            Status = ObservationStatus.Unknown,
            Value = ids,
            Confidence = 0,
            Evidence = evidence,
            Uncertainty = uncertainty.Length == 0
                ? ["库存或备战席特殊物品尚未完整解析。"]
                : uncertainty,
            ObservedAt = observedAt
        };
    }

    private static Observation<IReadOnlyList<string>> ProjectInventoryIds(
        IReadOnlyList<InventorySlotState> slots,
        InventoryItemKind kind,
        bool allResolved,
        EvidenceReference evidence,
        DateTimeOffset observedAt) =>
        ProjectInventoryIds(
            slots,
            [kind],
            allResolved,
            evidence,
            observedAt);

    private static Observation<IReadOnlyList<string>> ProjectInventoryIds(
        IReadOnlyList<InventorySlotState> slots,
        IReadOnlyCollection<InventoryItemKind> kinds,
        bool allResolved,
        EvidenceReference evidence,
        DateTimeOffset observedAt)
    {
        var ids = slots
            .Where(item => item.Occupancy == EquipmentSlotOccupancy.Equipped &&
                           kinds.Contains(item.ItemKind) &&
                           item.ItemId is not null)
            .Select(item => item.ItemId!)
            .ToArray();
        return allResolved
            ? Observation<IReadOnlyList<string>>.Known(
                ids,
                slots.Count == 0 ? 1 : slots.Average(item => item.Confidence),
                [evidence with { Locator = $"vision:inventory:{string.Join("+", kinds)}" }],
                observedAt)
            : new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = ids,
                Confidence = 0,
                Evidence = [evidence with { Locator = $"vision:inventory:{string.Join("+", kinds)}" }],
                Uncertainty = ["背包存在未解析槽位；已确认项目仍被保留。"],
                ObservedAt = observedAt
            };
    }

    private static IReadOnlyList<Phase2IconTemplateDefinition>
        BuildBenchSpecialItemTemplates(
            IReadOnlyList<Phase2IconTemplateDefinition> templates) =>
        templates
            .Where(item => string.Equals(
                item.Category,
                "special-item",
                StringComparison.Ordinal))
            // D 步（2026-08-17）：备战席/后台"武装箱/聘用书"特殊单位识别。
            // 根因：旧代码仅按 SimpleArmamentBoxId("special_item_004") 过滤，
            // 但三档武装箱在 catalog 里因与装备库同名被 canonical 改写为
            // 装备 id（currency_wars_equipment_156=特权/157=简易/158=进阶），
            // 原 special_item_004/005/006 命名已不存在；三档聘书仍保留
            // special_item_020/021/022。旧过滤因此恒空、武装箱从未被识别。
            // 这里按 catalog 实际 id 选三档武装箱 + 三档聘书模板。
            .Where(item => (item.CandidateIds ?? [item.Id])
                .Any(id => BenchSpecialItemTemplateIds.Contains(
                    id,
                    StringComparer.Ordinal)))
            // 保留各模板自身 ComparisonMode（武装箱/聘书为 FullFrameColor）、
            // 阈值与唯一性语义，仅把类目标成备战席特殊物品类别供该链路使用。
            .Select(item => item with
            {
                Category = BenchSpecialItemCategory,
                MinimumConfidence = BenchSpecialItemMinimumConfidence,
                ResolvesExactIdentity = true,
                SemanticKind = "special-item"
            })
            .ToArray();

    private static IReadOnlyDictionary<string, InventoryItemKind>
        BuildInventoryKindMap(
            IReadOnlyList<Phase2IconTemplateDefinition> templates)
    {
        var result = new Dictionary<string, InventoryItemKind>(
            StringComparer.Ordinal);
        foreach (var template in templates.Where(item =>
                     item.Category == "inventory-item"))
        {
            var kind = template.SemanticKind switch
            {
                "simple-equipment" => InventoryItemKind.SimpleEquipment,
                "advanced-equipment" => InventoryItemKind.AdvancedEquipment,
                "dismantle-tool" => InventoryItemKind.DismantleTool,
                "special-item" => InventoryItemKind.SpecialItem,
                _ => InventoryItemKind.Unknown
            };
            foreach (var id in template.CandidateIds ?? [template.Id])
            {
                result[id] = kind;
            }
        }

        return result;
    }

    private sealed record BenchSpecialItemObservation(
        Observation<IReadOnlyList<string>> ItemIds,
        IReadOnlySet<int> RecognizedSlotIndices,
        bool WasEvaluated);

    private sealed record InventoryObservation(
        Observation<IReadOnlyList<string>> SimpleEquipmentIds,
        Observation<IReadOnlyList<string>> SpecialItemIds,
        Observation<IReadOnlyList<InventorySlotState>> Slots);

    private Observation<IReadOnlyList<string>> ObserveIcons(
        CaptureFrame frame,
        string category,
        PendingIconCategory pendingCategory,
        IReadOnlyList<NormalizedRect> slots,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending)
    {
        var recognized = RecognizeIconsSafely(
            frame,
            category,
            slots,
            iconTemplates);
        AddPending(frame, pendingCategory, slots, recognized, evidence, pending);
        var ids = recognized.Where(item => item.IsKnown)
            .Select(item => item.TemplateId!)
            .ToArray();
        var unresolvedVisible = recognized
            .Select((item, index) => (item, index))
            .Any(pair => !pair.item.IsKnown &&
                         HasDetailedForeground(frame, slots[pair.index]));
        return ids.Length > 0 && !unresolvedVisible
            ? Observation<IReadOnlyList<string>>.Known(
                ids,
                recognized.Where(item => item.IsKnown)
                    .Average(item => item.Confidence),
                [evidence with { Locator = $"vision:{category}" }],
                frame.CapturedAt)
            : new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = ids,
                Confidence = 0,
                Evidence = [evidence with { Locator = $"vision:{category}" }],
                Uncertainty = ids.Length > 0
                    ? [$"{category} 仅部分槽位得到可靠匹配；已识别ID已保留。"]
                    : [$"{category} 模板尚未导入或未达到逐槽阈值"],
                ObservedAt = frame.CapturedAt
            };
    }

    private Observation<string> ObserveSingleIcon(
        CaptureFrame frame,
        string category,
        PendingIconCategory pendingCategory,
        IReadOnlyList<NormalizedRect> slots,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending)
    {
        var recognized = RecognizeIconsSafely(
            frame,
            category,
            slots,
            iconTemplates);
        AddPending(frame, pendingCategory, slots, recognized, evidence, pending);
        var known = recognized.SingleOrDefault(item => item.IsKnown);
        return known is not null
            ? Observation<string>.Known(
                known.TemplateId!,
                known.Confidence,
                [evidence with { Locator = $"vision:{category}" }],
                frame.CapturedAt)
            : Observation<string>.Unknown(
                $"{category} 模板尚未导入或未达到逐槽阈值",
                [evidence with { Locator = $"vision:{category}" }],
                frame.CapturedAt);
    }

    private static void AddPending(
        CaptureFrame frame,
        PendingIconCategory category,
        IReadOnlyList<NormalizedRect> slots,
        IReadOnlyList<Phase2IconRecognition> recognized,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending)
    {
        for (var index = 0; index < recognized.Count; index++)
        {
            var item = recognized[index];
            if (item.IsKnown)
            {
                continue;
            }


            if (!HasDetailedForeground(frame, slots[index]))
            {
                continue;
            }

            var region = slots[index];
            pending.Add(new PendingIconObservation(
                category,
                $"{category}-{index + 1}",
                ToRelative(region),
                item.TemplateId,
                item.Confidence,
                evidence with
                {
                    Locator = $"crop:{category}:{index + 1}",
                    Confidence = item.Confidence
                },
                item.CandidateTemplateIds is { Count: > 1 }
                    ? "ambiguous-visual-identity"
                    : "unresolved",
                item.CandidateTemplateIds));
        }
    }

    private async Task<(
        Observation<IReadOnlyList<CharacterDamageState>> Damage,
        Observation<IReadOnlyList<SynergyDamageState>> SynergyDamage,
        Observation<IReadOnlyList<UnresolvedDamageSourceState>> UnresolvedDamage,
        Observation<long> TotalCandidate,
        IReadOnlyList<PendingIconObservation> Pending)> ReadBattleDamageAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        // 战斗伤害榜只处理前 6 行（用户 2026-08-18 确认）：第 7 行已超出
        // 排行榜 UI 底部，只显示半截头像、伤害数字也更不全、且该行伤害
        // 通常 < 总伤害 1%（如 1-7 结算实测 rank7 藿藿 6 点）可忽略。
        var avatarSlots = Enumerable.Range(0, 6)
            .Select(Phase2RecognitionRegions.BattleDamageAvatar)
            .ToArray();
        var avatars = RecognizeIconsSafely(
            frame,
            "character-avatar",
            avatarSlots,
            iconTemplates);
        var synergies = RecognizeIconsSafely(
            frame,
            "synergy",
            avatarSlots,
            iconTemplates);
        var hasAvatarForegroundByRow = new bool[avatarSlots.Length];
        var hasDamageBarByRow = new bool[avatarSlots.Length];
        var rowIsVisibleByRow = new bool[avatarSlots.Length];
        var primaryTextTasks = new Task<IReadOnlyList<string>>[avatarSlots.Length];
        for (var row = 0; row < avatarSlots.Length; row++)
        {
            var avatar = avatars[row];
            var synergy = synergies[row];
            var hasAvatarForeground = HasDetailedForeground(
                frame,
                avatarSlots[row]);
            var hasDamageBar = HasVisibleBattleDamageBar(frame, row);
            var rowIsVisible = avatar.IsKnown ||
                               synergy.IsKnown ||
                               avatar.Confidence >= 0.25 ||
                               synergy.Confidence >= 0.25 ||
                               hasAvatarForeground ||
                               hasDamageBar;
            hasAvatarForegroundByRow[row] = hasAvatarForeground;
            hasDamageBarByRow[row] = hasDamageBar;
            rowIsVisibleByRow[row] = rowIsVisible;
            primaryTextTasks[row] = rowIsVisible
                ? ReadNumericTextOnceAsync(
                    frame,
                    Phase2RecognitionRegions.BattleDamageValue(row),
                    cancellationToken)
                : Task.FromResult<IReadOnlyList<string>>([]);
        }

        await Task.WhenAll(primaryTextTasks).ConfigureAwait(false);
        var textByRow = primaryTextTasks
            .Select(task => task.Result)
            .ToArray();
        var candidatesByRow = new (
            long Value,
            int Score,
            string Text)[avatarSlots.Length][];
        var secondaryConflictByRow = new bool[avatarSlots.Length];
        var secondaryBlankByRow = new bool[avatarSlots.Length];
        for (var row = 0; row < avatarSlots.Length; row++)
        {
            // 每行 OCR 只识别一次（用户 2026-08-06 确认）：去掉 robust 兜底重试。
            // 性能优先：战斗页 OCR 调用次数从最多 16 次降到最多 7 次。
            candidatesByRow[row] = textByRow[row]
                .SelectMany(ParseBattleDamageCandidates)
                .OrderByDescending(item => item.Score)
                .ToArray();
        }

        // PP-OCR occasionally turns an empty row decoration into a small
        // unscaled integer (the 0.2.852 field frame produced "1"/"117").
        // Ask the independent Windows recognizer only for those suspicious
        // rows; normal 万/亿 and comma-scaled rows stay on the one-call path.
        if (_numericOcr is ISecondaryOfflineOcr { IsSecondaryAvailable: true } secondaryOcr)
        {
            var secondaryTasks = new Task<SecondaryNumericTextAttempt>?[avatarSlots.Length];
            for (var row = 0; row < avatarSlots.Length; row++)
            {
                var candidates = candidatesByRow[row];
                if (candidates.Length == 0 ||
                    (!candidates.Any(candidate => candidate.Value > 0) &&
                     !hasDamageBarByRow[row]) ||
                    candidates.Any(candidate =>
                        candidate.Value >= 1_000 ||
                        HasExplicitDamageScaleSafe(candidate.Text)))
                {
                    continue;
                }

                secondaryTasks[row] = ReadSecondaryNumericTextOnceAsync(
                    secondaryOcr,
                    frame,
                    Phase2RecognitionRegions.BattleDamageValue(row),
                    cancellationToken);
            }

            await Task.WhenAll(secondaryTasks.Where(task => task is not null)!)
                .ConfigureAwait(false);
            for (var row = 0; row < avatarSlots.Length; row++)
            {
                var task = secondaryTasks[row];
                if (task is null)
                {
                    continue;
                }

                var attempt = task.Result;
                if (!attempt.Succeeded)
                {
                    continue;
                }

                var secondaryCandidates = attempt.Texts
                    .SelectMany(ParseBattleDamageCandidates)
                    .OrderByDescending(item => item.Score)
                    .ToArray();
                if (secondaryCandidates.Length > 0)
                {
                    var primary = candidatesByRow[row][0];
                    var secondary = secondaryCandidates[0];
                    var resolved = ReconcileSuspiciousBattleDamageCandidate(
                        primary.Value,
                        primary.Score,
                        primary.Text,
                        secondary.Value,
                        secondary.Score,
                        secondary.Text,
                        hasDamageBarByRow[row]);
                    candidatesByRow[row] = [(resolved.Value, resolved.Score, resolved.Text)];
                    secondaryConflictByRow[row] = resolved.HasConflict;
                }
                else if (attempt.Texts.Count == 0)
                {
                    secondaryBlankByRow[row] = true;
                }
                else
                {
                    secondaryConflictByRow[row] = true;
                }
            }
        }

        var unresolvedPositiveValue = false;
        var missingVisibleValue = false;
        var unresolvedDamageSource = false;
        var characterRows = new List<CharacterDamageState>();
        var synergyRows = new List<SynergyDamageState>();
        var unresolvedRows = new List<UnresolvedDamageSourceState>();
        var pending = new List<PendingIconObservation>();
        for (var row = 0; row < avatarSlots.Length; row++)
        {
            var avatar = avatars[row];
            var synergy = synergies[row];
            var hasAvatarForeground = hasAvatarForegroundByRow[row];
            var hasDamageBar = hasDamageBarByRow[row];
            var rowIsVisible = rowIsVisibleByRow[row];
            if (!rowIsVisible)
            {
                continue;
            }

            var damageRegion = Phase2RecognitionRegions.BattleDamageValue(row);
            var text = textByRow[row];
            var candidates = candidatesByRow[row];
            if (CanConfirmEmptyBattleDamageRow(
                    secondaryBlankByRow[row],
                    hasDamageBar,
                    avatar.IsKnown,
                    synergy.IsKnown))
            {
                candidates =
                    [(0, 2, "0 (secondary OCR and row structure confirmed empty)")];
            }
            if (candidates.Length == 0 &&
                !hasDamageBar)
            {
                candidates = [(0, 2, "0 (empty battle damage bar)")];
            }

            if (candidates.Length == 0)
            {
                // An avatar can remain visible for a legitimate zero-damage row.
                // Only a detected damage bar is strong enough evidence that a
                // positive numeric value was missed and the total is incomplete.
                if (hasDamageBar)
                {
                    missingVisibleValue = true;
                }

                continue;
            }

            var best = candidates[0];
            var damageConfidence = best.Score >= 3 ? 0.68 : 0.35;
            unresolvedPositiveValue |= secondaryConflictByRow[row];
            unresolvedPositiveValue |= best.Value == 0 && hasDamageBar;
            if (best.Value > 0 &&
                (best.Score < 3 || !HasExplicitDamageScaleSafe(best.Text)))
            {
                unresolvedPositiveValue = true;
            }
            if (avatar.IsKnown && synergy.IsKnown)
            {
                unresolvedDamageSource = true;
                unresolvedRows.Add(new UnresolvedDamageSourceState(
                    row + 1,
                    $"unknown-damage-source-slot-{row + 1}",
                    BattleDamageSourceKind.Unknown,
                    null,
                    best.Value,
                    best.Text,
                    Math.Max(avatar.Confidence, synergy.Confidence),
                    damageConfidence,
                    ToRelative(avatarSlots[row]),
                    ToRelative(damageRegion),
                    [avatar.TemplateId!, synergy.TemplateId!],
                    "同一伤害行同时匹配角色与羁绊，来源冲突。",
                    evidence with
                    {
                        Locator = $"partial:battle-damage-source:{row + 1}",
                        Summary = best.Text,
                        Confidence = 0
                    }));
                pending.Add(new PendingIconObservation(
                    PendingIconCategory.CharacterAvatar,
                    $"damage-source-{row + 1}",
                    ToRelative(avatarSlots[row]),
                    avatar.TemplateId,
                    Math.Min(avatar.Confidence, synergy.Confidence),
                    evidence with
                    {
                        Locator = $"conflict:battle-damage-source:{row + 1}",
                        Summary = $"character={avatar.TemplateId}; synergy={synergy.TemplateId}"
                    },
                    "character-synergy-conflict",
                    [avatar.TemplateId!, synergy.TemplateId!],
                    $"unknown-damage-source-slot-{row + 1}",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["damage"] = best.Value.ToString(CultureInfo.InvariantCulture),
                        ["rawText"] = best.Text,
                        ["sourceType"] = "conflict"
                    },
                    false));
                continue;
            }

            if (avatar.IsKnown)
            {
                characterRows.Add(new CharacterDamageState(
                    row + 1,
                    avatar.TemplateId,
                    best.Value,
                    best.Text,
                    avatar.Confidence,
                    damageConfidence,
                    ToRelative(avatarSlots[row]),
                    ToRelative(damageRegion),
                    evidence with
                    {
                        Locator = $"ocr:battle-character-damage-row-{row + 1}",
                        Summary = best.Text,
                        Confidence = Math.Min(avatar.Confidence, damageConfidence)
                    }));
                continue;
            }

            if (synergy.IsKnown)
            {
                synergyRows.Add(new SynergyDamageState(
                    row + 1,
                    synergy.TemplateId,
                    best.Value,
                    best.Text,
                    synergy.Confidence,
                    damageConfidence,
                    ToRelative(avatarSlots[row]),
                    ToRelative(damageRegion),
                    evidence with
                    {
                        Locator = $"ocr:battle-synergy-damage-row-{row + 1}",
                        Summary = best.Text,
                        Confidence = Math.Min(synergy.Confidence, damageConfidence)
                    }));
                continue;
            }

            if (best.Value >= 0)
            {
                unresolvedDamageSource = true;
                var avatarCandidates = (avatar.CandidateTemplateIds ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var synergyCandidates = (synergy.CandidateTemplateIds ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var likelyCharacter = avatar.Confidence >= synergy.Confidence + 0.03 ||
                                      (avatarCandidates.Length > 0 &&
                                       synergyCandidates.Length == 0);
                var likelySynergy = synergy.Confidence >= avatar.Confidence + 0.03 ||
                                    (synergyCandidates.Length > 0 &&
                                     avatarCandidates.Length == 0);
                var temporaryId = likelyCharacter
                    ? $"unknown-character-slot-{row + 1}"
                    : likelySynergy
                        ? $"unknown-synergy-slot-{row + 1}"
                        : $"unknown-damage-source-slot-{row + 1}";
                if (likelyCharacter)
                {
                    characterRows.Add(new CharacterDamageState(
                        row + 1,
                        temporaryId,
                        best.Value,
                        best.Text,
                        avatar.Confidence,
                        damageConfidence,
                        ToRelative(avatarSlots[row]),
                        ToRelative(damageRegion),
                        evidence with
                        {
                            Locator = $"partial:battle-character-damage-row-{row + 1}",
                            Summary = best.Text,
                            Confidence = Math.Min(avatar.Confidence, damageConfidence)
                        },
                        temporaryId,
                        avatarCandidates,
                        "角色头像无法唯一识别；伤害数值已保留。",
                        false));
                }
                else if (likelySynergy)
                {
                    synergyRows.Add(new SynergyDamageState(
                        row + 1,
                        temporaryId,
                        best.Value,
                        best.Text,
                        synergy.Confidence,
                        damageConfidence,
                        ToRelative(avatarSlots[row]),
                        ToRelative(damageRegion),
                        evidence with
                        {
                            Locator = $"partial:battle-synergy-damage-row-{row + 1}",
                            Summary = best.Text,
                            Confidence = Math.Min(synergy.Confidence, damageConfidence)
                        },
                        temporaryId,
                        synergyCandidates,
                        "羁绊图标无法唯一识别；伤害数值已保留。",
                        false));
                }
                else
                {
                    unresolvedRows.Add(new UnresolvedDamageSourceState(
                        row + 1,
                        temporaryId,
                        BattleDamageSourceKind.Unknown,
                        null,
                        best.Value,
                        best.Text,
                        Math.Max(avatar.Confidence, synergy.Confidence),
                        damageConfidence,
                        ToRelative(avatarSlots[row]),
                        ToRelative(damageRegion),
                        avatarCandidates.Concat(synergyCandidates)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        "伤害数值可读，但来源可能是角色、羁绊或尚未适配的特殊单位。",
                        evidence with
                        {
                            Locator = $"partial:battle-damage-source:{row + 1}",
                            Summary = best.Text,
                            Confidence = 0
                        }));
                }

                pending.Add(new PendingIconObservation(
                    PendingIconCategory.CharacterAvatar,
                    $"damage-character-or-synergy-{row + 1}",
                    ToRelative(avatarSlots[row]),
                    avatar.TemplateId ?? synergy.TemplateId,
                    Math.Max(avatar.Confidence, synergy.Confidence),
                    evidence with
                    {
                        Locator = $"crop:battle-damage-source:{row + 1}",
                        Summary = best.Text
                    },
                    "character-or-synergy-unresolved",
                    avatarCandidates
                        .Concat(synergyCandidates)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    temporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["damage"] = best.Value.ToString(CultureInfo.InvariantCulture),
                        ["rawText"] = best.Text,
                        ["sourceType"] = likelyCharacter
                            ? "character"
                            : likelySynergy
                                ? "synergy"
                                : "unknown"
                    },
                    false));
            }
        }

        var damageObservation = characterRows.Count == 0 ||
                                unresolvedPositiveValue ||
                                unresolvedDamageSource
            ? PartialUnknown<IReadOnlyList<CharacterDamageState>>(
                characterRows,
                characterRows.Count == 0
                    ? "伤害列表中没有可可靠解析的角色伤害行。"
                    : unresolvedPositiveValue
                        ? "部分正伤害数值或其单位暂不可见；已保留其他可确认行。"
                        : "部分伤害来源无法区分角色与羁绊；已保留其他可确认行。",
                evidence with { Locator = "ocr:battle-damage-panel" },
                frame.CapturedAt)
            : Observation<IReadOnlyList<CharacterDamageState>>.Known(
                characterRows,
                characterRows.Average(item => Math.Min(
                    item.AvatarConfidence,
                    item.DamageConfidence)),
                characterRows.Select(item => item.Evidence),
                frame.CapturedAt);
        var synergyObservation = unresolvedPositiveValue ||
                                 unresolvedDamageSource
            ? PartialUnknown<IReadOnlyList<SynergyDamageState>>(
                synergyRows,
                "部分伤害来源暂不可见或存在角色/羁绊冲突；已保留其他可确认的羁绊伤害行。",
                evidence with { Locator = "ocr:battle-synergy-damage-panel" },
                frame.CapturedAt)
            : Observation<IReadOnlyList<SynergyDamageState>>.Known(
                synergyRows,
                synergyRows.Count == 0
                    ? 1
                    : synergyRows.Average(item => Math.Min(
                        item.IconConfidence,
                        item.DamageConfidence)),
                synergyRows.Select(item => item.Evidence),
                frame.CapturedAt);
        var unresolvedObservation = unresolvedRows.Count == 0
            ? Observation<IReadOnlyList<UnresolvedDamageSourceState>>.Known(
                [],
                1,
                observedAt: frame.CapturedAt)
            : PartialUnknown<IReadOnlyList<UnresolvedDamageSourceState>>(
                unresolvedRows,
                "存在尚未适配或来源冲突的伤害对象；已保留数值、区域、候选和裁剪证据。",
                evidence with { Locator = "partial:battle-unresolved-damage" },
                frame.CapturedAt);
        var recordedTotal = characterRows.Sum(item => item.Damage) +
                            synergyRows.Sum(item => item.Damage) +
                            unresolvedRows.Sum(item => item.Damage);
        var totalObservation = recordedTotal <= 0
            ? Observation<long>.Unknown(
                "战斗最后一帧没有可可靠求和的伤害数值。",
                [evidence with { Locator = "ocr:battle-damage-total-candidate" }],
                frame.CapturedAt)
            : unresolvedPositiveValue || missingVisibleValue
                ? PartialUnknown(
                    recordedTotal,
                    "战斗最后一帧仍有伤害数值暂不可见；当前和仅作残缺候选。",
                    evidence with { Locator = "partial:battle-damage-total-candidate" },
                    frame.CapturedAt)
                : Observation<long>.Known(
                    recordedTotal,
                    0.68,
                    [evidence with
                    {
                        Locator = "derived:battle-damage-total-candidate",
                        Summary = recordedTotal.ToString(CultureInfo.InvariantCulture),
                        Confidence = 0.68
                    }],
                    frame.CapturedAt);
        return (
            damageObservation,
            synergyObservation,
            unresolvedObservation,
            totalObservation,
            pending);
    }

    private async Task<Observation<RemainingActionValueState>>
        ReadRemainingActionValueAsync(
            CaptureFrame frame,
            EvidenceReference evidence,
            CancellationToken cancellationToken)
    {
        var locatedIndicators = Phase2ActionIndicatorLocator.LocateCandidates(
            frame,
            iconTemplates,
            maximumCandidates: 6);
        // The countdown row moves vertically, but its leading marker stays in
        // one narrow normalized column. Combat portraits, the 1/1 wave badge,
        // and skill HUD elements can resemble the small row template at other
        // horizontal positions. Do not let OCR text from those false locations
        // become a trusted action value.
        var indicators = locatedIndicators
            .Where(indicator => IsPlausibleActionIndicator(frame, indicator))
            .ToArray();
        IReadOnlyList<EvidenceReference> indicatorEvidence = [];
        if (indicators.Length > 0)
        {
            var candidatesToRead = indicators
                .OrderByDescending(indicator => ActionIndicatorRank(frame, indicator))
                .Take(MaximumActionCandidatesToRead)
                .ToArray();
            var locatedTasks = candidatesToRead.Select(async indicator => (
                Indicator: indicator,
                Observation: await ReadLocatedActionValueAsync(
                    frame,
                    indicator,
                    evidence,
                    cancellationToken).ConfigureAwait(false)))
                .ToArray();
            var located = await Task.WhenAll(locatedTasks).ConfigureAwait(false);
            var knownLocated = located
                .Where(item => item.Observation.Status == ObservationStatus.Known)
                .ToArray();
            var bestLocatedRank = knownLocated.Length == 0
                ? double.NegativeInfinity
                : knownLocated.Max(item =>
                    ActionIndicatorRank(frame, item.Indicator));
            var direct = knownLocated
                // OCR text in an unrelated row must not outrank a much
                // stronger action-row location merely because it happened to
                // contain two integers.
                .Where(item => ActionIndicatorRank(frame, item.Indicator) >=
                               bestLocatedRank - 0.05)
                // Preserve the mature OCR path whenever it produced a unique
                // value. The local digit matcher is a recovery path for
                // effect-obscured rows, not a reason for a weaker candidate
                // elsewhere on the timeline to outrank verified OCR.
                .OrderByDescending(item => item.Observation.Evidence.Any(entry =>
                    string.Equals(
                        entry.Locator,
                        "ocr:located-action-value",
                        StringComparison.Ordinal)))
                .ThenByDescending(item => ActionIndicatorRank(frame, item.Indicator))
                .ThenByDescending(item => item.Indicator.Region.Y)
                .Select(item => item.Observation)
                .FirstOrDefault();
            if (direct is not null)
            {
                return direct;
            }

            indicatorEvidence = indicators.Select(indicator => evidence with
                {
                    Locator = "template:action-value-indicator",
                    Summary = $"{indicator.TemplateId}; region={indicator.Region}; " +
                              $"match={indicator.Confidence:F3}"
                })
                .Concat(located.SelectMany(item => item.Observation.Evidence))
                .ToArray();

            // A located row whose focused OCR and digit recovery both failed
            // is normally obscured by a battle effect. Re-running OCR across
            // the entire timeline is both expensive and prone to interpreting
            // unrelated combat numbers as action value. Preserve the partial
            // evidence and let a later frame recover instead.
            return Observation<RemainingActionValueState>.Unknown(
                "The action row was located, but its current numbers were not reliably visible.",
                indicatorEvidence,
                frame.CapturedAt);
        }

        if (locatedIndicators.Count > 0)
        {
            return Observation<RemainingActionValueState>.Unknown(
                "Action-like rows were outside the fixed countdown-marker column or had insufficient visual evidence.",
                locatedIndicators.Select(indicator => evidence with
                {
                    Locator = "template:rejected-action-value-indicator",
                    Summary = $"{indicator.TemplateId}; region={indicator.Region}; " +
                              $"match={indicator.Confidence:F3}"
                }).ToArray(),
                frame.CapturedAt);
        }

        // No row locator survived. Keep the older broad OCR path as a bounded
        // compatibility fallback for layouts where the icon itself is hidden
        // but the round/value text remains visible.
        var region = Phase2RecognitionRegions.BattleActionTimeline;
        var lines = await ReadNumericTextAsync(
                frame,
                region,
                cancellationToken,
                allowEnlargedFallback: true)
            .ConfigureAwait(false);
        var candidates = lines
            .SelectMany(line => ActionValuePattern().Matches(line)
                .Select(match => (
                    Round: int.Parse(match.Groups["round"].Value, CultureInfo.InvariantCulture),
                    Value: int.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture),
                    Text: match.Value)))
            .Where(item => item.Round is >= 0 and <= 1 &&
                           item.Value is >= 0 and <= 100)
            .Where(item => item.Round > 0 || item.Value >= 50)
            .Concat(lines.SelectMany(line =>
            {
                var numbers = IntegerPattern().Matches(line)
                    .Select(match => int.Parse(
                        match.Value,
                        CultureInfo.InvariantCulture))
                    .ToArray();
                return numbers.Length >= 2 &&
                       numbers[^2] is >= 0 and <= 1 &&
                       numbers[^1] is >= 0 and <= 100
                    ? [(numbers[^2], numbers[^1], line)]
                    : Array.Empty<(int Round, int Value, string Text)>();
            }))
            .Distinct()
            .OrderByDescending(item => item.Value)
            .ThenByDescending(item => item.Round)
            .ToArray();
        if (candidates.Length == 0)
        {
            return Observation<RemainingActionValueState>.Unknown(
                "行动条 OCR 未得到“轮数+行动值”",
                indicatorEvidence.Concat([evidence with
                {
                    Locator = "ocr:battle-action-timeline",
                    Summary = string.Join(" | ", lines)
                }]).ToArray(),
                frame.CapturedAt);
        }

        var candidate = candidates[0];
        return Observation<RemainingActionValueState>.Known(
            RemainingActionValueState.Create(candidate.Round, candidate.Value),
            0.52,
            [evidence with
            {
                Locator = "ocr:battle-action-timeline",
                Summary = candidate.Text
            }],
            frame.CapturedAt);
    }

    private static double ActionIndicatorRank(
        CaptureFrame frame,
        Phase2IndicatorLocation indicator) =>
        indicator.Confidence +
        (indicator.Region.X <= frame.Width * 0.020
            ? 0.16
            : indicator.Region.X <= frame.Width * 0.040
                ? 0.04
                : 0);

    private static bool IsPlausibleActionIndicator(
        CaptureFrame frame,
        Phase2IndicatorLocation indicator)
    {
        var normalizedX = indicator.Region.X / (double)frame.Width;
        return normalizedX is >= 0.009 and <= 0.016 &&
               indicator.Confidence >= 0.40;
    }

    private async Task<Observation<RemainingActionValueState>>
        ReadLocatedActionValueAsync(
            CaptureFrame frame,
            Phase2IndicatorLocation indicator,
            EvidenceReference evidence,
            CancellationToken cancellationToken)
    {
        var expandedIndicatorRegion = new PixelRect(
            Math.Max(0, indicator.Region.X - (int)Math.Round(frame.Width * 0.005)),
            Math.Max(0, indicator.Region.Y - (int)Math.Round(frame.Height * 0.012)),
            Math.Min(
                frame.Width - Math.Max(
                    0,
                    indicator.Region.X - (int)Math.Round(frame.Width * 0.005)),
                Math.Max(
                    indicator.Region.Width + (int)Math.Round(frame.Width * 0.020),
                    (int)Math.Round(frame.Width * 0.090))),
            Math.Min(
                frame.Height - Math.Max(
                    0,
                    indicator.Region.Y - (int)Math.Round(frame.Height * 0.012)),
                Math.Max(
                    indicator.Region.Height + (int)Math.Round(frame.Height * 0.018),
                    (int)Math.Round(frame.Height * 0.060))));
        var actionRowTop = expandedIndicatorRegion.Y / (double)frame.Height;
        var actionRowHeight = expandedIndicatorRegion.Height /
                              (double)frame.Height;
        // Horizontal positions are fixed by the left timeline layout. Using
        // the template match width here is unstable because the same row can
        // match at several scales; that previously clipped the leading digit
        // from values such as 76.
        var roundRegion = RelativePixelRegion(
            frame,
            expandedIndicatorRegion,
            0.24,
            0.05,
            0.34,
            0.90);
        // The round counter and the remaining-action value are two separate
        // fields on the same timeline row.  The old 5%-10% crop included both
        // fields, so values such as "0 | 72" reached OCR as "0172".  The
        // fallback digit matcher then sometimes kept only the final glyph and
        // reported 2.  Keep this crop on the value column only.  These are
        // normalized coordinates from the stable 16:9 timeline layout, not a
        // resolution-specific fixture adjustment; the width still fits the
        // legal three-digit value 100 at every supported resolution.
        var valueRegion = new NormalizedRect(
            0.0645,
            actionRowTop,
            0.027,
            actionRowHeight);
        var wideValueFallbackRegion = new NormalizedRect(
            0.050,
            actionRowTop,
            0.050,
            actionRowHeight);
        // The action-row templates are locators only. Their source images
        // contain a concrete round/value pair (for example 1 + 28), while the
        // same row moves through the timeline and later displays round 0.
        // Reusing the round encoded in the template id would turn 0 + 95 into
        // 1 + 95. Always read both current numbers from the captured frame.
        var roundTask = ReadNumericTextAsync(
            frame,
            roundRegion,
            cancellationToken,
            allowEnlargedFallback: true);
        var valueTask = ReadNumericTextAsync(
            frame,
            valueRegion,
            cancellationToken,
            allowEnlargedFallback: true);
        await Task.WhenAll(roundTask, valueTask).ConfigureAwait(false);
        var roundText = roundTask.Result;
        var valueText = valueTask.Result;
        var rounds = roundText.SelectMany(text => IntegerPattern().Matches(text)
                .Select(match => int.Parse(
                    match.Value,
                    CultureInfo.InvariantCulture)))
            .Where(value => value is >= 0 and <= 1)
            .Distinct()
            .ToArray();
        var parsedValueLines = valueText
            .Select(text => IntegerPattern().Matches(text)
                .Select(match => int.Parse(
                    match.Value,
                    CultureInfo.InvariantCulture))
                .Where(value => value is >= 0 and <= 100)
                .ToArray())
            .Where(line => line.Length > 0)
            .ToArray();
        var trailingValueRanks = parsedValueLines
            .Select(line => line[^1])
            .GroupBy(value => value)
            .Select(group => (Value: group.Key, Count: group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenByDescending(item => item.Value)
            .ToArray();
        var allValues = parsedValueLines
            .SelectMany(line => line)
            .Where(value => value is >= 0 and <= 100)
            .Distinct()
            .ToArray();
        var values = trailingValueRanks.Length > 0 &&
                     trailingValueRanks[0].Count >= 2 &&
                     (trailingValueRanks.Length == 1 ||
                      trailingValueRanks[0].Count >
                      trailingValueRanks[1].Count)
            ? [trailingValueRanks[0].Value]
            : allValues;
        UiDigitSequenceRecognition? roundDigitFallback = null;
        if (rounds.Length != 1)
        {
            roundDigitFallback = _uiDigitRecognizer.Recognize(
                frame,
                roundRegion.ToPixels(frame.Width, frame.Height),
                iconTemplates,
                0,
                1);
            if (roundDigitFallback.IsRecognized &&
                roundDigitFallback.Confidence >= 0.52)
            {
                rounds = [roundDigitFallback.Value!.Value];
            }
        }
        if (rounds.Length != 1)
        {
            var repeatedRound = await ReadRepeatedNumericValuesAsync(
                    frame,
                    roundRegion,
                    0,
                    1,
                    cancellationToken)
                .ConfigureAwait(false);
            if (repeatedRound.Length == 1)
            {
                rounds = repeatedRound;
            }
        }

        UiDigitSequenceRecognition? digitFallback = null;
        var cleanSingleValueOcr = values.Length == 1 && valueText.Any(text =>
            Regex.IsMatch(
                text.Trim(),
                "^[0-9]{1,3}$",
                RegexOptions.CultureInvariant) &&
            int.TryParse(
                text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) &&
            parsed == values[0]);
        if (values.Length != 1 || values[0] < 10 || !cleanSingleValueOcr)
        {
            digitFallback = _uiDigitRecognizer.Recognize(
                frame,
                valueRegion.ToPixels(frame.Width, frame.Height),
                iconTemplates,
                0,
                100);
            if (digitFallback.IsRecognized &&
                (digitFallback.Value!.Value != 0 ||
                 digitFallback.Confidence >= 0.62) &&
                (values.Length != 1 ||
                 !cleanSingleValueOcr ||
                 digitFallback.Value.Value == values[0]))
            {
                values = [digitFallback.Value.Value];
            }
            else if (!cleanSingleValueOcr)
            {
                values = [];
            }
        }
        if (values.Length != 1 &&
            !(allValues.Length == 1 && !cleanSingleValueOcr))
        {
            var repeatedValue = await ReadRepeatedNumericValuesAsync(
                    frame,
                    valueRegion,
                    0,
                    100,
                    cancellationToken)
                .ConfigureAwait(false);
            if (repeatedValue.Length == 1)
            {
                values = repeatedValue;
            }
        }
        if (values.Length != 1)
        {
            // Heavy glow can erase the leading edge of a digit in the tight
            // crop.  Preserve the proven wider digit-template path only as a
            // bounded fallback, and require at least two glyphs so a combined
            // "round | value" row cannot collapse to one trailing digit.
            var wideDigitFallback = _uiDigitRecognizer.Recognize(
                frame,
                wideValueFallbackRegion.ToPixels(frame.Width, frame.Height),
                iconTemplates,
                0,
                100);
            if (wideDigitFallback.IsRecognized &&
                wideDigitFallback.Glyphs.Count >= 2)
            {
                digitFallback = wideDigitFallback;
                values = [wideDigitFallback.Value!.Value];
            }
        }
        int? resolvedRound = rounds.Length == 1 ? rounds[0] : null;
        if (resolvedRound is null || values.Length != 1)
        {
            return Observation<RemainingActionValueState>.Unknown(
                "已定位行动值行，但数字分区 OCR 不唯一",
                [evidence with
                {
                    Locator = "ocr:located-action-value",
                    Summary = $"round={string.Join(',', roundText)}; " +
                              $"round-digit-fallback={roundDigitFallback?.FailureReason}; " +
                              $"value={string.Join(',', valueText)}; " +
                              $"digit-fallback={digitFallback?.FailureReason}; " +
                              $"match={indicator.Confidence:F3}"
                }],
                frame.CapturedAt);
        }

        var usedRoundDigitFallback = roundDigitFallback?.IsRecognized == true;
        var usedDigitFallback = digitFallback?.IsRecognized == true;
        var templateRound = ParseActionIndicatorTemplateRound(
            indicator.TemplateId);
        if (indicator.Confidence < 0.45 &&
            templateRound is not null &&
            templateRound.Value != resolvedRound.Value)
        {
            return Observation<RemainingActionValueState>.Unknown(
                "Low-confidence action-row match disagreed with the round visible in its locator template.",
                [evidence with
                {
                    Locator = "template:located-action-value-round-conflict",
                    Summary = $"template-round={templateRound.Value}; " +
                              $"recognized-round={resolvedRound.Value}; " +
                              $"value={values[0]}; region={indicator.Region}; " +
                              $"match={indicator.Confidence:F3}"
                }],
                frame.CapturedAt);
        }
        if (indicator.Confidence < 0.55 && values[0] < 10)
        {
            return Observation<RemainingActionValueState>.Unknown(
                "A weak action-row match produced a single-digit value; this can be the persistent 1/1 wave counter.",
                [evidence with
                {
                    Locator = "template:weak-action-value-wave-counter-guard",
                    Summary = $"round={resolvedRound.Value}; value={values[0]}; " +
                              $"region={indicator.Region}; " +
                              $"match={indicator.Confidence:F3}"
                }],
                frame.CapturedAt);
        }
        return Observation<RemainingActionValueState>.Known(
            RemainingActionValueState.Create(resolvedRound.Value, values[0]),
            usedDigitFallback || usedRoundDigitFallback
                ? Math.Min(
                    0.72,
                    Math.Min(
                        usedDigitFallback ? digitFallback!.Confidence : 1,
                        usedRoundDigitFallback
                            ? roundDigitFallback!.Confidence
                            : 1))
                : Math.Min(0.85, indicator.Confidence),
            [evidence with
            {
                Locator = usedDigitFallback || usedRoundDigitFallback
                    ? "template:located-action-value-digits"
                    : "ocr:located-action-value",
                Summary = usedDigitFallback || usedRoundDigitFallback
                    ? $"round={resolvedRound.Value}; value={values[0]}; " +
                      $"round-ocr={string.Join(',', roundText)}; " +
                      $"value-ocr={string.Join(',', valueText)}; " +
                      $"round-digit-confidence={roundDigitFallback?.Confidence:F3}; " +
                      $"value-digit-confidence={digitFallback?.Confidence:F3}; " +
                      $"region={indicator.Region}; match={indicator.Confidence:F3}"
                    : $"{resolvedRound.Value} + {values[0]}; " +
                      $"region={indicator.Region}; match={indicator.Confidence:F3}"
            }],
            frame.CapturedAt);
    }

    private static int? ParseActionIndicatorTemplateRound(string templateId)
    {
        var match = Regex.Match(
            templateId,
            "^round-(?<round>[0-5])-action-",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success
            ? int.Parse(match.Groups["round"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private async Task<int[]> ReadRepeatedNumericValuesAsync(
        CaptureFrame frame,
        NormalizedRect region,
        int minimum,
        int maximum,
        CancellationToken cancellationToken)
    {
        var repeated = CaptureFramePreprocessor.CreateRepeatedEnlargedCrop(
            frame,
            region.ToPixels(frame.Width, frame.Height));
        var recognized = await RecognizeTextSafelyAsync(
            _numericOcr,
            repeated,
            new PixelRect(0, 0, repeated.Width, repeated.Height),
            cancellationToken).ConfigureAwait(false);
        var ranked = RankRepeatedIntegers(recognized, minimum, maximum);
        return ranked.Length > 0 && ranked[0].Count >= 2 &&
               (ranked.Length == 1 || ranked[0].Count > ranked[1].Count)
            ? [ranked[0].Value]
            : [];
    }

    /// <summary>
    /// 商店等级（'购买经验 Lv.X'）识别：robust OCR（放大+增强）为主——
    /// 区域含 "Lv.7" 整串，LevelPattern 提取数字；暗色小字普通 OCR/数字
    /// 模板都读不出（2026-08-08 实测恒 0）。数字模板作辅助兜底。
    /// </summary>
    private async Task<Observation<int>> ReadStoreLevelAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        // 2026-08-17 用户拍板路线：商店等级走 OCR（PpOcrOfflineOcr
        // 开源识别，不用 Windows 内置 OCR）——legacy 区（0.148-0.243,
        // 0.79-0.86）优先：旧版 UI 小数字在区内（000032 实测 Lv.7），
        // 新版 UI 大数字的左半也在区内（000006 实测 PpOcr 读 "4"）。
        var robustLines = await ReadTextRobustAsync(
                frame,
                Phase2RecognitionRegions.StoreLevelValue,
                _storeLevelOcr,
                cancellationToken)
            .ConfigureAwait(false);
        var joined = string.Join(" ", robustLines);
        var levelMatch = LevelPattern().Match(joined);
        if (levelMatch.Success &&
            int.TryParse(
                levelMatch.Groups["level"].Value,
                CultureInfo.InvariantCulture,
                out var level) &&
            level is >= 3 and <= 10)
        {
            return Observation<int>.Known(
                level,
                0.66,
                [evidence with
                {
                    Locator = "ocr:store-level",
                    Summary = $"商店 Lv={level}（{joined}）"
                }],
                frame.CapturedAt);
        }

        var values = ParseIntegerValues(robustLines, 3, 10);
        if (values.Length == 1)
        {
            return Observation<int>.Known(
                values[0],
                0.62,
                [evidence with
                {
                    Locator = "ocr:store-level-digits",
                    Summary = $"商店 Lv 数字={values[0]}（{joined}）"
                }],
                frame.CapturedAt);
        }

        // 2026-08-19「storeLevel 修复」：PpOcr 常把按钮 "Lv.5" 误读成 "15"
        //（'L'→'1'），而商店等级只有 3-6（用户规则）。OCR 读到 "1[3-6]"
        //（十位 1 = 'L/'I' 误读）时，还原其个位为真实等级——否则 15/14
        // 被 3-10 校验拒绝 → Unknown → 后台格数(人口-storeLevel)错 → 后台崩。
        var lvMisread = new List<int>();
        // 用 joined（DirectTexts+Enlarged 合并后的整串）匹配，比逐行更可靠。
        foreach (System.Text.RegularExpressions.Match m in
            StoreLevelMisreadPattern.Matches(joined))
        {
            if (int.TryParse(
                m.Groups["digit"].Value,
                CultureInfo.InvariantCulture,
                out var lv))
            {
                lvMisread.Add(lv);
            }
        }
        lvMisread = lvMisread.Distinct().ToList();
        if (lvMisread.Count == 1)
        {
            return Observation<int>.Known(
                lvMisread[0],
                0.66,
                [evidence with
                {
                    Locator = "ocr:store-level-misread-recovered",
                    Summary = $"商店 Lv={lvMisread[0]}（OCR 误读 'Lv.x'→'1x' 还原个位：{joined}）"
                }],
                frame.CapturedAt);
        }

        // 兜底：数字模板 + 放大反相拉伸预处理（黑字反相变白，模板可识别；
        // 区域含 "Lv." 前缀会被误识 "l"→1、"v"→7，取最右侧 glyph 还原）。
        var region = Phase2RecognitionRegions.StoreLevelBigDigit;
        var pixelRegion = region.ToPixels(frame.Width, frame.Height);
        var enhanced = CreateInvertedStretchedCrop(frame, pixelRegion);
        var localized = _uiDigitRecognizer.Recognize(
            enhanced,
            new PixelRect(0, 0, enhanced.Width, enhanced.Height),
            iconTemplates,
            1,
            20,
            UiDigitForegroundStyle.BrightOnDark,
            includeSmallSlidingGlyphs: true);
        if (localized.IsRecognized && localized.Value is { } digitValue)
        {
            var resolvedValue = localized.Glyphs
                .OrderByDescending(g => g.Region.X)
                .First().Digit;
            if (resolvedValue == 0)
            {
                resolvedValue = 10;
            }

            if (resolvedValue is >= 1 and <= 20)
            {
                return Observation<int>.Known(
                    resolvedValue,
                    Math.Min(0.72, localized.Confidence),
                    [evidence with
                    {
                        Locator = "template:store-level-inverted",
                        Summary = $"商店 Lv={resolvedValue} " +
                                  $"(raw={digitValue}); " +
                                  $"confidence={localized.Confidence:F3}"
                    }],
                    frame.CapturedAt);
            }
        }

        return Observation<int>.Unknown(
            $"商店 Lv 未识别：OCR {joined}；模板 {localized.FailureReason}",
            [evidence with
            {
                Locator = "ocr:store-level",
                Summary = $"商店 Lv 区域 OCR 输出：{joined}"
            }],
            frame.CapturedAt);
    }

        /// <summary>
    /// 后台槽位 = 6 + (人口 - 商店等级)，n ∈ {0,1,2,3}（用户 2026-08-15
    /// 拍板逻辑）。人口或商店等级识别失败时返回 null（调用方回退检测器）。
    /// </summary>
    private static int? DeriveBackSlotCount(
        Observation<int> population,
        Observation<int> storeLevel)
    {
        if (population.Status != ObservationStatus.Known ||
            storeLevel.Status != ObservationStatus.Known)
        {
            return null;
        }

        var n = population.Value - storeLevel.Value;
        return n is >= 0 and <= 3 ? 6 + n : null;
    }

    private static CaptureFrame CreateInvertedStretchedCrop(
        CaptureFrame frame,
        PixelRect region)
    {
        var enlarged = CaptureFramePreprocessor.CreateEnlargedCrop(
            frame,
            region);
        var pixels = enlarged.BgraPixels;
        var min = 255;
        var max = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = (pixels[i] * 29 +
                       pixels[i + 1] * 150 +
                       pixels[i + 2] * 77) >> 8;
            if (lum < min)
            {
                min = lum;
            }

            if (lum > max)
            {
                max = lum;
            }
        }

        if (max - min < 12)
        {
            return enlarged;
        }

        var range = max - min;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = (pixels[i] * 29 +
                       pixels[i + 1] * 150 +
                       pixels[i + 2] * 77) >> 8;
            var stretched = (lum - min) * 255 / range;
            var inverted = 255 - stretched;
            pixels[i] = (byte)inverted;
            pixels[i + 1] = (byte)inverted;
            pixels[i + 2] = (byte)inverted;
        }

        return enlarged;
    }


    private async Task<Observation<int>> ReadPopulationAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        // 2026-08-20 ISOLATION-CHECK：临时改回 ReadTextAsync 测耗时，勿保留
        var lines = await ReadTextAsync(
                frame,
                Phase2RecognitionRegions.PreparationPopulation,
                cancellationToken,
                allowEnlargedFallback: true)
            .ConfigureAwait(false);
        var joined = string.Join(" ", lines);
        if (TryParsePopulationText(
                joined,
                out var population,
                out var recoveredSeparatorArtifact))
        {
            return Observation<int>.Known(
                population,
                recoveredSeparatorArtifact ? 0.62 : 0.68,
                [evidence with
                {
                    Locator = "ocr:population",
                    Summary = recoveredSeparatorArtifact
                        ? $"人口={joined} → {population}（图标/分隔符误读为 1）"
                        : $"人口={joined} → {population}"
                }],
                frame.CapturedAt);
        }

        return Observation<int>.Unknown(
            $"人口 N/N 未识别：{joined}",
            [evidence with
            {
                Locator = "ocr:population",
                Summary = $"人口区域 OCR 输出：{joined}"
            }],
            frame.CapturedAt);
    }

    internal static bool TryParsePopulationText(
        string text,
        out int population,
        out bool recoveredSeparatorArtifact)
    {
        population = 0;
        recoveredSeparatorArtifact = false;

        var match = ExperiencePattern().Match(text);
        if (match.Success &&
            int.TryParse(
                match.Groups["next"].Value,
                CultureInfo.InvariantCulture,
                out population) &&
            population is >= 3 and <= 13)
        {
            return true;
        }

        population = 0;

        var compact = string.Concat(text.Where(character =>
            !char.IsWhiteSpace(character)));
        for (var candidate = 3; candidate <= 13; candidate++)
        {
            if (!compact.Equals(
                    $"1{candidate}1{candidate}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            population = candidate;
            recoveredSeparatorArtifact = true;
            return true;
        }

        return false;
    }

    private async Task<Observation<PlayerProgressState>> ReadProgressAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        // The front-line capacity denominator is the player level and is much
        // larger on screen than the tiny "Lv." glyph. Read both regions in
        // parallel, then require consistency when both are available.
        var capacityTextTask = ReadNumericTextAsync(
            frame,
            Phase2RecognitionRegions.PreparationFrontCapacity,
            cancellationToken);
        IReadOnlyList<string> lines;
        if (!_enableRobustFallback && _numericOcr.IsAvailable)
        {
            lines = await ReadNumericTextAsync(
                frame,
                Phase2RecognitionRegions.LevelAndExperience,
                cancellationToken).ConfigureAwait(false);
            var fastJoined = string.Join(" ", lines);
            if (!LevelPattern().IsMatch(fastJoined) ||
                !ExperiencePattern().IsMatch(fastJoined))
            {
                var enlarged = await ReadEnlargedNumericTextAsync(
                    frame,
                    Phase2RecognitionRegions.LevelAndExperience,
                    cancellationToken).ConfigureAwait(false);
                lines = lines.Concat(enlarged)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }
        else
        {
            lines = await ReadNumericTextAsync(
                frame,
                Phase2RecognitionRegions.LevelAndExperience,
                cancellationToken).ConfigureAwait(false);
        }
        var capacityLines = await capacityTextTask.ConfigureAwait(false);
        var joined = string.Join(" ", lines);
        var capacityJoined = string.Join(" ", capacityLines);
        var level = LevelPattern().Match(joined);
        var experience = ExperiencePattern().Match(joined);
        var capacity = ExperiencePattern().Match(capacityJoined);
        var capacityDenominator = CapacityDenominatorPattern().Match(
            capacityJoined);
        int? explicitLevel = level.Success
            ? int.Parse(
                level.Groups["level"].Value,
                CultureInfo.InvariantCulture)
            : null;
        int? capacityLevel = null;
        var capacityLevelText = capacity.Success
            ? capacity.Groups["next"].Value
            : capacityDenominator.Success
                ? capacityDenominator.Groups["next"].Value
                : null;
        if (capacityLevelText is not null &&
            int.TryParse(
                capacityLevelText,
                CultureInfo.InvariantCulture,
                out var parsedCapacity) &&
            parsedCapacity is >= 1 and <= 10)
        {
            capacityLevel = parsedCapacity;
        }

        if (explicitLevel is not null &&
            capacityLevel is not null &&
            explicitLevel != capacityLevel)
        {
            return Observation<PlayerProgressState>.Conflict(
                [$"等级文字为 {explicitLevel}，前台容量上限为 {capacityLevel}。"],
                [evidence with
                {
                    Locator = "ocr:player-progress+front-capacity",
                    Summary = $"progress={joined} | capacity={capacityJoined}"
                }],
                frame.CapturedAt);
        }

        int? localizedLevel = null;
        if (explicitLevel is null && capacityLevel is null)
        {
            var localized = await ReadRepeatedNumericValuesAsync(
                    frame,
                    Phase2RecognitionRegions.PlayerLevelDigits,
                    1,
                    10,
                    cancellationToken)
                .ConfigureAwait(false);
            localizedLevel = localized.Length == 1 ? localized[0] : null;
        }
        var resolvedLevel = explicitLevel ?? capacityLevel ?? localizedLevel;
        if (resolvedLevel == 10 && !experience.Success)
        {
            return Observation<PlayerProgressState>.Known(
                new PlayerProgressState(10, 0, 0),
                0.68,
                [evidence with
                {
                    Locator = "ocr:player-progress",
                    Summary = $"{joined} | capacity={capacityJoined} (max level)"
                }],
                frame.CapturedAt);
        }

        if (resolvedLevel is null || !experience.Success)
        {
            return Observation<PlayerProgressState>.Unknown(
                "等级或经验 OCR 不完整",
                [evidence with
                {
                    Locator = "ocr:player-progress",
                    Summary = $"progress={joined} | capacity={capacityJoined}"
                }],
                frame.CapturedAt);
        }

        return Observation<PlayerProgressState>.Known(
            new PlayerProgressState(
                resolvedLevel.Value,
                int.Parse(experience.Groups["current"].Value, CultureInfo.InvariantCulture),
                int.Parse(experience.Groups["next"].Value, CultureInfo.InvariantCulture)),
            explicitLevel is not null && capacityLevel is not null
                ? 0.74
                : capacityLevel is not null
                    ? 0.70
                    : localizedLevel is null ? 0.68 : 0.64,
            [evidence with
            {
                Locator = capacityLevel is not null
                    ? "ocr:player-progress+front-capacity"
                    : localizedLevel is null
                        ? "ocr:player-progress"
                        : "ocr:player-progress+localized-level",
                Summary = capacityLevel is not null
                    ? $"progress={joined} | capacity={capacityJoined}"
                    : localizedLevel is null
                        ? joined
                        : $"{joined} | localized-level={localizedLevel.Value}"
            }],
            frame.CapturedAt);
    }

    /// <summary>
    /// 备战页节点号解析（页面/数字解耦，用户 2026-08-04 方案）：
    /// 1. 页面状态 = 分类器页面 ID（preparation_*）确认这是备战页；
    /// 2. 节点数字 = PaddleOCR（_numericOcr）读顶部"备战阶段 X-X"整行——
    ///    大区域带"备战阶段"上下文，1/9 区分远强于小区域数字模板；
    /// 3. 自研数字识别器（小区域 nodeResult）仅作后备。
    /// 旧方案按节点做模板（preparation_1_1/1_2），1-3/1-4 备战帧会被
    /// 1-2 模板误匹配（"备战阶段"标题相似度可达 0.914>0.9），污染节点。
    /// </summary>
    private async Task<Observation<string>> ResolvePreparationNodeAsync(
        CaptureFrame frame,
        string configuredPageId,
        Observation<string> smallRegionNode,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        // 页面 ID 提取（分类器给出的 preparation_1_x 依然可用，但模板已删，
        // 实际只有 preparation_generic；这里保留页面ID路径作为第一优先，
        // 兼容历史测试与外部调用）。
        if (PreparationNodeFromPageId(configuredPageId) is { } fromPage)
        {
            return Observation<string>.Known(
                fromPage,
                0.9,
                [evidence with
                {
                    Locator = "page-id:preparation-node",
                    Summary = $"页面 ID 含节点号 {fromPage}"
                }],
                frame.CapturedAt);
        }

        // PaddleOCR 大区域："备战阶段 X-X"整行（PreparationNode 区域）。
        if (_numericOcr.IsAvailable)
        {
            var pixelRegion = Phase2RecognitionRegions.PreparationNode
                .ToPixels(frame.Width, frame.Height);
            var fullText = await RecognizeTextSafelyAsync(
                    _numericOcr,
                    frame,
                    pixelRegion,
                    cancellationToken)
                .ConfigureAwait(false);
            var lines = DistinctTexts(fullText);
            var joined = string.Join(" ", lines);
            var hasPreparationMarker =
                joined.Contains("备战阶段", StringComparison.Ordinal) ||
                joined.Contains("前台区域", StringComparison.Ordinal) ||
                joined.Contains("后台区域", StringComparison.Ordinal) ||
                joined.Contains("购买经验", StringComparison.Ordinal);
            var nodeValues = ParseNodeValues(lines);
            if (hasPreparationMarker && nodeValues.Length == 1)
            {
                var resolved = PreparationNodeFromPageId(
                    $"preparation_{nodeValues[0]}");
                if (resolved is not null)
                {
                    return Observation<string>.Known(
                        resolved,
                        0.8,
                        [evidence with
                        {
                            Locator = "paddle:preparation-node-full-text",
                            Summary = $"备战页大区域 OCR 读到 {joined} → 节点 {resolved}"
                        }],
                        frame.CapturedAt);
                }
            }
        }

        // 后备：小区域自研数字识别器（nodeResult 已由调用方计算）。
        if (smallRegionNode.Status == ObservationStatus.Known)
        {
            var resolved = PreparationNodeFromPageId(
                $"preparation_{smallRegionNode.Value}");
            if (resolved is not null)
            {
                return Observation<string>.Known(
                    resolved,
                    smallRegionNode.Confidence,
                    smallRegionNode.Evidence,
                    smallRegionNode.ObservedAt);
            }
        }

        return Observation<string>.Unknown(
            "备战页节点号无法可靠识别（大区域 OCR 与数字模板均失败）。",
            [evidence],
            frame.CapturedAt);
    }

    private async Task<Observation<string>> ReadNodeAsync(
        CaptureFrame frame,
        NormalizedRect region,
        string locator,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        if (!_enableRobustFallback && _numericOcr.IsAvailable)
        {
            var numericLines = await ReadNumericTextAsync(
                frame,
                region,
                cancellationToken).ConfigureAwait(false);
            var numericValues = ParseNodeValues(numericLines);
            var values = numericValues;
            var resolvedByNumericOcr = numericValues.Length == 1;
            if (values.Length != 1)
            {
                var enlarged = await ReadEnlargedNumericTextAsync(
                    frame,
                    region,
                    cancellationToken).ConfigureAwait(false);
                var enlargedValues = ParseNodeValues(enlarged);
                if (enlargedValues.Length == 1)
                {
                    values = enlargedValues;
                    resolvedByNumericOcr = true;
                }
            }
            var localized = values.Length == 1
                ? null
                : ReadLocalizedNodeDigits(
                    frame,
                    region,
                    locator,
                    evidence,
                    out _);
            if (localized is not null)
            {
                return localized;
            }
            return values.Length == 1
                ? Observation<string>.Known(
                    values[0],
                    resolvedByNumericOcr ? 0.72 : 0.65,
                    [evidence with
                    {
                        Locator = $"ocr:{locator}",
                        Summary = values[0]
                    }],
                    frame.CapturedAt)
                : Observation<string>.Unknown(
                    values.Length == 0
                        ? "node OCR did not produce a value"
                        : "node OCR produced conflicting values",
                    [evidence with
                    {
                        Locator = $"ocr:{locator}",
                        Summary = string.Join(" | ", numericLines)
                    }],
                    frame.CapturedAt);
        }

        var direct = await ReadNodeOnceAsync(
                frame,
                region,
                locator,
                evidence,
                cancellationToken)
            .ConfigureAwait(false);
        if (direct.Status == ObservationStatus.Known ||
            !_numericOcr.IsAvailable ||
            !_enableRobustFallback)
        {
            return direct;
        }

        var textLines = await ReadTextAsync(frame, region, cancellationToken)
            .ConfigureAwait(false);
        var textValues = textLines.Select(line => IntegerPattern().Matches(line)
                .Select(match => int.Parse(
                    match.Value,
                    CultureInfo.InvariantCulture))
                .ToArray())
            .Where(values => values.Length >= 2 &&
                             values[0] is >= 1 and <= 3 &&
                             values[1] is >= 0 and <= 9)
            .Select(values => $"{values[0]}-{values[1]}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (textValues.Length == 1)
        {
            return Observation<string>.Known(
                textValues[0],
                0.65,
                [evidence with
                {
                    Locator = $"ocr:{locator}-general-fallback",
                    Summary = string.Join(" | ", textLines),
                    Confidence = 0.65
                }],
                frame.CapturedAt);
        }

        var localizedNode = ReadLocalizedNodeDigits(
            frame,
            region,
            locator,
            evidence,
            out var localizedAttempt);
        if (localizedNode is not null)
        {
            return localizedNode;
        }

        return direct with
        {
            Evidence = direct.Evidence.Append(localizedAttempt).ToArray()
        };
    }

    private Observation<string>? ReadLocalizedNodeDigits(
        CaptureFrame frame,
        NormalizedRect region,
        string locator,
        EvidenceReference evidence,
        out EvidenceReference attempt)
    {
        // The separator is only a few pixels wide and can disappear under
        // bright battle effects even when both digits remain clear. Split the
        // normalized field around the separator; no screen-pixel assumption is
        // introduced, so scaling and DPI continue to follow the captured frame.
        var nodePixels = region.ToPixels(frame.Width, frame.Height);
        var chapterRegion = new PixelRect(
            nodePixels.X,
            nodePixels.Y,
            Math.Max(8, (int)Math.Round(nodePixels.Width * 0.45)),
            nodePixels.Height);
        var stageX = nodePixels.X + (int)Math.Round(nodePixels.Width * 0.52);
        var stageRegion = new PixelRect(
            stageX,
            nodePixels.Y,
            Math.Max(8, nodePixels.Right - stageX),
            nodePixels.Height);
        var chapter = _uiDigitRecognizer.Recognize(
            frame,
            chapterRegion,
            iconTemplates,
            1,
            3,
            UiDigitForegroundStyle.BrightOnDark);
        var stage = _uiDigitRecognizer.Recognize(
            frame,
            stageRegion,
            iconTemplates,
            0,
            9,
            UiDigitForegroundStyle.BrightOnDark);
        var confidence = Math.Min(chapter.Confidence, stage.Confidence);
        if (chapter.IsRecognized && stage.IsRecognized)
        {
            var value = $"{chapter.Value!.Value}-{stage.Value!.Value}";
            attempt = evidence with
            {
                Locator = $"template:{locator}-localized-digits",
                Summary = $"value={value}; confidence={confidence:F3}",
                Confidence = confidence
            };
            return Observation<string>.Known(
                value,
                Math.Min(0.68, confidence),
                [attempt],
                frame.CapturedAt);
        }

        attempt = evidence with
        {
            Locator = $"template:{locator}-localized-digits",
            Summary = $"chapter={chapter.FailureReason}; stage={stage.FailureReason}",
            Confidence = confidence
        };
        return null;
    }

    private async Task<Observation<string>> ReadNodeOnceAsync(
        CaptureFrame frame,
        NormalizedRect region,
        string locator,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var lines = await ReadNumericTextAsync(frame, region, cancellationToken)
            .ConfigureAwait(false);
        var values = ParseNodeValues(lines);
        if (values.Length != 1 &&
            _enableRobustFallback &&
            _numericOcr is IAdaptiveOfflineOcr)
        {
            lines = await ReadTextRobustAsync(
                frame,
                region,
                _numericOcr,
                cancellationToken).ConfigureAwait(false);
            values = ParseNodeValues(lines);
        }
        return values.Length == 1
            ? Observation<string>.Known(
                values[0],
                0.72,
                [evidence with { Locator = $"ocr:{locator}", Summary = values[0] }],
                frame.CapturedAt)
            : Observation<string>.Unknown(
                values.Length == 0 ? "节点编号 OCR 未识别" : "节点编号 OCR 冲突",
                [evidence with
                {
                    Locator = $"ocr:{locator}",
                    Summary = string.Join(" | ", lines)
                }],
                frame.CapturedAt);
    }

    private static string[] ParseNodeValues(IReadOnlyList<string> lines) =>
        lines.SelectMany(line => NodePattern().Matches(line)
                .Select(match =>
                    $"{match.Groups["plane"].Value}-{match.Groups["node"].Value}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private async Task<Observation<int>> ReadIntegerWithLocalizedFallbackAsync(
        CaptureFrame frame,
        NormalizedRect ocrRegion,
        NormalizedRect digitRegion,
        int minimum,
        int maximum,
        string locator,
        EvidenceReference evidence,
        CancellationToken cancellationToken,
        UiDigitForegroundStyle foregroundStyle,
        UiDigitForegroundStyle? secondaryForegroundStyle = null)
    {
        var ocrObservation = await ReadIntegerAsync(
                frame,
                ocrRegion,
                minimum,
                maximum,
                locator,
                evidence,
                cancellationToken)
            .ConfigureAwait(false);
        var noisyOcrNeedsCorroboration =
            ocrObservation.Status == ObservationStatus.Known &&
            RequiresCleanNumericToken(locator) &&
            !HasCleanIntegerOcrEvidence(
                ocrObservation.Evidence,
                ocrObservation.Value);
        var localizedEvidenceIsAuthoritative = string.Equals(
            locator,
            "cumulative-spend",
            StringComparison.Ordinal);
        if (ocrObservation.Status == ObservationStatus.Known &&
            !noisyOcrNeedsCorroboration &&
            !localizedEvidenceIsAuthoritative)
        {
            return ocrObservation;
        }

        var localized = _uiDigitRecognizer.Recognize(
            frame,
            digitRegion.ToPixels(frame.Width, frame.Height),
            iconTemplates,
            minimum,
            maximum,
            foregroundStyle);
        if (!localized.IsRecognized && secondaryForegroundStyle is { } secondary)
        {
            var secondaryResult = _uiDigitRecognizer.Recognize(
                frame,
                digitRegion.ToPixels(frame.Width, frame.Height),
                iconTemplates,
                minimum,
                maximum,
                secondary);
            if (secondaryResult.IsRecognized ||
                secondaryResult.Confidence > localized.Confidence)
            {
                localized = secondaryResult;
            }
        }
        if (!localized.IsRecognized)
        {
            if (noisyOcrNeedsCorroboration)
            {
                return Observation<int>.Unknown(
                    $"{locator} OCR contained non-numeric glyphs and the digit template did not corroborate it.",
                    ocrObservation.Evidence.Append(evidence with
                    {
                        Locator = $"template:{locator}-localized-digits",
                        Summary = localized.FailureReason,
                        Confidence = localized.Confidence
                    }),
                    frame.CapturedAt);
            }

            return ocrObservation with
            {
                Evidence = ocrObservation.Evidence.Append(evidence with
                {
                    Locator = $"template:{locator}-localized-digits",
                    Summary = localized.FailureReason,
                    Confidence = localized.Confidence
                }).ToArray()
            };
        }

        if (noisyOcrNeedsCorroboration)
        {
            var localizedEvidence = evidence with
            {
                Locator = $"template:{locator}-localized-digits",
                Summary = $"value={localized.Value!.Value}; " +
                          $"confidence={localized.Confidence:F3}; " +
                          $"runner-up={localized.RunnerUpConfidence:F3}",
                Confidence = localized.Confidence
            };
            if (localized.Value.Value != ocrObservation.Value)
            {
                return new Observation<int>
                {
                    Status = ObservationStatus.Known,
                    Value = localized.Value.Value,
                    Confidence = Math.Min(0.72, localized.Confidence),
                    Evidence = ocrObservation.Evidence
                        .Append(localizedEvidence)
                        .ToArray(),
                    Uncertainty =
                    [
                        $"Ignored noisy {locator} OCR value {ocrObservation.Value}; localized digit evidence resolved {localized.Value.Value}."
                    ],
                    ObservedAt = frame.CapturedAt
                };
            }

            return Observation<int>.Known(
                localized.Value.Value,
                Math.Min(0.72, localized.Confidence),
                ocrObservation.Evidence.Append(localizedEvidence),
                frame.CapturedAt);
        }

        return Observation<int>.Known(
            localized.Value!.Value,
            Math.Min(0.72, localized.Confidence),
            [evidence with
            {
                Locator = $"template:{locator}-localized-digits",
                Summary = $"value={localized.Value.Value}; " +
                          $"confidence={localized.Confidence:F3}; " +
                          $"runner-up={localized.RunnerUpConfidence:F3}",
                Confidence = localized.Confidence
            }],
            frame.CapturedAt);
    }

    private static bool RequiresCleanNumericToken(string locator) =>
        string.Equals(locator, "interest", StringComparison.Ordinal) ||
        string.Equals(locator, "cumulative-spend", StringComparison.Ordinal);

    internal static bool IsCleanIntegerOcrText(string? text, int expectedValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var expected = expectedValue.ToString(CultureInfo.InvariantCulture);
        return text.Split('|', StringSplitOptions.RemoveEmptyEntries |
                               StringSplitOptions.TrimEntries)
            .All(item => string.Equals(item, expected, StringComparison.Ordinal));
    }

    private static bool HasCleanIntegerOcrEvidence(
        IReadOnlyList<EvidenceReference> evidence,
        int expectedValue) =>
        evidence.Any(item =>
            item.Locator.StartsWith("ocr:", StringComparison.OrdinalIgnoreCase) &&
            IsCleanIntegerOcrText(item.Summary, expectedValue));

    private async Task<Observation<int>> ReadIntegerAsync(
        CaptureFrame frame,
        NormalizedRect region,
        int minimum,
        int maximum,
        string locator,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        if (!_enableRobustFallback && _numericOcr.IsAvailable)
        {
            var numericTask = ReadNumericTextAsync(
                frame,
                region,
                cancellationToken);
            await numericTask.ConfigureAwait(false);
            var numericValues = ParseIntegerValues(
                numericTask.Result,
                minimum,
                maximum);
            var values = numericValues;
            return values.Length == 1
                ? Observation<int>.Known(
                    values[0],
                    0.65,
                    [evidence with
                    {
                        Locator = $"ocr:{locator}",
                        Summary = string.Join(" | ", numericTask.Result)
                    }],
                    frame.CapturedAt)
                : Observation<int>.Unknown(
                    values.Length == 0
                        ? $"{locator} OCR did not produce a value"
                        : $"{locator} OCR produced conflicting values",
                    [evidence with
                    {
                        Locator = $"ocr:{locator}",
                        Summary = string.Join(" | ", numericTask.Result)
                    }],
                    frame.CapturedAt);
        }

        var direct = await ReadIntegerOnceAsync(
                frame,
                region,
                minimum,
                maximum,
                locator,
                evidence,
                cancellationToken)
            .ConfigureAwait(false);
        if (direct.Status == ObservationStatus.Known ||
            !_numericOcr.IsAvailable ||
            !_enableRobustFallback)
        {
            return direct;
        }

        var textLines = await ReadTextAsync(frame, region, cancellationToken)
            .ConfigureAwait(false);
        var textValues = ParseIntegerValues(textLines, minimum, maximum);
        if (textValues.Length != 1 &&
            _enableRobustFallback &&
            ocr is IAdaptiveOfflineOcr)
        {
            textLines = await ReadTextRobustAsync(
                frame,
                region,
                ocr,
                cancellationToken).ConfigureAwait(false);
            textValues = ParseIntegerValues(textLines, minimum, maximum);
        }
        if (textValues.Length == 1)
        {
            return Observation<int>.Known(
                textValues[0],
                0.60,
                [evidence with
                {
                    Locator = $"ocr:{locator}-general-fallback",
                    Summary = string.Join(" | ", textLines),
                    Confidence = 0.60
                }],
                frame.CapturedAt);
        }

        return direct;
    }

    private async Task<Observation<int>> ReadIntegerOnceAsync(
        CaptureFrame frame,
        NormalizedRect region,
        int minimum,
        int maximum,
        string locator,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var lines = await ReadNumericTextAsync(frame, region, cancellationToken)
            .ConfigureAwait(false);
        var values = ParseIntegerValues(lines, minimum, maximum);
        if (values.Length != 1 &&
            _enableRobustFallback &&
            _numericOcr is IAdaptiveOfflineOcr)
        {
            lines = await ReadTextRobustAsync(
                frame,
                region,
                _numericOcr,
                cancellationToken).ConfigureAwait(false);
            values = ParseIntegerValues(lines, minimum, maximum);
        }
        return values.Length == 1
            ? Observation<int>.Known(
                values[0],
                0.65,
                [evidence with
                {
                    Locator = $"ocr:{locator}",
                    Summary = string.Join(" | ", lines)
                }],
                frame.CapturedAt)
            : Observation<int>.Unknown(
                values.Length == 0 ? $"{locator} OCR 未识别" : $"{locator} OCR 冲突",
                [evidence with
                {
                    Locator = $"ocr:{locator}",
                    Summary = string.Join(" | ", lines)
                }],
                frame.CapturedAt);
    }

    private static int[] ParseIntegerValues(
        IReadOnlyList<string> lines,
        int minimum,
        int maximum) =>
        lines.SelectMany(line => IntegerPattern().Matches(line)
                .Select(match => int.TryParse(match.Value, out var value)
                    ? (int?)value
                    : null))
            .Where(value => value >= minimum && value <= maximum)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();

    private Task<IReadOnlyList<string>> ReadTextCachedAsync(
        CaptureFrame frame,
        NormalizedRect region,
        ConcurrentDictionary<NormalizedRect, Lazy<Task<IReadOnlyList<string>>>>
            frameOcrCache,
        CancellationToken cancellationToken) =>
        frameOcrCache.GetOrAdd(
            region,
            _ => new Lazy<Task<IReadOnlyList<string>>>(
                () => ReadTextAsync(frame, region, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private async Task<IReadOnlyList<string>> ReadTextAsync(
        CaptureFrame frame,
        NormalizedRect region,
        CancellationToken cancellationToken,
        bool allowEnlargedFallback = false)
    {
        if (!ocr.IsAvailable)
        {
            return [];
        }

        var pixelRegion = region.ToPixels(frame.Width, frame.Height);
        var result = await RecognizeTextSafelyAsync(
            ocr,
            frame,
            pixelRegion,
            cancellationToken).ConfigureAwait(false);
        var directTexts = DistinctTexts(result);
        if (directTexts.Count > 0)
        {
            return directTexts;
        }

        if (!_enableRobustFallback && !allowEnlargedFallback)
        {
            return directTexts;
        }

        var enlarged = CaptureFramePreprocessor.CreateEnlargedCrop(
            frame,
            pixelRegion);
        var enlargedResult = await RecognizeTextSafelyAsync(
            ocr,
            enlarged,
            new PixelRect(0, 0, enlarged.Width, enlarged.Height),
            cancellationToken).ConfigureAwait(false);
        return DistinctTexts(enlargedResult);
    }

    private async Task<IReadOnlyList<string>> ReadNumericTextOnceAsync(
        CaptureFrame frame,
        NormalizedRect region,
        CancellationToken cancellationToken)
    {
        // 伤害行 OCR：正常只识别一次（性能优先，用户 2026-08-06 要求）。
        // 仅当该行完全没读到候选时，放大补救一次（保伤害数据完整）——
        // 不是每行都双份识别，失败容错不影响正常帧性能。
        var texts = await ReadNumericTextAsync(
                frame,
                region,
                cancellationToken,
                allowEnlargedFallback: false,
                allowRobustFallback: false)
            .ConfigureAwait(false);
        if (texts.Count > 0)
        {
            return texts;
        }

        return await ReadNumericTextAsync(
                frame,
                region,
                cancellationToken,
                allowEnlargedFallback: true)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ReadNumericTextAsync(
        CaptureFrame frame,
        NormalizedRect region,
        CancellationToken cancellationToken,
        bool allowEnlargedFallback = false,
        bool allowRobustFallback = true)
    {
        if (!_numericOcr.IsAvailable)
        {
            return await ReadTextAsync(
                    frame,
                    region,
                    cancellationToken,
                    allowEnlargedFallback)
                .ConfigureAwait(false);
        }

        var pixelRegion = region.ToPixels(frame.Width, frame.Height);
        var result = await RecognizeTextSafelyAsync(
            _numericOcr,
            frame,
            pixelRegion,
            cancellationToken).ConfigureAwait(false);
        var directTexts = DistinctTexts(result);
        if (directTexts.Count > 0)
        {
            return directTexts;
        }

        // 用户 2026-08-06 要求：伤害行 OCR 严格只识别一次（allowRobustFallback
        // = false）——不放大重试、不 robust 兜底，性能优先。其余调用默认
        // 保持原有 fallback 行为（识别率优先）。
        if (!allowRobustFallback ||
            (!_enableRobustFallback && !allowEnlargedFallback))
        {
            return directTexts;
        }

        var enlarged = CaptureFramePreprocessor.CreateEnlargedCrop(
            frame,
            pixelRegion);
        var enlargedResult = await RecognizeTextSafelyAsync(
            _numericOcr,
            enlarged,
            new PixelRect(0, 0, enlarged.Width, enlarged.Height),
            cancellationToken).ConfigureAwait(false);
        return DistinctTexts(enlargedResult);
    }

    private static IReadOnlyList<string> DistinctTexts(OcrTextResult result) =>
        result.Lines.Prepend(result.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private async Task<IReadOnlyList<string>> ReadEnlargedNumericTextAsync(
        CaptureFrame frame,
        NormalizedRect region,
        CancellationToken cancellationToken)
    {
        var pixelRegion = region.ToPixels(frame.Width, frame.Height);
        var enlarged = CaptureFramePreprocessor.CreateEnlargedCrop(
            frame,
            pixelRegion);
        var result = await RecognizeTextSafelyAsync(
            _numericOcr,
            enlarged,
            new PixelRect(0, 0, enlarged.Width, enlarged.Height),
            cancellationToken).ConfigureAwait(false);
        return DistinctTexts(result);
    }

    private static async Task<IReadOnlyList<string>> ReadTextRobustAsync(
        CaptureFrame frame,
        NormalizedRect region,
        IOfflineOcr engine,
        CancellationToken cancellationToken,
        int scale = 4)
    {
        var pixelRegion = region.ToPixels(frame.Width, frame.Height);
        var direct = await RecognizeTextSafelyAsync(
            engine,
            frame,
            pixelRegion,
            cancellationToken,
            robust: true).ConfigureAwait(false);
        var enlarged = CaptureFramePreprocessor.CreateEnlargedCrop(
            frame,
            pixelRegion,
            scale);
        var enlargedResult = await RecognizeTextSafelyAsync(
            engine,
            enlarged,
            new PixelRect(0, 0, enlarged.Width, enlarged.Height),
            cancellationToken,
            robust: true).ConfigureAwait(false);
        return DistinctTexts(direct)
            .Concat(DistinctTexts(enlargedResult))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static async ValueTask<OcrTextResult> RecognizeTextSafelyAsync(
        IOfflineOcr engine,
        CaptureFrame frame,
        PixelRect region,
        CancellationToken cancellationToken,
        bool robust = false)
    {
        try
        {
            return robust && engine is IAdaptiveOfflineOcr adaptive
                ? await adaptive.RecognizeRobustAsync(
                    frame,
                    region,
                    cancellationToken).ConfigureAwait(false)
                : await engine.RecognizeAsync(
                    frame,
                    region,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A single OCR region may be covered by a popup or sampled during a
            // transition. Treat only that region as temporarily unavailable;
            // the caller will emit an Unknown observation for the field.
            return new OcrTextResult(string.Empty, []);
        }
    }

    private static Observation<T> PartialUnknown<T>(
        T partialValue,
        string reason,
        EvidenceReference evidence,
        DateTimeOffset observedAt) =>
        new()
        {
            Status = ObservationStatus.Unknown,
            Value = partialValue,
            Confidence = 0,
            Evidence = [evidence],
            Uncertainty = [reason],
            ObservedAt = observedAt
        };

    private IReadOnlyList<Phase2IconRecognition> RecognizeIconsSafely(
        CaptureFrame frame,
        string category,
        IReadOnlyList<NormalizedRect> slots,
        IReadOnlyList<Phase2IconTemplateDefinition> templates)
    {
        try
        {
            return iconRecognizer.Recognize(frame, category, slots, templates);
        }
        catch (Exception)
        {
            return slots.Select((slot, index) => new Phase2IconRecognition(
                    index,
                    slot.ToPixels(frame.Width, frame.Height),
                    null,
                    0,
                    false,
                    [],
                    []))
                .ToArray();
        }
    }

    internal static IReadOnlyList<Phase2FormationSlotObservation>
        BuildFormationSlotObservations(
            IReadOnlyList<CharacterCardSlotRecognition> board,
            IReadOnlyList<CharacterCardSlotRecognition> bench)
    {
        return board.Select(slot => ToObservation(
                slot,
                slot.SlotIndex < 4 ? FormationZone.Front : FormationZone.Back))
            .Concat(bench.Select(slot => ToObservation(slot, FormationZone.Bench)))
            .OrderBy(item => item.Zone)
            .ThenBy(item => item.SlotIndex)
            .ToArray();

        static Phase2FormationSlotObservation ToObservation(
            CharacterCardSlotRecognition slot,
            FormationZone zone) => new(
                zone,
                slot.SlotIndex,
                slot.State switch
                {
                    CharacterCardSlotState.Empty =>
                        Phase2FormationSlotOccupancy.Empty,
                    CharacterCardSlotState.Uncertain =>
                        Phase2FormationSlotOccupancy.Uncertain,
                    _ => Phase2FormationSlotOccupancy.Recognized
                });
    }

    private sealed record SecondaryNumericTextAttempt(
        bool Succeeded,
        IReadOnlyList<string> Texts);

    private static async Task<SecondaryNumericTextAttempt> ReadSecondaryNumericTextOnceAsync(
        ISecondaryOfflineOcr engine,
        CaptureFrame frame,
        NormalizedRect region,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await engine.RecognizeSecondaryAsync(
                    frame,
                    region.ToPixels(frame.Width, frame.Height),
                    cancellationToken)
                .ConfigureAwait(false);
            return new SecondaryNumericTextAttempt(true, DistinctTexts(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new SecondaryNumericTextAttempt(false, []);
        }
    }

    /// <summary>
    /// 场上阵容识别：前台 4 槽用原帧轴对齐矩形；后台槽位是斜视平面
    /// 投影的平行四边形（用户 2026-08-15 实机确认），先按全局单应把
    /// 后台行拉正，再在 warped 帧上按轴对齐矩形识别——否则 9 槽时
    /// 最左最右卡牌套不住、后台漏人。
    /// </summary>
    private IReadOnlyList<CharacterCardSlotRecognition>
        RecognizeBoardWithBackRowPerspective(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> characterTemplates,
            IReadOnlyList<int> selectedBoardIndices,
            IReadOnlyList<PixelRect> boardReferenceSlots,
            CharacterCardRecognitionOptions boardOptions,
            IReadOnlySet<int> cheeredBoardIndices,
            IReadOnlySet<string>? requestedFormationSlots,
            int backSlotCount)
    {
        var frontIndices = selectedBoardIndices
            .Where(index => index < 4)
            .ToArray();
        var backIndices = selectedBoardIndices
            .Where(index => index >= 4)
            .ToArray();
        // cheeredBoardIndices 存场上列表绝对索引（Front 0-3、Back 4-10）；
        // 各段调用须换算成本段索引空间，否则相对索引与绝对索引错位
        //（01433 那可夏=back 相对 0 撞上 Front 的 0 → 被误裁底部 25%
        // → 匹配分压到阈值下 unknown；真应援槽反而拿不到裁剪）。
        var frontCheered = cheeredBoardIndices
            .Where(index => index < 4)
            .ToHashSet();
        // front/back 互不依赖，并行识别（recognizer 模板缓存为 ConcurrentDictionary，
        // 只读 frame，线程安全）。后台 warp 也在独立线程执行。
        IReadOnlyList<CharacterCardSlotRecognition>? frontResult = null;
        IReadOnlyList<CharacterCardSlotRecognition>? backResult = null;
        var frontTask = System.Threading.Tasks.Task.Run(() =>
        {
            if (frontIndices.Length == 0)
            {
                frontResult = [];
                return;
            }
            // 2026-08-21 用户方案：前台角色用三文档精确四角正交投影。
            // 每张卡拉正(111×127)排进 16:9 画布再识别，不再用 Preparation 矩形。
            var (warpedFront, warpedFrontSlots) =
                CalibratedPerspective.WarpRow(
                    frame, CalibrationSlots.FrontQuads1920, 111, 127, 10);
            frontResult = RecognizeCharactersSafely(
                warpedFront,
                characterTemplates,
                frontIndices.Select(index => warpedFrontSlots[index]).ToArray(),
                boardOptions,
                frontCheered,
                starBand: StarBand.FrontCenter,
                absoluteSlotIndices: requestedFormationSlots is null
                    ? null
                    : frontIndices);
        });
        var backTask = System.Threading.Tasks.Task.Run(() =>
        {
            if (backIndices.Length == 0)
            {
                backResult = [];
                return;
            }

            // 透视矫正（用户 2026-08-21 三文档标定四角）：后台行是斜视平面
            // 上的平行四边形卡牌——用 CalibrationSlots.BackQuads1920[人口档] 的
            // 精确四角拉正后识别（取代旧 BackRowPerspectiveGeometry / BackCharacterSlots1920）。
            var backPop = Math.Clamp(backSlotCount, 6, 9);
            var (warpedBack, warpedBackSlots) =
                CalibratedPerspective.WarpRow(
                    frame,
                    CalibrationSlots.BackQuads1920[backPop],
                    111,
                    127,
                    10);
            // 增量路径（absoluteSlotIndices=backIndices 非 null）单槽循环用
            // 绝对索引查 Contains——backCheered 须保留绝对空间；全量路径
            // 用相对 i 查——须减 4。按路径分叉（review 2026-08-16 blocking）。
            var backCheered = requestedFormationSlots is null
                ? cheeredBoardIndices
                    .Where(index => index >= 4)
                    .Select(index => index - 4)
                    .ToHashSet()
                : cheeredBoardIndices
                    .Where(index => index >= 4)
                    .ToHashSet();
            backResult = RecognizeCharactersSafely(
                warpedBack,
                characterTemplates,
                backIndices
                    .Select(index => warpedBackSlots[index - 4])
                    .ToArray(),
                // 2026-08-19 用户拍板方案 A + 第一步：
                // - BackRow=true：让 Recognize 对后台槽用「卡面内缩 6px 裁框」做
                //   角色/模板匹配（剔除能量地块淡淡特效），并对 Match 用后台专属
                //   宽松颜色惩罚触发线(0.80)。rect 保持原样——星级检测依赖原槽位
                //   （内缩会伤星级，见 000032）。前台/备战席保留 0.90 原惩罚。
                boardOptions with { BackRow = true },
                backCheered,
                starBand: StarBand.BackRight,
                absoluteSlotIndices: requestedFormationSlots is null
                    ? null
                    : backIndices);
        });
        System.Threading.Tasks.Task.WaitAll(frontTask, backTask);
        var front = frontResult ?? [];
        var back = backResult ?? [];
        // 识别器返回的相对索引（0..8）重映射回场上绝对槽位（4..12），
        // 否则与前台槽位索引冲突（formation 组装按 (Zone, SlotIndex)
        // 去重，重复 key 抛异常——全量回归抓到）。
        // 批量路径（absoluteSlotIndices==null 且无应援）识别器返回相对
        // 索引（0..N）需重映射；增量路径（absoluteSlotIndices=backIndices
        // 非 null）单槽循环已把 SlotIndex 写成绝对索引（4..12）——直接
        // 透传。注意：不能用数值范围判断（相对 0-6 与绝对 4-10 重叠），
        // 必须按路径区分（review 2026-08-16 should-fix）。
        if (backIndices.Length == 0)
        {
            return front;
        }

        var remapBackSlotIndex = requestedFormationSlots is null;
        return front.Concat(back.Select(slot =>
            remapBackSlotIndex
                ? slot with { SlotIndex = backIndices[slot.SlotIndex] }
                : slot))
            .ToArray();
    }

    private IReadOnlyList<CharacterCardSlotRecognition> RecognizeCharactersSafely(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> slots,
        CharacterCardRecognitionOptions options = default,
        IReadOnlySet<int>? cheeredIndices = null,
        // 场上列表保持单次识别器调用；识别器按完整列表索引选择前/后台星级带。
        // bench 调用仍显式传 BenchRight。
        StarBand starBand = StarBand.BoardBySlotIndex,
        IReadOnlyList<int>? absoluteSlotIndices = null)
    {
        try
        {
            // 应援槽位（打call 特效）：模板匹配避开底部应援棒区域
            //（ExcludeBottomRatio=0.25），否则被应援角色底部污染匹配失败
            //（用户 2026-08-07：000036/000037 银狼应援 → 0.43 卡阈值）。
            if (cheeredIndices is { Count: > 0 } ||
                absoluteSlotIndices is not null)
            {
                var results = new List<CharacterCardSlotRecognition>(slots.Count);
                for (var i = 0; i < slots.Count; i++)
                {
                    var absoluteIndex = absoluteSlotIndices?[i] ?? i;
                    var slotOptions = cheeredIndices?.Contains(absoluteIndex) == true
                        ? options with
                        {
                            // review 2026-08-19 should-fix：应援槽若叠加 BackRow
                            // (0.80 宽松触发线) 会削弱应援棒污染本应触发的颜色惩罚，
                            // 轻微抬升相似角色误判风险——应援路径强制恢复原惩罚线。
                            BackRow = false,
                            ExcludeBottomRatio = 0.25,
                            ExcludeSides = true
                        }
                        : options;
                    var band = ResolveCharacterStarBand(starBand, absoluteIndex);
                    // 单槽识别（应援路径）：Recognize 对单元素列表返回
                    // SlotIndex=0——必须用真实槽位索引覆盖，否则报告
                    // "Front/Back 的格都是 0"（用户 2026-08-08 验收根因）。
                    var recognized = characterRecognizer.Recognize(
                        frame,
                        templates,
                        [slots[i]],
                        (slotOptions == default
                            ? CharacterCardRecognitionOptions.Standard
                            : slotOptions) with { StarBand = band })[0];
                    results.Add(recognized.SlotIndex == absoluteIndex
                        ? recognized
                        : recognized with { SlotIndex = absoluteIndex });
                }

                return results;
            }

            // 非应援路径保持完整槽位列表的一次调用。拆成前/后台两次会让
            // ICharacterCardRecognizer 的局部 SlotIndex 重新从 0 开始，也会
            // 破坏依赖完整列表的降级识别器语义。
            var resolvedOptions = options == default
                ? CharacterCardRecognitionOptions.Standard
                : options;
            return characterRecognizer.Recognize(
                frame,
                templates,
                slots,
                resolvedOptions with { StarBand = starBand });
        }
        catch (Exception)
        {
            return slots.Select((slot, index) => new CharacterCardSlotRecognition(
                    absoluteSlotIndices?[index] ?? index,
                    slot,
                    CharacterCardSlotState.Uncertain,
                    null,
                    null,
                    0,
                    0,
                    0))
                .ToArray();
        }
    }

    private string? LastStablePreparationNodeId(string runId)
    {
        var run = GetStableRun(runId);
        lock (run.Gate)
        {
            return run.LastPreparationNodeId;
        }
    }

    internal static StarBand ResolveCharacterStarBand(
        StarBand requestedBand,
        int slotIndex) => requestedBand switch
        {
            StarBand.BoardBySlotIndex when slotIndex < 4 => StarBand.FrontCenter,
            StarBand.BoardBySlotIndex => StarBand.BackRight,
            _ => requestedBand
        };

    private IReadOnlyList<HorizontalSpecialUnitRecognition>
        RecognizeHorizontalSpecialUnitsSafely(CaptureFrame frame)
    {
        if (_horizontalSpecialUnitRecognizer is null)
        {
            return [];
        }

        try
        {
            return _horizontalSpecialUnitRecognizer.Recognize(frame);
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static string NormalizeDamageNumber(string raw)
    {
        var cleaned = raw.Replace(" ", string.Empty, StringComparison.Ordinal);
        var builder = new System.Text.StringBuilder(cleaned.Length);
        for (var i = 0; i < cleaned.Length; i++)
        {
            var current = cleaned[i];
            if (current is '，' or '·')
            {
                builder.Append('.');
                continue;
            }

            if (current != ',')
            {
                builder.Append(current);
                continue;
            }

            // 半角逗号：其后跟 1~2 位数字时视为小数点（OCR 可能把小数点
            // 误读为逗号）；否则视为千分位分隔符直接丢弃（如 "1,234万"）。
            var digitsAfter = 0;
            for (var j = i + 1; j < cleaned.Length && digitsAfter < 3; j++)
            {
                if (cleaned[j] is >= '0' and <= '9')
                {
                    digitsAfter++;
                }
                else
                {
                    break;
                }
            }

            if (digitsAfter is 1 or 2)
            {
                builder.Append('.');
            }
        }

        return builder.ToString();
    }

    internal static IEnumerable<(long Value, int Score, string Text)>
        ParseDamageCandidates(string text)
    {
        foreach (Match match in DamagePattern().Matches(text))
        {
            var rawNumber = NormalizeDamageNumber(
                match.Groups["number"].Value);
            if (!decimal.TryParse(
                    rawNumber,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var numeric))
            {
                continue;
            }

            var multiplier = match.Groups["unit"].Value switch
            {
                "万" => 10_000m,
                "亿" or "億" => 100_000_000m,
                // 万亿连写支持（review 2026-08-06 建议）：1.5万亿 → 1.5e12
                "万亿" => 1_000_000_000_000m,
                _ => 1m
            };
            var scaled = numeric * multiplier;
            // 上限放宽（C 修复，用户 2026-08-06 深夜确认）：原 1000 亿会
            // 丢弃 3-7 等首领节点大伤害，放宽到 1 万亿。
            if (scaled is < 0 or > 10_000_000_000_000m)
            {
                continue;
            }

            var value = decimal.ToInt64(decimal.Round(
                scaled,
                0,
                MidpointRounding.AwayFromZero));
            var score = match.Groups["unit"].Value.Length > 0 ? 3 : 1;
            if (rawNumber.Contains('.', StringComparison.Ordinal))
            {
                score += 2;
            }

            yield return (value, score, match.Value.Trim());
        }
    }

    internal static Phase2PageFamily MapPage(string pageId) => pageId switch
    {
        "currency_wars_home" => Phase2PageFamily.Main,
        string value when value.StartsWith(
            "preparation_",
            StringComparison.OrdinalIgnoreCase) =>
            Phase2PageFamily.Preparation,
        "reward_shop" => Phase2PageFamily.Supply,
        // 2026-08-11：补 normal_hud（战斗 HUD）→ Battle。此前漏映射导致
        // 战斗 HUD 被当 unknown 走全区域 OCR 黑洞（2.6s），而它只需战斗页
        // 轻量识别（行动值+伤害，<1s）。
        "reward_battle" or "reward_battle_pause" or "battle_generic" or
            "normal_hud" =>
            Phase2PageFamily.Battle,
        "challenge_success" or "challenge_failed" or
            "challenge_health_depleted" =>
            Phase2PageFamily.BattleSettlement,
        _ => Phase2PageFamily.Unknown
    };

    private static NormalizedRect ToNormalized(PixelRect region) => new(
        region.X / 1920d,
        region.Y / 1080d,
        region.Width / 1920d,
        region.Height / 1080d);

    private static RelativeRegion ToRelative(NormalizedRect region) => new(
        region.X,
        region.Y,
        region.Width,
        region.Height);

    private static RelativeRegion ToRelative(PixelRect region) => new(
        region.X / 1920d,
        region.Y / 1080d,
        region.Width / 1920d,
        region.Height / 1080d);

    private static RelativeRegion ToFrameRelative(
        PixelRect region,
        CaptureFrame frame) => new(
        region.X / (double)frame.Width,
        region.Y / (double)frame.Height,
        region.Width / (double)frame.Width,
        region.Height / (double)frame.Height);

    private static NormalizedRect RelativePixelRegion(
        CaptureFrame frame,
        PixelRect parent,
        double x,
        double y,
        double width,
        double height) => new(
        (parent.X + parent.Width * x) / frame.Width,
        (parent.Y + parent.Height * y) / frame.Height,
        parent.Width * width / frame.Width,
        parent.Height * height / frame.Height);

    private static async Task<TimedRecognition<T>> MeasureAsync<T>(
        Func<Task<T>> operation)
    {
        var started = Stopwatch.GetTimestamp();
        var value = await operation().ConfigureAwait(false);
        return new TimedRecognition<T>(
            value,
            Stopwatch.GetElapsedTime(started));
    }

    private static Task<TimedRecognition<T>> SkipTimed<T>(T unknownValue) =>
        Task.FromResult(new TimedRecognition<T>(
            unknownValue,
            TimeSpan.Zero));

    // B 提速补强（2026-08-18）：判断当前是否是"可跳过"的增量帧字段。
    // 增量帧 = 画面已被 pipeline 帧选择器判定为静止且页面未变化（fastPageChanged=false）；
    // 此时上一全量帧已 Known 的字段值必然不变 → 跳过全量重读，保留上次值，
    // 2s 全量兜底仍会周期性校验（防漏）。上次失败（在 RecognizeFields 里）的字段照样重试。
    // 非增量帧（全量/关键帧）一律不跳。
    private static bool ShouldSkipIncrementField(
        Phase2IncrementalSelection? incremental,
        string field) =>
        incremental is not null &&
        !incremental.ShouldRecognize(field);

    private sealed record TimedRecognition<T>(T Value, TimeSpan Elapsed);

    private sealed record NamedCatalogItem(string Id, string Name);

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex("(?<plane>[1-3])\\s*[-—–·•・.:．]\\s*(?<node>[0-9])(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NodePattern();

    [GeneratedRegex("(?:L|I)?v\\.?\\s*(?<level>[1-9][0-9]?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelPattern();

    // 2026-08-19「storeLevel 修复」：PpOcr 误读 "Lv.5"→"15"（'L'→'1'）。
    // 匹配 1[3-6]（十位 1 = 'L' 误读；商店等级只有 3-6），取个位还原真实等级。
    // 用普通 static Regex（GeneratedRegex partial 曾在本环境不回填，致匹配空）。
    private static readonly Regex StoreLevelMisreadPattern = new(
        "1(?<digit>[3-6])(?![0-9])",
        RegexOptions.CultureInvariant);

    [GeneratedRegex("(?<current>[0-9]{1,3})\\s*/\\s*(?<next>[0-9]{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex ExperiencePattern();

    [GeneratedRegex("[/／]\\s*(?<next>10|[1-9])(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex CapacityDenominatorPattern();

    [GeneratedRegex("(?<active>[0-9]{1,2})\\s*/\\s*(?<next>[0-9]{1,2})", RegexOptions.CultureInvariant)]
    private static partial Regex SynergyProgressPattern();

    [GeneratedRegex("(?<round>[0-5])\\s+[^0-9]{0,4}(?<value>[0-9]{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex ActionValuePattern();

    [GeneratedRegex("(?<number>[0-9]+(?:[.,，·][0-9]+)*)\\s*(?<unit>[万亿億]{0,2})", RegexOptions.CultureInvariant)]
    private static partial Regex DamagePattern();

    [GeneratedRegex("^[1-9][0-9]{0,2}(?:,[0-9]{3})+$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalThousandsPattern();

    [GeneratedRegex("(?<number>[0-9]+(?:[.,][0-9]+)*)\\s*[Bb]", RegexOptions.CultureInvariant)]
    private static partial Regex SettlementAsciiUnitPattern();

    [GeneratedRegex("(?<number>[0-9]+(?:[.,][0-9]+)+)", RegexOptions.CultureInvariant)]
    private static partial Regex SettlementDecimalPattern();

}