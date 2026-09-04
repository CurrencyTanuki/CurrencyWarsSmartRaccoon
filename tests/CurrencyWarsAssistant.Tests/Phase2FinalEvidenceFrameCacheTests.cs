using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

public sealed class Phase2FinalEvidenceFrameCacheTests
{
    [Fact]
    public void CanonicalSourceParsingRejectsCrossRunAndTraversal()
    {
        const string runId = "run-safe";
        var valid = Phase2FinalEvidenceFrameCache.CanonicalSourceId(
            runId,
            "20260809-133111200.png");

        Assert.Equal(
            Phase2EvidenceSourceParseResult.Valid,
            Phase2FinalEvidenceFrameCache.ParseSourceId(
                runId,
                valid,
                out var fileName));
        Assert.Equal("20260809-133111200.png", fileName);
        Assert.Equal(
            Phase2EvidenceSourceParseResult.NotScreenshot,
            Phase2FinalEvidenceFrameCache.ParseSourceId(
                runId,
                "fixture:battle",
                out _));
        Assert.Equal(
            Phase2EvidenceSourceParseResult.Invalid,
            Phase2FinalEvidenceFrameCache.ParseSourceId(
                runId,
                "run:other/screenshots/20260809-133111200.png",
                out _));
        Assert.Equal(
            Phase2EvidenceSourceParseResult.Invalid,
            Phase2FinalEvidenceFrameCache.ParseSourceId(
                runId,
                "run:run-safe/screenshots/../outside.png",
                out _));
        Assert.Equal(
            Phase2EvidenceSourceParseResult.Invalid,
            Phase2FinalEvidenceFrameCache.ParseSourceId(
                runId,
                "run:run-safe/screenshots/subdir/outside.png",
                out _));
        Assert.Equal(
            Phase2EvidenceSourceParseResult.Invalid,
            Phase2FinalEvidenceFrameCache.ParseSourceId(
                runId,
                "run:run-safe/screenshots/C:\\outside.png",
                out _));
        Assert.Throws<InvalidDataException>(() =>
            new Phase2FinalEvidenceFrameCache("../other"));
        Assert.Throws<InvalidDataException>(() =>
            Phase2FinalEvidenceFrameCache.CanonicalSourceId(
                "run/subdir",
                "frame.png"));
    }

    [Fact]
    public void DefaultBoundsHoldSixteenNative4kBgraFrames()
    {
        const long native4kBgraBytes = 3840L * 2160 * 4;
        const int maximumTrackerEvidenceFrames = 16;
        Assert.Equal(
            maximumTrackerEvidenceFrames,
            Phase2FinalEvidenceFrameCache.DefaultMaximumFrames);
        Assert.True(
            Phase2FinalEvidenceFrameCache.DefaultMaximumBytes >=
            Phase2FinalEvidenceFrameCache.DefaultMaximumFrames *
            native4kBgraBytes);
    }

