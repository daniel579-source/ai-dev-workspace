using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// Layout 編輯器框選用的幾何判斷（ADR-0006 第 10 點）。
public sealed class LayoutGeometryTests
{
    static readonly (double, double, double, double) Box = (2, 2, 6, 4);

    [Theory]
    [InlineData(0, 3, 10, 3, true)]   // 橫穿
    [InlineData(3, 3, 4, 3.5, true)]  // 整段在裡面
    [InlineData(0, 0, 2, 2, true)]    // 端點碰到角
    [InlineData(0, 0, 10, 0, false)]  // 在上方
    [InlineData(7, 0, 7, 10, false)]  // 在右邊
    [InlineData(0, 5, 3, 10, false)]  // 斜線經過旁邊
    [InlineData(1, 6, 7, 0, true)]    // 斜線穿過
    public void 線段碰到矩形(double x1, double y1, double x2, double y2, bool expected) =>
        Assert.Equal(expected, LayoutGeometry.SegmentTouchesRect(x1, y1, x2, y2, Box));

    [Theory]
    [InlineData(6, 4, 8, 5, true)]    // 角相接也算碰到
    [InlineData(6.1, 0, 8, 5, false)]
    [InlineData(0, 0, 10, 10, true)]  // 包住
    public void 矩形碰到矩形(double l, double t, double r, double b, bool expected) =>
        Assert.Equal(expected, LayoutGeometry.RectsTouch((l, t, r, b), Box));
}
