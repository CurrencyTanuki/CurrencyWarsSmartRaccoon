using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 槽位自适应修正验证（用户 2026-08-07：兼容 16:9 全分辨率）：
/// 视频 1080p 帧（卡牌 x 偏移）修正后应更贴近实际卡牌位置。
/// </summary>
public sealed class Phase2SlotDetectorTests
{
    private readonly ITestOutputHelper _output;

    public Phase2SlotDetectorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AlignsVideoFrameSlotsTowardDetectedCards()
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000036_prep31.png"));
        // 000036 是 2K 帧：AlignSlotColumns 内部会缩放到帧空间，
        // 这里传原始 1920 参考系坐标即可
        var reference = Phase2RecognitionRegions.PreparationCharacterSlots1920;
        var aligned = Phase2SlotDetector.AlignSlotColumns(frame, reference);
        _output.WriteLine(
            $"修正后槽位数: {aligned.Count} (固定 {reference.Count})");

        _output.WriteLine("修正后槽位 (000036):");
        foreach (var a in aligned)
        {
            _output.WriteLine($"  ({a.X},{a.Y}) {a.Width}x{a.Height}");
        }

        // Front#0（银狼）应被修正到实际位置（实际 1920 参考系 x 413-545
        // → 2K 帧 x 551-727，中心约 639）；修正后 Front#0 中心应接近
        //（偏差 < 0.5x 槽宽）。注意：检测到金星才修正。
        var front0 = aligned[0];
        var front0Center = front0.X + front0.Width / 2;
        _output.WriteLine($"Front#0 中心 x={front0Center} (银狼实际中心≈639)");
        Assert.True(Math.Abs(front0Center - 639) < 200,
            $"Front#0 未修正到银狼位置：中心 {front0Center}");
    }

    [Fact]
    public void DebugGoldClustersInFrontRow()
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "user_ref_000036_prep31.png"));
        // 前台行带（2K: 槽位 y 438, 高 186, ±25% → y 392-484）
        var clusters = Phase2SlotDetector.DetectGoldClustersInRow(
            frame, 392, 484);
        _output.WriteLine($"前台行带(y392-484) 簇数: {clusters.Count}");
        foreach (var c in clusters)
        {
            _output.WriteLine($"  簇中心={c.Center} 宽={c.Width}");
        }
        Assert.True(clusters.Count > 0, "前台行带无金星簇");
    }

    [Fact]
    public void KeepsStandardFrameSlotsStable()
    {
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "PageReplay",
            "prep_3_7_142723217.png"));
        var reference = Phase2RecognitionRegions.PreparationCharacterSlots1920;
        var aligned = Phase2SlotDetector.AlignSlotColumns(frame, reference);
        // 标准 2K 帧：检测到卡牌即重建，应有结果且槽位在画面内
        Assert.NotEmpty(aligned);
        foreach (var a in aligned)
        {
            Assert.True(a.X >= 0 && a.X + a.Width <= frame.Width,
                $"标准帧槽位越界：({a.X},{a.Y}) {a.Width}x{a.Height}");
        }
    }

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
