using CurrencyWarsAssistant.Tasks;

namespace CurrencyWarsAssistant.Tests;

/// <summary>
/// 红测（后台斜视平面几何）：用户 2026-08-15 实机标定的最左槽 4 点
/// （2560×1440：TL(457,800) TR(621,800) BL(425,980) BR(595,980)），
/// 断言 ComputeSlotQuads 在 2K 帧上还原该锚点，且中间槽中心≈画面中轴、
/// 左右槽上下边错位方向相反（对称透视）。
/// </summary>
public sealed class BackRowPerspectiveGeometryTests
{
    [Fact]
    public void TwoKFrameReproducesUserAnchors()
    {
        var quads = BackRowPerspectiveGeometry.ComputeSlotQuads(2560, 1440);
        var left = quads[0];
        Assert.Equal(457, left.Item1, 0);
        Assert.Equal(800, left.Item2, 0);
        Assert.Equal(621, left.Item3, 0);
        Assert.Equal(800, left.Item4, 0);
        Assert.Equal(425, left.Item5, 0);
        Assert.Equal(980, left.Item6, 0);
        Assert.Equal(595, left.Item7, 0);
        Assert.Equal(980, left.Item8, 0);
    }

    [Fact]
    public void CenterSlotIsCenteredAndQuadsAreSymmetric()
    {
        var quads = BackRowPerspectiveGeometry.ComputeSlotQuads(2560, 1440);
        var center = quads[4];
        var centerTop = (center.Item1 + center.Item3) / 2;
        var centerBottom = (center.Item5 + center.Item7) / 2;
        Assert.Equal(1280, centerTop, 1);
        Assert.Equal(1280, centerBottom, 1);

        // 左槽下边相对上边左移（用户模型：上面移动距离小、下面大）
        var left = quads[0];
        var leftTopC = (left.Item1 + left.Item3) / 2;
        var leftBottomC = (left.Item5 + left.Item7) / 2;
        Assert.True(leftBottomC < leftTopC,
            $"左槽下边中心 {leftBottomC} 应在上边中心 {leftTopC} 左侧");

        // 右槽对称（下边右移）
        var right = quads[8];
        var rightTopC = (right.Item1 + right.Item3) / 2;
        var rightBottomC = (right.Item5 + right.Item7) / 2;
        Assert.True(rightBottomC > rightTopC,
            $"右槽下边中心 {rightBottomC} 应在上边中心 {rightTopC} 右侧");

        // 对称性：左右槽相对中轴的偏移量一致
        var leftShift = leftTopC - leftBottomC;
        var rightShift = rightBottomC - rightTopC;
        Assert.Equal(leftShift, rightShift, 1);
    }

    [Fact]
    public void WarpedSlotRectsCoverNineSlotsInWarpedRow()
    {
        var rects = BackRowPerspectiveGeometry.ComputeWarpedSlotRects(2560, 1440);
        Assert.Equal(9, rects.Count);
        // 拉正后槽位应连续铺开、互不重叠、宽度一致
        for (var i = 1; i < rects.Count; i++)
        {
            Assert.Equal(rects[0].Width, rects[i].Width);
            Assert.True(rects[i].X >= rects[i - 1].X + rects[i - 1].Width - 1,
                $"槽位 {i} 与前一槽位重叠: {rects[i - 1]} vs {rects[i]}");
        }
    }
}