    [Fact]
    public void SixteenActiveFramesFitAndSeventeenthDoesNotEvictThem()
    {
        const string runId = "run-sixteen-active";
        var cache = new Phase2FinalEvidenceFrameCache(runId);
        var active = new List<string>();
        for (var index = 0;
             index < Phase2FinalEvidenceFrameCache.DefaultMaximumFrames;
             index++)
        {
            active.Add(cache.Register(
                $"active-{index:D2}.png",
                Frame((byte)(index + 1), index)));
            cache.RetainOnly(active);
        }

        Assert.Equal(16, cache.Count);
        Assert.Throws<InvalidDataException>(() =>
            cache.Register("overflow.png", Frame(30, 30)));
        Assert.Equal(16, cache.Count);
        Assert.Equal(active.Count, active.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RetainOnlySkipsCrossRunSourcesInsteadOfThrowing()
    {
        // 2026-09-04 实测：软件重启后 tracker 帧状态可能残留旧 run 的截图路径，
        // RetainOnly 遇跨会话来源抛异常会让周期清理永远失败
        //（每 5 分钟 UnhandledUiException 一次，全天 20+ 次）——
        // 正确语义=跳过不活跃来源并继续完成保留清理。
        const string runId = "run-retain";
        var cache = new Phase2FinalEvidenceFrameCache(runId);
        var current = cache.Register("current.png", Frame(10, 1));

        const string crossRun =
            "run:cmdtest-20260904-999999/screenshots/old.png";
        cache.RetainOnly([crossRun, current, crossRun]);

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task PersistUsesOriginalRegisteredPixelsNotHeartbeatPixels()
    {
        var root = NewRoot();
        try
        {
            const string runId = "run-heartbeat";
            var cache = new Phase2FinalEvidenceFrameCache(runId);
            var sourceId = cache.Register(
                "battle.png",
                Frame(marker: 20, sequence: 1));
            cache.RetainOnly([sourceId]);

            // A heartbeat frame is deliberately not registered because its
            // pixels are newer than the cached full analysis it revalidates.
            _ = Frame(marker: 30, sequence: 2);
            await cache.PersistFinalEvidenceAsync(
                root,
                Final(sourceId),
                CancellationToken.None);

            var persisted = CaptureFrameLoader.LoadFile(
                Path.Combine(root, "screenshots", "battle.png"));
            Assert.Equal(20, persisted.BgraPixels[0]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PruneKeepsActiveAndLatestButDoesNotPersistUnreferencedFrames()
    {
        var root = NewRoot();
        try
        {
            const string runId = "run-prune";
            var cache = new Phase2FinalEvidenceFrameCache(runId);
            var referenced = cache.Register("referenced.png", Frame(10, 1));
            cache.RetainOnly([referenced]);
            var superseded = cache.Register("superseded.png", Frame(20, 2));
            cache.RetainOnly([referenced]);
            var latest = cache.Register("latest.png", Frame(30, 3));
            cache.RetainOnly([referenced]);

            Assert.Equal(2, cache.Count);
            Assert.Equal(latest, cache.LatestFullSourceId);
            await cache.PersistFinalEvidenceAsync(
                root,
                Final(referenced),
                CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(
                root,
                "screenshots",
                "referenced.png")));
            Assert.False(File.Exists(Path.Combine(
                root,
                "screenshots",
                "superseded.png")));
            Assert.False(File.Exists(Path.Combine(
                root,
                "screenshots",
                "latest.png")));
            Assert.NotEqual(superseded, cache.LatestFullSourceId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void BoundFailureDoesNotEvictActiveCandidateAndClearReleasesState()
    {
        const string runId = "run-bounded";
        var cache = new Phase2FinalEvidenceFrameCache(
            runId,
            maximumFrames: 1,
            maximumBytes: 64);
        var active = cache.Register("active.png", Frame(10, 1));
        cache.RetainOnly([active]);

        Assert.Throws<InvalidDataException>(() =>
            cache.Register("next.png", Frame(20, 2)));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.CachedBytes > 0);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CachedBytes);
        Assert.Null(cache.LatestFullSourceId);
    }

    [Fact]
    public async Task MissingOrCrossRunFinalEvidenceFailsClosed()
    {
        var root = NewRoot();
        try
        {
            var cache = new Phase2FinalEvidenceFrameCache("run-current");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.PersistFinalEvidenceAsync(
                    root,
                    Final("run:run-current/screenshots/missing.png"),
                    CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.PersistFinalEvidenceAsync(
                    root,
                    Final("run:run-other/screenshots/cross-run.png"),
                    CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.PersistFinalEvidenceAsync(
                    root,
                    Final("run:run-current/screenshots/../outside.png"),
                    CancellationToken.None));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PngWriteFailureLeavesNoFinalOrTemporaryFile()
    {
        var root = NewRoot();
        try
        {
            const string runId = "run-write-failure";
            var cache = new Phase2FinalEvidenceFrameCache(runId);
            var sourceId = cache.Register(
                "blocked.png",
                Frame(20, 1));
            var blockedPath = Path.Combine(
                root,
                "screenshots",
                "blocked.png");
            Directory.CreateDirectory(blockedPath);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                cache.PersistFinalEvidenceAsync(
                    root,
                    Final(sourceId),
                    CancellationToken.None));
            Assert.False(File.Exists(blockedPath));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(root, "screenshots"),
                "*.tmp"));
            Assert.False(File.Exists(Path.Combine(
                root,
                "nodes",
                "node-1-1-final.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PngWriteFailureKeepsFrameAvailableForRetry()
    {
        var root = NewRoot();
        try
        {
            const string runId = "run-write-retry";
            var cache = new Phase2FinalEvidenceFrameCache(runId);
            var sourceId = cache.Register("retry.png", Frame(20, 1));
            cache.RetainOnly([sourceId]);
            var blockedPath = Path.Combine(
                root,
                "screenshots",
                "retry.png");
            Directory.CreateDirectory(blockedPath);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                cache.PersistFinalEvidenceAsync(
                    root,
                    Final(sourceId),
                    CancellationToken.None));
            Directory.Delete(blockedPath);

            await cache.PersistFinalEvidenceAsync(
                root,
                Final(sourceId),
                CancellationToken.None);

            var persisted = CaptureFrameLoader.LoadFile(blockedPath);
            Assert.Equal(20, persisted.BgraPixels[0]);
            Assert.Equal(1, cache.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExistingCorruptPngFailsClosedInsteadOfBeingTrusted()
    {
        var root = NewRoot();
        try
        {
            const string runId = "run-corrupt-existing";
            var cache = new Phase2FinalEvidenceFrameCache(runId);
            var sourceId = cache.Register("corrupt.png", Frame(20, 1));
            var screenshotDirectory = Path.Combine(root, "screenshots");
            Directory.CreateDirectory(screenshotDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(screenshotDirectory, "corrupt.png"),
                "not a png");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.PersistFinalEvidenceAsync(
                    root,
                    Final(sourceId),
                    CancellationToken.None));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CandidateCollectorIncludesBattleSettlementAndDiagnosticEvidence()
    {
        var evidence = Evidence("run:run-collector/screenshots/candidate.png");
        var damage = Damage(evidence);
        var state = new Phase2OperationalState
        {
            NodeId = Observation<string>.Known("1-1", 0.9, [evidence]),
            BattleDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
                [damage],
                0.9,
                [evidence]),
            BattleScreenDamageCandidate = Observation<long>.Known(
                123,
                0.9,
                [evidence]),
            SettlementDamage = Observation<IReadOnlyList<CharacterDamageState>>.Known(
                [damage],
                0.9,
                [evidence]),
            SettlementScreenDamageCandidate = Observation<long>.Known(
                123,
                0.9,
                [evidence]),
            SettlementGoldReward = Observation<int>.Known(10, 0.9, [evidence]),
            RemainingActionValue = Observation<RemainingActionValueState>.Known(
                RemainingActionValueState.Create(1, 50),
                0.9,
                [evidence]),
            PendingIcons =
            [
                new PendingIconObservation(
                    PendingIconCategory.CharacterAvatar,
                    "slot",
                    new RelativeRegion(0, 0, 0.1, 0.1),
                    null,
                    0,
                    evidence,
                    "pending")
            ],
            PartialFields =
            [
                new Phase2PartialFieldObservation(
                    "field",
                    "tmp",
                    new RelativeRegion(0, 0, 0.1, 0.1),
                    new Dictionary<string, string>(),
                    [],
                    [],
                    0,
                    "partial",
                    evidence)
            ]
        };

        var sources = Phase2FinalEvidenceSourceCollector.Collect(state);
        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.Equal(evidence.SourceId, source));
    }

    private static FinalNodeBattleState Final(string sourceId)
    {
        var evidence = Evidence(sourceId);
        return new FinalNodeBattleState(
            "1-1",
            [Damage(evidence)],
            123,
            RemainingActionValueState.Create(1, 50),
            evidence.CapturedAt!.Value,
            evidence);
    }

    private static CharacterDamageState Damage(EvidenceReference evidence) =>
        new(
            1,
            "currency_wars_character_01",
            123,
            "123",
            0.9,
            0.9,
            new RelativeRegion(0, 0, 0.1, 0.1),
            new RelativeRegion(0.1, 0, 0.1, 0.1),
            evidence);

    private static EvidenceReference Evidence(string sourceId) =>
        new(
            sourceId,
            "screenshot:full-frame",
            CapturedAt: DateTimeOffset.Parse("2026-08-09T13:31:11.200+08:00"));

    private static CaptureFrame Frame(byte marker, int sequence)
    {
        const int width = 2;
        const int height = 2;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = marker;
            pixels[offset + 1] = 100;
            pixels[offset + 2] = 100;
            pixels[offset + 3] = 255;
        }

        return new CaptureFrame(
            width,
            height,
            width * 4,
            pixels,
            new PixelRect(0, 0, width, height),
            DateTimeOffset.Parse("2026-08-09T13:31:11+08:00")
                .AddMilliseconds(sequence));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "CurrencyWarsAssistant.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
