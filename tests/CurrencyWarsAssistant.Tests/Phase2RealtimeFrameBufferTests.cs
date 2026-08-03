using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Advisor;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2RealtimeFrameBufferTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void IdenticalFramesStayOnTheFastUnchangedPath()
    {
        var buffer = new Phase2RealtimeFrameBuffer();

        buffer.Add(CreateFrame(24), isReliable: true);
        var second = buffer.Add(CreateFrame(24), isReliable: true);

        Assert.Equal(Phase2FrameChangeKind.Unchanged, second.ChangeKind);
    }

    [Fact]
    public void LocalOverlayDoesNotDiscardTheLastReliableFrame()
    {
        var buffer = new Phase2RealtimeFrameBuffer();
        var reliable = buffer.Add(CreateFrame(24), isReliable: true);
        var covered = CreateFrame(24, (60, 30, 35, 25, (byte)230));

        var update = buffer.Add(covered, isReliable: false);
        var locked = buffer.LockLatestReliable();

        Assert.NotEqual(Phase2FrameChangeKind.Unchanged, update.ChangeKind);
        Assert.Equal(reliable.Sequence, Assert.Single(locked).Sequence);
    }

    [Fact]
    public void RingCapacityKeepsOnlyTheMostRecentReliableCandidates()
    {
        var buffer = new Phase2RealtimeFrameBuffer(capacity: 4);
        for (var index = 0; index < 7; index++)
        {
            buffer.Add(
                CreateFrame((byte)(20 + index * 20)),
                isReliable: index is 1 or 3 or 5 or 6);
        }

        var locked = buffer.LockLatestReliable(count: 3);

        Assert.Equal(new long[] { 4, 6, 7 }, locked.Select(item => item.Sequence));
    }

    [Fact]
    public void FullSceneReplacementIsMarkedAsTransition()
    {
        var buffer = new Phase2RealtimeFrameBuffer();
        buffer.Add(CreatePatternFrame(invert: false), isReliable: true);

        var changed = buffer.Add(CreatePatternFrame(invert: true), isReliable: false);

        Assert.Equal(Phase2FrameChangeKind.SceneTransition, changed.ChangeKind);
    }

    [Fact]
    public void SceneTransitionLocksRecentStableFramesEvenWhenAnalysisLags()
    {
        var buffer = new Phase2RealtimeFrameBuffer();
        buffer.Add(CreatePatternFrame(invert: false), isReliable: false);
        var firstStable = buffer.Add(CreatePatternFrame(invert: false), isReliable: false);
        var secondStable = buffer.Add(
            CreatePatternFrame(invert: false),
            isReliable: false);

        var transition = buffer.Add(
            CreatePatternFrame(invert: true),
            isReliable: false);
        var locked = buffer.LockLatestStableCandidates(3);

        Assert.Equal(Phase2FrameChangeKind.SceneTransition, transition.ChangeKind);
        Assert.Contains(locked, item => item.Sequence == firstStable.Sequence);
        Assert.Contains(locked, item => item.Sequence == secondStable.Sequence);
        Assert.DoesNotContain(locked, item => item.Sequence == transition.Sequence);
    }

    [Fact]
    public async Task RecognitionQueueReplacesIntermediateRegularFramesWithLatest()
    {
        var buffer = new Phase2RealtimeFrameBuffer();
        var queue = new Phase2BoundedRecognitionQueue(capacity: 3);
        var first = Work(buffer.Add(CreateFrame(20), true), "first", false);
        var second = Work(buffer.Add(CreateFrame(40), true), "second", false);

        Assert.True(queue.Enqueue(first));
        Assert.True(queue.Enqueue(second));

        var dequeued = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("second", dequeued.ScreenshotName);
    }

    [Fact]
    public async Task RecognitionQueuePreservesCriticalFrameDuringRegularChurn()
    {
        var buffer = new Phase2RealtimeFrameBuffer();
        var queue = new Phase2BoundedRecognitionQueue(capacity: 3);
        var critical = Work(buffer.Add(CreateFrame(20), true), "critical", true);
        queue.Enqueue(critical);
        queue.Enqueue(Work(buffer.Add(CreateFrame(40), true), "middle", false));
        queue.Enqueue(Work(buffer.Add(CreateFrame(60), true), "latest", false));

        var first = await queue.DequeueAsync(CancellationToken.None);
        var second = await queue.DequeueAsync(CancellationToken.None);

        Assert.True(first.IsCritical);
        Assert.Equal("critical", first.ScreenshotName);
        Assert.Equal("latest", second.ScreenshotName);
    }

    [Fact]
    public async Task RecognitionQueueExplicitlyRejectsCriticalOverflow()
    {
        var buffer = new Phase2RealtimeFrameBuffer();
        var queue = new Phase2BoundedRecognitionQueue(
            capacity: 2,
            maximumCriticalCapacity: 3);

        Assert.True(queue.Enqueue(Work(buffer.Add(CreateFrame(20), true), "c1", true)));
        Assert.True(queue.Enqueue(Work(buffer.Add(CreateFrame(30), true), "c2", true)));
        Assert.True(queue.Enqueue(Work(buffer.Add(CreateFrame(40), true), "c3", true)));
        // 拥塞时保留最新关键帧（丢最旧保新）——终局/结算帧不因拥塞丢失。
        Assert.True(queue.Enqueue(Work(buffer.Add(CreateFrame(50), true), "c4", true)));
        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("c2", first.ScreenshotName); // 最旧的 c1 已被替换，c2 最先出队
    }

    [Fact]
    public void BattleAnimationCannotBePromotedToCriticalPageBoundary()
    {
        var lastBoundary = DateTimeOffset.MinValue;

        var critical = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged: false,
            DateTimeOffset.UtcNow,
            ref lastBoundary);

        Assert.False(critical);
        Assert.Equal(DateTimeOffset.MinValue, lastBoundary);
    }

    [Fact]
    public void FastPageBoundaryIsRateLimitedWithoutLosingFirstCandidate()
    {
        var start = DateTimeOffset.UtcNow;
        var lastBoundary = DateTimeOffset.MinValue;

        var first = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged: true,
            start,
            ref lastBoundary);
        var animationFrame = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged: true,
            start.AddMilliseconds(200),
            ref lastBoundary);
        var laterBoundary = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged: true,
            start.AddSeconds(1),
            ref lastBoundary);

        Assert.True(first);
        Assert.False(animationFrame);
        Assert.True(laterBoundary);
    }

    [Fact]
    public void PreparationDiagnosticFallbackRequiresTwoIndependentAnchors()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "preparation_generic",
                "preparation_stage_label",
                0.438,
                0.50),
            new PageAnchorDiagnostic(
                "preparation_1_2",
                "preparation_stage_1_2",
                0.847,
                0.90)
        ]);

        Assert.NotNull(inferred);
        Assert.Equal("preparation_generic", inferred.Value.PageId);
        Assert.Equal(Phase2PageFamily.Preparation, inferred.Value.PageFamily);
    }

    [Fact]
    public void MainDiagnosticFallbackAcceptsUniqueDominantHomeAnchor()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "currency_wars_home",
                "currency_wars_home_title",
                0.698,
                0.90),
            new PageAnchorDiagnostic(
                "plane_progress",
                "plane_progress_continue",
                0.465,
                0.90),
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_damage_tabs",
                0.21,
                0.70)
        ]);

        Assert.NotNull(inferred);
        Assert.Equal("currency_wars_home", inferred.Value.PageId);
        Assert.Equal(Phase2PageFamily.Main, inferred.Value.PageFamily);
    }

    [Fact]
    public void MainDiagnosticFallbackRejectsAmbiguousCompetingAnchor()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "currency_wars_home",
                "currency_wars_home_title",
                0.68,
                0.90),
            new PageAnchorDiagnostic(
                "reward_shop",
                "reward_shop_refresh_panel",
                0.61,
                0.86)
        ]);

        Assert.Null(inferred);
    }

    [Fact]
    public void BattleDiagnosticFallbackRecoversEffectObscuredPauseControl()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_damage_tabs",
                0.865,
                0.70),
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_pause_control",
                0.624,
                0.74),
            new PageAnchorDiagnostic(
                "reward_battle",
                "reward_battle_status_bar",
                0.655,
                0.90)
        ]);

        Assert.NotNull(inferred);
        Assert.Equal("battle_generic", inferred.Value.PageId);
        Assert.Equal(Phase2PageFamily.Battle, inferred.Value.PageFamily);
    }

    [Fact]
    public void BattleDiagnosticFallbackRejectsWeakDamageTabs()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_damage_tabs",
                0.66,
                0.70),
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_pause_control",
                0.69,
                0.74)
        ]);

        Assert.Null(inferred);
    }

    [Fact]
    public void BattleDiagnosticFallbackRecoversDamagePanelWhenPauseIsObscured()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_damage_tabs",
                0.769,
                0.70),
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_pause_control",
                0.31,
                0.74),
            new PageAnchorDiagnostic(
                "preparation_generic",
                "preparation_stage_label",
                0.18,
                0.50),
            new PageAnchorDiagnostic(
                "preparation_1_2",
                "preparation_stage_1_2",
                0.42,
                0.90)
        ]);

        Assert.NotNull(inferred);
        Assert.Equal(Phase2PageFamily.Battle, inferred.Value.PageFamily);
    }

    [Fact]
    public void PreparationDiagnosticFallbackRejectsSingleWeakAnchor()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "preparation_generic",
                "preparation_stage_label",
                0.49,
                0.50),
            new PageAnchorDiagnostic(
                "reward_shop",
                "reward_shop_refresh_panel",
                0.64,
                0.86)
        ]);

        Assert.Null(inferred);
    }

    [Fact]
    public void PreparationDiagnosticFallbackRecoversBlankBoardOnlyWithBattleExclusion()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "preparation_generic",
                "preparation_stage_label",
                0.377,
                0.50),
            new PageAnchorDiagnostic(
                "preparation_1_2",
                "preparation_stage_1_2",
                0.865,
                0.90),
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_damage_tabs",
                0.097,
                0.70)
        ]);

        Assert.NotNull(inferred);
        Assert.Equal(Phase2PageFamily.Preparation, inferred.Value.PageFamily);
    }

    [Fact]
    public void PreparationDiagnosticFallbackCannotOverrideBattleDamageTabs()
    {
        var inferred = Phase2PageDiagnosticFallback.TryInfer(
        [
            new PageAnchorDiagnostic(
                "preparation_generic",
                "preparation_stage_label",
                0.44,
                0.50),
            new PageAnchorDiagnostic(
                "preparation_1_2",
                "preparation_stage_1_2",
                0.86,
                0.90),
            new PageAnchorDiagnostic(
                "battle_generic",
                "battle_generic_damage_tabs",
                0.78,
                0.70)
        ]);

        Assert.Null(inferred);
    }

    [Fact]
    public void FastSettlementEvidencePreservesBattleExitCandidates()
    {
        var lastBoundary = DateTimeOffset.MinValue;

        var critical = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged: true,
            DateTimeOffset.UtcNow,
            ref lastBoundary);

        Assert.True(critical);
    }

    [Fact]
    public void OneCapturedChallengeSuccessFrameIsLockedAcrossNextPreparationBoundary()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.UtcNow;
        selector.Observe(
            CreateFrame(20) with { CapturedAt = start },
            true,
            Phase2PageFamily.Battle,
            new Phase2FastPageObservation(true, Phase2PageFamily.Battle, "battle_generic"));
        var yellow = selector.Observe(
            CreateFrame(80) with { CapturedAt = start.AddMilliseconds(200) },
            true,
            Phase2PageFamily.Battle,
            new Phase2FastPageObservation(true, Phase2PageFamily.BattleSettlement, "challenge_success"));
        var yellowSequence = yellow.Current.Sequence;
        var preparation = selector.Observe(
            CreateFrame(30) with { CapturedAt = start.AddMilliseconds(350) },
            true,
            Phase2PageFamily.BattleSettlement,
            new Phase2FastPageObservation(true, Phase2PageFamily.Preparation, "preparation_generic"));

        Assert.Contains(yellow.FramesToRecognize, item =>
            item.IsCritical && item.BufferedFrame.Sequence == yellowSequence);
        Assert.Contains(preparation.FramesToRecognize, item =>
            item.IsCritical && item.BufferedFrame.Sequence == yellowSequence);
    }

    [Fact]
    public void UnclassifiedOneFrameSettlementIsRetainedByNextPreparationBoundary()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.UtcNow;
        selector.Observe(
            CreateFrame(20) with { CapturedAt = start },
            true,
            Phase2PageFamily.Battle,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Battle,
                "battle_generic"));
        var unclassifiedSettlement = selector.Observe(
            CreateFrame(80) with { CapturedAt = start.AddMilliseconds(100) },
            true,
            Phase2PageFamily.Battle,
            Phase2FastPageObservation.None);
        var preparation = selector.Observe(
            CreateFrame(30) with { CapturedAt = start.AddMilliseconds(200) },
            true,
            Phase2PageFamily.Battle,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Preparation,
                "preparation_generic"));

        Assert.Contains(preparation.FramesToRecognize, item =>
            item.IsCritical &&
            item.BufferedFrame.Sequence == unclassifiedSettlement.Current.Sequence);
    }

    [Fact]
    public void LongUnknownTransitionStillLocksLastMatchedPageFrame()
    {
        var selector = new Phase2RealtimeFrameSelector(bufferCapacity: 3);
        var startedAt = DateTimeOffset.UtcNow;
        var preparation = selector.Observe(
            CreatePatternFrame(invert: false) with { CapturedAt = startedAt },
            wasReliable: true,
            Phase2PageFamily.Preparation,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Preparation,
                "preparation_generic"));
        var retainedSequence = preparation.Current.Sequence;

        for (var index = 1; index <= 8; index++)
        {
            selector.Observe(
                CreateFrame((byte)(20 + index * 10)) with
                {
                    CapturedAt = startedAt.AddMilliseconds(index * 200)
                },
                wasReliable: false,
                Phase2PageFamily.Preparation,
                Phase2FastPageObservation.None);
        }

        var battle = selector.Observe(
            CreatePatternFrame(invert: true) with
            {
                CapturedAt = startedAt.AddSeconds(2)
            },
            wasReliable: false,
            Phase2PageFamily.Preparation,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Battle,
                "battle_generic"));

        Assert.Contains(
            battle.FramesToRecognize,
            item => item.IsCritical &&
                    item.BufferedFrame.Sequence == retainedSequence);
    }

    [Fact]
    public void FastBattleEvidenceDoesNotPromoteOrdinaryBattleAnimation()
    {
        var lastBoundary = DateTimeOffset.MinValue;

        var critical = Phase2CriticalFramePolicy.ShouldQueueBoundary(
            fastPageChanged: false,
            DateTimeOffset.UtcNow,
            ref lastBoundary);

        Assert.False(critical);
    }

    [Fact]
    public void StableFiveFpsStreamQueuesAFullRefreshAtTwoSecondCadence()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-07-29T14:42:12.000+08:00");
        var selected = new List<Phase2SelectedFrame>();

        for (var index = 0; index < 31; index++)
        {
            var frame = CreateFrame(24) with
            {
                CapturedAt = start.AddMilliseconds(index * 200)
            };
            selected.AddRange(selector.Observe(
                frame,
                wasReliable: true,
                Phase2PageFamily.Preparation).FramesToRecognize);
        }

        Assert.Equal(4, selected.Count);
        Assert.Equal(start, selected[0].BufferedFrame.Frame.CapturedAt);
        Assert.Equal(start.AddSeconds(2), selected[1].BufferedFrame.Frame.CapturedAt);
        Assert.Equal(start.AddSeconds(4), selected[2].BufferedFrame.Frame.CapturedAt);
        Assert.Equal(start.AddSeconds(6), selected[3].BufferedFrame.Frame.CapturedAt);
    }

    [Fact]
    public void PreparationBoundaryGetsBoundedFastCorrectionFrame()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-07-29T14:42:12.000+08:00");
        var preparation = new Phase2FastPageObservation(
            true,
            Phase2PageFamily.Preparation,
            "preparation_generic");

        var first = selector.Observe(
            CreateFrame(24) with { CapturedAt = start },
            wasReliable: true,
            Phase2PageFamily.Unknown,
            preparation);
        var tooSoon = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(200) },
            wasReliable: true,
            Phase2PageFamily.Preparation,
            preparation);
        var correction = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(500) },
            wasReliable: true,
            Phase2PageFamily.Preparation,
            preparation);

        Assert.Contains(first.FramesToRecognize, item => item.IsCritical);
        Assert.Empty(tooSoon.FramesToRecognize);
        Assert.Contains(correction.FramesToRecognize, item =>
            item.BufferedFrame.Frame.CapturedAt == start.AddMilliseconds(500));
    }

    [Fact]
    public void EquivalentPreparationAnchorsDoNotCreateFalseBoundaries()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-07-29T14:42:12.000+08:00");

        var first = selector.Observe(
            CreateFrame(24) with { CapturedAt = start },
            wasReliable: true,
            Phase2PageFamily.Preparation,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Preparation,
                "preparation_1_1"));
        var equivalent = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddSeconds(1.2) },
            wasReliable: true,
            Phase2PageFamily.Preparation,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Preparation,
                "preparation_generic"));
        var shop = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddSeconds(2.4) },
            wasReliable: true,
            Phase2PageFamily.Preparation,
            new Phase2FastPageObservation(
                true,
                Phase2PageFamily.Preparation,
                "reward_shop"));

        Assert.Contains(first.FramesToRecognize, item => item.IsCritical);
        Assert.DoesNotContain(equivalent.FramesToRecognize, item => item.IsCritical);
        Assert.Contains(shop.FramesToRecognize, item => item.IsCritical);
    }

    [Fact]
    public void PageBoundaryQueuesTwoPredecessorsAndCurrentWithoutDuplicates()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-07-29T14:42:12.000+08:00");
        var preparation = new Phase2FastPageObservation(
            true,
            Phase2PageFamily.Preparation,
            "preparation_generic");
        var battle = new Phase2FastPageObservation(
            true,
            Phase2PageFamily.Battle,
            "battle_generic");
        selector.Observe(
            CreateFrame(24) with { CapturedAt = start },
            true,
            Phase2PageFamily.Preparation,
            preparation);
        selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(400) },
            true,
            Phase2PageFamily.Preparation,
            preparation);
        selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddMilliseconds(800) },
            true,
            Phase2PageFamily.Preparation,
            preparation);

        var boundary = selector.Observe(
            CreateFrame(24) with { CapturedAt = start.AddSeconds(1.2) },
            true,
            Phase2PageFamily.Preparation,
            battle);

        Assert.InRange(boundary.FramesToRecognize.Count, 3, 4);
        Assert.InRange(
            boundary.FramesToRecognize.Select(item => item.BufferedFrame.Sequence)
                .Distinct()
                .Count(),
            3,
            4);
        Assert.Equal(
            boundary.Current.Sequence,
            boundary.FramesToRecognize[^1].BufferedFrame.Sequence);
        Assert.All(boundary.FramesToRecognize, item => Assert.True(item.IsCritical));
    }

    [Fact]
    public void BattleAnimationChangesAreCoalescedWithoutCriticalQueueGrowth()
    {
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-07-29T14:42:12.000+08:00");
        var selected = new List<Phase2SelectedFrame>();

        for (var index = 0; index < 26; index++)
        {
            var frame = CreatePatternFrame(invert: index % 2 == 1) with
            {
                CapturedAt = start.AddMilliseconds(index * 200)
            };
            selected.AddRange(selector.Observe(
                frame,
                wasReliable: true,
                Phase2PageFamily.Battle).FramesToRecognize);
        }

        Assert.InRange(selected.Count, 3, 4);
        Assert.DoesNotContain(selected, item => item.IsCritical);
        Assert.True(selected.Count < 26 / 2);
    }

    [Fact]
    public void MainPageSceneTransitionsDoNotQueueCriticalFrames()
    {
        // 回归：0.2.758 曾把主页（currency_wars_home，Main）切换也当作
        // 页面边界，刷爆关键帧队列导致开局时误判"未知节点战斗"。
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-07-29T14:42:12.000+08:00");
        var selected = new List<Phase2SelectedFrame>();

        for (var index = 0; index < 10; index++)
        {
            var frame = CreatePatternFrame(invert: index % 2 == 1) with
            {
                CapturedAt = start.AddMilliseconds(index * 200)
            };
            selected.AddRange(selector.Observe(
                frame,
                wasReliable: true,
                Phase2PageFamily.Main).FramesToRecognize);
        }

        Assert.DoesNotContain(selected, item => item.IsCritical);
    }

    [Fact]
    public void UnknownPageSceneTransitionIsDropped()
    {
        // 过场动画/未知页面（如"竞争对手生成中"）不是关键帧：
        // 识别只会得到 Unknown 并阻塞关键帧序列，直接丢弃不占队列。
        var selector = new Phase2RealtimeFrameSelector();
        var start = DateTimeOffset.Parse("2026-07-29T14:42:12.000+08:00");
        var selected = new List<Phase2SelectedFrame>();

        for (var index = 0; index < 4; index++)
        {
            var frame = CreatePatternFrame(invert: index % 2 == 1) with
            {
                CapturedAt = start.AddMilliseconds(index * 200)
            };
            selected.AddRange(selector.Observe(
                frame,
                wasReliable: true,
                Phase2PageFamily.Unknown).FramesToRecognize);
        }

        // 过场动画/未知页面不是关键帧：识别只会得到 Unknown 并阻塞关键帧序列。
        // 允许极少量 regular 节律帧，但绝不允许 critical 关键帧。
        Assert.DoesNotContain(selected, item => item.IsCritical);
    }

    [Fact]
    public async Task RealtimePipelineKeepsCapturingWhileRealFramesAreRecognizedInBackground()
    {
        var battle = LoadLiveFrame("battle-1-6-late.png");
        var preparation = LoadLiveFrame("preparation-1-7-user.png");
        var capture = new SequencedRealFrameCapture(
            Enumerable.Repeat(battle, 8)
                .Concat(Enumerable.Repeat(preparation, 12))
                .ToArray());
        var analyzer = new DelayedUnknownAnalyzer(TimeSpan.FromMilliseconds(650));
        var windowService = new StaticWindowService();
        var pipeline = new Phase2RealtimeRecognitionPipeline(
            windowService,
            capture,
            analyzer,
            new SequencedFastPageClassifier(battleFrameCount: 8));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var updates = new List<Phase2RealtimePipelineUpdate>();

        try
        {
            await foreach (var update in pipeline.RunAsync(
                               windowService.Window.Handle,
                               new AdvisorSelection(
                                   AdvisorMode.Auto,
                                   "test",
                                   "4.4"),
                               "realtime-real-frame-test",
                               timeout.Token))
            {
                if (!update.IsHeartbeat)
                {
                    updates.Add(update);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Expected bounded integration run.
        }

        Assert.InRange(capture.Calls, 35, 70);
        Assert.True(
            capture.Calls > analyzer.Calls * 2,
            $"Capture ({capture.Calls}) was blocked by recognition ({analyzer.Calls}).");
        Assert.Contains(updates, update => update.IsCritical);
        Assert.Contains(updates, update => update.Analysis?.OperationalState?.PageFamily ==
                                           Phase2PageFamily.Unknown);
    }

    private static Phase2RecognitionWorkItem Work(
        Phase2BufferedFrame frame,
        string name,
        bool critical) =>
        new(frame, name, $"test:{name}", "test-run", critical);

    private static CaptureFrame LoadLiveFrame(string name) =>
        CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            name));

    private sealed class SequencedRealFrameCapture(
        IReadOnlyList<CaptureFrame> frames) : IGameCapture
    {
        private int index;

        public int Calls { get; private set; }

        public ValueTask<CaptureFrame> CaptureAsync(
            GameWindowInfo window,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = frames[Math.Min(index++, frames.Count - 1)];
            Calls++;
            return ValueTask.FromResult(new CaptureFrame(
                source.Width,
                source.Height,
                source.Stride,
                source.BgraPixels.ToArray(),
                source.ScreenArea,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class DelayedUnknownAnalyzer(TimeSpan delay) :
        ISituationScreenshotAnalyzer
    {
        public int Calls { get; private set; }

        public async Task<ScreenshotAnalysisResult> AnalyzeAsync(
            CaptureFrame frame,
            string evidenceSourceId,
            AdvisorSelection selection,
            CancellationToken cancellationToken,
            string? runId = null)
        {
            Calls++;
            await Task.Delay(delay, cancellationToken);
            var capturedAt = frame.CapturedAt;
            return new ScreenshotAnalysisResult
            {
                AnalysisId = evidenceSourceId,
                Snapshot = new RunSnapshot
                {
                    RunId = runId ?? "test-run",
                    AsOf = capturedAt
                },
                OperationalState = new Phase2OperationalState
                {
                    PageFamily = Phase2PageFamily.Unknown
                }
            };
        }
    }

    private sealed class SequencedFastPageClassifier(int battleFrameCount) :
        IPhase2FastPageClassifier
    {
        private int calls;

        public Phase2FastPageObservation Classify(CaptureFrame frame)
        {
            var isBattle = calls++ < battleFrameCount;
            return isBattle
                ? new Phase2FastPageObservation(
                    true,
                    Phase2PageFamily.Battle,
                    "battle_generic")
                : new Phase2FastPageObservation(
                    true,
                    Phase2PageFamily.Preparation,
                    "preparation_generic");
        }
    }

    private sealed class StaticWindowService : IGameWindowService
    {
        public GameWindowInfo Window { get; } = new(
            1,
            1,
            "StarRail",
            "StarRail",
            new PixelRect(0, 0, 2560, 1440));

        public IReadOnlyList<GameWindowInfo> FindCandidates() => [Window];

        public GameWindowInfo? Refresh(nint handle) =>
            handle == Window.Handle ? Window : null;

        public bool IsForeground(GameWindowInfo window) => true;

        public bool BringToForeground(GameWindowInfo window) => true;
    }

    private static CaptureFrame CreateFrame(
        byte value,
        (int X, int Y, int Width, int Height, byte Value)? patch = null)
    {
        const int width = 160;
        const int height = 90;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        if (patch is { } area)
        {
            for (var y = area.Y; y < area.Y + area.Height; y++)
            {
                for (var x = area.X; x < area.X + area.Width; x++)
                {
                    var offset = (y * width + x) * 4;
                    pixels[offset] = area.Value;
                    pixels[offset + 1] = area.Value;
                    pixels[offset + 2] = area.Value;
                }
            }
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }

    private static CaptureFrame CreatePatternFrame(bool invert)
    {
        const int width = 160;
        const int height = 90;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(((x / 8 + y / 8) % 2 == 0) ^ invert
                    ? 230
                    : 20);
                var offset = (y * width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.UtcNow);
    }
}
