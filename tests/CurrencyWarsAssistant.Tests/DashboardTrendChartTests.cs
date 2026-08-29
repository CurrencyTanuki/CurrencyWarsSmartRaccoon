using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CurrencyWarsAssistant.App;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// DashboardTrendChart 奖励节点图线规则测试（用户 2026-08-05 规则）：
/// 1-8/2-6/3-6 是奖励节点——数据点保留、不连线、不参与 Y 轴范围，
/// 连线时跳过这些节点（2-5→2-7 等直连）。
/// </summary>
public sealed class DashboardTrendChartTests
{
    private static readonly MethodInfo IsRewardNodeId = typeof(DashboardTrendChart)
        .GetMethod("IsRewardNodeId", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo IsRewardNodeAt = typeof(DashboardTrendChart)
        .GetMethod("IsRewardNodeAt", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ReadMetricValue = typeof(DashboardTrendChart)
        .GetMethod("ReadMetricValue", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("1-8", true)]
    [InlineData("2-6", true)]
    [InlineData("3-6", true)]
    [InlineData("2-5", false)]
    [InlineData("2-7", false)]
    [InlineData("1-7", false)]
    [InlineData("1-9", false)]
    [InlineData("3-7", false)]
    [InlineData("3-8", false)]
    [InlineData("3-9", false)]
    public void RewardNodeRuleMatchesOnlyDesignatedNodes(
        string nodeId,
        bool expected)
    {
        var actual = (bool)IsRewardNodeId.Invoke(null, [nodeId])!;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RewardNodeAtMatchesRowsByNodeId()
    {
        var rows = new object[]
        {
            MakeRow("1-7"),
            MakeRow("1-8"),
            MakeRow("1-9"),
            MakeRow("2-5"),
            MakeRow("2-6"),
            MakeRow("2-7"),
        };

        Assert.False((bool)IsRewardNodeAt.Invoke(null, [rows, 0])!);
        Assert.True((bool)IsRewardNodeAt.Invoke(null, [rows, 1])!);
        Assert.False((bool)IsRewardNodeAt.Invoke(null, [rows, 2])!);
        Assert.False((bool)IsRewardNodeAt.Invoke(null, [rows, 3])!);
        Assert.True((bool)IsRewardNodeAt.Invoke(null, [rows, 4])!);
        Assert.False((bool)IsRewardNodeAt.Invoke(null, [rows, 5])!);
    }

    [Fact]
    public void RewardNodeMetricValueIsStillReadForPointDrawing()
    {
        // 奖励节点值必须仍能读出（用于画点），不能返回 null（旧逻辑会
        // 因为 ExcludesRewardNodes 把奖励节点滤成 null，导致点也不画）。
        RunSta(() =>
        {
            var chart = new DashboardTrendChart { Metric = "FinalDamage" };
            var rewardRow = MakeRow("2-6", finalDamage: 7115);
            var value = (double?)ReadMetricValue.Invoke(
                chart,
                [rewardRow])!;
            Assert.NotNull(value);
            Assert.Equal(7115d, value.Value);
        });
    }

    [Fact]
    public void RewardNodeTheoreticalDamageValueIsStillRead()
    {
        // 理论极限同理：奖励节点值保留（画点），只是不连线/不参与 Y 轴。
        RunSta(() =>
        {
            var chart = new DashboardTrendChart { Metric = "TheoreticalDamage" };
            var rewardRow = MakeRow("3-6", theoreticalDamage: 123456);
            var value = (double?)ReadMetricValue.Invoke(
                chart,
                [rewardRow])!;
            Assert.NotNull(value);
            Assert.Equal(123456d, value.Value);
        });
    }

    [Fact]
    public void NullRewardNodeDoesNotBreakLineBetweenNeighbors()
    {
        // 用户 2026-08-06 实测：1-8 奖励关在理论极限/行动值图中值为 null，
        // 旧逻辑把 previousConnectablePoint 清空 → 1-7 与 1-9 断开。
        // 修复后：null 奖励节点不清空锚点 → 1-7→1-9 直连（跳过 1-8）。
        // 用真实渲染扫描像素验证 1-7 与 1-9 之间（1-8 的 x 带）存在连线像素。
        RunSta(() =>
        {
            var rows = new object[]
            {
                MakeRow("1-7", theoreticalDamage: 990_000_000),
                MakeRow("1-8", theoreticalDamage: null),
                MakeRow("1-9", theoreticalDamage: 2_566_000_000),
            };
            var chart = new DashboardTrendChart
            {
                Metric = "TheoreticalDamage",
                ScaleLabel = "线性",
                Width = 300,
                Height = 120,
            };
            chart.ItemsSource = rows;
            chart.Measure(new Size(300, 120));
            chart.Arrange(new Rect(0, 0, 300, 120));
            chart.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                300,
                120,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(chart);

            // 1-8 的 x 带：3 个节点，x = 36 + plotWidth*i/(count-1)。
            // 在 1-7 与 1-9 的 y 高度附近，扫描 1-8 x 带内是否有 Stroke 色像素。
            // 1-8 无值 → 该带无格点，连线应从 1-7 直连 1-9 穿过该带。
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            var stroke = Color.FromRgb(0x00, 0xBF, 0xFF); // 默认 Stroke = DeepSkyBlue
            var x8 = (int)(36 + (300 - 36 - 7) * 1d / 2d);
            var found = false;
            for (var y = 30; y < 90; y++)
            {
                for (var x = x8 - 2; x <= x8 + 2; x++)
                {
                    var o = y * stride + x * 4;
                    var b = pixels[o];
                    var g = pixels[o + 1];
                    var r = pixels[o + 2];
                    if (Math.Abs(r - stroke.R) < 40 &&
                        Math.Abs(g - stroke.G) < 40 &&
                        Math.Abs(b - stroke.B) < 40)
                    {
                        found = true;
                    }
                }
            }

            Assert.True(found, "1-7→1-9 连线应穿过 1-8 的 x 带（奖励节点 null 不应断开）");
        });
    }

    [Fact]
    public void AllNodeLabelsAreDrawnIncludingSkippedOnes()
    {
        // 用户 2026-08-06 要求：X 轴标签全部显示（1-4、1-7 不能缺失）。
        // 旧逻辑 labelStride 抽样导致部分标签缺失；修复后所有节点都画。
        // 用真实渲染扫描底部标签区，断言 1-4 与 1-7 的 x 带存在标签像素
        //（标签颜色 191,204,218，绘制在 y≈Height-17+2 附近）。
        RunSta(() =>
        {
            var rows = new object[]
            {
                MakeRow("1-1"), MakeRow("1-2"), MakeRow("1-3"), MakeRow("1-4"),
                MakeRow("1-6"), MakeRow("1-7"), MakeRow("1-8"), MakeRow("1-9"),
            };
            var chart = new DashboardTrendChart
            {
                Metric = "FinalDamage",
                ScaleLabel = "线性",
                Width = 400,
                Height = 120,
            };
            chart.ItemsSource = rows;
            chart.Measure(new Size(400, 120));
            chart.Arrange(new Rect(0, 0, 400, 120));
            chart.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                400,
                120,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(chart);

            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            var labelColor = Color.FromRgb(191, 204, 218);
            // 8 个节点：x = 36 + plotWidth*i/(count-1)，plotWidth = 400-36-7 = 357
            // 1-4 → i=3，1-7 → i=5。标签绘制在底部 y≈105-115。
            foreach (var (index, nodeId) in new[]
                     {
                         (3, "1-4"),
                         (5, "1-7"),
                     })
            {
                var labelX = (int)(36 + 357d * index / 7d);
                var found = false;
                for (var y = 95; y < 118; y++)
                {
                    for (var x = labelX - 14; x <= labelX + 14; x++)
                    {
                        var o = y * stride + x * 4;
                        if (o + 2 >= pixels.Length)
                        {
                            continue;
                        }

                        var b = pixels[o];
                        var g = pixels[o + 1];
                        var r = pixels[o + 2];
                        if (Math.Abs(r - labelColor.R) < 70 &&
                            Math.Abs(g - labelColor.G) < 70 &&
                            Math.Abs(b - labelColor.B) < 70)
                        {
                            found = true;
                        }
                    }
                }

                Assert.True(
                    found,
                    $"节点 {nodeId} 的 X 轴标签应被绘制（旧逻辑 labelStride 抽样会缺失）");
            }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null)
        {
            throw new Xunit.Sdk.XunitException(
                $"STA 线程异常: {captured}");
        }
    }

    private static HistoricalDashboardRow MakeRow(
        string nodeId,
        long? finalDamage = null,
        long? theoreticalDamage = null) =>
        new(
            nodeId,
            "—",
            "—",
            "—",
            "—",
            "—",
            "—",
            "—",
            "—",
            0,
            false,
            finalDamage,
            null,
            null,
            "—",
            theoreticalDamage,
            "—",
            false);
}
