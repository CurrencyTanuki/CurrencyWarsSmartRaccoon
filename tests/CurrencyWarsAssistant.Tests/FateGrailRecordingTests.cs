using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using Xunit;

namespace CurrencyWarsAssistant.Tests;

/// <summary>「滚动录屏」纯逻辑测试：画质档位、ffmpeg 定位、整局循环的录屏生命周期。</summary>
public sealed class FateGrailRecordingTests
{
    // ---------- 画质档位 ----------
    [Fact]
    public void Quality_High_Is60Fps()
    {
        var q = FateGrailRecordingQuality.FromLevel(FateGrailRecordingQualityLevel.High);
        Assert.Equal(60, q.FramesPerSecond);
        Assert.NotEqual(0, q.Crf);
    }

    [Fact]
    public void Quality_Low_IsPlainLowFps()
    {
        var q = FateGrailRecordingQuality.FromLevel(FateGrailRecordingQualityLevel.Low);
        Assert.Equal(15, q.FramesPerSecond);
        Assert.True(q.FramesPerSecond < 60);
    }

    [Fact]
    public void Quality_Medium_BetweenLowAndHigh()
    {
        var low = FateGrailRecordingQuality.FromLevel(FateGrailRecordingQualityLevel.Low).FramesPerSecond;
        var mid = FateGrailRecordingQuality.FromLevel(FateGrailRecordingQualityLevel.Medium).FramesPerSecond;
        var high = FateGrailRecordingQuality.FromLevel(FateGrailRecordingQualityLevel.High).FramesPerSecond;
        Assert.True(low < mid && mid < high);
    }

    // ---------- ffmpeg 定位 ----------
    [Fact]
    public void FfmpegLocator_ConfiguredPath_Resolves()
    {
        // 用当前进程的可执行文件路径当"存在的文件"来模拟用户配置的 ffmpeg.exe。
        // （Locate 只检查 File.Exists，不校验它真是 ffmpeg。）
        var fake = typeof(FateGrailRecordingTests).Assembly.Location;
        var r = FfmpegLocator.Locate(fake);
        Assert.True(r.Found);
        Assert.NotNull(r.ExecutablePath);
    }

    [Fact]
    public void FfmpegLocator_MissingPath_ReturnsDownloadUrl()
    {
        var r = FfmpegLocator.Locate(@"C:\definitely\missing\ffmpeg_not_there.exe");
        // 若 PATH 里真有 ffmpeg（本机装了），Found 可能 true；这里只断言"缺失时一定给下载页"。
        if (!r.Found)
        {
            Assert.NotNull(r.DownloadUrl);
            Assert.Contains("ffmpeg", r.DownloadUrl);
        }
    }

    // ---------- 整局循环录屏生命周期（fake recorder 验证 start/finish 被调用） ----------
    private sealed class FakeRecorder : FateGrailRunLoop.IRoundRecorder
    {
        public List<string> Starts { get; } = new();
        public List<(bool Success, string? OutDir)> Finishes { get; } = new();

        public Task StartAsync(string roundId, CancellationToken cancellationToken)
        {
            Starts.Add(roundId);
            return Task.CompletedTask;
        }

        public Task FinishAsync(bool success, string? outputDirectory, CancellationToken cancellationToken)
        {
            Finishes.Add((success, outputDirectory));
            return Task.CompletedTask;
        }
    }

    private static (FateGrailRunLoop Loop, FakeRecorder Recorder, OpeningLoopStub Stub)
        BuildLoopWithRecorder(bool openingSucceeds)
    {
        var recorder = new FakeRecorder();
        var stub = new OpeningLoopStub { Succeed = openingSucceeds };
        var coordinator = new FateGrailRunCoordinator(
            _ => Task.FromResult<FateGrailRunEngine.Snapshot?>(
                new FateGrailRunEngine.Snapshot(
                    EnvironmentId: FateGrailRunEngine.EnvironmentHeroArrival,
                    Hp: 94,
                    Gold: 50,
                    OwnedMembers: new HashSet<string>(
                        ["远坂凛", "吉尔伽美什", "Saber", "Archer"],
                        System.StringComparer.OrdinalIgnoreCase),
                    AvailableStrategyIds: new HashSet<string>(
                        [InvestmentStrategyPicker.PurchaseSpecialistColor],
                        System.StringComparer.OrdinalIgnoreCase),
                    Goal: FateGrailRunEngine.UserGoal.AnyOne,
                    Line: FateGrailRunEngine.EndLine.MiracleCompensation,
                    HasBody5Cost: true,
                    HasStarBadge: true,
                    BondTier: 4,
                    FiveCostBodyIds: new HashSet<string>(new[] { "Archer" }))),
            new AlwaysTrueExecutor());
        var loop = new FateGrailRunLoop(
            (filters, options, ct) => stub.Run(filters, options),
            coordinator)
        {
            RoundRecorder = recorder,
            RecordingOutputDirectory = @"C:\recording\out",
        };
        return (loop, recorder, stub);
    }

    private sealed class OpeningLoopStub
    {
        public bool Succeed { get; set; }

        public Task<OpeningRerollLoopResult> Run(OpeningFilterSet filters, OpeningRerollLoopOptions options) =>
            Task.FromResult(new OpeningRerollLoopResult(
                Succeed ? OpeningRerollLoopState.Matched : OpeningRerollLoopState.NavigationFailed,
                1, null, null, null, null,
                Succeed ? "matched" : "nav-failed"));
    }

    private sealed class AlwaysTrueExecutor : FateGrailRunCoordinator.IActionExecutor
    {
        public Task<bool> ExecuteAsync(
            FateGrailRunEngine.Action action, string stepNode, string message,
            FateGrailRunEngine.TrialSide? trialToChoose,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    [Fact]
    public async Task RecordingLoop_EachRoundStartsAndFinishes()
    {
        var (loop, recorder, _) = BuildLoopWithRecorder(openingSucceeds: true);
        await loop.RunAsync(
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            maxRounds: 1);
        Assert.True(recorder.Starts.Count >= 1);
        Assert.True(recorder.Finishes.Count >= 1);
    }

    [Fact]
    public async Task RecordingLoop_OpeningNotMatched_AlsoFinishesWithoutRetain()
    {
        var (loop, recorder, stub) = BuildLoopWithRecorder(openingSucceeds: false);
        stub.Succeed = false;
        await loop.RunAsync(
            new OpeningFilterSet(),
            new OpeningRerollLoopOptions(),
            maxRounds: 1);
        // 开局未命中：start 了也 finish，且 success=false（不保留录像）。
        Assert.True(recorder.Starts.Count >= 1);
        Assert.True(recorder.Finishes.Count >= 1);
        Assert.False(recorder.Finishes.Last().Success);
    }
}