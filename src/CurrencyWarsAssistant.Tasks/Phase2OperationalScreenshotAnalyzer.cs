using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tasks;

public sealed partial class Phase2OperationalScreenshotAnalyzer(
    ICharacterCardRecognizer characterRecognizer,
    IReadOnlyList<CharacterCardTemplateDefinition> characterTemplates,
    IPhase2IconRecognizer iconRecognizer,
    IReadOnlyList<Phase2IconTemplateDefinition> iconTemplates,
    IOfflineOcr ocr,
    GameDataCatalog gameData,
    IOfflineOcr? numericOcr = null,
    GameDataNameMatcher? nameMatcher = null,
    IGamePageClassifier? pageClassifier = null,
    bool enableRobustFallback = true)
{
    private const int MaximumActionCandidatesToRead = 1;
    private readonly bool _enableRobustFallback = enableRobustFallback;
    private static readonly bool EnableTimingDiagnostics = string.Equals(
        Environment.GetEnvironmentVariable("CURRENCY_WARS_PHASE2_TIMING"),
        "1",
        StringComparison.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> KnownSpecialUnitIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Gemi狸"] = "special_unit_gemi_li",
            ["佩佩"] = "special_unit_peipei",
            ["姵姵"] = "special_unit_variant_peipei",
            ["叽米"] = "special_unit_jimi",
            ["狸狸"] = "special_unit_lili",
            ["狸小龙"] = "special_unit_li_xiaolong",
            ["狸小虎"] = "special_unit_li_xiaohu"
        };
    private readonly IReadOnlyDictionary<string, string> _standingByCharacterId =
        gameData.CurrencyWarsCharacters.ToDictionary(
            item => item.Id,
            item => item.Position,
            StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, InventoryItemKind>
        _inventoryKindById = BuildInventoryKindMap(iconTemplates);
    private readonly IOfflineOcr _numericOcr = numericOcr ?? ocr;
    private readonly OpenCvUiDigitSequenceRecognizer _uiDigitRecognizer = new();
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
        CancellationToken cancellationToken)
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

        if (page == Phase2PageFamily.Preparation)
        {
            return await AnalyzePreparationAsync(
                    frame,
                    state,
                    evidence,
                    baseSnapshot,
                    pageId,
                    cancellationToken)
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

        return await AnalyzeUnknownRegionsAsync(
                frame,
                state,
                evidence,
                frameOcrCache,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Phase2OperationalState> AnalyzeUnknownRegionsAsync(
        CaptureFrame frame,
        Phase2OperationalState state,
        EvidenceReference evidence,
        ConcurrentDictionary<NormalizedRect, Lazy<Task<IReadOnlyList<string>>>>
            frameOcrCache,
        CancellationToken cancellationToken)
    {
        var regions = new[]
        {
            (Field: "main-title", Region: Phase2RecognitionRegions.MainTitle),
            (Field: "preparation-node", Region: Phase2RecognitionRegions.PreparationNodeValue),
            (Field: "battle-node", Region: Phase2RecognitionRegions.BattleNodeValue),
            (Field: "battle-damage-panel", Region: Phase2RecognitionRegions.BattleDamagePanel),
            (Field: "remaining-action", Region: Phase2RecognitionRegions.BattleActionTimeline)
        };

        // Unknown and transition frames must retain every readable region, but the
        // independent crops do not need to block one another. The OCR service owns
        // the concurrency limit, so this cannot create unbounded recognition work.
        var observations = await Task.WhenAll(regions.Select(async item =>
        {
            var texts = await ReadTextCachedAsync(
                    frame,
                    item.Region,
                    frameOcrCache,
                    cancellationToken)
                .ConfigureAwait(false);
            if (texts.Count == 0)
            {
                return null;
            }

            var summary = string.Join(" | ", texts);
            return new Phase2PartialFieldObservation(
                item.Field,
                $"unknown-page-{item.Field}",
                ToRelative(item.Region),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["text"] = summary
                },
                texts,
                [],
                0.35,
                "页面类型尚未确认；该区域文字仅作降级证据，不能驱动操作。",
                evidence with
                {
                    Locator = $"partial:unknown-page:{item.Field}",
                    Summary = summary,
                    Confidence = 0.35
                },
                false);
        })).ConfigureAwait(false);

        var partial = observations
            .OfType<Phase2PartialFieldObservation>()
            .ToList();

        return state with { PartialFields = partial };
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
            .Count(match => match.Groups["unit"].Success);
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
                .Count(match => match.Groups["unit"].Success);
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
        CancellationToken cancellationToken)
    {
        var totalStarted = Stopwatch.GetTimestamp();
        var nodeTask = MeasureAsync(() => ReadNodeAsync(
            frame,
            Phase2RecognitionRegions.PreparationNodeValue,
            "preparation-node",
            evidence,
            cancellationToken));
        var difficultyTask = MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
            frame,
            Phase2RecognitionRegions.PreparationDifficultyValue,
            Phase2RecognitionRegions.PreparationDifficultyDigits,
            100,
            999,
            "enemy-difficulty",
            evidence,
            cancellationToken,
            UiDigitForegroundStyle.BrightOnDark));
        var interestTask = MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
            frame,
            Phase2RecognitionRegions.Interest,
            Phase2RecognitionRegions.InterestValue,
            0,
            5,
            "interest",
            evidence,
            cancellationToken,
            UiDigitForegroundStyle.GoldSaturated,
            UiDigitForegroundStyle.DarkOnLight));
        var cumulativeSpendTask = MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
            frame,
            Phase2RecognitionRegions.CumulativeSpend,
            Phase2RecognitionRegions.CumulativeSpend,
            0,
            100,
            "cumulative-spend",
            evidence,
            cancellationToken,
            UiDigitForegroundStyle.BrightOnDark));
        var progressTask = MeasureAsync(() => ReadProgressAsync(
            frame,
            evidence,
            cancellationToken));
        var toolsTask = MeasureAsync(() => ReadIntegerWithLocalizedFallbackAsync(
            frame,
            Phase2RecognitionRegions.DismantleToolCountValue,
            Phase2RecognitionRegions.DismantleToolCountValue,
            0,
            99,
            "dismantle-tools",
            evidence,
            cancellationToken,
            UiDigitForegroundStyle.BrightOnDark));

        var boardReferenceSlots = string.Equals(
            configuredPageId,
            "reward_shop",
            StringComparison.Ordinal)
            ? Phase2RecognitionRegions.RewardShopCharacterSlots1920
            : Phase2RecognitionRegions.PreparationCharacterSlots1920;
        // Character matching is independent from named-content/icon OCR. Run
        // it on a separate worker so a preparation frame does not pay both
        // CPU-heavy passes serially on high-core desktop machines.
        var formationTask = Task.Run(() =>
        {
            var formationStarted = Stopwatch.GetTimestamp();
            var boardSlots = RecognizeCharactersSafely(
                frame,
                characterTemplates,
                boardReferenceSlots,
                string.Equals(
                    configuredPageId,
                    "reward_shop",
                    StringComparison.Ordinal)
                    ? CharacterCardRecognitionOptions.RewardShopCompact
                    : CharacterCardRecognitionOptions.Standard);
            var benchSlots = RecognizeCharactersSafely(
                frame,
                characterTemplates,
                Phase2RecognitionRegions.BenchCharacterSlots1920);
            var localPending = new List<PendingIconObservation>();
            var recognizedFormation = ObserveFormation(
                frame,
                boardSlots,
                benchSlots,
                evidence,
                frame.CapturedAt,
                localPending,
                string.Equals(
                    configuredPageId,
                    "reward_shop",
                    StringComparison.Ordinal));
            return (
                Formation: recognizedFormation,
                Pending: localPending,
                Elapsed: Stopwatch.GetElapsedTime(formationStarted));
        }, cancellationToken);

        // Node OCR runs concurrently with the other preparation readers, but
        // its result must be observed before planning stable-content scans.
        // Otherwise a generic preparation page discovers the new node only
        // after this frame has already skipped the strategy HUD.
        var nodeResult = await nodeTask.ConfigureAwait(false);
        // 节点号以页面 ID 提取为准（分类器更稳），OCR 仅兜底：
        // 实测 OCR 会把 preparation_1_2 的节点号读成 1-9，污染节点历史。
        var resolvedNode = NodeFromPageIdOrOcr(
            configuredPageId,
            nodeResult.Value.Value);
        var nodeObservation = resolvedNode is null
            ? nodeResult.Value
            : Observation<string>.Known(resolvedNode, 0.9);
        ObservePreparationNode(baseSnapshot.RunId, nodeObservation);

        var namedContentStarted = Stopwatch.GetTimestamp();
        var stablePlan = PlanStableRecognitions(frame, baseSnapshot.RunId);
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
        var synergyTask = ObserveNamedContentAsync(
            frame,
            "synergy",
            Phase2NamedContentKind.Synergy,
            PendingIconCategory.Synergy,
            Phase2RecognitionRegions.SynergyIconSlots,
            Phase2RecognitionRegions.SynergyTextSlots,
            _synergies,
            evidence,
            synergyPending,
            cancellationToken);
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
        var synergies = ToSynergyObservation(
            synergyContent,
            frame.CapturedAt);
        var equipmentStarted = Stopwatch.GetTimestamp();
        var inventory = ObserveInventory(frame, evidence, pending);
        var equipmentElapsed = Stopwatch.GetElapsedTime(equipmentStarted);
        var difficultyResult = await difficultyTask.ConfigureAwait(false);
        var interestResult = await interestTask.ConfigureAwait(false);
        var cumulativeSpendResult = await cumulativeSpendTask.ConfigureAwait(false);
        var progressResult = await progressTask.ConfigureAwait(false);
        var toolsResult = await toolsTask.ConfigureAwait(false);
        var node = nodeObservation;
        var difficulty = difficultyResult.Value;
        var interest = interestResult.Value;
        var cumulativeSpend = cumulativeSpendResult.Value;
        var progress = progressResult.Value;
        var tools = toolsResult.Value;
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
            Formation = formation,
            ActiveSynergies = synergies,
            DismantleToolCount = tools,
            SimpleEquipmentIds = inventory.SimpleEquipmentIds,
            SpecialItemIds = inventory.SpecialItemIds,
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
        var goldTask = ReadSettlementGoldAsync(
            frame,
            evidence,
            cancellationToken);
        var damageTask = ReadSettlementDamageAsync(
            frame,
            evidence,
            cancellationToken);
        var node = await nodeTask.ConfigureAwait(false);
        var gold = await goldTask.ConfigureAwait(false);
        var (damage, totalCandidate, pending) =
            await damageTask.ConfigureAwait(false);
        return state with
        {
            NodeId = node,
            SettlementDamage = damage,
            SettlementScreenDamageCandidate = totalCandidate,
            SettlementGoldReward = gold,
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
        var identitiesKnown = rows.All(item => item.CanDriveDecisions);
        if (complete && identitiesKnown && !ambiguousScale)
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

    private async Task<Observation<int>> ReadSettlementGoldAsync(
        CaptureFrame frame,
        EvidenceReference evidence,
        CancellationToken cancellationToken)
    {
        var direct = await ReadIntegerAsync(
            frame,
            Phase2RecognitionRegions.SettlementGoldReward,
            0,
            100,
            "settlement-gold-reward",
            evidence,
            cancellationToken).ConfigureAwait(false);
        if (direct.Status == ObservationStatus.Known)
        {
            return direct;
        }

        // An isolated one-character crop is not a reliable Windows OCR input:
        // digits such as 1 and 9 are frequently dropped even after enlargement.
        // Keep the complete, semantically unique "获得金币总览" row as context and
        // accept it only when that row yields exactly one bounded integer.
        var labeledRegion = Phase2RecognitionRegions.SettlementGoldRewardLabeledRow
            .ToPixels(frame.Width, frame.Height);
        var maskedRow = CaptureFramePreprocessor.CreateMaskedCrop(
            frame,
            labeledRegion,
            [Phase2RecognitionRegions.SettlementGoldRewardIcon.ToPixels(
                frame.Width,
                frame.Height)]);
        var labeledRecognition = await RecognizeTextSafelyAsync(
            _numericOcr,
            maskedRow,
            new PixelRect(0, 0, maskedRow.Width, maskedRow.Height),
            cancellationToken).ConfigureAwait(false);
        var labeledRow = DistinctTexts(labeledRecognition);
        var labeledValues = ParseIntegerValues(labeledRow, 0, 100);
        if (labeledValues.Length == 1)
        {
            return Observation<int>.Known(
                labeledValues[0],
                0.70,
                [evidence with
                {
                    Locator = "ocr:settlement-gold-reward-labeled-row",
                    Summary = string.Join(" | ", labeledRow)
                }],
                frame.CapturedAt);
        }

        var repeated = CaptureFramePreprocessor.CreateRepeatedEnlargedCrop(
            frame,
            Phase2RecognitionRegions.SettlementGoldRewardDigit.ToPixels(
                frame.Width,
                frame.Height));
        var recognized = await RecognizeTextSafelyAsync(
            _numericOcr,
            repeated,
            new PixelRect(0, 0, repeated.Width, repeated.Height),
            cancellationToken).ConfigureAwait(false);
        var ranked = RankRepeatedIntegers(recognized, maximum: 100);
        if ((ranked.Length == 0 || ranked[0].Count < 2) &&
            _enableRobustFallback &&
            _numericOcr is IAdaptiveOfflineOcr)
        {
            recognized = await RecognizeTextSafelyAsync(
                _numericOcr,
                repeated,
                new PixelRect(0, 0, repeated.Width, repeated.Height),
                cancellationToken,
                robust: true).ConfigureAwait(false);
            ranked = RankRepeatedIntegers(recognized, maximum: 100);
        }

        if (ranked.Length > 0 && ranked[0].Count >= 2 &&
            (ranked.Length == 1 || ranked[0].Count > ranked[1].Count))
        {
            return Observation<int>.Known(
                ranked[0].Value,
                0.65,
                [evidence with
                {
                    Locator = "ocr:settlement-gold-reward-repeated",
                    Summary = string.Join(" | ", recognized.Lines)
                }],
                frame.CapturedAt);
        }

        return direct;
    }

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
            if (scaled is < 0 or > 100_000_000_000m)
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
               text.Any(character => character is '\u4E07' or '\u4EBF');
    }

    private static bool HasVisibleBattleDamageBar(
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
        return Math.Sqrt(variance) >= 18 && transitionRatio >= 0.035;
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
        bool compactBoardLayout)
    {
        var states = new List<FormationCharacterState>();
        Add(board.Take(4), FormationZone.Front, states);
        Add(board.Skip(4), FormationZone.Back, states);
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
        return uncertainCount == 0
            ? Observation<IReadOnlyList<FormationCharacterState>>.Known(
                states,
                confidence,
                [formationEvidence],
                observedAt)
            : new Observation<IReadOnlyList<FormationCharacterState>>
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
                var equipment = RecognizeEquipmentSlots(
                    slot,
                    zone,
                    temporaryId,
                    pending);
                target.Add(new FormationCharacterState(
                    zone,
                    slot.SlotIndex,
                    temporaryId,
                    slot.StarLevel,
                    "unknown",
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
                    equipment.Slots));
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
                var equipment = RecognizeEquipmentSlots(
                    slot,
                    zone,
                    id,
                    pending);
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
                    CardRegion: ToRelative(slot.ReferenceBounds),
                    EquipmentSlots: equipment.Slots));
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
            var regions = Phase2RecognitionRegions.CharacterEquipmentSlots(
                owner.ReferenceBounds,
                compactBoardLayout && zone == FormationZone.Front);
            var occupiedIndices = Enumerable.Range(0, regions.Count)
                .Where(index => HasDetailedForeground(frame, regions[index]))
                .ToArray();
            var occupiedRecognitions = RecognizeIconsSafely(
                frame,
                "advanced-equipment",
                occupiedIndices.Select(index => regions[index]).ToArray(),
                iconTemplates);
            var recognitionBySlot = occupiedIndices
                .Select((slotIndex, resultIndex) => (
                    SlotIndex: slotIndex,
                    Result: occupiedRecognitions[resultIndex]))
                .ToDictionary(item => item.SlotIndex, item => item.Result);
            var slotStates = new List<CharacterEquipmentSlotState>(regions.Count);
            var equipmentIds = new List<string>(regions.Count);

            for (var index = 0; index < regions.Count; index++)
            {
                var relativeRegion = ToRelative(regions[index]);
                if (!recognitionBySlot.TryGetValue(index, out var item))
                {
                    slotStates.Add(new CharacterEquipmentSlotState(
                        index,
                        EquipmentSlotOccupancy.Empty,
                        null,
                        [],
                        0.90,
                        relativeRegion,
                        evidence with
                        {
                            Locator = $"vision:advanced-equipment-empty:{zone}:" +
                                      $"{owner.SlotIndex + 1}:{index + 1}",
                            Summary = "装备槽没有检测到可见装备前景",
                            Confidence = 0.90
                        }));
                    continue;
                }

                var slotEvidence = evidence with
                {
                    Locator = $"crop:advanced-equipment:{zone}:" +
                              $"{owner.SlotIndex + 1}:{index + 1}",
                    Confidence = item.Confidence
                };
                if (item.IsKnown)
                {
                    equipmentIds.Add(item.TemplateId!);
                    slotStates.Add(new CharacterEquipmentSlotState(
                        index,
                        EquipmentSlotOccupancy.Equipped,
                        item.TemplateId,
                        item.CandidateTemplateIds ?? [],
                        item.Confidence,
                        relativeRegion,
                        slotEvidence));
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
                    slotEvidence,
                    failureReason,
                    false));
                pendingTarget.Add(new PendingIconObservation(
                    PendingIconCategory.AdvancedEquipment,
                    $"{zone}-{owner.SlotIndex + 1}-equipment-{index + 1}",
                    relativeRegion,
                    item.TemplateId,
                    item.Confidence,
                    slotEvidence,
                    candidates.Count > 1
                        ? "ambiguous-visual-identity"
                        : "unresolved",
                    candidates,
                    temporaryId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ownerCharacterId"] = ownerId,
                        ["zone"] = zone.ToString(),
                        ["equipmentSlot"] = (index + 1)
                            .ToString(CultureInfo.InvariantCulture)
                    },
                    false));
            }

            return (equipmentIds, slotStates);
        }
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
            if (matches.Length == 0 &&
                _enableRobustFallback &&
                ocr is IAdaptiveOfflineOcr)
            {
                rawTexts = await ReadTextRobustAsync(
                    frame,
                    textSlots[index],
                    ocr,
                    cancellationToken).ConfigureAwait(false);
                matches = MatchNamedContent(rawTexts, catalog);
            }
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

    private InventoryObservation ObserveInventory(
        CaptureFrame frame,
        EvidenceReference evidence,
        ICollection<PendingIconObservation> pending)
    {
        var slots = Phase2RecognitionRegions.InventoryIconSlots
            .Skip(1)
            .ToArray();
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

            var candidates = item.CandidateTemplateIds ?? [];
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
                values.Add(new InventorySlotState(
                    index,
                    EquipmentSlotOccupancy.Equipped,
                    itemKind,
                    item.TemplateId,
                    candidates,
                    item.Confidence,
                    relativeRegion,
                    slotEvidence));
                continue;
            }

            var reason = candidates.Count > 1
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
                candidates.Count > 1
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

        var allResolved = values.All(item =>
            item.Occupancy is EquipmentSlotOccupancy.Empty or
                EquipmentSlotOccupancy.Equipped);
        var slotsObservation = allResolved
            ? Observation<IReadOnlyList<InventorySlotState>>.Known(
                values,
                values.Average(item => item.Confidence),
                values.Select(item => item.Evidence),
                frame.CapturedAt)
            : new Observation<IReadOnlyList<InventorySlotState>>
            {
                Status = ObservationStatus.Unknown,
                Value = values,
                Confidence = values.Count == 0
                    ? 0
                    : values.Average(item => item.Confidence),
                Evidence = values.Select(item => item.Evidence).ToArray(),
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
                InventoryItemKind.SpecialItem,
                allResolved,
                evidence,
                frame.CapturedAt),
            slotsObservation);
    }

    private static Observation<IReadOnlyList<string>> ProjectInventoryIds(
        IReadOnlyList<InventorySlotState> slots,
        InventoryItemKind kind,
        bool allResolved,
        EvidenceReference evidence,
        DateTimeOffset observedAt)
    {
        var ids = slots
            .Where(item => item.Occupancy == EquipmentSlotOccupancy.Equipped &&
                           item.ItemKind == kind &&
                           item.ItemId is not null)
            .Select(item => item.ItemId!)
            .ToArray();
        return allResolved
            ? Observation<IReadOnlyList<string>>.Known(
                ids,
                slots.Count == 0 ? 1 : slots.Average(item => item.Confidence),
                [evidence with { Locator = $"vision:inventory:{kind}" }],
                observedAt)
            : new Observation<IReadOnlyList<string>>
            {
                Status = ObservationStatus.Unknown,
                Value = ids,
                Confidence = 0,
                Evidence = [evidence with { Locator = $"vision:inventory:{kind}" }],
                Uncertainty = ["背包存在未解析槽位；已确认项目仍被保留。"],
                ObservedAt = observedAt
            };
    }

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
        var avatarSlots = Enumerable.Range(0, 8)
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
                ? ReadNumericTextAsync(
                    frame,
                    Phase2RecognitionRegions.BattleDamageValue(row),
                    cancellationToken,
                    allowEnlargedFallback: true)
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
        var robustTextTasks = new Task<IReadOnlyList<string>>?[avatarSlots.Length];
        for (var row = 0; row < avatarSlots.Length; row++)
        {
            var candidates = textByRow[row]
                .SelectMany(ParseSettlementDamageCandidates)
                .OrderByDescending(item => item.Score)
                .ToArray();
            candidatesByRow[row] = candidates;
            if (!rowIsVisibleByRow[row] ||
                !_enableRobustFallback ||
                _numericOcr is not IAdaptiveOfflineOcr ||
                (candidates.Length > 0 && candidates.Any(candidate =>
                    candidate.Value <= 0 ||
                    HasExplicitDamageScaleSafe(candidate.Text))))
            {
                continue;
            }

            // Damage rows are independent. Run their mature robust OCR fallback
            // concurrently so a frame with several visible sources does not pay
            // the fallback latency once per row in series.
            robustTextTasks[row] = ReadTextRobustAsync(
                frame,
                Phase2RecognitionRegions.BattleDamageValue(row),
                _numericOcr,
                cancellationToken);
        }

        var activeRobustTasks = robustTextTasks
            .Where(task => task is not null)
            .Select(task => task!)
            .ToArray();
        if (activeRobustTasks.Length > 0)
        {
            await Task.WhenAll(activeRobustTasks).ConfigureAwait(false);
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
            if (robustTextTasks[row] is { } robustTextTask)
            {
                text = robustTextTask.Result;
                candidates = text.SelectMany(ParseSettlementDamageCandidates)
                    .OrderByDescending(item => item.Score)
                    .ToArray();
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
        if (ocrObservation.Status == ObservationStatus.Known &&
            !noisyOcrNeedsCorroboration)
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

    private async Task<IReadOnlyList<string>> ReadNumericTextAsync(
        CaptureFrame frame,
        NormalizedRect region,
        CancellationToken cancellationToken,
        bool allowEnlargedFallback = false)
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

        if (!_enableRobustFallback && !allowEnlargedFallback)
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
        CancellationToken cancellationToken)
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
            pixelRegion);
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

    private IReadOnlyList<CharacterCardSlotRecognition> RecognizeCharactersSafely(
        CaptureFrame frame,
        IReadOnlyList<CharacterCardTemplateDefinition> templates,
        IReadOnlyList<PixelRect> slots,
        CharacterCardRecognitionOptions options = default)
    {
        try
        {
            return characterRecognizer.Recognize(
                frame,
                templates,
                slots,
                options == default
                    ? CharacterCardRecognitionOptions.Standard
                    : options);
        }
        catch (Exception)
        {
            return slots.Select((slot, index) => new CharacterCardSlotRecognition(
                    index,
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
                _ => 1m
            };
            var scaled = numeric * multiplier;
            if (scaled is < 0 or > 100_000_000_000m)
            {
                continue;
            }

            var value = decimal.ToInt64(decimal.Round(
                scaled,
                0,
                MidpointRounding.AwayFromZero));
            var score = match.Groups["unit"].Success ? 3 : 1;
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
        "preparation_1_1" or "preparation_1_2" or "preparation_generic" or
            "reward_shop" =>
            Phase2PageFamily.Preparation,
        "reward_battle" or "reward_battle_pause" or "battle_generic" =>
            Phase2PageFamily.Battle,
        "challenge_success" or "challenge_failed" or
            "challenge_health_depleted" =>
            Phase2PageFamily.BattleSettlement,
        _ => Phase2PageFamily.Unknown
    };

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

    private sealed record TimedRecognition<T>(T Value, TimeSpan Elapsed);

    private sealed record NamedCatalogItem(string Id, string Name);

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex("(?<plane>[1-3])\\s*[-—–·•・.:．]\\s*(?<node>[0-9])(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex NodePattern();

    [GeneratedRegex("(?:L|I)?v\\.?\\s*(?<level>[1-9][0-9]?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelPattern();

    [GeneratedRegex("(?<current>[0-9]{1,3})\\s*/\\s*(?<next>[0-9]{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex ExperiencePattern();

    [GeneratedRegex("[/／]\\s*(?<next>10|[1-9])(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex CapacityDenominatorPattern();

    [GeneratedRegex("(?<active>[0-9]{1,2})\\s*/\\s*(?<next>[0-9]{1,2})", RegexOptions.CultureInvariant)]
    private static partial Regex SynergyProgressPattern();

    [GeneratedRegex("(?<round>[0-5])\\s+[^0-9]{0,4}(?<value>[0-9]{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex ActionValuePattern();

    [GeneratedRegex("(?<number>[0-9]+(?:[.,，·][0-9]+)*)\\s*(?<unit>[万亿億]?)", RegexOptions.CultureInvariant)]
    private static partial Regex DamagePattern();

    [GeneratedRegex("(?<number>[0-9]+(?:[.,][0-9]+)*)\\s*[Bb]", RegexOptions.CultureInvariant)]
    private static partial Regex SettlementAsciiUnitPattern();

    [GeneratedRegex("(?<number>[0-9]+(?:[.,][0-9]+)+)", RegexOptions.CultureInvariant)]
    private static partial Regex SettlementDecimalPattern();
}
